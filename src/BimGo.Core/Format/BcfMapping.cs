using System.Security.Cryptography;
using System.Text;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Which coordinates BCF viewpoints are written in (and read back with).
    /// </summary>
    public enum BcfCoordinates
    {
        /// <summary>Shared (survey) coordinates: the usual Revit IFC export setting (default).</summary>
        Shared = 0,

        /// <summary>Relative to the project base point, project-north axes.</summary>
        Project = 1,

        /// <summary>Revit's internal coordinates.</summary>
        Internal = 2
    }

    /// <summary>
    /// Converts Revit internal metres to and from a BCF coordinate system (<see cref="BcfCoordinates"/>), in double
    /// precision. Directions only rotate.
    /// </summary>
    public readonly struct BcfFrame
    {
        private readonly SiteCoordinates.SharedTransform _shared;
        private readonly double _baseX, _baseY, _baseZ;

        private BcfFrame(BcfCoordinates kind, SiteCoordinates.SharedTransform shared, double baseX, double baseY, double baseZ)
        {
            Kind = kind;
            _shared = shared;
            _baseX = baseX;
            _baseY = baseY;
            _baseZ = baseZ;
        }

        /// <summary>The coordinates actually used (may differ from the wanted ones, see <see cref="Resolve"/>).</summary>
        public BcfCoordinates Kind { get; }

        /// <summary>
        /// The frame for a model: the wanted coordinates when the site supports them, else internal.
        /// </summary>
        /// <param name="site">The model's site (may be null).</param>
        /// <param name="wanted">The coordinates asked for.</param>
        /// <param name="fellBack">True when the wanted coordinates weren't available (internal used instead).</param>
        public static BcfFrame Resolve(SiteInfo site, BcfCoordinates wanted, out bool fellBack)
        {
            fellBack = false;
            if (wanted == BcfCoordinates.Shared && SiteCoordinates.TryGetShared(site, out SiteCoordinates.SharedTransform shared))
            {
                return new BcfFrame(BcfCoordinates.Shared, shared, 0, 0, 0);
            }
            if (wanted == BcfCoordinates.Project && SiteCoordinates.TryGetProjectBase(site, out double x, out double y, out double z))
            {
                return new BcfFrame(BcfCoordinates.Project, default, x, y, z);
            }
            fellBack = wanted != BcfCoordinates.Internal;
            return new BcfFrame(BcfCoordinates.Internal, default, 0, 0, 0);
        }

        /// <summary>An internal point (metres) in BCF coordinates.</summary>
        public BcfVector PointToBcf(double x, double y, double z)
        {
            switch (Kind)
            {
                case BcfCoordinates.Shared:
                    _shared.Apply(x, y, z, out double east, out double north, out double elevation);
                    return new BcfVector(east, north, elevation);
                case BcfCoordinates.Project:
                    return new BcfVector(x - _baseX, y - _baseY, z - _baseZ);
                default:
                    return new BcfVector(x, y, z);
            }
        }

        /// <summary>A BCF point back in internal metres.</summary>
        public void PointFromBcf(BcfVector v, out double x, out double y, out double z)
        {
            switch (Kind)
            {
                case BcfCoordinates.Shared:
                    double dx = v.X - _shared.East, dy = v.Y - _shared.North;
                    x = dx * _shared.Cos + dy * _shared.Sin;
                    y = -dx * _shared.Sin + dy * _shared.Cos;
                    z = v.Z - _shared.Elevation;
                    break;
                case BcfCoordinates.Project:
                    x = v.X + _baseX;
                    y = v.Y + _baseY;
                    z = v.Z + _baseZ;
                    break;
                default:
                    x = v.X;
                    y = v.Y;
                    z = v.Z;
                    break;
            }
        }

        /// <summary>An internal direction in BCF coordinates.</summary>
        public BcfVector DirectionToBcf(double x, double y, double z)
        {
            if (Kind != BcfCoordinates.Shared) { return new BcfVector(x, y, z); }
            return new BcfVector(x * _shared.Cos - y * _shared.Sin, x * _shared.Sin + y * _shared.Cos, z);
        }

        /// <summary>A BCF direction back in internal axes.</summary>
        public void DirectionFromBcf(BcfVector v, out double x, out double y, out double z)
        {
            if (Kind != BcfCoordinates.Shared)
            {
                x = v.X;
                y = v.Y;
                z = v.Z;
                return;
            }
            x = v.X * _shared.Cos + v.Y * _shared.Sin;
            y = -v.X * _shared.Sin + v.Y * _shared.Cos;
            z = v.Z;
        }
    }

    /// <summary>
    /// BimGo comments ↔ BCF topics: issue fields, the reply thread, the viewpoint's camera and the merge rules of an
    /// import (BCF / Revizto / ACC… words for status and priority are mapped generously). Pure functions, no IO.
    /// </summary>
    public static class BcfMapping
    {
        /// <summary>Longest topic title written (the full text goes in the description).</summary>
        public const int TITLE_LENGTH = 60;

        /// <summary>BCF 2.1 limits the field of view to 45–60°.</summary>
        public const double MIN_FOV = 45, MAX_FOV = 60;

        /// <summary>Who an imported change is credited to when the file doesn't say.</summary>
        public const string IMPORT_AUTHOR = "BCF import";

        #region Status and priority

        /// <summary>BimGo status → BCF ("Open", "In Progress", "Closed").</summary>
        public static string StatusToBcf(string status) => CommentStatus.Normalise(status) switch
        {
            CommentStatus.IN_PROGRESS => "In Progress",
            CommentStatus.CLOSED => "Closed",
            _ => "Open"
        };

        /// <summary>
        /// A BCF status → BimGo: closed / resolved / done / fixed / complete → closed; in progress / active / assigned
        /// / review / pending → in progress; anything else (open, new, reopened, blank) → open.
        /// </summary>
        public static string StatusFromBcf(string status)
        {
            string s = (status ?? string.Empty).Trim().ToLowerInvariant();
            if (s.Length == 0) { return CommentStatus.OPEN; }
            if (s.Contains("reopen") || s == "open" || s == "new") { return CommentStatus.OPEN; }
            if (s.Contains("close") || s.Contains("resolv") || s.Contains("done") || s.Contains("fixed") || s.Contains("complete") || s.Contains("approved"))
            {
                return CommentStatus.CLOSED;
            }
            if (s.Contains("progress") || s.Contains("active") || s.Contains("assigned") || s.Contains("review") || s.Contains("pending") || s.Contains("answered"))
            {
                return CommentStatus.IN_PROGRESS;
            }
            return CommentStatus.OPEN;
        }

        /// <summary>BimGo priority → BCF ("Low", "Normal", "High").</summary>
        public static string PriorityToBcf(string priority) => CommentPriority.Normalise(priority) switch
        {
            CommentPriority.LOW => "Low",
            CommentPriority.HIGH => "High",
            _ => "Normal"
        };

        /// <summary>
        /// A BCF priority → BimGo: critical / high / major / urgent / blocker → high; low / minor / trivial → low;
        /// anything else → normal.
        /// </summary>
        public static string PriorityFromBcf(string priority)
        {
            string p = (priority ?? string.Empty).Trim().ToLowerInvariant();
            if (p.Contains("critical") || p.Contains("high") || p.Contains("major") || p.Contains("urgent") || p.Contains("blocker")) { return CommentPriority.HIGH; }
            if (p.Contains("low") || p.Contains("minor") || p.Contains("trivial")) { return CommentPriority.LOW; }
            return CommentPriority.NORMAL;
        }

        #endregion

        #region Export

        /// <summary>
        /// The topic title for a comment: its first line, at most <see cref="TITLE_LENGTH"/> characters (… when cut).
        /// </summary>
        public static string TitleOf(string text)
        {
            string line = (text ?? string.Empty).Trim();
            int newline = line.IndexOfAny(new[] { '\r', '\n' });
            if (newline >= 0) { line = line[..newline].Trim(); }
            return line.Length <= TITLE_LENGTH ? line : line[..(TITLE_LENGTH - 1)].TrimEnd() + "…";
        }

        /// <summary>
        /// A comment as a topic: title (first line), description (full text), status, priority, assignee, dates and
        /// authors, and the replies as BCF comments. The viewpoint and snapshot are added by the caller.
        /// </summary>
        public static BcfTopic ToTopic(CommentRecord record)
        {
            Guid guid = Guid.TryParse(record.Id, out Guid id) ? id : DeterministicGuid("comment:" + record.Id);
            DateTimeOffset? modified = Latest(record.Edited, record.Updated);
            string modifiedBy = modified == null ? null : modified == record.Updated ? record.UpdatedBy : record.EditedBy;
            var topic = new BcfTopic
            {
                Guid = guid,
                TopicType = "Issue",
                Status = StatusToBcf(record.Status),
                Title = TitleOf(record.Text),
                Priority = PriorityToBcf(record.Priority),
                CreationDate = record.Created,
                CreationAuthor = record.Author ?? string.Empty,
                ModifiedDate = modified,
                ModifiedAuthor = modifiedBy,
                AssignedTo = record.AssignedTo,
                Description = record.Text
            };
            if (record.Replies != null)
            {
                foreach (CommentReply reply in record.Replies)
                {
                    if (reply == null || string.IsNullOrWhiteSpace(reply.Text)) { continue; }
                    topic.Comments.Add(new BcfComment
                    {
                        Guid = Guid.TryParse(reply.Id, out Guid replyId) ? replyId : DeterministicGuid("reply:" + record.Id + ":" + reply.Id),
                        Date = reply.Created,
                        Author = reply.Author ?? string.Empty,
                        Text = reply.Text
                    });
                }
            }
            return topic;
        }

        /// <summary>
        /// A comment's saved view as a BCF perspective camera: the eye (feet + eye height), the look direction from
        /// yaw / pitch, the up vector, and the vertical field of view of a 16:9 picture taken with the horizontal one
        /// (clamped to BCF 2.1's 45–60°).
        /// </summary>
        /// <param name="view">The saved view (Revit internal metres).</param>
        /// <param name="eyeHeight">Eye height above the feet (m).</param>
        /// <param name="frame">The BCF coordinates.</param>
        /// <param name="horizontalFovDegrees">The walkthrough's horizontal field of view.</param>
        public static BcfViewpoint ToViewpoint(CommentView view, double eyeHeight, BcfFrame frame, double horizontalFovDegrees)
        {
            double cy = Math.Cos(view.Yaw), sy = Math.Sin(view.Yaw);
            double cp = Math.Cos(view.Pitch), sp = Math.Sin(view.Pitch);
            double h = Math.Clamp(horizontalFovDegrees, 30, 150) * Math.PI / 180.0;
            double vertical = 2.0 * Math.Atan(Math.Tan(h * 0.5) * 9.0 / 16.0) * 180.0 / Math.PI;
            return new BcfViewpoint
            {
                Position = frame.PointToBcf(view.X, view.Y, view.Z + eyeHeight),
                Direction = frame.DirectionToBcf(cp * cy, cp * sy, sp),
                Up = frame.DirectionToBcf(-sp * cy, -sp * sy, cp),
                FieldOfView = Math.Round(Math.Clamp(vertical, MIN_FOV, MAX_FOV), 3)
            };
        }

        /// <summary>
        /// A viewpoint's camera as a BimGo view: feet (eye − eye height, internal metres), yaw and pitch from the look
        /// direction. Flying is left false (the caller decides from the floor under the feet).
        /// </summary>
        /// <returns>False when the camera is unusable.</returns>
        public static bool TryToView(BcfViewpoint viewpoint, double eyeHeight, BcfFrame frame, out CommentView view)
        {
            view = null;
            if (viewpoint == null || !viewpoint.Position.IsFinite || !viewpoint.Direction.IsFinite || viewpoint.Direction.Length < 1e-9) { return false; }
            frame.PointFromBcf(viewpoint.Position, out double x, out double y, out double z);
            frame.DirectionFromBcf(viewpoint.Direction, out double dx, out double dy, out double dz);
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= length;
            dy /= length;
            dz /= length;
            double flat = Math.Sqrt(dx * dx + dy * dy);
            view = new CommentView
            {
                X = Math.Round(x, 4),
                Y = Math.Round(y, 4),
                Z = Math.Round(z - eyeHeight, 4),
                Yaw = flat < 1e-6 ? 0f : (float)Math.Atan2(dy, dx),
                Pitch = (float)Math.Clamp(Math.Atan2(dz, flat), -1.5, 1.5)
            };
            return double.IsFinite(view.X) && double.IsFinite(view.Y) && double.IsFinite(view.Z);
        }

        /// <summary>
        /// A section cut (internal metres) as BCF clipping planes in the frame's coordinates (section box round).
        /// </summary>
        public static List<BcfClippingPlane> ToClippingPlanes(SectionCut cut, BcfFrame frame)
        {
            var planes = new List<BcfClippingPlane>();
            if (cut == null || !cut.IsActive) { return planes; }
            foreach ((System.Numerics.Vector3 point, System.Numerics.Vector3 direction) in cut.ToPlanes())
            {
                planes.Add(new BcfClippingPlane(frame.PointToBcf(point.X, point.Y, point.Z), frame.DirectionToBcf(direction.X, direction.Y, direction.Z)));
            }
            return planes;
        }

        /// <summary>
        /// BCF clipping planes back as a section cut (internal metres): a box when six axis-aligned planes make one,
        /// else the first plane (<see cref="SectionCut.FromPlanes"/>). Null when there are none.
        /// </summary>
        public static SectionCut ToSection(IReadOnlyList<BcfClippingPlane> planes, BcfFrame frame, out int dropped)
        {
            dropped = 0;
            if (planes == null || planes.Count == 0) { return null; }
            var internalPlanes = new List<(System.Numerics.Vector3, System.Numerics.Vector3)>(planes.Count);
            foreach (BcfClippingPlane plane in planes)
            {
                frame.PointFromBcf(plane.Location, out double x, out double y, out double z);
                frame.DirectionFromBcf(plane.Direction, out double dx, out double dy, out double dz);
                internalPlanes.Add((new System.Numerics.Vector3((float)x, (float)y, (float)z), new System.Numerics.Vector3((float)dx, (float)dy, (float)dz)));
            }
            return SectionCut.FromPlanes(internalPlanes, out dropped);
        }

        /// <summary>
        /// A stable GUID from text (MD5 of the UTF-8 bytes): BCF project ids from BimGo's model key, ids for records
        /// whose own id isn't a GUID.
        /// </summary>
        public static Guid DeterministicGuid(string text)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return new Guid(hash);
        }

        #endregion

        #region Import

        /// <summary>
        /// A new comment from a topic: id = the topic GUID (so a later import merges into it), text from the title and
        /// description, issue fields, dates, authors and the thread. The marker, view, level and element are set by
        /// the caller (they need the scene).
        /// </summary>
        public static CommentRecord ToComment(BcfTopic topic)
        {
            string text = TextOf(topic, out BcfComment usedComment);
            var record = new CommentRecord
            {
                Id = topic.Guid.ToString("N"),
                Author = string.IsNullOrWhiteSpace(topic.CreationAuthor) ? IMPORT_AUTHOR : topic.CreationAuthor.Trim(),
                Created = topic.CreationDate,
                Text = text,
                Status = StatusFromBcf(topic.Status),
                Priority = PriorityFromBcf(topic.Priority),
                AssignedTo = string.IsNullOrWhiteSpace(topic.AssignedTo) ? null : topic.AssignedTo.Trim(),
                Updated = topic.ModifiedDate,
                UpdatedBy = topic.ModifiedDate.HasValue ? (topic.ModifiedAuthor ?? IMPORT_AUTHOR) : null
            };
            foreach (BcfComment comment in topic.Comments)
            {
                if (ReferenceEquals(comment, usedComment) || IsSameText(comment.Text, text)) { continue; }
                (record.Replies ??= new List<CommentReply>()).Add(ToReply(comment));
            }
            return record.Clean();
        }

        /// <summary>
        /// Merges a topic into the comment it came from (same GUID): status, priority and assignee take the file's
        /// values, and replies the comment doesn't have yet (by GUID, or the same author, time and text) are added in
        /// date order. The text, marker, view and picture are left alone; nothing is deleted.
        /// </summary>
        /// <param name="record">The existing comment.</param>
        /// <param name="topic">The imported topic.</param>
        /// <param name="fieldsChanged">Out: true when status, priority or assignee changed.</param>
        /// <returns>The number of replies added.</returns>
        public static int Merge(CommentRecord record, BcfTopic topic, out bool fieldsChanged)
        {
            fieldsChanged = false;
            string status = StatusFromBcf(topic.Status);
            string priority = string.IsNullOrWhiteSpace(topic.Priority) ? record.Priority : PriorityFromBcf(topic.Priority);
            string assignee = string.IsNullOrWhiteSpace(topic.AssignedTo) ? null : topic.AssignedTo.Trim();
            if (status != record.Status || priority != record.Priority || assignee != record.AssignedTo)
            {
                record.Status = status;
                record.Priority = priority;
                record.AssignedTo = assignee;
                record.Updated = topic.ModifiedDate ?? DateTimeOffset.Now;
                record.UpdatedBy = topic.ModifiedAuthor ?? IMPORT_AUTHOR;
                record.UpdatedLabel = null;
                fieldsChanged = true;
            }

            int added = 0;
            foreach (BcfComment comment in topic.Comments)
            {
                if (IsSameText(comment.Text, record.Text) || HasReply(record, comment)) { continue; }
                (record.Replies ??= new List<CommentReply>()).Add(ToReply(comment));
                added++;
            }
            if (added > 0) { record.Replies.Sort((a, b) => a.Created.CompareTo(b.Created)); }
            return added;
        }

        /// <summary>
        /// The comment text for a topic: the description (with the title in front when the description doesn't
        /// already start with it), else the first comment, else the title.
        /// </summary>
        public static string TextOf(BcfTopic topic, out BcfComment usedComment)
        {
            usedComment = null;
            string title = (topic.Title ?? string.Empty).Trim();
            string description = (topic.Description ?? string.Empty).Trim();
            if (description.Length > 0)
            {
                string titleStem = title.TrimEnd('…').TrimEnd();
                if (title.Length == 0 || description.StartsWith(titleStem, StringComparison.OrdinalIgnoreCase)) { return description; }
                return title + " — " + description;
            }
            BcfComment first = topic.Comments.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Text));
            if (first != null)
            {
                usedComment = first;
                string text = first.Text.Trim();
                return title.Length == 0 || text.StartsWith(title.TrimEnd('…').TrimEnd(), StringComparison.OrdinalIgnoreCase) ? text : title + " — " + text;
            }
            return title.Length > 0 ? title : "(untitled issue)";
        }

        private static CommentReply ToReply(BcfComment comment) => new()
        {
            Id = comment.Guid.ToString("N"),
            Author = string.IsNullOrWhiteSpace(comment.Author) ? IMPORT_AUTHOR : comment.Author.Trim(),
            Created = comment.Date,
            Text = comment.Text.Trim()
        };

        private static bool HasReply(CommentRecord record, BcfComment comment)
        {
            if (record.Replies == null) { return false; }
            string id = comment.Guid.ToString("N");
            foreach (CommentReply reply in record.Replies)
            {
                if (string.Equals(reply.Id, id, StringComparison.OrdinalIgnoreCase)) { return true; }
                if (Guid.TryParse(reply.Id, out Guid replyGuid) && replyGuid == comment.Guid) { return true; }
                if (IsSameText(reply.Text, comment.Text) && string.Equals(reply.Author?.Trim(), comment.Author?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((reply.Created - comment.Date).TotalSeconds) < 2) { return true; }
            }
            return false;
        }

        private static bool IsSameText(string a, string b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.Ordinal);

        private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b)
        {
            if (a == null) { return b; }
            if (b == null) { return a; }
            return a.Value >= b.Value ? a : b;
        }

        #endregion
    }
}
