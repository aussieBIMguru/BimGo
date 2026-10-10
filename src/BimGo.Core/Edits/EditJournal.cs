using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Edits namespace
namespace BimGo.Edits
{
    /// <summary>
    /// The kind of a journal entry (as written to journal.json).
    /// </summary>
    public static class JournalOps
    {
        /// <summary>The element (and its hosted inserts) was removed: see <see cref="JournalEntry.Mode"/>.</summary>
        public const string HIDE = "hide";

        /// <summary>The element was rotated about its pivot, then moved.</summary>
        public const string TRANSFORM = "transform";

        /// <summary>The element was copied, then the copy rotated and moved.</summary>
        public const string CLONE = "clone";

        /// <summary>
        /// A new instance of a family type (the family library) was placed at <see cref="JournalEntry.Pivot"/>, turned
        /// by <see cref="JournalEntry.Angle"/>; <see cref="JournalEntry.TypeUniqueId"/> names the type and
        /// <see cref="JournalEntry.NewCloneKey"/> the new instance (later moves target it like a clone).
        /// </summary>
        public const string PLACE = "place";

        /// <summary>True for the ops that create a new instance with a clone key (clone, place).</summary>
        public static bool Creates(string op) => op == CLONE || op == PLACE;

        /// <summary><see cref="HIDE"/> mode: Phase Demolished set to the session phase.</summary>
        public const string MODE_DEMOLISH = "demolish";

        /// <summary><see cref="HIDE"/> mode: deleted from the model.</summary>
        public const string MODE_DELETE = "delete";
    }

    /// <summary>
    /// One committed edit. The journal is the non-destructive record of everything changed in a walkthrough: the
    /// extracted geometry is never altered, and on load the entries are replayed in order.
    ///
    /// Targets are identified by the source element (<see cref="UniqueId"/> is the stable key; <see cref="ElementId"/>
    /// is kept for speed and as a fallback) or, for clones made in the walkthrough, by their clone key.
    /// Coordinates are Revit internal metres, as in <see cref="EditRequest"/>.
    /// </summary>
    public sealed class JournalEntry
    {
        /// <summary>1-based order of the entry.</summary>
        public int Seq { get; set; }

        /// <summary>One of <see cref="JournalOps"/>.</summary>
        public string Op { get; set; } = string.Empty;

        /// <summary>For hide: <see cref="JournalOps.MODE_DEMOLISH"/> or <see cref="JournalOps.MODE_DELETE"/>.</summary>
        public string Mode { get; set; }

        /// <summary>The target (or clone source) ElementId value; 0 when the target is a walkthrough clone.</summary>
        public long ElementId { get; set; }

        /// <summary>The target (or clone source) UniqueId; empty when the target is a walkthrough clone.</summary>
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>When non-zero, the target (or clone source) is the walkthrough clone with this key.</summary>
        public int TargetCloneKey { get; set; }

        /// <summary>For clone and place: the key of the new instance (unique within the file).</summary>
        public int NewCloneKey { get; set; }

        /// <summary>For place: the family type's UniqueId (null otherwise, and not written).</summary>
        public string TypeUniqueId { get; set; }

        /// <summary>For place: the family type's ElementId value (0 otherwise, and not written).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long TypeId { get; set; }

        /// <summary>Rotation pivot before the move (Revit internal metres).</summary>
        public Vector3 Pivot { get; set; }

        /// <summary>Translation after the rotation (metres).</summary>
        public Vector3 Offset { get; set; }

        /// <summary>Rotation about +Z (radians, counter-clockwise).</summary>
        public float Angle { get; set; }

        /// <summary>A short description (shown in lists and used as the Revit transaction name on push).</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>When the edit was made (UTC).</summary>
        public DateTime Utc { get; set; }

        /// <summary>Who made it.</summary>
        public string User { get; set; } = string.Empty;

        /// <summary>True if the edit was also committed to a live Revit model when it was made.</summary>
        public bool AppliedToRevit { get; set; }

        /// <summary>For clones and placements committed to Revit: the new element's ElementId value (0 otherwise).</summary>
        public long RevitElementId { get; set; }
    }

    /// <summary>
    /// The ordered list of committed edits of one model, with a revision counter for dirty tracking and a redo history
    /// of undone entries (in memory only: never saved). Single-threaded (game thread).
    /// </summary>
    public sealed class EditJournal
    {
        private readonly List<JournalEntry> _entries = new();

        // Undone entries, most recent last (redo pops from the end). Cleared by any new edit.
        private readonly List<JournalEntry> _redo = new();

