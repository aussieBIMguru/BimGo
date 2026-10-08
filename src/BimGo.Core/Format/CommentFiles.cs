using System.Text.Json;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Reads and writes the comment sidecar kept beside a Revit model (&lt;model&gt;.bimgo-comments.json), and migrates
    /// the pre-BimGo sidecar (&lt;model&gt;.rvtgo.json). Never throws.
    /// </summary>
    public static class CommentFiles
    {
        // PascalCase as the RvtGo sidecar was written; reads are case-insensitive so either spelling loads
        private static readonly JsonSerializerOptions OPTIONS = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        /// <summary>
        /// The legacy sidecar path for a sidecar path (same folder and base name, .rvtgo.json), or null.
        /// </summary>
        public static string LegacyPathFor(string sidecarPath)
        {
            if (string.IsNullOrEmpty(sidecarPath) || !sidecarPath.EndsWith(BimGoFormat.SIDECAR_SUFFIX, StringComparison.OrdinalIgnoreCase)) { return null; }
            return sidecarPath[..^BimGoFormat.SIDECAR_SUFFIX.Length] + BimGoFormat.LEGACY_SIDECAR_SUFFIX;
        }

        /// <summary>
        /// Copies a legacy sidecar to the new name if only the legacy one exists (the old file is left in place).
        /// </summary>
        /// <returns>True if a migration happened.</returns>
        public static bool MigrateLegacy(string sidecarPath)
        {
            try
            {
                string legacy = LegacyPathFor(sidecarPath);
                if (legacy == null || File.Exists(sidecarPath) || !File.Exists(legacy)) { return false; }
                File.Copy(legacy, sidecarPath, overwrite: false);
                Utilities.Log_Utils.Write($"Comments migrated: {legacy} -> {sidecarPath}");
                return true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Comment migration skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads a sidecar.
        /// </summary>
        /// <param name="path">The sidecar path.</param>
        /// <param name="error">A reason on failure (null when the file simply doesn't exist).</param>
        /// <returns>The comments, or null if the file doesn't exist or can't be read.</returns>
        public static CommentDocument Read(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }
                CommentDocument document = JsonSerializer.Deserialize<CommentDocument>(File.ReadAllText(path), OPTIONS);
                if (document == null) { return null; }
                document.Comments ??= new List<CommentRecord>();
                document.Comments.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Text));
                return document;
            }
            catch (Exception ex)
            {
                error = $"Comments could not be read: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return null;
            }
        }

        /// <summary>
        /// Writes a sidecar atomically (temp file, then replace).
        /// </summary>
        /// <returns>True on success.</returns>
        public static bool Write(string path, CommentDocument document, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(document, OPTIONS));
                File.Move(temp, path, overwrite: true);
                ModelFolders.MirrorAfterWrite(path);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Comments could not be saved: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return false;
            }
        }
    }
}
