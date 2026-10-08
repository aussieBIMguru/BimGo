using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BimGo.Format;
using BimGo.Scene;
using Visual = Autodesk.Revit.DB.Visual;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Materials round: render colours, textures and surface coordinates for the walkthrough's Realistic mode.
    /// Only runs when <see cref="LaunchSettings.ExtractTextures"/> is ticked; otherwise nothing here does any work
    /// and the snapshot is exactly as before.
    /// <list type="bullet">
    /// <item><b>Table:</b> one <see cref="SceneMaterial"/> per material used (per document). The colour and texture
    /// slot depend on the appearance schema: Generic <c>generic_diffuse</c> (+ image fade), Advanced
    /// <c>opaque_albedo</c>, Metal <c>metal_f0</c>, Layered <c>layered_diffuse</c>, Hardwood <c>hardwood_color</c>,
    /// and the simple schemas' <c>*_color</c>. Prism <c>surface_albedo</c> is the reflection map and is never used
    /// as colour.</item>
    /// <item><b>Textures:</b> the bitmap on the colour slot is found with <see cref="TextureLocator"/> (absolute →
    /// Autodesk library → Revit's additional render appearance paths → model folder), downscaled to the size cap,
    /// re-encoded as JPEG and embedded once per image. Images are cached for the Revit session, so a live refresh
    /// (F5) only reads new ones.</item>
    /// <item><b>Surface coordinates:</b> metres along each face's own axes. Planar walls get U horizontal and V up,
    /// floors and roofs get plan X / Y (so coursing and boards line up across faces); curved faces use the face's
    /// parameters scaled to metres; free meshes are box-mapped.</item>
    /// </list>
    /// Everything is best effort: a failure leaves a material untextured (its colour still shows), never fails the
    /// extraction.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Constants

        /// <summary>JPEG quality for embedded textures.</summary>
        private const long JPEG_QUALITY = 85L;

        /// <summary>Faces this close to horizontal (|n.z|) are mapped in plan, like floors.</summary>
        private const double PLAN_FACE = 0.7;

        /// <summary>Most images kept in the session cache (each a few tens of KB).</summary>
        private const int TEXTURE_CACHE_LIMIT = 2000;

        /// <summary>
        /// Colour slots in priority order: the property holding the render colour and (when a bitmap is connected) the
        /// texture. The first one the asset has wins.
        /// </summary>
        private static readonly string[] COLOUR_SLOTS =
        {
            "generic_diffuse",            // Generic (and legacy generic presets)
            "opaque_albedo",              // Advanced / Physical opaque
            "metal_f0",                   // Advanced metal
            "layered_diffuse",            // Advanced layered
            "transparent_color",          // Advanced transparent
            "hardwood_color",             // Hardwood (bitmap)
            "ceramic_color", "metal_color", "plasticvinyl_color", "wallpaint_color", "metallicpaint_base_color",
            "concrete_color", "masonrycmu_color", "stone_color", "solidglass_transmittance_custom_color",
            "glazing_transmittance_map", "mirror_tintcolor", "water_tint_color"
        };

        #endregion

        #region Fields

        private readonly bool _extractMaterials;
        private readonly int _textureCap;
        private TextureLocator _locator;
        private TextureOverrideSet _overrides;

        /// <summary>
        /// Texture review ("Review textures…"): find the images but don't decode or embed them; where each was found
        /// is kept in <see cref="_lookups"/>.
        /// </summary>
        private bool _resolveOnly;
        private readonly Dictionary<SceneMaterial, TextureLookup> _lookups = new(ReferenceEqualityComparer.Instance);

        // Per-element temporary streams, parallel to _tmpVertices
        private readonly List<ushort> _tmpMaterial = new(4096);
        private readonly List<Vector2> _tmpUv = new(4096);

        // Output
        private readonly List<ushort> _vertexMaterial = new();
        private readonly List<Vector2> _vertexUv = new();
        private readonly List<SceneMaterial> _materialTable = new();
        private readonly Dictionary<string, byte[]> _textures = new(StringComparer.Ordinal);
        private int _texturedMaterials, _missingTextures, _proceduralTextures, _unreadableTextures;
        private int _overrideCount, _proxiedMaterials, _searchHits, _fallbackColours;
        private readonly Stopwatch _materialTime = new();

        /// <summary>
        /// Images already re-encoded this Revit session: "path|last write|cap" → (entry name, bytes). Lets a live
        /// refresh skip the decode / resize / encode of every texture it has seen.
        /// </summary>
        private static readonly Dictionary<string, (string Entry, byte[] Bytes)> TEXTURE_CACHE = new(StringComparer.OrdinalIgnoreCase);

        #endregion

        /// <summary>
        /// How one face's vertices get their surface coordinates.
        /// </summary>
        private enum UvKind
        {
            /// <summary>Box mapping by the vertex normal (free meshes, failed projections).</summary>
            Box,

            /// <summary>Plan X / Y (floors, roofs, ceilings, near-horizontal planar faces).</summary>
            Plan,

            /// <summary>Planar, not horizontal: U along the face horizontally, V up the face.</summary>
            Wall,

            /// <summary>Curved: the face's own parameters, scaled to metres.</summary>
            Parametric
        }

        /// <summary>
        /// A face's coordinate mapping, worked out once per face.
        /// </summary>
        private readonly struct UvMapping
        {
            public UvKind Kind { get; init; }
            public Face Face { get; init; }
            public XYZ U { get; init; }
            public XYZ V { get; init; }
            public double ScaleU { get; init; }
            public double ScaleV { get; init; }
            public bool Swap { get; init; }

            public static readonly UvMapping BOX = new() { Kind = UvKind.Box };
        }

        #region Per element

        /// <summary>
        /// Clears the per-element streams (with the other temporary buffers).
        /// </summary>
        private void ResetMaterialStreams()
        {
            _tmpMaterial.Clear();
            _tmpUv.Clear();
        }

        /// <summary>
        /// The coordinate mapping of a face (best effort: anything unreadable falls back to box mapping).
        /// </summary>
        private UvMapping MappingOf(Face face)
        {
            if (!_extractMaterials || face == null) { return UvMapping.BOX; }
            try
            {
                if (face is PlanarFace planar)
                {
                    XYZ n = planar.FaceNormal;
                    if (Math.Abs(n.Z) >= PLAN_FACE) { return new UvMapping { Kind = UvKind.Plan }; }
                    XYZ u = XYZ.BasisZ.CrossProduct(n);
                    if (u.GetLength() < 1e-9) { return new UvMapping { Kind = UvKind.Plan }; }
                    u = u.Normalize();
                    return new UvMapping { Kind = UvKind.Wall, U = u, V = n.CrossProduct(u).Normalize() };
                }

                // Curved: parameters scaled by the surface's derivatives at the middle of the face. Whichever
                // parameter runs more vertically becomes V, so coursing stays horizontal on curved walls.
                BoundingBoxUV box = face.GetBoundingBox();
                var middle = new UV((box.Min.U + box.Max.U) * 0.5, (box.Min.V + box.Max.V) * 0.5);
                Transform derivatives = face.ComputeDerivatives(middle);
                double su = derivatives.BasisX.GetLength() * FT, sv = derivatives.BasisY.GetLength() * FT;
                if (!(su > 1e-9) || !(sv > 1e-9) || double.IsInfinity(su) || double.IsInfinity(sv)) { return UvMapping.BOX; }
                bool swap = Math.Abs(derivatives.BasisX.Normalize().Z) > Math.Abs(derivatives.BasisY.Normalize().Z);
                return new UvMapping { Kind = UvKind.Parametric, Face = face, ScaleU = su, ScaleV = sv, Swap = swap };
            }
            catch
            {
                return UvMapping.BOX;
            }
        }

        /// <summary>
        /// Appends the material index and surface coordinates of a mesh just added to the element buffers (after its
        /// normals are final).
        /// </summary>
        /// <param name="points">The mesh's points in its own (symbol / face) coordinates, feet.</param>
        /// <param name="baseIndex">Where the mesh's vertices start in the element buffers.</param>
        /// <param name="mapping">The face's mapping (box for free meshes).</param>
        /// <param name="material">The material index (<see cref="MaterialData.NONE"/> for none).</param>
        private void AppendSurfaceStreams(IList<XYZ> points, int baseIndex, UvMapping mapping, ushort material)
        {
            if (!_extractMaterials) { return; }
            PadMaterialStreams(baseIndex);
            int count = points.Count;
            for (int i = 0; i < count; i++)
            {
                _tmpMaterial.Add(material);
                _tmpUv.Add(material == MaterialData.NONE ? Vector2.Zero : SurfaceCoordinate(points[i], baseIndex + i, mapping));
            }
        }

        /// <summary>
        /// One vertex's surface coordinate (m).
        /// </summary>
        private Vector2 SurfaceCoordinate(XYZ p, int vertex, in UvMapping mapping)
        {
            switch (mapping.Kind)
            {
                case UvKind.Plan:
                    return new Vector2((float)(p.X * FT), (float)(p.Y * FT));

                case UvKind.Wall:
                    return new Vector2((float)(p.DotProduct(mapping.U) * FT), (float)(p.DotProduct(mapping.V) * FT));

                case UvKind.Parametric:
                    try
                    {
                        IntersectionResult hit = mapping.Face.Project(p);
                        if (hit?.UVPoint is UV uv)
                        {
                            var value = new Vector2((float)(uv.U * mapping.ScaleU), (float)(uv.V * mapping.ScaleV));
                            return mapping.Swap ? new Vector2(value.Y, value.X) : value;
                        }
                    }
                    catch
                    {
                        // Fall through to box mapping
                    }
                    goto default;

                default:
                    // Box mapping in the scene by the vertex normal (world-anchored: scene position + origin)
                    SceneVertex v = _tmpVertices[vertex];
                    Vector3 w = v.Position + _origin, n = Vector3.Abs(v.Normal);
                    if (n.Z >= n.X && n.Z >= n.Y) { return new Vector2(w.X, w.Y); }
                    return n.X >= n.Y ? new Vector2(w.Y, w.Z) : new Vector2(w.X, w.Z);
            }
        }

        /// <summary>
        /// Pads the element's material streams up to a vertex count (boxes and proxies carry no material).
        /// </summary>
        private void PadMaterialStreams(int count)
        {
            if (!_extractMaterials) { return; }
            while (_tmpMaterial.Count < count) { _tmpMaterial.Add(MaterialData.NONE); }
            while (_tmpUv.Count < count) { _tmpUv.Add(Vector2.Zero); }
        }

        /// <summary>
        /// Appends the element's streams to the scene's (call where the element's vertices are committed).
        /// </summary>
        private void CommitMaterialStreams()
        {
            if (!_extractMaterials) { return; }
            PadMaterialStreams(_tmpVertices.Count);
            _vertexMaterial.AddRange(_tmpMaterial);
            _vertexUv.AddRange(_tmpUv);
        }

        #endregion

        #region Material table

        /// <summary>
        /// The table index of a material in the current document (added on first use), or <see cref="MaterialData.NONE"/>.
        /// </summary>
        private ushort MaterialIndexOf(Material material)
        {
            if (!_extractMaterials || material == null) { return MaterialData.NONE; }
            long key = material.Id.Value;
            if (_src.MaterialIndex.TryGetValue(key, out ushort cached)) { return cached; }

            ushort index = MaterialData.NONE;
            if (_materialTable.Count < MaterialData.MAX_MATERIALS)
            {
                _materialTime.Start();
                try
                {
                    SceneMaterial entry = ReadAppearance(material);
                    index = (ushort)_materialTable.Count;
                    _materialTable.Add(entry);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"{_src.Describe()}: material “{material.Name}” skipped for Realistic mode: {ex.Message}");
                }
                finally
                {
                    _materialTime.Stop();
                }
            }
            _src.MaterialIndex[key] = index;
            return index;
        }

        /// <summary>
        /// Finds where textures live on this machine (library, Revit.ini paths, remembered search folders) and loads
        /// this model's texture overrides. Logged.
        /// </summary>
        private void PrepareTextures()
        {
            _locator = TextureLocator.Discover(_doc.Application.VersionNumber);
            _locator.SetSearchFolders(_settings.TextureSearchFolders);
            _overrides = TextureOverrideSet.LoadFromModelFolder(ModelFolderResolver.FolderOf(_doc), LinkResolver.HostKey(_doc));
            Utilities.Log_Utils.Write($"Textures: Autodesk library {(_locator.HasLibrary ? string.Join("; ", _locator.LibraryRoots) : "not found")}; " +
                $"additional render appearance paths: {(_locator.ExtraPaths.Count == 0 ? "none" : string.Join("; ", _locator.ExtraPaths))}; " +
                $"search folders: {(_locator.SearchFolders.Count == 0 ? "none" : string.Join("; ", _locator.SearchFolders))}; " +
                $"{_overrides.Documents.Values.Sum(d => d.Count)} override(s) for this model; cap {_textureCap} px.");
        }

        /// <summary>
        /// The override-file document key of the current source: "host", or the link's model key.
        /// </summary>
        private string DocumentKey()
        {
            if (_src == null || _src.Link == 0) { return TextureOverrideSet.HOST; }
            string key = _src.Info?.ModelKey;
            return string.IsNullOrWhiteSpace(key) ? "link:" + _src.Link : key;
        }

        /// <summary>
        /// Reads a material's render colour, texture and placement from its appearance asset, then applies the user's
        /// override (image, proxy or plain colour), an automatic proxy for a missing image, and the shading-colour
        /// fallback when the texture didn't make it in.
        /// </summary>
        private SceneMaterial ReadAppearance(Material material)
        {
            Vector3 shading = ShadingColour(material);
            string uniqueId = null;
            try { uniqueId = material.UniqueId; }
            catch { /* none */ }

            var entry = new SceneMaterial
            {
                Name = material.Name ?? string.Empty,
                UniqueId = uniqueId,
                Link = _src.Link,
                MaterialId = material.Id.Value,
                Colour = shading
            };
            TextureOverride choice = _overrides?.Find(DocumentKey(), uniqueId, entry.Name);

            Visual.Asset asset = null;
            ElementId assetId = material.AppearanceAssetId;
            if (assetId != null && assetId != ElementId.InvalidElementId && _src.Doc.GetElement(assetId) is AppearanceAssetElement appearance)
            {
                asset = appearance.GetRenderingAsset();
            }
            if (asset == null || asset.Size == 0)
            {
                // No readable appearance (legacy presets with no properties): the shading colour stands in
                entry.Schema = asset?.Name ?? string.Empty;
                ApplyReflectivity(entry, asset, entry.Schema, material);
                Finish(entry, choice, shading);
                return entry;
            }

            entry.Schema = SchemaName(asset);

            // The appearance's own tint (Appearance tab "Tint": the whole look, colour and image)
            entry.AssetTint = ReadTint(asset);

            // The colour slot
            Visual.AssetProperty slot = null;
            foreach (string name in COLOUR_SLOTS)
            {
                slot = asset.FindByName(name);
                if (slot != null) { break; }
            }
            slot ??= FirstColourProperty(asset);

            if (slot != null)
            {
                if (ReadColour(slot) is Vector3 colour) { entry.Colour = colour; }
                if (slot.Name == "hardwood_color" && FindInt(asset, "hardwood_tint_enabled") == 1 && FindColourAny(asset, "hardwood_tint_color") is Vector3 tint)
                {
                    entry.Tint = tint;
                }
                ReadTexture(slot, entry, choice);
            }

            // Generic: how much of the image shows over the colour
            if (entry.TextureState == TextureState.Embedded && FindDouble(asset, "generic_diffuse_image_fade") is double fade && slot?.Name == "generic_diffuse")
            {
                entry.Fade = (float)Math.Clamp(fade, 0.0, 1.0);
            }

            ApplyReflectivity(entry, asset, entry.Schema, material);
            Finish(entry, choice, shading);
            return entry;
        }

        /// <summary>
        /// The last steps for a material: the user's override, an automatic proxy (missing / unreadable images only,
        /// when "Proxy textures for missing images" is on), and the shading colour in place of the render colour when
        /// no texture made it in (the render colour is often a white placeholder when a bitmap was connected).
        /// </summary>
        private void Finish(SceneMaterial entry, TextureOverride choice, Vector3 shading)
        {
            bool found = entry.TextureState == TextureState.Embedded;
            if (choice != null)
            {
                _overrideCount++;
                if (choice.ColourOnly)
                {
                    DropTexture(entry);
                    entry.TextureOrigin = TextureOrigins.OVERRIDE;
                    found = false;
                }
                else if (!string.IsNullOrWhiteSpace(choice.Proxy))
                {
                    DropTexture(entry);
                    entry.Proxy = ProxyCatalog.Normalise(choice.Proxy);
                    entry.TextureOrigin = TextureOrigins.PROXY;
                    _proxiedMaterials++;
                    found = false;
                }
                else if (!string.IsNullOrWhiteSpace(choice.Image) && entry.TextureSource == null && !found)
                {
                    // A material with no bitmap of its own (a plain colour the user gave an image): 1 m repeats.
                    // (With a bitmap, ReadTexture already tried the image before the locator.)
                    found = TryUseImage(entry, choice.Image, TextureOrigins.OVERRIDE) || found;
                }
            }

            if (!found && entry.Proxy == null && choice == null && _settings.ProxyMissingTextures
                && entry.TextureState is TextureState.Missing or TextureState.Unreadable)
            {
                entry.Proxy = ProxyCatalog.Suggest(entry.Name, entry.Schema);
                if (entry.Proxy != null)
                {
                    entry.TextureOrigin = TextureOrigins.PROXY;
                    _proxiedMaterials++;
                }
            }

            bool lostTexture = entry.TextureState is TextureState.Missing or TextureState.Unreadable or TextureState.Procedural
                || (choice?.ColourOnly == true && entry.TextureSource != null);
            if (!found && lostTexture)
            {
                if (entry.Colour != shading) { entry.RenderColour = entry.Colour; }
                entry.Colour = shading;
                entry.AssetTint = null; // the shaded look has no appearance tint (it would tint the shading colour twice)
                _fallbackColours++;
            }
        }

        /// <summary>
        /// Clears an embedded texture from an entry (an override chose a proxy or plain colour instead). The image stays
        /// in the snapshot only if another material uses it (unreferenced images are not written).
        /// </summary>
        private void DropTexture(SceneMaterial entry)
        {
            if (entry.TextureState == TextureState.Embedded)
            {
                entry.TextureState = entry.TextureSource == null ? TextureState.None : TextureState.Missing;
                _texturedMaterials--;
            }
            entry.Texture = null;
        }

        /// <summary>
        /// Uses an image file for an entry (an override or a search hit): embedded, or in resolve-only mode just
        /// checked. False if the file is gone or unreadable (the entry is then unchanged).
        /// </summary>
        private bool TryUseImage(SceneMaterial entry, string path, string origin)
        {
            bool exists;
            try { exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
            catch { exists = false; }
            if (!exists)
            {
                Utilities.Log_Utils.Write($"{_src.Describe()}: override image of “{entry.Name}” not found: {path}");
                return false;
            }

            if (_resolveOnly)
            {
                _lookups[entry] = new TextureLookup(entry.TextureSource ?? path, path, TextureFound.Override, false);
            }
            else
            {
                (string Entry, byte[] Bytes)? image = EmbedImage(path);
                if (image == null) { return false; }
                _textures[image.Value.Entry] = image.Value.Bytes;
                entry.Texture = image.Value.Entry;
            }
            if (entry.TextureState != TextureState.Embedded) { _texturedMaterials++; }
            entry.TextureState = TextureState.Embedded;
            entry.TextureOrigin = origin;
            return true;
        }

        /// <summary>
        /// An asset's tint when its toggle is on (<c>common_Tint_toggle</c> + <c>common_Tint_color</c>), else null.
        /// </summary>
        private static Vector3? ReadTint(Visual.Asset asset)
        {
            try
            {
                if (asset.FindByName("common_Tint_toggle") is Visual.AssetPropertyBoolean { Value: true }
                    && FindColourAny(asset, "common_Tint_color") is Vector3 tint)
                {
                    return tint;
                }
            }
            catch { /* none */ }
            return null;
        }

        /// <summary>
        /// Reflection probes round: reads the material's reflections with <see cref="ReflectivityReader"/> (every
        /// schema; mirror / water name rules) and stores the raw values. Glass keeps <see cref="SceneMaterial.Reflectivity"/>
        /// (its sky sheen, as in build A/B); everything else gets <see cref="SceneMaterial.Shine"/> (tiered in the app).
        /// </summary>
        private void ApplyReflectivity(SceneMaterial entry, Visual.Asset asset, string schema, Material material)
        {
            ReflectivityInfo info = ReflectivityReader.Read(asset, schema ?? string.Empty, material, RoughnessMapAverage);
            entry.ReflectSource = string.IsNullOrEmpty(info.Source) ? null : info.Source;
            if (!info.Mapped) { return; }

            if (info.Glass)
            {
                entry.Reflectivity = info.Strength;
                return;
            }
            entry.Shine = info.Strength;
            entry.Roughness = info.Roughness;
            entry.Metallic = info.Metallic;
            entry.Water = info.Water;
            entry.WaterBump = info.Water ? info.WaterBump : 0f;
        }

        /// <summary>Average brightness of roughness maps by resolved path (shared across extractions; null = unreadable).</summary>
        private static readonly Dictionary<string, double?> ROUGHNESS_AVERAGES = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The average brightness of a connected roughness bitmap (Prism), found like a colour texture and cached by
        /// path. Null when the path is empty, missing or not decodable.
        /// </summary>
        private double? RoughnessMapAverage(Visual.Asset bitmap)
        {
            try
            {
                if (_locator == null) { return null; }
                if (bitmap.FindByName("unifiedbitmap_Bitmap") is not Visual.AssetPropertyString path || string.IsNullOrWhiteSpace(path.Value)) { return null; }
                TextureLookup lookup = _locator.Resolve(path.Value, DocumentFolder());
                if (lookup.Path == null) { return null; }
                lock (ROUGHNESS_AVERAGES)
                {
                    if (!ROUGHNESS_AVERAGES.TryGetValue(lookup.Path, out double? average))
                    {
                        average = ReflectivityReader.AverageLuminance(lookup.Path);
                        ROUGHNESS_AVERAGES[lookup.Path] = average;
                    }
                    return average;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The texture on a colour slot: the connected bitmap's placement, tint and invert, then the image: the user's
        /// override image if any, else found by the locator (… → remembered search folders), resized and embedded.
        /// A connected procedural map (noise, checker…) leaves the plain colour.
        /// </summary>
        private void ReadTexture(Visual.AssetProperty slot, SceneMaterial entry, TextureOverride choice)
        {
            Visual.Asset bitmap = null;
            try
            {
                if (slot.NumberOfConnectedProperties > 0) { bitmap = slot.GetSingleConnectedAsset(); }
            }
            catch
            {
                bitmap = null;
            }
            if (bitmap == null) { return; }

            if (bitmap.FindByName("unifiedbitmap_Bitmap") is not Visual.AssetPropertyString path)
            {
                entry.TextureState = TextureState.Procedural;
                _proceduralTextures++;
                return;
            }

            string raw = path.Value;
            if (string.IsNullOrWhiteSpace(raw)) { return; } // "" = no texture, not a missing one
            entry.TextureSource = raw;

            // Placement (real-world size, offset, angle), tint and invert
            entry.ScaleU = (float)(Metres(bitmap, "texture_RealWorldScaleX", 1.0) / Math.Max(FindDouble(bitmap, "texture_UScale") ?? 1.0, 1e-3));
            entry.ScaleV = (float)(Metres(bitmap, "texture_RealWorldScaleY", 1.0) / Math.Max(FindDouble(bitmap, "texture_VScale") ?? 1.0, 1e-3));
            entry.OffsetU = Metres(bitmap, "texture_RealWorldOffsetX", 0.0);
            entry.OffsetV = Metres(bitmap, "texture_RealWorldOffsetY", 0.0);
            entry.Angle = (float)(FindDouble(bitmap, "texture_WAngle") ?? 0.0);
            Vector3 tint = entry.Tint;
            if (ReadTint(bitmap) is Vector3 t) { tint *= t; }
            if (FindDouble(bitmap, "unifiedbitmap_RGBAmount") is double amount && amount > 0.0)
            {
                tint *= (float)Math.Min(amount, 4.0);
            }
            entry.Tint = tint;
            entry.Invert = bitmap.FindByName("unifiedbitmap_Invert") is Visual.AssetPropertyBoolean { Value: true };

            // The user's image wins over the asset's path
            if (!string.IsNullOrWhiteSpace(choice?.Image) && TryUseImage(entry, choice.Image, TextureOrigins.OVERRIDE)) { return; }

            // The image
            TextureLookup lookup = _locator.Resolve(raw, DocumentFolder());
            entry.Autodesk = lookup.AutodeskLibrary;
            if (_resolveOnly) { _lookups[entry] = lookup; }
            if (lookup.Path == null)
            {
                entry.TextureState = TextureState.Missing;
                _missingTextures++;
                if (!_resolveOnly) { Utilities.Log_Utils.Write($"{_src.Describe()}: texture of “{entry.Name}” not found: {raw}"); }
                return;
            }

            string origin = lookup.Found == TextureFound.SearchFolder ? TextureOrigins.SEARCH : TextureOrigins.ASSET;
            if (lookup.Found == TextureFound.SearchFolder) { _searchHits++; }
            if (_resolveOnly)
            {
                entry.TextureState = TextureState.Embedded; // "found": nothing is decoded in review
                entry.TextureOrigin = origin;
                _texturedMaterials++;
                return;
            }

            (string Entry, byte[] Bytes)? image = EmbedImage(lookup.Path);
            if (image == null)
            {
                entry.TextureState = TextureState.Unreadable;
                _unreadableTextures++;
                return;
            }
            _textures[image.Value.Entry] = image.Value.Bytes;
            entry.Texture = image.Value.Entry;
            entry.TextureState = TextureState.Embedded;
            entry.TextureOrigin = origin;
            _texturedMaterials++;
        }

        /// <summary>
        /// The image re-encoded for embedding (longest side ≤ the cap, JPEG), from the session cache when unchanged.
        /// Null if it can't be decoded.
        /// </summary>
        private (string Entry, byte[] Bytes)? EmbedImage(string path)
        {
            string key;
            try { key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{_textureCap}"; }
            catch { return null; }
            lock (TEXTURE_CACHE)
            {
                if (TEXTURE_CACHE.TryGetValue(key, out (string Entry, byte[] Bytes) cached)) { return cached; }
            }

            try
            {
                byte[] source = File.ReadAllBytes(path);
                using var input = new MemoryStream(source);
                using var image = System.Drawing.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
                int width = image.Width, height = image.Height;
                double scale = Math.Min(1.0, (double)_textureCap / Math.Max(width, height));
                int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));

                using var resized = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(resized))
                {
                    g.Clear(System.Drawing.Color.White); // transparent pixels (PNG) read as white, not black
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    using var wrap = new System.Drawing.Imaging.ImageAttributes();
                    wrap.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY); // no dark seams at the edges
                    g.DrawImage(image, new System.Drawing.Rectangle(0, 0, w, h), 0, 0, width, height, System.Drawing.GraphicsUnit.Pixel, wrap);
                }

                using var output = new MemoryStream();
                System.Drawing.Imaging.ImageCodecInfo jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                if (jpeg != null)
                {
                    using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                    parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JPEG_QUALITY);
                    resized.Save(output, jpeg, parameters);
                }
                else
                {
                    resized.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                byte[] bytes = output.ToArray();
                string entry = BimGoFormat.TEXTURE_FOLDER + HashName(path.ToLowerInvariant() + "|" + _textureCap) + ".jpg";
                lock (TEXTURE_CACHE)
                {
                    if (TEXTURE_CACHE.Count >= TEXTURE_CACHE_LIMIT) { TEXTURE_CACHE.Clear(); }
                    TEXTURE_CACHE[key] = (entry, bytes);
                }
                return (entry, bytes);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture {path} could not be read: {ex.Message}");
                return null;
            }
        }

        private static string HashName(string text)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
        }

        private string DocumentFolder()
        {
            try
            {
                string path = _src.Doc.PathName;
                return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The material table, streams and images for the snapshot (logged), or empty when not extracted.
        /// </summary>
        private MaterialData BuildMaterials()
        {
            if (!_extractMaterials) { return MaterialData.Empty; }
            if (_vertexMaterial.Count != _vertices.Count || _vertexUv.Count != _vertices.Count)
            {
                Utilities.Log_Utils.Write($"Materials dropped: streams out of step ({_vertexMaterial.Count} / {_vertexUv.Count} for {_vertices.Count} vertices).");
                return MaterialData.Empty;
            }

            long bytes = _textures.Values.Sum(b => (long)b.Length);
            Utilities.Log_Utils.Write($"Materials: {_materialTable.Count} used, {_texturedMaterials} textured ({_textures.Count} images, {bytes / 1024} KB at ≤ {_textureCap} px), " +
                $"{_missingTextures} missing, {_unreadableTextures} unreadable, {_proceduralTextures} procedural; {_searchHits} found in search folders, " +
                $"{_overrideCount} overridden, {_proxiedMaterials} proxied, {_fallbackColours} on the shading colour; {_materialTime.Elapsed.TotalSeconds:F1}s reading appearances. " +
                $"Library {(_locator.HasLibrary ? "found" : "not found")}, {_locator.ExtraPaths.Count} additional render appearance path(s).");
            return _materialTable.Count == 0
                ? MaterialData.Empty
                : new MaterialData
                {
                    Materials = _materialTable.ToArray(),
                    VertexMaterial = _vertexMaterial.ToArray(),
                    VertexUv = _vertexUv.ToArray(),
                    Textures = new Dictionary<string, byte[]>(_textures, StringComparer.Ordinal),
                    TextureMaxSize = _textureCap
                };
        }

        #endregion

        #region Asset reading

        private static string SchemaName(Visual.Asset asset)
        {
            try
            {
                if (asset.FindByName("BaseSchema") is Visual.AssetPropertyString s && !string.IsNullOrWhiteSpace(s.Value)) { return s.Value; }
            }
            catch { /* fall through */ }
            return asset.Name ?? string.Empty;
        }

        /// <summary>
        /// The material's shading colour (0–1), else light grey.
        /// </summary>
        private static Vector3 ShadingColour(Material material)
        {
            try
            {
                DB.Color c = material.Color;
                if (c != null && c.IsValid) { return new Vector3(c.Red, c.Green, c.Blue) / 255f; }
            }
            catch { /* fall through */ }
            return new Vector3(0.8f);
        }

        /// <summary>
        /// A colour property's RGB (Double4 or the legacy Double3), or null.
        /// </summary>
        private static Vector3? ReadColour(Visual.AssetProperty property)
        {
            try
            {
                switch (property)
                {
                    case Visual.AssetPropertyDoubleArray4d c4:
                        IList<double> rgba = c4.GetValueAsDoubles();
                        if (rgba != null && rgba.Count >= 3) { return Clamp01(new Vector3((float)rgba[0], (float)rgba[1], (float)rgba[2])); }
                        break;
                    case Visual.AssetPropertyDoubleArray3d c3:
                        XYZ xyz = c3.GetValueAsXYZ();
                        if (xyz != null) { return Clamp01(new Vector3((float)xyz.X, (float)xyz.Y, (float)xyz.Z)); }
                        break;
                }
            }
            catch { /* unreadable */ }
            return null;

            static Vector3 Clamp01(Vector3 v) => Vector3.Clamp(v, Vector3.Zero, Vector3.One);
        }

        private static Vector3? FindColourAny(Visual.Asset asset, string name)
        {
            Visual.AssetProperty property = asset.FindByName(name);
            return property == null ? null : ReadColour(property);
        }

        /// <summary>
        /// For schemas the slot list doesn't know: the first colour property named "…_color" or "…_diffuse".
        /// </summary>
        private static Visual.AssetProperty FirstColourProperty(Visual.Asset asset)
        {
            for (int i = 0; i < asset.Size; i++)
            {
                try
                {
                    Visual.AssetProperty property = asset.Get(i);
                    string name = property?.Name ?? string.Empty;
                    if ((name.EndsWith("_color", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_diffuse", StringComparison.OrdinalIgnoreCase))
                        && property is Visual.AssetPropertyDoubleArray4d or Visual.AssetPropertyDoubleArray3d)
                    {
                        return property;
                    }
                }
                catch { /* skip */ }
            }
            return null;
        }

        private static int? FindInt(Visual.Asset asset, string name) => asset.FindByName(name) switch
        {
            Visual.AssetPropertyInteger i => i.Value,
            Visual.AssetPropertyEnum e => e.Value,
            _ => null
        };

        /// <summary>
        /// A distance property in metres (its own unit converted), or a fallback.
        /// </summary>
        private static float Metres(Visual.Asset asset, string name, double fallback)
        {
            try
            {
                switch (asset.FindByName(name))
                {
                    case Visual.AssetPropertyDistance distance:
                        double metres = UnitUtils.Convert(distance.Value, distance.GetUnitTypeId(), UnitTypeId.Meters);
                        if (double.IsFinite(metres)) { return (float)metres; }
                        break;
                    case Visual.AssetPropertyDouble d:
                        return (float)(d.Value * 0.0254); // older assets: inches
                    case Visual.AssetPropertyFloat f:
                        return (float)(f.Value * 0.0254);
                }
            }
            catch { /* fall back */ }
            return (float)fallback;
        }

        #endregion
    }
}
