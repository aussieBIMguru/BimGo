using BimGo.Scene;
using BimGo.Utilities;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// One material in the "Review textures…" window: how the next extraction would read it (with the overrides as
    /// they stand when the review ran), and where its image was found.
    /// </summary>
    internal sealed class TextureReviewRow
    {
        /// <summary>The material as the extraction reads it (resolve-only: <see cref="SceneMaterial.Texture"/> is null).</summary>
        public SceneMaterial Material { get; init; }

        /// <summary>The override-file document key ("host" or the link's model key).</summary>
        public string DocumentKey { get; init; } = TextureOverrideSet.HOST;

        /// <summary>"Host" or the link's label.</summary>
        public string ModelLabel { get; init; } = "Host";

        /// <summary>The model folder (deep-scan hits are absolute, so this is only for display), or null.</summary>
        public string DocumentFolder { get; init; }

        /// <summary>The image file found on this machine (or the override image), else null.</summary>
        public string FoundPath { get; init; }

        /// <summary>Which locator stage found it.</summary>
        public TextureFound Found { get; init; }

        /// <summary>The proxy keyword the name / schema suggests, or null.</summary>
        public string SuggestedProxy { get; init; }

        /// <summary>Elements (among those to be extracted) that use the material.</summary>
        public int Uses { get; set; }
    }

    /// <summary>
    /// The result of a texture review pass.
    /// </summary>
    internal sealed class TextureReview
    {
        /// <summary>The materials, host first, then by status (missing first) and name.</summary>
        public List<TextureReviewRow> Rows { get; init; } = new();

        /// <summary>The host model key the overrides are saved under.</summary>
        public string HostKey { get; init; } = string.Empty;

        /// <summary>The model's BimGo folder (its texture choices live there).</summary>
        public string ModelFolder { get; init; } = string.Empty;

        /// <summary>The host model's title.</summary>
        public string ModelTitle { get; init; } = string.Empty;

        /// <summary>The locator's discovery notes (library, Revit.ini, search folders).</summary>
        public IReadOnlyList<string> LocatorNotes { get; init; } = Array.Empty<string>();

        /// <summary>True if the Autodesk Material Library was found on this machine.</summary>
        public bool HasLibrary { get; init; }

        /// <summary>Revit's additional render appearance paths found.</summary>
        public int ExtraPaths { get; init; }

        /// <summary>How long the pass took.</summary>
        public TimeSpan Elapsed { get; init; }
    }

    internal sealed partial class SceneExtractor
    {
        /// <summary>
        /// "Review textures…": finds the materials the next Go / Export would extract (the ticked categories or the
        /// active view, plus the ticked links) and resolves each one's texture exactly as the extraction would,
        /// including remembered search folders and the model's overrides, but without decoding or embedding any image.
        /// Read only. Throws <see cref="OperationCanceledException"/> if cancelled.
        /// </summary>
        /// <param name="uiDoc">The active UIDocument.</param>
        /// <param name="settings">The settings as currently chosen in the Options window (not yet saved).</param>
        /// <param name="progress">Progress and cancellation.</param>
        public static TextureReview Review(UIDocument uiDoc, LaunchSettings settings, OperationProgress progress)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var extractor = new SceneExtractor(uiDoc.Document, settings, progress, resolveOnly: true);
            TextureReview review = extractor.RunReview(uiDoc);
            stopwatch.Stop();
            Log_Utils.Write($"Texture review: {review.Rows.Count} materials, " +
                $"{review.Rows.Count(r => r.Material.TextureState == TextureState.Missing)} missing, in {stopwatch.Elapsed.TotalSeconds:F1}s.");
            return new TextureReview
            {
                Rows = review.Rows,
                HostKey = review.HostKey,
                ModelFolder = review.ModelFolder,
                ModelTitle = review.ModelTitle,
                LocatorNotes = review.LocatorNotes,
                HasLibrary = review.HasLibrary,
                ExtraPaths = review.ExtraPaths,
                Elapsed = stopwatch.Elapsed
            };
        }

        private TextureReview RunReview(UIDocument uiDoc)
        {
            _progress?.Begin("Looking for texture folders", 0.0, 0.05);
            _phases = PhaseResolver.Resolve(_doc, uiDoc.ActiveView, _settings);
            PrepareTextures();

            List<SourceModel> sources = PrepareSources(uiDoc, out DB.View view);
            _progress?.Begin("Finding elements", 0.05, 0.3);
            List<(SourceModel Source, CategoryDef Def, List<Element> Elements)> work = Gather(sources, view);

            // Materials per element (geometry and paint), read once per document
            var uses = new Dictionary<(int Link, long Id), int>();
            var rowSource = new Dictionary<SceneMaterial, SourceModel>(ReferenceEqualityComparer.Instance);
            int total = work.Sum(w => w.Elements.Count), done = 0;
            var ids = new HashSet<long>();
            _progress?.Begin("Reading materials", 0.3, 1.0);
            foreach ((SourceModel source, CategoryDef def, List<Element> elements) in work)
            {
                _src = source;
                foreach (Element element in elements)
                {
                    if ((done++ & 63) == 0)
                    {
                        _progress?.Step(done, total);
                        _progress?.Detail($"{done:N0} of {total:N0} elements · {_materialTable.Count} materials");
                        _progress?.ThrowIfCancelled();
                    }

                    ids.Clear();
                    try
                    {
                        foreach (ElementId id in element.GetMaterialIds(false)) { ids.Add(id.Value); }
                        foreach (ElementId id in element.GetMaterialIds(true)) { ids.Add(id.Value); }
                    }
                    catch
                    {
                        continue; // no readable geometry: no materials for this purpose
                    }

                    foreach (long id in ids)
                    {
                        var key = (source.Link, id);
                        uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
                        if (source.MaterialIndex.ContainsKey(id)) { continue; }
                        if (source.Doc.GetElement(new ElementId(id)) is not Material material)
                        {
                            source.MaterialIndex[id] = MaterialData.NONE;
                            continue;
                        }
                        int before = _materialTable.Count;
                        MaterialIndexOf(material);
                        if (_materialTable.Count > before) { rowSource[_materialTable[^1]] = source; }
                    }
                }
            }
            _src = sources[0];

            var rows = new List<TextureReviewRow>(_materialTable.Count);
            foreach (SceneMaterial material in _materialTable)
            {
                SourceModel source = rowSource.TryGetValue(material, out SourceModel s) ? s : sources[0];
                _src = source;
                _lookups.TryGetValue(material, out TextureLookup lookup);
                rows.Add(new TextureReviewRow
                {
                    Material = material,
                    DocumentKey = DocumentKey(),
                    ModelLabel = source.Link == 0 ? "Host" : source.Info?.Label ?? $"Link {source.Link}",
                    DocumentFolder = DocumentFolder(),
                    FoundPath = lookup.Path,
                    Found = lookup.Path == null ? TextureFound.Missing : lookup.Found,
                    SuggestedProxy = ProxyCatalog.Suggest(material.Name, material.Schema),
                    Uses = uses.TryGetValue((material.Link, material.MaterialId), out int n) ? n : 0
                });
            }
            _src = sources[0];

            rows.Sort((a, b) =>
            {
                int c = a.Material.Link.CompareTo(b.Material.Link);
                if (c != 0) { return c; }
                c = Rank(a).CompareTo(Rank(b));
                return c != 0 ? c : string.Compare(a.Material.Name, b.Material.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            return new TextureReview
            {
                Rows = rows,
                HostKey = LinkResolver.HostKey(_doc),
                ModelFolder = ModelFolderResolver.FolderOf(_doc),
                ModelTitle = _doc.Title ?? string.Empty,
                LocatorNotes = _locator.Notes.ToList(),
                HasLibrary = _locator.HasLibrary,
                ExtraPaths = _locator.ExtraPaths.Count
            };

            static int Rank(TextureReviewRow row) => row.Material.TextureState switch
            {
                TextureState.Missing => 0,
                TextureState.Unreadable => 1,
                TextureState.Procedural => 3,
                TextureState.Embedded => 2,
                _ => 4
            };
        }
    }
}
