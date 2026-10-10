using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// How a family type is placed in Revit, as far as the family library cares.
    /// </summary>
    public static class LibraryPlacement
    {
        /// <summary>Placed on a level, free in plan (non-hosted): can be placed from the walkthrough.</summary>
        public const string LEVEL_BASED = "levelBased";

        /// <summary>Needs a wall, floor, ceiling or roof host: listed, not placeable yet.</summary>
        public const string HOSTED = "hosted";

        /// <summary>Work-plane- or face-based: placed on the level's plane (hosted by the level's work plane).</summary>
        public const string WORK_PLANE = "workPlane";

        /// <summary>Older name of <see cref="WORK_PLANE"/> (snapshots from the first library build; listed only).</summary>
        public const string FACE_BASED = "faceBased";

        /// <summary>Anything else (two-level, line-based, adaptive…): listed, not placeable.</summary>
        public const string OTHER = "other";
    }

    /// <summary>
    /// One loadable family type offered in the walkthrough's family library (next round: family library).
    /// Placeable types have a template element: hidden geometry at the tail of the snapshot that the Place gun
    /// clones, exactly like the Clone gun clones a placed element.
    /// </summary>
    public sealed class LibraryEntry
    {
        /// <summary>The family type's ElementId value (a fallback; <see cref="TypeUniqueId"/> is the key).</summary>
        public long TypeId { get; set; }

        /// <summary>The family type's UniqueId: what a placement asks Revit to place.</summary>
        public string TypeUniqueId { get; set; } = string.Empty;

        /// <summary>Family name.</summary>
        public string Family { get; set; } = string.Empty;

        /// <summary>Type name.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Category catalog key (e.g. "furniture").</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>One of <see cref="LibraryPlacement"/>.</summary>
        public string Placement { get; set; } = LibraryPlacement.OTHER;

        /// <summary>True when the walkthrough can place it (it then has a template element).</summary>
        public bool Placeable { get; set; }

        /// <summary>Why it can't be placed (shown greyed in the library), or null.</summary>
        public string Reason { get; set; }

        /// <summary>The template element (index into <see cref="SceneData.Elements"/>), or -1 when there is none.</summary>
        public int Element { get; set; } = -1;

        /// <summary>The preview image's entry name ("library/n.png"), or null.</summary>
        public string Preview { get; set; }

        /// <summary>Instances of the type already in the snapshot (for sorting and the card).</summary>
        public int Placed { get; set; }

        /// <summary>The catalog index of <see cref="Category"/> (set on read; not written).</summary>
        [JsonIgnore]
        public int CategoryIndex { get; set; } = -1;

        /// <summary>"Family : Type".</summary>
        [JsonIgnore]
        public string Label => string.IsNullOrEmpty(Type) ? Family : $"{Family} : {Type}";
    }

    /// <summary>
    /// The family library of a snapshot: the offered types, their preview images and where the template geometry
    /// starts. Immutable after construction. Never null on a scene (<see cref="Empty"/>).
    /// </summary>
    public sealed class LibraryData
    {
        /// <summary>No library (the default: the Revit option is off, or an older snapshot).</summary>
        public static LibraryData Empty { get; } = new();

        /// <summary>The offered types, sorted by category, family and type.</summary>
        public LibraryEntry[] Entries { get; init; } = Array.Empty<LibraryEntry>();

        /// <summary>Preview images (PNG) by entry name.</summary>
        public IReadOnlyDictionary<string, byte[]> Previews { get; init; } = new Dictionary<string, byte[]>();

        /// <summary>
        /// The first vertex of the template geometry: every vertex from here on belongs to a template (hidden), so
        /// whole-scene passes (reflection probe placement) stop here. Equals the vertex count when there are none.
        /// </summary>
        public int VertexStart { get; init; } = int.MaxValue;

        /// <summary>True when nothing is offered.</summary>
        public bool IsEmpty => Entries.Length == 0;

        /// <summary>Number of placeable types.</summary>
        public int PlaceableCount => Entries.Count(e => e.Placeable && e.Element >= 0);

        /// <summary>The entry of a type, by UniqueId (null when the library doesn't have it).</summary>
        public LibraryEntry Find(string typeUniqueId)
        {
            if (string.IsNullOrEmpty(typeUniqueId)) { return null; }
            foreach (LibraryEntry entry in Entries)
            {
                if (string.Equals(entry.TypeUniqueId, typeUniqueId, StringComparison.Ordinal)) { return entry; }
            }
            return null;
        }
    }
}