        /// <summary>The entries in order (don't modify; use <see cref="Add"/> / <see cref="RemoveLast"/>).</summary>
        public IReadOnlyList<JournalEntry> Entries => _entries;

        /// <summary>Number of entries.</summary>
        public int Count => _entries.Count;

        /// <summary>Number of undone entries that <see cref="Redo"/> can restore.</summary>
        public int RedoCount => _redo.Count;

        /// <summary>Increments on every change (compare with a saved value to detect unsaved edits).</summary>
        public int Revision { get; private set; }

        /// <summary>
        /// Creates an empty journal, or one holding entries read from a file.
        /// </summary>
        public EditJournal(IEnumerable<JournalEntry> entries = null)
        {
            if (entries == null) { return; }
            foreach (JournalEntry entry in entries)
            {
                if (entry != null) { _entries.Add(entry); }
            }
            Renumber();
        }

        /// <summary>
        /// Appends a new edit (its <see cref="JournalEntry.Seq"/> is assigned here). A new edit ends the redo history,
        /// as in any editor: what was undone can no longer be redone on top of it.
        /// </summary>
        public void Add(JournalEntry entry)
        {
            entry.Seq = _entries.Count + 1;
            _entries.Add(entry);
            _redo.Clear();
            Revision++;
        }

        /// <summary>
        /// Removes and returns the last entry (undo), or null if empty. The entry is kept for <see cref="Redo"/>.
        /// </summary>
        public JournalEntry RemoveLast()
        {
            if (_entries.Count == 0) { return null; }
            JournalEntry last = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            _redo.Add(last);
            Revision++;
            return last;
        }

        /// <summary>
        /// Puts the most recently undone entry back at the end of the journal (redo), or returns null if there is
        /// nothing to redo. The entry keeps its content (target, transform, author, time); only its sequence number is
        /// reassigned.
        /// </summary>
        public JournalEntry Redo()
        {
            if (_redo.Count == 0) { return null; }
            JournalEntry entry = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            entry.Seq = _entries.Count + 1;
            _entries.Add(entry);
            Revision++;
            return entry;
        }

        /// <summary>
        /// The entry <see cref="Redo"/> would restore, or null.
        /// </summary>
        public JournalEntry PeekRedo() => _redo.Count == 0 ? null : _redo[^1];

        /// <summary>
        /// The largest clone key used by any entry (new clones must use larger keys).
        /// </summary>
        public int MaxCloneKey()
        {
            int max = 0;
            foreach (JournalEntry entry in _entries)
            {
                max = Math.Max(max, Math.Max(entry.NewCloneKey, entry.TargetCloneKey));
            }

            // Undone clones keep their keys reserved, so a redo never collides with a clone made in between
            foreach (JournalEntry entry in _redo)
            {
                max = Math.Max(max, Math.Max(entry.NewCloneKey, entry.TargetCloneKey));
            }
            return max;
        }

        /// <summary>
        /// Number of entries not yet applied to a Revit model.
        /// </summary>
        public int CountNotInRevit()
        {
            int count = 0;
            foreach (JournalEntry entry in _entries)
            {
                if (!entry.AppliedToRevit) { count++; }
            }
            return count;
        }

        /// <summary>
        /// The entries not yet applied to a Revit model, in order (what a push sends).
        /// </summary>
        public List<JournalEntry> PendingForRevit()
        {
            var pending = new List<JournalEntry>();
            foreach (JournalEntry entry in _entries)
            {
                if (!entry.AppliedToRevit) { pending.Add(entry); }
            }
            return pending;
        }

        /// <summary>
        /// Records that an entry is now in the Revit model (after a push). Counts as a change (the file is dirty).
        /// </summary>
        /// <param name="seq">The entry's sequence number.</param>
        /// <param name="revitElementId">For clones: the new element's id (0 to leave unchanged).</param>
        /// <returns>True if the entry was found and changed.</returns>
        public bool MarkApplied(int seq, long revitElementId)
        {
            if (seq < 1 || seq > _entries.Count) { return false; }
            JournalEntry entry = _entries[seq - 1];
            if (entry.Seq != seq) { return false; }
            if (entry.AppliedToRevit && (revitElementId <= 0 || entry.RevitElementId == revitElementId)) { return false; }

            entry.AppliedToRevit = true;
            if (revitElementId > 0) { entry.RevitElementId = revitElementId; }
            Revision++;
            return true;
        }

        private void Renumber()
        {
            for (int i = 0; i < _entries.Count; i++) { _entries[i].Seq = i + 1; }
        }
    }
}
