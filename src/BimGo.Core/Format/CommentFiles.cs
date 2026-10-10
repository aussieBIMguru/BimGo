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
                CommentSnapshots.LoadBeside(path, document);
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
                CommentSnapshots.WriteBeside(path, document);
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

    /// <summary>
    /// The larger comment pictures used for BCF snapshots (BCF round). Each is a JPEG named
    /// <c>comments/&lt;comment id&gt;.jpg</c>: an entry inside a .bimgo, or a file in the <c>comments</c> folder beside
    /// <c>comments.json</c> in a model folder (only there: nothing is written beside a Revit model). Never throws.
    /// </summary>
    public static class CommentSnapshots
    {
        /// <summary>The folder (and entry prefix) holding the pictures.</summary>
        public const string FOLDER = "comments/";

        /// <summary>Largest picture read back (bytes).</summary>
        private const long MAX_BYTES = 16L * 1024 * 1024;

        /// <summary>
        /// The picture name for a comment id: "comments/&lt;id&gt;.jpg" (characters other than letters, digits and
        /// '-' replaced).
        /// </summary>
        public static string NameFor(string commentId)
        {
            var builder = new System.Text.StringBuilder(FOLDER.Length + 40);
            builder.Append(FOLDER);
            foreach (char c in commentId ?? string.Empty)
            {
                builder.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
            }
            if (builder.Length == FOLDER.Length) { builder.Append(Guid.NewGuid().ToString("N")); }
            return builder.Append(".jpg").ToString();
        }

        /// <summary>True for "comments/&lt;safe name&gt;.jpg" (no sub-folders, no "..").</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(FOLDER, StringComparison.Ordinal) || !name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) { return false; }
            string file = name[FOLDER.Length..^4];
            if (file.Length == 0 || file.Length > 80) { return false; }
            foreach (char c in file)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Reads the pictures of a sidecar's comments from the folder beside it (missing files leave the comment
        /// without one).
        /// </summary>
        public static void LoadBeside(string sidecarPath, CommentDocument document)
        {
            try
            {
                string folder = Path.GetDirectoryName(sidecarPath);
                if (string.IsNullOrEmpty(folder) || document?.Comments == null) { return; }
                foreach (CommentRecord record in document.Comments)
                {
                    if (record == null || !IsValidName(record.Snapshot)) { continue; }
                    var file = new FileInfo(Path.Combine(folder, record.Snapshot.Replace('/', Path.DirectorySeparatorChar)));
                    if (!file.Exists || file.Length > MAX_BYTES) { continue; }
                    record.SnapshotData = File.ReadAllBytes(file.FullName);
                    record.SnapshotDirty = false;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Comment pictures not read: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes changed pictures beside a model folder's comments.json and removes pictures no comment uses any
        /// more. Does nothing for an old-style sidecar beside a model (pictures then stay in memory).
        /// </summary>
        public static void WriteBeside(string sidecarPath, CommentDocument document)
        {
            try
            {
                if (ModelFolders.FolderOf(sidecarPath) == null || document?.Comments == null) { return; }
                string folder = Path.Combine(Path.GetDirectoryName(sidecarPath), FOLDER.TrimEnd('/'));
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CommentRecord record in document.Comments)
                {
                    if (record == null || !IsValidName(record.Snapshot)) { continue; }
                    string file = Path.Combine(folder, record.Snapshot[FOLDER.Length..]);
                    used.Add(Path.GetFileName(file));
                    if (record.SnapshotData == null || (!record.SnapshotDirty && File.Exists(file))) { continue; }
                    Directory.CreateDirectory(folder);
                    string temp = file + ".tmp";
                    File.WriteAllBytes(temp, record.SnapshotData);
                    File.Move(temp, file, overwrite: true);
                    record.SnapshotDirty = false;
                }

                if (!Directory.Exists(folder)) { return; }
                foreach (string file in Directory.EnumerateFiles(folder, "*.jpg"))
                {
                    if (!used.Contains(Path.GetFileName(file))) { File.Delete(file); }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Comment pictures not saved: {ex.Message}");
            }
        }
    }
}
