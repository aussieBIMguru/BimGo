using System.Numerics;
using BimGo.Edits;
using BimGo.Extraction;

// The class belongs to the Bridge namespace
namespace BimGo.Bridge
{
    /// <summary>
    /// Applies walkthrough edits to one Revit document. Each request runs in its own transaction named
    /// "BimGo: …" (one undo step each) with warnings swallowed and errors rolled back. Revit API thread only;
    /// the session's ExternalEvent handler (<see cref="Live.LiveDispatcher"/>) calls <see cref="Apply"/> for live
    /// edits and <see cref="ApplyJournal"/> (RevitEditor.Push.cs) for a file's journal. Nothing here throws to the
    /// caller.
    ///
    /// Phases: demolition sets Phase Demolished to the session's "new" phase and only accepts elements that exist in
    /// the "existing" phase; copies are created in the new phase.
    /// </summary>
    internal sealed partial class RevitEditor
    {
        #region Fields

        private ElementId _newPhaseId = ElementId.InvalidElementId;
        private ElementId _existingPhaseId = ElementId.InvalidElementId;

        /// <summary>Clones made in this session: the app's clone key to the Revit id.</summary>
        private readonly Dictionary<int, ElementId> _cloneIds = new();

        #endregion

        /// <summary>
        /// Sets the session's phases (ElementId values, -1 for none). Updated with each snapshot.
        /// </summary>
        public void SetPhases(long existingPhaseId, long newPhaseId)
        {
            _existingPhaseId = existingPhaseId > 0 ? new ElementId(existingPhaseId) : ElementId.InvalidElementId;
            _newPhaseId = newPhaseId > 0 ? new ElementId(newPhaseId) : ElementId.InvalidElementId;
        }

        /// <summary>
        /// The session's phases as Revit objects (either may be null).
        /// </summary>
        private PhasePair SessionPhases(Document doc) => new()
        {
            Existing = _existingPhaseId == ElementId.InvalidElementId ? null : doc.GetElement(_existingPhaseId) as Phase,
            New = _newPhaseId == ElementId.InvalidElementId ? null : doc.GetElement(_newPhaseId) as Phase
        };

        /// <summary>
        /// Applies one live edit.
        /// </summary>
        /// <param name="doc">The session's document.</param>
        /// <param name="request">The edit.</param>
        /// <returns>The outcome.</returns>
        public EditResult Apply(Document doc, EditRequest request)
        {
            try
            {
                string refusal = CheckDocument(doc);
                if (refusal != null) { return Fail(request, refusal); }

                // A new instance from the family library: no target element
                if (request.Op == EditOp.Place)
                {
                    EditResult placed = Place(doc, request, SessionPhases(doc), out ElementId placedId);
                    if (placed.Success && request.NewCloneKey != 0) { _cloneIds[request.NewCloneKey] = placedId; }
                    return placed;
                }

                // Resolve the target (clones made earlier this session are found by key)
                ElementId id;
                if (request.TargetCloneKey != 0)
                {
                    if (!_cloneIds.TryGetValue(request.TargetCloneKey, out id)) { return Fail(request, "The clone was never created in Revit"); }
                }
                else
                {
                    id = new ElementId(request.ElementId);
                }

                Element element = doc.GetElement(id);
                if (element == null || !element.IsValidObject) { return Fail(request, "The element no longer exists in Revit"); }

                refusal = CheckEditable(doc, element);
                if (refusal != null) { return Fail(request, refusal); }

                EditResult result = Execute(doc, element, request, SessionPhases(doc), out ElementId newId);
                if (result.Success && request.Op == EditOp.Copy) { _cloneIds[request.NewCloneKey] = newId; }
                return result;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Edit {request.Op} failed: {ex}");
                return Fail(request, ex.Message);
            }
        }

        #region Processing

        /// <summary>
        /// Null if edits can be made to the document, else a short reason.
        /// </summary>
        private static string CheckDocument(Document doc)
        {
            if (doc == null || !doc.IsValidObject) { return "The model is no longer open in Revit"; }
            if (doc.IsReadOnly) { return "The model is read-only"; }
            if (doc.IsModifiable) { return "Revit is busy with another edit"; }
            return null;
        }

