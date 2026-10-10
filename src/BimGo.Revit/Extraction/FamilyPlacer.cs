using Autodesk.Revit.DB.Structure;

// The class belongs to the Extraction namespace
namespace BimGo.Extraction
{
    /// <summary>
    /// Places a new instance of a family type the way its family needs (family library): used by the library's
    /// rolled-back template pass and by live / pushed placements (<see cref="Bridge.RevitEditor"/>), so templates and
    /// real instances are made the same way and match.
    /// <list type="bullet">
    /// <item><b>Level-based</b> (<see cref="FamilyPlacementType.OneLevelBased"/>): on the level.</item>
    /// <item><b>Work-plane / face-based</b> (<see cref="FamilyPlacementType.WorkPlaneBased"/>): hosted on the level's
    /// plane (its plane reference, else a sketch plane of the level), oriented along project X like a level-based
    /// one; failing that, the level overload.</item>
    /// </list>
    /// The caller moves the instance onto the exact point afterwards (Revit may read the point's height relative to
    /// the level, or ignore it) and rotates it. Call inside an open transaction; throws on failure.
    /// </summary>
    internal static class FamilyPlacer
    {
        /// <summary>True when BimGo can place instances of this placement type.</summary>
        public static bool CanPlace(FamilyPlacementType placement) =>
            placement == FamilyPlacementType.OneLevelBased || placement == FamilyPlacementType.WorkPlaneBased;

        /// <summary>The family's placement type (Invalid when it can't be read).</summary>
        public static FamilyPlacementType PlacementOf(FamilySymbol symbol)
        {
            try { return symbol?.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid; }
            catch { return FamilyPlacementType.Invalid; }
        }

        /// <summary>
        /// Creates the instance at a point (internal feet) associated with a level. The symbol must be active.
        /// </summary>
        /// <exception cref="InvalidOperationException">When Revit places nothing (with Revit's own message when it threw).</exception>
        public static FamilyInstance Place(Document doc, FamilySymbol symbol, Level level, XYZ point)
        {
            FamilyPlacementType placement = PlacementOf(symbol);
            if (placement == FamilyPlacementType.WorkPlaneBased)
            {
                var onPlane = new XYZ(point.X, point.Y, level.ProjectElevation);
                string firstError = null;

                // The level's own plane (keeps the instance associated with the level: Revit shows "Host: Level 1")
                try
                {
                    Reference plane = level.GetPlaneReference();
                    if (plane != null)
                    {
                        FamilyInstance hosted = doc.Create.NewFamilyInstance(plane, onPlane, XYZ.BasisX, symbol);
                        if (hosted != null) { return hosted; }
                    }
                }
                catch (Exception ex) { firstError = ex.Message; }

                // A sketch plane of the level (some versions / families refuse the datum reference)
                try
                {
                    SketchPlane sketch = SketchPlane.Create(doc, level.Id);
                    Reference plane = sketch?.GetPlaneReference();
                    if (plane != null)
                    {
                        FamilyInstance hosted = doc.Create.NewFamilyInstance(plane, onPlane, XYZ.BasisX, symbol);
                        if (hosted != null) { return hosted; }
                    }
                }
                catch (Exception ex) { firstError ??= ex.Message; }

                // Last resort: the level overload (works for some work-plane families)
                try
                {
                    FamilyInstance onLevel = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
                    if (onLevel != null) { return onLevel; }
                }
                catch (Exception ex) { firstError ??= ex.Message; }

                throw new InvalidOperationException(firstError ?? "Revit did not place the family on the level's work plane");
            }

            return doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                ?? throw new InvalidOperationException("Revit did not place the family");
        }
    }
}
