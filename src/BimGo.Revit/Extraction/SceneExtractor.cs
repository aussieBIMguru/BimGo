using System.Diagnostics;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using BimGo.Utilities;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Builds the immutable <see cref="SceneData"/> snapshot from the active Revit document.
    /// Runs on the Revit API thread only. Converts feet to metres and shifts everything to a
    /// scene-local origin (rounded to whole metres) so float precision holds up on large sites.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Constants

        /// <summary>Feet to metres.</summary>
        public const double FT = 0.3048;

        /// <summary>Face triangulation level of detail (0 coarse .. 1 fine).</summary>
        private const double TRIANGULATION_LOD = 0.3;

        /// <summary>Default colour when no material is found (light grey).</summary>
        private const uint DEFAULT_COLOUR = 0xFFC8C8C8;

        #endregion

        #region Fields

        private readonly Document _doc;
        private readonly LaunchSettings _settings;
        private readonly Options _geometryOptions;
        private readonly OperationProgress _progress;
        private Vector3 _origin;
        private PhasePair _phases = new();

        // Output buffers
        private readonly List<SceneVertex> _vertices = new(1 << 18);
        private readonly List<uint> _indices = new(1 << 19);
        private readonly List<ElementRecord> _elements = new(4096);

        // Per-element temporary buffers (reused)
        private readonly List<SceneVertex> _tmpVertices = new(4096);
        private readonly List<uint> _tmpEmissive = new(4096);
        private readonly List<int> _tmpOpaque = new(8192);
        private readonly List<int> _tmpTransparent = new(1024);
        private Vector3[] _normalAccumulator = new Vector3[1024];
        private int _tmpTriangles;
        private bool _overLimit;
        private bool _thresholdActive;


        // The model being extracted (the host, or one link instance) and the extracted link instances
        private SourceModel _src;
        private readonly List<LinkInfo> _links = new();

        // Helper geometry (IES light-source cones, clearance zones…): lower-case subcategory name fragments, or null when off
        private readonly string[] _helperKeywords;

        // Optional extra parameters (null when none were picked)
        private readonly string[] _extraParameters;
        private readonly ParameterTableBuilder _parameters;

        // Stats
        private int _proxyCount;
        private int _skippedCount;

        #endregion

        /// <summary>
        /// Creates the extractor.
        /// </summary>
        /// <param name="doc">The document to extract.</param>
        /// <param name="settings">The launch settings.</param>
        /// <param name="progress">Optional progress / cancellation.</param>
        /// <param name="resolveOnly">Texture review: read materials and find images, embed nothing.</param>
        private SceneExtractor(Document doc, LaunchSettings settings, OperationProgress progress, bool resolveOnly = false)
        {
            _doc = doc;
            _settings = settings;
            _progress = progress;
            if (settings.ExtraParameters != null && settings.ExtraParameters.Count > 0)
            {
                _extraParameters = settings.ExtraParameters.ToArray();
                _parameters = new ParameterTableBuilder();
            }
            _geometryOptions = new Options
            {
                DetailLevel = ViewDetailLevel.Medium,
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            };
            _extractMaterials = settings.ExtractTextures || resolveOnly;
            _resolveOnly = resolveOnly;
            _textureCap = MaterialData.NearestTextureSize(settings.TextureMaxSize);
            _emissiveKeywords = (settings.EmissiveKeywords ?? LaunchSettings.DefaultEmissiveKeywords())
                .Select(k => k.Trim().ToLowerInvariant())
                .Where(k => k.Length > 0)
                .ToArray();
            if (settings.SkipHelperGeometry)
            {
                _helperKeywords = (settings.HelperSubcategoryKeywords ?? new List<string>())
                    .Select(k => k.Trim().ToLowerInvariant())
                    .Where(k => k.Length > 0)
                    .ToArray();
            }
        }

        #region Public API

        /// <summary>
        /// Extracts a scene snapshot.
        /// </summary>
        /// <param name="uiDoc">The active UIDocument.</param>
        /// <param name="settings">The launch settings.</param>
        /// <param name="progress">
        /// Optional progress / cancellation: the extraction fills the bar up to 85 % (the caller writes the file after
        /// it). Cancelling throws <see cref="OperationCanceledException"/>; nothing in the model is changed.
        /// </param>
        /// <returns>The SceneData.</returns>
        public static SceneData Extract(UIDocument uiDoc, LaunchSettings settings, OperationProgress progress = null)
        {
            var extractor = new SceneExtractor(uiDoc.Document, settings, progress);
            return extractor.Run(uiDoc);
        }

        /// <summary>
        /// Describes where the player will start (used by the Options dialog).
        /// </summary>
        /// <param name="uiDoc">The active UIDocument.</param>
        /// <returns>A short description.</returns>
        public static string DescribeSpawn(UIDocument uiDoc)
        {
            if (uiDoc?.ActiveView is View3D view3D && !view3D.IsTemplate)
            {
                return view3D.IsPerspective
                    ? $"Active 3D view “{view3D.Name}”"
                    : $"Random point on the ground (“{view3D.Name}” is orthographic)";
            }
            return "Random point on the ground (no 3D view active)";
        }

        #endregion

        #region Main pass

        /// <summary>
        /// Runs the extraction.
        /// </summary>
        private SceneData Run(UIDocument uiDoc)
        {
            var stopwatch = Stopwatch.StartNew();
            IReadOnlyList<CategoryDef> catalog = CategoryCatalog.All;

            // The existing / new phases first: the walkthrough shows the model as it stands in the new phase
            _phases = PhaseResolver.Resolve(_doc, uiDoc.ActiveView, _settings);

            // Materials and textures (opt-in): find where texture images live on this machine, as Revit does
            if (_extractMaterials) { PrepareTextures(); }

            // The host and the ticked links, then their candidate elements (needed for the origin)
            List<SourceModel> sources = PrepareSources(uiDoc, out DB.View view);
            SourceModel host = sources[0];
            _progress?.Begin("Finding elements", 0.0, 0.08);
            List<(SourceModel Source, CategoryDef Def, List<Element> Elements)> work = Gather(sources, view);

            // Scene origin: median of element centres, rounded to whole metres (robust against outliers)
            _origin = ComputeOrigin(work.SelectMany(w => w.Elements.Select(e => (e, w.Source.Transform))));

            // Level names and elevations (host levels; links only name their elements' levels), and the rooms
            _progress?.Begin("Reading levels and rooms", 0.08, 0.12);
            _progress?.ThrowIfCancelled();
            LevelInfo[] levels = CollectLevels();
            _levelElevations = levels.Select(l => l.Elevation).ToArray();
            Phase phase = _phases.New;
            var rooms = new List<RoomInfo>(CollectRooms(host, phase));
            foreach (SourceModel source in sources)
            {
                if (source.Link == 0) { continue; }
                CollectLevelNames(source);
                RoomInfo[] linkRooms = CollectRooms(source, source.Phases.New);
                source.Info.RoomCount = linkRooms.Length;
                rooms.AddRange(linkRooms);
            }

            // Extract geometry (host first: the app looks host elements up by id, so they keep the lower indices)
            int[] counts = new int[catalog.Count];
            bool[] loaded = new bool[catalog.Count];
            int totalElements = work.Sum(w => w.Elements.Count);
            int processed = 0;
            _progress?.Begin("Extracting geometry", 0.12, 0.85);
            foreach ((SourceModel source, CategoryDef def, List<Element> elements) in work)
            {
                _src = source;
                loaded[def.Index] = true;
                foreach (Element element in elements)
                {
                    // Every 32 elements: move the bar and stop here if the user cancelled
                    if (_progress != null && (processed++ & 31) == 0)
                    {
                        _progress.Step(processed, totalElements);
                        _progress.Detail($"{processed:N0} of {totalElements:N0} elements · {def.Label}{(source.Link == 0 ? string.Empty : " · " + source.Info.Label)}");
                        _progress.ThrowIfCancelled();
                    }
                    try
                    {
                        if (ExtractElement(element, def))
                        {
                            counts[def.Index]++;
                            if (source.Info != null) { source.Info.ElementCount++; }
                        }
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"{source.Describe()}: element {element.Id.Value} skipped: {ex.Message}");
                    }
                }
            }
            _src = host;
            _progress?.Step(1.0);
            foreach (LinkInfo link in _links)
            {
                Utilities.Log_Utils.Write($"Link {link.Index} “{link.Name}”: {link.ElementCount} elements, {link.RoomCount} rooms, phases {link.ExistingPhaseName ?? "none"} → {link.PhaseName ?? "none"}.");
            }

            // Bounds of everything
            Aabb bounds = Aabb.Empty;
            foreach (ElementRecord record in _elements)
            {
                bounds.Include(record.Bounds);
            }
            if (!bounds.IsValid) { bounds = new Aabb(new Vector3(-10, -10, 0), new Vector3(10, 10, 3)); }

            stopwatch.Stop();
            Utilities.Log_Utils.Write($"Extracted {_elements.Count} elements, {_indices.Count / 3} triangles, {rooms.Count} rooms, {_links.Count} links " +
                $"in {stopwatch.Elapsed.TotalSeconds:F1}s (proxies {_proxyCount}, skipped {_skippedCount}). Phases: {_phases.Existing?.Name ?? "none"} → {phase?.Name ?? "none"}.");

            return new SceneData
            {
                Vertices = _vertices.ToArray(),
                Indices = _indices.ToArray(),
                Elements = _elements.ToArray(),
                Levels = levels,
                Rooms = rooms.ToArray(),
                Links = _links.ToArray(),
                PhaseId = phase?.Id.Value ?? -1,
                PhaseName = phase?.Name,
                ExistingPhaseId = _phases.Existing?.Id.Value ?? -1,
                ExistingPhaseName = _phases.Existing?.Name,
                PhaseNote = _phases.Note,
                Spawn = ResolveSpawn(uiDoc),
                Bounds = bounds,
                OriginOffset = _origin,
                ModelTitle = _doc.Title,
                CommentsPath = ResolveCommentsPath(),
                Provenance = BuildProvenance(),
                Site = BuildSite(uiDoc),
                Parameters = _parameters?.Build() ?? ParameterTable.Empty,
                Lighting = BuildLighting(),
                Materials = BuildMaterials(),
                CategoryLoaded = loaded,
                CategoryElementCounts = counts,
                Settings = _settings,
                SourceView = view?.Name,
                ProxyCount = _proxyCount,
                SkippedCount = _skippedCount,
                ExtractionTime = stopwatch.Elapsed
            };
        }

        /// <summary>
        /// The host source, then the link instances ticked for this model in the Options dialog (none by default),
        /// minus links the active view hides when extracting the active view only. In a 3D view the host's geometry
        /// is read through the view (its subcategory visibility and detail level).
        /// </summary>
        /// <param name="uiDoc">The active UIDocument.</param>
        /// <param name="view">The active view when "active view only" applies (null = extract by category).</param>
        private List<SourceModel> PrepareSources(UIDocument uiDoc, out DB.View view)
        {
            view = _settings.ActiveViewOnly ? ViewScope.Resolve(uiDoc, _doc) : null;
            if (_settings.ActiveViewOnly)
            {
                Utilities.Log_Utils.Write(view != null
                    ? $"Active view only: “{view.Name}” ({view.ViewType})."
                    : "Active view only: no view that shows model elements is available; extracting by category instead.");
            }

            var host = new SourceModel
            {
                Doc = _doc,
                Phases = _phases,
                GeometryOptions = view is View3D ? new Options { View = view, ComputeReferences = false, IncludeNonVisibleObjects = false } : _geometryOptions
            };
            _src = host;
            var sources = new List<SourceModel> { host };
            foreach (LinkCandidate candidate in LinkResolver.Selected(_doc, _settings))
            {
                if (view != null && ViewScope.HidesLink(view, candidate.InstanceId))
                {
                    Utilities.Log_Utils.Write($"Link “{candidate.Name}” is hidden in the active view: skipped.");
                    continue;
                }
                sources.Add(CreateLinkSource(candidate, sources.Count));
            }
            return sources;
        }

        /// <summary>
        /// The candidate elements per source and enabled definition (by category, or what the active view shows).
        /// Moves the current progress stage and stops if cancelled. Shared by the extraction and the texture review.
        /// </summary>
        private List<(SourceModel Source, CategoryDef Def, List<Element> Elements)> Gather(List<SourceModel> sources, DB.View view)
        {
            var enabled = new HashSet<string>(_settings.EnabledCategories ?? new List<string>());
            var work = new List<(SourceModel Source, CategoryDef Def, List<Element> Elements)>();
            int sourceNumber = 0;
            foreach (SourceModel source in sources)
            {
                _progress?.Step(sourceNumber++, sources.Count);
                _progress?.Detail(source.Link == 0 ? _doc.Title : source.Info.Label);
                _progress?.ThrowIfCancelled();
                if (view != null)
                {
                    GatherVisible(source, view, work);
                    continue;
                }

                foreach (CategoryDef def in CategoryCatalog.All)
                {
                    if (!enabled.Contains(def.Key)) { continue; }
                    try
                    {
                        FilteredElementCollector collector = CategoryResolver.Collect(source.Doc, def);
                        if (collector == null) { continue; }

                        var elements = new List<Element>();
                        foreach (Element element in collector)
                        {
                            if (IsExtractable(element, source.Phases)) { elements.Add(element); }
                        }
                        work.Add((source, def, elements));
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"{source.Describe()}: {def.Key} skipped: {ex.Message}");
                    }
                }
            }
            return work;
        }

        /// <summary>
        /// Filters out elements that shouldn't appear (view-specific, secondary design options, and anything not
        /// standing in the new phase: demolished by it or built after it).
        /// </summary>
        private static bool IsExtractable(Element element, PhasePair phases)
        {
            if (element.ViewSpecific || element.Category == null) { return false; }

            // Secondary design options are not part of the main model
            if (element.DesignOption is DesignOption option && !option.IsPrimary) { return false; }

            // The model as it stands in the new phase
            if (!PhaseResolver.StandsIn(element, phases)) { return false; }

            // Component stairs: runs, landings and supports are collected as their own elements
            if (element is Autodesk.Revit.DB.Architecture.Stairs stairs && stairs.GetStairsRuns().Count > 0) { return false; }

            return true;
        }

        /// <summary>
        /// Computes the scene origin as the rounded median of element bounding-box centres.
        /// </summary>
        /// <param name="elements">Each element with its model's transform into the host (null for host elements).</param>
        private static Vector3 ComputeOrigin(IEnumerable<(Element Element, Transform Transform)> elements)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach ((Element element, Transform transform) in elements)
            {
                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box == null) { continue; }
                XYZ centre = (box.Min + box.Max) * 0.5;
                if (transform != null) { centre = transform.OfPoint(centre); }
                xs.Add(centre.X * FT);
                ys.Add(centre.Y * FT);
            }
            if (xs.Count == 0) { return Vector3.Zero; }

            xs.Sort();
            ys.Sort();
            return new Vector3((float)Math.Round(xs[xs.Count / 2]), (float)Math.Round(ys[ys.Count / 2]), 0f);
        }

        /// <summary>
        /// Collects levels sorted by elevation (ProjectElevation is relative to the internal origin, like geometry).
        /// </summary>
        private LevelInfo[] CollectLevels()
        {
            var levels = new List<LevelInfo>();
            foreach (Level level in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>())
            {
                float elevation = (float)(level.ProjectElevation * FT) - _origin.Z;
                levels.Add(new LevelInfo(level.Name, elevation));
                _src.LevelNames[level.Id.Value] = level.Name;
            }
            levels.Sort((a, b) => a.Elevation.CompareTo(b.Elevation));
            return levels.ToArray();
        }

        #endregion

        #region Linked models

        /// <summary>
        /// The model an element is being extracted from: the host, or one link instance (its document, total transform
        /// and phases), with the per-document caches (material and category colours, level names).
        /// </summary>
        private sealed class SourceModel
        {
            /// <summary>The document.</summary>
            public Document Doc { get; init; }

            /// <summary>Link coordinates → host internal coordinates (feet), or null for the host.</summary>
            public Transform Transform { get; init; }

            /// <summary>0 for the host, n for <c>Links[n - 1]</c>.</summary>
            public int Link { get; init; }

            /// <summary>The phases the model is shown in (a link's phases are matched to the host's by name).</summary>
            public PhasePair Phases { get; init; } = new();

            /// <summary>The link's record in the snapshot (null for the host).</summary>
            public LinkInfo Info { get; init; }

            /// <summary>Why linked elements can't be moved or copied (shown by the Gizmo / Clone guns).</summary>
            public string ReadOnlyReason { get; init; }

            /// <summary>Material colours and glow by material id (ids are per document).</summary>
            public Dictionary<long, MaterialLook> MaterialLooks { get; } = new();

            /// <summary>Realistic-mode material table indices by material id (textures extracted only).</summary>
            public Dictionary<long, ushort> MaterialIndex { get; } = new();

            /// <summary>Category colours by category id.</summary>
            public Dictionary<long, uint> CategoryColours { get; } = new();

            /// <summary>Level names by level id.</summary>
            public Dictionary<long, string> LevelNames { get; } = new();

            /// <summary>Helper-geometry verdicts by graphics style id.</summary>
            public Dictionary<long, bool> HelperStyles { get; } = new();

            /// <summary>The geometry options (a 3D view's for the host in active-view-only mode), or null for the defaults.</summary>
            public Options GeometryOptions { get; init; }

            /// <summary>The RevitLinkInstance's id in the host (links only).</summary>
            public long LinkInstanceId { get; init; }

            /// <summary>"Host" or "Link n “name”", for the log.</summary>
            public string Describe() => Info == null ? "Host" : $"Link {Info.Index} “{Info.Name}”";
        }

        /// <summary>
        /// Prepares a ticked link instance: its phases (by the host's phase names, else the link's last phase and the
        /// one before) and its <see cref="LinkInfo"/> record.
        /// </summary>
        /// <param name="candidate">The loaded link instance.</param>
        /// <param name="index">Its link number (1-based).</param>
        private SourceModel CreateLinkSource(LinkCandidate candidate, int index)
        {
            Document linkDoc = candidate.Document;
            PhasePair phases = PhaseResolver.Resolve(linkDoc, activeView: null, _phases.Existing?.Name, _phases.New?.Name);
            Transform transform = candidate.Transform ?? Transform.Identity;

            var info = new LinkInfo
            {
                Index = index,
                Name = candidate.Name,
                Title = linkDoc.Title ?? candidate.FileName,
                InstanceId = candidate.InstanceId,
                InstanceUniqueId = candidate.UniqueId,
                OriginX = transform.Origin.X * FT,
                OriginY = transform.Origin.Y * FT,
                OriginZ = transform.Origin.Z * FT,
                BasisX = ToVector(transform.BasisX),
                BasisY = ToVector(transform.BasisY),
                BasisZ = ToVector(transform.BasisZ),
                PhaseName = phases.New?.Name,
                ExistingPhaseName = phases.Existing?.Name
            };
            try { info.ModelKey = linkDoc.ProjectInformation?.UniqueId ?? string.Empty; }
            catch { /* leave empty */ }
            try { info.ModelPath = linkDoc.IsModelInCloud ? string.Empty : linkDoc.PathName ?? string.Empty; }
            catch { /* leave empty */ }

            _links.Add(info);
            return new SourceModel
            {
                Doc = linkDoc,
                Transform = transform.IsIdentity ? null : transform,
                Link = index,
                Phases = phases,
                Info = info,
                LinkInstanceId = candidate.InstanceId,
                ReadOnlyReason = $"In linked model “{info.Label}” (read-only)"
            };
        }

        /// <summary>
        /// Names a link's levels (its elements show their own level; the walkthrough's level list stays the host's).
        /// </summary>
        private static void CollectLevelNames(SourceModel source)
        {
            try
            {
                foreach (Level level in new FilteredElementCollector(source.Doc).OfClass(typeof(Level)).Cast<Level>())
                {
                    source.LevelNames[level.Id.Value] = level.Name;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{source.Describe()}: level names unavailable: {ex.Message}");
            }
        }

        /// <summary>
        /// Active-view-only: buckets every model element the view shows (of the host, or of a link through the host
        /// view) by catalog definition, ignoring category ticks, phases and design options. Categories the catalog
        /// doesn't list go to "Other (active view)".
        /// </summary>
        private void GatherVisible(SourceModel source, DB.View view, List<(SourceModel Source, CategoryDef Def, List<Element> Elements)> work)
        {
            FilteredElementCollector collector = source.Link == 0
                ? ViewScope.CollectHost(_doc, view)
                : ViewScope.CollectLink(_doc, view, source.LinkInstanceId);
            if (collector == null)
            {
                Utilities.Log_Utils.Write($"{source.Describe()}: the active view can't list its elements (needs Revit 2024+ for links): skipped.");
                return;
            }

            var buckets = new Dictionary<int, List<Element>>();
            int skipped = 0;
            try
            {
                foreach (Element element in collector)
                {
                    CategoryDef def = ViewScope.DefinitionOf(element);
                    if (def == null) { skipped++; continue; }
                    if (!buckets.TryGetValue(def.Index, out List<Element> list)) { buckets[def.Index] = list = new List<Element>(); }
                    list.Add(element);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{source.Describe()}: active view collection failed: {ex.Message}");
            }

            foreach (CategoryDef def in CategoryCatalog.All)
            {
                if (buckets.TryGetValue(def.Index, out List<Element> elements)) { work.Add((source, def, elements)); }
            }
            Utilities.Log_Utils.Write($"{source.Describe()}: {buckets.Values.Sum(b => b.Count)} elements visible in the view ({skipped} not model geometry).");
        }

        /// <summary>
        /// True if a geometry object sits on helper geometry: the Light Source subcategory (IES / photometric cones),
        /// or a subcategory whose name contains one of the keywords (clearance zones, spray cones…). Cached per style.
        /// </summary>
        private bool IsHelperStyle(ElementId styleId)
        {
            if (styleId == null || styleId == ElementId.InvalidElementId) { return false; }
            long key = styleId.Value;
            if (_src.HelperStyles.TryGetValue(key, out bool cached)) { return cached; }

            bool helper = false;
            try
            {
                Category category = (_src.Doc.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory;
                if (category != null)
                {
                    if (category.Id.Value == (long)BuiltInCategory.OST_LightingFixtureSource)
                    {
                        helper = true;
                    }
                    else if (category.Parent != null)
                    {
                        // Keywords only match subcategories, never a whole category
                        string name = category.Name?.ToLowerInvariant() ?? string.Empty;
                        foreach (string keyword in _helperKeywords)
                        {
                            if (name.Contains(keyword, StringComparison.Ordinal)) { helper = true; break; }
                        }
                    }
                    if (helper) { Utilities.Log_Utils.Write($"{_src.Describe()}: helper geometry on “{category.Parent?.Name}: {category.Name}” left out."); }
                }
            }
            catch
            {
                helper = false;
            }

            _src.HelperStyles[key] = helper;
            return helper;
        }

        private static Vector3 ToVector(XYZ v) => v == null ? Vector3.Zero : new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        #endregion

        #region Element extraction

        /// <summary>
        /// Extracts one element into the output buffers.
        /// </summary>
        /// <returns>True if a record was added.</returns>
        private bool ExtractElement(Element element, CategoryDef def)
        {
            GeometryElement geometry = element.get_Geometry(_src.GeometryOptions ?? _geometryOptions);
            if (geometry == null) { return false; }

            // Reset per-element state
            _tmpVertices.Clear();
            _tmpEmissive.Clear();
            _tmpOpaque.Clear();
            _tmpTransparent.Clear();
            ResetMaterialStreams();
            _tmpTriangles = 0;
            _overLimit = false;
            _thresholdActive = def.ThresholdApplies;
            BeginFixture(element);

            uint fallback = FallbackColour(element);
            Walk(geometry, _src.Transform ?? Transform.Identity, fallback);

            bool isProxy = false;
            if (_overLimit)
            {
                _tmpVertices.Clear();
                _tmpEmissive.Clear();
                _tmpOpaque.Clear();
                _tmpTransparent.Clear();
                ResetMaterialStreams();

                if (_settings.OverLimit == OverLimitMode.Skip)
                {
                    _skippedCount++;
                    return false;
                }

                if (!AddBoundingBoxProxy(element, fallback)) { return false; }
                isProxy = true;
                _proxyCount++;
            }

            // Nothing to draw
            if (_tmpOpaque.Count == 0 && _tmpTransparent.Count == 0) { return false; }

            // Fixtures without a glowing material: guess the lens (downward faces at the bottom of a raised fixture)
            if (_fixture && !isProxy) { MarkFallbackLens(); }

            // Commit
            int vertexBase = _vertices.Count;
            CommitEmissive(vertexBase);
            CommitMaterialStreams();
            Aabb bounds = Aabb.Empty;
            foreach (SceneVertex vertex in _tmpVertices)
            {
                bounds.Include(vertex.Position);
            }
            _vertices.AddRange(_tmpVertices);

            int opaqueStart = _indices.Count;
            foreach (int index in _tmpOpaque) { _indices.Add((uint)(index + vertexBase)); }
            int transparentStart = _indices.Count;
            foreach (int index in _tmpTransparent) { _indices.Add((uint)(index + vertexBase)); }

            string blockReason = MoveBlockReasonOf(element, out Vector3 pivot);

            var record = new ElementRecord
            {
                ElementId = element.Id.Value,
                UniqueId = element.UniqueId ?? string.Empty,
                HostId = HostIdOf(element),
                Name = SafeName(element),
                CategoryName = element.Category?.Name ?? def.Label,
                FamilyType = FamilyTypeOf(element),
                LevelName = LevelNameOf(element),
                CategoryIndex = def.Index,
                OpaqueStart = opaqueStart,
                OpaqueCount = _tmpOpaque.Count,
                TransparentStart = transparentStart,
                TransparentCount = _tmpTransparent.Count,
                Bounds = bounds,
                IsProxy = isProxy,
                Movable = blockReason == null,
                MoveBlockReason = blockReason,
                Pivot = pivot,
                Phase = PhaseResolver.RoleOf(element, _src.Phases),
                Link = _src.Link
            };

            if (_parameters != null) { AddParameters(element); }
            if (_fixture) { AddFixtureLight(_elements.Count, bounds); }
            _elements.Add(record);
            return true;
        }

        /// <summary>
        /// Recursively walks a geometry element, tessellating solids and meshes.
        /// Instances are expanded with an explicit accumulated transform.
        /// </summary>
        private void Walk(GeometryElement geometry, Transform transform, uint fallback)
        {
            foreach (GeometryObject geometryObject in geometry)
            {
                if (_overLimit) { return; }
                if (_helperKeywords != null && IsHelperStyle(geometryObject.GraphicsStyleId)) { continue; }

                switch (geometryObject)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        AddSolid(solid, transform, fallback);
                        break;

                    case Mesh mesh when mesh.NumTriangles > 0:
                        if (CountTriangles(mesh.NumTriangles))
                        {
                            MaterialLook look = MaterialLookOf(mesh.MaterialElementId, fallback);
                            AddMesh(mesh, transform, null, look.Colour, EmissiveOf(look), UvMapping.BOX, look.Material);
                        }
                        break;

                    case GeometryInstance instance:
                        GeometryElement symbolGeometry = instance.GetSymbolGeometry();
                        if (symbolGeometry != null)
                        {
                            Walk(symbolGeometry, transform.Multiply(instance.Transform), fallback);
                        }
                        break;

                    case GeometryElement nested:
                        Walk(nested, transform, fallback);
                        break;
                }
            }
        }

        /// <summary>
        /// Adds all faces of a solid.
        /// </summary>
        private void AddSolid(Solid solid, Transform transform, uint fallback)
        {
            foreach (Face face in solid.Faces)
            {
                Mesh mesh = face.Triangulate(TRIANGULATION_LOD);
                if (mesh == null || mesh.NumTriangles == 0) { continue; }
                if (!CountTriangles(mesh.NumTriangles)) { return; }

                MaterialLook look = MaterialLookOf(face.MaterialElementId, fallback);
                XYZ planarNormal = face is PlanarFace planar ? planar.FaceNormal : null;
                UvMapping mapping = look.Material == MaterialData.NONE ? UvMapping.BOX : MappingOf(face);
                AddMesh(mesh, transform, planarNormal, look.Colour, EmissiveOf(look), mapping, look.Material);
            }
        }

        /// <summary>
        /// Adds triangles to the running count and flags the element when it goes over the threshold.
        /// </summary>
        /// <returns>False if the element is now over the limit.</returns>
        private bool CountTriangles(int triangles)
        {
            _tmpTriangles += triangles;
            if (_thresholdActive && _tmpTriangles > _settings.TriangleThreshold)
            {
                _overLimit = true;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Appends a Revit mesh to the temporary element buffers.
        /// Winding is made consistent with Revit's outward normals so back-face detection works in the shader.
        /// </summary>
        /// <param name="mesh">The mesh (from a face triangulation or a free mesh).</param>
        /// <param name="transform">The accumulated transform to model coordinates.</param>
        /// <param name="planarNormal">The face normal for planar faces (local), or null.</param>
        /// <param name="colour">The RGBA8 colour.</param>
        /// <param name="emissive">Packed glow (<see cref="LightingData.PackEmissive"/>), or 0. Glowing surfaces are always opaque.</param>
        /// <param name="mapping">How the vertices get surface coordinates (Realistic mode).</param>
        /// <param name="material">The Realistic-mode material index, or <see cref="MaterialData.NONE"/>.</param>
        private void AddMesh(Mesh mesh, Transform transform, XYZ planarNormal, uint colour, uint emissive, UvMapping mapping, ushort material)
        {
            IList<XYZ> points = mesh.Vertices;
            int vertexCount = points.Count;
            int triangleCount = mesh.NumTriangles;
            int baseIndex = _tmpVertices.Count;
            if (emissive != 0) { colour |= 0xFF000000u; }
            bool transparent = (colour >> 24) < 250;

            // Per-vertex glow, parallel to _tmpVertices (proxies and boxes add none: padded with 0 here)
            while (_tmpEmissive.Count < baseIndex) { _tmpEmissive.Add(0u); }
            for (int i = 0; i < vertexCount; i++) { _tmpEmissive.Add(emissive); }
            List<int> target = transparent ? _tmpTransparent : _tmpOpaque;
            bool identity = transform.IsIdentity;

            // Which normals does Revit provide?
            int normalMode = 0; // 0 none, 1 per point, 2 per facet, 3 one per face
            try
            {
                if (mesh.NumberOfNormals > 0)
                {
                    normalMode = mesh.DistributionOfNormals switch
                    {
                        DistributionOfNormals.AtEachPoint => mesh.NumberOfNormals >= vertexCount ? 1 : 0,
                        DistributionOfNormals.OnEachFacet => mesh.NumberOfNormals >= triangleCount ? 2 : 0,
                        DistributionOfNormals.OnePerFace => 3,
                        _ => 0
                    };
                }
            }
            catch
            {
                normalMode = 0;
            }

            Vector3 faceReference = Vector3.Zero;
            if (normalMode == 3) { faceReference = ToSceneVector(mesh.GetNormal(0), transform, identity); }
            else if (planarNormal != null) { faceReference = ToSceneVector(planarNormal, transform, identity); }

            // Vertices (normals filled after the triangles are known)
            if (_normalAccumulator.Length < vertexCount) { _normalAccumulator = new Vector3[Math.Max(vertexCount, _normalAccumulator.Length * 2)]; }
            Vector3[] pointNormals = normalMode == 1 ? new Vector3[vertexCount] : null;
            for (int i = 0; i < vertexCount; i++)
            {
                XYZ point = identity ? points[i] : transform.OfPoint(points[i]);
                _tmpVertices.Add(new SceneVertex(ToScenePoint(point), Vector3.UnitZ, colour));
                _normalAccumulator[i] = Vector3.Zero;
                if (pointNormals != null) { pointNormals[i] = ToSceneVector(mesh.GetNormal(i), transform, identity); }
            }

            // Triangles, with winding matched to the reference normal
            for (int t = 0; t < triangleCount; t++)
            {
                MeshTriangle triangle = mesh.get_Triangle(t);
                int a = (int)triangle.get_Index(0);
                int b = (int)triangle.get_Index(1);
                int c = (int)triangle.get_Index(2);

                Vector3 pa = _tmpVertices[baseIndex + a].Position;
                Vector3 pb = _tmpVertices[baseIndex + b].Position;
                Vector3 pc = _tmpVertices[baseIndex + c].Position;
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);

                Vector3 reference = normalMode switch
                {
                    1 => pointNormals[a] + pointNormals[b] + pointNormals[c],
                    2 => ToSceneVector(mesh.GetNormal(t), transform, identity),
                    _ => faceReference
                };

                if (reference != Vector3.Zero && Vector3.Dot(cross, reference) < 0f)
                {
                    (b, c) = (c, b);
                    cross = -cross;
                }

                target.Add(baseIndex + a);
                target.Add(baseIndex + b);
                target.Add(baseIndex + c);

                _normalAccumulator[a] += cross;
                _normalAccumulator[b] += cross;
                _normalAccumulator[c] += cross;
            }

            // Final normals
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 normal = pointNormals != null ? pointNormals[i] : _normalAccumulator[i];
                float length = normal.Length();
                normal = length > 1e-12f ? normal / length : Vector3.UnitZ;

                SceneVertex vertex = _tmpVertices[baseIndex + i];
                vertex.Normal = normal;
                _tmpVertices[baseIndex + i] = vertex;
            }

            // Realistic mode: material index and surface coordinates (from the mesh's own points, so textures move with families)
            AppendSurfaceStreams(points, baseIndex, mapping, material);

        }

        /// <summary>
        /// Replaces an element with its (world axis-aligned) bounding box.
        /// </summary>
        /// <returns>True if a box was added.</returns>
        private bool AddBoundingBoxProxy(Element element, uint colour)
        {
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null) { return false; }

            Aabb bounds = Aabb.Empty;
            Transform transform = box.Transform ?? Transform.Identity;
            if (_src.Transform != null) { transform = _src.Transform.Multiply(transform); }
            for (int i = 0; i < 8; i++)
            {
                var corner = new XYZ(
                    (i & 1) == 0 ? box.Min.X : box.Max.X,
                    (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                    (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                bounds.Include(ToScenePoint(transform.OfPoint(corner)));
            }

            AppendBox(bounds, colour | 0xFF000000);
            return true;
        }

        /// <summary>
        /// Appends an axis-aligned box (24 vertices, 12 triangles) to the temporary buffers.
        /// </summary>
        private void AppendBox(Aabb box, uint colour)
        {
            Vector3 n = box.Min, x = box.Max;
            Span<Vector3> normals = stackalloc Vector3[] { -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ };
            Span<Vector3> quad = stackalloc Vector3[4];

            for (int f = 0; f < 6; f++)
            {
                Vector3 normal = normals[f];
                int baseIndex = _tmpVertices.Count;
                switch (f)
                {
                    case 0: quad[0] = new(n.X, n.Y, n.Z); quad[1] = new(n.X, n.Y, x.Z); quad[2] = new(n.X, x.Y, x.Z); quad[3] = new(n.X, x.Y, n.Z); break;
                    case 1: quad[0] = new(x.X, n.Y, n.Z); quad[1] = new(x.X, x.Y, n.Z); quad[2] = new(x.X, x.Y, x.Z); quad[3] = new(x.X, n.Y, x.Z); break;
                    case 2: quad[0] = new(n.X, n.Y, n.Z); quad[1] = new(x.X, n.Y, n.Z); quad[2] = new(x.X, n.Y, x.Z); quad[3] = new(n.X, n.Y, x.Z); break;
                    case 3: quad[0] = new(n.X, x.Y, n.Z); quad[1] = new(n.X, x.Y, x.Z); quad[2] = new(x.X, x.Y, x.Z); quad[3] = new(x.X, x.Y, n.Z); break;
                    case 4: quad[0] = new(n.X, n.Y, n.Z); quad[1] = new(n.X, x.Y, n.Z); quad[2] = new(x.X, x.Y, n.Z); quad[3] = new(x.X, n.Y, n.Z); break;
                    default: quad[0] = new(n.X, n.Y, x.Z); quad[1] = new(x.X, n.Y, x.Z); quad[2] = new(x.X, x.Y, x.Z); quad[3] = new(n.X, x.Y, x.Z); break;
                }

                for (int i = 0; i < 4; i++) { _tmpVertices.Add(new SceneVertex(quad[i], normal, colour)); }

                // Ensure counter-clockwise winding about the outward normal
                bool ccw = Vector3.Dot(Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]), normal) > 0f;
                if (ccw)
                {
                    _tmpOpaque.Add(baseIndex); _tmpOpaque.Add(baseIndex + 1); _tmpOpaque.Add(baseIndex + 2);
                    _tmpOpaque.Add(baseIndex); _tmpOpaque.Add(baseIndex + 2); _tmpOpaque.Add(baseIndex + 3);
                }
                else
                {
                    _tmpOpaque.Add(baseIndex); _tmpOpaque.Add(baseIndex + 2); _tmpOpaque.Add(baseIndex + 1);
                    _tmpOpaque.Add(baseIndex); _tmpOpaque.Add(baseIndex + 3); _tmpOpaque.Add(baseIndex + 2);
                }
            }
        }

        #endregion

        #region Colour

        /// <summary>
        /// Gets a material's colour (with transparency in alpha) and glow (self-illumination, name keywords), cached.
        /// </summary>
        private MaterialLook MaterialLookOf(ElementId materialId, uint fallback)
        {
            if (materialId == null || materialId == ElementId.InvalidElementId) { return new MaterialLook(fallback, 0u, false, MaterialData.NONE); }

            long key = materialId.Value;
            if (_src.MaterialLooks.TryGetValue(key, out MaterialLook cached)) { return cached; }

            uint colour = fallback;
            uint selfIllumination = 0u;
            bool keyword = false;
            ushort index = MaterialData.NONE;
            if (_src.Doc.GetElement(materialId) is Material material)
            {
                DB.Color c = material.Color;
                if (c != null && c.IsValid)
                {
                    // A material named "mirror" is a mirror even when modelled with a glass appearance: drawn opaque
                    int alpha = ReflectivityReader.IsMirrorByName(material) ? 255 : Math.Clamp(255 - (int)(material.Transparency * 2.55), 64, 255);
                    colour = Pack(c.Red, c.Green, c.Blue, (byte)alpha);
                }
                selfIllumination = ReadSelfIllumination(material);
                keyword = MatchesEmissiveKeyword(material.Name);
                index = MaterialIndexOf(material);
            }

            var look = new MaterialLook(colour, selfIllumination, keyword, index);
            _src.MaterialLooks[key] = look;
            return look;
        }

        /// <summary>
        /// The element's fallback colour: its category material, else light grey.
        /// </summary>
        private uint FallbackColour(Element element)
        {
            Category category = element.Category;
            if (category == null) { return DEFAULT_COLOUR; }

            long key = category.Id.Value;
            if (_src.CategoryColours.TryGetValue(key, out uint cached)) { return cached; }

            uint colour = DEFAULT_COLOUR;
            try
            {
                if (category.Material is Material material && material.Color is DB.Color c && c.IsValid)
                {
                    colour = Pack(c.Red, c.Green, c.Blue, 255);
                }
            }
            catch
            {
                // Some categories throw on Material; keep the default
            }

            _src.CategoryColours[key] = colour;
            return colour;
        }

        /// <summary>
        /// Packs RGBA bytes.
        /// </summary>
        private static uint Pack(byte r, byte g, byte b, byte a) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

        #endregion

        #region Metadata

        private static string SafeName(Element element)
        {
            try { return string.IsNullOrWhiteSpace(element.Name) ? "(unnamed)" : element.Name; }
            catch { return "(unnamed)"; }
        }

        private string FamilyTypeOf(Element element)
        {
            try
            {
                if (element is FamilyInstance instance && instance.Symbol != null)
                {
                    return $"{instance.Symbol.FamilyName}: {instance.Symbol.Name}";
                }
                if (_src.Doc.GetElement(element.GetTypeId()) is ElementType type)
                {
                    return string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName}: {type.Name}";
                }
            }
            catch
            {
                // Fall through
            }
            return "—";
        }

        private string LevelNameOf(Element element)
        {
            ElementId levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM })
                {
                    Parameter parameter = element.get_Parameter(bip);
                    if (parameter != null && parameter.StorageType == StorageType.ElementId && parameter.AsElementId() != ElementId.InvalidElementId)
                    {
                        levelId = parameter.AsElementId();
                        break;
                    }
                }
            }

            if (levelId != null && _src.LevelNames.TryGetValue(levelId.Value, out string name)) { return name; }
            return "—";
        }

        /// <summary>
        /// The host's ElementId value (doors and windows in walls, face-hosted families...), or 0. Always 0 for linked
        /// elements (read-only: nothing cascades from them, and their ids live in another model's namespace).
        /// </summary>
        private long HostIdOf(Element element)
        {
            if (_src.Link > 0) { return 0; }
            try
            {
                if (element is FamilyInstance instance && instance.Host is Element host && host is not RevitLinkInstance)
                {
                    return host.Id.Value;
                }
            }
            catch
            {
                // No host
            }
            return 0;
        }

        /// <summary>
        /// Adds the picked extra parameters of an element (instance value first, else its type's) and closes its row.
        /// Called once per extracted element, in element order.
        /// </summary>
        private void AddParameters(Element element)
        {
            Element type = null;
            bool typeLooked = false;
            foreach (string name in _extraParameters)
            {
                try
                {
                    Parameter parameter = element.LookupParameter(name);
                    if (parameter == null)
                    {
                        if (!typeLooked)
                        {
                            ElementId typeId = element.GetTypeId();
                            type = typeId != null && typeId != ElementId.InvalidElementId ? _src.Doc.GetElement(typeId) : null;
                            typeLooked = true;
                        }
                        parameter = type?.LookupParameter(name);
                    }
                    if (parameter != null) { _parameters.Add(name, ParameterText(parameter)); }
                }
                catch
                {
                    // Unreadable parameter: leave it out for this element
                }
            }
            _parameters.EndElement();
        }

        /// <summary>
        /// A parameter's display text (as Revit shows it), or an empty string when it has no value.
        /// </summary>
        private string ParameterText(Parameter parameter)
        {
            if (!parameter.HasValue) { return string.Empty; }
            string text = parameter.AsValueString();
            if (!string.IsNullOrEmpty(text)) { return text; }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return parameter.AsDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    ElementId id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) { return string.Empty; }
                    return _src.Doc.GetElement(id)?.Name ?? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }

        #endregion

        #region Provenance and site

        /// <summary>
        /// Where the snapshot comes from (model identity for matching files back to models).
        /// </summary>
        private ModelProvenance BuildProvenance()
        {
            var provenance = new ModelProvenance
            {
                ModelTitle = _doc.Title ?? string.Empty,
                RevitVersion = _doc.Application?.VersionNumber ?? string.Empty,
                AddinVersion = Globals.ADDIN_VERSION,
                User = Environment.UserName,
                Machine = Environment.MachineName,
                ExtractedUtc = DateTime.UtcNow
            };

            try { provenance.ModelKey = _doc.ProjectInformation?.UniqueId ?? string.Empty; }
            catch (Exception ex) { Utilities.Log_Utils.Write($"Model key unavailable: {ex.Message}"); }

            try { provenance.IsWorkshared = _doc.IsWorkshared; }
            catch { /* leave false */ }

            try
            {
                provenance.IsCloud = _doc.IsModelInCloud;
                if (provenance.IsCloud)
                {
                    ModelPath cloudPath = _doc.GetCloudModelPath();
                    provenance.CloudProjectId = cloudPath.GetProjectGUID().ToString();
                    provenance.CloudModelId = cloudPath.GetModelGUID().ToString();
                }
                else
                {
                    provenance.ModelPath = _doc.PathName ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Model path details unavailable: {ex.Message}");
            }
            return provenance;
        }

        /// <summary>
        /// True north, project base point, survey point and the internal → shared transform (double precision: the
        /// shared position of the internal origin and the rotation, for the coordinate readout).
        /// </summary>
        private SiteInfo BuildSite(UIDocument uiDoc)
        {
            var site = new SiteInfo();
            CaptureLocation(site, uiDoc);
            site.ProjectBasePoint = SitePointOf(() => BasePoint.GetProjectBasePoint(_doc));
            site.SurveyPoint = SitePointOf(() => BasePoint.GetSurveyPoint(_doc));

            try
            {
                ProjectPosition position = _doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                site.TrueNorthAngle = (float)position.Angle;
                site.SharedEast = position.EastWest * FT;
                site.SharedNorth = position.NorthSouth * FT;
                site.SharedElevation = position.Elevation * FT;
                site.SharedAngle = position.Angle;
                site.HasSharedTransform = true;

                // The angle's sign convention is checked against the survey / base point (flipped if they disagree)
                if (SiteCoordinates.VerifySharedAngle(site)) { Utilities.Log_Utils.Write("Shared coordinates: rotation sign flipped to match the survey point."); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Shared coordinates unavailable: {ex.Message}");
            }
            return site;
        }

        /// <summary>
        /// The site location (latitude, longitude, time zone, place name) and the launch view's sun-study start time,
        /// for the walkthrough's sun and shadows. Best effort: anything missing is left empty and logged.
        /// </summary>
        private void CaptureLocation(SiteInfo site, UIDocument uiDoc)
        {
            try
            {
                SiteLocation location = _doc.SiteLocation;
                if (location != null)
                {
                    // Revit stores latitude / longitude in radians, east and north positive; the time zone in hours
                    site.Latitude = location.Latitude * 180.0 / Math.PI;
                    site.Longitude = location.Longitude * 180.0 / Math.PI;
                    site.TimeZone = location.TimeZone;
                    site.PlaceName = location.PlaceName ?? string.Empty;
                    site.HasLocation = double.IsFinite(site.Latitude) && double.IsFinite(site.Longitude);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Site location unavailable: {ex.Message}");
            }

            try
            {
                DB.View view = uiDoc?.ActiveView;
                SunAndShadowSettings sun = view?.SunAndShadowSettings;
                if (sun != null)
                {
                    // Revit hands the study time back as UTC on some versions: convert it to the site's clock time
                    DateTime start = sun.StartDateAndTime;
                    if (start.Kind == DateTimeKind.Utc && site.HasLocation) { start = start.AddHours(site.TimeZone); }
                    site.SunStart = start.ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sun settings unavailable: {ex.Message}");
            }

            Utilities.Log_Utils.Write(site.HasLocation
                ? $"Site: {site.PlaceName} ({site.Latitude:0.####}°, {site.Longitude:0.####}°, UTC{site.TimeZone:+0.##;-0.##}); sun start {(string.IsNullOrEmpty(site.SunStart) ? "none" : site.SunStart)}."
                : "Site: no location.");
        }

        private static SitePoint SitePointOf(Func<BasePoint> get)
        {
            try
            {
                BasePoint point = get();
                if (point == null) { return null; }
                return new SitePoint { Position = ToMetres(point.Position), SharedPosition = ToMetres(point.SharedPosition) };
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Base point unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>Feet to metres without the scene offset (Revit internal / shared coordinates).</summary>
        private static Vector3 ToMetres(XYZ p) => p == null ? Vector3.Zero : new Vector3((float)(p.X * FT), (float)(p.Y * FT), (float)(p.Z * FT));

        #endregion

        #region Movability

        /// <summary>
        /// Decides whether the Gizmo / Clone guns may transform the element, and captures its rotation pivot.
        /// Only point-based loadable family instances that Revit will let us move freely in plan qualify.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <param name="pivot">The location point in scene coordinates (zero when not movable).</param>
        /// <returns>Null when movable, else a short reason for the HUD.</returns>
        private string MoveBlockReasonOf(Element element, out Vector3 pivot)
        {
            pivot = Vector3.Zero;
            if (_src.Link > 0) { return _src.ReadOnlyReason; }
            try
            {
                if (element is not FamilyInstance instance) { return "Not a loadable family"; }
                if (instance.Symbol?.Family is Family family && family.IsInPlace) { return "In-place family"; }
                if (instance.SuperComponent != null) { return "Nested in another family"; }
                if (element.GroupId != ElementId.InvalidElementId) { return "Part of a group"; }
                if (element.Pinned) { return "Pinned"; }
                if (instance.Host is Wall) { return "Hosted by a wall"; }
                if (element.Location is not LocationPoint location) { return "No location point"; }

                pivot = ToScenePoint(location.Point);
                return null;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Movability check failed for {element.Id.Value}: {ex.Message}");
                return "Unknown";
            }
        }

        #endregion

        #region Phase and rooms

        /// <summary>
        /// Collects a model's placed, bounded rooms (finish boundaries, arcs tessellated) for the HUD readout.
        /// Rooms of the working phase are preferred; if none match, all placed rooms are used.
        /// </summary>
        /// <param name="source">The host or a link (its rooms are transformed into the host and tagged with the link).</param>
        /// <param name="phase">The source's new phase, or null.</param>
        private RoomInfo[] CollectRooms(SourceModel source, Phase phase)
        {
            _src = source;
            var all = new List<(RoomInfo Room, long PhaseId)>();
            try
            {
                var options = new SpatialElementBoundaryOptions
                {
                    SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
                };

                var collector = new FilteredElementCollector(source.Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType();
                foreach (Element element in collector)
                {
                    if (element is not Autodesk.Revit.DB.Architecture.Room room) { continue; }
                    try
                    {
                        RoomInfo info = BuildRoom(room, options);
                        if (info == null) { continue; }
                        long roomPhase = room.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId()?.Value ?? -1;
                        all.Add((info, roomPhase));
                    }
                    catch (Exception ex)
                    {
                        Utilities.Log_Utils.Write($"{source.Describe()}: room {room.Id.Value} skipped: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"{source.Describe()}: room collection failed: {ex.Message}");
            }

            if (phase != null && all.Any(r => r.PhaseId == phase.Id.Value))
            {
                return all.Where(r => r.PhaseId == phase.Id.Value).Select(r => r.Room).ToArray();
            }
            return all.Select(r => r.Room).ToArray();
        }

        /// <summary>
        /// Builds one room's plan loops and vertical extent, or null if it is unplaced, unbounded or redundant.
        /// </summary>
        private RoomInfo BuildRoom(Autodesk.Revit.DB.Architecture.Room room, SpatialElementBoundaryOptions options)
        {
            if (room.Location == null || room.Area <= 1e-6) { return null; }

            IList<IList<BoundarySegment>> segments = room.GetBoundarySegments(options);
            if (segments == null || segments.Count == 0) { return null; }

            var loops = new List<Vector2[]>();
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            var points = new List<Vector2>();

            foreach (IList<BoundarySegment> loop in segments)
            {
                points.Clear();
                foreach (BoundarySegment segment in loop)
                {
                    Curve curve = segment.GetCurve();
                    if (curve == null) { continue; }
                    IList<XYZ> tessellated = curve.Tessellate();

                    // Each curve's last point is the next curve's first: skip it
                    for (int i = 0; i < tessellated.Count - 1; i++)
                    {
                        Vector3 p = ToScenePoint(_src.Transform == null ? tessellated[i] : _src.Transform.OfPoint(tessellated[i]));
                        var p2 = new Vector2(p.X, p.Y);
                        points.Add(p2);
                        min = Vector2.Min(min, p2);
                        max = Vector2.Max(max, p2);
                    }
                }
                if (points.Count >= 3) { loops.Add(points.ToArray()); }
            }
            if (loops.Count == 0) { return null; }

            // Vertical extent from the room's bounding box (accounts for base offset and upper limit)
            // (Links only rotate in plan, so a link's transform shifts heights by its origin's Z.)
            double linkZ = _src.Transform?.Origin.Z ?? 0.0;
            BoundingBoxXYZ box = room.get_BoundingBox(null);
            float bottom, top;
            if (box != null)
            {
                bottom = (float)((box.Min.Z + linkZ) * FT) - _origin.Z;
                top = (float)((box.Max.Z + linkZ) * FT) - _origin.Z;
            }
            else
            {
                double baseZ = (room.Level?.ProjectElevation ?? 0.0) + room.BaseOffset + linkZ;
                bottom = (float)(baseZ * FT) - _origin.Z;
                top = bottom + (float)(room.UnboundedHeight * FT);
            }
            if (top - bottom < 0.1f) { top = bottom + 2.4f; }

            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
            return new RoomInfo
            {
                Number = string.IsNullOrWhiteSpace(room.Number) ? "—" : room.Number,
                Name = string.IsNullOrWhiteSpace(name) ? "Room" : name,
                Loops = loops.ToArray(),
                Min = min,
                Max = max,
                BottomZ = bottom,
                TopZ = top,
                Link = _src.Link
            };
        }

        #endregion

        #region Spawn and paths

        /// <summary>
        /// Uses the active perspective 3D view's eye, else null (the game picks a random valid point).
        /// </summary>
        private SpawnInfo ResolveSpawn(UIDocument uiDoc)
        {
            DB.View activeView;
            try { activeView = uiDoc.ActiveView; }
            catch { return null; } // not the active document (live refresh): the app keeps its own position

            if (activeView is View3D view3D && !view3D.IsTemplate && view3D.IsPerspective)
            {
                ViewOrientation3D orientation = view3D.GetOrientation();
                Vector3 forward = Vector3.Normalize(new Vector3(
                    (float)orientation.ForwardDirection.X, (float)orientation.ForwardDirection.Y, (float)orientation.ForwardDirection.Z));

                return new SpawnInfo
                {
                    Eye = ToScenePoint(orientation.EyePosition),
                    Yaw = MathF.Atan2(forward.Y, forward.X),
                    Pitch = MathF.Asin(Math.Clamp(forward.Z, -1f, 1f)),
                    Source = view3D.Name
                };
            }
            return null;
        }

        /// <summary>
        /// The comments path in the model's BimGo folder (<c>%LocalAppData%\BimGo\Models\&lt;title&gt;_&lt;hash&gt;\comments.json</c>),
        /// with the bookmarks, sun and visibility files beside it. Prepares the folder: older sidecars beside the
        /// model or in the old Comments folder are copied in once, and with sharing on newer shared copies are taken.
        /// </summary>
        public string ResolveCommentsPath() => ModelFolderResolver.PrepareCommentsPath(_doc, _settings);

        #endregion

        #region Conversion

        /// <summary>Converts a model point (feet) to scene-local metres.</summary>
        private Vector3 ToScenePoint(XYZ p) => new(
            (float)(p.X * FT - _origin.X),
            (float)(p.Y * FT - _origin.Y),
            (float)(p.Z * FT - _origin.Z));

        /// <summary>Transforms a direction into the scene (unit length not guaranteed).</summary>
        private static Vector3 ToSceneVector(XYZ v, Transform transform, bool identity)
        {
            XYZ w = identity ? v : transform.OfVector(v);
            return new Vector3((float)w.X, (float)w.Y, (float)w.Z);
        }

        #endregion
    }
}
