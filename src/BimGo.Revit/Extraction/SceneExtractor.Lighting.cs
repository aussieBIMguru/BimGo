using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using BimGo.Scene;
using SceneLight = BimGo.Scene.LightSource;
using Visual = Autodesk.Revit.DB.Visual;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Artificial lighting capture: which surfaces glow and where lighting fixtures emit light.
    /// <list type="bullet">
    /// <item><b>Glow</b> comes from a material's Revit self-illumination (Generic "Self Illumination" luminance, filter
    /// colour and colour temperature; Advanced / Physical "Emission"), anywhere in the model. Inside Lighting Fixtures
    /// elements, materials whose name contains one of <see cref="LaunchSettings.EmissiveKeywords"/> glow too, and a
    /// raised fixture with neither gets its bottom, downward-facing faces as a guessed lens.</item>
    /// <item><b>Lights:</b> one per Lighting Fixtures element, at the centre of its glowing surface (just in front of
    /// it), pointing the way that surface faces; output and colour temperature from the fixture's parameters when they
    /// can be read ("Initial Intensity", "Initial Color"…), else estimated.</item>
    /// </list>
    /// Everything here is best effort: a failure leaves the element unlit, never fails the extraction.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Constants

        /// <summary>Glow strength of keyword-matched and guessed lenses (≈ a 2000 cd/m² diffuser).</summary>
        private const float KEYWORD_STRENGTH = 2.3f;

        /// <summary>A fixture whose bottom is this far above the level below counts as ceiling / wall mounted (m).</summary>
        private const float RAISED_FIXTURE = 1.2f;

        /// <summary>Output assumed when a fixture's parameters don't say (lm).</summary>
        private const float DEFAULT_LUMENS = 1000f;

        /// <summary>Colour temperature assumed when a fixture's parameters don't say (K).</summary>
        private const float DEFAULT_KELVIN = 3500f;

        private static readonly string[] LUMEN_PARAMETERS = { "Initial Intensity", "Luminous Flux", "Lumens", "Light Output", "Lamp Lumens" };
        private static readonly string[] KELVIN_PARAMETERS = { "Initial Color", "Initial Colour", "Color Temperature", "Colour Temperature", "CCT" };

        #endregion

        #region Fields

        private readonly string[] _emissiveKeywords;
        private float[] _levelElevations = Array.Empty<float>();

        // The element being extracted, when it is a lighting fixture
        private bool _fixture;
        private float? _fixtureLumens, _fixtureKelvin;
        private bool _fixtureGuessedLens;

        // Output
        private readonly List<EmissiveRun> _emissiveRuns = new();
        private readonly List<SceneLight> _lights = new();
        private int _selfIlluminatedMaterials, _guessedLenses, _estimatedLights;

        #endregion

        /// <summary>
        /// A material's colour and glow, cached per document.
        /// </summary>
        /// <param name="Colour">RGBA8 colour (transparency in alpha).</param>
        /// <param name="SelfIllumination">Packed glow from the appearance asset, or 0.</param>
        /// <param name="Keyword">True if the name contains an emissive keyword (counts inside lighting fixtures only).</param>
        /// <param name="Material">The material's index in the Realistic-mode table (<see cref="MaterialData.NONE"/> when
        /// textures aren't extracted or there is no material).</param>
        private readonly record struct MaterialLook(uint Colour, uint SelfIllumination, bool Keyword, ushort Material);

        #region Per element

        /// <summary>
        /// Notes whether the element is a lighting fixture and reads its output and colour temperature.
        /// </summary>
        private void BeginFixture(Element element)
        {
            _fixture = false;
            _fixtureLumens = _fixtureKelvin = null;
            _fixtureGuessedLens = false;
            try
            {
                _fixture = element.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures;
                if (!_fixture) { return; }
                Element type = _src.Doc.GetElement(element.GetTypeId());
                _fixtureLumens = ReadLumens(element, type);
                _fixtureKelvin = ReadKelvin(element, type);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: fixture {element.Id.Value} parameters unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// The glow a surface with this material gets in the current element (0 = none).
        /// </summary>
        private uint EmissiveOf(MaterialLook look)
        {
            if (look.SelfIllumination != 0u) { return look.SelfIllumination; }
            if (_fixture && look.Keyword) { return LightingData.PackEmissive(LightingData.KelvinToRgb(_fixtureKelvin ?? DEFAULT_KELVIN), KEYWORD_STRENGTH); }
            return 0u;
        }

        /// <summary>
        /// For a raised fixture with no glowing material: marks its downward-facing faces in the bottom 15 % (at least
        /// 3 cm) as the lens.
        /// </summary>
        private void MarkFallbackLens()
        {
            while (_tmpEmissive.Count < _tmpVertices.Count) { _tmpEmissive.Add(0u); }
            foreach (uint e in _tmpEmissive) { if (e != 0u) { return; } }
            if (_tmpOpaque.Count == 0) { return; }

            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (SceneVertex v in _tmpVertices)
            {
                minZ = MathF.Min(minZ, v.Position.Z);
                maxZ = MathF.Max(maxZ, v.Position.Z);
            }
            if (minZ - LevelBelow(minZ) < RAISED_FIXTURE) { return; }

            float limit = minZ + MathF.Max(0.03f, 0.15f * (maxZ - minZ));
            uint glow = LightingData.PackEmissive(LightingData.KelvinToRgb(_fixtureKelvin ?? DEFAULT_KELVIN), KEYWORD_STRENGTH);
            bool any = false;
            for (int i = 0; i + 2 < _tmpOpaque.Count; i += 3)
            {
                int a = _tmpOpaque[i], b = _tmpOpaque[i + 1], c = _tmpOpaque[i + 2];
                Vector3 pa = _tmpVertices[a].Position, pb = _tmpVertices[b].Position, pc = _tmpVertices[c].Position;
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                float length = cross.Length();
                if (length < 1e-10f || cross.Z / length > -0.7f) { continue; }
                if (MathF.Max(pa.Z, MathF.Max(pb.Z, pc.Z)) > limit) { continue; }
                _tmpEmissive[a] = _tmpEmissive[b] = _tmpEmissive[c] = glow;
                any = true;
            }
            if (any)
            {
                _fixtureGuessedLens = true;
                _guessedLenses++;
            }
        }

        /// <summary>
        /// Appends the element's glowing vertices as runs (merged with the previous run when contiguous and equal).
        /// </summary>
        /// <param name="vertexBase">Where the element's vertices start in the scene.</param>
        private void CommitEmissive(int vertexBase)
        {
            while (_tmpEmissive.Count < _tmpVertices.Count) { _tmpEmissive.Add(0u); }
            int i = 0;
            while (i < _tmpVertices.Count)
            {
                uint value = _tmpEmissive[i];
                if (value == 0u) { i++; continue; }
                int start = i;
                while (i < _tmpVertices.Count && _tmpEmissive[i] == value) { i++; }

                int sceneStart = vertexBase + start, count = i - start;
                if (_emissiveRuns.Count > 0)
                {
                    EmissiveRun last = _emissiveRuns[^1];
                    if (last.Emissive == value && last.Start + last.Count == sceneStart)
                    {
                        _emissiveRuns[^1] = last with { Count = last.Count + count };
                        continue;
                    }
                }
                _emissiveRuns.Add(new EmissiveRun(sceneStart, count, value));
            }
        }

        /// <summary>
        /// Adds the fixture's light: at the area-weighted centre of its glowing triangles, 5 cm in front of them, aimed
        /// the way they face; without any, near the top of the fixture and nearly omnidirectional (a floor or table lamp).
        /// </summary>
        /// <param name="elementIndex">The element's index in the snapshot.</param>
        /// <param name="bounds">The element's bounds (scene-local).</param>
        private void AddFixtureLight(int elementIndex, Aabb bounds)
        {
            Vector3 centroid = Vector3.Zero, normal = Vector3.Zero;
            float area = 0f;
            for (int i = 0; i + 2 < _tmpOpaque.Count; i += 3)
            {
                int a = _tmpOpaque[i], b = _tmpOpaque[i + 1], c = _tmpOpaque[i + 2];
                if (a >= _tmpEmissive.Count || _tmpEmissive[a] == 0u) { continue; }
                Vector3 pa = _tmpVertices[a].Position, pb = _tmpVertices[b].Position, pc = _tmpVertices[c].Position;
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);
                float twiceArea = cross.Length();
                if (twiceArea < 1e-10f) { continue; }
                centroid += (pa + pb + pc) / 3f * twiceArea;
                normal += cross;
                area += twiceArea;
            }

            Vector3 position;
            float downward;
            bool estimated = _fixtureGuessedLens || _fixtureLumens == null;
            if (area > 1e-8f)
            {
                centroid /= area;
                Vector3 facing = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : -Vector3.UnitZ;
                position = centroid + facing * 0.05f;
                downward = facing.Z < -0.5f ? 0.8f : facing.Z > 0.5f ? 0.15f : 0.4f;
            }
            else
            {
                if (!bounds.IsValid) { return; }
                Vector3 size = bounds.Size;
                position = new Vector3(bounds.Center.X, bounds.Center.Y, bounds.Max.Z - 0.15f * size.Z);
                downward = 0.2f;
                estimated = true;
            }

            _lights.Add(new SceneLight
            {
                Element = elementIndex,
                Position = position,
                Lumens = _fixtureLumens ?? DEFAULT_LUMENS,
                Kelvin = _fixtureKelvin ?? DEFAULT_KELVIN,
                Downward = downward,
                Estimated = estimated
            });
            if (estimated) { _estimatedLights++; }
        }

        /// <summary>
        /// The elevation of the highest level at or below a height (scene Z), or the lowest level.
        /// </summary>
        private float LevelBelow(float z)
        {
            float best = float.MinValue;
            foreach (float elevation in _levelElevations)
            {
                if (elevation <= z + 0.01f && elevation > best) { best = elevation; }
            }
            if (best == float.MinValue) { best = _levelElevations.Length > 0 ? _levelElevations[0] : z; }
            return best;
        }

        #endregion

        #region Snapshot

        /// <summary>
        /// The captured lighting for the snapshot (logged).
        /// </summary>
        private LightingData BuildLighting()
        {
            Utilities.Log_Utils.Write($"Lighting: {_lights.Count} fixture lights ({_estimatedLights} estimated), {_emissiveRuns.Count} glowing vertex runs, " +
                $"{_selfIlluminatedMaterials} self-illuminated materials, {_guessedLenses} guessed lenses.");
            return _emissiveRuns.Count == 0 && _lights.Count == 0
                ? LightingData.Empty
                : new LightingData { Emissive = _emissiveRuns.ToArray(), Lights = _lights.ToArray() };
        }

        #endregion

        #region Materials

        /// <summary>
        /// True if a material name contains one of the emissive keywords.
        /// </summary>
        private bool MatchesEmissiveKeyword(string name)
        {
            if (string.IsNullOrEmpty(name) || _emissiveKeywords.Length == 0) { return false; }
            string lower = name.ToLowerInvariant();
            foreach (string keyword in _emissiveKeywords)
            {
                if (lower.Contains(keyword, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        /// <summary>
        /// The glow from a material's appearance asset: Generic self-illumination (luminance, filter colour, colour
        /// temperature) or Advanced / Physical emission. 0 if none or unreadable.
        /// </summary>
        private uint ReadSelfIllumination(Material material)
        {
            try
            {
                ElementId assetId = material.AppearanceAssetId;
                if (assetId == null || assetId == ElementId.InvalidElementId) { return 0u; }
                if (_src.Doc.GetElement(assetId) is not AppearanceAssetElement appearance) { return 0u; }
                Visual.Asset asset = appearance.GetRenderingAsset();
                if (asset == null) { return 0u; }

                // Advanced / Physical materials switch emission on and off explicitly
                if (asset.FindByName("opaque_emission") is Visual.AssetPropertyBoolean emission && !emission.Value) { return 0u; }

                double luminance = FindDouble(asset, "generic_self_illum_luminance") ?? FindDouble(asset, "opaque_luminance") ?? 0.0;
                if (!(luminance > 0.0)) { return 0u; }

                Vector3 colour = FindColour(asset, "generic_self_illum_filter_map") ?? FindColour(asset, "opaque_luminance_modifier") ?? Vector3.One;
                double kelvin = FindDouble(asset, "generic_self_illum_color_temperature") ?? 0.0;
                if (kelvin >= 1000.0) { colour *= LightingData.KelvinToRgb((float)kelvin); }

                _selfIlluminatedMaterials++;
                Utilities.Log_Utils.Write($"{_src.Describe()}: material “{material.Name}” glows ({luminance:0} cd/m²).");
                return LightingData.PackEmissive(colour, LightingData.StrengthFromLuminance((float)luminance));
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: material “{material.Name}” appearance unreadable: {ex.Message}");
                return 0u;
            }
        }

        private static double? FindDouble(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyDouble d => d.Value,
            Visual.AssetPropertyFloat f => f.Value,
            _ => null
        };

        private static Vector3? FindColour(Visual.Asset asset, string name)
        {
            if (asset.FindByName(name) is not Visual.AssetPropertyDoubleArray4d property) { return null; }
            IList<double> rgba = property.GetValueAsDoubles();
            if (rgba == null || rgba.Count < 3) { return null; }
            var colour = new Vector3((float)rgba[0], (float)rgba[1], (float)rgba[2]);
            return colour.X + colour.Y + colour.Z > 1e-3f ? colour : null;
        }

        #endregion

        #region Fixture parameters

        private static readonly Regex NUMBER_UNIT = new(@"(?<number>[0-9][0-9.,   ']*)\s*(?<unit>lm/w|lm|cd|w|k|lx)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The fixture's output in lumens from its instance or type parameters (lm, cd or W [@ lm/W]), or null.
        /// </summary>
        private static float? ReadLumens(Element element, Element type)
        {
            string text = ParameterText(element, type, LUMEN_PARAMETERS);
            if (text == null) { return null; }

            MatchCollection matches = NUMBER_UNIT.Matches(text);
            if (matches.Count == 0 || !TryParseNumber(matches[0].Groups["number"].Value, out double value) || value <= 0.0) { return null; }
            string unit = matches[0].Groups["unit"].Value.ToLowerInvariant();

            double lumens = unit switch
            {
                "lm" => value,
                "cd" => value * 2.0 * Math.PI,       // a downward hemisphere
                "w" => value * Efficacy(matches),     // wattage × efficacy (Revit's "W @ lm/W")
                _ => double.NaN                       // illuminance or unknown: can't convert
            };
            if (double.IsNaN(lumens)) { return null; }
            return (float)Math.Clamp(lumens, 50.0, 50000.0);

            static double Efficacy(MatchCollection all)
            {
                foreach (Match m in all)
                {
                    if (string.Equals(m.Groups["unit"].Value, "lm/w", StringComparison.OrdinalIgnoreCase) && TryParseNumber(m.Groups["number"].Value, out double e) && e > 0.0)
                    {
                        return e;
                    }
                }
                return 15.0; // incandescent, Revit's default efficacy
            }
        }

        /// <summary>
        /// The fixture's colour temperature in kelvin from its instance or type parameters, or null.
        /// </summary>
        private static float? ReadKelvin(Element element, Element type)
        {
            string text = ParameterText(element, type, KELVIN_PARAMETERS);
            if (text == null) { return null; }
            foreach (Match m in NUMBER_UNIT.Matches(text))
            {
                if (!TryParseNumber(m.Groups["number"].Value, out double value)) { continue; }
                bool kelvinUnit = string.Equals(m.Groups["unit"].Value, "k", StringComparison.OrdinalIgnoreCase);
                if ((kelvinUnit || m.Groups["unit"].Value.Length == 0) && value >= 1000.0 && value <= 15000.0) { return (float)value; }
            }
            return null;
        }

        /// <summary>
        /// The display text of the first named parameter found on the instance, then the type.
        /// </summary>
        private static string ParameterText(Element element, Element type, string[] names)
        {
            foreach (string name in names)
            {
                Parameter parameter = element.LookupParameter(name) ?? type?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue) { continue; }
                string text = parameter.StorageType == StorageType.String ? parameter.AsString() : parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(text)) { return text; }
            }
            return null;
        }

        /// <summary>
        /// Parses a displayed number in either "1,500.5" or "1.500,5" style (spaces / apostrophes as group separators).
        /// </summary>
        internal static bool TryParseNumber(string text, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            string s = text.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").TrimEnd('.', ',');
            int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
            if (lastDot >= 0 && lastComma >= 0)
            {
                // Both: the later one is the decimal separator
                s = lastDot > lastComma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
            }
            else if (lastComma >= 0)
            {
                // Only commas: "1,500" (groups of three) is thousands, "2,5" is a decimal
                bool grouped = s.Split(',').Skip(1).All(part => part.Length == 3);
                s = grouped ? s.Replace(",", "") : s.Replace(',', '.');
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        #endregion
    }
}
