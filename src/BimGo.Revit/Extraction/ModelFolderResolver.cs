using BimGo.Format;
using BimGo.Scene;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Finds a Revit model's BimGo folder (<see cref="ModelFolders"/>): where a live session keeps its comments,
    /// bookmarks, sun, visibility and texture choices. Read-only on the model.
    /// </summary>
    internal static class ModelFolderResolver
    {
        /// <summary>
        /// The key source, most stable first: "cloud:&lt;project&gt;/&lt;model GUID&gt;", else "central:&lt;central path&gt;"
        /// for a workshared model, else "local:&lt;path&gt;", else "unsaved:&lt;title&gt;".
        /// </summary>
        public static string KeySource(Document doc)
        {
            if (doc == null) { return "unsaved:"; }

            try
            {
                if (doc.IsModelInCloud)
                {
                    ModelPath cloud = doc.GetCloudModelPath();
                    return $"cloud:{cloud.GetProjectGUID():D}/{cloud.GetModelGUID():D}";
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Cloud model id unavailable, using the path: {ex.Message}");
            }

            try
            {
                if (doc.IsWorkshared)
                {
                    ModelPath central = doc.GetWorksharingCentralModelPath();
                    string centralPath = central == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                    if (!string.IsNullOrWhiteSpace(centralPath)) { return "central:" + centralPath; }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Central model path unavailable, using the local path: {ex.Message}");
            }

            string path = SafePathName(doc);
            return string.IsNullOrWhiteSpace(path) ? "unsaved:" + SafeTitle(doc) : "local:" + path;
        }

        /// <summary>
        /// The model's folder (not created, nothing migrated): for reading, e.g. the Options window's texture summary.
        /// </summary>
        public static string FolderOf(Document doc) => ModelFolders.FolderFor(SafeTitle(doc), KeySource(doc));

        /// <summary>
        /// Prepares the model's folder (model.json, one-time copy of older sidecars, shared files when sharing is on)
        /// and returns the comments path in it. Falls back to the old per-title file in
        /// <c>%LocalAppData%\BimGo\Comments</c> if the folder can't be created.
        /// </summary>
        public static string PrepareCommentsPath(Document doc, LaunchSettings settings)
        {
            string title = SafeTitle(doc);
            string key = KeySource(doc);
            string folder = ModelFolders.FolderFor(title, key);
            string beside = BesideModelCommentsPath(doc);
            string oldFallback = Path.Combine(Utilities.Log_Utils.Folder, "Comments", MakeSafeFileName(title) + BimGoFormat.SIDECAR_SUFFIX);

            var info = new ModelFolderInfo
            {
                Title = title,
                Path = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key,
                Key = ModelFolders.NormaliseKey(key),
                ShareBesideModel = settings?.SidecarsBesideModel == true && beside != null,
                BesideCommentsPath = beside ?? string.Empty
            };
            return ModelFolders.Prepare(folder, info, new[] { beside, oldFallback }) ?? oldFallback;
        }

        /// <summary>
        /// The comments sidecar beside the model (pre-build-B location), or null for cloud / unsaved models or a
        /// folder that doesn't exist.
        /// </summary>
        public static string BesideModelCommentsPath(Document doc)
        {
            try
            {
                string modelPath = SafePathName(doc);
                if (doc.IsModelInCloud || string.IsNullOrEmpty(modelPath) || !Path.IsPathRooted(modelPath)) { return null; }
                string folder = Path.GetDirectoryName(modelPath);
                if (!Directory.Exists(folder)) { return null; }
                return Path.Combine(folder, Path.GetFileNameWithoutExtension(modelPath) + BimGoFormat.SIDECAR_SUFFIX);
            }
            catch
            {
                return null;
            }
        }

        private static string SafePathName(Document doc)
        {
            try { return doc?.PathName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string SafeTitle(Document doc)
        {
            try { return doc?.Title ?? "Model"; }
            catch { return "Model"; }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) { name = name.Replace(c, '_'); }
            return name;
        }
    }
}
