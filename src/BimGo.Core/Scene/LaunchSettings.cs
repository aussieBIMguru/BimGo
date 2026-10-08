using System.Text.Json;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// What to do with an element whose triangle count exceeds the threshold.
    /// </summary>
    public enum OverLimitMode
    {
        /// <summary>Replace the element with its bounding box.</summary>
        Proxy = 0,

        /// <summary>Leave the element out.</summary>
        Skip = 1
    }

    /// <summary>
    /// How lighting fixtures show in the walkthrough.
    /// </summary>
    public enum ArtificialLightMode
    {
        /// <summary>Not shown: fixtures look like any other element.</summary>
        Off = 0,

        /// <summary>Lamps and lenses glow (with a bloom), but cast no light.</summary>
        Glow = 1,

        /// <summary>Glow, and the nearest fixtures cast light (with cached shadow maps).</summary>
        Lights = 2
    }

    /// <summary>
    /// Shadow-map quality: resolution, number of cascades and edge softening (see the renderer's presets).
    /// </summary>
    public enum ShadowQuality
    {
        /// <summary>One 2048 px map near the player, hard edges.</summary>
        Low = 0,

        /// <summary>Three 2048 px cascades, softened edges.</summary>
        Medium = 1,

        /// <summary>Four 3072 px cascades, softer edges, longer shadow distance.</summary>
        High = 2
    }

    /// <summary>
    /// How geometry is coloured.
    /// </summary>
    public enum ColourMode
    {
        /// <summary>Greyscale study-model look.</summary>
        Whitecard = 0,

        /// <summary>Material colours cached from Revit (the shading colour, as Revit's Shaded view).</summary>
        Material = 1,

        /// <summary>
        /// Render colours and textures from the appearance assets (as Revit's Realistic view). Needs a snapshot taken
        /// with <see cref="LaunchSettings.ExtractTextures"/>; without one the walkthrough shows material colours.
        /// </summary>
        Realistic = 2
    }

    /// <summary>
    /// How Revit's tint is drawn in the Realistic colour mode (<see cref="LaunchSettings.RevitTint"/>).
    /// </summary>
    public enum TintMode
    {
        /// <summary>Revit tint ignored.</summary>
        Off = 0,

        /// <summary>The image (and, for an appearance tint, the colour) multiplied by the tint colour in linear light:
        /// Revit's own blend (confirmed against Realistic view on a tint test model).</summary>
        Multiply = 1,

        /// <summary>Build B trial value (hue at the image's lightness), dropped once Multiply was confirmed: read as
        /// <see cref="Multiply"/> (kept so a settings file that has it still reads).</summary>
        KeepLightness = 2
    }

    /// <summary>
    /// Options chosen in the launch dialog. Persisted as JSON in %AppData%\BimGo\settings.json and
    /// shared by the Revit add-in (extraction options) and the standalone app (display options).
    /// </summary>
    public sealed class LaunchSettings
    {
        /// <summary>Keys of the enabled category definitions.</summary>
        public List<string> EnabledCategories { get; set; } = Scene.CategoryCatalog.DefaultEnabledKeys();

        /// <summary>
        /// Extract only what the active view shows (off by default): every model element visible in the view comes in,
        /// whatever its category tick, phase or design option; ticked links contribute what the view shows of them.
        /// </summary>
        public bool ActiveViewOnly { get; set; }

        /// <summary>
        /// Leave out helper geometry: the Light Source subcategory (IES / photometric cones) and any subcategory whose
        /// name contains one of <see cref="HelperSubcategoryKeywords"/> (clearance zones, spray cones…). On by default.
        /// </summary>
        public bool SkipHelperGeometry { get; set; } = true;

        /// <summary>Subcategory name fragments (case-insensitive) treated as helper geometry.</summary>
        public List<string> HelperSubcategoryKeywords { get; set; } = DefaultHelperKeywords();

        /// <summary>The default helper subcategory keywords.</summary>
        public static List<string> DefaultHelperKeywords() => new() { "light source", "clearance", "zone", "cone", "photometric" };

        /// <summary>Triangle threshold per element (FFE and Services only).</summary>
        public int TriangleThreshold { get; set; } = 20000;

        /// <summary>Fallback for elements over the threshold.</summary>
        public OverLimitMode OverLimit { get; set; } = OverLimitMode.Proxy;

        /// <summary>Colour mode at launch.</summary>
        public ColourMode Colour { get; set; } = ColourMode.Whitecard;

        /// <summary>
        /// Extract materials and textures at Go / Export (off by default: the model stays light). Embeds each
        /// material's colour texture (size-capped by <see cref="TextureMaxSize"/>) and per-vertex surface coordinates,
        /// for the Realistic colour mode.
        /// </summary>
        public bool ExtractTextures { get; set; }

        /// <summary>Texture size cap at extraction (longest side, px): one of <see cref="MaterialData.TEXTURE_SIZES"/>.</summary>
        public int TextureMaxSize { get; set; } = 512;

        /// <summary>Reflections in the Realistic colour mode: glass, mirrors and shiny surfaces (on by default).</summary>
        public bool Reflections { get; set; } = true;

        /// <summary>
        /// Lowest reflection tier that reflects (%): 50 = shiny things only (the default), 25 = also satin and semi-gloss
        /// surfaces. Glass and water always reflect while <see cref="Reflections"/> is on.
        /// </summary>
        public int ReflectionThreshold { get; set; } = 50;

        /// <summary>Reflection strength multiplier (0.5–2, 1 = default).</summary>
        public float ReflectionStrength { get; set; } = 1f;

        /// <summary>
        /// Reflections read reflection probes (captures of the rooms around reflective surfaces) where there are any;
        /// false = the sky only (cheaper). On by default.
        /// </summary>
        public bool ReflectionProbes { get; set; } = true;

        /// <summary>Probe face size in px, per machine: 128 (default) or 256 ("Probes HQ": sharper, fewer probes fit).</summary>
        public int ProbeResolution { get; set; } = 128;

        /// <summary>
        /// Revit's tint (appearance and bitmap "Tint") in the Realistic colour mode: <see cref="TintMode.Multiply"/>
        /// (Revit's blend, the default) or <see cref="TintMode.Off"/>.
        /// </summary>
        public TintMode RevitTint { get; set; } = TintMode.Multiply;

        /// <summary>
        /// Draw a CC0 proxy texture (by material name, then schema) for materials whose image is missing or unreadable
        /// (on by default). Plain-colour materials are left alone unless the user assigns a proxy.
        /// </summary>
        public bool ProxyMissingTextures { get; set; } = true;

        /// <summary>
        /// Proxy textures take the material's own colour (the image's pattern and shading, the material's hue), so a
        /// white vinyl stays white whatever colour the pack's vinyl is. On by default; off shows the pack's colours.
        /// </summary>
        public bool ProxyMaterialColour { get; set; } = true;

        /// <summary>
        /// Folders searched (by exact file name, recursively) for textures Revit can't find, for every model. Added
        /// from the "Review textures…" window or the app's Textures panel ("Remember this folder").
        /// </summary>
        public List<string> TextureSearchFolders { get; set; } = new();

        /// <summary>Most remembered texture search folders.</summary>
        public const int MAX_TEXTURE_SEARCH_FOLDERS = 20;

        /// <summary>
        /// Remembers a texture search folder (moved to the end if already there; the oldest drops off past the limit).
        /// </summary>
        /// <returns>True if the list changed.</returns>
        public bool AddTextureSearchFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) { return false; }
            string clean = folder.Trim().TrimEnd('\\', '/');
            TextureSearchFolders ??= new List<string>();
            if (TextureSearchFolders.Count > 0 && string.Equals(TextureSearchFolders[^1], clean, StringComparison.OrdinalIgnoreCase)) { return false; }
            TextureSearchFolders.RemoveAll(f => string.Equals(f, clean, StringComparison.OrdinalIgnoreCase));
            TextureSearchFolders.Add(clean);
            if (TextureSearchFolders.Count > MAX_TEXTURE_SEARCH_FOLDERS)
            {
                TextureSearchFolders.RemoveRange(0, TextureSearchFolders.Count - MAX_TEXTURE_SEARCH_FOLDERS);
            }
            return true;
        }

        /// <summary>MSAA samples (0, 2 or 4).</summary>
        public int Msaa { get; set; } = 0;

        /// <summary>Screen-space ambient occlusion in the walkthrough (contact shading in corners and under objects).</summary>
        public bool AmbientOcclusion { get; set; } = true;

        /// <summary>Artificial lights at launch (the sun panel and K change it in the walkthrough).</summary>
        public ArtificialLightMode ArtificialLights { get; set; } = ArtificialLightMode.Lights;

        /// <summary>Artificial light brightness multiplier (0–2, 1 = default).</summary>
        public float ArtificialLightIntensity { get; set; } = 1f;

        /// <summary>Bloom (the halo around glowing surfaces) multiplier (0–2, 1 = default; 0 = no bloom).</summary>
        public float BloomIntensity { get; set; } = 1f;

        /// <summary>
        /// Material name fragments (case-insensitive) that glow inside Lighting Fixtures elements, for families whose
        /// materials have no self-illumination set. Materials with Revit self-illumination glow anywhere.
        /// </summary>
        public List<string> EmissiveKeywords { get; set; } = DefaultEmissiveKeywords();

        /// <summary>The default emissive keywords.</summary>
        public static List<string> DefaultEmissiveKeywords() => new() { "lamp", "bulb", "led", "lens", "diffuser", "emissive", "glow", "illum", "light source", "luminaire" };

        /// <summary>Mouse sensitivity multiplier.</summary>
        public float MouseSensitivity { get; set; } = 1.0f;

        /// <summary>Horizontal field of view in degrees.</summary>
        public float FieldOfView { get; set; } = 90f;

        /// <summary>Invert vertical mouse look.</summary>
        public bool InvertY { get; set; }

        /// <summary>Synchronise presentation to the display refresh rate.</summary>
        public bool VSync { get; set; } = true;

        /// <summary>Load comments from the JSON sidecar.</summary>
        public bool LoadComments { get; set; } = true;

        /// <summary>Show the FPS readout.</summary>
        public bool ShowFps { get; set; } = true;

        /// <summary>Maximum riser the player steps up without jumping (mm).</summary>
        public float MaxStepHeightMm { get; set; } = 200f;

        /// <summary>Gizmo / Clone: move and rotate in fixed increments (G toggles in the walkthrough).</summary>
        public bool GizmoSnap { get; set; }

        /// <summary>Gizmo / Clone snap: move increment (mm), one of <see cref="SNAP_MOVE_STEPS_MM"/>.</summary>
        public float SnapMoveMm { get; set; } = 50f;

        /// <summary>Gizmo / Clone snap: rotation increment (degrees), one of <see cref="SNAP_ANGLE_STEPS_DEG"/>.</summary>
        public float SnapAngleDeg { get; set; } = 15f;

        /// <summary>
        /// Extra parameter names to extract per element (instance first, then type). Shown by the Scan gun and
        /// stored in .bimgo files. Empty by default.
        /// </summary>
        public List<string> ExtraParameters { get; set; } = new();

        /// <summary>
        /// The "existing" phase by name: what is there before the works. Only elements that exist in this phase (and
        /// still stand in <see cref="NewPhase"/>) can be demolished. Empty = the phase before the new phase.
        /// Names (not ids) are stored because the settings are shared by every model.
        /// </summary>
        public string ExistingPhase { get; set; } = string.Empty;

        /// <summary>
        /// The "new" phase by name: the walkthrough shows the model as it stands in this phase, demolition sets
        /// Phase Demolished to it and clones are created in it. Empty = the launch view's phase, else the last phase.
        /// </summary>
        public string NewPhase { get; set; } = string.Empty;

        /// <summary>
        /// Linked models to extract with each host model: host model key (<c>ProjectInformation.UniqueId</c>) → the
        /// UniqueIds of the ticked RevitLinkInstances. A model with no entry extracts no links (the default); the
        /// Options dialog pre-ticks the saved choice and Refresh (F5) reuses it.
        /// </summary>
        public Dictionary<string, List<string>> LinkedModels { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Most host models remembered in <see cref="LinkedModels"/> (oldest entries are dropped).</summary>
        public const int MAX_LINKED_MODEL_ENTRIES = 200;

        /// <summary>
        /// The link instances (UniqueIds) ticked for a host model; empty when none (never null).
        /// </summary>
        /// <param name="hostModelKey">The host model key.</param>
        public IReadOnlyList<string> LinksFor(string hostModelKey)
        {
            if (string.IsNullOrEmpty(hostModelKey) || LinkedModels == null) { return Array.Empty<string>(); }
            return LinkedModels.TryGetValue(hostModelKey, out List<string> links) && links != null ? links : Array.Empty<string>();
        }

        /// <summary>
        /// Remembers the link instances ticked for a host model (an empty choice removes the entry).
        /// </summary>
        /// <param name="hostModelKey">The host model key.</param>
        /// <param name="linkInstanceIds">The ticked RevitLinkInstance UniqueIds.</param>
        public void SetLinksFor(string hostModelKey, IEnumerable<string> linkInstanceIds)
        {
            if (string.IsNullOrEmpty(hostModelKey)) { return; }
            LinkedModels ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> links = (linkInstanceIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Keep the list bounded (models not seen for a long time lose their choice: none, the default).
            // Rebuilt rather than edited in place: a Dictionary reuses a removed entry's slot, so after the first
            // removal Keys.First() is no longer the oldest choice and would drop the one just added.
            var ordered = LinkedModels.Where(p => !string.Equals(p.Key, hostModelKey, StringComparison.Ordinal)).ToList();
            if (links.Count > 0) { ordered.Add(new KeyValuePair<string, List<string>>(hostModelKey, links)); }
            int excess = Math.Max(0, ordered.Count - MAX_LINKED_MODEL_ENTRIES);
            LinkedModels = new Dictionary<string, List<string>>(ordered.Skip(excess), StringComparer.Ordinal);
        }

        /// <summary>Shadow-map quality on this machine (the sun panel and the Options dialog change it).</summary>
        public ShadowQuality ShadowQuality { get; set; } = ShadowQuality.Medium;

        /// <summary>
        /// The quality profile last chosen on this machine (pause menu or Options window), or Custom after a manual
        /// change. Kept in step with the values it governs (<see cref="QualityProfiles.Detect"/> on load).
        /// </summary>
        public QualityProfile QualityProfile { get; set; } = QualityProfile.Custom;

        /// <summary>
        /// Also write a live session's comments, bookmarks, sun and visibility beside the Revit model when its folder is
        /// writable (and take newer copies from there at Go), so colleagues on a shared drive see them. Off: they live
        /// only in BimGo's per-model folder (<see cref="Format.ModelFolders"/>).
        /// </summary>
        public bool SidecarsBesideModel { get; set; }

        /// <summary>The Options window's last tab (0 Load … 6 Player).</summary>
        public int LastOptionsTab { get; set; }

        /// <summary>The walkthrough's coordinate readout (L cycles it; remembered between sessions).</summary>
        public CoordinateReadout CoordinateReadout { get; set; } = CoordinateReadout.Off;

        /// <summary>Move increments offered for gizmo snapping (mm).</summary>
        public static readonly float[] SNAP_MOVE_STEPS_MM = { 5f, 10f, 25f, 50f, 100f, 250f, 500f, 1000f };

        /// <summary>Rotation increments offered for gizmo snapping (degrees).</summary>
        public static readonly float[] SNAP_ANGLE_STEPS_DEG = { 1f, 5f, 10f, 15f, 30f, 45f, 90f };

        /// <summary>
        /// The preset nearest to a value (keeps hand-edited settings on the offered steps).
        /// </summary>
        public static float NearestStep(float[] steps, float value)
        {
            float best = steps[0];
            foreach (float step in steps)
            {
                if (MathF.Abs(step - value) < MathF.Abs(best - value)) { best = step; }
            }
            return best;
        }

        /// <summary>Upper bound on extra parameters (keeps the Scan panel and file size sensible).</summary>
        public const int MAX_EXTRA_PARAMETERS = 24;

        #region Persistence

        private static readonly JsonSerializerOptions JSON_OPTIONS = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// The path of the settings file.
        /// </summary>
        public static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BimGo", "settings.json");

        /// <summary>
        /// The pre-BimGo settings file (%AppData%\RvtGo\settings.json), copied across once if no BimGo file exists.
        /// </summary>
        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RvtGo", "settings.json");

        /// <summary>
        /// Loads saved settings, or defaults if none exist or the file can't be read.
        /// </summary>
        /// <returns>A LaunchSettings object.</returns>
        public static LaunchSettings LoadOrDefault()
        {
            try
            {
                MigrateLegacy();
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    if (JsonSerializer.Deserialize<LaunchSettings>(json, JSON_OPTIONS) is LaunchSettings loaded)
                    {
                        loaded.Sanitise();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Settings could not be read, using defaults: {ex.Message}");
            }
            return new LaunchSettings();
        }

        /// <summary>
        /// Copies the RvtGo settings file to the BimGo location once (the old file is left in place).
        /// </summary>
        private static void MigrateLegacy()
        {
            try
            {
                if (File.Exists(SettingsPath) || !File.Exists(LegacySettingsPath)) { return; }
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
                Utilities.Log_Utils.Write("Settings migrated from RvtGo.");
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Settings migration skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// A deep copy (through the same JSON the settings file uses), e.g. to try the Options window's unsaved choices.
        /// </summary>
        public LaunchSettings Clone()
        {
            LaunchSettings copy = JsonSerializer.Deserialize<LaunchSettings>(JsonSerializer.Serialize(this, JSON_OPTIONS), JSON_OPTIONS) ?? new LaunchSettings();
            copy.Sanitise();
            return copy;
        }

        /// <summary>
        /// Saves the settings. Failures are logged, never thrown.
        /// </summary>
        public void Save()
        {
            try
            {
                QualityProfile = QualityProfiles.Detect(this); // whoever saved, the profile matches the values
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                string temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JSON_OPTIONS));
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Settings could not be saved: {ex.Message}");
            }
        }

        /// <summary>
        /// Clamps values into valid ranges (guards against hand-edited files).
        /// </summary>
        public void Sanitise()
        {
            EnabledCategories ??= Scene.CategoryCatalog.DefaultEnabledKeys();
            ExtraParameters = (ExtraParameters ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(MAX_EXTRA_PARAMETERS)
                .ToList();
            LinkedModels = LinkedModels == null
                ? new Dictionary<string, List<string>>(StringComparer.Ordinal)
                : LinkedModels
                    .Where(p => !string.IsNullOrEmpty(p.Key) && p.Value != null && p.Value.Count > 0)
                    .ToDictionary(p => p.Key, p => p.Value.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            HelperSubcategoryKeywords = (HelperSubcategoryKeywords ?? DefaultHelperKeywords())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
            ExistingPhase = ExistingPhase?.Trim() ?? string.Empty;
            NewPhase = NewPhase?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(CoordinateReadout)) { CoordinateReadout = CoordinateReadout.Off; }
            if (!Enum.IsDefined(ShadowQuality)) { ShadowQuality = ShadowQuality.Medium; }
            if (!Enum.IsDefined(ArtificialLights)) { ArtificialLights = ArtificialLightMode.Lights; }
            if (!Enum.IsDefined(Colour)) { Colour = ColourMode.Material; }
            TextureMaxSize = MaterialData.NearestTextureSize(TextureMaxSize);
            if (RevitTint != TintMode.Off) { RevitTint = TintMode.Multiply; } // unknown and retired values
            TextureSearchFolders = (TextureSearchFolders ?? new List<string>())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim().TrimEnd('\\', '/'))
                .Where(f => f.Length > 0)
                .Reverse()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MAX_TEXTURE_SEARCH_FOLDERS)
                .Reverse()
                .ToList();
            ArtificialLightIntensity = float.IsFinite(ArtificialLightIntensity) ? Math.Clamp(ArtificialLightIntensity, 0f, 2f) : 1f;
            BloomIntensity = float.IsFinite(BloomIntensity) ? Math.Clamp(BloomIntensity, 0f, 2f) : 1f;
            ReflectionThreshold = ReflectionThreshold <= 37 ? 25 : 50;
            ReflectionStrength = float.IsFinite(ReflectionStrength) ? Math.Clamp(ReflectionStrength, 0.5f, 2f) : 1f;
            ProbeResolution = ProbeResolution >= 192 ? 256 : 128;
            EmissiveKeywords = (EmissiveKeywords ?? DefaultEmissiveKeywords())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
            TriangleThreshold = Math.Clamp(TriangleThreshold, 100, 5_000_000);
            Msaa = Msaa >= 4 ? 4 : Msaa >= 2 ? 2 : 0;
            MouseSensitivity = Math.Clamp(MouseSensitivity, 0.1f, 3f);
            FieldOfView = Math.Clamp(FieldOfView, 60f, 120f);
            MaxStepHeightMm = Math.Clamp(MaxStepHeightMm, 50f, 450f);
            SnapMoveMm = NearestStep(SNAP_MOVE_STEPS_MM, SnapMoveMm);
            SnapAngleDeg = NearestStep(SNAP_ANGLE_STEPS_DEG, SnapAngleDeg);

            LastOptionsTab = Math.Clamp(LastOptionsTab, 0, 6);

            // Last: the profile is whatever the (clamped) values match, so a hand-edited or older file reads true
            QualityProfile = QualityProfiles.Detect(this);
        }

        #endregion
    }
}
