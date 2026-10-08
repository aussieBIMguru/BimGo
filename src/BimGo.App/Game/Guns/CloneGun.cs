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
    /// Gun 8: copy a loadable family instance in place and go straight into the gizmo (move mode) on the copy.
    /// RMB commits (Revit copies the element with the same move and rotation, or the file records it); Esc discards.
    /// </summary>
    internal sealed class CloneGun : Gun
    {
        private readonly GizmoController _gizmo;
        private int _hover = -1, _hoverDynamic;

        // The element the current clone was made from (for the Revit copy request)
        private long _sourceRevitId;
        private int _sourceCloneKey;

        public CloneGun(GameSession session) : base(session)
        {
            _gizmo = new GizmoController(session);
        }

        public override string Name => "CLONE";
        public override string HintPrimary => _gizmo.Active ? "Esc discard" : "Clone (FFE)";
        public override string HintSecondary => _gizmo.Active ? (Session.EditsGoToRevit ? "Commit to Revit" : "Commit") : "—";
        public override uint Colour => UiTheme.CLONE;
        public override float PanelHeight => 140f;
        public override bool CapturesInput => _gizmo.Active;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Clone(ui, cx, cy, size, colour);

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
                Session.Toast($"Can't clone {record.Name}: {record.MoveBlockReason}", important: true);
                return;
            }

            // Clone from whatever was hit: the static element, a moved original or another clone
            DynamicInstance source = aim.Hit.DynamicId > 0 ? Session.Dynamics.Find(aim.Hit.DynamicId) : null;
            if (aim.Hit.DynamicId > 0 && source == null) { return; }

            _sourceRevitId = source?.RevitId ?? record.ElementId;
            _sourceCloneKey = source != null && source.RevitId <= 0 ? source.CloneKey : 0;

            DynamicInstance clone = Session.CreateClone(aim.Hit.Element, source);
            _gizmo.Begin(clone);
            Session.Sound.Play(SoundId.Grab);
            Session.Toast("Clone made: move it, then RMB commits it (Esc discards it)");
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_gizmo.Active) { Commit(); }
        }

        public override void OnCancel()
        {
            DynamicInstance clone = _gizmo.End();
            if (clone != null) { Session.Dynamics.Remove(clone); }
            Session.Sound.Play(SoundId.Remove);
            Session.Toast("Clone discarded");
        }

        #endregion

        #region Commit

        /// <summary>
        /// Keeps the clone and asks the model source to create the same copy.
        /// </summary>
        private void Commit()
        {
            Vector3 delta = _gizmo.DeltaOffset;
            float angle = _gizmo.DeltaAngle;
            Vector3 startPivot = _gizmo.StartPivot;
            DynamicInstance clone = _gizmo.End();
            clone.Committed = true;
            ElementRecord record = Session.Scene.Elements[clone.Element];

            Session.Sound.Play(SoundId.Commit);
            var request = new EditRequest
            {
                Op = EditOp.Copy,
                ElementId = _sourceRevitId,
                TargetCloneKey = _sourceCloneKey,
                NewCloneKey = clone.CloneKey,
                Pivot = Session.ToRevit(startPivot),
                Translation = delta,
                Angle = angle,
                Label = "Clone " + record.Name
            };

            bool sent = Session.SubmitEdit(request, result =>
            {
                if (result.Success)
                {
                    if (result.NewElementId > 0)
                    {
                        clone.RevitId = result.NewElementId;
                        Session.Toast($"Created in Revit: {record.Name} (id {result.NewElementId})");
                    }
                    else
                    {
                        Session.Toast($"Cloned: {record.Name}");
                    }
                    return;
                }

                Session.Dynamics.Remove(clone);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{Session.EditTargetName} refused the copy ({result.Message}). Clone removed.", 4f, important: true);
            });

            if (!sent) { Session.Toast($"Clone of {record.Name} kept in the walkthrough only (not connected to Revit)"); }
        }

        #endregion

        #region Drawing

        public override void CollectHighlights(List<Highlight> highlights)
        {
            if (_gizmo.Active)
            {
                highlights.Add(new Highlight(_gizmo.Target.Element, _gizmo.Target.Id, UiTheme.CLONE, 0.32f));
                return;
            }
            if (_hover < 0) { return; }
            bool movable = Session.Scene.Elements[_hover].Movable;
            highlights.Add(new Highlight(_hover, _hoverDynamic, movable ? UiTheme.CLONE : UiTheme.TEXT_FAINT, movable ? 0.25f : 0.12f));
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            if (selected) { _gizmo.Draw(overlay, UiTheme.CLONE); }
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width) =>
            GizmoPanel.Draw(ui, Session, _gizmo, "CLONE", UiTheme.CLONE_LABEL, _hover, x, y, width, Session.UiScale);

        #endregion
    }
}
