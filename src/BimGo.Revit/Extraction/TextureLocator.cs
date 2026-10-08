using System.Text.RegularExpressions;
using BimGo.Scene;
using Microsoft.Win32;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Where a texture path was found, in the order the stages are tried.
    /// </summary>
    internal enum TextureFound
    {
        /// <summary>No stage found the file.</summary>
        Missing = 0,

        /// <summary>The path as written (absolute, environment variables expanded).</summary>
        Absolute,

        /// <summary>Relative to an Autodesk Material Library texture root (found on this machine, never assumed).</summary>
        Library,

        /// <summary>In one of Revit's "Additional render appearance paths" (Revit.ini).</summary>
        ExtraPath,

        /// <summary>Beside the Revit model.</summary>
        DocumentFolder,

        /// <summary>By exact file name in one of the remembered texture search folders (<see cref="LaunchSettings.TextureSearchFolders"/>).</summary>
        SearchFolder,

        /// <summary>A per-model override image (<see cref="TextureOverrideSet"/>), not looked up at all.</summary>
        Override
    }

    /// <summary>
    /// The result of resolving one appearance-asset bitmap path.
    /// </summary>
    /// <param name="Raw">The path exactly as the asset stores it (may hold several alternatives separated by '|').</param>
    /// <param name="Path">The file found on disk, or null.</param>
    /// <param name="Found">Which stage found it.</param>
    /// <param name="AutodeskLibrary">True if the path's syntax (or where it was found) marks it as an Autodesk library
    /// texture rather than a user's own image.</param>
    internal readonly record struct TextureLookup(string Raw, string Path, TextureFound Found, bool AutodeskLibrary);

    /// <summary>
    /// Finds the image files that Revit appearance assets point at. Nothing about the machine is assumed: the Autodesk
    /// Material Library roots are probed (standard folders, then the registry) and Revit's additional render appearance
    /// paths are read from Revit.ini. Each discovery step is recorded in <see cref="Notes"/> for diagnostics.
    /// <para>Resolution stages, first hit wins: absolute path → library roots → additional paths → model folder →
    /// file name in the library's numbered "Mats" folders → exact file name in the remembered search folders (indexed
    /// once per folder and cached while the folder is unchanged). Results are cached by raw path.</para>
    /// </summary>
    internal sealed class TextureLocator
    {
        #region Constants

        /// <summary>
        /// Path syntax used by Autodesk-supplied library textures: "1\Mats\…", "…\Autodesk Shared\Materials\…" or
        /// "…\Materials\Textures\…".
        /// </summary>
        private static readonly Regex AUTODESK_SYNTAX = new(
            @"(^|[\\/])\d+[\\/]Mats[\\/]|Autodesk Shared[\\/]Materials|[\\/]Materials[\\/]Textures[\\/]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Registry keys (under SOFTWARE) that may name texture library folders.</summary>
        private static readonly string[] REGISTRY_KEYS =
        {
            @"SOFTWARE\Autodesk\ADSKTextureLibrary",
            @"SOFTWARE\Autodesk\ADSKTextureLibraryNew",
            @"SOFTWARE\Autodesk\ADSKAssetLibrary",
            @"SOFTWARE\Autodesk\ADSKMaterialLibrary"
        };

        #endregion

        #region Fields and properties

        private readonly List<string> _libraryRoots = new();
        private readonly List<string> _extraPaths = new();
        private readonly List<string> _searchFolders = new();
        private List<TextureFolderIndex> _searchIndexes;
        private readonly List<string> _notes = new();
        private readonly Dictionary<string, TextureLookup> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Autodesk Material Library texture roots that exist on this machine.</summary>
        public IReadOnlyList<string> LibraryRoots => _libraryRoots;

        /// <summary>Revit's additional render appearance paths that exist on this machine.</summary>
        public IReadOnlyList<string> ExtraPaths => _extraPaths;

        /// <summary>The remembered texture search folders that exist on this machine.</summary>
        public IReadOnlyList<string> SearchFolders => _searchFolders;

        /// <summary>What discovery checked and found (one line each).</summary>
        public IReadOnlyList<string> Notes => _notes;

        /// <summary>True if any Autodesk Material Library texture root was found.</summary>
        public bool HasLibrary => _libraryRoots.Count > 0;

        #endregion

        #region Discovery

        /// <summary>
        /// Probes this machine for texture locations. Never throws.
        /// </summary>
        /// <param name="revitVersion">The Revit year, e.g. "2025" (finds that year's Revit.ini).</param>
        public static TextureLocator Discover(string revitVersion)
        {
            var locator = new TextureLocator();
            locator.ProbeStandardRoots();
            locator.ProbeRegistry();
            locator.ProbeRevitIni(revitVersion);
            return locator;
        }

        /// <summary>
        /// The Common Files locations the library installs to.
        /// </summary>
        private void ProbeStandardRoots()
        {
            var commonFolders = new List<string>();
            foreach (string variable in new[] { "CommonProgramFiles(x86)", "CommonProgramFiles", "CommonProgramW6432" })
            {
                string value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(value)) { commonFolders.Add(value); }
            }

            foreach (string common in commonFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string sub in new[] { @"Autodesk Shared\Materials\Textures", @"Autodesk Shared\Materials" })
                {
                    string candidate = SafeCombine(common, sub);
                    bool exists = candidate != null && Directory.Exists(candidate);
                    _notes.Add($"Standard library folder {(exists ? "FOUND" : "absent")}: {candidate}");
                    if (exists) { AddUnique(_libraryRoots, candidate); }
                }
            }
        }

        /// <summary>
        /// Autodesk texture / asset library registry keys (both registry views, machine and user), two levels deep.
        /// Any string value naming an existing folder becomes a library root.
        /// </summary>
        private void ProbeRegistry()
        {
            if (!OperatingSystem.IsWindows()) { return; }
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                        foreach (string keyPath in REGISTRY_KEYS)
                        {
                            using RegistryKey key = baseKey.OpenSubKey(keyPath);
                            if (key == null) { continue; }
                            ReadRegistryKey(key, $"{hive}/{view}\\{keyPath}", depth: 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        _notes.Add($"Registry {hive}/{view} unreadable: {ex.Message}");
                    }
                }
            }
        }

        private void ReadRegistryKey(RegistryKey key, string label, int depth)
        {
            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is not string text || string.IsNullOrWhiteSpace(text)) { continue; }
                _notes.Add($"Registry {label} [{(valueName.Length == 0 ? "(default)" : valueName)}] = {text}");
                foreach (string part in text.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string folder = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
                    if (folder.Length > 2 && Directory.Exists(folder)) { AddUnique(_libraryRoots, folder.TrimEnd('\\', '/')); }
                }
            }
            if (depth >= 2) { return; }
            foreach (string subName in key.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey sub = key.OpenSubKey(subName);
                    if (sub != null) { ReadRegistryKey(sub, label + "\\" + subName, depth + 1); }
                }
                catch (Exception ex)
                {
                    _notes.Add($"Registry {label}\\{subName} unreadable: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Revit.ini lines that mention appearance or texture paths (logged verbatim). Values naming existing folders
        /// become additional paths.
        /// </summary>
        private void ProbeRevitIni(string revitVersion)
        {
            string iniPath = SafeCombine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                $@"Autodesk\Revit\Autodesk Revit {revitVersion}\Revit.ini");
            if (iniPath == null || !File.Exists(iniPath))
            {
                _notes.Add($"Revit.ini not found: {iniPath}");
                return;
            }

            try
            {
                int matched = 0;
                foreach (string line in File.ReadAllLines(iniPath))
                {
                    int equals = line.IndexOf('=');
                    if (equals <= 0) { continue; }
                    string name = line.Substring(0, equals);
                    if (name.IndexOf("Appearance", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                    matched++;
                    _notes.Add($"Revit.ini {line.Trim()}");
                    foreach (string part in line.Substring(equals + 1).Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string folder = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
                        if (folder.Length > 2 && Directory.Exists(folder)) { AddUnique(_extraPaths, folder.TrimEnd('\\', '/')); }
                    }
                }
                if (matched == 0) { _notes.Add($"Revit.ini has no appearance / texture path entries: {iniPath}"); }
            }
            catch (Exception ex)
            {
                _notes.Add($"Revit.ini unreadable ({iniPath}): {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the remembered texture search folders (missing ones are noted and skipped). Each is indexed the first
        /// time a texture isn't found by the earlier stages.
        /// </summary>
        public void SetSearchFolders(IEnumerable<string> folders)
        {
            _searchFolders.Clear();
            _searchIndexes = null;
            _cache.Clear();
            foreach (string folder in folders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder)) { continue; }
                bool exists;
                try { exists = Directory.Exists(folder); }
                catch { exists = false; }
                _notes.Add($"Texture search folder {(exists ? "FOUND" : "absent")}: {folder}");
                if (exists) { AddUnique(_searchFolders, folder.TrimEnd('\\', '/')); }
            }
        }

        #endregion

        #region Resolution

        /// <summary>
        /// Finds the file an asset's bitmap path refers to. Never throws; cached by raw path and model folder.
        /// </summary>
        /// <param name="raw">The asset's path text (alternatives separated by '|').</param>
        /// <param name="documentFolder">The Revit model's folder, or null.</param>
        public TextureLookup Resolve(string raw, string documentFolder)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return new TextureLookup(raw ?? string.Empty, null, TextureFound.Missing, false); }
            string cacheKey = raw + "\n" + (documentFolder ?? string.Empty);
            if (_cache.TryGetValue(cacheKey, out TextureLookup cached)) { return cached; }

            TextureLookup result = ResolveUncached(raw, documentFolder);
            _cache[cacheKey] = result;
            return result;
        }

        private TextureLookup ResolveUncached(string raw, string documentFolder)
        {
            bool syntax = LooksAutodeskLibrary(raw);
            List<string> alternatives = SplitAlternatives(raw).ToList();

            // 1. As written
            foreach (string alt in alternatives)
            {
                if (IsRooted(alt) && File.Exists(alt)) { return Found(alt, TextureFound.Absolute); }
            }

            foreach (string alt in alternatives)
            {
                string relative = IsRooted(alt) ? null : alt.TrimStart('\\');
                string fileName = SafeFileName(alt);

                // 2. Library roots (the relative path, e.g. "1\Mats\Brick.jpg")
                if (relative != null)
                {
                    foreach (string root in _libraryRoots)
                    {
                        string candidate = SafeCombine(root, relative);
                        if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.Library); }
                    }
                }

                // 3. Additional render appearance paths (relative path, then file name; Revit searches these flat)
                foreach (string extra in _extraPaths)
                {
                    string candidate = relative != null ? SafeCombine(extra, relative) : null;
                    if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.ExtraPath); }
                    candidate = fileName != null ? SafeCombine(extra, fileName) : null;
                    if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.ExtraPath); }
                }

                // 4. Beside the model
                if (!string.IsNullOrEmpty(documentFolder))
                {
                    string candidate = relative != null ? SafeCombine(documentFolder, relative) : null;
                    if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.DocumentFolder); }
                    candidate = fileName != null ? SafeCombine(documentFolder, fileName) : null;
                    if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.DocumentFolder); }
                }

                // 5. File name in the library's numbered Mats folders (absolute paths from another machine)
                if (fileName != null)
                {
                    foreach (string root in _libraryRoots)
                    {
                        for (int n = 1; n <= 4; n++)
                        {
                            string candidate = SafeCombine(root, $@"{n}\Mats\{fileName}");
                            if (candidate != null && File.Exists(candidate)) { return Found(candidate, TextureFound.Library); }
                        }
                    }
                }
            }

            // 6. Exact file name in the remembered search folders (a unique hit only: two files of the same name in
            //    a folder are left for the user to choose in "Review textures…")
            if (_searchFolders.Count > 0)
            {
                _searchIndexes ??= _searchFolders.Select(TextureFolderIndex.GetCached).Where(i => i != null).ToList();
                foreach (TextureFolderIndex index in _searchIndexes)
                {
                    TextureSearchResult hit = TextureSearch.RunStage(index, new[] { raw }, TextureMatchStage.Exact).FirstOrDefault();
                    if (hit?.Chosen != null) { return Found(hit.Chosen, TextureFound.SearchFolder); }
                    if (hit?.IsAmbiguous == true) { _notes.Add($"Ambiguous in {index.Folder}: {raw} ({hit.Candidates.Count} files)"); }
                }
            }

            return new TextureLookup(raw, null, TextureFound.Missing, syntax);

            TextureLookup Found(string path, TextureFound stage) =>
                new(raw, path, stage, syntax || stage == TextureFound.Library);
        }

        /// <summary>
        /// True if the path's syntax marks it as an Autodesk-supplied library texture.
        /// </summary>
        public static bool LooksAutodeskLibrary(string raw) => !string.IsNullOrEmpty(raw) && AUTODESK_SYNTAX.IsMatch(raw);

        /// <summary>
        /// The '|'-separated alternatives of an asset path: trimmed, unquoted, environment variables expanded and
        /// forward slashes turned into backslashes.
        /// </summary>
        public static IEnumerable<string> SplitAlternatives(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { yield break; }
            foreach (string part in raw.Split('|'))
            {
                string alt = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"')).Replace('/', '\\');
                if (alt.Length > 0) { yield return alt; }
            }
        }

        #endregion

        #region Helpers

        private static bool IsRooted(string path)
        {
            try { return Path.IsPathFullyQualified(path); }
            catch { return false; }
        }

        private static string SafeCombine(string a, string b)
        {
            try { return Path.GetFullPath(Path.Combine(a, b)); }
            catch { return null; }
        }

        private static string SafeFileName(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }

        private static void AddUnique(List<string> list, string folder)
        {
            if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase)) { list.Add(folder); }
        }

        #endregion
    }
}
