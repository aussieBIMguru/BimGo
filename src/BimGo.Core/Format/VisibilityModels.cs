using System.Text.Json.Serialization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// What the walkthrough hides without editing the model: category and link toggles, elements hidden with the
    /// Scan gun (I), and the ground plane's height (pause menu). Saved as <c>visibility.json</c> in a .bimgo, or a sidecar beside the Revit model in live sessions.
    /// Everything is matched by stable keys (category keys, link instance UniqueIds, element UniqueIds), so it survives
    /// re-extraction.
    /// </summary>
    public sealed class VisibilitySettings
    {
        /// <summary>Catalog keys of loaded categories that are switched off.</summary>
        public List<string> HiddenCategories { get; set; } = new();

        /// <summary>Link instance UniqueIds of links that are switched off.</summary>
        public List<string> HiddenLinks { get; set; } = new();

        /// <summary>Elements hidden one by one.</summary>
        public List<HiddenElement> HiddenElements { get; set; } = new();

        /// <summary>
        /// The ground plane moved in the pause menu: metres above (+) or below (−) its default (100 mm under the
        /// lowest level), so it stays right after a re-extraction. Null = the default. Older readers ignore it.
        /// </summary>
        public float? GroundOffset { get; set; }

        /// <summary>The section cut (section box round), or null when nothing is cut. Older readers ignore it.</summary>
        public Scene.SectionCut Section { get; set; }

        /// <summary>Most the ground plane can move from its default (m), as the pause menu slider.</summary>
        public const float MAX_GROUND_OFFSET = 10f;

        /// <summary>True when nothing is hidden and the ground is at its default (the file then leaves the entry out).</summary>
        [JsonIgnore]
        public bool IsEmpty => (HiddenCategories?.Count ?? 0) == 0 && (HiddenLinks?.Count ?? 0) == 0 && (HiddenElements?.Count ?? 0) == 0
            && GroundOffset == null && Section == null;

        /// <summary>
        /// Drops null and blank entries (guards against hand-edited files).
        /// </summary>
        /// <returns>This instance.</returns>
        public VisibilitySettings Clean()
        {
            HiddenCategories = (HiddenCategories ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
            HiddenLinks = (HiddenLinks ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
            HiddenElements = (HiddenElements ?? new List<HiddenElement>()).Where(e => e != null && (!string.IsNullOrEmpty(e.UniqueId) || e.Id > 0)).ToList();
            if (GroundOffset is float ground)
            {
                GroundOffset = float.IsFinite(ground) && MathF.Abs(ground) > 1e-4f ? Math.Clamp(ground, -MAX_GROUND_OFFSET, MAX_GROUND_OFFSET) : null;
            }
            Section = Section?.IsActive == true ? Section.Clean() : null;
            return this;
        }
    }

    /// <summary>
    /// One element hidden in the walkthrough.
    /// </summary>
    public sealed class HiddenElement
    {
        /// <summary>The link instance's UniqueId for a linked element; null for the host.</summary>
        public string Link { get; set; }

        /// <summary>The element's UniqueId (the stable key).</summary>
        public string UniqueId { get; set; }

        /// <summary>The ElementId value (fallback for files without unique ids).</summary>
        public long Id { get; set; }
    }

    /// <summary>
    /// The visibility sidecar beside a Revit model (&lt;model&gt;.bimgo-visibility.json, next to the comments sidecar).
    /// Never throws.
    /// </summary>
    public static class VisibilityFiles
    {
        /// <summary>The visibility sidecar for a comments sidecar path, or null.</summary>
        public static string SidecarFor(string commentsSidecarPath) => SidecarJson.BesideComments(commentsSidecarPath, BimGoFormat.VISIBILITY_SIDECAR_SUFFIX);

        /// <summary>Reads the sidecar (null if missing or unreadable).</summary>
        public static VisibilitySettings Read(string path, out string error) => SidecarJson.Read<VisibilitySettings>(path, "Visibility", out error)?.Clean();

        /// <summary>Writes the sidecar.</summary>
        public static bool Write(string path, VisibilitySettings settings, out string error) => SidecarJson.Write(path, settings, "Visibility", out error);
    }
}
