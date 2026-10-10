using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using BimGo.Edits;
using BimGo.Live;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Live-session protocol: envelope round-trips through a <see cref="FolderChannel"/> pair and the channel's
    /// validation (session id, protocol version, 4 MB cap, de-duplication, order). Uses temp folders, never the real
    /// %LocalAppData%\BimGo\Sessions.
    /// </summary>
    [TestClass]
    public sealed class LiveProtocolTests
    {
        private const string SESSION = "0123456789abcdef0123456789abcdef";

        /// <summary>Two channels wired back to back (app ↔ Revit) in a temp folder.</summary>
        private sealed class ChannelPair : IDisposable
        {
            public readonly TempFolder Folder = new();
            public readonly FolderChannel App;
            public readonly FolderChannel Revit;
            public string ToRevit => Path.Combine(Folder.Path, "to-revit");
            public string ToApp => Path.Combine(Folder.Path, "to-app");

            public ChannelPair(string revitSession = SESSION)
            {
                App = new FolderChannel(SESSION, ToRevit, ToApp, "test-app");
                Revit = new FolderChannel(revitSession, ToApp, ToRevit, "test-revit");
            }

            public void Dispose()
            {
                App.Dispose();
                Revit.Dispose();
                Folder.Dispose();
            }
        }

        /// <summary>Drains everything the channel has accepted after a scan.</summary>
        private static List<Envelope> Receive(FolderChannel channel)
        {
            channel.Scan();
            var received = new List<Envelope>();
            while (channel.TryReceive(out Envelope envelope)) { received.Add(envelope); }
            return received;
        }

        /// <summary>
        /// Drops a raw message file into an inbox, as another process would: written as .tmp, then renamed, so the
        /// channel's watcher / poll timer never sees it half written.
        /// </summary>
        private static string DropRaw(string inbox, string name, string json)
        {
            string path = Path.Combine(inbox, name);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return path;
        }

        private static string EnvelopeJson(string sessionId, int protocol, string id = null, string type = MessageTypes.DETACH) =>
            $"{{\"protocol\":{protocol},\"id\":\"{id ?? Guid.NewGuid().ToString("N")}\",\"seq\":1,\"sessionId\":\"{sessionId}\",\"type\":\"{type}\",\"sentUtc\":\"2026-10-05T00:00:00Z\",\"payload\":{{}}}}";

        [TestMethod]
        public void SendAndReceive_RoundTripsEnvelopeAndPayload()
        {
            using var pair = new ChannelPair();
            Envelope sent = pair.App.Send(MessageTypes.HELLO, new HelloPayload { AppPid = 1234, AppVersion = "1.0.0" });
            Assert.IsNotNull(sent);

            List<Envelope> received = Receive(pair.Revit);
            Assert.AreEqual(1, received.Count);
            Envelope envelope = received[0];
            Assert.AreEqual(sent.Id, envelope.Id);
            Assert.AreEqual(LiveProtocol.VERSION, envelope.Protocol);
            Assert.AreEqual(SESSION, envelope.SessionId);
            Assert.AreEqual(MessageTypes.HELLO, envelope.Type);
            Assert.AreEqual(1234, envelope.Read<HelloPayload>().AppPid);
            Assert.AreEqual("1.0.0", envelope.Read<HelloPayload>().AppVersion);

            // Handled messages are deleted; a file Windows held for a moment (anti-virus, indexer) goes on a later scan
            Assert.IsTrue(WaitUntilEmpty(pair.Revit, pair.ToRevit), "Handled messages are deleted");
        }

        /// <summary>Scans until the inbox is empty (up to 2 s): deletion can lag a moment on Windows.</summary>
        private static bool WaitUntilEmpty(FolderChannel channel, string inbox)
        {
            for (int i = 0; i < 40; i++)
            {
                if (Directory.GetFiles(inbox).Length == 0) { return true; }
                Thread.Sleep(50);
                channel.Scan();
            }
            return Directory.GetFiles(inbox).Length == 0;
        }

        [TestMethod]
        public void Reply_CarriesReplyTo()
        {
            using var pair = new ChannelPair();
            Envelope request = pair.App.Send(MessageTypes.SELECT, new SelectPayload { ElementIds = new long[] { 1, 2 } });
            Envelope received = Receive(pair.Revit)[0];
            CollectionAssert.AreEqual(new long[] { 1, 2 }, received.Read<SelectPayload>().ElementIds);

            pair.Revit.Send(MessageTypes.SELECT_RESULT, new MessagePayload { Success = true, Message = "ok" }, replyTo: received.Id);
            Envelope reply = Receive(pair.App)[0];
            Assert.AreEqual(request.Id, reply.ReplyTo);
            Assert.IsTrue(reply.Read<MessagePayload>().Success);
        }

        [TestMethod]
        public void Messages_ArriveInSendOrder()
        {
            using var pair = new ChannelPair();
            for (int i = 0; i < 5; i++)
            {
                pair.App.Send(MessageTypes.EXTRACT_REQUEST, new RefreshRequestPayload { Reason = $"r{i}" });
            }

            List<Envelope> received = Receive(pair.Revit);
            Assert.AreEqual(5, received.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual($"r{i}", received[i].Read<RefreshRequestPayload>().Reason);
                Assert.AreEqual(i + 1L, received[i].Seq);
            }
        }

        [TestMethod]
        public void MessageForAnotherSession_IsDiscardedAndDeleted()
        {
            using var pair = new ChannelPair();
            string file = DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", EnvelopeJson("ffffffffffffffffffffffffffffffff", 1));

            Assert.AreEqual(0, Receive(pair.Revit).Count);
            Assert.IsFalse(File.Exists(file));
        }

        [TestMethod]
        public void MessageFromANewerProtocol_IsDiscarded()
        {
            using var pair = new ChannelPair();
            DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", EnvelopeJson(SESSION, LiveProtocol.VERSION + 1));
            Assert.AreEqual(0, Receive(pair.Revit).Count);
        }

        [TestMethod]
        public void OlderOrSameProtocol_IsAccepted()
        {
            using var pair = new ChannelPair();
            DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", EnvelopeJson(SESSION, LiveProtocol.VERSION));
            Assert.AreEqual(1, Receive(pair.Revit).Count);
        }

        [TestMethod]
        public void OversizedMessage_IsDiscardedAndDeleted()
        {
            using var pair = new ChannelPair();
            var json = new StringBuilder(EnvelopeJson(SESSION, 1));
            json.Append(' ', (int)LiveProtocol.MAX_MESSAGE_BYTES); // valid JSON padded past the cap
            string file = DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", json.ToString());

            Assert.AreEqual(0, Receive(pair.Revit).Count);
            Assert.IsFalse(File.Exists(file));
        }

        [TestMethod]
        public void UnreadableOrUntypedMessages_AreDiscarded()
        {
            using var pair = new ChannelPair();
            DropRaw(pair.ToRevit, "20261005000000000-000001-bad.json", "{ not json");
            DropRaw(pair.ToRevit, "20261005000000000-000002-untyped.json", EnvelopeJson(SESSION, 1, type: ""));
            Assert.AreEqual(0, Receive(pair.Revit).Count);
        }

        [TestMethod]
        public void DuplicateIds_AreHandledOnce()
        {
            using var pair = new ChannelPair();
            string id = Guid.NewGuid().ToString("N");
            DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", EnvelopeJson(SESSION, 1, id));
            Assert.AreEqual(1, Receive(pair.Revit).Count);

            // The same message again (e.g. a delete that failed and a rescan)
            DropRaw(pair.ToRevit, "20261005000000000-000001-detach.json", EnvelopeJson(SESSION, 1, id));
            Assert.AreEqual(0, Receive(pair.Revit).Count);
        }

        [TestMethod]
        public void Envelope_ReadOfMissingOrWrongPayload_ReturnsNull()
        {
            Assert.IsNull(new Envelope().Read<HelloPayload>());

            var wrong = new Envelope { Payload = JsonSerializer.SerializeToElement("just a string") };
            Assert.IsNull(wrong.Read<HelloPayload>());
        }

        [TestMethod]
        public void JournalApplyPayload_RoundTripsThroughTheChannel()
        {
            using var pair = new ChannelPair();
            var payload = new JournalApplyPayload
            {
                DryRun = false, ApplyConflicts = true, ModelKey = "key", FileName = "model.bimgo",
                KnownClones = new List<CloneRef> { new() { CloneKey = 3, ElementId = 77 } },
                Entries = new List<JournalEntry> { new() { Seq = 1, Op = JournalOps.HIDE, Mode = JournalOps.MODE_DELETE, ElementId = 5, UniqueId = "u5" } }
            };
            pair.App.Send(MessageTypes.JOURNAL_APPLY, payload);

            JournalApplyPayload read = Receive(pair.Revit)[0].Read<JournalApplyPayload>();
            Assert.AreEqual(payload.RequestId, read.RequestId);
            Assert.IsFalse(read.DryRun);
            Assert.IsTrue(read.ApplyConflicts);
            Assert.AreEqual(5.0, read.ToleranceMm);
            Assert.AreEqual(77L, read.KnownClones[0].ElementId);
            Assert.AreEqual(JournalOps.MODE_DELETE, read.Entries[0].Mode);
        }

        [TestMethod]
        public void JournalResult_CountTalliesEachStatus()
        {
            var result = new JournalResultPayload
            {
                Results = new List<JournalEntryResult>
                {
                    new() { Status = JournalStatus.APPLIED }, new() { Status = JournalStatus.APPLIED },
                    new() { Status = JournalStatus.CONFLICT }, new() { Status = JournalStatus.FAILED },
                    new() { Status = JournalStatus.ALREADY_APPLIED }, new() { Status = JournalStatus.SKIPPED },
                    new() { Status = "something-new" } // unknown statuses count as skipped
                }
            };
            result.Count();

            Assert.AreEqual(2, result.Applied);
            Assert.AreEqual(1, result.Conflicts);
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(1, result.AlreadyApplied);
            Assert.AreEqual(2, result.Skipped);
        }

        [TestMethod]
        public void SessionIds_AreValidatedAsThirtyTwoHexDigits()
        {
            Assert.IsTrue(LiveProtocol.IsValidSessionId(LiveProtocol.NewSessionId()));
            Assert.IsTrue(LiveProtocol.IsValidSessionId(SESSION.ToUpperInvariant()));
            Assert.IsFalse(LiveProtocol.IsValidSessionId(null));
            Assert.IsFalse(LiveProtocol.IsValidSessionId(SESSION[..31]));
            Assert.IsFalse(LiveProtocol.IsValidSessionId(SESSION[..31] + "g"));
            Assert.IsFalse(LiveProtocol.IsValidSessionId(@"..\..\..\..\..\..\..\..\..\..\x")); // no path tricks
        }

        [TestMethod]
        public void Sessions_WithoutHeartbeatOrClosed_AreNotAlive()
        {
            var closed = new SessionInfo { State = SessionStates.CLOSED, HeartbeatUtc = DateTime.UtcNow, RevitPid = Environment.ProcessId };
            Assert.IsFalse(closed.IsAlive());

            var stale = new SessionInfo { State = SessionStates.READY, HeartbeatUtc = DateTime.UtcNow.AddMinutes(-1), RevitPid = Environment.ProcessId };
            Assert.IsFalse(stale.IsAlive());

            var live = new SessionInfo { State = SessionStates.READY, HeartbeatUtc = DateTime.UtcNow, RevitPid = Environment.ProcessId };
            Assert.IsTrue(live.IsAlive());
        }
    }
}
