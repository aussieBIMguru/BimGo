using System.Numerics;
using BimGo.Audio;
using BimGo.Physics;
using BimGo.Rendering;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// Gun 5: point-and-blink movement, like VR teleport tools. A landing marker previews where you will stand
    /// (green when there is room for the capsule, red when blocked). LMB blinks there; RMB steps back through
    /// the last few positions.
    /// </summary>
    internal sealed class TeleportGun : Gun
    {
        #region Constants and fields

        private const float MAX_RANGE = 80f;
        private const float DROP_DISTANCE = 4f;
        private const int HISTORY = 12;

        private bool _hasTarget;
        private bool _valid;
        private Vector3 _feet;
        private string _reason;
        private float _distance;
        private float _clock;

        private readonly Vector3[] _history = new Vector3[HISTORY];
        private int _historyCount;

        #endregion

        public TeleportGun(GameSession session) : base(session) { }

        public override string Name => "TELEPORT";
        public override string HintPrimary => "Blink to marker";
        public override string HintSecondary => _historyCount > 0 ? "Back" : "Back (none)";
        public override uint Colour => UiTheme.TELEPORT;
        public override float PanelHeight => 80f;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Teleport(ui, cx, cy, size, colour);

        public override void Tick(float dt) => _clock += dt;

        public override void OnDeselect() => _hasTarget = false;

        public override void ClearMarkers() => _historyCount = 0;

        #region Targeting

        public override void Update(float dt, in AimInfo aim)
        {
            _hasTarget = false;
            _valid = false;
            _reason = null;

            if (!aim.HasHit || aim.Hit.Distance > MAX_RANGE)
            {
                _reason = aim.HasHit ? "Too far (80 m max)" : "Aim at a surface";
                return;
            }

            _hasTarget = true;
            _distance = aim.Hit.Distance;
            bool flying = Session.Player.Flying;
            RayHit hit = aim.Hit;
            Vector3 feet;

            if (hit.Normal.Z > 0.7f)
            {
                // A floor: stand on it
                feet = hit.Point + new Vector3(0f, 0f, 0.02f);
            }
            else if (hit.Normal.Z < -0.7f)
            {
                // A ceiling: only while flying (hang just below it)
                if (!flying)
                {
                    _feet = hit.Point;
                    _reason = "Can't stand on a ceiling";
                    return;
                }
                feet = hit.Point - new Vector3(0f, 0f, CharacterController.STAND_HEIGHT + 0.05f);
            }
            else
            {
                // A wall: step back from it, then find the floor below (flying: stay at aim height)
                var flat = new Vector3(hit.Normal.X, hit.Normal.Y, 0f);
                flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : -aim.Direction;
                Vector3 backed = hit.Point + flat * (CharacterController.RADIUS + 0.08f);

                if (flying)
                {
                    feet = backed - new Vector3(0f, 0f, CharacterController.STAND_EYE);
                }
                else if (Session.Pick(backed + new Vector3(0f, 0f, 0.3f), -Vector3.UnitZ, DROP_DISTANCE, out RayHit floor) && floor.Normal.Z > 0.7f)
                {
                    feet = floor.Point + new Vector3(0f, 0f, 0.02f);
                }
                else
                {
                    _feet = backed;
                    _reason = "No floor below that point";
                    return;
                }
            }

            // Room for the capsule? Try a few small lifts (thresholds, rugs) before giving up.
            for (int lift = 0; lift < 4; lift++)
            {
                Vector3 candidate = feet + new Vector3(0f, 0f, lift * 0.06f);
                if (!Session.Player.Controller.Overlaps(candidate + new Vector3(0f, 0f, 0.01f), CharacterController.STAND_HEIGHT))
                {
                    _feet = candidate;
                    _valid = true;
                    return;
                }
            }

            _feet = feet;
            _reason = "Not enough room to stand there";
        }

        #endregion

        #region Actions

        public override void OnPrimary(in AimInfo aim)
        {
            if (!_valid)
            {
                Session.Sound.Play(SoundId.Error);
                if (_reason != null) { Session.Toast(_reason, important: true); }
                return;
            }

            Push(Session.Player.Feet);
            Session.Player.TeleportTo(_feet);
            Session.Sound.Play(SoundId.Blink);
            Session.Flash(UiTheme.TELEPORT, 0.12f);
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_historyCount == 0)
            {
                Session.Sound.Play(SoundId.Error);
                return;
            }

            Vector3 back = _history[--_historyCount];
            Session.Player.TeleportTo(back);
            Session.Sound.Play(SoundId.Blink);
            Session.Flash(UiTheme.TELEPORT, 0.08f);
        }

        /// <summary>
        /// Remembers a position (oldest dropped when full).
        /// </summary>
        private void Push(Vector3 feet)
        {
            if (_historyCount == HISTORY)
            {
                Array.Copy(_history, 1, _history, 0, HISTORY - 1);
                _historyCount--;
            }
            _history[_historyCount++] = feet;
        }

        #endregion

        #region Drawing

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            if (!selected || !_hasTarget) { return; }

            uint colour = _valid ? UiTheme.TELEPORT : UiTheme.TELEPORT_BLOCKED;
            float pulse = 0.5f + 0.5f * MathF.Sin(_clock * 6f);
            Vector3 ground = _feet + new Vector3(0f, 0f, 0.015f);

            // Landing marker: disc, ring and a faint standing-height column
            overlay.Disc(ground, Vector3.UnitX, Vector3.UnitY, 0.38f, 0.38f, Rgba.WithAlpha(colour, 0.22f));
            overlay.Ring(ground, Vector3.UnitX, Vector3.UnitY, 0.42f + 0.04f * pulse, 0.42f + 0.04f * pulse, 0.035f, colour);
            if (_valid)
            {
                Vector3 top = _feet + new Vector3(0f, 0f, CharacterController.STAND_HEIGHT);
                overlay.Line(ground, top, 1.5f, Rgba.WithAlpha(colour, 0.45f));
                overlay.Ring(top, Vector3.UnitX, Vector3.UnitY, 0.18f, 0.18f, 0.02f, Rgba.WithAlpha(colour, 0.5f));
            }
            else
            {
                // A cross through the marker
                overlay.Line(ground + new Vector3(-0.3f, -0.3f, 0f), ground + new Vector3(0.3f, 0.3f, 0f), 3f, colour);
                overlay.Line(ground + new Vector3(-0.3f, 0.3f, 0f), ground + new Vector3(0.3f, -0.3f, 0f), 3f, colour);
            }

            // Arc from just below the eye to the marker
            Vector3 start = Session.Camera.Position + Session.Camera.Right * 0.18f - new Vector3(0f, 0f, 0.25f);
            Vector3 end = ground;
            float lift = MathF.Min(2.5f, 0.15f * Vector3.Distance(start, end) + 0.3f);
            Vector3 control = (start + end) * 0.5f + new Vector3(0f, 0f, lift);
            Vector3 previous = start;
            const int segments = 20;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                Vector3 next = (1 - t) * (1 - t) * start + 2 * (1 - t) * t * control + t * t * end;
                bool dash = ((int)(t * segments - _clock * 8f) & 1) == 0;
                overlay.Line(previous, next, 2.5f, Rgba.WithAlpha(colour, dash ? 0.9f : 0.45f));
                previous = next;
            }
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width)
        {
            FontAtlas f = ui.Atlas;
            ui.Text(f.Small, x, y, "TELEPORT", UiTheme.TELEPORT_LABEL, S(1.1f));
            y += S(20);

            if (_valid)
            {
                TextBuffer text = Session.Text.Clear().Append(_distance, 1).Append(" m · ").Append(Session.LevelNameAt(_feet.Z));
                ui.Text(f.Bold, x, y, text.Span, UiTheme.TEXT);
            }
            else
            {
                ui.Text(f.Body, x, y, _reason ?? "Aim at a floor or wall", UiTheme.DANGER);
            }
            y += S(24);

            TextBuffer history = Session.Text.Clear().Append("Back steps: ").Append(_historyCount).Append(" · X clears");
            ui.Text(f.Body, x, y, history.Span, UiTheme.TEXT_MUTED);
        }

        #endregion
    }
}
