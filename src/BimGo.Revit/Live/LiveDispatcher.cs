using System.Runtime.InteropServices;
using BimGo.Edits;
using BimGo.Extraction;
using BimGo.Format;
using BimGo.Scene;
using UIEvents = Autodesk.Revit.UI.Events;
using DBEvents = Autodesk.Revit.DB.Events;

// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// Runs every live session's Revit-side work. One <see cref="ExternalEvent"/> is shared by all sessions: a
    /// session's channel raises it when messages arrive, and <see cref="Execute"/> then handles them on the Revit
    /// thread (edits, refreshes, selection). Also owns the session registry, the document events (closing,
    /// changes made outside BimGo) and the ribbon's session status.
    /// </summary>
    internal sealed class LiveDispatcher : IExternalEventHandler
    {
        #region Fields

        private static readonly object LOCK = new();
        private static readonly List<SessionHost> HOSTS = new();
        private static ExternalEvent _event;
        private static nint _revitWindow;
        private static volatile bool _statusDirty = true;

        /// <summary>The ribbon's status button (its text shows the session state).</summary>
        public static PushButton StatusButton { get; set; }

        #endregion

        #region Setup

        /// <summary>
        /// Subscribes to the application and document events (add-in startup) and tidies stale session folders.
        /// </summary>
        public static void Initialise(UIControlledApplication app)
        {
            app.ControlledApplication.DocumentClosing += OnDocumentClosing;
            app.ControlledApplication.DocumentChanged += OnDocumentChanged;
            app.ApplicationClosing += OnApplicationClosing;
            app.Idling += OnIdling;

            // Off the startup path: folder IO only
            Task.Run(LiveSessions.CleanupStale);
        }

        /// <summary>
        /// Creates the shared external event (needs an API context, e.g. a command).
        /// </summary>
        public static void EnsureEvent(UIApplication uiApp)
        {
            lock (LOCK)
            {
                _revitWindow = uiApp.MainWindowHandle;
                _event ??= ExternalEvent.Create(new LiveDispatcher());
            }
        }

        /// <summary>
        /// Ends every session (add-in shutdown).
        /// </summary>
        public static void Shutdown(string reason)
        {
            List<SessionHost> hosts;
            lock (LOCK)
            {
                hosts = HOSTS.ToList();
                HOSTS.Clear();
            }
            foreach (SessionHost host in hosts) { host.Close(reason); }
        }

        #endregion

        #region Registry

        /// <summary>The open sessions.</summary>
        public static IReadOnlyList<SessionHost> Hosts
        {
            get { lock (LOCK) { return HOSTS.ToList(); } }
        }

        /// <summary>
        /// The session of a document, or null.
        /// </summary>
        public static SessionHost Find(Document doc)
        {
            if (doc == null) { return null; }
            lock (LOCK)
            {
                return HOSTS.FirstOrDefault(h => !h.IsClosed && SameDocument(h.Document, doc));
            }
        }

        /// <summary>
        /// The session of a document, created if needed (Revit thread).
        /// </summary>
        public static SessionHost GetOrCreate(Document doc)
        {
            SessionHost existing = Find(doc);
            if (existing != null) { return existing; }

            var host = new SessionHost(doc, Environment.ProcessId);
            lock (LOCK) { HOSTS.Add(host); }
            MarkStatusDirty();
            return host;
        }

        /// <summary>
        /// Ends a session and forgets it.
        /// </summary>
        public static void End(SessionHost host, string reason)
        {
            if (host == null) { return; }
            lock (LOCK) { HOSTS.Remove(host); }
            host.Close(reason);
            MarkStatusDirty();
        }

        private static bool SameDocument(Document a, Document b)
        {
            try
            {
                if (a == null || b == null || !a.IsValidObject || !b.IsValidObject) { return false; }
                return ReferenceEquals(a, b) || a.Equals(b);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Raising

        /// <summary>
        /// Any thread: asks Revit to run the handler at its next idle moment. Revit only checks for raised events
        /// when its message loop turns over (which may not happen while the app has focus), so a WM_NULL is posted
        /// to its main window to wake it.
        /// </summary>
        public static void Raise()
        {
            try
            {
                ExternalEvent externalEvent = _event;
                externalEvent?.Raise();
                if (_revitWindow != 0) { PostMessageW(_revitWindow, WM_NULL, 0, 0); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Raise failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Any thread: the ribbon status text needs updating (applied on the next Idling).
        /// </summary>
        public static void MarkStatusDirty() => _statusDirty = true;

        #endregion

        #region IExternalEventHandler

        /// <summary>
        /// Revit thread: handles every waiting message of every session, in order.
        /// </summary>
        public void Execute(UIApplication app)
        {
            foreach (SessionHost host in Hosts)
            {
                while (!host.IsClosed && host.TryReceive(out Envelope envelope))
                {
                    try
                    {
                        Handle(app, host, envelope);
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"Live message {envelope.Type} failed: {ex}");
                    }
                }
            }
        }

        /// <summary>
        /// The handler's name (shown by Revit if something goes wrong).
        /// </summary>
        public string GetName() => "BimGo live session";

        private static void Handle(UIApplication app, SessionHost host, Envelope envelope)
        {
            switch (envelope.Type)
            {
                case MessageTypes.HELLO:
                    host.Send(MessageTypes.HELLO_ACK, new HelloAckPayload
                    {
                        DocTitle = host.Info.DocTitle,
                        RevitVersion = host.Info.RevitVersion,
                        PhaseName = host.Info.PhaseName,
                        ExistingPhaseName = host.Info.ExistingPhaseName
                    }, envelope.Id);
                    MarkStatusDirty();
                    break;

                case MessageTypes.EDIT:
                    EditRequest request = envelope.Read<EditRequest>();
                    if (request == null) { break; }
                    EditResult result = host.Editor.Apply(host.Document, request);
                    host.Send(MessageTypes.EDIT_RESULT, result, envelope.Id);
                    break;

                case MessageTypes.EXTRACT_REQUEST:
                    Refresh(host, "refresh", envelope.Id);
                    break;

                case MessageTypes.SELECT:
                    SelectPayload select = envelope.Read<SelectPayload>();
                    MessagePayload selected = select?.Linked is { Length: > 0 } linked
                        ? SelectLinked(app, host, linked) ?? Select(app, host, select.ElementIds ?? Array.Empty<long>())
                        : Select(app, host, select?.ElementIds ?? Array.Empty<long>());
                    host.Send(MessageTypes.SELECT_RESULT, selected, envelope.Id);
                    break;

                case MessageTypes.JOURNAL_APPLY:
                    host.Send(MessageTypes.JOURNAL_RESULT, PushJournal(host, envelope.Read<JournalApplyPayload>()), envelope.Id);
                    break;

                case MessageTypes.DETACH:
                    Utilities.Log_Utils.Write($"Session {host.SessionId}: app detached.");
                    MarkStatusDirty();
                    break;
            }
        }

        #endregion

        #region Work

        /// <summary>
        /// Re-extracts the session's document with the saved options and announces the new snapshot (Revit thread).
        /// </summary>
        /// <param name="host">The session.</param>
        /// <param name="reason">"go" or "refresh".</param>
        /// <param name="replyTo">The request's id, or null.</param>
        /// <returns>True on success.</returns>
        public static bool Refresh(SessionHost host, string reason, string replyTo)
        {
            try
            {
                Document doc = host.Document;
                if (doc == null || !doc.IsValidObject) { throw new InvalidOperationException("The model is no longer open."); }

                // Progress with Cancel (the walkthrough is told "cancelled in Revit" and keeps the snapshot it has)
                var progress = new Utilities.OperationProgress();
                using (Forms.ProgressWindow.Show($"Refreshing {doc.Title}", progress, _revitWindow))
                {
                    SceneData scene = SceneExtractor.Extract(new UIDocument(doc), LaunchSettings.LoadOrDefault(), progress, liveSession: true);
                    progress.Begin("Writing the snapshot", 0.85, 1.0);
                    return Announce(host, scene, reason, replyTo, progress);
                }
            }
            catch (OperationCanceledException)
            {
                Utilities.Log_Utils.Write("Refresh cancelled in Revit.");
                host.Send(MessageTypes.EXTRACT_FAILED, new MessagePayload { Success = false, Message = "The refresh was cancelled in Revit." }, replyTo);
                return false;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Refresh failed: {ex}");
                host.Send(MessageTypes.EXTRACT_FAILED, new MessagePayload { Success = false, Message = ex.Message }, replyTo);
                return false;
            }
        }

        /// <summary>
        /// Writes a scene as the session's snapshot and tells the app.
        /// </summary>
        /// <returns>True on success.</returns>
        public static bool Announce(SessionHost host, SceneData scene, string reason, string replyTo, Utilities.OperationProgress progress = null)
        {
            SnapshotReadyPayload ready = host.WriteSnapshot(scene, Commands.Cmds_BimGo.CommandSteps.Writer, reason, out string error, progress);
            if (ready == null)
            {
                host.Send(MessageTypes.EXTRACT_FAILED, new MessagePayload { Success = false, Message = error ?? "The snapshot could not be written." }, replyTo);
                return false;
            }
            host.Send(MessageTypes.EXTRACT_READY, ready, replyTo);
            return true;
        }

        /// <summary>
        /// Applies (or previews) a standalone file's journal (Revit thread). Large requests arrive as a file in the
        /// session's snapshots folder.
        /// </summary>
        private static JournalResultPayload PushJournal(SessionHost host, JournalApplyPayload request)
        {
            JournalApplyPayload full = JournalPush.ResolvePayloadFile(host.SessionId, request, out string error);
            if (full == null)
            {
                return new JournalResultPayload { RequestId = request?.RequestId ?? string.Empty, DryRun = request?.DryRun ?? true, Success = false, Message = error };
            }

            return host.Editor.ApplyJournal(host.Document, full, host.Info.ModelKey);
        }

        /// <summary>
        /// Selects and shows elements in Revit (the session's document must be the active one).
        /// </summary>
        private static MessagePayload Select(UIApplication app, SessionHost host, long[] ids)
        {
            UIDocument uiDoc = app.ActiveUIDocument;
            if (uiDoc == null || !SameDocument(uiDoc.Document, host.Document))
            {
                return new MessagePayload { Success = false, Message = $"Switch to {host.Info.DocTitle} in Revit to show elements there" };
            }

            var elementIds = ids
                .Where(id => id > 0)
                .Select(id => new ElementId(id))
                .Where(id => host.Document.GetElement(id) != null)
                .ToList();
            if (elementIds.Count == 0)
            {
                return new MessagePayload { Success = false, Message = "That element is no longer in the Revit model" };
            }

            uiDoc.Selection.SetElementIds(elementIds);
            try
            {
                uiDoc.ShowElements(elementIds);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"ShowElements failed: {ex.Message}");
            }
            if (_revitWindow != 0) { SetForegroundWindow(_revitWindow); }

            return new MessagePayload { Success = true, Message = elementIds.Count == 1 ? "Selected in Revit" : $"Selected {elementIds.Count} elements in Revit" };
        }

        /// <summary>
        /// Selects elements inside linked models by link reference and zooms the active view to them.
        /// </summary>
        /// <returns>The answer, or null to fall back to selecting the link instances.</returns>
        private static MessagePayload SelectLinked(UIApplication app, SessionHost host, LinkedElementRef[] linked)
        {
            UIDocument uiDoc = app.ActiveUIDocument;
            if (uiDoc == null || !SameDocument(uiDoc.Document, host.Document)) { return null; }

            try
            {
                var references = new List<DB.Reference>();
                XYZ min = null, max = null;
                foreach (LinkedElementRef item in linked)
                {
                    if (item == null) { continue; }
                    if (host.Document.GetElement(new ElementId(item.LinkInstanceId)) is not RevitLinkInstance instance) { continue; }
                    if (instance.GetLinkDocument()?.GetElement(new ElementId(item.ElementId)) is not Element element) { continue; }

                    references.Add(new DB.Reference(element).CreateLinkReference(instance));

                    // The element's box in host coordinates (corners through the link's transform)
                    BoundingBoxXYZ box = element.get_BoundingBox(null);
                    if (box == null) { continue; }
                    Transform transform = instance.GetTotalTransform().Multiply(box.Transform ?? Transform.Identity);
                    for (int i = 0; i < 8; i++)
                    {
                        XYZ corner = transform.OfPoint(new XYZ(
                            (i & 1) == 0 ? box.Min.X : box.Max.X,
                            (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                            (i & 4) == 0 ? box.Min.Z : box.Max.Z));
                        min = min == null ? corner : new XYZ(Math.Min(min.X, corner.X), Math.Min(min.Y, corner.Y), Math.Min(min.Z, corner.Z));
                        max = max == null ? corner : new XYZ(Math.Max(max.X, corner.X), Math.Max(max.Y, corner.Y), Math.Max(max.Z, corner.Z));
                    }
                }
                if (references.Count == 0) { return null; }

                uiDoc.Selection.SetReferences(references);
                if (min != null)
                {
                    try
                    {
                        UIView view = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uiDoc.ActiveView.Id);
                        view?.ZoomAndCenterRectangle(min, max);
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"Zoom to linked element failed: {ex.Message}");
                    }
                }
                if (_revitWindow != 0) { SetForegroundWindow(_revitWindow); }
                return new MessagePayload { Success = true, Message = "Selected in the linked model in Revit" };
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Linked selection failed (selecting the link instead): {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Events

        private static void OnDocumentClosing(object sender, DBEvents.DocumentClosingEventArgs e)
        {
            try
            {
                SessionHost host = Find(e.Document);
                if (host != null) { End(host, "The model was closed in Revit"); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"DocumentClosing handling failed: {ex.Message}");
            }
        }

        private static void OnApplicationClosing(object sender, UIEvents.ApplicationClosingEventArgs e)
        {
            Shutdown("Revit is closing");
        }

        /// <summary>
        /// Counts changes made outside BimGo (BimGo's own committed "BimGo: …" transactions are ignored; undoing them
        /// counts, since the walkthrough still shows the edit).
        /// </summary>
        private static void OnDocumentChanged(object sender, DBEvents.DocumentChangedEventArgs e)
        {
            try
            {
                lock (LOCK)
                {
                    if (HOSTS.Count == 0) { return; }
                }

                SessionHost host = Find(e.GetDocument());
                if (host == null) { return; }

                // BimGo's own commits are already in the walkthrough, and rolling back a BimGo transaction or push
                // preview changes nothing; undoing a BimGo edit does count (the walkthrough still shows it)
                bool ownWork = e.Operation is DBEvents.UndoOperation.TransactionCommitted
                    or DBEvents.UndoOperation.TransactionRolledBack
                    or DBEvents.UndoOperation.TransactionGroupRolledBack;
                if (ownWork)
                {
                    IList<string> names = e.GetTransactionNames();
                    if (names.Count > 0 && names.All(n => n != null && n.StartsWith("BimGo:", StringComparison.Ordinal))) { return; }
                }

                host.NoteChanges(e.GetAddedElementIds().Count, e.GetModifiedElementIds().Count, e.GetDeletedElementIds().Count);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"DocumentChanged handling failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the ribbon status text when something changed (cheap check otherwise).
        /// </summary>
        private static void OnIdling(object sender, UIEvents.IdlingEventArgs e)
        {
            if (!_statusDirty) { return; }
            _statusDirty = false;

            try
            {
                if (StatusButton == null) { return; }
                List<SessionHost> hosts = Hosts.Where(h => !h.IsClosed).ToList();
                string state = hosts.Count == 0 ? "off" : hosts.Any(h => h.AppAttached) ? "attached" : "ready";
                StatusButton.ItemText = "Live\n" + state;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Status update failed: {ex.Message}");
            }
        }

        #endregion

        #region Native

        private const uint WM_NULL = 0x0000;

        [DllImport("user32.dll")]
        private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hwnd);

        #endregion
    }
}
