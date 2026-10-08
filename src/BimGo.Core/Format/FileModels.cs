using System.Numerics;
using BimGo.Edits;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    // The data transfer objects of the .bimgo JSON entries. They are deliberately separate from the runtime types
    // (SceneData, ElementRecord...) so the file format can stay stable while the engine evolves.
    // Every member is optional on read: missing values fall back to their defaults.

    #region manifest.json

    /// <summary>
    /// manifest.json: what the file is and where it came from.
    /// </summary>
    public sealed class ManifestDto
    {
        /// <summary>Always "bimgo".</summary>
        public string Format { get; set; } = BimGoFormat.FORMAT_NAME;

        /// <summary>The format version (see <see cref="BimGoFormat.FORMAT_VERSION"/>).</summary>
        public int FormatVersion { get; set; } = BimGoFormat.FORMAT_VERSION;

        /// <summary>The program that wrote the file ("BimGo for Revit 2026", "BimGo").</summary>
        public string Generator { get; set; } = string.Empty;

        /// <summary>The writer's version.</summary>
        public string GeneratorVersion { get; set; } = string.Empty;

        /// <summary>How the file came about: <see cref="FileKinds"/>.</summary>
        public string Kind { get; set; } = FileKinds.EXPORT;

        /// <summary>A display title (normally the model title).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>When the geometry was extracted (UTC).</summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>When the file was last written (UTC).</summary>
        public DateTime SavedUtc { get; set; }

        /// <summary>Who last wrote the file.</summary>
        public string SavedBy { get; set; } = string.Empty;

        /// <summary>Length unit of every coordinate.</summary>
        public string Units { get; set; } = "metres";

        /// <summary>Coordinate convention.</summary>
        public string Axes { get; set; } = "Z up; geometry is scene-local (add model.originOffset for Revit internal coordinates)";

        /// <summary>The model the snapshot came from.</summary>
        public ModelProvenance Provenance { get; set; } = new();

        /// <summary>The options used for the extraction.</summary>
        public ExtractionDto Extraction { get; set; } = new();

        /// <summary>
        /// Live snapshots only: the comment sidecar beside the Revit model, which the app keeps using.
        /// </summary>
        public string CommentsSidecar { get; set; }

        /// <summary>Sizes, for quick listing without reading the rest.</summary>
        public CountsDto Counts { get; set; } = new();
    }

    /// <summary>
    /// Values of <see cref="ManifestDto.Kind"/>.
    /// </summary>
    public static class FileKinds
    {
        /// <summary>Written by the Revit add-in's Export command.</summary>
        public const string EXPORT = "revit-export";

        /// <summary>Saved from a walkthrough of a Revit session (snapshot + session journal).</summary>
        public const string SESSION_SAVE = "session-save";

        /// <summary>Saved by the standalone app.</summary>
        public const string SAVE = "save";

        /// <summary>Written by the add-in for a live session (uncompressed, throwaway; keeps the comment sidecar path).</summary>
        public const string SNAPSHOT = "live-snapshot";
    }

    /// <summary>
    /// Extraction options recorded in the manifest.
    /// </summary>
    public sealed class ExtractionDto
    {
        public List<string> EnabledCategories { get; set; } = new();
        public int TriangleThreshold { get; set; }
        public string OverLimit { get; set; } = string.Empty;
        public List<string> ExtraParameters { get; set; } = new();
        public int ProxyCount { get; set; }
        public int SkippedCount { get; set; }
        public double ExtractionSeconds { get; set; }

        /// <summary>The view the elements came from (active-view-only extraction), or null (by category).</summary>
        public string ActiveView { get; set; }
    }

    /// <summary>
    /// Counts recorded in the manifest.
    /// </summary>
    public sealed class CountsDto
    {
        public int Elements { get; set; }
        public int Vertices { get; set; }
        public int Indices { get; set; }
        public int Levels { get; set; }
        public int Rooms { get; set; }
        public int Comments { get; set; }
        public int JournalEntries { get; set; }
        public int Bookmarks { get; set; }

        /// <summary>Linked models extracted with the host (v7; 0 in older files).</summary>
        public int Links { get; set; }
    }

    #endregion

    #region model.json

    /// <summary>
    /// model.json: scene-wide data.
    /// </summary>
    public sealed class ModelDto
    {
        /// <summary>Scene-local origin in Revit internal coordinates (metres).</summary>
        public Vector3 OriginOffset { get; set; }

        public Vector3 BoundsMin { get; set; }
        public Vector3 BoundsMax { get; set; }

        public SiteInfo Site { get; set; } = new();

        /// <summary>The "new" phase's ElementId value (-1 if none).</summary>
        public long PhaseId { get; set; } = -1;

        /// <summary>The "new" phase's name (the phase the walkthrough shows and demolishes in).</summary>
        public string PhaseName { get; set; }

        /// <summary>The "existing" phase's ElementId value (-1 if none; absent in older files).</summary>
        public long ExistingPhaseId { get; set; } = -1;

        /// <summary>The "existing" phase's name (absent in older files).</summary>
        public string ExistingPhaseName { get; set; }

        /// <summary>A note when a saved phase name wasn't found at extraction and a default was used, or null.</summary>
        public string PhaseNote { get; set; }

        /// <summary>The start position, or null for a random start.</summary>
        public SpawnDto Spawn { get; set; }

        public List<LevelDto> Levels { get; set; } = new();
        public List<RoomDto> Rooms { get; set; } = new();

        /// <summary>The category definitions the elements refer to (by position).</summary>
        public List<CategoryDto> Categories { get; set; } = new();

        /// <summary>
        /// The link instances extracted with the host (v7, optional): <c>elements[].link</c> = n refers to
        /// <c>links[n - 1]</c>. Absent when only the host was extracted.
        /// </summary>
        public List<LinkInfo> Links { get; set; }
    }

    public sealed class SpawnDto
    {
        public Vector3 Eye { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public string Source { get; set; }
    }

    public sealed class LevelDto
    {
        public string Name { get; set; }

        /// <summary>Elevation (scene Z, metres).</summary>
        public float Elevation { get; set; }
    }

    public sealed class RoomDto
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public float BottomZ { get; set; }
        public float TopZ { get; set; }

        /// <summary>The link the room comes from (v7): n = <c>model.links[n - 1]</c>; absent for host rooms.</summary>
        public int? Link { get; set; }

        /// <summary>Boundary loops in plan, each flattened as [x0, y0, x1, y1, ...] (scene-local metres).</summary>
        public List<float[]> Loops { get; set; } = new();
    }

    public sealed class CategoryDto
    {
        /// <summary>The catalog key (stable across versions).</summary>
        public string Key { get; set; }

        /// <summary>True if the category was extracted.</summary>
        public bool Loaded { get; set; }

        /// <summary>Number of elements extracted.</summary>
        public int Count { get; set; }
    }

    #endregion

    #region elements.json / parameters.json / journal.json

    /// <summary>
    /// elements.json: one entry per element, in geometry order.
    /// </summary>
    public sealed class ElementDto
    {
        public long Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }

        /// <summary>Index into <see cref="ModelDto.Categories"/>.</summary>
        public int Category { get; set; }

        /// <summary>The Revit category name.</summary>
        public string CategoryName { get; set; }

        public string FamilyType { get; set; }
        public string Level { get; set; }
        public long HostId { get; set; }
        public bool Proxy { get; set; }
        public bool Movable { get; set; }
        public string MoveBlockReason { get; set; }
        public Vector3 Pivot { get; set; }

        /// <summary>
        /// The phase role: "new", "between" or "unphased"; omitted for existing elements (and in older files).
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// The linked model the element comes from (v7): n = <c>model.links[n - 1]</c>; absent for host elements.
        /// Ids and unique ids are only unique within one model.
        /// </summary>
        public int? Link { get; set; }

        public Vector3 BoundsMin { get; set; }
        public Vector3 BoundsMax { get; set; }

        /// <summary>Opaque triangles: [first index, index count] into geometry.bin.</summary>
        public int[] Opaque { get; set; }

        /// <summary>Transparent triangles: [first index, index count].</summary>
        public int[] Transparent { get; set; }
    }

    /// <summary>
    /// elements.json root.
    /// </summary>
    public sealed class ElementsDto
    {
        public List<ElementDto> Elements { get; set; } = new();
    }

    /// <summary>
    /// parameters.json: pooled names and values, and per element (name, value) index pairs.
    /// </summary>
    public sealed class ParametersDto
    {
        public string[] Names { get; set; } = Array.Empty<string>();
        public string[] Values { get; set; } = Array.Empty<string>();

        /// <summary>Aligned with elements.json; null where an element has none.</summary>
        public int[][] Rows { get; set; } = Array.Empty<int[]>();
    }

    /// <summary>
    /// journal.json root.
    /// </summary>
    public sealed class JournalDto
    {
        public List<JournalEntry> Entries { get; set; } = new();
    }

    #endregion

    #region lighting.json

    /// <summary>
    /// lighting.json: glowing surfaces and lighting-fixture lights (optional; older readers ignore it).
    /// </summary>
    public sealed class LightingDto
    {
        /// <summary>Entry layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Emissive vertex runs as [start, count, packed RGBA (uint)].</summary>
        public List<long[]> Emissive { get; set; } = new();

        /// <summary>The lights.</summary>
        public List<LightDto> Lights { get; set; } = new();
    }

    /// <summary>
    /// One light in lighting.json.
    /// </summary>
    public sealed class LightDto
    {
        /// <summary>Element index (into elements.json).</summary>
        public int Element { get; set; }

        /// <summary>Position (scene-local metres).</summary>
        public Vector3 Position { get; set; }

        /// <summary>Luminous flux (lm).</summary>
        public float Lumens { get; set; } = 1000f;

        /// <summary>Colour temperature (K).</summary>
        public float Kelvin { get; set; } = 3500f;

        /// <summary>0 omnidirectional .. 1 downward.</summary>
        public float Downward { get; set; } = 0.7f;

        /// <summary>True when guessed.</summary>
        public bool Estimated { get; set; }
    }

    #endregion

    #region materials.json

    /// <summary>
    /// materials.json: the material table for Realistic mode (optional; older readers ignore it). The per-vertex
    /// material index and surface coordinates are in material.bin; the images under textures/.
    /// </summary>
    public sealed class MaterialsDto
    {
        /// <summary>Entry layout version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>The size cap the textures were stored at (longest side, px).</summary>
        public int TextureMaxSize { get; set; } = 512;

        /// <summary>The materials, in index order.</summary>
        public List<SceneMaterial> Materials { get; set; } = new();
    }

    #endregion
}
