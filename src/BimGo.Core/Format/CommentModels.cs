using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// One persisted comment (an issue): text, author, marker, and (comments upgrade) status, assignee, priority,
    /// replies, the viewpoint it was made from and a thumbnail of it. Coordinates are metres in Revit's internal
    /// coordinate system, so markers stay put across sessions regardless of the scene origin. Every newer field is
    /// optional: older files read as open, normal priority, unassigned, no replies, no view.
    /// Stored in the Revit-session sidecar (&lt;model&gt;.bimgo-comments.json) or in a .bimgo file's comments.json.
    /// </summary>
    public sealed class CommentRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Author { get; set; } = Environment.UserName;
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public string Text { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public long ElementId { get; set; } = -1;
        public string Level { get; set; } = string.Empty;

        /// <summary>When the text was last edited, or null if never.</summary>
        public DateTimeOffset? Edited { get; set; }

        /// <summary>Who last edited the text, or null.</summary>
        public string EditedBy { get; set; }

        /// <summary>Issue status: <see cref="CommentStatus"/> (absent in older files = open).</summary>
        public string Status { get; set; } = CommentStatus.OPEN;

        /// <summary>Who it is assigned to (free text), or null.</summary>
        public string AssignedTo { get; set; }

        /// <summary>Priority: <see cref="CommentPriority"/> (absent = normal).</summary>
        public string Priority { get; set; } = CommentPriority.NORMAL;

        /// <summary>When the status, assignee or priority last changed (null = never), and by whom.</summary>
        public DateTimeOffset? Updated { get; set; }

        /// <inheritdoc cref="Updated"/>
        public string UpdatedBy { get; set; }

        /// <summary>Follow-up replies, oldest first (null or empty = none).</summary>
        public List<CommentReply> Replies { get; set; }

        /// <summary>The viewpoint the comment was made from (GO returns there), or null (older comments).</summary>
        public CommentView View { get; set; }

        /// <summary>A small picture of that view (base64 JPEG, 192 × 108), or null.</summary>
        public string Thumbnail { get; set; }

        /// <summary>
        /// The commented element's Revit UniqueId (BCF round: lets BCF exports name the right element even when an
        /// ElementId repeats in a linked model), or null (older comments, or no element).
        /// </summary>
        public string ElementUniqueId { get; set; }

        /// <summary>
        /// The larger picture of the view used for BCF snapshots (BCF round): its name, "comments/&lt;id&gt;.jpg",
        /// inside the .bimgo or beside comments.json in the model folder. Null when there is none (older comments use
        /// their thumbnail).
        /// </summary>
        public string Snapshot { get; set; }

        /// <summary>The snapshot's JPEG bytes in memory (loaded with the comments, or just taken), or null.</summary>
        [JsonIgnore]
        public byte[] SnapshotData { get; set; }

        /// <summary>True when <see cref="SnapshotData"/> changed since it was last written beside a sidecar.</summary>
        [JsonIgnore]
        public bool SnapshotDirty { get; set; }

        /// <summary>Number of replies.</summary>
        [JsonIgnore]
        public int ReplyCount => Replies?.Count ?? 0;

        /// <summary>"Updated by … · date" for the detail view (made on first use, cleared on change; not serialised).</summary>
        [JsonIgnore]
        public string UpdatedLabel { get; set; }

        /// <summary>
        /// Normalises the issue fields (unknown status or priority → open / normal, blank assignee → null, empty reply
        /// list → null, blank replies dropped). Guards against hand-edited and older files.
        /// </summary>
        /// <returns>This record.</returns>
        public CommentRecord Clean()
        {
            Status = CommentStatus.Normalise(Status);
            Priority = CommentPriority.Normalise(Priority);
            AssignedTo = string.IsNullOrWhiteSpace(AssignedTo) ? null : AssignedTo.Trim();
            Replies = Replies?.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Text)).ToList();
            if (Replies != null && Replies.Count == 0) { Replies = null; }
            if (View != null && !(double.IsFinite(View.X) && double.IsFinite(View.Y) && double.IsFinite(View.Z) && float.IsFinite(View.Yaw) && float.IsFinite(View.Pitch)))
            {
                View = null;
            }
            View?.Section?.Clean();
            if (string.IsNullOrWhiteSpace(Thumbnail)) { Thumbnail = null; }
            if (string.IsNullOrWhiteSpace(ElementUniqueId)) { ElementUniqueId = null; }
            if (!CommentSnapshots.IsValidName(Snapshot)) { Snapshot = null; }
            return this;
        }

        /// <summary>Scene-local position (not serialised).</summary>
        [JsonIgnore]
        public Vector3 Local { get; set; }

        /// <summary>Pre-formatted "COMMENT · AUTHOR · date" header (not serialised).</summary>
        [JsonIgnore]
        public string Header { get; set; } = string.Empty;
    }

    /// <summary>Comment issue statuses (stored as these strings).</summary>
    public static class CommentStatus
    {
        public const string OPEN = "open";
        public const string IN_PROGRESS = "inProgress";
        public const string CLOSED = "closed";

        /// <summary>In display order.</summary>
        public static readonly string[] ALL = { OPEN, IN_PROGRESS, CLOSED };

        /// <summary>A known status (case-insensitive), else open.</summary>
        public static string Normalise(string status) =>
            ALL.FirstOrDefault(s => string.Equals(s, status?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? OPEN;

        /// <summary>"Open", "In progress", "Closed".</summary>
        public static string Label(string status) => Normalise(status) switch
        {
            IN_PROGRESS => "In progress",
            CLOSED => "Closed",
            _ => "Open"
        };
    }

    /// <summary>Comment priorities (stored as these strings).</summary>
    public static class CommentPriority
    {
        public const string LOW = "low";
        public const string NORMAL = "normal";
        public const string HIGH = "high";

        /// <summary>In display order.</summary>
        public static readonly string[] ALL = { LOW, NORMAL, HIGH };

        /// <summary>A known priority (case-insensitive), else normal.</summary>
        public static string Normalise(string priority) =>
            ALL.FirstOrDefault(p => string.Equals(p, priority?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? NORMAL;

        /// <summary>"Low", "Normal", "High".</summary>
        public static string Label(string priority) => Normalise(priority) switch
        {
            LOW => "Low",
            HIGH => "High",
            _ => "Normal"
        };
    }

    /// <summary>One reply in a comment's thread.</summary>
    public sealed class CommentReply
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Author { get; set; } = Environment.UserName;
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public string Text { get; set; } = string.Empty;

        /// <summary>"AUTHOR · dd MMM HH:mm" for lists (made on first use; not serialised).</summary>
        [JsonIgnore]
        public string Header { get; set; }
    }

    /// <summary>
    /// Where a comment was made from: the player's feet (Revit internal metres, like the marker), the view direction
    /// and whether the player was flying.
    /// </summary>
    public sealed class CommentView
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        /// <summary>Heading (radians, as the player's yaw).</summary>
        public float Yaw { get; set; }

        /// <summary>Pitch (radians).</summary>
        public float Pitch { get; set; }

        public bool Flying { get; set; }

        /// <summary>
        /// The section cut when the view was saved (section box round): GO restores it, BCF exports it as clipping
        /// planes; a cut with nothing on clears the cut; null (older comments) leaves the current cut alone.
        /// </summary>
        public Scene.SectionCut Section { get; set; }
    }

    /// <summary>
    /// A set of comments (the sidecar document, or comments.json inside a .bimgo).
    /// </summary>
    public sealed class CommentDocument
    {
        public int Version { get; set; } = 1;
        public string Model { get; set; } = string.Empty;
        public string Units { get; set; } = "metres, Revit internal coordinates";
        public List<CommentRecord> Comments { get; set; } = new();
    }
}
