using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// What <c>model.json</c> in a per-model folder records: which model the folder belongs to, and whether its
    /// sidecars are also shared beside the model.
    /// </summary>
    public sealed class ModelFolderInfo
    {
        /// <summary>The model's title when last seen.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The model's path when last seen (local, central or cloud description).</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The normalised key source the folder name was hashed from ("cloud:…", "central:…", "local:…").</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>True: sidecars are also written beside the model (and newer ones there are read in at Go).</summary>
        public bool ShareBesideModel { get; set; }

        /// <summary>The comments sidecar beside the model (the other sidecars sit next to it), or empty.</summary>
        public string BesideCommentsPath { get; set; } = string.Empty;

        /// <summary>When the folder was last prepared (UTC, ISO 8601).</summary>
        public string Updated { get; set; } = string.Empty;
    }

    /// <summary>
    /// BimGo's own per-model folders, <c>%LocalAppData%\BimGo\Models\&lt;title&gt;_&lt;hash&gt;\</c>, holding the data a
    /// live Revit session keeps between walkthroughs: <c>comments.json</c>, <c>bookmarks.json</c>, <c>sun.json</c>,
    /// <c>visibility.json</c>, <c>texture-overrides.json</c> and <c>model.json</c>. Used instead of sidecars beside
    /// the model, which is often in the cloud or read-only. A .bimgo file keeps its own copies inside the file.
    /// <list type="bullet">
    /// <item>The key, most stable first: the cloud model GUID, else the workshared central path, else the local path
    /// (the Revit side builds it; <see cref="NormaliseKey"/> makes paths case- and slash-insensitive). Two local
    /// copies of one model get two folders.</item>
    /// <item>Migration: sidecars beside the model (or in the old <c>Comments</c> folder, or an RvtGo
    /// <c>.rvtgo.json</c>) are copied in once when the folder has none; the originals are left as a backup.</item>
    /// <item>Sharing (optional, off by default): writes are mirrored beside the model, and at each Go a newer file
    /// there replaces the folder's copy, so colleagues on a shared drive see each other's comments.</item>
    /// </list>
    /// Never throws: failures are logged.
    /// </summary>
    public static class ModelFolders
    {
        #region Names

        /// <summary>Comments in a model folder.</summary>
        public const string COMMENTS_FILE = "comments.json";

        /// <summary>Bookmarks and home in a model folder.</summary>
        public const string BOOKMARKS_FILE = "bookmarks.json";

        /// <summary>Sun state in a model folder.</summary>
        public const string SUN_FILE = "sun.json";

        /// <summary>Walkthrough-only hiding in a model folder.</summary>
        public const string VISIBILITY_FILE = "visibility.json";

        /// <summary>Texture choices in a model folder (machine-specific image paths: never shared).</summary>
        public const string TEXTURE_OVERRIDES_FILE = "texture-overrides.json";

        /// <summary>The folder's description.</summary>
        public const string INFO_FILE = "model.json";

        /// <summary>Longest title part of a folder name.</summary>
        private const int MAX_TITLE = 60;

        // The sidecars that move into the folder: folder file name and beside-model suffix (comments first)
        private static readonly (string File, string Suffix)[] SIDECARS =
        {
            (COMMENTS_FILE, BimGoFormat.SIDECAR_SUFFIX),
            (BOOKMARKS_FILE, BimGoFormat.BOOKMARK_SIDECAR_SUFFIX),
            (SUN_FILE, BimGoFormat.SUN_SIDECAR_SUFFIX),
            (VISIBILITY_FILE, BimGoFormat.VISIBILITY_SIDECAR_SUFFIX)
        };

        #endregion

        #region Paths

        /// <summary>
        /// The root: <c>%LocalAppData%\BimGo\Models</c>.
        /// </summary>
        public static string DefaultRoot => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BimGo", "Models");

        /// <summary>
        /// Makes a key source stable: paths ("local:", "central:") are trimmed, use backslashes and are lower-cased
        /// (Windows paths are case-insensitive); other kinds ("cloud:", "unsaved:") are trimmed only.
        /// </summary>
        public static string NormaliseKey(string keySource)
        {
            string key = (keySource ?? string.Empty).Trim();
            if (key.StartsWith("local:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("central:", StringComparison.OrdinalIgnoreCase))
            {
                key = key.Replace('/', '\\').ToLowerInvariant();
            }
            return key;
        }

        /// <summary>
        /// The first 8 hex characters of the SHA-256 of the normalised key.
        /// </summary>
        public static string HashOf(string keySource)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormaliseKey(keySource)));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }

        /// <summary>
        /// The folder name: <c>&lt;safe title&gt;_&lt;hash&gt;</c> (title trimmed to 60 characters; "Model" when blank).
        /// </summary>
        public static string FolderNameFor(string title, string keySource)
        {
            string safe = (title ?? string.Empty).Trim();
            if (safe.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) { safe = safe[..^4]; }
            var builder = new StringBuilder(safe.Length);
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in safe)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' || c == '\\' || c == '/' || char.IsControl(c) ? '_' : c);
            }
            safe = builder.ToString().Trim(' ', '.');
            if (safe.Length > MAX_TITLE) { safe = safe[..MAX_TITLE].TrimEnd(' ', '.'); }
            if (safe.Length == 0) { safe = "Model"; }
            return safe + "_" + HashOf(keySource);
        }

        /// <summary>
        /// The model's folder (not created).
        /// </summary>
        /// <param name="title">The model title.</param>
        /// <param name="keySource">The key source (see the class remarks).</param>
        /// <param name="root">The root, or null for <see cref="DefaultRoot"/>.</param>
        public static string FolderFor(string title, string keySource, string root = null) =>
            System.IO.Path.Combine(root ?? DefaultRoot, FolderNameFor(title, keySource));

        /// <summary>
        /// The model folder a comments path sits in, or null when the path is an old-style sidecar (beside a model or in
        /// the old Comments folder) or empty.
        /// </summary>
        public static string FolderOf(string commentsPath)
        {
            if (string.IsNullOrWhiteSpace(commentsPath)) { return null; }
            return string.Equals(System.IO.Path.GetFileName(commentsPath), COMMENTS_FILE, StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.GetDirectoryName(commentsPath)
                : null;
        }

        /// <summary>
        /// The folder file for a beside-model suffix (<see cref="BimGoFormat.BOOKMARK_SIDECAR_SUFFIX"/> → bookmarks.json…),
        /// or null for an unknown suffix.
        /// </summary>
        public static string FileForSuffix(string suffix)
        {
            foreach ((string file, string s) in SIDECARS)
            {
                if (string.Equals(s, suffix, StringComparison.OrdinalIgnoreCase)) { return file; }
            }
            return null;
        }

        #endregion

        #region Prepare and migrate

        /// <summary>
        /// Creates the folder if needed, records <c>model.json</c>, copies old sidecars in where the folder has none,
        /// and (sharing on) takes newer copies from beside the model.
        /// </summary>
        /// <param name="folder">The model folder.</param>
        /// <param name="info">The model description (title, path, key, sharing and the beside-model comments path).</param>
        /// <param name="legacyCommentsPaths">Old comments sidecars to migrate from, most relevant first (beside the model,
        /// then the old Comments folder); null entries are ignored. Each one's bookmarks / sun / visibility sidecars
        /// are found next to it.</param>
        /// <returns>The comments path in the folder (<c>…\comments.json</c>), or null if the folder can't be created.</returns>
        public static string Prepare(string folder, ModelFolderInfo info, IEnumerable<string> legacyCommentsPaths)
        {
            if (string.IsNullOrWhiteSpace(folder)) { return null; }
            info ??= new ModelFolderInfo();
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Model folder could not be created ({folder}): {ex.Message}");
                return null;
            }

            info.Updated = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            WriteInfo(folder, info);

            List<string> candidates = (legacyCommentsPaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int migrated = MigrateInto(folder, candidates);
            int pulled = info.ShareBesideModel ? PullNewer(folder, info.BesideCommentsPath) : 0;
            if (migrated > 0 || pulled > 0)
            {
                Utilities.Log_Utils.Write($"Model folder {folder}: {migrated} sidecar(s) copied in from older locations, {pulled} newer shared file(s) taken from beside the model.");
            }
            return System.IO.Path.Combine(folder, COMMENTS_FILE);
        }

        /// <summary>
        /// Copies each sidecar the folder lacks from the first candidate that has it (comments also from an RvtGo
        /// <c>.rvtgo.json</c>). The originals are left untouched.
        /// </summary>
        /// <returns>How many files were copied.</returns>
        public static int MigrateInto(string folder, IReadOnlyList<string> legacyCommentsPaths)
        {
            int copied = 0;
            foreach ((string file, string suffix) in SIDECARS)
            {
                string target = System.IO.Path.Combine(folder, file);
                if (File.Exists(target)) { continue; }
                foreach (string comments in legacyCommentsPaths)
                {
                    string source = suffix == BimGoFormat.SIDECAR_SUFFIX ? comments : SidecarJson.BesideComments(comments, suffix);
                    if (suffix == BimGoFormat.SIDECAR_SUFFIX && !File.Exists(source)) { source = CommentFiles.LegacyPathFor(comments); }
                    if (TryCopy(source, target, overwrite: false))
                    {
                        copied++;
                        break;
                    }
                }
            }
            return copied;
        }

        /// <summary>
        /// Sharing: replaces folder files with newer ones beside the model.
        /// </summary>
        /// <returns>How many files were copied.</returns>
        public static int PullNewer(string folder, string besideCommentsPath)
        {
            if (string.IsNullOrWhiteSpace(besideCommentsPath)) { return 0; }
            int copied = 0;
            foreach ((string file, string suffix) in SIDECARS)
            {
                string source = suffix == BimGoFormat.SIDECAR_SUFFIX ? besideCommentsPath : SidecarJson.BesideComments(besideCommentsPath, suffix);
                string target = System.IO.Path.Combine(folder, file);
                try
                {
                    if (!File.Exists(source)) { continue; }
                    if (File.Exists(target) && File.GetLastWriteTimeUtc(source) <= File.GetLastWriteTimeUtc(target)) { continue; }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Shared sidecar check failed ({source}): {ex.Message}");
                    continue;
                }
                if (TryCopy(source, target, overwrite: true)) { copied++; }
            }
            return copied;
        }

        /// <summary>
        /// After a sidecar in a model folder was written: with sharing on, copies it beside the model too. Does nothing
        /// for any other path. Failures (read-only or cloud folders) are logged only.
        /// </summary>
        /// <param name="writtenPath">The file just written.</param>
        public static void MirrorAfterWrite(string writtenPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(writtenPath)) { return; }
                string name = System.IO.Path.GetFileName(writtenPath);
                string suffix = null;
                foreach ((string file, string s) in SIDECARS)
                {
                    if (string.Equals(file, name, StringComparison.OrdinalIgnoreCase)) { suffix = s; break; }
                }
                if (suffix == null) { return; }

                string folder = System.IO.Path.GetDirectoryName(writtenPath);
                ModelFolderInfo info = ReadInfo(folder);
                if (info == null || !info.ShareBesideModel || string.IsNullOrWhiteSpace(info.BesideCommentsPath)) { return; }

                string target = suffix == BimGoFormat.SIDECAR_SUFFIX ? info.BesideCommentsPath : SidecarJson.BesideComments(info.BesideCommentsPath, suffix);
                if (!Directory.Exists(System.IO.Path.GetDirectoryName(target))) { return; }
                TryCopy(writtenPath, target, overwrite: true);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Shared sidecar not written beside the model: {ex.Message}");
            }
        }

        #endregion

        #region model.json

        /// <summary>
        /// Reads a folder's <c>model.json</c>, or null.
        /// </summary>
        public static ModelFolderInfo ReadInfo(string folder)
        {
            try
            {
                string path = System.IO.Path.Combine(folder ?? string.Empty, INFO_FILE);
                if (!File.Exists(path)) { return null; }
                return JsonSerializer.Deserialize<ModelFolderInfo>(File.ReadAllText(path), BimGoFormat.JSON_INDENTED);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Model folder description unreadable ({folder}): {ex.Message}");
                return null;
            }
        }

        private static void WriteInfo(string folder, ModelFolderInfo info)
        {
            try
            {
                string path = System.IO.Path.Combine(folder, INFO_FILE);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(info, BimGoFormat.JSON_INDENTED));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Model folder description not written ({folder}): {ex.Message}");
            }
        }

        #endregion

        private static bool TryCopy(string source, string target, bool overwrite)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) { return false; }
                if (!overwrite && File.Exists(target)) { return false; }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                File.Copy(source, target, overwrite);
                return true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sidecar copy failed ({source} -> {target}): {ex.Message}");
                return false;
            }
        }
    }
}
