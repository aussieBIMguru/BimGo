using System.Windows.Interop;
using BimGo.Edits;
using BimGo.Extraction;
using BimGo.Format;
using BimGo.Scene;
using WinForms = System.Windows.Forms;

// The class belongs to the Commands namespace
namespace BimGo.Commands.Cmds_BimGo
{
    /// <summary>
    /// Shared steps of the BimGo commands.
    /// </summary>
    internal static class CommandSteps
    {
        /// <summary>
        /// Who writes files from this add-in (manifest generator).
        /// </summary>
        public static WriterInfo Writer => new($"BimGo for Revit {Globals.REVIT_VERSION_STR}", Globals.ADDIN_VERSION);

        /// <summary>
        /// Shows the Options dialog for the active document and saves the choices.
        /// </summary>
        /// <param name="uiApp">The UIApplication.</param>
        /// <param name="primaryButtonText">The confirm button's text.</param>
        /// <returns>The settings, or null if cancelled.</returns>
        public static LaunchSettings ShowOptions(UIApplication uiApp, string primaryButtonText)
        {
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            int[] counts = CategoryCatalog.All.Select(def => CategoryResolver.Count(doc, def)).ToArray();

            // The model's phases; the default new phase is the launch view's (the dialog defaults existing to the one before)
            PhasePair defaults = PhaseResolver.Resolve(doc, uiDoc.ActiveView, existingName: null, newName: null);
            var phases = new Forms.PhaseChoices
            {
                Names = PhaseResolver.All(doc).Select(p => p.Name).ToList(),
                DefaultNew = defaults.New?.Name
            };

            // The model's link instances (none extracted unless ticked; the choice is remembered per host model)
            var links = new Forms.LinkChoices
            {
                HostKey = LinkResolver.HostKey(doc),
                ModelFolder = ModelFolderResolver.FolderOf(doc),
                Items = LinkResolver.Candidates(doc).Select(c => new Forms.LinkChoice
                {
                    UniqueId = c.UniqueId,
                    Name = c.Name,
                    FileName = c.FileName,
                    IsLoaded = c.IsLoaded
                }).ToList()
            };

            // The active view (for "only elements visible in the active view")
            DB.View activeView = uiDoc.ActiveView;
            bool viewUsable = ViewScope.ShowsModel(activeView);
            var view = new Forms.ViewChoice
            {
                Available = viewUsable,
                Name = activeView?.Name ?? string.Empty,
                Is3D = activeView is View3D,
                ElementCount = viewUsable ? ViewScope.Count(doc, activeView) : 0
            };

            // "Review textures…": a resolve-only pass over what this Go would load, and the full material report
            var textures = new Forms.TextureReviewServices
            {
                Review = (trial, progress) => SceneExtractor.Review(uiDoc, trial, progress),
                ExportReport = progress =>
                {
                    string path = MaterialScan.Run(doc, uiApp.Application.VersionNumber, progress, out string summary);
                    return (path, summary);
                },
                Owner = uiApp.MainWindowHandle
            };

            var dialog = new Forms.OptionsWindow(settings, SceneExtractor.DescribeSpawn(uiDoc), counts,
                () => ParameterScanner.ScanNames(doc), primaryButtonText, phases, links, view, textures);
            new WindowInteropHelper(dialog).Owner = uiApp.MainWindowHandle;
            if (dialog.ShowDialog() != true) { return null; }

            settings.Save();
            return settings;
        }

