using System.Numerics;

// The class belongs to the Edits namespace
namespace BimGo.Edits
{
    /// <summary>
    /// What an edit does.
    /// </summary>
    public enum EditOp
    {
        /// <summary>Set Phase Demolished to the session's phase.</summary>
        PhaseDemolish,

        /// <summary>Delete the element (and anything Revit deletes with it).</summary>
        Delete,

        /// <summary>Rotate about a vertical axis through a pivot, then translate.</summary>
        Transform,

        /// <summary>Copy the element, then rotate and translate the copy.</summary>
        Copy,

        /// <summary>
        /// Place a new instance of a family type (the family library) at <see cref="EditRequest.Pivot"/>, turned by
        /// <see cref="EditRequest.Angle"/>. Additive to protocol 1: an older add-in can't read it (the app times out).
        /// </summary>
        Place
    }

    /// <summary>
    /// An edit the game asks its model source to make (Revit, or a standalone file's journal).
    /// Plain data only: no Revit types. All points and vectors are Revit internal coordinates in metres
    /// (scene-local + origin offset).
    /// </summary>
    public sealed class EditRequest
    {
        /// <summary>Unique id, echoed in the result.</summary>
        public int Ticket { get; set; }

        /// <summary>The operation.</summary>
        public EditOp Op { get; init; }

        /// <summary>The target's Revit ElementId value, or 0 when the target is a pending clone (see <see cref="TargetCloneKey"/>).</summary>
        public long ElementId { get; init; }

        /// <summary>
        /// When non-zero, the target is a clone created earlier in this session whose Revit id the game may not
        /// know yet; the Revit side resolves it from its own key map (requests run in order).
        /// </summary>
        public int TargetCloneKey { get; init; }

        /// <summary>For <see cref="EditOp.Copy"/> and <see cref="EditOp.Place"/>: the key the new element is registered under.</summary>
        public int NewCloneKey { get; init; }

        /// <summary>For <see cref="EditOp.Place"/>: the family type's UniqueId (null otherwise).</summary>
        public string TypeUniqueId { get; init; }

        /// <summary>For <see cref="EditOp.Place"/>: the family type's ElementId value (fallback; 0 otherwise).</summary>
        public long TypeId { get; init; }

        /// <summary>
        /// Rotation pivot (metres, Revit internal) before any translation. For <see cref="EditOp.Place"/>: where the
        /// new instance's location point goes (no translation).
        /// </summary>
        public Vector3 Pivot { get; init; }

        /// <summary>Translation applied after the rotation (metres).</summary>
        public Vector3 Translation { get; init; }

        /// <summary>Rotation about +Z (radians, counter-clockwise).</summary>
        public float Angle { get; init; }

        /// <summary>A short label for the Revit transaction / undo list.</summary>
        public string Label { get; init; }
    }

    /// <summary>
    /// The model source's answer to a request.
    /// </summary>
    public sealed class EditResult
    {
        /// <summary>The request's ticket.</summary>
        public int Ticket { get; init; }

        /// <summary>The request's operation.</summary>
        public EditOp Op { get; init; }

        /// <summary>True if the change was committed (in Revit, or recorded in the file's journal).</summary>
        public bool Success { get; init; }

        /// <summary>A short, user-facing reason on failure (or a note on success).</summary>
        public string Message { get; init; }

        /// <summary>For delete / demolish: every element Revit deleted or demolished as a result (ElementId values).</summary>
        public long[] AffectedIds { get; init; } = Array.Empty<long>();

        /// <summary>For copy and place: the new element's ElementId value.</summary>
        public long NewElementId { get; init; }

        /// <summary>For copy and place: the clone key from the request.</summary>
        public int CloneKey { get; init; }
    }
}
