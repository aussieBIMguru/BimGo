using System.Reflection;
using Visual = Autodesk.Revit.DB.Visual;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// What a material's appearance says about its reflections, as the user set it up in Revit (never from keywords).
    /// <para><b>Strength</b> (0–1) is the intent: how strongly the surface reflects. It picks the tier (25 % steps).
    /// <b>Roughness</b> (0 mirror – 1 matt) is how blurred the reflection is, independent of the strength, so a strongly
    /// reflective but rough metal and a mirror can land in the same tier with a different blur.</para>
    /// </summary>
    internal sealed class ReflectivityInfo
    {
        /// <summary>Reflection strength, 0–1 (the intent; not a physical F0).</summary>
        public float Strength;

        /// <summary>Blur, 0 = mirror sharp, 1 = fully diffuse.</summary>
        public float Roughness = 1f;

        /// <summary>Metals tint their reflection by their own colour.</summary>
        public bool Metallic;

        /// <summary>See-through material (glass): keeps its own sky sheen path; a special case.</summary>
        public bool Glass;

        /// <summary>Revit Water schema (animated ripples in the app).</summary>
        public bool Water;

        /// <summary>Water type name (pool, river…) when <see cref="Water"/>, else empty.</summary>
        public string WaterType = string.Empty;

        /// <summary>Ripple strength (Revit <c>water_bump_amount</c>, 0.1 typical) when <see cref="Water"/>.</summary>
        public float WaterBump = 0.1f;

        /// <summary>True when a rule recognised the material; false = no reflection data (reflects nothing).</summary>
        public bool Mapped;

        /// <summary>A texture is connected to the property the value came from (the scalar is used; the map is not).</summary>
        public bool MapConnected;

        /// <summary>How the strength and roughness were decided (diagnostics, e.g. "wallpaint_finish=Gloss").</summary>
        public string Source = string.Empty;

        /// <summary>The strength rounded to the nearest quarter: 0, 1 (25 %), 2 (50 %), 3 (75 % +).</summary>
        public int Tier => ReflectivityReader.TierOf(Strength);
    }

    /// <summary>
    /// Reads reflection strength and roughness from an appearance asset, for every schema (reflection probes round).
    /// Rules, in order: Mirror schema → "mirror" in the material name → Water schema (or a see-through material named "water") → see-through (glass) → Prism (Advanced) → Generic → a finish enum on the simple
    /// schemas (Wall Paint, Ceramic, Stone, Concrete, Hardwood, Masonry/CMU, Metal, Metallic Paint, Plastic/Vinyl) →
    /// legacy presets with no readable properties (the material's graphics shininess, when raised above the default) →
    /// nothing (reflects nothing).
    /// <para>Stage 0 review (2026-10-08): tiers round to the nearest quarter instead of flooring; Prism roughness comes
    /// from the connected roughness map's average when there is one (its scalar is a library placeholder), with a
    /// steeper curve; a dark-tinted mirror is a glossy black surface, not a dark mirror; concrete "Custom" is mapped.</para>
    /// <para>Finish enums are matched by the Revit enum member name, found by reflection on the
    /// <c>Autodesk.Revit.DB.Visual</c> enums, so the table holds up across Revit years; the ordinal guesses are only the
    /// fallback when no enum type is found. Every value here is a first proposal, tuned against Gavin's scans.</para>
    /// </summary>
    internal static class ReflectivityReader
    {
        #region Tables

        /// <summary>Name fragments that make a property worth listing in the scan's REFLECTIVITY section.</summary>
        private static readonly string[] CANDIDATE_PARTS =
        {
            "reflect", "refl", "gloss", "rough", "finish", "application", "_f0", "ior", "is_metal", "mirror",
            "water", "sealant", "topcoat", "specular", "anisotropy"
        };

        /// <summary>Finish-style enum properties, in the order they are tried.</summary>
        private static readonly string[] FINISH_PROPERTIES =
        {
            "wallpaint_finish", "ceramic_application", "stone_application", "concrete_finish", "hardwood_finish",
            "masonrycmu_application", "masonry_application", "metal_finish", "metallicpaint_topcoat",
            "plasticvinyl_application"
        };

        /// <summary>Fallback ordinal → member name when Revit's enum type can't be found by reflection (best guesses).</summary>
        private static readonly Dictionary<string, string[]> ORDINAL_GUESS = new(StringComparer.OrdinalIgnoreCase)
        {
            ["wallpaint_finish"] = new[] { "flat", "eggshell", "platinum", "pearl", "semigloss", "gloss" },
            ["ceramic_application"] = new[] { "highglossy", "satin", "matte" },
            ["stone_application"] = new[] { "polished", "glossy", "matte", "unfinished" },
            ["concrete_finish"] = new[] { "straightbroom", "curvedbroom", "smooth", "polished", "custom" },
            ["concrete_sealant"] = new[] { "none", "epoxy", "acryl" },
            ["hardwood_finish"] = new[] { "gloss", "semigloss", "satin", "unfinished" },
            ["masonrycmu_application"] = new[] { "glossy", "matte", "unfinished" },
            ["masonry_application"] = new[] { "glossy", "matte", "unfinished" },
            ["metal_finish"] = new[] { "polished", "semipolished", "satin", "brushed" },
            ["metallicpaint_topcoat"] = new[] { "carpaint", "chrome", "matte" },
            ["plasticvinyl_application"] = new[] { "polished", "glossy", "matte" },
            ["water_type"] = new[] { "swimmingpool", "reflectingpool", "river", "lake", "ocean" }
        };

        /// <summary>
        /// Finish → (strength, roughness). Keys are "property:member" (an override for one schema) or just "member"
        /// (lower case, no spaces or underscores). Plasterboard paint (flat / eggshell) reflects nothing; gloss paint,
        /// gloss tiles and polished stone land at 50 %; polished metal and chrome at 75 %.
        /// </summary>
        private static readonly Dictionary<string, (float Strength, float Roughness)> FINISH_TABLE = new(StringComparer.OrdinalIgnoreCase)
        {
            // Shared
            ["flat"] = (0.00f, 0.90f),
            ["eggshell"] = (0.05f, 0.80f),
            ["platinum"] = (0.10f, 0.70f),
            ["pearl"] = (0.15f, 0.60f),
            ["semigloss"] = (0.30f, 0.40f),
            ["gloss"] = (0.55f, 0.15f),
            ["glossy"] = (0.50f, 0.15f),
            ["highglossy"] = (0.55f, 0.10f),
            ["satin"] = (0.15f, 0.55f),
            ["matte"] = (0.05f, 0.80f),
            ["unfinished"] = (0.00f, 0.90f),
            ["polished"] = (0.60f, 0.05f),
            ["smooth"] = (0.10f, 0.60f),
            ["straightbroom"] = (0.00f, 0.90f),
            ["curvedbroom"] = (0.00f, 0.90f),
            ["stamped"] = (0.05f, 0.80f),

            // Per schema
            ["ceramic_application:satin"] = (0.30f, 0.40f),     // glazed satin tile still has a sheen
            ["concrete_finish:polished"] = (0.35f, 0.25f),      // polished concrete: 25 %, more with a sealant
            ["concrete_finish:custom"] = (0.00f, 0.90f),        // a custom bump: no sheen information (a sealant still adds one)
            ["metal_finish:polished"] = (0.85f, 0.05f),
            ["metal_finish:semipolished"] = (0.70f, 0.20f),
            ["metal_finish:satin"] = (0.55f, 0.40f),
            ["metal_finish:brushed"] = (0.50f, 0.50f),
            ["metallicpaint_topcoat:carpaint"] = (0.50f, 0.10f),
            ["metallicpaint_topcoat:chrome"] = (0.85f, 0.05f),
            ["metallicpaint_topcoat:matte"] = (0.10f, 0.60f)
        };

        /// <summary>Concrete sealant → (strength added, roughness removed).</summary>
        private static readonly Dictionary<string, (float Strength, float Roughness)> SEALANT_TABLE = new(StringComparer.OrdinalIgnoreCase)
        {
            ["none"] = (0f, 0f),
            ["epoxy"] = (0.20f, 0.20f),
            ["acryl"] = (0.10f, 0.10f),
            ["acrylic"] = (0.10f, 0.10f)
        };

        /// <summary>
        /// Prism (Advanced) dielectrics: strength = this × (1 − roughness)⁴. Rounded to tiers: roughness ≤ 0.1 → 50 %,
        /// ≤ 0.32 → 25 %, rougher → 0. (Library paints at 0.08 reflect at 50 %; "Default Wall" at 0.12 lands at 25 %.)
        /// </summary>
        private const float PRISM_DIELECTRIC_MAX = 0.6f;

        /// <summary>Exponent of the Prism dielectric curve (see <see cref="PRISM_DIELECTRIC_MAX"/>).</summary>
        private const int PRISM_CURVE_POWER = 4;

        /// <summary>Roughness used when a roughness map is connected but can't be read (its scalar is a placeholder).</summary>
        private const float UNREADABLE_MAP_ROUGHNESS = 0.5f;

        /// <summary>A mirror tint darker than this (max RGB) is a glossy black surface (screens), not a dark mirror.</summary>
        private const double DARK_MIRROR_TINT = 0.3;

        /// <summary>Revit's default graphics shininess; legacy presets only shine when it was raised above this.</summary>
        private const int DEFAULT_SHININESS = 64;

        /// <summary>Mirror schema strength (75 % + tier).</summary>
        private const float MIRROR_STRENGTH = 0.9f;

        /// <summary>Water schema strength and base roughness (the ripples add the movement).</summary>
        private const float WATER_STRENGTH = 0.6f, WATER_ROUGHNESS = 0.05f;

        #endregion

        #region Read

        /// <summary>
        /// Reads the reflectivity of one appearance asset. Never throws: an unreadable asset reflects nothing.
        /// </summary>
        /// <param name="asset">The material's rendering asset.</param>
        /// <param name="schema">Its schema name (BaseSchema).</param>
        /// <param name="material">The material (transparency for the see-through test; shininess for legacy presets).</param>
        /// <param name="mapAverage">Average brightness (0–1) of a connected bitmap asset, or null when it can't be read.
        /// Used for Prism roughness maps; when omitted, a connected map counts as unreadable.</param>
        public static ReflectivityInfo Read(Visual.Asset asset, string schema, Material material, Func<Visual.Asset, double?> mapAverage = null)
        {
            var info = new ReflectivityInfo();
            try
            {
                if (asset == null) { info.Source = "no appearance asset"; return info; }
                schema ??= string.Empty;

                if (schema.Contains("Mirror", StringComparison.OrdinalIgnoreCase)) { return Mirror(asset, info, byName: false); }
                if (IsMirrorByName(material)) { return Mirror(asset, info, byName: true); }
                if (schema.Contains("Water", StringComparison.OrdinalIgnoreCase)) { return Water(asset, info); }
                if (IsSeeThrough(asset, schema, material))
                {
                    // Advanced (Prism) materials have no Water schema: a see-through material named "water" is water
                    return IsWaterByName(material) ? WaterByName(asset, info) : Glass(asset, info);
                }
                if (schema.StartsWith("Prism", StringComparison.OrdinalIgnoreCase)) { return Prism(asset, schema, info, mapAverage); }
                if (asset.FindByName("generic_reflectivity_at_0deg") != null) { return Generic(asset, info); }
                if (Finish(asset, schema, info)) { return info; }
                if (asset.Size == 0 && LegacyShininess(schema, material, info)) { return info; }

                info.Source = "no reflection data (legacy preset or unknown schema)";
            }
            catch (Exception ex)
            {
                info = new ReflectivityInfo { Source = $"unreadable: {ex.Message}" };
            }
            return info;
        }

        /// <summary>Same test as the glass sky sheen in build A/B: transparency, Glazing, Prism Transparent or Solid Glass.</summary>
        public static bool IsSeeThrough(Visual.Asset asset, string schema, Material material)
        {
            return (material?.Transparency ?? 0) > 0
                || (FindDouble(asset, "generic_transparency") ?? 0.0) > 0.01
                || schema.Contains("Glazing", StringComparison.OrdinalIgnoreCase)
                || schema.Contains("Transparent", StringComparison.OrdinalIgnoreCase)
                || schema.Contains("SolidGlass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The one keyword exception (agreed 2026-10-08): a material whose name contains "mirror" is a mirror whatever
        /// its schema, because mirrors are often modelled with a glass appearance. It wins over the glass rule, so a
        /// mirror never stays see-through in the app.
        /// </summary>
        public static bool IsMirrorByName(Material material)
        {
            try { return material?.Name?.Contains("mirror", StringComparison.OrdinalIgnoreCase) == true; }
            catch { return false; }
        }

        private static ReflectivityInfo Mirror(Visual.Asset asset, ReflectivityInfo info, bool byName)
        {
            info.Mapped = true;
            if (byName)
            {
                info.Strength = MIRROR_STRENGTH;
                info.Roughness = 0f;
                info.Metallic = true;   // tinted by the material colour (near white for a plain mirror)
                info.Source = "material name contains \"mirror\" (keyword rule; schema ignored, drawn opaque)";
                return info;
            }
            double tint = MaxComponent(asset, "mirror_tintcolor") ?? 1.0;
            if (tint < DARK_MIRROR_TINT)
            {
                // A near-black mirror (device screens, black glass): a glossy dark surface with an untinted sheen
                info.Strength = 0.5f;
                info.Roughness = 0.05f;
                info.Source = $"Mirror schema, dark tint {tint:0.##} → glossy black";
                return info;
            }
            info.Strength = MIRROR_STRENGTH;
            info.Roughness = 0f;
            info.Metallic = true;   // tinted by mirror_tintcolor (read as the colour)
            info.Source = $"Mirror schema, tint {tint:0.##}";
            return info;
        }

        /// <summary>
        /// Second keyword exception (agreed 2026-10-08): "water" in the name of a <b>see-through</b> material makes it
        /// water (Advanced materials have no Water schema). Opaque materials named water are left alone.
        /// </summary>
        public static bool IsWaterByName(Material material)
        {
            try { return material?.Name?.Contains("water", StringComparison.OrdinalIgnoreCase) == true; }
            catch { return false; }
        }

        private static ReflectivityInfo WaterByName(Visual.Asset asset, ReflectivityInfo info)
        {
            info.Water = true;
            info.Strength = WATER_STRENGTH;
            info.Roughness = WATER_ROUGHNESS;
            info.Mapped = true;
            info.Source = "see-through material named \"water\" (keyword rule)";
            return info;
        }

        private static ReflectivityInfo Water(Visual.Asset asset, ReflectivityInfo info)
        {
            info.Water = true;
            info.Strength = WATER_STRENGTH;
            info.Roughness = WATER_ROUGHNESS;
            info.Mapped = true;
            if (FindDouble(asset, "water_bump_amount") is double bump) { info.WaterBump = (float)Math.Clamp(bump, 0.0, 1.0); }
            info.WaterType = EnumText(asset, "water_type") ?? string.Empty;
            info.Source = "Water schema" + (info.WaterType.Length > 0 ? $", water_type={info.WaterType}" : "");
            return info;
        }

        private static ReflectivityInfo Glass(Visual.Asset asset, ReflectivityInfo info)
        {
            info.Glass = true;
            info.Mapped = true;
            string source = "see-through";
            double? value = FindDouble(asset, "glazing_reflectance");
            if (value != null) { source += ", glazing_reflectance"; }
            else if ((value = FindDouble(asset, "solidglass_reflectance")) != null) { source += ", solidglass_reflectance"; }
            else if ((value = FindDouble(asset, "generic_reflectivity_at_0deg")) != null) { source += ", generic_reflectivity_at_0deg"; }
            else if (FindDouble(asset, "transparent_ior") is double ior && ior > 1.0)
            {
                double r = (ior - 1.0) / (ior + 1.0);
                value = r * r;
                source += ", from transparent_ior";
            }
            else { source += ", default 0.06"; }

            info.Strength = (float)Math.Clamp(value ?? 0.06, 0.02, 0.6);
            info.Roughness = (float)Math.Clamp(FindDouble(asset, "surface_roughness") ?? 0.0, 0.0, 1.0);
            info.Source = source;
            return info;
        }

        private static ReflectivityInfo Prism(Visual.Asset asset, string schema, ReflectivityInfo info, Func<Visual.Asset, double?> mapAverage)
        {
            string roughName = FirstPresent(asset, "surface_roughness", "layered_roughness") ?? FirstEndingWith(asset, "_roughness");
            float roughness = (float)Math.Clamp((roughName == null ? null : FindDouble(asset, roughName)) ?? 0.5, 0.0, 1.0);
            string roughText = roughName == null ? "no roughness (0.5)" : $"{roughName} {roughness:0.##}";
            info.MapConnected = roughName != null && HasConnectedAsset(asset, roughName);
            if (info.MapConnected)
            {
                // The scalar is a library placeholder when a map drives roughness: use the map's average instead
                Visual.Asset bitmap = ConnectedAsset(asset, roughName);
                double? average = bitmap == null ? null : mapAverage?.Invoke(bitmap);
                if (average is double value)
                {
                    if (FindBool(bitmap, "unifiedbitmap_Invert") == true) { value = 1.0 - value; }
                    roughness = (float)Math.Clamp(value, 0.0, 1.0);
                    roughText = $"{roughName} map average {roughness:0.##}";
                }
                else
                {
                    roughness = Math.Max(roughness, UNREADABLE_MAP_ROUGHNESS);
                    roughText = $"{roughName} map unreadable → {roughness:0.##}";
                }
            }
            info.Roughness = roughness;
            info.Mapped = true;

            if (schema.Contains("Metal", StringComparison.OrdinalIgnoreCase))
            {
                // A metal's F0 is its colour; brighter metals reflect more, rougher ones a little less
                double f0 = MaxComponent(asset, "metal_f0") ?? 0.8;
                info.Metallic = true;
                info.Strength = (float)Math.Clamp(f0 * (1.0 - 0.5 * roughness), 0.0, 1.0);
                info.Source = $"Prism metal, metal_f0 max {f0:0.##}, {roughText}";
            }
            else
            {
                info.Strength = Math.Clamp(PRISM_DIELECTRIC_MAX * MathF.Pow(1f - roughness, PRISM_CURVE_POWER), 0f, 1f);
                info.Source = $"Prism dielectric, {roughText}";
            }
            return info;
        }

        private static ReflectivityInfo Generic(Visual.Asset asset, ReflectivityInfo info)
        {
            double reflect = FindDouble(asset, "generic_reflectivity_at_0deg") ?? 0.0;
            double gloss = FindDouble(asset, "generic_glossiness") ?? 0.0;
            info.Strength = (float)Math.Clamp(reflect, 0.0, 1.0);
            info.Roughness = (float)Math.Clamp(1.0 - gloss, 0.0, 1.0);
            info.Metallic = FindBool(asset, "generic_is_metal") == true;
            info.MapConnected = HasConnectedAsset(asset, "generic_reflectivity_at_0deg") || HasConnectedAsset(asset, "generic_glossiness");
            info.Mapped = true;
            info.Source = $"Generic, reflectivity_at_0deg {reflect:0.##}, glossiness {gloss:0.##}{(info.Metallic ? ", is_metal" : "")}" +
                          (info.MapConnected ? " (map connected; scalar used)" : "");
            return info;
        }

        private static bool Finish(Visual.Asset asset, string schema, ReflectivityInfo info)
        {
            foreach (string property in FINISH_PROPERTIES)
            {
                string member = EnumMember(asset, property);
                if (member == null) { continue; }

                if (!FINISH_TABLE.TryGetValue(property + ":" + member, out (float Strength, float Roughness) value)
                    && !FINISH_TABLE.TryGetValue(member, out value))
                {
                    info.Source = $"{property}={member}: not in the finish table";
                    return true; // recognised but unmapped: reflects nothing, shown in the scan
                }

                float strength = value.Strength, roughness = value.Roughness;
                string source = $"{property}={member}";

                if (property.StartsWith("concrete", StringComparison.OrdinalIgnoreCase)
                    && EnumMember(asset, "concrete_sealant") is string sealant
                    && SEALANT_TABLE.TryGetValue(sealant, out (float Strength, float Roughness) seal))
                {
                    strength += seal.Strength;
                    roughness -= seal.Roughness;
                    source += $", concrete_sealant={sealant}";
                }

                info.Strength = Math.Clamp(strength, 0f, 1f);
                info.Roughness = Math.Clamp(roughness, 0f, 1f);
                info.Metallic = property.StartsWith("metal_", StringComparison.OrdinalIgnoreCase)
                    || (property.StartsWith("metallicpaint", StringComparison.OrdinalIgnoreCase) && member == "chrome")
                    || schema.Equals("MetalSchema", StringComparison.OrdinalIgnoreCase);
                info.Mapped = true;
                info.Source = source;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Legacy presets with no readable properties (Metal-025, Paint-052…): the material's graphics shininess (0–128),
        /// only when raised above Revit's default of 64. 128 → 85 % sharp. Metallic when the preset is a Metal-… one.
        /// </summary>
        private static bool LegacyShininess(string schema, Material material, ReflectivityInfo info)
        {
            int shininess;
            try { shininess = material?.Shininess ?? 0; }
            catch { return false; }
            if (shininess <= DEFAULT_SHININESS) { return false; }

            float t = Math.Clamp((shininess - DEFAULT_SHININESS) / (128f - DEFAULT_SHININESS), 0f, 1f);
            info.Strength = 0.25f + 0.6f * t;
            info.Roughness = 1f - shininess / 128f;
            info.Metallic = schema.StartsWith("Metal", StringComparison.OrdinalIgnoreCase) && !schema.StartsWith("MetallicPaint", StringComparison.OrdinalIgnoreCase);
            info.Mapped = true;
            info.Source = $"legacy preset (no properties), graphics shininess {shininess}";
            return true;
        }

        #endregion

        #region Tiers

        /// <summary>The strength rounded to the nearest quarter: 0 (&lt; 12.5 %), 1 (25 %), 2 (50 %), 3 (75 % +, from 62.5 %).</summary>
        public static int TierOf(float strength) => Math.Clamp((int)Math.Floor(strength * 4f + 0.5f), 0, 3);

        /// <summary>A tier as text: "0", "25 %", "50 %", "75 % +".</summary>
        public static string TierText(int tier) => tier switch { 1 => "25 %", 2 => "50 %", 3 => "75 % +", _ => "0" };

        /// <summary>The blur as one of four words (the app uses roughness continuously; this is for reading the report).</summary>
        public static string BlurText(float roughness) => roughness switch
        {
            < 0.15f => "sharp",
            < 0.35f => "soft",
            < 0.6f => "blurred",
            _ => "very blurred"
        };

        #endregion

        #region Candidates and enum names

        /// <summary>True if a property's name suggests it bears on reflections (listed in the scan).</summary>
        public static bool IsCandidate(string name)
        {
            if (string.IsNullOrEmpty(name)) { return false; }
            foreach (string part in CANDIDATE_PARTS)
            {
                if (name.Contains(part, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// The Revit enum member name for an enum property's value (e.g. "Semigloss"), found by reflection on the
        /// <c>Autodesk.Revit.DB.Visual</c> enums whose name matches the property ("wallpaint_finish" ↔
        /// <c>WallPaintFinishType</c>). Null when no enum type matches.
        /// </summary>
        public static string RevitEnumName(string propertyName, int value)
        {
            Type type = EnumTypeFor(propertyName);
            if (type == null) { return null; }
            try
            {
                foreach (object member in Enum.GetValues(type))
                {
                    if (Convert.ToInt64(member) == value) { return Enum.GetName(type, member); }
                }
            }
            catch { /* fall through */ }
            return null;
        }

        /// <summary>The Revit enum type's name for a property, or null.</summary>
        public static string RevitEnumTypeName(string propertyName) => EnumTypeFor(propertyName)?.Name;

        /// <summary>The ordinal-guess member for a value (lower case), or null.</summary>
        public static string GuessedName(string propertyName, int value) =>
            ORDINAL_GUESS.TryGetValue(propertyName, out string[] names) && value >= 0 && value < names.Length ? names[value] : null;

        private static readonly Dictionary<string, Type> ENUM_TYPES = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object ENUM_LOCK = new();
        private static bool _enumTypesLoaded;

        private static Type EnumTypeFor(string propertyName)
        {
            lock (ENUM_LOCK)
            {
                if (!_enumTypesLoaded)
                {
                    _enumTypesLoaded = true;
                    try
                    {
                        foreach (Type type in typeof(Visual.Asset).Assembly.GetTypes())
                        {
                            if (!type.IsEnum || type.Namespace != "Autodesk.Revit.DB.Visual") { continue; }
                            string key = type.Name.ToLowerInvariant();
                            ENUM_TYPES[key] = type;
                            if (key.EndsWith("type", StringComparison.Ordinal) && key.Length > 4) { ENUM_TYPES.TryAdd(key[..^4], type); }
                        }
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        foreach (Type type in ex.Types)
                        {
                            if (type is { IsEnum: true, Namespace: "Autodesk.Revit.DB.Visual" }) { ENUM_TYPES[type.Name.ToLowerInvariant()] = type; }
                        }
                    }
                    catch { /* no names: the ordinal guesses are used */ }
                }
                string normalised = propertyName.Replace("_", string.Empty).ToLowerInvariant();
                return ENUM_TYPES.TryGetValue(normalised, out Type found) ? found : null;
            }
        }

        /// <summary>
        /// An enum property's member as a lower-case key without spaces or underscores ("semigloss"): the Revit enum
        /// name when found, else the ordinal guess, else "#n". Null when the property is absent.
        /// </summary>
        private static string EnumMember(Visual.Asset asset, string property)
        {
            int? value = FindInt(asset, property);
            if (value == null) { return null; }
            string name = RevitEnumName(property, value.Value) ?? GuessedName(property, value.Value) ?? "#" + value.Value;
            return name.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        private static string EnumText(Visual.Asset asset, string property)
        {
            int? value = FindInt(asset, property);
            if (value == null) { return null; }
            return RevitEnumName(property, value.Value) ?? GuessedName(property, value.Value) ?? "#" + value.Value;
        }

        #endregion

        #region Property helpers

        private static double? FindDouble(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyDouble d => d.Value,
            Visual.AssetPropertyFloat f => f.Value,
            _ => null
        };

        private static int? FindInt(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyInteger i => i.Value,
            Visual.AssetPropertyEnum e => e.Value,
            _ => null
        };

        private static bool? FindBool(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyBoolean b => b.Value,
            Visual.AssetPropertyInteger i => i.Value != 0,
            _ => null
        };

        /// <summary>The largest RGB component of a colour property (Double4 or Double3), or a plain number.</summary>
        private static double? MaxComponent(Visual.Asset asset, string name)
        {
            try
            {
                switch (asset.FindByName(name))
                {
                    case Visual.AssetPropertyDoubleArray4d c4:
                        IList<double> rgba = c4.GetValueAsDoubles();
                        if (rgba != null && rgba.Count >= 3) { return Math.Max(rgba[0], Math.Max(rgba[1], rgba[2])); }
                        break;
                    case Visual.AssetPropertyDoubleArray3d c3:
                        XYZ xyz = c3.GetValueAsXYZ();
                        if (xyz != null) { return Math.Max(xyz.X, Math.Max(xyz.Y, xyz.Z)); }
                        break;
                    case Visual.AssetPropertyDouble d:
                        return d.Value;
                    case Visual.AssetPropertyFloat f:
                        return f.Value;
                }
            }
            catch { /* unreadable */ }
            return null;
        }

        private static string FirstPresent(Visual.Asset asset, params string[] names)
        {
            foreach (string name in names)
            {
                if (asset.FindByName(name) != null) { return name; }
            }
            return null;
        }

        private static string FirstEndingWith(Visual.Asset asset, string suffix)
        {
            for (int i = 0; i < asset.Size; i++)
            {
                try
                {
                    string name = asset.Get(i)?.Name;
                    if (name != null && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return name; }
                }
                catch { /* skip */ }
            }
            return null;
        }

        private static Visual.Asset ConnectedAsset(Visual.Asset asset, string name)
        {
            try { return asset.FindByName(name)?.GetSingleConnectedAsset(); }
            catch { return null; }
        }

        /// <summary>
        /// The average brightness (0–1, raw values, no colour management) of an image file, read at 32 × 32. Used for
        /// roughness maps, which are data rather than colour. Null if the file can't be decoded.
        /// </summary>
        public static double? AverageLuminance(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using System.Drawing.Image image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                const int SIZE = 32;
                using var small = new System.Drawing.Bitmap(SIZE, SIZE, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(image, 0, 0, SIZE, SIZE);
                }
                double sum = 0.0;
                for (int y = 0; y < SIZE; y++)
                {
                    for (int x = 0; x < SIZE; x++)
                    {
                        System.Drawing.Color c = small.GetPixel(x, y);
                        sum += (c.R + c.G + c.B) / (3.0 * 255.0);
                    }
                }
                return sum / (SIZE * SIZE);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasConnectedAsset(Visual.Asset asset, string name)
        {
            try { return asset.FindByName(name) is { NumberOfConnectedProperties: > 0 }; }
            catch { return false; }
        }

        #endregion
    }
}