        /// <summary>
        /// True if the command can run on the active document; otherwise sets a reason.
        /// </summary>
        public static bool CheckDocument(UIDocument uiDoc, out string reason)
        {
            reason = null;
            if (uiDoc == null || uiDoc.Document.IsFamilyDocument)
            {
                reason = "Open a project (not a family) to use BimGo.";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Go: opens the launch options, extracts a snapshot of the model into the document's live session and opens it
    /// in the BimGo app (starting the app, or handing the session to the running one). Demolish / move / clone
    /// edits made in the app come back to this model. Pressing Go again re-extracts and the app reloads.
    /// </summary>
    // Manual (not ReadOnly): the family library's temporary transaction needs a modifiable document. Go itself
    // commits nothing; that transaction is always rolled back (SceneExtractor.Library).
    [Transaction(TransactionMode.Manual)]
    public class Cmd_Launch : IExternalCommand
    {
        /// <summary>
        /// Execute the command.
        /// </summary>
        /// <param name="commandData">Command related data.</param>
        /// <param name="message">Command related message.</param>
        /// <param name="elements">Command related elements.</param>
        /// <returns>A Result.</returns>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (!CommandSteps.CheckDocument(uiDoc, out string reason)) { return FormCallers.Cancelled(reason); }

            if (Utilities.App_Utils.FindExe() == null)
            {
                return FormCallers.Error($"The BimGo app was not found.\n\nInstall it (or build it) to:\n{Utilities.App_Utils.InstalledExePath}");
            }

            try
            {
                LaunchSettings settings = CommandSteps.ShowOptions(uiApp, "Launch BimGo");
                if (settings == null) { return Result.Cancelled; }

                // Extraction (Revit API thread) and the snapshot, with a progress window and Cancel
                var progress = new Utilities.OperationProgress();
                using Forms.ProgressWindow window = Forms.ProgressWindow.Show($"Opening {uiDoc.Document.Title} in BimGo", progress, uiApp.MainWindowHandle);

                SceneData scene = SceneExtractor.Extract(uiDoc, settings, progress, liveSession: true);
                if (scene.Elements.Length == 0)
                {
                    window?.Dispose();
                    return FormCallers.Cancelled(settings.ActiveViewOnly
                        ? "Nothing to walk through: the active view shows no model geometry."
                        : "Nothing to walk through: the ticked categories have no geometry in this model.");
                }

                // The document's live session: snapshot + announce (an attached app reloads)
                Live.LiveDispatcher.EnsureEvent(uiApp);
                Live.SessionHost host = Live.LiveDispatcher.GetOrCreate(uiDoc.Document);
                progress.Begin("Writing the snapshot", 0.85, 1.0);
                bool announced = Live.LiveDispatcher.Announce(host, scene, "go", replyTo: null, progress);
                window?.Dispose();
                if (!announced)
                {
                    if (progress.CancelRequested) { return Result.Cancelled; }
                    return FormCallers.Error($"The snapshot could not be written. See the log:\n{Utilities.Log_Utils.LogPath}");
                }

                // Start the app on this session, or hand the session to the running app (which comes to the front)
                string error = Utilities.App_Utils.AttachInApp(host.SessionId);
                if (error != null) { return FormCallers.Error($"BimGo could not be started:\n{error}"); }
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Utilities.Log_Utils.Write("Go cancelled during extraction.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Launch failed: {ex}");
                return FormCallers.Error($"BimGo could not start:\n{ex.Message}\n\nLog: {Utilities.Log_Utils.LogPath}");
            }
        }
    }

    /// <summary>
    /// Live status: shows the active model's live session (snapshot, whether the app is attached) and offers to
    /// bring the app forward, push a fresh snapshot, or end the session.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class Cmd_Status : IExternalCommand
    {
        /// <summary>
        /// Execute the command.
        /// </summary>
        /// <param name="commandData">Command related data.</param>
        /// <param name="message">Command related message.</param>
        /// <param name="elements">Command related elements.</param>
        /// <returns>A Result.</returns>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (!CommandSteps.CheckDocument(uiDoc, out string reason)) { return FormCallers.Cancelled(reason); }

            try
            {
                Live.SessionHost host = Live.LiveDispatcher.Find(uiDoc.Document);
                int others = Live.LiveDispatcher.Hosts.Count(h => !h.IsClosed) - (host == null ? 0 : 1);
                string otherText = others > 0 ? $"\n\n{others} other open model{(others == 1 ? " has" : "s have")} a live session." : string.Empty;

                if (host == null)
                {
                    var none = new UI.TaskDialog("BimGo")
                    {
                        MainInstruction = "No live session for this model",
                        MainContent = "Press Go to walk this model in BimGo, with your edits coming back to Revit." + otherText,
                        CommonButtons = UI.TaskDialogCommonButtons.Close
                    };
                    none.Show();
                    return Result.Succeeded;
                }

                Live.LiveDispatcher.EnsureEvent(uiApp);
                string snapshot = host.Info.SnapshotNumber > 0
                    ? $"Snapshot {host.Info.SnapshotNumber} at {host.Info.SnapshotUtc.ToLocalTime():HH:mm:ss}"
                    : "No snapshot yet";
                var dialog = new UI.TaskDialog("BimGo")
                {
                    MainInstruction = host.AppAttached ? "Live session: BimGo is connected" : "Live session: waiting for BimGo to connect",
                    MainContent = $"{snapshot}.\nSession {host.SessionId[..8]}." + otherText,
                    CommonButtons = UI.TaskDialogCommonButtons.Close
                };
                dialog.AddCommandLink(UI.TaskDialogCommandLinkId.CommandLink1, host.AppAttached ? "Bring BimGo to the front" : "Open this session in BimGo");
                dialog.AddCommandLink(UI.TaskDialogCommandLinkId.CommandLink2, "Send a fresh snapshot", "Re-extracts the model with the saved options; BimGo reloads it where you stand.");
                dialog.AddCommandLink(UI.TaskDialogCommandLinkId.CommandLink3, "End the live session", "BimGo keeps the walkthrough open read-only (it can still be saved as a .bimgo file).");

                switch (dialog.Show())
                {
                    case UI.TaskDialogResult.CommandLink1:
                        string error = Utilities.App_Utils.AttachInApp(host.SessionId);
                        if (error != null) { return FormCallers.Error($"BimGo could not be started:\n{error}"); }
                        break;

                    case UI.TaskDialogResult.CommandLink2:
                        if (!Live.LiveDispatcher.Refresh(host, "refresh", replyTo: null))
                        {
                            return FormCallers.Error($"The snapshot could not be written. See the log:\n{Utilities.Log_Utils.LogPath}");
                        }
                        break;

                    case UI.TaskDialogResult.CommandLink3:
                        Live.LiveDispatcher.End(host, "The live session was ended in Revit");
                        break;
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Status failed: {ex}");
                return FormCallers.Error($"BimGo status failed:\n{ex.Message}");
            }
        }
    }

    /// <summary>
    /// Export .bimgo: opens the options, asks where to save, extracts the model and writes a standalone .bimgo
    /// (geometry, rooms, levels, metadata, picked parameters and the model's comments) for the BimGo app.
    /// Offers to open the file in BimGo afterwards.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class Cmd_Export : IExternalCommand
    {
        /// <summary>
        /// Execute the command.
        /// </summary>
        /// <param name="commandData">Command related data.</param>
        /// <param name="message">Command related message.</param>
        /// <param name="elements">Command related elements.</param>
        /// <returns>A Result.</returns>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            if (!CommandSteps.CheckDocument(uiDoc, out string reason)) { return FormCallers.Cancelled(reason); }

            try
            {
                Document doc = uiDoc.Document;
                LaunchSettings settings = CommandSteps.ShowOptions(uiApp, "Export…");
                if (settings == null) { return Result.Cancelled; }

                // Where to (asked before the extraction so a cancel costs nothing)
                string path = AskForPath(doc);
                if (path == null) { return Result.Cancelled; }

                // Extraction (Revit API thread) and writing, with a progress window and Cancel
                var progress = new Utilities.OperationProgress();
                using Forms.ProgressWindow window = Forms.ProgressWindow.Show($"Exporting {doc.Title}", progress, uiApp.MainWindowHandle);
                SceneData scene = SceneExtractor.Extract(uiDoc, settings, progress);
                if (scene.Elements.Length == 0)
                {
                    window?.Dispose();
                    return FormCallers.Cancelled(settings.ActiveViewOnly
                        ? "Nothing to export: the active view shows no model geometry."
                        : "Nothing to export: the ticked categories have no geometry in this model.");
                }

                // The model's comments travel with the file (from its BimGo folder, migrated there from older sidecars)
                CommentDocument comments = null;
                if (settings.LoadComments && !string.IsNullOrEmpty(scene.CommentsPath))
                {
                    CommentFiles.MigrateLegacy(scene.CommentsPath);
                    comments = CommentFiles.Read(scene.CommentsPath, out _);
                }

                // So do the bookmarks and the saved home of live walkthroughs (next to the comments)
                BookmarkDocument bookmarks = string.IsNullOrEmpty(scene.CommentsPath)
                    ? null
                    : BookmarkFiles.Read(BookmarkFiles.SidecarFor(scene.CommentsPath), out _);

                var document = new BimGoDocument
                {
                    Scene = scene,
                    Bookmarks = bookmarks ?? new BookmarkDocument { Model = scene.ModelTitle },
                    Comments = comments ?? new CommentDocument { Model = scene.ModelTitle },
                    Journal = new EditJournal(),
                    CreatedUtc = scene.Provenance.ExtractedUtc,
                    Kind = FileKinds.EXPORT,
                    Path = path
                };

                progress.Begin("Writing the .bimgo file", 0.85, 1.0);
                bool written = BimGoWriter.Write(path, document, CommandSteps.Writer, FileKinds.EXPORT, out string error, progress: progress);
                window?.Dispose();
                if (!written)
                {
                    if (progress.CancelRequested) { return Result.Cancelled; }
                    return FormCallers.Error($"The .bimgo file could not be written:\n{error}");
                }

                return OfferToOpen(path, scene, document.Comments.Comments.Count);
            }
            catch (OperationCanceledException)
            {
                Utilities.Log_Utils.Write("Export cancelled.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Export failed: {ex}");
                return FormCallers.Error($"BimGo could not export:\n{ex.Message}\n\nLog: {Utilities.Log_Utils.LogPath}");
            }
        }

        /// <summary>
        /// The Save dialog, starting beside the model (or in Documents for cloud / unsaved models).
        /// </summary>
        private static string AskForPath(Document doc)
        {
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            try
            {
                if (!doc.IsModelInCloud && !string.IsNullOrEmpty(doc.PathName) && Path.IsPathRooted(doc.PathName))
                {
                    string modelFolder = Path.GetDirectoryName(doc.PathName);
                    if (Directory.Exists(modelFolder)) { folder = modelFolder; }
                }
            }
            catch
            {
                // Documents
            }

            string name = doc.Title ?? "Model";
            foreach (char c in Path.GetInvalidFileNameChars()) { name = name.Replace(c, '_'); }

            using var dialog = new WinForms.SaveFileDialog
            {
                Title = "Export BimGo model",
                Filter = BimGoFormat.DIALOG_FILTER,
                DefaultExt = BimGoFormat.EXTENSION.TrimStart('.'),
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = folder,
                FileName = name + BimGoFormat.EXTENSION
            };
            if (dialog.ShowDialog() != WinForms.DialogResult.OK) { return null; }

            string path = dialog.FileName;
            return BimGoFormat.HasExtension(path) ? path : path + BimGoFormat.EXTENSION;
        }

        /// <summary>
        /// Reports the export and offers to open the file in the BimGo app.
        /// </summary>
        private static Result OfferToOpen(string path, SceneData scene, int comments)
        {
            long bytes = 0;
            try { bytes = new FileInfo(path).Length; }
            catch { /* size unknown */ }

            string summary = $"{scene.Elements.Length:N0} elements · {scene.TriangleCount:N0} triangles · {scene.Rooms.Length:N0} rooms · " +
                $"{comments:N0} comments · {bytes / (1024.0 * 1024.0):0.0} MB\n\n{path}";

            bool appFound = Utilities.App_Utils.FindExe() != null;
            var dialog = new UI.TaskDialog("BimGo")
            {
                MainInstruction = "Exported " + Path.GetFileName(path),
                MainContent = summary,
                CommonButtons = UI.TaskDialogCommonButtons.Close
            };
            if (appFound) { dialog.AddCommandLink(UI.TaskDialogCommandLinkId.CommandLink1, "Open in BimGo"); }
            else { dialog.FooterText = "Install the BimGo app to open .bimgo files."; }

            if (dialog.Show() == UI.TaskDialogResult.CommandLink1)
            {
                string error = Utilities.App_Utils.OpenInApp(path);
                if (error != null) { return FormCallers.Error($"BimGo could not be started:\n{error}"); }
            }
            return Result.Succeeded;
        }
    }
}
