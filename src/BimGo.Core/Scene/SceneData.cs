using System.Numerics;
using System.Runtime.InteropServices;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// One vertex of the static scene: position and normal in metres (Z up), RGBA8 colour.
    /// The layout matches the GL vertex attributes (28 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SceneVertex
    {
        /// <summary>Size in bytes.</summary>
        public const int SIZE = 28;

        /// <summary>Position (metres, scene-local).</summary>
        public Vector3 Position;

        /// <summary>Outward normal.</summary>
        public Vector3 Normal;

        /// <summary>Colour packed as R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24.</summary>
        public uint Colour;

        /// <summary>
        /// Creates a vertex.
        /// </summary>
        public SceneVertex(Vector3 position, Vector3 normal, uint colour)
        {
            Position = position;
            Normal = normal;
            Colour = colour;
        }
    }

    /// <summary>
    /// An axis-aligned bounding box.
    /// </summary>
    public struct Aabb
    {
        /// <summary>Minimum corner.</summary>
        public Vector3 Min;

        /// <summary>Maximum corner.</summary>
        public Vector3 Max;

        /// <summary>An inverted (empty) box, ready to be grown.</summary>
        public static Aabb Empty => new(new Vector3(float.MaxValue), new Vector3(float.MinValue));

        /// <summary>
        /// Creates a box.
        /// </summary>
        public Aabb(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>True if the box has been grown at least once.</summary>
        public readonly bool IsValid => Min.X <= Max.X;

        /// <summary>The centre point.</summary>
        public readonly Vector3 Center => (Min + Max) * 0.5f;

        /// <summary>The extents.</summary>
        public readonly Vector3 Size => Max - Min;

        /// <summary>Grows the box to include a point.</summary>
        public void Include(Vector3 p)
        {
            Min = Vector3.Min(Min, p);
            Max = Vector3.Max(Max, p);
        }

        /// <summary>Grows the box to include another box.</summary>
        public void Include(in Aabb other)
        {
            Min = Vector3.Min(Min, other.Min);
            Max = Vector3.Max(Max, other.Max);
        }

        /// <summary>True if two boxes overlap.</summary>
        public readonly bool Overlaps(in Aabb other)
        {
            return Min.X <= other.Max.X && Max.X >= other.Min.X
                && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
                && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
        }
    }

    /// <summary>
    /// Metadata and index ranges for one extracted Revit element.
    /// </summary>
    public sealed class ElementRecord
    {
        /// <summary>The Revit ElementId value (fast, but only stable within one model copy).</summary>
        public long ElementId { get; init; }

        /// <summary>
        /// The Revit UniqueId: the stable key used to match a .bimgo element (and its journal edits) back to the model.
        /// Empty for files written before it was captured.
        /// </summary>
        public string UniqueId { get; init; } = string.Empty;

        /// <summary>
        /// The ElementId value of the host (e.g. the wall a door sits in), or 0. Lets standalone demolition remove
        /// hosted inserts with their host, as Revit does.
        /// </summary>
        public long HostId { get; init; }

        /// <summary>The element name.</summary>
        public string Name { get; init; }

        /// <summary>The Revit category name.</summary>
        public string CategoryName { get; init; }

        /// <summary>"Family: Type" (or the type name for system families).</summary>
        public string FamilyType { get; init; }

        /// <summary>The associated level name, or an em dash.</summary>
        public string LevelName { get; init; }

        /// <summary>Index into <see cref="CategoryCatalog.All"/>.</summary>
        public int CategoryIndex { get; init; }

        /// <summary>First index (into <see cref="SceneData.Indices"/>) of the opaque triangles.</summary>
        public int OpaqueStart { get; set; }

        /// <summary>Number of opaque indices.</summary>
        public int OpaqueCount { get; set; }

        /// <summary>First index of the transparent triangles.</summary>
        public int TransparentStart { get; set; }

        /// <summary>Number of transparent indices.</summary>
        public int TransparentCount { get; set; }

        /// <summary>The element's bounds (scene-local metres).</summary>
        public Aabb Bounds { get; set; }

        /// <summary>True if the element was replaced by a bounding-box proxy.</summary>
        public bool IsProxy { get; init; }

        /// <summary>
        /// True if the Gizmo / Clone guns may move or copy it: a loadable family instance with a location point
        /// that is not pinned, grouped, nested, in-place or wall-hosted.
        /// </summary>
        public bool Movable { get; init; }

        /// <summary>Why the element can't be moved (shown on hover), or null when <see cref="Movable"/>.</summary>
        public string MoveBlockReason { get; init; }

        /// <summary>Rotation pivot: the Revit location point (scene-local metres). Valid when <see cref="Movable"/>.</summary>
        public Vector3 Pivot { get; init; }

        /// <summary>
        /// The element's role between the "existing" and "new" phases. Only <see cref="PhaseRole.Existing"/>
        /// elements can be demolished; the rest can only be deleted. Files written before phases were captured
        /// report every element as existing (Revit still checks on demolition).
        /// </summary>
        public PhaseRole Phase { get; init; }

        /// <summary>
        /// Which model the element comes from: 0 = the host model, n = <c>SceneData.Links[n - 1]</c>. Linked elements
        /// have their own ElementId / UniqueId namespace (ids can repeat across models), are never movable or
        /// demolishable, and never enter the journal.
        /// </summary>
        public int Link { get; init; }

        /// <summary>True for an element from a linked model (read-only in the walkthrough).</summary>
        public bool IsLinked => Link > 0;
    }

    /// <summary>
    /// How an element relates to the session's "existing" and "new" phases.
    /// </summary>
    public enum PhaseRole
    {
        /// <summary>Present in the existing phase and still standing in the new phase: can be demolished.</summary>
        Existing = 0,

        /// <summary>New work: created in the new phase (delete it rather than demolish it).</summary>
        New = 1,

        /// <summary>Created after the existing phase but before the new phase: not existing, not new.</summary>
        Between = 2,

        /// <summary>Has no phases (Revit doesn't phase this category): can only be deleted.</summary>
        Unphased = 3
    }

    /// <summary>
    /// A placed, bounded Revit room: plan boundary loops and a vertical extent, used for the HUD room readout.
    /// </summary>
    public sealed class RoomInfo
    {
        /// <summary>The room number.</summary>
        public string Number { get; init; }

        /// <summary>The room name (without the number).</summary>
        public string Name { get; init; }

        /// <summary>
        /// Boundary loops in plan (scene-local metres). The outer loop and any islands; a point is inside when it is
        /// inside an odd number of loops (even-odd rule), so holes need no special handling.
        /// </summary>
        public Vector2[][] Loops { get; init; }

        /// <summary>Plan bounds minimum (fast rejection).</summary>
        public Vector2 Min { get; init; }

        /// <summary>Plan bounds maximum.</summary>
        public Vector2 Max { get; init; }

        /// <summary>Bottom of the room volume (scene Z).</summary>
        public float BottomZ { get; init; }

        /// <summary>Top of the room volume (scene Z).</summary>
        public float TopZ { get; init; }

        /// <summary>0 for a host room, n for a room from <c>SceneData.Links[n - 1]</c> (host rooms win in the readout).</summary>
        public int Link { get; init; }
    }

    /// <summary>
    /// A Revit level.
    /// </summary>
    /// <param name="Name">The level name.</param>
    /// <param name="Elevation">The elevation in metres (scene Z).</param>
    public readonly record struct LevelInfo(string Name, float Elevation);

    /// <summary>
    /// Where the player starts.
    /// </summary>
    public sealed class SpawnInfo
    {
        /// <summary>Eye position (scene-local metres).</summary>
        public Vector3 Eye { get; init; }

        /// <summary>Yaw (radians, 0 = +X, CCW).</summary>
        public float Yaw { get; init; }

        /// <summary>Pitch (radians).</summary>
        public float Pitch { get; init; }

        /// <summary>A short description for the HUD/log.</summary>
        public string Source { get; init; }
    }

    /// <summary>
    /// The immutable snapshot handed from the Revit thread to the game thread.
    /// After construction nothing in here is modified; the game thread builds its own derived structures.
    /// </summary>
    public sealed class SceneData
    {
        /// <summary>All static vertices.</summary>
        public SceneVertex[] Vertices { get; init; }

        /// <summary>All static triangle indices (per element: opaque range then transparent range).</summary>
        public uint[] Indices { get; init; }

        /// <summary>All extracted elements.</summary>
        public ElementRecord[] Elements { get; init; }

        /// <summary>Levels sorted by elevation.</summary>
        public LevelInfo[] Levels { get; init; }

        /// <summary>Placed, bounded rooms of the current phase (may be empty).</summary>
        public RoomInfo[] Rooms { get; init; } = Array.Empty<RoomInfo>();

        /// <summary>
        /// The "new" phase (ElementId value, -1 if none): the walkthrough shows the model as it stands in this phase,
        /// rooms come from it, phase demolition sets Phase Demolished to it and clones are created in it.
        /// </summary>
        public long PhaseId { get; init; } = -1;

        /// <summary>The "new" phase name (for the HUD), or null.</summary>
        public string PhaseName { get; init; }

        /// <summary>The "existing" phase (ElementId value, -1 if none or unknown, e.g. older files).</summary>
        public long ExistingPhaseId { get; init; } = -1;

        /// <summary>The "existing" phase name, or null (older files).</summary>
        public string ExistingPhaseName { get; init; }

        /// <summary>Set when a saved phase name wasn't found in the model and a default was used (shown once), or null.</summary>
        public string PhaseNote { get; init; }

        /// <summary>Spawn from the active 3D view, or null to pick a random valid point.</summary>
        public SpawnInfo Spawn { get; init; }

        /// <summary>Bounds of all geometry.</summary>
        public Aabb Bounds { get; init; }

        /// <summary>Scene-local origin in Revit internal coordinates (metres). World = local + origin.</summary>
        public Vector3 OriginOffset { get; init; }

        /// <summary>The model title.</summary>
        public string ModelTitle { get; init; }

        /// <summary>
        /// Where comments are saved when running against a Revit session (a JSON sidecar beside the model).
        /// Null for standalone .bimgo files, which keep comments inside the file.
        /// </summary>
        public string CommentsPath { get; init; }

        /// <summary>Where the snapshot came from (model identity, Revit version, who extracted it).</summary>
        public ModelProvenance Provenance { get; init; } = new();

        /// <summary>True north, project base point and survey point.</summary>
        public SiteInfo Site { get; init; } = new();

        /// <summary>
        /// The link instances whose elements were extracted (picked in the Options dialog), in link order; empty when
        /// only the host model was extracted. Link n is <c>Links[n - 1]</c> (see <see cref="ElementRecord.Link"/>).
        /// </summary>
        public LinkInfo[] Links { get; init; } = Array.Empty<LinkInfo>();

        /// <summary>The link an element comes from, or null for a host element (or an unknown link number).</summary>
        public LinkInfo LinkOf(ElementRecord record) =>
            record != null && record.Link > 0 && record.Link <= Links.Length ? Links[record.Link - 1] : null;

        /// <summary>Glowing surfaces and lighting-fixture lights (optional; never null).</summary>
        public LightingData Lighting { get; init; } = LightingData.Empty;

        /// <summary>Materials, textures and surface coordinates for Realistic mode (optional; never null).</summary>
        public MaterialData Materials { get; init; } = MaterialData.Empty;

        /// <summary>Optional extra parameter values per element (names picked in the Options dialog). Never null.</summary>
        public ParameterTable Parameters { get; init; } = ParameterTable.Empty;

        /// <summary>Per category definition: was it loaded at launch.</summary>
        public bool[] CategoryLoaded { get; init; }

        /// <summary>Per category definition: number of extracted elements.</summary>
        public int[] CategoryElementCounts { get; init; }

        /// <summary>The launch settings.</summary>
        public LaunchSettings Settings { get; init; }

        /// <summary>
        /// The view the elements came from when "only elements visible in the active view" was used, else null
        /// (extracted by category).
        /// </summary>
        public string SourceView { get; init; }

        /// <summary>Number of elements replaced by proxies.</summary>
        public int ProxyCount { get; init; }

        /// <summary>Number of elements skipped for being over the limit.</summary>
        public int SkippedCount { get; init; }

        /// <summary>Extraction duration.</summary>
        public TimeSpan ExtractionTime { get; init; }

        /// <summary>Total triangles.</summary>
        public int TriangleCount => Indices.Length / 3;
    }
}
