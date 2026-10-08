using BimGo.Edits;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Everything a .bimgo file holds, in memory: the immutable scene snapshot plus the parts that change while the
    /// file is open (comments and the edit journal) and the manifest metadata carried from save to save.
    /// </summary>
    public sealed class BimGoDocument
    {
        /// <summary>The geometry and model data (never modified after loading).</summary>
        public SceneData Scene { get; init; }

        /// <summary>The comments (may be empty, never null).</summary>
        public CommentDocument Comments { get; init; } = new();

        /// <summary>The ordered edits (may be empty, never null).</summary>
        public EditJournal Journal { get; init; } = new();

        /// <summary>Saved viewpoints (may be empty, never null).</summary>
        public BookmarkDocument Bookmarks { get; init; } = new();

        /// <summary>The sun / shadow state, or null if the file has none (defaults then come from the site).</summary>
        public SunSettings Sun { get; init; }

        /// <summary>What the walkthrough hides (categories, links, elements), or null if nothing.</summary>
        public VisibilitySettings Visibility { get; init; }

        /// <summary>
        /// The materials as changed in the walkthrough (Textures panel: picked images, scan hits, proxies), or null to
        /// write the scene's own (<see cref="SceneData.Materials"/>). The scene snapshot itself stays immutable.
        /// </summary>
        public MaterialData Materials { get; set; }

        /// <summary>When the geometry was extracted (UTC); kept across saves.</summary>
        public DateTime CreatedUtc { get; init; }

        /// <summary>How the file came about (<see cref="FileKinds"/>).</summary>
        public string Kind { get; init; } = FileKinds.EXPORT;

        /// <summary>Where the document was read from, or null if it has not been saved yet.</summary>
        public string Path { get; set; }

        /// <summary>The format version the file was read with (0 for a new document).</summary>
        public int ReadFormatVersion { get; init; }
    }

    /// <summary>
    /// Who is writing a file (recorded in the manifest).
    /// </summary>
    /// <param name="Generator">e.g. "BimGo for Revit 2026" or "BimGo".</param>
    /// <param name="Version">The writer's version string.</param>
    public readonly record struct WriterInfo(string Generator, string Version);
}
