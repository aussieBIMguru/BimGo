using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// One saved viewpoint: where the player stood and which way they looked. Positions are metres in Revit's internal
    /// coordinate system (like comments), so a bookmark stays put across snapshots whose scene origin differs.
    /// Stored in a .bimgo file's bookmarks.json, or (live Revit sessions) in &lt;model&gt;.bimgo-bookmarks.json beside the model.
    /// </summary>
    public sealed class BookmarkRecord
    {
        /// <summary>Stable id (GUID, no dashes).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>The name shown in the list and on the map.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Who made it.</summary>
        public string Author { get; set; } = Environment.UserName;

        /// <summary>When it was made.</summary>
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

        /// <summary>Feet position X (Revit internal metres).</summary>
        public double X { get; set; }

        /// <summary>Feet position Y (Revit internal metres).</summary>
        public double Y { get; set; }

        /// <summary>Feet position Z (Revit internal metres).</summary>
        public double Z { get; set; }

        /// <summary>Look direction about +Z (radians, 0 = +X, counter-clockwise).</summary>
        public float Yaw { get; set; }

        /// <summary>Look pitch (radians, positive = up).</summary>
        public float Pitch { get; set; }

        /// <summary>True if the player was flying (no-clip) when it was saved.</summary>
        public bool Flying { get; set; }

        /// <summary>The level name at the time (for the list; informational).</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>
        /// The sun's date and time when the bookmark was saved with shadows on (GO restores it and turns shadows on),
        /// else null.
        /// </summary>
        public SunTime Sun { get; set; }

        /// <summary>
        /// The section cut when the bookmark was set (section box round): GO restores it; a cut with nothing on
        /// clears the cut; null (older bookmarks) leaves the current cut alone.
        /// </summary>
        public Scene.SectionCut Section { get; set; }

        /// <summary>
        /// A small picture of the view (base64 JPEG, about 192 × 108 px, a few kB) for the bookmark list, or null.
        /// </summary>
        public string Thumbnail { get; set; }

        /// <summary>Scene-local feet position (not serialised).</summary>
        [JsonIgnore]
        public Vector3 Local { get; set; }

        /// <summary>Pre-formatted "Level · author · date" line for lists (not serialised).</summary>
        [JsonIgnore]
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// A set of bookmarks (bookmarks.json inside a .bimgo, or the sidecar beside a Revit model). Order is the user's
    /// order: Ctrl+1..9 jump to the first nine.
    /// </summary>
    public sealed class BookmarkDocument
    {
        public int Version { get; set; } = 1;
        public string Model { get; set; } = string.Empty;
        public string Units { get; set; } = "metres, Revit internal coordinates; angles in radians";
        public List<BookmarkRecord> Bookmarks { get; set; } = new();

        /// <summary>
        /// The saved home viewpoint (Shift+H / SET HOME HERE): walkthroughs of this model start here and H returns
        /// here. Null until a home is set.
        /// </summary>
        public BookmarkRecord Home { get; set; }

        /// <summary>True when there is nothing to save (no bookmarks and no home).</summary>
        [JsonIgnore]
        public bool IsEmpty => (Bookmarks?.Count ?? 0) == 0 && Home == null;

        /// <summary>
        /// Drops null and unusable records (no name, non-finite position). Returns the document for chaining.
        /// </summary>
        public BookmarkDocument Clean()
        {
            Bookmarks ??= new List<BookmarkRecord>();
            Bookmarks.RemoveAll(b => b == null || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Z)
                || !float.IsFinite(b.Yaw) || !float.IsFinite(b.Pitch));
            if (Home != null && (!double.IsFinite(Home.X) || !double.IsFinite(Home.Y) || !double.IsFinite(Home.Z)
                || !float.IsFinite(Home.Yaw) || !float.IsFinite(Home.Pitch)))
            {
                Home = null;
            }
            foreach (BookmarkRecord bookmark in Bookmarks)
            {
                if (string.IsNullOrWhiteSpace(bookmark.Name)) { bookmark.Name = "Viewpoint"; }
                bookmark.Id = string.IsNullOrWhiteSpace(bookmark.Id) ? Guid.NewGuid().ToString("N") : bookmark.Id;
                bookmark.Section?.Clean();
            }
            return this;
        }
    }
}
