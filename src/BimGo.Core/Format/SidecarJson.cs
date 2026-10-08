using System.Text.Json;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Shared read / atomic write for the small JSON sidecars of a Revit model (bookmarks, sun, visibility), kept in
    /// its BimGo model folder (<see cref="ModelFolders"/>) or, for older snapshots, beside the model. Same JSON
    /// settings as the entries inside a .bimgo. Never throws.
    /// </summary>
    internal static class SidecarJson
    {
        /// <summary>
        /// The sidecar with <paramref name="suffix"/> that goes with a comments sidecar (same folder and base name), or null.
        /// </summary>
        public static string BesideComments(string commentsSidecarPath, string suffix)
        {
            if (string.IsNullOrWhiteSpace(commentsSidecarPath)) { return null; }

            // In a per-model folder the files have fixed names (comments.json → bookmarks.json, sun.json…)
            string modelFolder = ModelFolders.FolderOf(commentsSidecarPath);
            string folderFile = modelFolder == null ? null : ModelFolders.FileForSuffix(suffix);
            if (folderFile != null) { return Path.Combine(modelFolder, folderFile); }

            string basePath = commentsSidecarPath.EndsWith(BimGoFormat.SIDECAR_SUFFIX, StringComparison.OrdinalIgnoreCase)
                ? commentsSidecarPath[..^BimGoFormat.SIDECAR_SUFFIX.Length]
                : Path.Combine(Path.GetDirectoryName(commentsSidecarPath) ?? string.Empty, Path.GetFileNameWithoutExtension(commentsSidecarPath));
            return basePath + suffix;
        }

        /// <summary>
        /// Reads a sidecar.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="what">What it holds, for messages ("Bookmarks").</param>
        /// <param name="error">A reason on failure (null when the file simply doesn't exist).</param>
        /// <returns>The value, or null if the file doesn't exist or can't be read.</returns>
        public static T Read<T>(string path, string what, out string error) where T : class
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), BimGoFormat.JSON_INDENTED);
            }
            catch (Exception ex)
            {
                error = $"{what} could not be read: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return null;
            }
        }

        /// <summary>
        /// Writes a sidecar atomically (temp file, then replace).
        /// </summary>
        /// <returns>True on success.</returns>
        public static bool Write<T>(string path, T value, string what, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(value, BimGoFormat.JSON_INDENTED));
                File.Move(temp, path, overwrite: true);
                ModelFolders.MirrorAfterWrite(path);
                return true;
            }
            catch (Exception ex)
            {
                error = $"{what} could not be saved: {ex.Message}";
                Utilities.Log_Utils.Write(error);
                return false;
            }
        }
    }

    /// <summary>
    /// The sun sidecar beside a Revit model (&lt;model&gt;.bimgo-sun.json, next to the comments sidecar). Never throws.
    /// </summary>
    public static class SunFiles
    {
        /// <summary>The sun sidecar for a comments sidecar path, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.SUN_SIDECAR_SUFFIX);

        /// <summary>Reads the sidecar (null if missing or unreadable).</summary>
        public static SunSettings Read(string path, out string error) => SidecarJson.Read<SunSettings>(path, "Sun settings", out error)?.Clean();

        /// <summary>Writes the sidecar.</summary>
        public static bool Write(string path, SunSettings settings, out string error) => SidecarJson.Write(path, settings, "Sun settings", out error);
    }
}
