using System.Text.Json;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// What the user decided for one material's texture: exactly one of an image, a proxy or "plain colour".
    /// </summary>
    public sealed class TextureOverride
    {
        /// <summary>An absolute image path: a user pick or an accepted deep-scan hit.</summary>
        public string Image { get; set; }

        /// <summary>A CC0 proxy keyword (<see cref="ProxyCatalog"/>).</summary>
        public string Proxy { get; set; }

        /// <summary>True: deliberately plain (no texture, no proxy; the shading colour).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ColourOnly { get; set; }

        /// <summary>The material name when saved (readability, and the fallback key for files without a UniqueId).</summary>
        public string Name { get; set; }

        /// <summary>An image override.</summary>
        public static TextureOverride ForImage(string path, string name = null) => new() { Image = path, Name = name };

        /// <summary>A proxy override.</summary>
        public static TextureOverride ForProxy(string keyword, string name = null) => new() { Proxy = ProxyCatalog.Normalise(keyword), Name = name };

        /// <summary>A plain-colour override.</summary>
        public static TextureOverride ForColourOnly(string name = null) => new() { ColourOnly = true, Name = name };

        /// <summary>True if the override says something (an image, a proxy or plain colour).</summary>
        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Image) || !string.IsNullOrWhiteSpace(Proxy) || ColourOnly;
    }

    /// <summary>
    /// The texture choices for one Revit host model (and the links extracted with it), remembered per machine in
    /// the model's BimGo folder (<c>texture-overrides.json</c>, UX round build B; older files in
    /// <c>%AppData%\BimGo\texture-overrides\&lt;host model key&gt;.json</c> are copied in once). Written by the Revit "Review textures…"
    /// window and by the app's Textures panel in a live session; read by every extraction of that model, so a fix made
    /// once is used on every Go / F5. Nothing is ever written into the Revit model.
    /// <para>Entries are keyed by document ("host", or a link's model key) and then by material UniqueId, with the
    /// material name ("name:…") as a fallback for snapshots that carry no UniqueId.</para>
    /// </summary>
    public sealed class TextureOverrideSet
    {
        /// <summary>The document key of the host model.</summary>
        public const string HOST = "host";

        /// <summary>Most materials remembered per document (keeps a damaged or runaway file bounded).</summary>
        public const int MAX_PER_DOCUMENT = 5000;

        private static readonly JsonSerializerOptions JSON = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>File layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The host model's title when last saved (readability only).</summary>
        public string ModelTitle { get; set; }

        /// <summary>Document key → material key → override.</summary>
        public Dictionary<string, Dictionary<string, TextureOverride>> Documents { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The host model key this set belongs to (not serialised).</summary>
        [JsonIgnore]
        public string HostKey { get; private set; } = string.Empty;

        /// <summary>The folder the file lives in (not serialised; tests use their own).</summary>
        [JsonIgnore]
        public string Folder { get; private set; }

        /// <summary>
        /// The exact file (not serialised): <c>texture-overrides.json</c> in the model's BimGo folder when loaded with
        /// <see cref="LoadFromModelFolder"/>; null for the older per-key file (<see cref="PathFor"/>).
        /// </summary>
        [JsonIgnore]
        public string FilePath { get; private set; }

        /// <summary>True if there are no overrides.</summary>
        [JsonIgnore]
        public bool IsEmpty => Documents.Values.All(d => d.Count == 0);

        /// <summary>
        /// The default folder: <c>%AppData%\BimGo\texture-overrides</c>.
        /// </summary>
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BimGo", "texture-overrides");

        /// <summary>
        /// The file path for a host model key (unsafe file-name characters replaced).
        /// </summary>
        public static string PathFor(string hostKey, string folder = null)
        {
            string key = string.IsNullOrWhiteSpace(hostKey) ? "unknown" : hostKey.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(key.Length);
            foreach (char c in key)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' || c == '\\' || c == '/' ? '_' : c);
            }
            string name = builder.ToString();
            if (name.Length > 120) { name = name.Substring(0, 120); }
            return Path.Combine(folder ?? DefaultFolder, name + ".json");
        }

        /// <summary>
        /// Loads the overrides for a host model, or an empty set if there are none or the file is unreadable. Never
        /// throws.
        /// </summary>
        /// <param name="hostKey">The host model key (<c>ProjectInformation.UniqueId</c>).</param>
        /// <param name="folder">The folder, or null for <see cref="DefaultFolder"/>.</param>
        public static TextureOverrideSet Load(string hostKey, string folder = null)
        {
            if (string.IsNullOrWhiteSpace(hostKey)) { return new TextureOverrideSet { HostKey = string.Empty, Folder = folder }; }
            return LoadFile(PathFor(hostKey, folder), hostKey, folder, null);
        }

        /// <summary>
        /// Loads the overrides kept in a model's BimGo folder (<c>texture-overrides.json</c>, see
        /// <see cref="Format.ModelFolders"/>). The first time, the older per-key file
        /// (<c>%AppData%\BimGo\texture-overrides\&lt;key&gt;.json</c>) is copied in and left as a backup. With no
        /// folder (an older snapshot), falls back to the per-key file. Never throws.
        /// </summary>
        /// <param name="modelFolder">The model's BimGo folder, or null.</param>
        /// <param name="legacyHostKey">The older key (<c>ProjectInformation.UniqueId</c>), for the one-time copy.</param>
        /// <param name="legacyFolder">The older files' folder, or null for <see cref="DefaultFolder"/> (tests use their own).</param>
        public static TextureOverrideSet LoadFromModelFolder(string modelFolder, string legacyHostKey, string legacyFolder = null)
        {
            if (string.IsNullOrWhiteSpace(modelFolder)) { return Load(legacyHostKey, legacyFolder); }
            string path = Path.Combine(modelFolder, Format.ModelFolders.TEXTURE_OVERRIDES_FILE);
            try
            {
                string legacy = string.IsNullOrWhiteSpace(legacyHostKey) ? null : PathFor(legacyHostKey, legacyFolder);
                if (!File.Exists(path) && legacy != null && File.Exists(legacy))
                {
                    Directory.CreateDirectory(modelFolder);
                    File.Copy(legacy, path, overwrite: false);
                    Utilities.Log_Utils.Write($"Texture overrides copied into the model folder: {legacy} -> {path}");
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture overrides not migrated ({path}): {ex.Message}");
            }
            return LoadFile(path, legacyHostKey ?? string.Empty, null, path);
        }

        private static TextureOverrideSet LoadFile(string path, string hostKey, string folder, string filePath)
        {
            var empty = new TextureOverrideSet { HostKey = hostKey ?? string.Empty, Folder = folder, FilePath = filePath };
            try
            {
                if (!File.Exists(path)) { return empty; }
                TextureOverrideSet loaded = JsonSerializer.Deserialize<TextureOverrideSet>(File.ReadAllText(path), JSON);
                if (loaded == null) { return empty; }
                loaded.HostKey = hostKey ?? string.Empty;
                loaded.Folder = folder;
                loaded.FilePath = filePath;
                loaded.Sanitise();
                return loaded;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture overrides unreadable ({path}), ignored: {ex.Message}");
                return empty;
            }
        }

        /// <summary>
        /// Saves the set (deletes the file when empty). Never throws; false on failure.
        /// </summary>
        public bool Save()
        {
            if (FilePath == null && string.IsNullOrWhiteSpace(HostKey)) { return false; }
            string path = FilePath ?? PathFor(HostKey, Folder);
            try
            {
                Sanitise();
                if (IsEmpty)
                {
                    if (File.Exists(path)) { File.Delete(path); }
                    return true;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JSON));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture overrides could not be saved ({path}): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The material key: the UniqueId, else "name:" + the material name.
        /// </summary>
        public static string MaterialKey(string uniqueId, string name) =>
            !string.IsNullOrWhiteSpace(uniqueId) ? uniqueId.Trim() : "name:" + (name ?? string.Empty).Trim();

        /// <summary>
        /// The override for a material, or null: by UniqueId first, then by name.
        /// </summary>
        /// <param name="documentKey"><see cref="HOST"/> or the link's model key.</param>
        /// <param name="uniqueId">The material UniqueId, or null.</param>
        /// <param name="name">The material name.</param>
        public TextureOverride Find(string documentKey, string uniqueId, string name)
        {
            if (!Documents.TryGetValue(documentKey ?? HOST, out Dictionary<string, TextureOverride> materials)) { return null; }
            if (!string.IsNullOrWhiteSpace(uniqueId) && materials.TryGetValue(uniqueId.Trim(), out TextureOverride byId)) { return byId; }
            return materials.TryGetValue(MaterialKey(null, name), out TextureOverride byName) ? byName : null;
        }

        /// <summary>
        /// Sets (or, with null, clears) a material's override. Clearing removes both the UniqueId and the name entry.
        /// </summary>
        public void Set(string documentKey, string uniqueId, string name, TextureOverride value)
        {
            documentKey ??= HOST;
            if (!Documents.TryGetValue(documentKey, out Dictionary<string, TextureOverride> materials))
            {
                if (value == null || !value.IsValid) { return; }
                Documents[documentKey] = materials = new Dictionary<string, TextureOverride>(StringComparer.Ordinal);
            }

            materials.Remove(MaterialKey(null, name));
            if (!string.IsNullOrWhiteSpace(uniqueId)) { materials.Remove(uniqueId.Trim()); }
            if (value != null && value.IsValid)
            {
                value.Name ??= name;
                materials[MaterialKey(uniqueId, name)] = value;
            }
            if (materials.Count == 0) { Documents.Remove(documentKey); }
        }

        /// <summary>
        /// The document key of a scene material: <see cref="HOST"/> for the host, else the link's model key (falling
        /// back to "link:n" when the snapshot has none).
        /// </summary>
        public static string DocumentKeyOf(SceneMaterial material, IReadOnlyList<LinkInfo> links)
        {
            if (material == null || material.Link <= 0) { return HOST; }
            int i = material.Link - 1;
            string key = links != null && i < links.Count ? links[i]?.ModelKey : null;
            return string.IsNullOrWhiteSpace(key) ? "link:" + material.Link : key;
        }

        private void Sanitise()
        {
            Documents = (Documents ?? new Dictionary<string, Dictionary<string, TextureOverride>>())
                .Where(d => !string.IsNullOrWhiteSpace(d.Key) && d.Value != null)
                .ToDictionary(
                    d => d.Key,
                    d => d.Value
                        .Where(m => !string.IsNullOrWhiteSpace(m.Key) && m.Value != null && m.Value.IsValid)
                        .Take(MAX_PER_DOCUMENT)
                        .ToDictionary(m => m.Key, m => Clean(m.Value), StringComparer.Ordinal),
                    StringComparer.Ordinal);
            foreach (string empty in Documents.Where(d => d.Value.Count == 0).Select(d => d.Key).ToList()) { Documents.Remove(empty); }

            static TextureOverride Clean(TextureOverride o)
            {
                // Exactly one meaning: image wins over proxy wins over plain colour
                if (!string.IsNullOrWhiteSpace(o.Image)) { return new TextureOverride { Image = o.Image.Trim(), Name = o.Name }; }
                if (!string.IsNullOrWhiteSpace(o.Proxy)) { return new TextureOverride { Proxy = ProxyCatalog.Normalise(o.Proxy), Name = o.Name }; }
                return new TextureOverride { ColourOnly = true, Name = o.Name };
            }
        }
    }
}
