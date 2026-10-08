using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using BimGo.Utilities;
using Visual = Autodesk.Revit.DB.Visual;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Materials round, stage 0 (diagnostic): writes a plain-text report of every material in the model and its
    /// loaded links. The report covers:
    /// <list type="bullet">
    /// <item>how many elements use each material;</item>
    /// <item>its shading colour and its appearance asset's schema;</item>
    /// <item>a full dump of the asset's properties, including connected texture assets;</item>
    /// <item>where each bitmap was found (or not), and whether its path looks Autodesk-supplied;</item>
    /// <item>image formats and sizes, and an estimate of the embedded size at 512 / 1024 px;</item>
    /// <item>what texture discovery found on this machine (library roots, registry, Revit.ini).</item>
    /// <item>reflection probes round, stage 0: the reflection strength, roughness and tier of every used material
    /// (<see cref="ReflectivityReader"/>), the properties they came from, and the finish enum values seen.</item>
    /// </list>
    /// Read only, best effort: an unreadable material or property is noted in the report and the scan carries on.
    /// The report confirms the property names before the material extraction is written against them.
    /// </summary>
    internal sealed class MaterialScan
    {
        #region Constants

        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        /// <summary>How deep connected assets are followed.</summary>
        private const int MAX_DEPTH = 4;

        /// <summary>Most items printed from one list or array value.</summary>
        private const int MAX_LIST_ITEMS = 16;

        /// <summary>Texture-placement properties pulled out of each bitmap asset into the compact texture lines.</summary>
        private static readonly string[] PLACEMENT_PROPERTIES =
        {
            "texture_RealWorldScaleX", "texture_RealWorldScaleY", "texture_RealWorldOffsetX", "texture_RealWorldOffsetY",
            "texture_UOffset", "texture_VOffset", "texture_UScale", "texture_VScale", "texture_WAngle",
            "texture_URepeat", "texture_VRepeat", "texture_ScaleLock", "texture_MapChannel",
            "unifiedbitmap_Invert", "unifiedbitmap_RGBAmount"
        };

        /// <summary>Rough JPEG (quality ~85) size per pixel for the embed estimate, in bytes.</summary>
        private const double JPEG_BYTES_PER_PIXEL = 0.2;

        #endregion

        #region Types

        /// <summary>One bitmap reference inside a material's appearance.</summary>
        private sealed class TextureSlot
        {
            public string Material = string.Empty;
            public string Document = string.Empty;
            public string Slot = string.Empty;          // e.g. "generic_diffuse" or "generic_bump_map"
            public bool Used;
            public TextureLookup Lookup;
            public string Placement = string.Empty;
        }

        /// <summary>Header information of one image file found on disk.</summary>
        private sealed class ImageInfo
        {
            public long Bytes;
            public int Width, Height;
            public string Extension = string.Empty;
            public string Format = string.Empty;
            public string Error;
        }

        /// <summary>A property name seen under a schema: its type(s) and how often.</summary>
        private sealed class PropertySeen
        {
            public readonly HashSet<string> Types = new();
            public int Count;
            public string Example;
        }

        /// <summary>Reflectivity of one used material (reflection probes round, stage 0).</summary>
        private sealed class ReflectRow
        {
            public string Material = string.Empty;
            public string Document = string.Empty;
            public string Schema = string.Empty;
            public int Uses;
            public ReflectivityInfo Info;
            public string Candidates = string.Empty;
        }

        #endregion

        #region Fields

        private readonly TextureLocator _locator;
        private readonly OperationProgress _progress;
        private readonly StringBuilder _materials = new();
        private readonly List<TextureSlot> _slots = new();
        private readonly Dictionary<string, int> _schemaCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _usedSchemaCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedDictionary<string, PropertySeen>> _propertiesBySchema = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImageInfo> _images = new(StringComparer.OrdinalIgnoreCase);
        private int _documents, _materialCount, _usedMaterials, _noAppearance, _unreadable, _elementsScanned;
        private int _renderForShading;

        /// <summary>Tint and invert findings (build B): one line per asset or bitmap with a tint or invert on.</summary>
        private readonly List<string> _tintInvert = new();

        /// <summary>Reflectivity per used material (reflection probes round, stage 0).</summary>
        private readonly List<ReflectRow> _reflect = new();

        /// <summary>Enum-style reflection properties seen in used materials: "property = value (name)" → count.</summary>
        private readonly SortedDictionary<string, int> _enumValuesSeen = new(StringComparer.Ordinal);

        /// <summary>Used materials named like water but without the Water schema (listed only, never applied).</summary>
        private readonly List<string> _waterByNameOnly = new();

        /// <summary>Average brightness of roughness maps, by resolved path (null = not decodable).</summary>
        private readonly Dictionary<string, double?> _mapAverages = new(StringComparer.OrdinalIgnoreCase);

        // The material being dumped
        private string _currentMaterial = string.Empty, _currentDocument = string.Empty, _currentFolder;
        private bool _currentUsed;

        #endregion

        private MaterialScan(TextureLocator locator, OperationProgress progress)
        {
            _locator = locator;
            _progress = progress;
        }

        #region Entry point

        /// <summary>
        /// Scans the host model and its loaded links and writes the report.
        /// </summary>
        /// <param name="host">The active document.</param>
        /// <param name="revitVersion">The Revit year (for Revit.ini).</param>
        /// <param name="progress">Progress and cancel (throws <see cref="OperationCanceledException"/>).</param>
        /// <param name="summary">A few lines for the finished dialog.</param>
        /// <returns>The report's path.</returns>
        public static string Run(Document host, string revitVersion, OperationProgress progress, out string summary)
        {
            progress.Begin("Looking for texture folders", 0.0, 0.05);
            var scan = new MaterialScan(TextureLocator.Discover(revitVersion), progress);

            // The host and each distinct loaded link document
            var documents = new List<Document> { host };
            foreach (LinkCandidate link in LinkResolver.Candidates(host))
            {
                if (link.Document == null) { continue; }
                if (documents.Any(d => SameDocument(d, link.Document))) { continue; }
                documents.Add(link.Document);
            }

            for (int i = 0; i < documents.Count; i++)
            {
                double start = 0.05 + 0.8 * i / documents.Count, end = 0.05 + 0.8 * (i + 1) / documents.Count;
                scan.ScanDocument(documents[i], i == 0, start, end);
            }

            progress.Begin("Reading image headers", 0.85, 0.98);
            scan.ReadImages();

            progress.Begin("Writing the report", 0.98, 1.0);
            string path = scan.WriteReport(host, revitVersion, out summary);
            Log_Utils.Write($"Material scan written: {path}");
            return path;
        }

        private static bool SameDocument(Document a, Document b)
        {
            if (ReferenceEquals(a, b)) { return true; }
            string pa = a.PathName, pb = b.PathName;
            return !string.IsNullOrEmpty(pa) && string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Documents and materials

        private void ScanDocument(Document doc, bool isHost, double start, double end)
        {
            _documents++;
            _currentDocument = (isHost ? "" : "[link] ") + doc.Title;
            _currentFolder = SafeFolder(doc.PathName);

            // Usage: how many model elements carry each material (geometry or paint)
            double mid = start + (end - start) * 0.6;
            _progress.Begin($"Counting material use: {doc.Title}", start, mid);
            Dictionary<long, int> usage = CountUsage(doc);

            _progress.Begin($"Reading materials: {doc.Title}", mid, end);
            List<Material> materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .OrderByDescending(m => usage.TryGetValue(m.Id.Value, out int n) ? n : 0)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _materials.AppendLine();
            _materials.AppendLine($"################ {_currentDocument} ({materials.Count} materials) ################");

            for (int i = 0; i < materials.Count; i++)
            {
                _progress.Step(i, materials.Count);
                _progress.ThrowIfCancelled();
                int uses = usage.TryGetValue(materials[i].Id.Value, out int n) ? n : 0;
                DumpMaterial(doc, materials[i], uses);
            }
        }

        private Dictionary<long, int> CountUsage(Document doc)
        {
            var usage = new Dictionary<long, int>();
            int total;
            try
            {
                total = new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent().GetElementCount();
            }
            catch (Exception ex)
            {
                Log_Utils.Write($"Material scan: element count failed in {doc.Title}: {ex.Message}");
                return usage;
            }

            int done = 0;
            var ids = new HashSet<long>();
            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent())
            {
                if (++done % 500 == 0)
                {
                    _progress.Step(done, total);
                    _progress.ThrowIfCancelled();
                }
                try
                {
                    Category category = element.Category;
                    if (category == null || category.CategoryType != CategoryType.Model) { continue; }
                    ids.Clear();
                    foreach (ElementId id in element.GetMaterialIds(false)) { ids.Add(id.Value); }
                    foreach (ElementId id in element.GetMaterialIds(true)) { ids.Add(id.Value); }
                    foreach (long id in ids) { usage[id] = usage.TryGetValue(id, out int n) ? n + 1 : 1; }
                    _elementsScanned++;
                }
                catch
                {
                    // Elements without readable geometry carry no materials for this purpose
                }
            }
            return usage;
        }

        private void DumpMaterial(Document doc, Material material, int uses)
        {
            _materialCount++;
            _currentMaterial = material.Name;
            _currentUsed = uses > 0;
            if (_currentUsed) { _usedMaterials++; }

            StringBuilder sb = _materials;
            sb.AppendLine();
            sb.AppendLine($"=== {material.Name}  (id {material.Id.Value}, used by {uses} element{(uses == 1 ? "" : "s")})");

            try
            {
                string colour = material.Color is { IsValid: true } c ? $"RGB {c.Red},{c.Green},{c.Blue}" : "invalid";
                sb.AppendLine($"    Shading colour {colour}; transparency {material.Transparency}%; shininess {material.Shininess}; " +
                    $"smoothness {material.Smoothness}; class “{material.MaterialClass}”; category “{material.MaterialCategory}”");

                object renderForShading = ReadMember(material, "UseRenderAppearanceForShading");
                if (renderForShading != null)
                {
                    sb.AppendLine($"    Use render appearance for shading: {renderForShading}");
                    if (renderForShading is true) { _renderForShading++; }
                }

                ElementId assetId = material.AppearanceAssetId;
                if (assetId == null || assetId == ElementId.InvalidElementId || doc.GetElement(assetId) is not AppearanceAssetElement appearance)
                {
                    sb.AppendLine("    No appearance asset.");
                    _noAppearance++;
                    NoteReflectivity(material, null, "(no appearance)", uses);
                    return;
                }

                Visual.Asset asset = appearance.GetRenderingAsset();
                if (asset == null)
                {
                    sb.AppendLine($"    Appearance “{appearance.Name}”: no rendering asset.");
                    _noAppearance++;
                    NoteReflectivity(material, null, "(no rendering asset)", uses);
                    return;
                }

                string schema = SchemaOf(asset);
                Increment(_schemaCounts, schema);
                if (_currentUsed) { Increment(_usedSchemaCounts, schema); }
                sb.AppendLine($"    Appearance “{appearance.Name}”: schema {schema}; asset name “{asset.Name}”; title “{asset.Title}”; " +
                    $"library “{asset.LibraryName}”; type {asset.AssetType}; {asset.Size} properties");

                NoteTintAndInvert(asset, "appearance", schema);
                NoteReflectivity(material, asset, schema, uses, appearance.Name);

                // Full dump only for materials in use: unused ones get the header lines above (keeps the report readable)
                if (_currentUsed) { DumpAsset(asset, schema, depth: 1, slotPrefix: string.Empty); }
                else { FindTexturesOnly(asset, depth: 1, slotPrefix: string.Empty); }
            }
            catch (Exception ex)
            {
                _unreadable++;
                sb.AppendLine($"    UNREADABLE: {ex.GetType().Name}: {ex.Message}");
            }
        }

        #endregion

        #region Asset dump

        /// <summary>
        /// The asset's schema: its "BaseSchema" string property when present, else its name.
        /// </summary>
        private static string SchemaOf(Visual.Asset asset)
        {
            try
            {
                if (asset.FindByName("BaseSchema") is Visual.AssetPropertyString s && !string.IsNullOrWhiteSpace(s.Value)) { return s.Value; }
            }
            catch { /* fall through */ }
            return string.IsNullOrWhiteSpace(asset.Name) ? "(unnamed)" : asset.Name;
        }

        private void DumpAsset(Visual.Asset asset, string schema, int depth, string slotPrefix)
        {
            string indent = new(' ', 4 * depth);
            for (int i = 0; i < asset.Size; i++)
            {
                Visual.AssetProperty property;
                try { property = asset.Get(i); }
                catch (Exception ex)
                {
                    _materials.AppendLine($"{indent}[{i}] unreadable: {ex.Message}");
                    continue;
                }
                if (property != null) { DumpProperty(property, schema, depth, indent, slotPrefix); }
            }
            RegisterTexture(asset, slotPrefix);
        }

        private void DumpProperty(Visual.AssetProperty property, string schema, int depth, string indent, string slotPrefix)
        {
            string name = SafeName(property);
            string type = SafeType(property);
            string value = FormatValue(property);
            _materials.AppendLine($"{indent}{name} : {type} = {value}");
            Seen(schema, name, type, value);

            // A list holds further properties
            if (property is Visual.AssetPropertyList list && depth < MAX_DEPTH)
            {
                try
                {
                    foreach (Visual.AssetProperty item in list.GetValue())
                    {
                        if (item != null) { DumpProperty(item, schema, depth + 1, indent + "    ", slotPrefix); }
                    }
                }
                catch (Exception ex) { _materials.AppendLine($"{indent}    list unreadable: {ex.Message}"); }
            }

            // Connected assets: textures (UnifiedBitmap), procedural maps, nested assets
            int connected = 0;
            try { connected = property.NumberOfConnectedProperties; }
            catch { /* none */ }
            for (int c = 0; c < connected && depth < MAX_DEPTH; c++)
            {
                try
                {
                    if (property.GetConnectedProperty(c) is not Visual.Asset child) { continue; }
                    string childSchema = SchemaOf(child);
                    _materials.AppendLine($"{indent}  ↳ connected asset: schema {childSchema}; name “{child.Name}”; {child.Size} properties");
                    DumpAsset(child, childSchema, depth + 1, Join(slotPrefix, name));
                }
                catch (Exception ex) { _materials.AppendLine($"{indent}  ↳ connected property {c} unreadable: {ex.Message}"); }
            }
        }

        /// <summary>
        /// For unused materials: finds texture slots (for the bitmap statistics) without writing the dump.
        /// </summary>
        private void FindTexturesOnly(Visual.Asset asset, int depth, string slotPrefix)
        {
            RegisterTexture(asset, slotPrefix);
            if (depth >= MAX_DEPTH) { return; }
            for (int i = 0; i < asset.Size; i++)
            {
                try
                {
                    Visual.AssetProperty property = asset.Get(i);
                    if (property == null) { continue; }
                    int connected = property.NumberOfConnectedProperties;
                    for (int c = 0; c < connected; c++)
                    {
                        if (property.GetConnectedProperty(c) is Visual.Asset child) { FindTexturesOnly(child, depth + 1, Join(slotPrefix, SafeName(property))); }
                    }
                }
                catch { /* best effort */ }
            }
        }

        /// <summary>
        /// If the asset is a bitmap (has "unifiedbitmap_Bitmap"), records the texture slot and resolves its path.
        /// </summary>
        private void RegisterTexture(Visual.Asset asset, string slot)
        {
            string raw;
            try
            {
                if (asset.FindByName("unifiedbitmap_Bitmap") is not Visual.AssetPropertyString bitmap) { return; }
                raw = bitmap.Value;
            }
            catch { return; }

            TextureLookup lookup = _locator.Resolve(raw, _currentFolder);
            var placement = new StringBuilder();
            foreach (string name in PLACEMENT_PROPERTIES)
            {
                try
                {
                    Visual.AssetProperty p = asset.FindByName(name);
                    if (p != null) { placement.Append(placement.Length == 0 ? "" : "; ").Append(name.Replace("texture_", "").Replace("unifiedbitmap_", "")).Append('=').Append(FormatValue(p)); }
                }
                catch { /* skip */ }
            }

            _slots.Add(new TextureSlot
            {
                Material = _currentMaterial,
                Document = _currentDocument,
                Slot = string.IsNullOrEmpty(slot) ? "(root)" : slot,
                Used = _currentUsed,
                Lookup = lookup,
                Placement = placement.ToString()
            });
            if (lookup.Path != null && !_images.ContainsKey(lookup.Path)) { _images[lookup.Path] = null; }
            NoteTintAndInvert(asset, string.IsNullOrEmpty(slot) ? "bitmap (root)" : "bitmap " + slot, "UnifiedBitmap");

            if (_currentUsed)
            {
                _materials.AppendLine($"    >>> TEXTURE {(string.IsNullOrEmpty(slot) ? "(root)" : slot)}: {Describe(lookup)}");
                if (placement.Length > 0) { _materials.AppendLine($"        placement: {placement}"); }
            }
        }

        /// <summary>
        /// Records a tint that is on (<c>common_Tint_toggle</c> or any "…tint_enabled" / "…tint_toggle"), with its
        /// colour and colour space, and an inverted bitmap (<c>unifiedbitmap_Invert</c>), for the TINT AND INVERT
        /// section. Used materials only.
        /// </summary>
        private void NoteTintAndInvert(Visual.Asset asset, string where, string schema)
        {
            if (!_currentUsed) { return; }
            try
            {
                var parts = new List<string>();
                for (int i = 0; i < asset.Size; i++)
                {
                    Visual.AssetProperty property = asset.Get(i);
                    string name = property?.Name ?? string.Empty;
                    bool toggle = name.Equals("common_Tint_toggle", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("tint_enabled", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("tint_toggle", StringComparison.OrdinalIgnoreCase);
                    if (toggle && property is Visual.AssetPropertyBoolean { Value: true } or Visual.AssetPropertyInteger { Value: 1 })
                    {
                        string colourName = name.Equals("common_Tint_toggle", StringComparison.OrdinalIgnoreCase)
                            ? "common_Tint_color"
                            : name.Substring(0, name.Length - (name.EndsWith("_enabled", StringComparison.OrdinalIgnoreCase) ? "_enabled".Length : "_toggle".Length)) + "_color";
                        Visual.AssetProperty colour = asset.FindByName(colourName);
                        Visual.AssetProperty space = asset.FindByName(colourName + "space") ?? asset.FindByName(colourName.Replace("_color", "_colorspace"));
                        parts.Add($"{name} ON, {colourName} = {(colour == null ? "?" : FormatValue(colour))}{(space == null ? "" : $", {space.Name} = {FormatValue(space)}")}");
                    }
                    if (name.Equals("unifiedbitmap_Invert", StringComparison.OrdinalIgnoreCase) && property is Visual.AssetPropertyBoolean { Value: true })
                    {
                        parts.Add("unifiedbitmap_Invert ON");
                    }
                }
                if (parts.Count > 0) { _tintInvert.Add($"  {_currentDocument} | {_currentMaterial} | {where} ({schema}) | {string.Join("; ", parts)}"); }
            }
            catch (Exception ex)
            {
                _tintInvert.Add($"  {_currentDocument} | {_currentMaterial} | {where}: unreadable ({ex.Message})");
            }
        }

        /// <summary>
        /// Reflection probes round, stage 0: reads the reflectivity of a used material, lists the properties that bear on
        /// it, counts the finish enum values seen and notes water-named materials without the Water schema.
        /// Writes one summary line into the material dump.
        /// </summary>
        private void NoteReflectivity(Material material, Visual.Asset asset, string schema, int uses, string appearanceName = null)
        {
            if (!_currentUsed) { return; }
            try
            {
                ReflectivityInfo info = ReflectivityReader.Read(asset, asset == null ? string.Empty : schema, material, MapAverage);
                var candidates = new List<string>();
                if (asset != null)
                {
                    for (int i = 0; i < asset.Size; i++)
                    {
                        Visual.AssetProperty property = asset.Get(i);
                        string name = property?.Name;
                        if (!ReflectivityReader.IsCandidate(name)) { continue; }

                        string text = $"{name}={FormatValue(property)}";
                        if (property is Visual.AssetPropertyEnum or Visual.AssetPropertyInteger)
                        {
                            int value = property is Visual.AssetPropertyEnum e ? e.Value : ((Visual.AssetPropertyInteger)property).Value;
                            string revitName = ReflectivityReader.RevitEnumName(name, value);
                            string guess = ReflectivityReader.GuessedName(name, value);
                            if (revitName != null || guess != null)
                            {
                                string label = revitName ?? $"guess {guess}";
                                text += $" ({label})";
                                string key = $"{name} = {value}: Revit {revitName ?? "-"} ({ReflectivityReader.RevitEnumTypeName(name) ?? "no enum type found"}); " +
                                             $"guess {guess ?? "-"}{(revitName != null && guess != null && !Same(revitName, guess) ? "   <<< MISMATCH" : "")}";
                                Increment(_enumValuesSeen, key);
                            }
                        }
                        int connected = 0;
                        try { connected = property.NumberOfConnectedProperties; } catch { /* none */ }
                        if (connected > 0) { text += " [map]"; }
                        candidates.Add(text);
                    }
                }

                _reflect.Add(new ReflectRow
                {
                    Material = material.Name,
                    Document = _currentDocument,
                    Schema = schema,
                    Uses = uses,
                    Info = info,
                    Candidates = string.Join("; ", candidates)
                });

                bool waterName = material.Name.Contains("water", StringComparison.OrdinalIgnoreCase)
                    || (appearanceName?.Contains("water", StringComparison.OrdinalIgnoreCase) ?? false);
                if (waterName && !info.Water) { _waterByNameOnly.Add($"  {_currentDocument} | {material.Name} | {schema}"); }

                _materials.AppendLine($"    Reflectivity: tier {ReflectivityReader.TierText(info.Tier)}; strength {info.Strength:0.##}; " +
                    $"roughness {info.Roughness:0.##} ({ReflectivityReader.BlurText(info.Roughness)}){Flags(info)}; {info.Source}");
            }
            catch (Exception ex)
            {
                _materials.AppendLine($"    Reflectivity unreadable: {ex.Message}");
            }

            static bool Same(string a, string b) =>
                string.Equals(a.Replace("_", "").Replace(" ", ""), b.Replace("_", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The average brightness of a connected bitmap asset's image (roughness maps), found with the texture locator
        /// and cached by path. Null when the path is empty, missing or not decodable.
        /// </summary>
        private double? MapAverage(Visual.Asset bitmap)
        {
            try
            {
                if (bitmap.FindByName("unifiedbitmap_Bitmap") is not Visual.AssetPropertyString path || string.IsNullOrWhiteSpace(path.Value)) { return null; }
                TextureLookup lookup = _locator.Resolve(path.Value, _currentFolder);
                if (lookup.Path == null) { return null; }
                if (!_mapAverages.TryGetValue(lookup.Path, out double? average))
                {
                    average = ReflectivityReader.AverageLuminance(lookup.Path);
                    _mapAverages[lookup.Path] = average;
                }
                return average;
            }
            catch
            {
                return null;
            }
        }

        private static string Flags(ReflectivityInfo info) =>
            (info.Metallic ? ", metallic" : "") + (info.Glass ? ", glass" : "") + (info.Water ? ", water" : "") + (info.Mapped ? "" : ", no rule");

        private void Seen(string schema, string name, string type, string value)
        {
            if (!_propertiesBySchema.TryGetValue(schema, out SortedDictionary<string, PropertySeen> names))
            {
                names = new SortedDictionary<string, PropertySeen>(StringComparer.Ordinal);
                _propertiesBySchema[schema] = names;
            }
            if (!names.TryGetValue(name, out PropertySeen seen))
            {
                seen = new PropertySeen();
                names[name] = seen;
            }
            seen.Types.Add(type);
            seen.Count++;
            if (seen.Example == null && value.Length > 0 && value.Length <= 80 && value != "\"\"") { seen.Example = value; }
        }

        #endregion

        #region Value formatting

        private static string SafeName(Visual.AssetProperty property)
        {
            try { return property.Name ?? "(null)"; }
            catch { return "(unreadable name)"; }
        }

        private static string SafeType(Visual.AssetProperty property)
        {
            try { return property.Type.ToString(); }
            catch { return property.GetType().Name; }
        }

        /// <summary>
        /// A property's value as text. Common types are read directly; anything else by reflection ("Value",
        /// "GetValue…") so that the diagnostic compiles and runs across Revit years.
        /// </summary>
        private static string FormatValue(Visual.AssetProperty property)
        {
            try
            {
                switch (property)
                {
                    case Visual.AssetPropertyString s: return Quote(s.Value);
                    case Visual.AssetPropertyBoolean b: return b.Value ? "true" : "false";
                    case Visual.AssetPropertyInteger n: return n.Value.ToString(CI);
                    case Visual.AssetPropertyDouble d: return Num(d.Value);
                    case Visual.AssetPropertyFloat f: return Num(f.Value);
                    case Visual.AssetPropertyEnum e: return e.Value.ToString(CI);
                    case Visual.AssetPropertyDoubleArray4d c4: return FormatObject(c4.GetValueAsDoubles(), 0);
                    case Visual.AssetPropertyList list: return $"list ({list.GetValue()?.Count ?? 0} items)";
                    case Visual.AssetPropertyDistance distance:
                        object unit = ReadMember(distance, "GetUnitTypeId");
                        string unitText = unit == null ? "" : " " + (ReadMember(unit, "TypeId") ?? unit);
                        return Num(distance.Value) + unitText;
                }
                object value = ReadMember(property, "Value") ?? ReadMember(property, "GetValueAsXYZ") ??
                               ReadMember(property, "GetValueAsDoubles") ?? ReadMember(property, "GetValue");
                return value == null ? "" : FormatObject(value, 0);
            }
            catch (Exception ex)
            {
                return $"<unreadable: {ex.GetType().Name}>";
            }
        }

        private static string FormatObject(object value, int depth)
        {
            switch (value)
            {
                case null: return "null";
                case string s: return Quote(s);
                case double d: return Num(d);
                case float f: return Num(f);
                case XYZ xyz: return $"({Num(xyz.X)}, {Num(xyz.Y)}, {Num(xyz.Z)})";
                case UV uv: return $"({Num(uv.U)}, {Num(uv.V)})";
                case Visual.AssetProperty p: return SafeName(p);
                case IEnumerable items when depth < 2:
                    var parts = new List<string>();
                    int n = 0;
                    foreach (object item in items)
                    {
                        if (++n > MAX_LIST_ITEMS) { parts.Add("…"); break; }
                        parts.Add(FormatObject(item, depth + 1));
                    }
                    return "[" + string.Join(", ", parts) + "]";
                case IFormattable formattable: return formattable.ToString(null, CI);
                default: return value.ToString();
            }
        }

        /// <summary>
        /// Reads a public instance property, or calls a public parameterless method, by name. Null if absent or failing.
        /// </summary>
        private static object ReadMember(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0) { return property.GetValue(target); }
                MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null);
                return method?.Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static string Num(double value) => value.ToString("0.######", CI);

        private static string Quote(string s) => s == null ? "null" : "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

        private static string Join(string prefix, string name) => string.IsNullOrEmpty(prefix) ? name : prefix + " › " + name;

        private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

        private static string SafeFolder(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path); }
            catch { return null; }
        }

        private string Describe(TextureLookup lookup)
        {
            string origin = lookup.AutodeskLibrary ? "Autodesk library" : "user image";
            if (lookup.Path == null) { return $"MISSING ({origin}) raw={Quote(lookup.Raw)}"; }
            string image = _images.TryGetValue(lookup.Path, out ImageInfo info) && info != null ? " " + DescribeImage(info) : "";
            return $"found via {lookup.Found} ({origin}) → {lookup.Path}{image}  raw={Quote(lookup.Raw)}";
        }

        private static string DescribeImage(ImageInfo info) => info.Error != null
            ? $"[{info.Format}, {info.Bytes / 1024} KB, not decodable: {info.Error}]"
            : $"[{info.Width}×{info.Height} {info.Format}, {info.Bytes / 1024} KB]";

        #endregion

        #region Images

        /// <summary>
        /// Reads each found image's header (size, pixel format) without decoding the pixels.
        /// </summary>
        private void ReadImages()
        {
            List<string> paths = _images.Keys.ToList();
            for (int i = 0; i < paths.Count; i++)
            {
                _progress.Step(i, paths.Count);
                _progress.ThrowIfCancelled();
                string extension = Path.GetExtension(paths[i]).ToLowerInvariant();
                var info = new ImageInfo { Extension = extension, Format = extension };
                try
                {
                    info.Bytes = new FileInfo(paths[i]).Length;
                    using var stream = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using System.Drawing.Image image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                    info.Width = image.Width;
                    info.Height = image.Height;
                    info.Format += " " + image.PixelFormat;
                }
                catch (Exception ex)
                {
                    info.Error = ex.GetType().Name;
                }
                _images[paths[i]] = info;
            }
        }

        #endregion

        #region Report

        private string WriteReport(Document host, string revitVersion, out string summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BimGo material scan — {host.Title} — {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Revit {revitVersion}; BimGo add-in {Globals.ADDIN_VERSION}; model {host.PathName}");
            sb.AppendLine("Read-only diagnostic for the materials and reflection probes rounds. Send this file back as is.");

            // Discovery
            sb.AppendLine();
            sb.AppendLine("==================== TEXTURE LOCATIONS ON THIS MACHINE ====================");
            sb.AppendLine($"Autodesk Material Library found: {(_locator.HasLibrary ? "yes" : "NO")}");
            foreach (string root in _locator.LibraryRoots) { sb.AppendLine($"  library root: {root}"); }
            foreach (string extra in _locator.ExtraPaths) { sb.AppendLine($"  additional render appearance path: {extra}"); }
            foreach (string note in _locator.Notes) { sb.AppendLine($"  · {note}"); }

            // Summary
            List<TextureSlot> usedSlots = _slots.Where(s => s.Used).ToList();
            var usedBitmaps = usedSlots.Select(s => s.Lookup.Raw).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var usedFound = usedSlots.Where(s => s.Lookup.Path != null).Select(s => s.Lookup.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var usedMissing = usedSlots.Where(s => s.Lookup.Path == null).Select(s => s.Lookup.Raw).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int materialsWithTextures = usedSlots.Select(s => s.Document + "\n" + s.Material).Distinct().Count();

            sb.AppendLine();
            sb.AppendLine("==================== SUMMARY ====================");
            sb.AppendLine($"Documents scanned: {_documents} (host + loaded links); model elements checked: {_elementsScanned}");
            sb.AppendLine($"Materials: {_materialCount}; used by at least one element: {_usedMaterials}; no appearance asset: {_noAppearance}; unreadable: {_unreadable}");
            sb.AppendLine($"Materials set to 'use render appearance for shading': {_renderForShading}");
            sb.AppendLine($"Used materials with at least one bitmap: {materialsWithTextures}");
            sb.AppendLine($"Texture slots in used materials: {usedSlots.Count}; distinct bitmap paths: {usedBitmaps.Count}; " +
                $"found: {usedFound.Count}; missing: {usedMissing.Count}");
            sb.AppendLine("Found by stage (used, distinct): " + string.Join(", ", usedSlots.Where(s => s.Lookup.Path != null)
                .GroupBy(s => s.Lookup.Found).Select(g => $"{g.Key} {g.Select(s => s.Lookup.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()}")));
            sb.AppendLine("Origin (used, distinct): " + string.Join(", ", usedSlots
                .GroupBy(s => s.Lookup.AutodeskLibrary ? "Autodesk library" : "user image")
                .Select(g => $"{g.Key} {g.Select(s => s.Lookup.Raw).Distinct(StringComparer.OrdinalIgnoreCase).Count()}")));
            sb.AppendLine("Slots (used): " + string.Join(", ", usedSlots.GroupBy(s => s.Slot).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ×{g.Count()}")));

            sb.AppendLine();
            sb.AppendLine("Schemas (all materials / used):");
            foreach (KeyValuePair<string, int> pair in _schemaCounts.OrderByDescending(p => p.Value))
            {
                sb.AppendLine($"  {pair.Key}: {pair.Value} / {(_usedSchemaCounts.TryGetValue(pair.Key, out int u) ? u : 0)}");
            }

            // Image formats, sizes and the embed estimate (used and found only)
            List<ImageInfo> images = usedFound.Select(p => _images.TryGetValue(p, out ImageInfo i) ? i : null).Where(i => i != null).ToList();
            sb.AppendLine();
            sb.AppendLine("Image files (used, found):");
            foreach (IGrouping<string, ImageInfo> group in images.GroupBy(i => i.Extension.Length > 0 ? i.Extension : "(no extension)"))
            {
                sb.AppendLine($"  {group.Key}: {group.Count()} files, {group.Sum(i => i.Bytes) / (1024 * 1024.0):0.0} MB on disk, " +
                    $"{group.Count(i => i.Error != null)} not decodable by System.Drawing");
            }
            List<ImageInfo> decodable = images.Where(i => i.Error == null && i.Width > 0 && i.Height > 0).ToList();
            if (decodable.Count > 0)
            {
                sb.AppendLine($"  Largest: {decodable.Max(i => Math.Max(i.Width, i.Height))} px; smallest: {decodable.Min(i => Math.Max(i.Width, i.Height))} px; " +
                    $"non-square: {decodable.Count(i => i.Width != i.Height)} of {decodable.Count}");
                foreach (int cap in new[] { 256, 512, 1024, 2048 })
                {
                    double bytes = decodable.Sum(i => ScaledPixels(i, cap)) * JPEG_BYTES_PER_PIXEL;
                    double gpu = decodable.Count * (double)cap * cap * 4 * 4 / 3;  // RGBA8 array layers with mipmaps
                    sb.AppendLine($"  At max {cap} px: ≈ {bytes / (1024 * 1024.0):0.0} MB embedded (JPEG), ≈ {gpu / (1024 * 1024.0):0} MB GPU memory as {cap}² layers");
                }
            }

            // Missing bitmaps
            sb.AppendLine();
            sb.AppendLine("==================== MISSING BITMAPS (used materials) ====================");
            if (usedMissing.Count == 0) { sb.AppendLine("None."); }
            foreach (IGrouping<string, TextureSlot> group in usedSlots.Where(s => s.Lookup.Path == null).GroupBy(s => s.Lookup.Raw, StringComparer.OrdinalIgnoreCase))
            {
                TextureSlot first = group.First();
                sb.AppendLine($"  {Quote(group.Key)} ({(first.Lookup.AutodeskLibrary ? "Autodesk library" : "user image")}) — " +
                    string.Join("; ", group.Select(s => $"{s.Document}: {s.Material} [{s.Slot}]").Distinct().Take(8)));
            }

            // Tint and invert (build B: Revit's tint overlay and inverted images)
            sb.AppendLine();
            sb.AppendLine("==================== TINT AND INVERT (used materials) ====================");
            if (_tintInvert.Count == 0) { sb.AppendLine("None."); }
            foreach (string line in _tintInvert) { sb.AppendLine(line); }

            // Reflectivity (reflection probes round, stage 0)
            WriteReflectivity(sb);

            // Texture lines, compact
            sb.AppendLine();
            sb.AppendLine("==================== TEXTURES (used materials) ====================");
            foreach (TextureSlot slot in usedSlots)
            {
                sb.AppendLine($"  {slot.Document} | {slot.Material} | {slot.Slot} | {Describe(slot.Lookup)}");
                if (slot.Placement.Length > 0) { sb.AppendLine($"      {slot.Placement}"); }
            }

            // Property names per schema
            sb.AppendLine();
            sb.AppendLine("==================== PROPERTY NAMES BY SCHEMA (used materials) ====================");
            foreach (KeyValuePair<string, SortedDictionary<string, PropertySeen>> schema in _propertiesBySchema.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  [{schema.Key}]");
                foreach (KeyValuePair<string, PropertySeen> name in schema.Value)
                {
                    sb.AppendLine($"    {name.Key} : {string.Join("/", name.Value.Types)} ×{name.Value.Count}{(name.Value.Example != null ? "  e.g. " + name.Value.Example : "")}");
                }
            }

            // Per-material dump
            sb.AppendLine();
            sb.AppendLine("==================== MATERIALS (used ones in full, unused as headers) ====================");
            sb.Append(_materials);

            string folder = Path.Combine(Log_Utils.LogFolder, "MaterialScans");
            Directory.CreateDirectory(folder);
            string safeTitle = string.Concat(host.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            string path = Path.Combine(folder, $"MaterialScan_{safeTitle}_{DateTime.Now:yyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            summary = $"{_usedMaterials} of {_materialCount} materials are used; {materialsWithTextures} of those have bitmaps.\n" +
                      $"{usedBitmaps.Count} distinct bitmaps: {usedFound.Count} found, {usedMissing.Count} missing.\n" +
                      $"Autodesk Material Library on this machine: {(_locator.HasLibrary ? "found" : "not found")}.\n" +
                      ReflectivitySummary();
            return path;
        }

        /// <summary>
        /// The REFLECTIVITY section: tier counts, finish enum values seen (Revit names vs the table's guesses), water
        /// found by name only, then every used material sorted by strength, with the properties it was read from.
        /// </summary>
        private void WriteReflectivity(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("==================== REFLECTIVITY (used materials) ====================");
            sb.AppendLine("Strength (0–1) sets the tier (rounded to the nearest 25 %); roughness sets the blur (0 sharp – 1 matt), separately.");
            sb.AppendLine("Rules in order: Mirror schema → \"mirror\" in the name → Water schema / see-through named \"water\" → see-through (glass) → Prism (roughness map average when connected) → Generic → finish enum → legacy preset shininess → nothing.");
            sb.AppendLine("Default threshold 50 %: only tiers 50 % and 75 % + reflect unless lowered to 25 %. Glass keeps its own sky sheen.");
            sb.AppendLine("All values are a first proposal (ReflectivityReader.cs): mark anything that lands in the wrong tier.");

            List<ReflectRow> opaque = _reflect.Where(r => !r.Info.Glass).ToList();
            sb.AppendLine();
            sb.AppendLine($"Used materials: {_reflect.Count}; glass: {_reflect.Count(r => r.Info.Glass)}; water: {_reflect.Count(r => r.Info.Water)}; " +
                $"metallic: {_reflect.Count(r => r.Info.Metallic)}; no rule: {_reflect.Count(r => !r.Info.Mapped)}");
            sb.AppendLine("Tiers (not glass): " + string.Join(", ", Enumerable.Range(0, 4).Reverse()
                .Select(t => $"{ReflectivityReader.TierText(t)} ×{opaque.Count(r => r.Info.Tier == t)}")));
            sb.AppendLine($"Would reflect at the 50 % default: {opaque.Count(r => r.Info.Tier >= 2)} materials, " +
                $"{opaque.Where(r => r.Info.Tier >= 2).Sum(r => r.Uses)} element uses; at 25 %: {opaque.Count(r => r.Info.Tier >= 1)} materials, " +
                $"{opaque.Where(r => r.Info.Tier >= 1).Sum(r => r.Uses)} element uses");

            sb.AppendLine();
            sb.AppendLine("Finish / type enum values seen (property = value: Revit enum name; the table's ordinal guess):");
            if (_enumValuesSeen.Count == 0) { sb.AppendLine("  None."); }
            foreach (KeyValuePair<string, int> pair in _enumValuesSeen) { sb.AppendLine($"  {pair.Key}   ×{pair.Value}"); }

            sb.AppendLine();
            sb.AppendLine("Named like water but not water (opaque, so the name rule doesn't apply):");
            if (_waterByNameOnly.Count == 0) { sb.AppendLine("  None."); }
            foreach (string line in _waterByNameOnly) { sb.AppendLine(line); }

            sb.AppendLine();
            sb.AppendLine("Schemas with no rule (reflect nothing):");
            List<IGrouping<string, ReflectRow>> unmapped = _reflect.Where(r => !r.Info.Mapped).GroupBy(r => r.Schema).OrderByDescending(g => g.Count()).ToList();
            if (unmapped.Count == 0) { sb.AppendLine("  None."); }
            foreach (IGrouping<string, ReflectRow> group in unmapped)
            {
                sb.AppendLine($"  {group.Key} ×{group.Count()}: {string.Join(", ", group.Select(r => r.Material).Take(6))}{(group.Count() > 6 ? ", …" : "")}");
            }

            sb.AppendLine();
            sb.AppendLine("Per material (tier | strength | roughness, blur | flags | uses | document | material | schema | source):");
            foreach (ReflectRow row in _reflect.OrderByDescending(r => r.Info.Glass ? -1 : r.Info.Tier).ThenByDescending(r => r.Info.Strength).ThenBy(r => r.Material))
            {
                ReflectivityInfo info = row.Info;
                string tier = info.Glass ? "glass" : ReflectivityReader.TierText(info.Tier);
                sb.AppendLine($"  [{tier,-6}] {info.Strength:0.00} | {info.Roughness:0.00} {ReflectivityReader.BlurText(info.Roughness)}{Flags(info)} | " +
                    $"×{row.Uses} | {(row.Document.Length == 0 ? "host" : row.Document)} | {row.Material} | {row.Schema} | {info.Source}");
                if (row.Candidates.Length > 0) { sb.AppendLine($"           {row.Candidates}"); }
            }
        }

        /// <summary>One line for the finished dialog: how many materials would reflect.</summary>
        private string ReflectivitySummary()
        {
            List<ReflectRow> opaque = _reflect.Where(r => !r.Info.Glass).ToList();
            return $"Reflectivity: {opaque.Count(r => r.Info.Tier >= 2)} materials at 50 %+, {opaque.Count(r => r.Info.Tier == 1)} at 25 %, " +
                   $"{_reflect.Count(r => r.Info.Glass)} glass, {_reflect.Count(r => r.Info.Water)} water.";
        }

        private static void Increment(SortedDictionary<string, int> counts, string key) => counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;

        private static double ScaledPixels(ImageInfo info, int cap)
        {
            double longest = Math.Max(info.Width, info.Height);
            double scale = longest > cap ? cap / longest : 1.0;
            return info.Width * scale * info.Height * scale;
        }

        #endregion
    }
}
