using System.Text.Json;
using BimGo.Edits;

// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// The app side of pushing a standalone file's journal into a live Revit session (<c>journal.apply</c>).
    ///
    /// The app is walking a .bimgo file, not the session, so it must not attach as the session's walkthrough:
    /// no app.json is written and no hello is sent. It opens a temporary channel on the session's folders, sends one
    /// request and waits for the <c>journal.result</c> with the same request id; anything else arriving in the
    /// session's to-app folder meanwhile (model.changed...) is meant for an attached walkthrough, which by definition
    /// isn't running, and is discarded.
    ///
    /// Game thread only (the channel's watcher runs on its own). Never throws.
    /// </summary>
    public sealed class JournalPush : IDisposable
    {
        #region Constants

        /// <summary>
        /// Seconds to wait for Revit before suggesting an add-in update (old add-ins ignore the request), plus
        /// <see cref="TIMEOUT_PER_ENTRY_SECONDS"/> per entry so big pushes have time to run.
        /// </summary>
        public const float TIMEOUT_SECONDS = 30f;

        /// <summary>Extra waiting time per pushed entry.</summary>
        public const float TIMEOUT_PER_ENTRY_SECONDS = 0.25f;

        /// <summary>Above this size the payload is written to the session's snapshots folder and sent by path.</summary>
        private const long INLINE_LIMIT_BYTES = 3L * 1024 * 1024;

        #endregion

        #region Fields

        private readonly FolderChannel _channel;
        private string _requestId;
        private float _waited;
        private float _timeout = TIMEOUT_SECONDS;
        private bool _disposed;

        #endregion

        /// <summary>
        /// Opens the temporary channel to a session.
        /// </summary>
        /// <param name="session">The session (from <see cref="LiveSessions.ListAlive"/>).</param>
        public JournalPush(SessionInfo session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            _channel = new FolderChannel(session.SessionId, LiveProtocol.ToRevitFolder(session.SessionId),
                LiveProtocol.ToAppFolder(session.SessionId), "push");
        }

        #region Properties

        /// <summary>The target session.</summary>
        public SessionInfo Session { get; }

        /// <summary>True while a request is waiting for its answer.</summary>
        public bool Waiting => _requestId != null;

        /// <summary>True for a dry run in flight.</summary>
        public bool WaitingDryRun { get; private set; }

        /// <summary>Seconds waited for the current request.</summary>
        public float Waited => _waited;

        #endregion

        #region Discovery

        /// <summary>
        /// The live sessions of a model (by <see cref="SessionInfo.ModelKey"/>), newest first. Empty for an empty key.
        /// </summary>
        public static List<SessionInfo> FindSessions(string modelKey)
        {
            var matches = new List<SessionInfo>();
            if (string.IsNullOrWhiteSpace(modelKey)) { return matches; }
            foreach (SessionInfo session in LiveSessions.ListAlive())
            {
                if (string.Equals(session.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase)) { matches.Add(session); }
            }
            return matches;
        }

        /// <summary>
        /// Builds the request for a journal: its entries not yet in Revit, and the clones already there.
        /// </summary>
        /// <param name="journal">The file's journal.</param>
        /// <param name="dryRun">Preview only.</param>
        /// <param name="applyConflicts">Apply moved-since-export entries anyway.</param>
        /// <param name="modelKey">The file's model key.</param>
        /// <param name="existingPhase">The file's existing phase name.</param>
        /// <param name="newPhase">The file's new phase name.</param>
        /// <param name="fileName">The file name (undo label).</param>
        public static JournalApplyPayload BuildRequest(EditJournal journal, bool dryRun, bool applyConflicts,
            string modelKey, string existingPhase, string newPhase, string fileName)
        {
            var payload = new JournalApplyPayload
            {
                DryRun = dryRun,
                ApplyConflicts = applyConflicts,
                ModelKey = modelKey ?? string.Empty,
                ExistingPhaseName = existingPhase,
                PhaseName = newPhase,
                FileName = fileName ?? string.Empty
            };
            foreach (JournalEntry entry in journal.Entries)
            {
                if (!entry.AppliedToRevit)
                {
                    payload.Entries.Add(entry);
                }
                else if (JournalOps.Creates(entry.Op) && entry.RevitElementId > 0 && entry.NewCloneKey != 0)
                {
                    payload.KnownClones.Add(new CloneRef { CloneKey = entry.NewCloneKey, ElementId = entry.RevitElementId });
                }
            }
            return payload;
        }

        /// <summary>
        /// Revit side: completes a request sent by file (<see cref="JournalApplyPayload.PayloadPath"/>). The path must
        /// be a push file inside the session's own snapshots folder; it is deleted once read.
        /// </summary>
        /// <param name="sessionId">The receiving session.</param>
        /// <param name="request">The request as received.</param>
        /// <param name="error">A reason on failure.</param>
        /// <returns>The full request, or null.</returns>
        public static JournalApplyPayload ResolvePayloadFile(string sessionId, JournalApplyPayload request, out string error)
        {
            error = null;
            if (request == null) { error = "Empty request"; return null; }
            if (string.IsNullOrEmpty(request.PayloadPath)) { return request; }

            try
            {
                if (!LiveProtocol.IsValidSessionId(sessionId)) { error = "Invalid session"; return null; }
                string folder = Path.GetFullPath(LiveProtocol.SnapshotsFolder(sessionId)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(request.PayloadPath);
                string name = Path.GetFileName(path);
                bool inside = path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(path) + Path.DirectorySeparatorChar, folder, StringComparison.OrdinalIgnoreCase);
                if (!inside || !name.StartsWith("push-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    error = "The request file is not in the session folder";
                    return null;
                }
                if (!File.Exists(path)) { error = "The request file is missing"; return null; }
                if (new FileInfo(path).Length > 256L * 1024 * 1024) { error = "The request file is too large"; return null; }

                JournalApplyPayload full = JsonSerializer.Deserialize<JournalApplyPayload>(File.ReadAllBytes(path), LiveProtocol.JSON);
                try { File.Delete(path); } catch { /* removed with the session folder */ }
                if (full == null || full.RequestId != request.RequestId) { error = "The request file does not match the request"; return null; }

                full.PayloadPath = null;
                return full;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Push request file unreadable: {ex.Message}");
                error = "The request file could not be read";
                return null;
            }
        }

        #endregion

        #region Request / answer

        /// <summary>
        /// Sends a request (replacing any unanswered one).
        /// </summary>
        /// <returns>Null on success, else a short reason.</returns>
        public string Send(JournalApplyPayload payload)
        {
            if (_disposed) { return "The push was closed"; }
            if (payload == null || payload.Entries.Count == 0) { return "Nothing to push"; }

            try
            {
                payload.RequestId = Guid.NewGuid().ToString("N");
                payload.PayloadPath = null;

                // Very large journals travel as a file beside the snapshots (messages are capped at 4 MB)
                byte[] inline = JsonSerializer.SerializeToUtf8Bytes(payload, LiveProtocol.JSON);
                object message = payload;
                if (inline.LongLength > INLINE_LIMIT_BYTES)
                {
                    string folder = LiveProtocol.SnapshotsFolder(Session.SessionId);
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, $"push-{payload.RequestId}.json");
                    File.WriteAllBytes(path, inline);
                    message = new JournalApplyPayload
                    {
                        RequestId = payload.RequestId,
                        DryRun = payload.DryRun,
                        ToleranceMm = payload.ToleranceMm,
                        ApplyConflicts = payload.ApplyConflicts,
                        ModelKey = payload.ModelKey,
                        PhaseName = payload.PhaseName,
                        ExistingPhaseName = payload.ExistingPhaseName,
                        FileName = payload.FileName,
                        PayloadPath = path
                    };
                    Utilities.Log_Utils.Write($"Push {payload.RequestId}: {inline.LongLength:N0} bytes sent by file.");
                }

                if (_channel.Send(MessageTypes.JOURNAL_APPLY, message) == null) { return "The request could not be written to the session folder"; }

                _requestId = payload.RequestId;
                WaitingDryRun = payload.DryRun;
                _waited = 0f;
                _timeout = TIMEOUT_SECONDS + TIMEOUT_PER_ENTRY_SECONDS * payload.Entries.Count;
                Utilities.Log_Utils.Write($"Push {payload.RequestId}: {(payload.DryRun ? "dry run" : "apply")} of {payload.Entries.Count} entries to session {Session.SessionId}.");
                return null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Push request failed: {ex}");
                return ex.Message;
            }
        }

        /// <summary>
        /// Per frame while waiting: takes the answer if it has arrived.
        /// </summary>
        /// <param name="dt">Frame time (seconds), for the timeout.</param>
        /// <param name="result">The answer, when it arrives.</param>
        /// <param name="error">Set on timeout or when the session ended.</param>
        /// <returns>True when finished (with a result or an error).</returns>
        public bool Poll(float dt, out JournalResultPayload result, out string error)
        {
            result = null;
            error = null;
            if (_requestId == null) { return false; }

            while (_channel.TryReceive(out Envelope envelope))
            {
                if (envelope.Type == MessageTypes.SESSION_CLOSING)
                {
                    _requestId = null;
                    error = envelope.Read<MessagePayload>()?.Message ?? "The Revit session ended";
                    return true;
                }
                if (envelope.Type != MessageTypes.JOURNAL_RESULT) { continue; }

                JournalResultPayload answer = envelope.Read<JournalResultPayload>();
                if (answer == null || !string.Equals(answer.RequestId, _requestId, StringComparison.Ordinal)) { continue; }

                _requestId = null;
                result = answer;
                return true;
            }

            _waited += dt;
            if (_waited >= _timeout)
            {
                _requestId = null;
                error = LiveSessions.ReadInfo(Session.SessionId)?.IsAlive() == true
                    ? "Revit did not answer. It may be busy (finish any command there), or the BimGo add-in needs updating."
                    : "Lost contact with Revit";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gives up on the current request (its answer, if any, is ignored).
        /// </summary>
        public void Abandon() => _requestId = null;

        #endregion

        /// <summary>
        /// Closes the temporary channel.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _channel.Dispose();
        }
    }
}
