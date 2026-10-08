using System.Numerics;
using BimGo.Audio;
using BimGo.Edits;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// Gun 6: the demolition hammer. LMB primes the element under the crosshair; LMB on a primed element
    /// demolishes it in the game and in the model source (Revit, or the file's journal). By default it sets
    /// Phase Demolished to the session's "new" phase, which only existing elements (there in the "existing" phase)
    /// accept; new work, clones and unphased elements can only be deleted (T toggles to deleting).
    /// RMB un-primes, X clears all primes.
    ///
    /// Removal is optimistic: the element vanishes at once and comes back if Revit refuses.
    /// In a standalone file both modes hide the element and record the mode for a later push to Revit.
    /// </summary>
    internal sealed class HammerGun : Gun
    {
        #region Types and fields

        /// <summary>A primed target.</summary>
        private readonly record struct Target(int Element, int DynamicId);

        private readonly List<Target> _primed = new();
        private Target _hover = new(-1, 0);
        private bool _deleteMode;
        private float _clock;

        // Cached "can't demolish" reasons (built on first use)
        private string _blockedNew;
        private string _blockedBetween;

        #endregion

        public HammerGun(GameSession session) : base(session) { }

        public override string Name => "DEMOLISH";
        public override string HintPrimary => IsPrimed(_hover) ? (_deleteMode ? "Delete" : "Demolish") : "Prime";
        public override string HintSecondary => "Unprime";
        public override uint Colour => UiTheme.HAMMER;
        public override float PanelHeight => 118f;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Hammer(ui, cx, cy, size, colour);

        public override void Tick(float dt) => _clock += dt;

        public override void OnDeselect() => _hover = new Target(-1, 0);

        public override void ClearMarkers()
        {
            if (_primed.Count > 0) { Session.Toast("Primed elements cleared"); }
            _primed.Clear();
        }

        #region Input

        public override void Update(float dt, in AimInfo aim)
        {
            _hover = aim.HasHit ? new Target(aim.Hit.Element, aim.Hit.DynamicId) : new Target(-1, 0);

            // Forget primes whose element has gone (demolished elsewhere, cloned-and-cancelled…)
            for (int i = _primed.Count - 1; i >= 0; i--)
            {
                if (!Session.IsTargetPresent(_primed[i].Element, _primed[i].DynamicId)) { _primed.RemoveAt(i); }
            }
        }

        public override void OnKeys(InputState input)
        {
            if (input.IsPressed('T'))
            {
                _deleteMode = !_deleteMode;
                Session.Sound.Play(SoundId.UiClick);
                if (Session.EditsGoToRevit)
                {
                    Session.Toast(_deleteMode ? "Demolish gun: DELETE mode (removes elements from the Revit model)" : $"Demolish gun: demolish in phase {PhaseLabel}");
                }
                else
                {
                    Session.Toast(_deleteMode ? "Demolish gun: DELETE mode (recorded in the file)" : $"Demolish gun: demolish in phase {PhaseLabel} (recorded in the file)");
                }
            }
        }

        public override void OnPrimary(in AimInfo aim)
        {
            if (!aim.HasHit)
            {
                Session.Sound.Play(SoundId.Error);
                return;
            }

            var target = new Target(aim.Hit.Element, aim.Hit.DynamicId);

            // Linked models are read-only (demolish or delete them in their own model)
            ElementRecord hit = Session.Scene.Elements[target.Element];
            if (hit.IsLinked)
            {
                _primed.Remove(target);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{hit.MoveBlockReason ?? "In a linked model"}: edit it in its own model.", 3.5f, important: true);
                return;
            }

            // Demolition is for existing elements only: say so up front rather than after the round trip
            string blocked = _deleteMode ? null : DemolishBlockReason(target);
            if (blocked != null)
            {
                _primed.Remove(target);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{blocked}, so it can't be demolished. Press T to delete it instead.", 3.5f, important: true);
                return;
            }

            if (IsPrimed(target))
            {
                _primed.Remove(target);
                Demolish(target);
                return;
            }

            _primed.Add(target);
            Session.Sound.Play(SoundId.Prime);
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_primed.Count == 0) { return; }

            // The primed element under the crosshair, else the most recent
            int index = _primed.IndexOf(_hover);
            _primed.RemoveAt(index >= 0 ? index : _primed.Count - 1);
            Session.Sound.Play(SoundId.UiClick);
        }

        private bool IsPrimed(Target target) => target.Element >= 0 && _primed.Contains(target);

        private string PhaseLabel => Session.Scene.PhaseName ?? "no phase";

        /// <summary>
        /// Why a target can't be demolished by phase (it can still be deleted), or null if it can.
        /// The strings are built once (this runs every frame for the panel).
        /// </summary>
        private string DemolishBlockReason(Target target)
        {
            if (target.Element < 0) { return null; }
            if (Session.Scene.PhaseName == null) { return "The model has no phases"; }

            DynamicInstance instance = target.DynamicId > 0 ? Session.Dynamics.Find(target.DynamicId) : null;
            if (instance != null && instance.IsClone) { return "Clones are new work"; }

            switch (Session.Scene.Elements[target.Element].Phase)
            {
                case PhaseRole.New:
                    return _blockedNew ??= $"New work in {PhaseLabel}";
                case PhaseRole.Between:
                    return _blockedBetween ??= Session.Scene.ExistingPhaseName == null ? "Not in the existing phase" : $"Built after {Session.Scene.ExistingPhaseName}";
                case PhaseRole.Unphased:
                    return "It has no phase";
                default:
                    return null;
            }
        }

        #endregion

        #region Demolition

        /// <summary>
        /// Removes the target in the game and asks Revit to demolish / delete it.
        /// </summary>
        private void Demolish(Target target)
        {
            ElementRecord record = Session.Scene.Elements[target.Element];
            DynamicInstance instance = Session.Dynamics.Find(target.DynamicId);
            if (target.DynamicId > 0 && instance == null) { return; }

            // Hide now (optimistic)
            if (instance != null) { instance.Hidden = true; }
            else { Session.SetStaticHidden(target.Element, true); }
            Session.Sound.Play(SoundId.Demolish);
            Session.Flash(UiTheme.HAMMER, 0.1f);

            bool deleting = _deleteMode;
            var request = new EditRequest
            {
                Op = deleting ? EditOp.Delete : EditOp.PhaseDemolish,
                ElementId = instance?.RevitId ?? record.ElementId,
                TargetCloneKey = instance != null && instance.RevitId <= 0 ? instance.CloneKey : 0,
                Label = (deleting ? "Delete " : "Demolish ") + record.Name
            };

            bool sent = Session.SubmitEdit(request, result => OnEditResult(result, target, instance, record));
            if (!sent) { Session.Toast($"{record.Name} removed in the walkthrough only (not connected to Revit)"); }
        }

        /// <summary>
        /// The source's answer: hide anything else that went with it, or put the element back.
        /// </summary>
        private void OnEditResult(EditResult result, Target target, DynamicInstance instance, ElementRecord record)
        {
            if (result.Success)
            {
                int extra = Math.Max(0, Session.ApplyRemovals(result.AffectedIds));
                string verb = result.Op == EditOp.Delete ? "Deleted" : $"Demolished ({PhaseLabel})";
                string where = Session.EditsGoToRevit ? " in Revit" : string.Empty;
                Session.Toast(extra > 0 ? $"{verb}{where}: {record.Name} + {extra} dependent element{(extra == 1 ? string.Empty : "s")}" : $"{verb}{where}: {record.Name}");
                return;
            }

            // Refused: restore
            if (instance != null) { instance.Hidden = false; }
            else { Session.SetStaticHidden(target.Element, false); }
            Session.Sound.Play(SoundId.Error);
            Session.Toast($"{Session.EditTargetName} refused ({result.Message}). {record.Name} restored.", 4f, important: true);
        }

        #endregion

        #region Drawing

        public override void CollectHighlights(List<Highlight> highlights)
        {
            float pulse = 0.38f + 0.18f * MathF.Sin(_clock * 7f);
            foreach (Target target in _primed)
            {
                highlights.Add(new Highlight(target.Element, target.DynamicId, UiTheme.HAMMER_PRIMED, pulse));
            }
            if (_hover.Element >= 0 && !IsPrimed(_hover) && !Session.Scene.Elements[_hover.Element].IsLinked)
            {
                highlights.Add(new Highlight(_hover.Element, _hover.DynamicId, UiTheme.HAMMER, 0.22f));
            }
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            // Primed elements keep a red box even when another gun is selected
            uint colour = Rgba.WithAlpha(UiTheme.HAMMER_PRIMED, selected ? 0.95f : 0.45f);
            foreach (Target target in _primed)
            {
                DynamicInstance instance = Session.Dynamics.Find(target.DynamicId);
                Aabb box = instance?.WorldBounds ?? Session.Scene.Elements[target.Element].Bounds;
                DrawBox(overlay, box, colour);
            }
        }

        /// <summary>
        /// A wireframe box, slightly inflated.
        /// </summary>
        private static void DrawBox(Overlay3D overlay, Aabb box, uint colour)
        {
            Vector3 n = box.Min - new Vector3(0.02f), x = box.Max + new Vector3(0.02f);
            Vector3 c000 = new(n.X, n.Y, n.Z), c100 = new(x.X, n.Y, n.Z), c110 = new(x.X, x.Y, n.Z), c010 = new(n.X, x.Y, n.Z);
            Vector3 c001 = new(n.X, n.Y, x.Z), c101 = new(x.X, n.Y, x.Z), c111 = new(x.X, x.Y, x.Z), c011 = new(n.X, x.Y, x.Z);
            overlay.Line(c000, c100, 2f, colour); overlay.Line(c100, c110, 2f, colour); overlay.Line(c110, c010, 2f, colour); overlay.Line(c010, c000, 2f, colour);
            overlay.Line(c001, c101, 2f, colour); overlay.Line(c101, c111, 2f, colour); overlay.Line(c111, c011, 2f, colour); overlay.Line(c011, c001, 2f, colour);
            overlay.Line(c000, c001, 2f, colour); overlay.Line(c100, c101, 2f, colour); overlay.Line(c110, c111, 2f, colour); overlay.Line(c010, c011, 2f, colour);
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width)
        {
            FontAtlas f = ui.Atlas;
            ui.Text(f.Small, x, y, "DEMOLISH", UiTheme.HAMMER_LABEL, S(1.1f));
            ui.TextRight(f.Small, x + width, y, "T  MODE", UiTheme.TEXT_MUTED, S(1f));
            y += S(20);

            if (_deleteMode)
            {
                ui.Text(f.Bold, x, y, Session.EditsGoToRevit ? "Delete from the Revit model" : "Delete (recorded in the file)", UiTheme.DANGER);
            }
            else
            {
                TextBuffer mode = Session.Text.Clear().Append("Demolish · ");
                if (Session.Scene.ExistingPhaseName != null) { mode.Append(Session.Scene.ExistingPhaseName).Append(" → "); }
                mode.Append(PhaseLabel);
                ui.TextWrapped(f.Bold, x, y, width, mode.Span, UiTheme.TEXT, maxLines: 1);
            }
            y += S(22);

            if (Session.EditsLocalOnly)
            {
                ui.Text(f.Body, x, y, "Not connected to Revit: walkthrough only", UiTheme.DANGER);
                y += S(19);
            }

            TextBuffer primed = Session.Text.Clear().Append("Primed: ").Append(_primed.Count);
            ui.Text(f.Body, x, y, primed.Span, _primed.Count > 0 ? UiTheme.HAMMER_PRIMED : UiTheme.TEXT_MUTED);
            y += S(19);

            if (_hover.Element >= 0)
            {
                ElementRecord record = Session.Scene.Elements[_hover.Element];
                string blocked = _deleteMode ? null : DemolishBlockReason(_hover);
                if (record.IsLinked)
                {
                    ui.TextWrapped(f.Body, x, y, width, record.MoveBlockReason ?? "In a linked model (read-only)", UiTheme.DANGER, maxLines: 1);
                }
                else if (blocked != null)
                {
                    TextBuffer line = Session.Text.Clear().Append(blocked).Append(": press T to delete instead");
                    ui.TextWrapped(f.Body, x, y, width, line.Span, UiTheme.DANGER, maxLines: 1);
                }
                else
                {
                    ui.TextWrapped(f.Body, x, y, width, record.Name, UiTheme.TEXT_SOFT, maxLines: 1);
                }
            }
            else
            {
                ui.Text(f.Body, x, y, "Aim at an element. LMB primes it, LMB again removes it.", UiTheme.TEXT_MUTED);
            }
        }

        #endregion
    }
}
