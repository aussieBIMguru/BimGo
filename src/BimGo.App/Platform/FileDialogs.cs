using BimGo.Native;
using WinForms = System.Windows.Forms;

// The class belongs to the Platform namespace
namespace BimGo.Platform
{
    /// <summary>
    /// Modal file dialogs and questions, owned by the game window. Uses the in-box WinForms dialogs (which show the
    /// modern Windows file dialog) and MessageBoxW. Call on an STA thread (the app's main thread, or the game thread
    /// inside Revit). Never throws.
    /// </summary>
    internal static class FileDialogs
    {
        /// <summary>Result of <see cref="AskYesNoCancel"/>.</summary>
        public enum Answer
        {
            Yes,
            No,
            Cancel
        }

        /// <summary>
        /// Shows an Open dialog.
        /// </summary>
        /// <returns>The chosen path, or null if cancelled.</returns>
        public static string ShowOpen(nint owner, string title, string filter, string initialFolder)
        {
            try
            {
                using var dialog = new WinForms.OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true
                };
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) { dialog.InitialDirectory = initialFolder; }
                return dialog.ShowDialog(new Owner(owner)) == WinForms.DialogResult.OK ? dialog.FileName : null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Open dialog failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Shows a folder picker (the modern Explorer-style dialog).
        /// </summary>
        /// <returns>The chosen folder, or null if cancelled.</returns>
        public static string ShowFolder(nint owner, string description, string initialFolder)
        {
            try
            {
                using var dialog = new WinForms.FolderBrowserDialog
                {
                    Description = description ?? string.Empty,
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    AutoUpgradeEnabled = true
                };
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) { dialog.InitialDirectory = initialFolder; }
                return dialog.ShowDialog(new Owner(owner)) == WinForms.DialogResult.OK ? dialog.SelectedPath : null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Folder dialog failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Shows a Save dialog (asks before overwriting).
        /// </summary>
        /// <returns>The chosen path, or null if cancelled.</returns>
        public static string ShowSave(nint owner, string title, string filter, string initialFolder, string fileName, string defaultExtension)
        {
            try
            {
                using var dialog = new WinForms.SaveFileDialog
                {
                    Title = title,
                    Filter = filter,
                    FileName = fileName ?? string.Empty,
                    DefaultExt = defaultExtension?.TrimStart('.') ?? string.Empty,
                    AddExtension = true,
                    OverwritePrompt = true,
                    RestoreDirectory = true
                };
                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)) { dialog.InitialDirectory = initialFolder; }
                return dialog.ShowDialog(new Owner(owner)) == WinForms.DialogResult.OK ? dialog.FileName : null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Save dialog failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Asks a Yes / No / Cancel question.
        /// </summary>
        public static Answer AskYesNoCancel(nint owner, string text, string caption)
        {
            int result = Win32.MessageBoxW(owner, text, caption, Win32.MB_YESNOCANCEL | Win32.MB_ICONWARNING);
            return result switch
            {
                Win32.IDYES => Answer.Yes,
                Win32.IDNO => Answer.No,
                _ => Answer.Cancel
            };
        }

        /// <summary>
        /// Asks a Yes / No question.
        /// </summary>
        /// <returns>True for Yes.</returns>
        public static bool AskYesNo(nint owner, string text, string caption)
        {
            return Win32.MessageBoxW(owner, text, caption, Win32.MB_YESNO | Win32.MB_ICONQUESTION) == Win32.IDYES;
        }

        /// <summary>
        /// Shows an error message.
        /// </summary>
        public static void ShowError(nint owner, string text, string caption = "BimGo")
        {
            Win32.MessageBoxW(owner, text, caption, Win32.MB_OK | Win32.MB_ICONERROR);
        }

        /// <summary>
        /// Wraps a raw window handle as a dialog owner.
        /// </summary>
        private sealed class Owner : WinForms.IWin32Window
        {
            public Owner(nint handle) => Handle = handle;

            public nint Handle { get; }
        }
    }
}
