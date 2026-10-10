using System.Numerics;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Vk = BimGo.Native.Win32;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// The gizmo's two modes: R switches between them while locked on.
    /// </summary>
    internal enum GizmoMode
    {
        /// <summary>WASD move in plan (view-relative), E / Q up / down.</summary>
        Move,

        /// <summary>A / D rotate about the vertical axis through the pivot (level families stay level).</summary>
        Rotate
    }

    /// <summary>
    /// The move / rotate gizmo shared by the Gizmo and Clone guns.
    ///
    /// Locking on starts in <see cref="GizmoMode.Move"/>: WASD (or the arrows) move the target in plan relative to
    /// the view, E raises it and Q lowers it. R switches to <see cref="GizmoMode.Rotate"/>: A / D (or ← / →) turn it
    /// counter-clockwise / clockwise about its pivot on the XY plane (no other rotations: Revit families stay level).
    /// Shift slows either down.
    ///
    /// Snap mode (G toggles it; holding Ctrl inverts it for as long as it is held) clamps the change since locking
    /// on to the session's increments (<see cref="GameSession.SnapMove"/> / <see cref="GameSession.SnapAngle"/>):
    /// each key press (and key repeat) steps one increment, plan moves follow the world X / Y axis nearest the view
    /// direction, so offsets stay exact multiples. Z / X step the current mode's increment down / up.
    /// The owning gun decides what committing and cancelling mean.
    /// </summary>
    internal sealed class GizmoController
    {
        #region Constants and fields

        private const float MOVE_SPEED = 1f;              // m/s
        private const float ROTATE_SPEED = MathF.PI / 2f; // rad/s (90°/s)
        private const float FINE = 0.25f;

        /// <summary>Drop to surface: furthest drop (m) below the element's base.</summary>
        public const float MAX_DROP = 10f;

        /// <summary>
        /// Drop to surface: the ray starts this far (m) above the box's base (at most half the box's height), so an
        /// element sunk into a floor or desk by up to this much is lifted onto it instead of dropping through.
        /// </summary>
        private const float LIFT_REACH = 0.3f;

        private readonly GameSession _session;
        private Vector3 _startOffset, _rawOffset;
        private float _startAngle, _rawAngle;
        private float _clock;

        // After a drop the height is exact, not a whole snap increment: snapping leaves Z alone until E / Q steps it
        private bool _zFree;

        #endregion

        /// <summary>
        /// Creates the controller.
        /// </summary>
        public GizmoController(GameSession session)
        {
            _session = session;
        }

        /// <summary>The instance being edited, or null.</summary>
        public DynamicInstance Target { get; private set; }

        /// <summary>True while locked on.</summary>
        public bool Active => Target != null;

        /// <summary>Move or rotate (R switches; every lock starts in move).</summary>
        public GizmoMode Mode { get; private set; } = GizmoMode.Move;

        /// <summary>
        /// Switches between move and rotate.
        /// </summary>
        public void ToggleMode() => Mode = Mode == GizmoMode.Move ? GizmoMode.Rotate : GizmoMode.Move;

        /// <summary>Pivot translation since locking on.</summary>
        public Vector3 DeltaOffset => Target == null ? Vector3.Zero : Target.Offset - _startOffset;

        /// <summary>Rotation since locking on (radians, CCW).</summary>
        public float DeltaAngle => Target == null ? 0f : Target.Angle - _startAngle;

        /// <summary>The pivot (scene-local) when the lock began.</summary>
        public Vector3 StartPivot => Target == null ? Vector3.Zero : Target.BasePivot + _startOffset;

        /// <summary>True if anything changed since locking on.</summary>
        public bool HasChanges => DeltaOffset.LengthSquared() > 1e-10f || MathF.Abs(DeltaAngle) > 1e-6f;

        #region Lifecycle

        /// <summary>
        /// Locks on to an instance.
        /// </summary>
        public void Begin(DynamicInstance target)
        {
            Target = target;
            Mode = GizmoMode.Move;
            _startOffset = _rawOffset = target.Offset;
            _startAngle = _rawAngle = target.Angle;
            _zFree = false;
        }

        /// <summary>
        /// Puts the target back where it was when the lock began, and lets go.
        /// </summary>
        /// <returns>The released instance.</returns>
        public DynamicInstance Cancel()
        {
            DynamicInstance target = Target;
            if (target != null) { _session.Dynamics.SetTransform(target, _startOffset, _startAngle); }
            Target = null;
            return target;
        }

        /// <summary>
        /// Lets go, keeping the current transform.
        /// </summary>
        /// <returns>The released instance.</returns>
        public DynamicInstance End()
        {
            DynamicInstance target = Target;
            Target = null;
            return target;
        }

        #endregion

        #region Update

        /// <summary>
        /// Applies this frame's keys.
        /// </summary>
        public void Update(float dt, InputState input)
        {
            _clock += dt;
            if (Target == null) { return; }

            bool rotating = Mode == GizmoMode.Rotate;
            float forward = 0f, strafe = 0f, lift = 0f, turn = 0f;
            if (!rotating)
            {
                if (input.IsDown('W') || input.IsDown(Vk.VK_UP)) { forward += 1f; }
                if (input.IsDown('S') || input.IsDown(Vk.VK_DOWN)) { forward -= 1f; }
                if (input.IsDown('D') || input.IsDown(Vk.VK_RIGHT)) { strafe += 1f; }
                if (input.IsDown('A') || input.IsDown(Vk.VK_LEFT)) { strafe -= 1f; }
                if (input.IsDown('E')) { lift += 1f; }
                if (input.IsDown('Q')) { lift -= 1f; }
            }
            else
            {
                if (input.IsDown('A') || input.IsDown(Vk.VK_LEFT)) { turn += 1f; }
                if (input.IsDown('D') || input.IsDown(Vk.VK_RIGHT)) { turn -= 1f; }
            }
            float scale = input.IsDown(Vk.VK_SHIFT) ? FINE : 1f;

            // Camera-relative in plan, plus straight up / down
            Vector3 flatForward = Flatten(_session.Camera.Forward);
            Vector3 flatRight = Flatten(_session.Camera.Right);
            Vector3 move = flatForward * forward + flatRight * strafe;
            if (move.LengthSquared() > 1f) { move = Vector3.Normalize(move); }
            move.Z = lift;

            Vector3 offset;
            float angle;
            if (IsSnapping(input))
            {
                // Stepped: one increment per press / key repeat, along the world axis nearest the view direction
                float step = _session.SnapMove, angleStep = _session.SnapAngle;
                if (!rotating)
                {
                    Vector3 stepMove = Vector3.Zero;
                    if (input.IsPressedOrRepeated('W') || input.IsPressedOrRepeated(Vk.VK_UP)) { stepMove += flatForward; }
                    if (input.IsPressedOrRepeated('S') || input.IsPressedOrRepeated(Vk.VK_DOWN)) { stepMove -= flatForward; }
                    if (input.IsPressedOrRepeated('D') || input.IsPressedOrRepeated(Vk.VK_RIGHT)) { stepMove += flatRight; }
                    if (input.IsPressedOrRepeated('A') || input.IsPressedOrRepeated(Vk.VK_LEFT)) { stepMove -= flatRight; }
                    _rawOffset += NearestAxis(stepMove) * step;
                    if (input.IsPressedOrRepeated('E')) { _rawOffset.Z += step; _zFree = false; }
                    if (input.IsPressedOrRepeated('Q')) { _rawOffset.Z -= step; _zFree = false; }
                }
                else
                {
                    if (input.IsPressedOrRepeated('A') || input.IsPressedOrRepeated(Vk.VK_LEFT)) { _rawAngle += angleStep; }
                    if (input.IsPressedOrRepeated('D') || input.IsPressedOrRepeated(Vk.VK_RIGHT)) { _rawAngle -= angleStep; }
                }

                // Clamp the change since lock-on to whole increments (also tidies a smooth move made before snapping)
                Vector3 delta = _rawOffset - _startOffset;
                _rawOffset = _startOffset + new Vector3(Snap(delta.X, step), Snap(delta.Y, step), _zFree ? delta.Z : Snap(delta.Z, step));
                _rawAngle = _startAngle + Snap(_rawAngle - _startAngle, angleStep);
                offset = _rawOffset;
                angle = _rawAngle;
            }
            else
            {
                _rawOffset += move * (MOVE_SPEED * scale * dt);
                _rawAngle += turn * ROTATE_SPEED * scale * dt;
                offset = _rawOffset;
                angle = _rawAngle;
            }

            if (offset != Target.Offset || angle != Target.Angle)
            {
                _session.Dynamics.SetTransform(Target, offset, angle);
            }
        }

        /// <summary>
        /// Drops or lifts the target so the bottom of its box rests on the first surface straight below the box's
        /// bottom centre: one ray down from <see cref="LIFT_REACH"/> above the base (at most half the box's height),
        /// against the visible static scene and the other moved / cloned elements, never the target itself. The first
        /// hit wins, so a lamp over a desk lands on the desk, not the floor under it. Stays locked on (uncommitted):
        /// RMB commits it as an ordinary move.
        /// </summary>
        /// <param name="message">Out: what happened, for a toast.</param>
        /// <returns>True when the target moved.</returns>
        public bool DropToSurface(out string message)
        {
            message = null;
            if (Target == null) { return false; }

            Aabb bounds = Target.WorldBounds;
            float reach = MathF.Min(LIFT_REACH, MathF.Max(0f, bounds.Size.Z) * 0.5f);
            Vector3 centre = bounds.Center;
            var origin = new Vector3(centre.X, centre.Y, bounds.Min.Z + reach);
            if (!_session.PickExcluding(origin, -Vector3.UnitZ, reach + MAX_DROP, Target, out RayHit hit))
            {
                message = "Nothing below to drop onto";
                return false;
            }

            float change = hit.Point.Z - bounds.Min.Z;
            if (MathF.Abs(change) < 5e-4f)
            {
                message = "Already resting on the surface below";
                return false;
            }

            _rawOffset = Target.Offset + new Vector3(0f, 0f, change);
            _zFree = true;
            _session.Dynamics.SetTransform(Target, _rawOffset, Target.Angle);
            message = change < 0f ? $"Dropped {-change:0.000} m onto the surface below" : $"Lifted {change:0.000} m onto the surface";
            return true;
        }

        /// <summary>
        /// True while snapping: the session's snap mode, inverted while Ctrl is held.
        /// </summary>
        public bool IsSnapping(InputState input) => _session.GizmoSnap ^ input.IsDown(Vk.VK_CONTROL);

        private static float Snap(float value, float step) => step > 0f ? MathF.Round(value / step) * step : value;

        /// <summary>
        /// The world X / Y unit direction nearest a plan direction (zero for no movement).
        /// </summary>
        private static Vector3 NearestAxis(Vector3 direction)
        {
            if (direction.LengthSquared() < 1e-6f) { return Vector3.Zero; }
            return MathF.Abs(direction.X) >= MathF.Abs(direction.Y)
                ? new Vector3(MathF.Sign(direction.X), 0f, 0f)
                : new Vector3(0f, MathF.Sign(direction.Y), 0f);
        }

        private static Vector3 Flatten(Vector3 v)
        {
            var flat = new Vector3(v.X, v.Y, 0f);
            return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitX;
        }

        #endregion

        #region Drawing

        /// <summary>
        /// Draws the gizmo: rotate ring, view-relative move arrows, and a trail from the starting pivot.
        /// </summary>
        public void Draw(Overlay3D overlay, uint colour)
        {
            if (Target == null) { return; }

            Aabb bounds = Target.WorldBounds;
            Vector3 pivot = Target.Pivot;
            float baseZ = bounds.Min.Z + 0.02f;
            var centre = new Vector3(pivot.X, pivot.Y, baseZ);
            Vector3 size = bounds.Size;
            float radius = MathF.Max(0.35f, 0.5f * MathF.Max(size.X, size.Y) + 0.2f);
            float pulse = 0.5f + 0.5f * MathF.Sin(_clock * 5f);

            // Rotate ring with a tick showing the current heading (bold in rotate mode, faint in move mode)
            bool rotating = Mode == GizmoMode.Rotate;
            overlay.Ring(centre, Vector3.UnitX, Vector3.UnitY, radius, radius, rotating ? 0.05f : 0.02f, Rgba.WithAlpha(colour, rotating ? 0.95f : 0.35f));
            float heading = Target.Angle;
            var tick = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
            overlay.Line(centre + tick * (radius - 0.12f), centre + tick * (radius + 0.12f), rotating ? 5f : 2f, Rgba.WithAlpha(colour, rotating ? 1f : 0.5f));

            if (!rotating)
            {
                // Move arrows along the view's forward / right in plan, and up / down (E / Q)
                Vector3 forward = Flatten(_session.Camera.Forward), right = Flatten(_session.Camera.Right);
                float arrow = radius + 0.35f;
                Arrow(overlay, centre, forward, arrow, UiTheme.AXIS_Y);
                Arrow(overlay, centre, -forward, arrow * 0.7f, Rgba.WithAlpha(UiTheme.AXIS_Y, 0.5f));
                Arrow(overlay, centre, right, arrow, UiTheme.AXIS_X);
                Arrow(overlay, centre, -right, arrow * 0.7f, Rgba.WithAlpha(UiTheme.AXIS_X, 0.5f));
                var top = new Vector3(pivot.X, pivot.Y, bounds.Max.Z + 0.1f);
                VerticalArrow(overlay, top, +1f, UiTheme.AXIS_Z);
                VerticalArrow(overlay, centre, -1f, Rgba.WithAlpha(UiTheme.AXIS_Z, 0.6f));
            }

            // Pivot post and the trail back to where it started
            overlay.Line(centre, new Vector3(pivot.X, pivot.Y, bounds.Max.Z + 0.1f), 1.5f, Rgba.WithAlpha(colour, 0.5f + 0.3f * pulse));
            Vector3 start = StartPivot;
            var startGround = new Vector3(start.X, start.Y, baseZ);
            if (Vector3.DistanceSquared(startGround, centre) > 1e-4f)
            {
                overlay.Line(startGround, centre, 2f, Rgba.WithAlpha(colour, 0.6f));
                overlay.Ring(startGround, Vector3.UnitX, Vector3.UnitY, 0.08f, 0.08f, 0.02f, Rgba.WithAlpha(colour, 0.6f));
            }
        }

        /// <summary>
        /// A short vertical arrow (up from the top, or down from the base) for E / Q.
        /// </summary>
        private void VerticalArrow(Overlay3D overlay, Vector3 from, float sign, uint colour)
        {
            Vector3 tip = from + new Vector3(0f, 0f, 0.45f * sign);
            Vector3 back = tip - new Vector3(0f, 0f, 0.14f * sign);
            Vector3 side = Flatten(_session.Camera.Right) * 0.08f;
            overlay.Line(from + new Vector3(0f, 0f, 0.05f * sign), back, 3f, colour);
            overlay.Triangle(tip, back + side, back - side, colour);
        }

        private static void Arrow(Overlay3D overlay, Vector3 centre, Vector3 direction, float length, uint colour)
        {
            Vector3 tip = centre + direction * length;
            Vector3 side = new Vector3(-direction.Y, direction.X, 0f) * 0.09f;
            Vector3 back = tip - direction * 0.18f;
            overlay.Line(centre + direction * 0.1f, back, 3f, colour);
            overlay.Triangle(tip, back + side, back - side, colour);
        }

        /// <summary>
        /// Writes "Δ 1.250 m · Z +0.100 m · 15.0°" for the panel (the Z part only when raised or lowered).
        /// </summary>
        public void DescribeDelta(TextBuffer text)
        {
            float degrees = DeltaAngle * 180f / MathF.PI;
            Vector3 delta = DeltaOffset;
            text.Append("Δ ").Append(new Vector2(delta.X, delta.Y).Length(), 3).Append(" m");
            if (MathF.Abs(delta.Z) > 5e-4f) { text.Append(" · Z ").Append(delta.Z, 3, plusSign: true).Append(" m"); }
            text.Append(" · ").Append(degrees, 1).Append('°');
        }

        #endregion
    }
}
