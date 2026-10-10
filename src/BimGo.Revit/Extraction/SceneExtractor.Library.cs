using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using SceneLight = BimGo.Scene.LightSource;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// The family library (next round): the loadable family types the walkthrough's Place gun can place.
    /// <list type="number">
    /// <item><b>Types:</b> every <see cref="FamilySymbol"/> loaded in the host model whose category is a ticked FFE or
    /// services category (so a placed instance is drawn), in-place families excluded, capped at
    /// <see cref="LaunchSettings.FamilyLibraryMax"/> (types already used in the model first).</item>
    /// <item><b>Previews:</b> Revit's own type preview (<see cref="ElementType.GetPreviewImage"/>), as a small PNG.</item>
    /// <item><b>Geometry:</b> for level-based and work-plane / face-based types (on the level's plane, see
    /// <see cref="FamilyPlacer"/>): one temporary instance of each on the lowest
    /// level, in a single transaction that is always <b>rolled back</b> (nothing stays in the model, nothing enters
    /// the undo list). Each is extracted like any element (materials, glow, light), then becomes a hidden template
    /// at the tail of the snapshot, moved <see cref="TEMPLATE_DROP"/> below the model so an older BimGo that draws it
    /// anyway draws it out of sight.</item>
    /// </list>
    /// Live sessions only (Go / refresh): Export .bimgo never calls it. Any failure leaves the library smaller or
    /// empty; the walkthrough is never affected.
    /// </summary>
    internal sealed partial class SceneExtractor
    {
        #region Constants

        /// <summary>Templates are moved this far (m) below where Revit placed them.</summary>
        private const float TEMPLATE_DROP = -2000f;

        /// <summary>Preview size (px), as Revit renders it.</summary>
        private const int PREVIEW_SIZE = 128;

        #endregion

        /// <summary>True when this extraction should build the family library (Go / refresh with the option on).</summary>
        private bool _includeLibrary;

        /// <summary>While extracting templates: read whole geometry (never through the active view).</summary>
        private bool _libraryPass;

        /// <summary>
        /// Builds the library: types, previews, then the template geometry (appended to the output buffers after every
        /// model element). Call after the model's elements (and the scene bounds) are done, with <see cref="_src"/> on
        /// the host.
        /// </summary>
        /// <param name="loaded">Per category definition: extracted in this snapshot (only those are offered).</param>
        /// <returns>The library (empty when nothing qualifies).</returns>
        private LibraryData ExtractLibrary(bool[] loaded)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _progress?.Begin("Preparing the family library", 0.78, 0.85);
            _progress?.ThrowIfCancelled();

            // 1. The offered types
            Dictionary<BuiltInCategory, CategoryDef> categories = LibraryCategories(loaded);
            if (categories.Count == 0) { return LibraryData.Empty; }

            var inUse = new Dictionary<long, int>();
            foreach (FamilyInstance instance in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                long typeId = instance.GetTypeId().Value;
                inUse[typeId] = inUse.TryGetValue(typeId, out int n) ? n + 1 : 1;
            }

            var candidates = new List<(FamilySymbol Symbol, LibraryEntry Entry)>();
            foreach (FamilySymbol symbol in new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                try
                {
                    Category category = symbol.Category;
                    if (category == null || !categories.TryGetValue((BuiltInCategory)category.Id.Value, out CategoryDef def)) { continue; }
                    Family family = symbol.Family;
                    if (family == null || family.IsInPlace) { continue; }

                    var entry = new LibraryEntry
                    {
                        TypeId = symbol.Id.Value,
                        TypeUniqueId = symbol.UniqueId ?? string.Empty,
                        Family = family.Name ?? string.Empty,
                        Type = symbol.Name ?? string.Empty,
                        Category = def.Key,
                        CategoryIndex = def.Index,
                        Placed = inUse.TryGetValue(symbol.Id.Value, out int placed) ? placed : 0
                    };
                    ClassifyPlacement(family, entry);
                    candidates.Add((symbol, entry));
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Family library: type {symbol.Id.Value} skipped: {ex.Message}");
                }
            }
            if (candidates.Count == 0)
            {
                Utilities.Log_Utils.Write("Family library: no loadable family types in the ticked FFE / services categories.");
                return LibraryData.Empty;
            }

            // The cap keeps types already used in the model first, then placeable ones, then by name
            int max = Math.Clamp(_settings.FamilyLibraryMax, 10, LaunchSettings.MAX_FAMILY_LIBRARY);
            List<(FamilySymbol Symbol, LibraryEntry Entry)> kept = candidates
                .OrderByDescending(c => c.Entry.Placed > 0)
                .ThenByDescending(c => c.Entry.Placeable)
                .ThenBy(c => c.Entry.Family, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Entry.Type, StringComparer.CurrentCultureIgnoreCase)
                .Take(max)
                .OrderBy(c => c.Entry.CategoryIndex)
                .ThenBy(c => c.Entry.Family, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Entry.Type, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (kept.Count < candidates.Count)
            {
                Utilities.Log_Utils.Write($"Family library: {candidates.Count} types qualify; the first {kept.Count} are offered (Options → Family library cap).");
            }

            // 2. Previews
            var previews = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int i = 0; i < kept.Count; i++)
            {
                if ((i & 15) == 0)
                {
                    _progress?.Step(i, kept.Count * 2);
                    _progress?.Detail($"Previews: {i:N0} of {kept.Count:N0} family types");
                    _progress?.ThrowIfCancelled();
                }
                byte[] png = PreviewPng(kept[i].Symbol);
                if (png == null) { continue; }
                string name = $"{BimGoFormat.LIBRARY_FOLDER}{i}.png";
                previews[name] = png;
                kept[i].Entry.Preview = name;
            }

            // 3. Template geometry (rolled back)
            int vertexStart = _vertices.Count;
            int lightsBefore = _lights.Count;
            ExtractTemplates(kept, categories);
            ShiftTemplates(vertexStart, lightsBefore);

            LibraryEntry[] entries = kept.Select(k => k.Entry).ToArray();
            int templates = entries.Count(e => e.Element >= 0);
            Utilities.Log_Utils.Write($"Family library: {entries.Length} types ({templates} with geometry, {entries.Count(e => !e.Placeable)} listed only), " +
                $"{previews.Count} previews, {_vertices.Count - vertexStart:N0} template vertices in {clock.Elapsed.TotalSeconds:F1}s.");
            return new LibraryData
            {
                Entries = entries,
                Previews = previews,
                VertexStart = templates > 0 ? vertexStart : _vertices.Count
            };
        }

        /// <summary>
        /// The ticked (extracted) FFE and services categories by Revit category: placing anything else would make an
        /// instance the walkthrough doesn't draw.
        /// </summary>
        private static Dictionary<BuiltInCategory, CategoryDef> LibraryCategories(bool[] loaded)
        {
            var map = new Dictionary<BuiltInCategory, CategoryDef>();
            foreach (CategoryDef def in CategoryCatalog.All)
            {
                if (def.Group == CategoryGroup.System || def.Heavy || def.Key == CategoryCatalog.KEY_OTHER) { continue; }
                if (def.Index >= loaded.Length || !loaded[def.Index]) { continue; }
                foreach (BuiltInCategory bic in CategoryResolver.Resolve(def)) { map.TryAdd(bic, def); }
            }
            return map;
        }

        /// <summary>
        /// Placeable (level-based, or work-plane / face-based on the level's plane) or listed with the reason.
        /// </summary>
        private static void ClassifyPlacement(Family family, LibraryEntry entry)
        {
            FamilyPlacementType placement;
            try { placement = family.FamilyPlacementType; }
            catch { placement = FamilyPlacementType.Invalid; }

            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                    entry.Placement = LibraryPlacement.LEVEL_BASED;
                    entry.Placeable = true;
                    break;
                case FamilyPlacementType.OneLevelBasedHosted:
                    entry.Placement = LibraryPlacement.HOSTED;
                    entry.Reason = "Needs a host (wall, floor, ceiling or roof): not placeable from BimGo yet";
                    break;
                case FamilyPlacementType.WorkPlaneBased:
                    // Work-plane / face-based: placed on the level's plane (FamilyPlacer)
                    entry.Placement = LibraryPlacement.WORK_PLANE;
                    entry.Placeable = true;
                    break;
                default:
                    entry.Placement = LibraryPlacement.OTHER;
                    entry.Reason = $"Placed by {placement}: not placeable from BimGo";
                    break;
            }
        }

        /// <summary>
        /// Revit's preview of a type as PNG bytes, or null when it has none.
        /// </summary>
        private static byte[] PreviewPng(FamilySymbol symbol)
        {
            try
            {
                using System.Drawing.Bitmap bitmap = symbol.GetPreviewImage(new System.Drawing.Size(PREVIEW_SIZE, PREVIEW_SIZE));
                if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) { return null; }
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Family library: no preview for {symbol.Id.Value}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Places one temporary instance of every placeable type on the lowest level at the scene origin, extracts each
        /// as a template, then rolls the whole transaction back. Types that fail are listed but not placeable.
        /// </summary>
        private void ExtractTemplates(List<(FamilySymbol Symbol, LibraryEntry Entry)> kept, Dictionary<BuiltInCategory, CategoryDef> categories)
        {
            List<(FamilySymbol Symbol, LibraryEntry Entry)> placeable = kept.Where(k => k.Entry.Placeable).ToList();
            if (placeable.Count == 0) { return; }

            Level level = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).FirstOrDefault();
            if (level == null)
            {
                MarkUnplaced(placeable, "The model has no levels");
                return;
            }
            if (_doc.IsReadOnly || _doc.IsModifiable)
            {
                MarkUnplaced(placeable, _doc.IsReadOnly ? "The model can't be changed right now (read-only), so no geometry: F5 retries" : "Revit was busy with another edit");
                return;
            }

            var at = new XYZ(_origin.X / FT, _origin.Y / FT, level.ProjectElevation);
            var failures = new Bridge.RevitEditor.SwallowFailures();
            using var transaction = new Transaction(_doc, "BimGo: family library (temporary, rolled back)");
            try
            {
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(failures);
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);
                if (transaction.Start() != TransactionStatus.Started)
                {
                    MarkUnplaced(placeable, "Revit could not start a temporary transaction");
                    return;
                }

                // Activate, place, then one regeneration for all of them
                foreach ((FamilySymbol Symbol, LibraryEntry Entry) item in placeable)
                {
                    try { if (!item.Symbol.IsActive) { item.Symbol.Activate(); } }
                    catch { /* reported when placing */ }
                }
                var instances = new List<(FamilyInstance Instance, LibraryEntry Entry)>();
                foreach ((FamilySymbol symbol, LibraryEntry entry) in placeable)
                {
                    try
                    {
                        FamilyInstance instance = FamilyPlacer.Place(_doc, symbol, level, at);
                        instances.Add((instance, entry));
                    }
                    catch (Exception ex)
                    {
                        Unplaced(entry, $"Revit could not place it ({ex.Message})");
                    }
                }
                _doc.Regenerate();

                // Extract each like any element (whole geometry, not through the active view), then make it a template
                _libraryPass = true;
                for (int i = 0; i < instances.Count; i++)
                {
                    (FamilyInstance instance, LibraryEntry entry) = instances[i];
                    if ((i & 7) == 0)
                    {
                        _progress?.Step(instances.Count + i, instances.Count * 2);
                        _progress?.Detail($"Geometry: {i:N0} of {instances.Count:N0} family types");
                        _progress?.ThrowIfCancelled();
                    }
                    try
                    {
                        if (!instance.IsValidObject) { Unplaced(entry, "Revit removed the temporary instance"); continue; }
                        CategoryDef def = CategoryCatalog.All[entry.CategoryIndex];
                        int before = _elements.Count;
                        if (!ExtractElement(instance, def) || _elements.Count != before + 1)
                        {
                            Unplaced(entry, "It has no 3D geometry");
                            continue;
                        }
                        _elements[before] = AsTemplate(_elements[before], entry);
                        entry.Element = before;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Unplaced(entry, $"Its geometry could not be read ({ex.Message})");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Family library: temporary placement failed: {ex}");
                MarkUnplaced(placeable.Where(p => p.Entry.Element < 0).ToList(), "Revit could not place a temporary instance");
            }
            finally
            {
                _libraryPass = false;

                // Always: nothing stays in the model
                if (transaction.HasStarted() && !transaction.HasEnded()) { transaction.RollBack(); }
            }
        }

        /// <summary>
        /// A template copy of an extracted record: no Revit identity (the temporary instance is gone), always movable,
        /// new work, flagged so every pass leaves it hidden, and moved <see cref="TEMPLATE_DROP"/> down with its geometry.
        /// </summary>
        private static ElementRecord AsTemplate(ElementRecord record, LibraryEntry entry) => new()
        {
            ElementId = 0,
            UniqueId = string.Empty,
            HostId = 0,
            Name = string.IsNullOrWhiteSpace(entry.Type) ? record.Name : entry.Type,
            CategoryName = record.CategoryName,
            FamilyType = entry.Label,
            LevelName = "—",
            CategoryIndex = record.CategoryIndex,
            OpaqueStart = record.OpaqueStart,
            OpaqueCount = record.OpaqueCount,
            TransparentStart = record.TransparentStart,
            TransparentCount = record.TransparentCount,
            Bounds = new Aabb(record.Bounds.Min + new Vector3(0f, 0f, TEMPLATE_DROP), record.Bounds.Max + new Vector3(0f, 0f, TEMPLATE_DROP)),
            IsProxy = record.IsProxy,
            Movable = true,
            MoveBlockReason = null,
            Pivot = record.Pivot + new Vector3(0f, 0f, TEMPLATE_DROP),
            Phase = PhaseRole.New,
            Link = 0,
            IsLibraryTemplate = true
        };

        /// <summary>
        /// Moves the template vertices and lights <see cref="TEMPLATE_DROP"/> down (out of sight of anything that would
        /// draw them, and out of the model's bounds); <see cref="AsTemplate"/> already moved the records.
        /// </summary>
        private void ShiftTemplates(int vertexStart, int lightsBefore)
        {
            var drop = new Vector3(0f, 0f, TEMPLATE_DROP);
            for (int v = vertexStart; v < _vertices.Count; v++)
            {
                SceneVertex vertex = _vertices[v];
                vertex.Position += drop;
                _vertices[v] = vertex;
            }
            for (int l = lightsBefore; l < _lights.Count; l++)
            {
                SceneLight light = _lights[l];
                _lights[l] = new SceneLight
                {
                    Element = light.Element,
                    Position = light.Position + drop,
                    Lumens = light.Lumens,
                    Kelvin = light.Kelvin,
                    Downward = light.Downward,
                    Estimated = light.Estimated
                };
            }
        }

        private static void MarkUnplaced(List<(FamilySymbol Symbol, LibraryEntry Entry)> items, string reason)
        {
            foreach ((FamilySymbol Symbol, LibraryEntry Entry) item in items) { Unplaced(item.Entry, reason); }
            Utilities.Log_Utils.Write($"Family library: {items.Count} types listed without geometry: {reason}.");
        }

        private static void Unplaced(LibraryEntry entry, string reason)
        {
            entry.Placeable = false;
            entry.Element = -1;
            entry.Reason = reason;
        }
    }
}
