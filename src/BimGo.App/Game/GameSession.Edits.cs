using System.Numerics;
using BimGo.Edits;
using BimGo.Physics;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Runtime element edits (hide, move, clone), the model source pump and the room readout.
    ///
    /// Edits are optimistic: the game changes immediately and the guns submit a request to the
    /// <see cref="Source"/> (Revit, or the file's journal); if it refuses, the gun's result callback puts things back.
    /// Every accepted edit is recorded in the journal.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly bool[] _hidden;
        private readonly Dictionary<long, int> _elementIndexById;
        private readonly Dictionary<string, int> _elementIndexByUniqueId = new(StringComparer.Ordinal);
        private readonly Dictionary<long, List<long>> _hostedBy = new();
        private readonly Dictionary<int, Action<EditResult>> _editCallbacks = new();
        private readonly Dictionary<int, EditRequest> _pendingRequests = new();
        private int _nextCloneKey;

        // Room readout
        private int _roomIndex = -1;
        private Vector3 _roomCheckedAt = new(float.MaxValue);
        private float _roomBannerUntil;

        #endregion

        #region Element state

        /// <summary>Moved and cloned elements.</summary>
        public DynamicSet Dynamics { get; private set; }

        /// <summary>True when edits go to a Revit model (false for a standalone file).</summary>
        public bool EditsGoToRevit => Source.IsRevit;

        /// <summary>True when a Revit session has no write-back: edits then only change the walkthrough.</summary>
        public bool EditsLocalOnly => Source.IsRevit && !Source.CanEdit;

        /// <summary>Edits still waiting for an answer.</summary>
        public int EditsPending => Source.Pending;

        /// <summary>Where committed edits go, for messages: "Revit" or "the file".</summary>
        public string EditTargetName => Source.IsRevit ? "Revit" : "the file";

        /// <summary>
        /// True if a static element is hidden (demolished, deleted or replaced by its moved instance).
        /// </summary>
        public bool IsHidden(int element) => _hidden[element];

        /// <summary>
        /// True if a pick target still exists in the game: a visible static element or an active dynamic instance.
        /// </summary>
        public bool IsTargetPresent(int element, int dynamicId)
        {
            if (dynamicId > 0)
            {
                DynamicInstance instance = Dynamics.Find(dynamicId);
                return instance != null && Dynamics.IsActive(instance);
            }
            return element >= 0 && !_hidden[element] && !_userHidden[element];
        }

        /// <summary>
        /// Hides or restores a static element (drawing, picking and collision).
        /// </summary>
        public void SetStaticHidden(int element, bool hidden)
        {
            if (_hidden[element] == hidden || Scene.Elements[element].IsLibraryTemplate) { return; }
            _hidden[element] = hidden;
            _sceneRevision++;
            _renderer.SetElementHidden(element, hidden || _userHidden[element]);

            bool visible = _groupVisible[Rendering.SceneBatches.GroupOf(Scene.Elements[element])] && !hidden && !_userHidden[element];
            _pickMask[element] = visible;
            _collisionMask[element] = visible && Scene.Elements[element].CategoryIndex != _doorCategory;
        }

        /// <summary>
        /// The dynamic instance standing in for a static element, created on first use (the static copy is hidden).
        /// </summary>
        public DynamicInstance MakeDynamic(int element)
        {
            DynamicInstance existing = Dynamics.FindOriginal(element);
            if (existing != null) { return existing; }

            _renderer.EnsureDynamicGeometry(element);
            DynamicInstance instance = Dynamics.Create(element, Vector3.Zero, 0f, Scene.Elements[element].ElementId, isClone: false, cloneKey: 0);
            SetStaticHidden(element, true);
            return instance;
        }

        /// <summary>
        /// Puts a moved original back into the static scene if it is untransformed (tidies up after a cancelled move).
        /// </summary>
        public void RestoreIfUnmoved(DynamicInstance instance)
        {
            if (instance == null || instance.IsClone || instance.Hidden) { return; }
            if (instance.Offset.LengthSquared() > 1e-10f || MathF.Abs(instance.Angle) > 1e-6f) { return; }
            Dynamics.Remove(instance);
            SetStaticHidden(instance.Element, false);
        }

        /// <summary>
        /// Creates an uncommitted clone of a target, starting at the target's current transform.
        /// </summary>
        /// <param name="element">The source element.</param>
        /// <param name="source">The dynamic instance being cloned, or null for a static element.</param>
        /// <param name="cloneKey">The key to use (journal replay), or 0 for the next free key.</param>
        public DynamicInstance CreateClone(int element, DynamicInstance source, int cloneKey = 0)
        {
            _renderer.EnsureDynamicGeometry(element);
            Vector3 offset = source?.Offset ?? Vector3.Zero;
            float angle = source?.Angle ?? 0f;
            if (cloneKey <= 0) { cloneKey = ++_nextCloneKey; }
            else { _nextCloneKey = Math.Max(_nextCloneKey, cloneKey); }
            return Dynamics.Create(element, offset, angle, revitId: 0, isClone: true, cloneKey: cloneKey);
        }

        /// <summary>
        /// Hides everything the source reports as deleted / demolished (static elements and dynamic instances).
        /// </summary>
        /// <returns>The number of game objects hidden.</returns>
        public int ApplyRemovals(long[] revitIds)
        {
            int count = 0;
            foreach (long id in revitIds)
            {
                if (_elementIndexById.TryGetValue(id, out int element) && Dynamics.FindOriginal(element) == null && !_hidden[element])
                {
                    SetStaticHidden(element, true);
                    count++;
                }
                foreach (DynamicInstance instance in Dynamics.Instances)
                {
                    if (instance.RevitId == id && !instance.Hidden)
                    {
                        instance.Hidden = true;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Scene-local metres to Revit internal metres.
        /// </summary>
        public Vector3 ToRevit(Vector3 local) => local + Scene.OriginOffset;

        #endregion

        #region Gizmo snap

        /// <summary>Gizmo / Clone snap mode (G toggles; Ctrl inverts while held).</summary>
        public bool GizmoSnap { get; private set; }

        /// <summary>Snap move increment (mm).</summary>
        public float SnapMoveMm { get; private set; } = 50f;

        /// <summary>Snap rotation increment (degrees).</summary>
        public float SnapAngleDeg { get; private set; } = 15f;

        /// <summary>Snap move increment (metres).</summary>
        public float SnapMove => SnapMoveMm / 1000f;

        /// <summary>Snap rotation increment (radians).</summary>
        public float SnapAngle => SnapAngleDeg * MathF.PI / 180f;

        /// <summary>
        /// Toggles snap mode.
        /// </summary>
        public void ToggleGizmoSnap()
        {
            GizmoSnap = !GizmoSnap;
            Sound.Play(Audio.SoundId.UiClick);
            Toast(GizmoSnap ? $"Snap ON: {DescribeSnap()}" : "Snap OFF: smooth moves (hold Ctrl to snap for a moment)");
        }

        /// <summary>
        /// Moves the move increment one preset up (+1) or down (-1).
        /// </summary>
        public void StepSnapMove(int direction)
        {
            SnapMoveMm = StepPreset(LaunchSettings.SNAP_MOVE_STEPS_MM, SnapMoveMm, direction);
            Sound.Play(Audio.SoundId.UiClick);
            Toast($"Snap: {DescribeSnap()}");
        }

        /// <summary>
        /// Moves the rotation increment one preset up (+1) or down (-1).
        /// </summary>
        public void StepSnapAngle(int direction)
        {
            SnapAngleDeg = StepPreset(LaunchSettings.SNAP_ANGLE_STEPS_DEG, SnapAngleDeg, direction);
            Sound.Play(Audio.SoundId.UiClick);
            Toast($"Snap: {DescribeSnap()}");
        }

        /// <summary>"50 mm · 15°".</summary>
        public string DescribeSnap()
        {
            string move = SnapMoveMm >= 1000f ? $"{SnapMoveMm / 1000f:0.##} m" : $"{SnapMoveMm:0} mm";
            return $"{move} · {SnapAngleDeg:0}°";
        }

        private static float StepPreset(float[] presets, float current, int direction)
        {
            int index = Array.IndexOf(presets, LaunchSettings.NearestStep(presets, current));
            index = Math.Clamp(index + Math.Sign(direction), 0, presets.Length - 1);
            return presets[index];
        }

        #endregion

        #region Model source

        /// <summary>
        /// Sends an edit to the model source. The callback runs on the game thread when the answer arrives;
        /// accepted edits are recorded in the journal first.
        /// </summary>
        /// <returns>False if the source can't take edits (the edit then stays in the walkthrough only).</returns>
        public bool SubmitEdit(EditRequest request, Action<EditResult> onResult)
        {
            if (!Source.CanEdit) { return false; }
            int ticket = Source.Submit(request);
            if (ticket < 0) { return false; }
            _pendingRequests[ticket] = request;
            if (onResult != null) { _editCallbacks[ticket] = onResult; }
            return true;
        }

        /// <summary>
        /// Delivers the source's answers (recording accepted edits) and lets the source do its housekeeping.
        /// </summary>
        private void PumpSource(float dt)
        {
            Source.Pump(dt);

            while (Source.TryGetResult(out EditResult result))
            {
                if (_pendingRequests.Remove(result.Ticket, out EditRequest request) && result.Success)
                {
                    RecordEdit(request, result);
                }

                if (!_editCallbacks.Remove(result.Ticket, out Action<EditResult> callback)) { continue; }
                try
                {
                    callback(result);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Edit callback failed: {ex}");
                }
            }
        }

        #endregion

        #region Rooms

        /// <summary>The room the player is in, or null.</summary>
        public RoomInfo CurrentRoom => _roomIndex >= 0 ? Scene.Rooms[_roomIndex] : null;

        /// <summary>
        /// Re-evaluates the current room when the player has moved a little; starts the banner on a change.
        /// </summary>
        private void UpdateRoom()
        {
            if (Scene.Rooms.Length == 0) { return; }

            Vector3 feet = _player.Feet;
            if (Vector3.DistanceSquared(feet, _roomCheckedAt) < 0.05f * 0.05f) { return; }
            _roomCheckedAt = feet;

            int room = FindRoom(new Vector3(feet.X, feet.Y, feet.Z + 0.3f));
            if (room == _roomIndex) { return; }

            _roomIndex = room;
            if (room >= 0) { _roomBannerUntil = _clock + 2.4f; }
        }

        /// <summary>
        /// The smallest room volume containing the point, or -1. Host rooms win: a linked model's rooms only name
        /// places the host has no room for.
        /// </summary>
        private int FindRoom(Vector3 point)
        {
            int best = -1;
            float bestHeight = float.MaxValue;
            bool bestIsHost = false;
            RoomInfo[] rooms = Scene.Rooms;

            for (int i = 0; i < rooms.Length; i++)
            {
                RoomInfo room = rooms[i];
                bool isHost = room.Link == 0;
                if (bestIsHost && !isHost) { continue; }
                if (point.Z < room.BottomZ || point.Z > room.TopZ) { continue; }
                if (point.X < room.Min.X || point.X > room.Max.X || point.Y < room.Min.Y || point.Y > room.Max.Y) { continue; }

                // Even-odd over all loops (islands become holes automatically)
                bool inside = false;
                foreach (Vector2[] loop in room.Loops)
                {
                    if (InsidePolygon(loop, point.X, point.Y)) { inside = !inside; }
                }
                if (!inside) { continue; }

                float height = room.TopZ - room.BottomZ;
                if (height < bestHeight || (isHost && !bestIsHost))
                {
                    best = i;
                    bestHeight = height;
                    bestIsHost = isHost;
                }
            }
            return best;
        }

        /// <summary>
        /// Crossing-number point-in-polygon test.
        /// </summary>
        private static bool InsidePolygon(Vector2[] polygon, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i], b = polygon[j];
                if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        #endregion
    }
}
