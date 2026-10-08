using System.Numerics;
using BimGo.Audio;
using BimGo.Edits;
using BimGo.Physics;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// Gun 7: move / rotate loadable family instances (FFE). LMB on an eligible element locks the gizmo on in move
    /// mode: WASD move it relative to the view, E / Q raise / lower it. R switches to rotate mode: A / D turn it CCW /
    /// CW on the XY plane. Shift for fine control. G toggles snapping to fixed increments (Ctrl inverts while held);
    /// Z / X step the current mode's increment. RMB commits (the same move and rotation are applied in Revit, or
    /// recorded in the file); Esc cancels.
    /// </summary>
    internal sealed class GizmoGun : Gun
    {
        private readonly GizmoController _gizmo;
        private int _hover = -1, _hoverDynamic;

        public GizmoGun(GameSession session) : base(session)
        {
            _gizmo = new GizmoController(session);
        }

        public override string Name => "GIZMO";
        public override string HintPrimary => _gizmo.Active ? "Esc cancel" : "Lock on (FFE)";
        public override string HintSecondary => _gizmo.Active ? (Session.EditsGoToRevit ? "Commit to Revit" : "Commit") : "—";
        public override uint Colour => UiTheme.GIZMO;
        public override float PanelHeight => 140f;
        public override bool CapturesInput => _gizmo.Active;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Gizmo(ui, cx, cy, size, colour);

        public override void ClearMarkers() { }

        public override void OnKeys(Platform.InputState input) => GizmoPanel.HandleKeys(Session, _gizmo, input);

        public override void OnDeselect()
        {
            _hover = -1;
            _hoverDynamic = 0;
        }

        #region Input

        public override void Update(float dt, in AimInfo aim)
        {
            if (_gizmo.Active)
            {
                _gizmo.Update(dt, Session.Input);
                return;
            }
            _hover = aim.HasHit ? aim.Hit.Element : -1;
            _hoverDynamic = aim.HasHit ? aim.Hit.DynamicId : 0;
        }

        public override void OnPrimary(in AimInfo aim)
        {
            if (_gizmo.Active) { return; }
            if (!aim.HasHit)
            {
                Session.Sound.Play(SoundId.Error);
                return;
            }

            ElementRecord record = Session.Scene.Elements[aim.Hit.Element];
            if (!record.Movable)
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"Can't move {record.Name}: {record.MoveBlockReason}", important: true);
                return;
            }

            DynamicInstance instance = aim.Hit.DynamicId > 0 ? Session.Dynamics.Find(aim.Hit.DynamicId) : Session.MakeDynamic(aim.Hit.Element);
            if (instance == null) { return; }

            _gizmo.Begin(instance);
            Session.Sound.Play(SoundId.Grab);
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_gizmo.Active) { Commit(); }
        }

        public override void OnCancel()
        {
            DynamicInstance released = _gizmo.Cancel();
            Session.RestoreIfUnmoved(released);
            Session.Sound.Play(SoundId.UiClick);
            Session.Toast("Move cancelled");
        }

        #endregion

        #region Commit

        /// <summary>
        /// Keeps the new transform and applies the same change through the model source.
        /// </summary>
        private void Commit()
        {
            DynamicInstance instance = _gizmo.Target;
            if (!_gizmo.HasChanges)
            {
                _gizmo.End();
                Session.RestoreIfUnmoved(instance);
                Session.Toast("Nothing moved");
                return;
            }

            Vector3 delta = _gizmo.DeltaOffset;
            float angle = _gizmo.DeltaAngle;
            Vector3 startPivot = _gizmo.StartPivot;
            ElementRecord record = Session.Scene.Elements[instance.Element];
            _gizmo.End();

            Session.Sound.Play(SoundId.Commit);
            var request = new EditRequest
            {
                Op = EditOp.Transform,
                ElementId = instance.RevitId,
                TargetCloneKey = instance.RevitId <= 0 ? instance.CloneKey : 0,
                Pivot = Session.ToRevit(startPivot),
                Translation = delta,
                Angle = angle,
                Label = "Move " + record.Name
            };

            bool sent = Session.SubmitEdit(request, result =>
            {
                if (result.Success)
                {
                    Session.Toast(Session.EditsGoToRevit ? $"Moved in Revit: {record.Name}" : $"Moved: {record.Name}");
                    return;
                }

                // Refused: undo this move in the game (later moves, if any, stay relative)
                Session.Dynamics.SetTransform(instance, instance.Offset - delta, instance.Angle - angle);
                Session.RestoreIfUnmoved(instance);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{Session.EditTargetName} refused the move ({result.Message}). Restored.", 4f, important: true);
            });

            if (!sent) { Session.Toast($"{record.Name} moved in the walkthrough only (not connected to Revit)"); }
        }

        #endregion

        #region Drawing

        public override void CollectHighlights(List<Highlight> highlights)
        {
            if (_gizmo.Active)
            {
                highlights.Add(new Highlight(_gizmo.Target.Element, _gizmo.Target.Id, UiTheme.GIZMO, 0.3f));
                return;
            }
            if (_hover < 0) { return; }
            bool movable = Session.Scene.Elements[_hover].Movable;
            highlights.Add(new Highlight(_hover, _hoverDynamic, movable ? UiTheme.GIZMO : UiTheme.TEXT_FAINT, movable ? 0.25f : 0.12f));
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            if (selected) { _gizmo.Draw(overlay, UiTheme.GIZMO); }
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width) =>
            GizmoPanel.Draw(ui, Session, _gizmo, "GIZMO", UiTheme.GIZMO_LABEL, _hover, x, y, width, Session.UiScale);

        #endregion
    }

    /// <summary>
    /// The context panel shared by the Gizmo and Clone guns.
    /// </summary>
    internal static class GizmoPanel
    {
        /// <summary>
        /// Keys shared by the Gizmo and Clone guns: G toggles snapping (any time the gun is selected); while locked on,
        /// R switches move / rotate and Z / X step the current mode's increment (move distance or angle) down / up.
        /// </summary>
        public static void HandleKeys(GameSession session, GizmoController gizmo, Platform.InputState input)
        {
            if (input.IsPressed('G')) { session.ToggleGizmoSnap(); }
            if (!gizmo.Active) { return; }
            if (input.IsPressed('R'))
            {
                gizmo.ToggleMode();
                session.Sound.Play(Audio.SoundId.UiClick);
            }
            bool rotating = gizmo.Mode == GizmoMode.Rotate;
            if (input.IsPressed('Z')) { if (rotating) { session.StepSnapAngle(-1); } else { session.StepSnapMove(-1); } }
            if (input.IsPressed('X')) { if (rotating) { session.StepSnapAngle(+1); } else { session.StepSnapMove(+1); } }
        }

        /// <summary>
        /// "50 mm · 15°" into a text buffer (no allocation).
        /// </summary>
        private static TextBuffer AppendSnap(TextBuffer text, GameSession session)
        {
            if (session.SnapMoveMm >= 1000f) { text.Append(session.SnapMoveMm / 1000f, 2).Append(" m"); }
            else { text.Append(session.SnapMoveMm, 0).Append(" mm"); }
            return text.Append(" · ").Append(session.SnapAngleDeg, 0).Append('°');
        }

        /// <summary>
        /// Draws the panel: locked state and controls, or the hovered element's eligibility.
        /// </summary>
        public static void Draw(UiBatch ui, GameSession session, GizmoController gizmo, string title, uint titleColour, int hover,
            float x, float y, float width, float scale)
        {
            float s(float value) => value * scale;
            FontAtlas f = ui.Atlas;
            float used = ui.Text(f.Small, x, y, title, titleColour, s(1.1f));
            if (gizmo.Active) { ui.Text(f.Small, x + used, y, gizmo.Mode == GizmoMode.Rotate ? " · ROTATE" : " · MOVE", titleColour, s(1.1f)); }
            y += s(20);

            if (gizmo.Active)
            {
                ElementRecord record = session.Scene.Elements[gizmo.Target.Element];
                ui.TextWrapped(f.Bold, x, y, width, record.Name, UiTheme.TEXT, maxLines: 1);
                y += s(22);
                TextBuffer delta = session.Text.Clear();
                gizmo.DescribeDelta(delta);
                ui.Text(f.Mono, x, y, delta.Span, UiTheme.TEXT);
                y += s(20);
                bool rotating = gizmo.Mode == GizmoMode.Rotate;
                ui.Text(f.Body, x, y, rotating ? "A/D rotate · R move" : "WASD · E/Q up/down · R rotate", UiTheme.TEXT_SOFT);
                y += s(18);
                if (gizmo.IsSnapping(session.Input))
                {
                    TextBuffer snap = AppendSnap(session.Text.Clear().Append("SNAP "), session).Append(session.GizmoSnap ? " · G off" : " · Ctrl");
                    ui.Text(f.Body, x, y, snap.Span, UiTheme.ACCENT);
                }
                else
                {
                    ui.Text(f.Body, x, y, "Shift fine · G snap on/off · Ctrl flips snap", UiTheme.TEXT_MUTED);
                }
                y += s(18);
                ui.Text(f.Body, x, y, rotating ? "Z/X angle step" : "Z/X move step", UiTheme.TEXT_MUTED);
                y += s(18);
                ui.Text(f.Body, x, y, "RMB commit · Esc cancel", UiTheme.TEXT_MUTED);
                return;
            }

            // Snap state (G toggles)
            TextBuffer state = session.GizmoSnap
                ? AppendSnap(session.Text.Clear().Append("Snap on · "), session).Append(" (G)")
                : session.Text.Clear().Append("Snap off (G)");
            ui.TextRight(f.Small, x + width, y - s(20), state.Span, session.GizmoSnap ? UiTheme.ACCENT : UiTheme.TEXT_FAINT, s(0.5f));

            if (hover < 0)
            {
                ui.Text(f.Body, x, y, "Aim at furniture or fittings.", UiTheme.TEXT_MUTED);
                y += s(19);
                ui.Text(f.Body, x, y, "Point-based loadable families only.", UiTheme.TEXT_MUTED);
                return;
            }

            ElementRecord hovered = session.Scene.Elements[hover];
            ui.TextWrapped(f.Bold, x, y, width, hovered.Name, UiTheme.TEXT, maxLines: 1);
            y += s(22);
            ui.TextWrapped(f.Body, x, y, width, hovered.FamilyType, UiTheme.TEXT_SOFT, maxLines: 1);
            y += s(20);
            if (hovered.Movable) { ui.Text(f.Body, x, y, "Movable: LMB locks on", UiTheme.GOOD); }
            else
            {
                TextBuffer reason = session.Text.Clear().Append("Can't move: ").Append(hovered.MoveBlockReason);
                ui.TextWrapped(f.Body, x, y, width, reason.Span, UiTheme.DANGER, maxLines: 1);
            }
            y += s(20);
            if (session.EditsLocalOnly) { ui.Text(f.Body, x, y, "Not connected to Revit: walkthrough only", UiTheme.DANGER); }
        }
    }
}
