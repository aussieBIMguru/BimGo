using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Materials and textures captured at extraction (optional: only when "Extract materials and textures" was ticked
    /// at Go / Export). Drives the walkthrough's Realistic colour mode:
    /// <list type="bullet">
    /// <item>a material table (render colour, texture, real-world placement, glass reflectivity);</item>
    /// <item>a per-vertex material index (<see cref="NONE"/> = the vertex keeps its baked colour);</item>
    /// <item>per-vertex surface coordinates in metres along each face's own axes (Revit-like alignment);</item>
    /// <item>the texture images, re-encoded and size-capped, keyed by their entry name in the .bimgo.</item>
    /// </list>
    /// Older files and light extractions have none (<see cref="Empty"/>).
    /// </summary>
    public sealed class MaterialData
    {
        /// <summary>No materials (Realistic mode unavailable).</summary>
        public static readonly MaterialData Empty = new();

        /// <summary>The per-vertex index meaning "no material: use the vertex colour".</summary>
        public const ushort NONE = 0xFFFF;

        /// <summary>Most materials a snapshot can index (one value is reserved for <see cref="NONE"/>).</summary>
        public const int MAX_MATERIALS = 0xFFFF;

        /// <summary>Texture size caps offered at extraction (longest side, px).</summary>
        public static readonly int[] TEXTURE_SIZES = { 256, 512, 1024, 2048 };

        /// <summary>The materials, indexed by <see cref="VertexMaterial"/>.</summary>
        public SceneMaterial[] Materials { get; init; } = Array.Empty<SceneMaterial>();

        /// <summary>One material index per scene vertex (same length as <see cref="SceneData.Vertices"/>).</summary>
        public ushort[] VertexMaterial { get; init; } = Array.Empty<ushort>();

        /// <summary>
        /// One surface coordinate per scene vertex, in metres along the face's own axes (U horizontal and V up on
        /// walls; plan X / Y on floors and roofs). The material's scale, offset and angle turn it into texture repeats.
        /// Same length as <see cref="VertexMaterial"/>.
        /// </summary>
        public Vector2[] VertexUv { get; init; } = Array.Empty<Vector2>();

        /// <summary>Image bytes (JPEG / PNG) by entry name (e.g. "textures/3fa2….jpg"), deduplicated.</summary>
        public IReadOnlyDictionary<string, byte[]> Textures { get; init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>The size cap the textures were stored at (longest side, px).</summary>
        public int TextureMaxSize { get; init; } = 512;

        /// <summary>True if there is nothing to use.</summary>
        public bool IsEmpty => Materials.Length == 0 || VertexMaterial.Length == 0;

        /// <summary>
        /// A copy with a new material table and extra images (the in-app Textures panel: a picked image, a scan hit or
        /// a proxy). The per-vertex streams are shared (they never change: surface coordinates are material
        /// independent), the images are merged (<paramref name="addedTextures"/> wins on a clash), and images no
        /// material references any more are dropped. The original is left untouched.
        /// </summary>
        /// <param name="materials">The new table (same length as <see cref="Materials"/>; indices must not move).</param>
        /// <param name="addedTextures">Images to add by entry name, or null.</param>
        public MaterialData With(SceneMaterial[] materials, IReadOnlyDictionary<string, byte[]> addedTextures = null)
        {
            if (materials == null || materials.Length != Materials.Length)
            {
                throw new ArgumentException("The material table must keep its length (the vertex indices point into it).", nameof(materials));
            }

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (SceneMaterial material in materials)
            {
                if (material?.Texture != null) { referenced.Add(material.Texture); }
            }

            var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, byte[]> pair in Textures)
            {
                if (referenced.Contains(pair.Key)) { textures[pair.Key] = pair.Value; }
            }
            if (addedTextures != null)
            {
                foreach (KeyValuePair<string, byte[]> pair in addedTextures)
                {
                    if (referenced.Contains(pair.Key) && pair.Value != null && pair.Value.Length > 0) { textures[pair.Key] = pair.Value; }
                }
            }

            return new MaterialData
            {
                Materials = materials,
                VertexMaterial = VertexMaterial,
                VertexUv = VertexUv,
                Textures = textures,
                TextureMaxSize = TextureMaxSize
            };
        }

        /// <summary>
        /// The nearest offered texture size cap to a value (keeps hand-edited settings on the offered steps).
        /// </summary>
        public static int NearestTextureSize(int value)
        {
            int best = TEXTURE_SIZES[0];
            foreach (int size in TEXTURE_SIZES)
            {
                if (Math.Abs(size - value) < Math.Abs(best - value)) { best = size; }
            }
            return best;
        }
    }

    /// <summary>
    /// Whether a material's texture made it into the snapshot.
    /// </summary>
    public enum TextureState
    {
        /// <summary>The appearance has no image on its colour slot (a plain colour).</summary>
        None = 0,

        /// <summary>The image was found, re-encoded and embedded (<see cref="SceneMaterial.Texture"/>).</summary>
        Embedded = 1,

        /// <summary>The appearance points at an image that wasn't found on the extracting machine.</summary>
        Missing = 2,

        /// <summary>The colour slot holds a procedural map (noise, checker…): drawn as the plain render colour.</summary>
        Procedural = 3,

        /// <summary>The image was found but couldn't be decoded (unsupported format, damaged).</summary>
        Unreadable = 4
    }

    /// <summary>
    /// Where an embedded texture came from (<see cref="SceneMaterial.TextureOrigin"/>). Strings rather than an enum on
    /// purpose: an older reader meeting an unknown enum string would drop the whole material table.
    /// </summary>
    public static class TextureOrigins
    {
        /// <summary>The path in the appearance asset (found by the locator).</summary>
        public const string ASSET = "asset";

        /// <summary>Found by file name in a remembered texture search folder.</summary>
        public const string SEARCH = "search";

        /// <summary>A per-model override: an image the user picked or a deep-scan hit they accepted.</summary>
        public const string OVERRIDE = "override";

        /// <summary>A CC0 proxy from the app's pack (<see cref="SceneMaterial.Proxy"/>; the image is not embedded).</summary>
        public const string PROXY = "proxy";
    }

    /// <summary>
    /// One material as the walkthrough draws it in Realistic mode. Serialised as-is into materials.json. Fields added
    /// after build A are optional (null / false are not written), so build A readers ignore them and build A files
    /// read with their defaults.
    /// </summary>
    public sealed class SceneMaterial
    {
        /// <summary>The Revit material name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>0 = host model, n = <c>SceneData.Links[n - 1]</c> (material ids are per document).</summary>
        public int Link { get; set; }

        /// <summary>The Revit material ElementId value in its document.</summary>
        public long MaterialId { get; set; }

        /// <summary>The appearance schema (GenericSchema, PrismOpaqueSchema…), for diagnostics.</summary>
        public string Schema { get; set; } = string.Empty;

        /// <summary>The Revit material UniqueId (per-model texture overrides match on it), or null in build A files.</summary>
        public string UniqueId { get; set; }

        /// <summary>
        /// The colour drawn under (or instead of) the texture, RGB 0–1: the render colour (appearance asset); the
        /// shading colour when the appearance is unreadable, or when its texture didn't make it in (missing,
        /// unreadable, procedural: the render colour is then often a white placeholder, see <see cref="RenderColour"/>).
        /// </summary>
        public Vector3 Colour { get; set; } = new(0.8f, 0.8f, 0.8f);

        /// <summary>
        /// The appearance's own render colour when <see cref="Colour"/> fell back to the shading colour (texture
        /// missing / unreadable / procedural), else null. Restored as the base when a texture is added later.
        /// </summary>
        public Vector3? RenderColour { get; set; }

        /// <summary>
        /// The appearance asset's own tint (Revit's Appearance tab "Tint", applying to the whole look: colour and
        /// image), or null when off. Kept apart from <see cref="Tint"/> so the walkthrough can switch Revit tint off.
        /// </summary>
        public Vector3? AssetTint { get; set; }

        /// <summary>
        /// The embedded image's entry name (e.g. "textures/3fa2….jpg"), or null. Several materials can share one.
        /// </summary>
        public string Texture { get; set; }

        /// <summary>The texture's state (why there is or isn't one).</summary>
        public TextureState TextureState { get; set; }

        /// <summary>The image path exactly as the appearance stores it (for later reconciliation), or null.</summary>
        public string TextureSource { get; set; }

        /// <summary>Where the embedded image came from (<see cref="TextureOrigins"/>), or null (build A: the asset).</summary>
        public string TextureOrigin { get; set; }

        /// <summary>
        /// A CC0 proxy keyword (<see cref="ProxyCatalog"/>) drawn when there is no embedded image, or null. Only the
        /// keyword is stored: the image ships with the app, so an app without the pack shows the plain colour.
        /// </summary>
        public string Proxy { get; set; }

        /// <summary>
        /// True if Revit shows the image inverted (<c>unifiedbitmap_Invert</c>): the shader uses 1 − rgb before the
        /// tint and fade. A flag rather than a baked image, because images are shared between materials.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Invert { get; set; }

        /// <summary>True if the path's syntax marks it as an Autodesk Material Library image.</summary>
        public bool Autodesk { get; set; }

        /// <summary>Real-world width one texture repeat covers (m).</summary>
        public float ScaleU { get; set; } = 1f;

        /// <summary>Real-world height one texture repeat covers (m).</summary>
        public float ScaleV { get; set; } = 1f;

        /// <summary>Real-world offset along U (m).</summary>
        public float OffsetU { get; set; }

        /// <summary>Real-world offset along V (m).</summary>
        public float OffsetV { get; set; }

        /// <summary>Texture rotation (degrees, anticlockwise).</summary>
        public float Angle { get; set; }

        /// <summary>How much of the image shows over the colour (Generic "image fade"): 1 = all image.</summary>
        public float Fade { get; set; } = 1f;

        /// <summary>A multiplier on the image (bitmap tint, Hardwood tint and RGB amount), 1 = none.</summary>
        public Vector3 Tint { get; set; } = Vector3.One;

        /// <summary>
        /// Reflectivity facing the surface head-on (0–1; glass ≈ 0.04–0.15, a mirror ≈ 0.9). 0 = no sky reflection.
        /// </summary>
        public float Reflectivity { get; set; }

        /// <summary>
        /// Reflection probes round: reflection strength of an <b>opaque</b> surface as Revit's appearance sets it
        /// (0–1, the intent; the app rounds it to 25 % tiers and applies the user's threshold). 0 = reflects nothing.
        /// Glass keeps <see cref="Reflectivity"/>. Not written when 0, so older files read as "no shine".
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float Shine { get; set; }

        /// <summary>How blurred the reflection is: 0 = mirror sharp, 1 = matt. Null = not read (treated as 1).</summary>
        public float? Roughness { get; set; }

        /// <summary>A metal: its reflection takes the material's colour.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Metallic { get; set; }

        /// <summary>Water (Revit Water schema, or a see-through material named "water"): animated ripples.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Water { get; set; }

        /// <summary>Ripple strength for <see cref="Water"/> (Revit <c>water_bump_amount</c>, 0.1 typical).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public float WaterBump { get; set; }

        /// <summary>Which Revit property the reflection values came from (diagnostics), or null.</summary>
        public string ReflectSource { get; set; }

        /// <summary>
        /// Creates a defensive copy with values clamped into range (guards against hand-edited or damaged files).
        /// </summary>
        public SceneMaterial Clean()
        {
            return new SceneMaterial
            {
                Name = Name ?? string.Empty,
                UniqueId = string.IsNullOrWhiteSpace(UniqueId) ? null : UniqueId,
                Link = Math.Max(0, Link),
                MaterialId = MaterialId,
                Schema = Schema ?? string.Empty,
                Colour = Clamp01(Colour, new Vector3(0.8f)),
                RenderColour = RenderColour is Vector3 render ? Clamp01(render, new Vector3(0.8f)) : null,
                AssetTint = AssetTint is Vector3 assetTint ? Clamp(assetTint, 0f, 4f, Vector3.One) : null,
                Texture = string.IsNullOrWhiteSpace(Texture) ? null : Texture,
                TextureState = Enum.IsDefined(TextureState) ? TextureState : TextureState.None,
                TextureSource = TextureSource,
                TextureOrigin = string.IsNullOrWhiteSpace(TextureOrigin) ? null : TextureOrigin.Trim().ToLowerInvariant(),
                Proxy = ProxyCatalog.Normalise(Proxy),
                Invert = Invert,
                Autodesk = Autodesk,
                ScaleU = PositiveOr(ScaleU, 1f),
                ScaleV = PositiveOr(ScaleV, 1f),
                OffsetU = Finite(OffsetU),
                OffsetV = Finite(OffsetV),
                Angle = Finite(Angle) % 360f,
                Fade = float.IsFinite(Fade) ? Math.Clamp(Fade, 0f, 1f) : 1f,
                Tint = Clamp(Tint, 0f, 4f, Vector3.One),
                Reflectivity = float.IsFinite(Reflectivity) ? Math.Clamp(Reflectivity, 0f, 1f) : 0f,
                Shine = float.IsFinite(Shine) ? Math.Clamp(Shine, 0f, 1f) : 0f,
                Roughness = Roughness is float rough && float.IsFinite(rough) ? Math.Clamp(rough, 0f, 1f) : null,
                Metallic = Metallic,
                Water = Water,
                WaterBump = float.IsFinite(WaterBump) ? Math.Clamp(WaterBump, 0f, 1f) : 0f,
                ReflectSource = string.IsNullOrWhiteSpace(ReflectSource) ? null : ReflectSource
            };

            static float Finite(float v) => float.IsFinite(v) ? v : 0f;
            static float PositiveOr(float v, float fallback) => float.IsFinite(v) && v > 1e-4f ? Math.Min(v, 1e4f) : fallback;
            static Vector3 Clamp01(Vector3 v, Vector3 fallback) => Clamp(v, 0f, 1f, fallback);
            static Vector3 Clamp(Vector3 v, float min, float max, Vector3 fallback) =>
                float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
                    ? Vector3.Clamp(v, new Vector3(min), new Vector3(max))
                    : fallback;
        }
    }
}
