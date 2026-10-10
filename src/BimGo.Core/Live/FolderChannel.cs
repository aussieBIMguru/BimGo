using System.Collections.Concurrent;
using System.Text.Json;

// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// One side of a session's message exchange over two folders: sends into an outbox, receives from an inbox.
    ///
    /// Sending writes one <see cref="Envelope"/> per file, atomically (a .tmp file renamed to .json), named so that
    /// ordinal file-name order is send order. Receiving uses a <see cref="FileSystemWatcher"/> plus a slow poll
    /// (watchers can miss events), reads files in name order, validates them and deletes them. Received messages
    /// queue up for <see cref="TryReceive"/>; <see cref="MessageArrived"/> fires (on a thread-pool thread) when new
    /// ones arrive.
    ///
    /// Transport-agnostic callers only use Send / TryReceive, so named pipes could replace the folders later.
    /// Thread-safe. Never throws from Send / TryReceive.
    /// </summary>
    public sealed class FolderChannel : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _outbox;
        private readonly string _inbox;
        private readonly string _name;
        private readonly ConcurrentQueue<Envelope> _received = new();
        private readonly object _scanLock = new();
        private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _seenOrder = new();
        private FileSystemWatcher _watcher;
        private Timer _poll;
        private long _seq;
        private volatile bool _disposed;

        /// <summary>
        /// Opens the channel and starts watching the inbox.
        /// </summary>
        /// <param name="sessionId">The session (messages for other sessions are ignored).</param>
        /// <param name="outbox">Folder to write to.</param>
        /// <param name="inbox">Folder to read from.</param>
        /// <param name="name">A short name for the log ("revit" / "app").</param>
        public FolderChannel(string sessionId, string outbox, string inbox, string name)
        {
            _sessionId = sessionId;
            _outbox = outbox;
            _inbox = inbox;
            _name = name;

            Directory.CreateDirectory(_outbox);
            Directory.CreateDirectory(_inbox);

            try
            {
                _watcher = new FileSystemWatcher(_inbox, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                    IncludeSubdirectories = false
                };
                _watcher.Created += (_, _) => Scan();
                _watcher.Renamed += (_, _) => Scan();
                _watcher.Error += (_, e) => Utilities.Log_Utils.Write($"Channel {_name} watcher error (polling continues): {e.GetException()?.Message}");
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Channel {_name} watcher unavailable, polling only: {ex.Message}");
                _watcher = null;
            }

            _poll = new Timer(_ => Scan(), null, 1000, 1000);
        }

        /// <summary>
        /// Raised (on a thread-pool thread) after new messages were queued.
        /// </summary>
        public event Action MessageArrived;

        /// <summary>
        /// Sends a message.
        /// </summary>
        /// <param name="type">One of <see cref="MessageTypes"/>.</param>
        /// <param name="payload">The payload object (serialised), or null.</param>
        /// <param name="replyTo">The id of the message being answered, or null.</param>
        /// <returns>The sent envelope, or null if it could not be written.</returns>
        public Envelope Send(string type, object payload, string replyTo = null)
        {
            if (_disposed) { return null; }
            try
            {
                var envelope = new Envelope
                {
                    Seq = Interlocked.Increment(ref _seq),
                    SessionId = _sessionId,
                    Type = type,
                    ReplyTo = replyTo,
                    SentUtc = DateTime.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(payload ?? new object(), payload?.GetType() ?? typeof(object), LiveProtocol.JSON)
                };

                Directory.CreateDirectory(_outbox);
                string baseName = $"{envelope.SentUtc:yyyyMMddHHmmssfff}-{envelope.Seq:D6}-{SafeType(type)}";
                string temp = Path.Combine(_outbox, baseName + ".tmp");
                File.WriteAllText(temp, JsonSerializer.Serialize(envelope, LiveProtocol.JSON));
                File.Move(temp, Path.Combine(_outbox, baseName + ".json"), overwrite: true);
                return envelope;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Channel {_name}: could not send {type}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Takes the next received message, if any.
        /// </summary>
        public bool TryReceive(out Envelope envelope) => _received.TryDequeue(out envelope);

        /// <summary>
        /// Deletes everything waiting in the inbox (stale messages from an earlier attach).
        /// </summary>
        public void PurgeInbox()
        {
            lock (_scanLock)
            {
                try
                {
                    foreach (string file in Directory.EnumerateFiles(_inbox))
                    {
                        TryDelete(file);
                    }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Channel {_name}: purge failed: {ex.Message}");
                }
                while (_received.TryDequeue(out _)) { }
            }
        }

        /// <summary>
        /// Reads every waiting message (watcher events, the poll timer, or callers wanting an immediate check).
        /// </summary>
        public void Scan()
        {
            if (_disposed) { return; }
            int queued = 0;

            lock (_scanLock)
            {
                try
                {
                    if (!Directory.Exists(_inbox)) { return; }
                    string[] files = Directory.GetFiles(_inbox, "*.json");
                    Array.Sort(files, StringComparer.Ordinal);

                    foreach (string file in files)
                    {
                        Envelope envelope = ReadMessage(file, out bool retryLater);
                        if (retryLater) { break; } // keep order: try this one (and the rest) next scan

                        TryDelete(file);
                        if (envelope == null || !Remember(envelope.Id)) { continue; }
                        _received.Enqueue(envelope);
                        queued++;
                    }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Channel {_name}: scan failed: {ex.Message}");
                }
            }

            if (queued > 0)
            {
                try { MessageArrived?.Invoke(); }
                catch (Exception ex) { Utilities.Log_Utils.Write($"Channel {_name}: arrival handler failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Reads and validates one message file.
        /// </summary>
        /// <param name="file">The file.</param>
        /// <param name="retryLater">True if the file is still locked by its writer.</param>
        /// <returns>The envelope, or null if invalid (the file is then discarded).</returns>
        private Envelope ReadMessage(string file, out bool retryLater)
        {
            retryLater = false;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > LiveProtocol.MAX_MESSAGE_BYTES)
                {
                    Utilities.Log_Utils.Write($"Channel {_name}: oversized message discarded ({info.Length} bytes).");
                    return null;
                }

                Envelope envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(file), LiveProtocol.JSON);
                if (envelope == null || string.IsNullOrEmpty(envelope.Type)) { return null; }
                if (!string.Equals(envelope.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    Utilities.Log_Utils.Write($"Channel {_name}: message for another session discarded.");
                    return null;
                }
                if (envelope.Protocol > LiveProtocol.VERSION)
                {
                    Utilities.Log_Utils.Write($"Channel {_name}: message from a newer protocol ({envelope.Protocol}) discarded.");
                    return null;
                }
                return envelope;
            }
            catch (IOException)
            {
                retryLater = true;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                retryLater = true;
                return null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Channel {_name}: unreadable message discarded: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records a message id; false if it was already handled (a file that could not be deleted is read again).
        /// </summary>
        private bool Remember(string id)
        {
            if (string.IsNullOrEmpty(id)) { return true; }
            if (!_seenIds.Add(id)) { return false; }
            _seenOrder.Enqueue(id);
            if (_seenOrder.Count > 4096) { _seenIds.Remove(_seenOrder.Dequeue()); }
            return true;
        }

        private static string SafeType(string type)
        {
            var chars = (type ?? "msg").Where(c => char.IsLetterOrDigit(c) || c == '.').Take(32).ToArray();
            return chars.Length == 0 ? "msg" : new string(chars);
        }

        /// <summary>
        /// Deletes a handled message, retrying briefly: on Windows a just-created file can be held for a moment by
        /// anti-virus or the search indexer. If it is still held, the next scan deletes it (the id check stops it being
        /// handled twice).
        /// </summary>
        private static void TryDelete(string file)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    File.Delete(file);
                    return;
                }
                catch (IOException) { Thread.Sleep(15); }
                catch (UnauthorizedAccessException) { Thread.Sleep(15); }
                catch { return; }
            }
        }

        /// <summary>
        /// Stops watching.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            try { _poll?.Dispose(); } catch { /* closing */ }
            _poll = null;
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                }
            }
            catch
            {
                // Closing
            }
            _watcher = null;
        }
    }
}
