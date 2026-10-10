using System.Numerics;
using BimGo.Edits;
using BimGo.Extraction;
using BimGo.Live;

// The class belongs to the Bridge namespace
namespace BimGo.Bridge
{
    /// <summary>
    /// Pushing a standalone file's journal into the model (<c>journal.apply</c>).
    ///
    /// The whole push runs in one <see cref="TransactionGroup"/> ("BimGo: Push N edits from file.bimgo") that is
    /// assimilated, so one Ctrl+Z in Revit undoes it all; each entry still gets its own transaction so one failure
    /// doesn't sink the rest. A dry run does exactly the same work, then rolls the group back: what it reports is
    /// what the real push would do, including clones of clones.
    ///
    /// Targets are found by UniqueId (stable across saves, upgrades and local copies); clones made by the push are
    /// tracked by their clone key. Moves and clones check that the element is still where it was when the file was
    /// made (a conflict otherwise).
    /// </summary>
    internal sealed partial class RevitEditor
    {
        /// <summary>
        /// Applies (or previews) a file's journal entries.
        /// </summary>
        /// <param name="doc">The session's document.</param>
        /// <param name="request">The request (entries in journal order).</param>
        /// <param name="sessionModelKey">The document's model key.</param>
        /// <returns>The per-entry outcome.</returns>
        public JournalResultPayload ApplyJournal(Document doc, JournalApplyPayload request, string sessionModelKey)
        {
            var answer = new JournalResultPayload { RequestId = request?.RequestId ?? string.Empty, DryRun = request?.DryRun ?? true };
            try
            {
                if (request == null || request.Entries == null || request.Entries.Count == 0) { return Refuse(answer, "Nothing to push"); }

                string refusal = CheckDocument(doc);
                if (refusal == null && !string.Equals(request.ModelKey ?? string.Empty, sessionModelKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    refusal = "This file was made from a different model";
                }
                if (refusal != null) { return Refuse(answer, refusal); }

                PhasePair phases = PushPhases(doc, request, out string phaseNote);
                answer.PhaseName = phases.New?.Name;
                answer.ExistingPhaseName = phases.Existing?.Name;
                answer.Message = phaseNote;

                List<JournalEntry> entries = request.Entries.Where(e => e != null).OrderBy(e => e.Seq).ToList();
                string label = $"BimGo: Push {entries.Count} edit{(entries.Count == 1 ? string.Empty : "s")}"
                    + (string.IsNullOrWhiteSpace(request.FileName) ? string.Empty : $" from {Path.GetFileName(request.FileName)}");

                // Clones: those already in the model, and which entry should create each new one
                var clones = new Dictionary<int, ElementId>();
                foreach (CloneRef known in request.KnownClones ?? new List<CloneRef>())
                {
                    if (known != null && known.CloneKey != 0 && known.ElementId > 0) { clones[known.CloneKey] = new ElementId(known.ElementId); }
                }
                var createdBy = new Dictionary<int, int>();
                foreach (JournalEntry entry in entries)
                {
                    if (JournalOps.Creates(entry.Op) && entry.NewCloneKey != 0) { createdBy[entry.NewCloneKey] = entry.Seq; }
                }

                double tolerance = Math.Clamp(request.ToleranceMm, 0.1, 1000.0);
                using var group = new TransactionGroup(doc, label);
                if (group.Start() != TransactionStatus.Started) { return Refuse(answer, "Revit could not start the push"); }

                try
                {
                    foreach (JournalEntry entry in entries)
                    {
                        answer.Results.Add(ApplyEntry(doc, entry, phases, clones, createdBy, tolerance, request.ApplyConflicts));
                    }
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Push failed part-way: {ex}");
                    if (group.HasStarted() && !group.HasEnded()) { group.RollBack(); }
                    answer.Results.Clear();
                    return Refuse(answer, $"The push stopped and was rolled back: {ex.Message}");
                }

                answer.Count();
                if (request.DryRun || answer.Applied == 0)
                {
                    group.RollBack();
                    if (request.DryRun)
                    {
                        // Ids made by a rolled-back preview mean nothing
                        foreach (JournalEntryResult result in answer.Results) { result.NewElementId = 0; }
                    }
                }
                else if (group.Assimilate() == TransactionStatus.Committed)
                {
                    answer.UndoLabel = label;
                }
                else
                {
                    if (group.HasStarted() && !group.HasEnded()) { group.RollBack(); }
                    answer.Results.Clear();
                    return Refuse(answer, "Revit could not commit the push; nothing was changed");
                }

                Utilities.Log_Utils.Write($"{(request.DryRun ? "Push preview" : "Push")} of {entries.Count}: {answer.Applied} applied, {answer.Conflicts} conflicts, " +
                    $"{answer.Skipped} skipped, {answer.Failed} failed, {answer.AlreadyApplied} already in the model.");
                return answer;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Push failed: {ex}");
                answer.Results.Clear();
                return Refuse(answer, ex.Message);
            }
        }

        #region Entries

        /// <summary>
        /// Applies one entry inside the push's transaction group.
        /// </summary>
        private JournalEntryResult ApplyEntry(Document doc, JournalEntry entry, PhasePair phases, Dictionary<int, ElementId> clones,
            Dictionary<int, int> createdBy, double toleranceMm, bool applyConflicts)
        {
            var result = new JournalEntryResult { Seq = entry.Seq };
            if (entry.AppliedToRevit) { return Status(result, JournalStatus.ALREADY_APPLIED, "Already in Revit"); }

            // A placement from the family library has no target: it creates the type's new instance
            if (entry.Op == JournalOps.PLACE) { return ApplyPlace(doc, entry, phases, clones, result); }

            bool isHide = entry.Op == JournalOps.HIDE;
            bool isDelete = isHide && entry.Mode == JournalOps.MODE_DELETE;

            // 1. Resolve the target
            Element element;
            if (entry.TargetCloneKey != 0)
            {
                if (!clones.TryGetValue(entry.TargetCloneKey, out ElementId cloneId))
                {
                    string why = createdBy.TryGetValue(entry.TargetCloneKey, out int by)
                        ? $"Its clone (edit #{by}) was not applied"
                        : "Its clone is not in the model";
                    return Status(result, JournalStatus.SKIPPED, why);
                }
                element = doc.GetElement(cloneId);
            }
            else
            {
                element = FindByIdentity(doc, entry);
            }

            if (element == null || !element.IsValidObject)
            {
                // Something deleted in the meantime (or with an earlier entry's host) already has the wanted outcome
                return isDelete
                    ? Status(result, JournalStatus.ALREADY_APPLIED, "Already gone from the model")
                    : Status(result, JournalStatus.SKIPPED, "Element not in the model");
            }

            string blocked = CheckEditable(doc, element);
            if (blocked != null) { return Status(result, JournalStatus.FAILED, blocked); }

            // 2. Hide: delete, or demolish in the new phase (existing elements only)
            if (isHide)
            {
                if (!isDelete)
                {
                    string reason = PhaseResolver.DemolishBlockReason(element, phases, out bool alreadyDone);
                    if (alreadyDone) { return Status(result, JournalStatus.ALREADY_APPLIED, reason); }
                    if (reason != null) { return Status(result, JournalStatus.FAILED, reason); }
                }

                EditResult hidden = Execute(doc, element, ToRequest(entry, element, isDelete ? EditOp.Delete : EditOp.PhaseDemolish), phases, out _);
                if (!hidden.Success) { return Status(result, JournalStatus.FAILED, hidden.Message); }

                result.Affected = hidden.AffectedIds?.Length ?? 1;
                int extra = Math.Max(0, result.Affected - 1);
                string done = isDelete ? "Deleted" : $"Demolished in {phases.New?.Name}";
                return Status(result, JournalStatus.APPLIED, extra > 0 ? $"{done} (+{extra} dependent)" : done);
            }

            // 3. Moves and clones: is the element still where the file saw it?
            bool isClone = entry.Op == JournalOps.CLONE;
            if (!isClone && entry.Op != JournalOps.TRANSFORM) { return Status(result, JournalStatus.FAILED, $"Unknown edit '{entry.Op}'"); }
            if (element.Location is not LocationPoint location) { return Status(result, JournalStatus.FAILED, "Not a point-based element"); }

            Vector3 now = ToMetres(location.Point);
            double movedMm = Vector3.Distance(now, entry.Pivot) * 1000.0;
            string note = null;
            if (movedMm > toleranceMm)
            {
                string moved = $"{(isClone ? "Source moved" : "Moved")} in Revit since the file was made ({FormatMm(movedMm)})";
                if (!applyConflicts) { return Status(result, JournalStatus.CONFLICT, moved); }
                note = moved + ": applied from where it is now";
            }

            // 4. Apply
            EditResult applied = Execute(doc, element, ToRequest(entry, element, isClone ? EditOp.Copy : EditOp.Transform), phases, out ElementId newId);
            if (!applied.Success) { return Status(result, JournalStatus.FAILED, applied.Message); }

            if (isClone)
            {
                if (entry.NewCloneKey != 0) { clones[entry.NewCloneKey] = newId; }
                result.NewElementId = newId.Value;
                note ??= phases.New == null ? "Copied" : $"Copied (new in {phases.New.Name})";
            }
            return Status(result, JournalStatus.APPLIED, note ?? "Moved");
        }

        /// <summary>
        /// A family library placement: places the type's new instance where the file put it and registers it under
        /// the entry's clone key (later moves of it target that key). No staleness check: nothing existed before.
        /// </summary>
        private JournalEntryResult ApplyPlace(Document doc, JournalEntry entry, PhasePair phases, Dictionary<int, ElementId> clones, JournalEntryResult result)
        {
            var request = new EditRequest
            {
                Ticket = entry.Seq,
                Op = EditOp.Place,
                NewCloneKey = entry.NewCloneKey,
                TypeUniqueId = entry.TypeUniqueId,
                TypeId = entry.TypeId,
                Pivot = entry.Pivot,
                Angle = entry.Angle,
                Label = string.IsNullOrWhiteSpace(entry.Label) ? "Place" : entry.Label
            };
            EditResult placed = Place(doc, request, phases, out ElementId newId);
            if (!placed.Success) { return Status(result, JournalStatus.FAILED, placed.Message); }

            if (entry.NewCloneKey != 0) { clones[entry.NewCloneKey] = newId; }
            result.NewElementId = newId.Value;
            return Status(result, JournalStatus.APPLIED, phases.New == null ? "Placed" : $"Placed (new in {phases.New.Name})");
        }

        /// <summary>
        /// The target by UniqueId, else (older files without one) by ElementId.
        /// </summary>
        private static Element FindByIdentity(Document doc, JournalEntry entry)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.UniqueId)) { return doc.GetElement(entry.UniqueId); }
                if (entry.ElementId > 0) { return doc.GetElement(new ElementId(entry.ElementId)); }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Push target lookup failed for #{entry.Seq}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// A journal entry as an edit request on a resolved element.
        /// </summary>
        private static EditRequest ToRequest(JournalEntry entry, Element element, EditOp op) => new()
        {
            Ticket = entry.Seq,
            Op = op,
            ElementId = element.Id.Value,
            NewCloneKey = entry.NewCloneKey,
            Pivot = entry.Pivot,
            Translation = entry.Offset,
            Angle = entry.Angle,
            Label = string.IsNullOrWhiteSpace(entry.Label) ? entry.Op : entry.Label
        };

