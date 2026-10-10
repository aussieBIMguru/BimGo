// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// A point or direction in a BCF viewpoint (metres, in whichever coordinates the export used). Double precision:
    /// shared coordinates are often hundreds of kilometres.
    /// </summary>
    public readonly record struct BcfVector(double X, double Y, double Z)
    {
        /// <summary>True when every component is a finite number.</summary>
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

        /// <summary>Length.</summary>
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    }

    /// <summary>
    /// One element named by a viewpoint (BCF <c>Component</c>): its IFC GUID and / or the authoring tool's id (the
    /// Revit ElementId).
    /// </summary>
    public sealed class BcfComponent
    {
        /// <summary>The 22-character IFC GUID, or null.</summary>
        public string IfcGuid { get; set; }

        /// <summary>The authoring tool's id (Revit: the ElementId value as text), or null.</summary>
        public string AuthoringToolId { get; set; }

        /// <summary>The authoring tool ("Autodesk Revit 2026"), or null.</summary>
        public string OriginatingSystem { get; set; }
    }

    /// <summary>
    /// A BCF viewpoint: the camera (perspective, or orthogonal read as a camera position and direction) and the
    /// selected elements.
    /// </summary>
    public sealed class BcfViewpoint
    {
        public Guid Guid { get; set; } = Guid.NewGuid();
        public BcfVector Position { get; set; }
        public BcfVector Direction { get; set; } = new(1, 0, 0);
        public BcfVector Up { get; set; } = new(0, 0, 1);

        /// <summary>Vertical field of view (degrees); for orthogonal cameras, the view-to-world scale instead.</summary>
        public double FieldOfView { get; set; } = 60;

        /// <summary>True when the file had an orthogonal camera.</summary>
        public bool Orthogonal { get; set; }

        /// <summary>The selected elements (may be empty).</summary>
        public List<BcfComponent> Selection { get; set; } = new();

        /// <summary>Clipping planes (section box round; may be empty).</summary>
        public List<BcfClippingPlane> ClippingPlanes { get; set; } = new();
    }

    /// <summary>
    /// A BCF clipping plane: a point on it and its direction, which points into the half-space that is clipped.
    /// </summary>
    public readonly record struct BcfClippingPlane(BcfVector Location, BcfVector Direction);

    /// <summary>One comment in a topic's thread (BCF <c>Comment</c>).</summary>
    public sealed class BcfComment
    {
        public Guid Guid { get; set; } = Guid.NewGuid();
        public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
        public string Author { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset? ModifiedDate { get; set; }
        public string ModifiedAuthor { get; set; }
    }

    /// <summary>
    /// One BCF topic (an issue): the markup fields BimGo uses, its comments, the first viewpoint and its snapshot.
    /// Status and priority hold the file's own words (see <see cref="BcfMapping"/> for BimGo's).
    /// </summary>
    public sealed class BcfTopic
    {
        public Guid Guid { get; set; } = Guid.NewGuid();
        public string TopicType { get; set; }
        public string Status { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Priority { get; set; }
        public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.Now;
        public string CreationAuthor { get; set; } = string.Empty;
        public DateTimeOffset? ModifiedDate { get; set; }
        public string ModifiedAuthor { get; set; }
        public string AssignedTo { get; set; }
        public string Description { get; set; }

        /// <summary>The thread, oldest first.</summary>
        public List<BcfComment> Comments { get; set; } = new();

        /// <summary>The topic's (first) viewpoint, or null.</summary>
        public BcfViewpoint Viewpoint { get; set; }

        /// <summary>The viewpoint's snapshot image (PNG or JPEG bytes), or null.</summary>
        public byte[] Snapshot { get; set; }

        /// <summary>The snapshot's extension with the dot (".png" / ".jpg"); BimGo writes PNG.</summary>
        public string SnapshotExtension { get; set; } = ".png";
    }

    /// <summary>The project a BCF file belongs to (project.bcfp).</summary>
    public sealed class BcfProject
    {
        public string ProjectId { get; set; } = Guid.NewGuid().ToString("D");
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>What reading a BCF file gave.</summary>
    public sealed class BcfReadResult
    {
        /// <summary>The BCF version the file declares ("2.1", "3.0"…, or "?" when it has no bcf.version).</summary>
        public string Version { get; set; } = "?";

        /// <summary>The project, or null.</summary>
        public BcfProject Project { get; set; }

        /// <summary>The topics read (damaged topics are skipped and counted in <see cref="Skipped"/>).</summary>
        public List<BcfTopic> Topics { get; set; } = new();

        /// <summary>Topics that couldn't be read.</summary>
        public int Skipped { get; set; }
    }
}
