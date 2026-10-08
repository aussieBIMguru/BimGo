// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// One CC0 proxy texture keyword: which material names and schemas it suits, and the real-world size one repeat of
    /// its image covers.
    /// </summary>
    /// <param name="Keyword">The keyword stored in <see cref="SceneMaterial.Proxy"/> (lower case, e.g. "brick").</param>
    /// <param name="Label">A short label for menus ("Brick").</param>
    /// <param name="Aliases">Material name fragments that suggest it, matched at the start of a word ("oak" matches
    /// "Oak floor" and "BG_Oak_01", not "Cloak").</param>
    /// <param name="Schemas">Appearance schema name fragments that suggest it when the name doesn't (e.g. "Masonry").</param>
    /// <param name="SizeU">Real-world width of one repeat (m), used when the material has no placement of its own.</param>
    /// <param name="SizeV">Real-world height of one repeat (m).</param>
    public sealed record ProxyKeyword(string Keyword, string Label, string[] Aliases, string[] Schemas, float SizeU, float SizeV);

    /// <summary>
    /// The CC0 proxy keyword list, shared by the Revit add-in (suggestions at extraction) and the app (suggestions in
    /// the Textures panel and the image lookup). Only keywords live here: the images ship with the app
    /// (Resources/Proxies + proxies.json), never inside a .bimgo.
    /// <para>Suggestion order matters: the first entry with a matching alias wins, so specific entries ("concrete
    /// block", "board formed", "carpet tile") come before general ones ("concrete", "tile"). Names that should never get
    /// a proxy (glass, mirrors, acoustic ceiling tiles) are caught first by <see cref="NEVER"/>.</para>
    /// </summary>
    public static class ProxyCatalog
    {
        /// <summary>Name fragments that never get a proxy suggestion (see-through, mirrors, acoustic tiles).</summary>
        private static readonly string[] NEVER = { "glass", "glazing", "mirror", "acoustic", "ceiling tile", "light source", "lamp", "lens" };

        /// <summary>The keywords, in suggestion order.</summary>
        public static readonly IReadOnlyList<ProxyKeyword> ALL = new[]
        {
            new ProxyKeyword("carpet", "Carpet", new[] { "carpet", "rug" }, new string[0], 2.0f, 2.0f),
            new ProxyKeyword("blockwork", "Blockwork", new[] { "block", "blockwork", "cmu", "besser" }, new[] { "MasonryCMU" }, 1.2f, 1.8f),
            new ProxyKeyword("brick", "Brick", new[] { "brick", "masonry" }, new[] { "Masonry" }, 0.9f, 1.38f),
            new ProxyKeyword("concrete-board", "Concrete, board-formed", new[] { "board formed", "boardformed", "board marked", "off form timber" }, new string[0], 2.4f, 2.4f),
            new ProxyKeyword("concrete", "Concrete", new[] { "concrete", "precast", "screed", "slab", "off form" }, new[] { "Concrete" }, 2.4f, 1.2f),
            new ProxyKeyword("render", "Render / plaster", new[] { "render", "plaster", "stucco", "bagged" }, new string[0], 2.0f, 2.0f),
            new ProxyKeyword("plywood", "Plywood", new[] { "ply", "plywood", "osb", "particleboard", "mdf" }, new string[0], 1.2f, 1.2f),
            new ProxyKeyword("timber-floor", "Timber floor", new[] { "floorboard", "timber floor", "wood floor", "hardwood floor", "parquet" }, new[] { "Hardwood" }, 1.2f, 1.2f),
            new ProxyKeyword("timber-panel", "Timber panel", new[] { "timber", "wood", "oak", "walnut", "maple", "pine", "cedar", "birch", "beech", "teak", "spotted gum", "blackbutt", "veneer", "cladding" }, new string[0], 1.2f, 1.2f),
            new ProxyKeyword("vinyl", "Vinyl", new[] { "vinyl", "lino", "linoleum", "rubber", "marmoleum" }, new[] { "PlasticVinyl" }, 1.0f, 1.0f),
            new ProxyKeyword("tile-600", "Tile 600", new[] { "tile 600", "600x600", "600 x 600", "600x1200", "large format", "porcelain" }, new string[0], 4.8f, 4.8f),
            new ProxyKeyword("tile-300", "Tile 300", new[] { "tile", "tiles", "ceramic", "mosaic", "terrazzo" }, new[] { "Ceramic" }, 3.6f, 3.6f),
            new ProxyKeyword("marble-brushed", "Marble, figured", new[] { "figured marble", "feature marble", "onyx" }, new string[0], 1.5f, 1.5f),
            new ProxyKeyword("marble", "Marble", new[] { "marble", "quartz", "granite", "caesarstone" }, new string[0], 1.5f, 1.5f),
            new ProxyKeyword("stone", "Stone", new[] { "stone", "sandstone", "limestone", "bluestone", "slate", "travertine", "basalt", "rock" }, new[] { "Stone" }, 2.0f, 2.0f),
            new ProxyKeyword("metal-galvanised", "Metal, galvanised", new[] { "galv", "galvanised", "galvanized", "zinc", "corrugated", "colorbond", "colourbond", "zincalume", "sheet metal" }, new string[0], 2.0f, 2.0f),
            new ProxyKeyword("metal-brushed", "Metal, brushed", new[] { "metal", "steel", "stainless", "aluminium", "aluminum", "brushed", "chrome", "brass", "copper" }, new[] { "Metal" }, 1.0f, 1.0f),
            new ProxyKeyword("gravel", "Gravel", new[] { "gravel", "pebble", "aggregate", "ballast", "crushed rock" }, new string[0], 1.5f, 1.5f),
            new ProxyKeyword("grass", "Grass", new[] { "grass", "turf", "lawn" }, new string[0], 2.0f, 2.0f),
            new ProxyKeyword("asphalt", "Asphalt", new[] { "asphalt", "bitumen", "road", "hotmix", "tarmac" }, new string[0], 2.5f, 2.5f),
            new ProxyKeyword("fabric", "Fabric", new[] { "fabric", "upholstery", "textile", "cloth", "linen", "felt", "curtain" }, new string[0], 0.3f, 0.3f)
        };

        private static readonly Dictionary<string, ProxyKeyword> BY_KEYWORD = ALL.ToDictionary(k => k.Keyword, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The keyword entry, or null if unknown.
        /// </summary>
        public static ProxyKeyword Find(string keyword) =>
            !string.IsNullOrWhiteSpace(keyword) && BY_KEYWORD.TryGetValue(keyword.Trim(), out ProxyKeyword entry) ? entry : null;

        /// <summary>
        /// A keyword cleaned for storage: trimmed and lower case, or null when empty. Unknown keywords are kept (a newer
        /// app may know them), only cleaned.
        /// </summary>
        public static string Normalise(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) { return null; }
            string value = keyword.Trim().ToLowerInvariant();
            return value.Length > 64 ? value.Substring(0, 64) : value;
        }

        /// <summary>
        /// Suggests a proxy keyword for a material: by name first (aliases, in list order), then by appearance schema.
        /// Null when nothing clearly suits (the material then keeps its plain colour).
        /// </summary>
        /// <param name="materialName">The Revit material name.</param>
        /// <param name="schema">The appearance schema (GenericSchema, MasonrySchema…), or null.</param>
        public static string Suggest(string materialName, string schema)
        {
            string name = " " + NormaliseName(materialName) + " ";
            if (name.Trim().Length > 0)
            {
                foreach (string never in NEVER)
                {
                    if (name.Contains(" " + never, StringComparison.Ordinal)) { return null; }
                }
                foreach (ProxyKeyword entry in ALL)
                {
                    foreach (string alias in entry.Aliases)
                    {
                        if (name.Contains(" " + alias, StringComparison.Ordinal)) { return entry.Keyword; }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(schema))
            {
                // Most specific schema fragment first (MasonryCMU before Masonry)
                ProxyKeyword best = null;
                int bestLength = 0;
                foreach (ProxyKeyword entry in ALL)
                {
                    foreach (string fragment in entry.Schemas)
                    {
                        if (fragment.Length > bestLength && schema.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                        {
                            best = entry;
                            bestLength = fragment.Length;
                        }
                    }
                }
                return best?.Keyword;
            }
            return null;
        }

        /// <summary>
        /// A material name as words: lower case, with every run of non-letters / non-digits turned into one space, and
        /// camel-case humps split ("BG_CarpetPlain1" → "bg carpet plain1").
        /// </summary>
        internal static string NormaliseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return string.Empty; }
            var builder = new System.Text.StringBuilder(name.Length + 8);
            char previous = ' ';
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (char.IsUpper(c) && char.IsLower(previous)) { builder.Append(' '); }
                    builder.Append(char.ToLowerInvariant(c));
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                }
                previous = c;
            }
            return builder.ToString().Trim();
        }
    }
}
