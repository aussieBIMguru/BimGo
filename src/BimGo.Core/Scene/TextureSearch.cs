using System.Text.RegularExpressions;
using BimGo.Utilities;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Which deep-scan stage matched a texture (<see cref="TextureSearch"/>), in the order the stages run.
    /// </summary>
    public enum TextureMatchStage
    {
        /// <summary>No candidate (yet).</summary>
        None = 0,

        /// <summary>The same file name, ignoring case.</summary>
        Exact = 1,

        /// <summary>The same name with another image extension (brick.jpg ↔ brick.png / .tif).</summary>
        Extension = 2,

        /// <summary>A loose name match (separators and colour-map suffixes ignored), only when unique. A proposal.</summary>
        Loose = 3
    }

    /// <summary>
    /// One missing texture's deep-scan result.
    /// </summary>
    public sealed class TextureSearchResult
    {
        /// <summary>The path as the appearance stores it (<see cref="SceneMaterial.TextureSource"/>).</summary>
        public string Raw { get; init; } = string.Empty;

        /// <summary>The stage that found the candidates, or <see cref="TextureMatchStage.None"/>.</summary>
        public TextureMatchStage Stage { get; init; }

        /// <summary>Every file that matched at that stage (several = ambiguous: the user chooses).</summary>
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        /// <summary>The file to use: the only candidate, else null (unresolved or ambiguous).</summary>
        public string Chosen { get; init; }

        /// <summary>True if several files matched and none was picked.</summary>
        public bool IsAmbiguous => Candidates.Count > 1 && Chosen == null;

        /// <summary>
        /// True if the hit is explicit enough to come pre-ticked (exact or extension stage, one candidate). Loose hits
        /// are proposals the user ticks by hand, never applied silently.
        /// </summary>
        public bool PreTicked => Chosen != null && Stage is TextureMatchStage.Exact or TextureMatchStage.Extension;
    }

    /// <summary>
    /// The image files under one folder, enumerated once and indexed by name for the deep-scan stages. Recursion is
    /// capped in depth and file count, unreadable folders are skipped, and the walk is cancellable.
    /// </summary>
    public sealed class TextureFolderIndex
    {
        /// <summary>Deepest sub-folder level walked (the folder itself is level 0).</summary>
        public const int DEFAULT_MAX_DEPTH = 8;

        /// <summary>Most image files indexed (the walk stops there and <see cref="Truncated"/> is set).</summary>
        public const int DEFAULT_MAX_FILES = 50_000;

        private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byStem = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byLoose = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, (DateTime Stamp, TextureFolderIndex Index)> CACHE = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder that was walked.</summary>
        public string Folder { get; private init; } = string.Empty;

        /// <summary>Image files indexed.</summary>
        public int FileCount { get; private set; }

        /// <summary>Folders walked.</summary>
        public int FolderCount { get; private set; }

        /// <summary>True if the depth or file cap cut the walk short.</summary>
        public bool Truncated { get; private set; }

        /// <summary>
        /// Walks a folder and indexes its images. Throws <see cref="OperationCanceledException"/> if cancelled; any
        /// other problem (missing folder, access denied) just leaves those parts out.
        /// </summary>
        /// <param name="folder">The folder to walk.</param>
        /// <param name="progress">Optional progress (detail line) and cancellation.</param>
        /// <param name="maxDepth">Deepest sub-folder level.</param>
        /// <param name="maxFiles">Most image files indexed.</param>
        public static TextureFolderIndex Build(string folder, OperationProgress progress = null,
            int maxDepth = DEFAULT_MAX_DEPTH, int maxFiles = DEFAULT_MAX_FILES)
        {
            var index = new TextureFolderIndex { Folder = folder ?? string.Empty };
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { return index; }

            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((folder, 0));
            while (pending.Count > 0)
            {
                progress?.ThrowIfCancelled();
                (string current, int depth) = pending.Pop();
                index.FolderCount++;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(current))
                    {
                        if (!TextureSearch.IsImageFile(file)) { continue; }
                        if (index.FileCount >= maxFiles)
                        {
                            index.Truncated = true;
                            return index;
                        }
                        index.Add(file);
                        if ((index.FileCount & 255) == 0)
                        {
                            progress?.ThrowIfCancelled();
                            progress?.Detail($"{index.FileCount:N0} images in {index.FolderCount:N0} folders");
                        }
                    }

                    if (depth >= maxDepth)
                    {
                        if (Directory.EnumerateDirectories(current).Any()) { index.Truncated = true; }
                        continue;
                    }
                    foreach (string sub in Directory.EnumerateDirectories(current))
                    {
                        pending.Push((sub, depth + 1));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // Unreadable folder: skip it
                }
            }
            progress?.Detail($"{index.FileCount:N0} images in {index.FolderCount:N0} folders");
            return index;
        }

        /// <summary>
        /// A folder's index from a cache kept for the process, rebuilt when the folder's last-write time changes (used
        /// for the remembered search folders, which every extraction consults). Never throws; null if the folder is
        /// missing.
        /// </summary>
        public static TextureFolderIndex GetCached(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { return null; }
                DateTime stamp = Directory.GetLastWriteTimeUtc(folder);
                lock (CACHE)
                {
                    if (CACHE.TryGetValue(folder, out (DateTime Stamp, TextureFolderIndex Index) cached) && cached.Stamp == stamp) { return cached.Index; }
                }
                TextureFolderIndex index = Build(folder);
                lock (CACHE)
                {
                    if (CACHE.Count > 32) { CACHE.Clear(); }
                    CACHE[folder] = (stamp, index);
                }
                return index;
            }
            catch (Exception ex)
            {
                Log_Utils.Write($"Texture search folder {folder} could not be indexed: {ex.Message}");
                return null;
            }
        }

        private void Add(string file)
        {
            string name = TextureSearch.FileNameOf(file);
            if (name == null) { return; }
            FileCount++;
            AddTo(_byName, name, file);
            AddTo(_byStem, TextureSearch.StemOf(name), file);
            if (!TextureSearch.IsAuxiliaryMap(name)) { AddTo(_byLoose, TextureSearch.LooseStem(name), file); }

            static void AddTo(Dictionary<string, List<string>> map, string key, string file)
            {
                if (string.IsNullOrEmpty(key)) { return; }
                if (!map.TryGetValue(key, out List<string> list)) { map[key] = list = new List<string>(1); }
                list.Add(file);
            }
        }

        internal IReadOnlyList<string> ByName(string fileName) =>
            _byName.TryGetValue(fileName, out List<string> list) ? list : Array.Empty<string>();

        internal IReadOnlyList<string> ByStem(string stem) =>
            _byStem.TryGetValue(stem, out List<string> list) ? list : Array.Empty<string>();

        internal IReadOnlyList<string> ByLoose(string looseStem) =>
            _byLoose.TryGetValue(looseStem, out List<string> list) ? list : Array.Empty<string>();
    }

    /// <summary>
    /// The deep scan's file-name matching, shared by the Revit "Review textures…" window, the remembered search
    /// folders at extraction, and the app's Textures panel. Pure file-name logic: no images are opened.
    /// <para>Stages run in order and each only sees what the earlier ones left without candidates:
    /// <see cref="TextureMatchStage.Exact"/> → <see cref="TextureMatchStage.Extension"/> →
    /// <see cref="TextureMatchStage.Loose"/>. Several candidates at a stage are all reported and none is picked.
    /// Bump, cutout, reflection and other non-colour maps (by suffix) are never offered by the extension or loose
    /// stages.</para>
    /// </summary>
    public static class TextureSearch
    {
        /// <summary>The image extensions indexed (lower case, with the dot).</summary>
        public static readonly IReadOnlyList<string> IMAGE_EXTENSIONS = new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif" };

        private static readonly HashSet<string> EXTENSIONS = new(IMAGE_EXTENSIONS, StringComparer.OrdinalIgnoreCase);

        /// <summary>A trailing colour-map suffix ("_color", "-diffuse", " albedo"…) ignored by the loose stage.</summary>
        private static readonly Regex COLOUR_SUFFIX = new(
            @"[_\-. ]+(colou?r|diffuse|diff|albedo|col|base[_\-. ]?colou?r)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A trailing non-colour map suffix: these files are never offered for a colour slot.</summary>
        private static readonly Regex AUXILIARY_SUFFIX = new(
            @"[_\-. ]+(bump|normal|nrm|norm|normalgl|normaldx|cutout|opacity|alpha|mask|refl|reflect|reflection|rough|roughness|gloss|glossiness|spec|specular|disp|displacement|height|ao|ambientocclusion|metalness|metallic|emissive)\d*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True if a path has one of the <see cref="IMAGE_EXTENSIONS"/>.
        /// </summary>
        public static bool IsImageFile(string path)
        {
            string name = FileNameOf(path);
            if (name == null) { return false; }
            int dot = name.LastIndexOf('.');
            return dot > 0 && EXTENSIONS.Contains(name.Substring(dot));
        }

        /// <summary>
        /// The file name of a path written with either slash (asset paths are Windows paths whatever the platform), or
        /// null.
        /// </summary>
        public static string FileNameOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) { return null; }
            string trimmed = path.Trim().Trim('"').TrimEnd('\\', '/');
            int slash = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
            string name = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// The distinct file names of an asset path's '|' alternatives ("1/Mats/x.jpg|2/Mats/x.jpg" → "x.jpg").
        /// </summary>
        public static IReadOnlyList<string> FileNamesOf(string raw)
        {
            var names = new List<string>(1);
            if (string.IsNullOrWhiteSpace(raw)) { return names; }
            foreach (string part in raw.Split('|'))
            {
                string name = FileNameOf(part);
                if (name != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) { names.Add(name); }
            }
            return names;
        }

        /// <summary>
        /// A file name without its extension.
        /// </summary>
        public static string StemOf(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) { return string.Empty; }
            int dot = fileName.LastIndexOf('.');
            return dot > 0 ? fileName.Substring(0, dot) : fileName;
        }

        /// <summary>
        /// The loose-stage key of a file name: lower case, extension and one trailing colour-map suffix removed, then
        /// separators (_ - . space) removed. "BG_Carpet_Plain1_Color.JPG" and "bg-carpet-plain-1.jpeg" both give
        /// "bgcarpetplain1".
        /// </summary>
        public static string LooseStem(string fileName)
        {
            string stem = StemOf(fileName ?? string.Empty).Trim();
            stem = COLOUR_SUFFIX.Replace(stem, string.Empty);
            var builder = new System.Text.StringBuilder(stem.Length);
            foreach (char c in stem)
            {
                if (c is '_' or '-' or '.' or ' ') { continue; }
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        /// <summary>
        /// True if a file name looks like a non-colour map (bump, normal, cutout, reflection, roughness…) by suffix.
        /// </summary>
        public static bool IsAuxiliaryMap(string fileName) => AUXILIARY_SUFFIX.IsMatch(StemOf(fileName ?? string.Empty));

        /// <summary>
        /// Runs one stage over the raw paths given (the caller passes only those earlier stages left without
        /// candidates). Every raw path gets a result; <see cref="TextureMatchStage.None"/> when this stage found
        /// nothing. Blank paths are skipped (no texture is not a missing texture).
        /// </summary>
        /// <param name="index">The folder index.</param>
        /// <param name="rawPaths">Asset paths (duplicates are reported once).</param>
        /// <param name="stage">The stage to run.</param>
        /// <param name="progress">Optional cancellation.</param>
        public static IReadOnlyList<TextureSearchResult> RunStage(TextureFolderIndex index, IEnumerable<string> rawPaths,
            TextureMatchStage stage, OperationProgress progress = null)
        {
            var results = new List<TextureSearchResult>();
            if (index == null || rawPaths == null) { return results; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in rawPaths)
            {
                if (string.IsNullOrWhiteSpace(raw) || !seen.Add(raw)) { continue; }
                progress?.ThrowIfCancelled();
                IReadOnlyList<string> candidates = Candidates(index, raw, stage);
                bool unique = candidates.Count == 1;

                // Loose hits are only proposed when exactly one file matches; several loose hits are no hit at all
                if (stage == TextureMatchStage.Loose && !unique) { candidates = Array.Empty<string>(); }

                results.Add(new TextureSearchResult
                {
                    Raw = raw,
                    Stage = candidates.Count > 0 ? stage : TextureMatchStage.None,
                    Candidates = candidates,
                    Chosen = candidates.Count == 1 ? candidates[0] : null
                });
            }
            return results;
        }

        /// <summary>
        /// Runs every stage up to <paramref name="lastStage"/> in order, each on what the earlier ones left without
        /// candidates (the remembered search folders use <see cref="TextureMatchStage.Exact"/> only). One result per
        /// distinct raw path.
        /// </summary>
        public static IReadOnlyList<TextureSearchResult> RunAll(TextureFolderIndex index, IEnumerable<string> rawPaths,
            TextureMatchStage lastStage = TextureMatchStage.Loose, OperationProgress progress = null)
        {
            var final = new Dictionary<string, TextureSearchResult>(StringComparer.OrdinalIgnoreCase);
            List<string> remaining = (rawPaths ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> order = remaining.ToList();

            for (TextureMatchStage stage = TextureMatchStage.Exact; stage <= lastStage && remaining.Count > 0; stage++)
            {
                IReadOnlyList<TextureSearchResult> results = RunStage(index, remaining, stage, progress);
                remaining = new List<string>();
                foreach (TextureSearchResult result in results)
                {
                    final[result.Raw] = result;
                    if (result.Stage == TextureMatchStage.None) { remaining.Add(result.Raw); }
                }
            }
            return order.Select(raw => final.TryGetValue(raw, out TextureSearchResult r) ? r : new TextureSearchResult { Raw = raw }).ToList();
        }

        private static IReadOnlyList<string> Candidates(TextureFolderIndex index, string raw, TextureMatchStage stage)
        {
            var found = new List<string>();
            foreach (string name in FileNamesOf(raw))
            {
                IEnumerable<string> hits = stage switch
                {
                    TextureMatchStage.Exact => index.ByName(name),
                    TextureMatchStage.Extension => index.ByStem(StemOf(name)).Where(f => !IsAuxiliaryMap(FileNameOf(f))),
                    TextureMatchStage.Loose => index.ByLoose(LooseStem(name)),
                    _ => Enumerable.Empty<string>()
                };
                foreach (string hit in hits)
                {
                    if (!found.Contains(hit, StringComparer.OrdinalIgnoreCase)) { found.Add(hit); }
                }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }
    }
}