        /// <summary>
        /// Null if the element may be edited, else a short reason (borrowed by another user).
        /// </summary>
        private static string CheckEditable(Document doc, Element element)
        {
            if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, element.Id) == CheckoutStatus.OwnedByOtherUser)
            {
                return "The element is borrowed by another user";
            }
            return null;
        }

        /// <summary>
        /// Runs one request on a resolved element, in its own transaction.
        /// </summary>
        /// <param name="newId">For a successful copy: the new element.</param>
        private EditResult Execute(Document doc, Element element, EditRequest request, PhasePair phases, out ElementId newId)
        {
            newId = ElementId.InvalidElementId;
            switch (request.Op)
            {
                case EditOp.PhaseDemolish: return PhaseDemolish(doc, element, request, phases);
                case EditOp.Delete: return Delete(doc, element, request);
                case EditOp.Transform: return TransformElement(doc, element, request);
                case EditOp.Copy: return Copy(doc, element, request, phases, out newId);
                default: return Fail(request, "Unknown request");
            }
        }

        /// <summary>
        /// Sets Phase Demolished to the new phase (existing elements only). Reports the element and any dependants
        /// Revit demolished with it.
        /// </summary>
        private EditResult PhaseDemolish(Document doc, Element element, EditRequest request, PhasePair phases)
        {
            string blocked = PhaseResolver.DemolishBlockReason(element, phases, out _);
            if (blocked != null) { return Fail(request, blocked); }

            Parameter parameter = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (parameter == null || parameter.IsReadOnly) { return Fail(request, "This element can't be demolished by phase"); }

            ElementId phaseId = phases.NewId;
            string error = RunTransaction(doc, request.Label, () => parameter.Set(phaseId));
            if (error != null) { return Fail(request, error); }

            // The element plus anything that went with it (e.g. doors in a demolished wall)
            var affected = new List<long> { element.Id.Value };
            try
            {
                foreach (ElementId dependentId in element.GetDependentElements(null))
                {
                    if (dependentId == element.Id) { continue; }
                    if (doc.GetElement(dependentId) is Element dependent && dependent.DemolishedPhaseId == phaseId)
                    {
                        affected.Add(dependentId.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Dependent lookup failed: {ex.Message}");
            }

            return new EditResult { Ticket = request.Ticket, Op = request.Op, Success = true, AffectedIds = affected.ToArray() };
        }

        /// <summary>
        /// Deletes the element. Reports everything Revit deleted with it.
        /// </summary>
        private EditResult Delete(Document doc, Element element, EditRequest request)
        {
            ICollection<ElementId> deleted = null;
            ElementId id = element.Id;
            string error = RunTransaction(doc, request.Label, () => deleted = doc.Delete(id));
            if (error != null) { return Fail(request, error); }

            long[] affected = deleted?.Select(d => d.Value).ToArray() ?? new[] { id.Value };
            return new EditResult { Ticket = request.Ticket, Op = request.Op, Success = true, AffectedIds = affected };
        }

        /// <summary>
        /// Rotates about the element's location point, then moves it.
        /// </summary>
        private EditResult TransformElement(Document doc, Element element, EditRequest request)
        {
            ElementId id = element.Id;
            XYZ pivot = PivotOf(element, request.Pivot);
            string error = RunTransaction(doc, request.Label, () => ApplyTransform(doc, id, pivot, request));
            if (error != null) { return Fail(request, error); }
            return new EditResult { Ticket = request.Ticket, Op = request.Op, Success = true };
        }

        /// <summary>
        /// Copies the element by the translation, rotates the copy about its own location point and puts it in the
        /// new phase (copies are new work).
        /// </summary>
        private EditResult Copy(Document doc, Element element, EditRequest request, PhasePair phases, out ElementId createdId)
        {
            ElementId sourceId = element.Id;
            ElementId newId = ElementId.InvalidElementId;
            XYZ sourcePivot = PivotOf(element, request.Pivot);
            ElementId phaseId = phases?.NewId ?? ElementId.InvalidElementId;

            string error = RunTransaction(doc, request.Label, () =>
            {
                ICollection<ElementId> copies = ElementTransformUtils.CopyElement(doc, sourceId, ToFeet(request.Translation));
                newId = copies.FirstOrDefault(c => doc.GetElement(c) is FamilyInstance) ?? copies.FirstOrDefault() ?? ElementId.InvalidElementId;
                if (newId == ElementId.InvalidElementId) { throw new InvalidOperationException("Revit did not create a copy"); }

                if (MathF.Abs(request.Angle) > 1e-6f)
                {
                    XYZ pivot = sourcePivot + ToFeet(request.Translation);
                    ElementTransformUtils.RotateElement(doc, newId, DB.Line.CreateBound(pivot, pivot + XYZ.BasisZ), request.Angle);
                }

                if (phaseId != ElementId.InvalidElementId) { SetNewWork(doc.GetElement(newId), phaseId); }
            });

            createdId = newId;
            if (error != null) { return Fail(request, error); }

            return new EditResult
            {
                Ticket = request.Ticket,
                Op = request.Op,
                Success = true,
                NewElementId = newId.Value,
                CloneKey = request.NewCloneKey
            };
        }

        /// <summary>
        /// Places a new instance of a family type (the family library): on the level at or below the point, then
        /// moved so its location point is exactly where the walkthrough put it (however Revit read the point's
        /// height), turned about the vertical through it, and put in the new phase. Level-based and work-plane / face-based
        /// types (on the level's plane: <see cref="FamilyPlacer"/>); wall-hosted types are refused.
        /// </summary>
        private EditResult Place(Document doc, EditRequest request, PhasePair phases, out ElementId createdId)
        {
            createdId = ElementId.InvalidElementId;
            FamilySymbol symbol = FindSymbol(doc, request.TypeUniqueId, request.TypeId);
            if (symbol == null) { return Fail(request, "The family type is no longer loaded in the model"); }

            if (!FamilyPlacer.CanPlace(FamilyPlacer.PlacementOf(symbol)))
            {
                return Fail(request, "Only level-based and work-plane-based families can be placed from BimGo");
            }

            XYZ point = ToFeet(request.Pivot);
            Level level = LevelAtOrBelow(doc, point.Z);
            if (level == null) { return Fail(request, "The model has no levels"); }

            ElementId newId = ElementId.InvalidElementId;
            ElementId phaseId = phases?.NewId ?? ElementId.InvalidElementId;
            string error = RunTransaction(doc, request.Label, () =>
            {
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    doc.Regenerate();
                }
                FamilyInstance instance = FamilyPlacer.Place(doc, symbol, level, point);
                newId = instance.Id;
                doc.Regenerate();

                if (instance.Location is LocationPoint location)
                {
                    XYZ correction = point - location.Point;
                    if (correction.GetLength() > 1e-6) { ElementTransformUtils.MoveElement(doc, newId, correction); }
                }
                if (MathF.Abs(request.Angle) > 1e-6f)
                {
                    ElementTransformUtils.RotateElement(doc, newId, DB.Line.CreateBound(point, point + XYZ.BasisZ), request.Angle);
                }
                if (phaseId != ElementId.InvalidElementId) { SetNewWork(doc.GetElement(newId), phaseId); }
            });

            createdId = newId;
            if (error != null) { return Fail(request, error); }
            return new EditResult
            {
                Ticket = request.Ticket,
                Op = request.Op,
                Success = true,
                NewElementId = newId.Value,
                CloneKey = request.NewCloneKey
            };
        }

        /// <summary>
        /// A family type by UniqueId, else by ElementId (null when neither finds a family type).
        /// </summary>
        private static FamilySymbol FindSymbol(Document doc, string uniqueId, long id)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(uniqueId) && doc.GetElement(uniqueId) is FamilySymbol byUniqueId) { return byUniqueId; }
                if (id > 0 && doc.GetElement(new ElementId(id)) is FamilySymbol byId) { return byId; }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Family type lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// The highest level at or just below a height (feet, internal), else the lowest level.
        /// </summary>
        private static Level LevelAtOrBelow(Document doc, double z)
        {
            List<Level> levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            if (levels.Count == 0) { return null; }
            Level best = levels[0];
            foreach (Level level in levels)
            {
                if (level.ProjectElevation <= z + 0.01) { best = level; }
            }
            return best;
        }

        /// <summary>
        /// Puts a copy in the new phase and clears any demolition it inherited (inside the copy's transaction).
        /// A failure leaves the copy in its source's phase (logged, not fatal).
        /// </summary>
        private static void SetNewWork(Element copy, ElementId phaseId)
        {
            if (copy == null) { return; }
            try
            {
                Parameter demolished = copy.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                if (demolished != null && !demolished.IsReadOnly && demolished.AsElementId() != ElementId.InvalidElementId)
                {
                    demolished.Set(ElementId.InvalidElementId);
                }

                Parameter created = copy.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (created != null && !created.IsReadOnly && created.AsElementId() != phaseId) { created.Set(phaseId); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Copy {copy.Id.Value} kept its source's phase: {ex.Message}");
            }
        }

        /// <summary>
        /// Rotation (about a vertical axis through the pivot) then translation.
        /// </summary>
        private static void ApplyTransform(Document doc, ElementId id, XYZ pivot, EditRequest request)
        {
            if (MathF.Abs(request.Angle) > 1e-6f)
            {
                ElementTransformUtils.RotateElement(doc, id, DB.Line.CreateBound(pivot, pivot + XYZ.BasisZ), request.Angle);
            }
            if (request.Translation.LengthSquared() > 1e-10f)
            {
                ElementTransformUtils.MoveElement(doc, id, ToFeet(request.Translation));
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Runs an action in a named transaction. Warnings are deleted; errors roll back.
        /// </summary>
        /// <returns>Null on success, else a short reason.</returns>
        private static string RunTransaction(Document doc, string label, Action action)
        {
            var failures = new SwallowFailures();
            using var transaction = new Transaction(doc, "BimGo: " + (string.IsNullOrWhiteSpace(label) ? "Edit" : label));

            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(failures);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);

            try
            {
                transaction.Start();
                action();
            }
            catch (Exception ex)
            {
                if (transaction.HasStarted() && !transaction.HasEnded()) { transaction.RollBack(); }
                return ex.Message;
            }

            TransactionStatus status = transaction.Commit();
            if (status == TransactionStatus.Committed) { return null; }
            return failures.FirstError ?? "Revit rolled the change back";
        }

        /// <summary>
        /// The element's current location point (feet), falling back to the game's pivot.
        /// </summary>
        private static XYZ PivotOf(Element element, Vector3 fallbackMetres)
        {
            if (element.Location is LocationPoint point) { return point.Point; }
            return ToFeet(fallbackMetres);
        }

        /// <summary>
        /// Metres (Revit internal axes) to an XYZ in feet.
        /// </summary>
        private static XYZ ToFeet(Vector3 metres) => new(metres.X / SceneExtractor.FT, metres.Y / SceneExtractor.FT, metres.Z / SceneExtractor.FT);

        /// <summary>
        /// A failed result.
        /// </summary>
        private static EditResult Fail(EditRequest request, string message)
        {
            Utilities.Log_Utils.Write($"Edit {request.Op} ({request.ElementId}/{request.TargetCloneKey}) refused: {message}");
            return new EditResult { Ticket = request.Ticket, Op = request.Op, Success = false, Message = message, CloneKey = request.NewCloneKey };
        }

        #endregion

        /// <summary>
        /// Deletes warnings so no dialog appears; any error rolls the transaction back (first message kept).
        /// Also used by the family library's temporary (rolled-back) transaction.
        /// </summary>
        internal sealed class SwallowFailures : IFailuresPreprocessor
        {
            /// <summary>The first error's description, if any.</summary>
            public string FirstError { get; private set; }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool hasError = false;
                foreach (FailureMessageAccessor message in accessor.GetFailureMessages())
                {
                    if (message.GetSeverity() == FailureSeverity.Warning)
                    {
                        accessor.DeleteWarning(message);
                    }
                    else
                    {
                        hasError = true;
                        FirstError ??= message.GetDescriptionText();
                    }
                }
                return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
    }
}