        #endregion

        #region Phases

        /// <summary>
        /// The phases for a push: the model's phases with the file's names, else the session's (with a note).
        /// </summary>
        private PhasePair PushPhases(Document doc, JournalApplyPayload request, out string note)
        {
            note = null;
            List<Phase> all = PhaseResolver.All(doc);
            if (all.Count == 0) { return new PhasePair(); }

            PhasePair session = SessionPhases(doc);
            Phase newPhase = PhaseResolver.FindByName(doc, request.PhaseName);
            if (newPhase == null)
            {
                newPhase = session.New ?? all[^1];
                if (!string.IsNullOrWhiteSpace(request.PhaseName))
                {
                    note = $"Phase “{request.PhaseName}” is not in this model: demolished in “{newPhase.Name}”.";
                }
            }

            int newIndex = all.FindIndex(p => p.Id == newPhase.Id);
            Phase existing = PhaseResolver.FindByName(doc, request.ExistingPhaseName);
            if (existing == null || all.FindIndex(p => p.Id == existing.Id) >= newIndex)
            {
                existing = session.New?.Id == newPhase.Id && session.Existing != null ? session.Existing : null;
                if (existing == null && newIndex > 0) { existing = all[newIndex - 1]; }
            }
            return new PhasePair { Existing = existing, New = newPhase };
        }

        #endregion

        #region Helpers

        private static JournalEntryResult Status(JournalEntryResult result, string status, string message)
        {
            result.Status = status;
            result.Message = message ?? string.Empty;
            return result;
        }

        private static JournalResultPayload Refuse(JournalResultPayload answer, string message)
        {
            Utilities.Log_Utils.Write($"Push refused: {message}");
            answer.Success = false;
            answer.Message = message;
            answer.Count();
            return answer;
        }

        /// <summary>
        /// Feet (XYZ) to metres (Revit internal axes).
        /// </summary>
        private static Vector3 ToMetres(XYZ feet) => new((float)(feet.X * SceneExtractor.FT), (float)(feet.Y * SceneExtractor.FT), (float)(feet.Z * SceneExtractor.FT));

        private static string FormatMm(double mm) => mm >= 1000.0 ? $"{mm / 1000.0:0.00} m" : $"{mm:0} mm";

        #endregion
    }
}
