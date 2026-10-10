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
    /// Gun 9 (next round): places new instances of loadable family types from the family library.
    /// <list type="bullet">
    /// <item>Not holding anything: LMB opens the library (pause menu panel); RMB places the last picked type again.</item>
    /// <item>A picked type appears in front of the player, resting on the surface below, in the gizmo's move mode
    /// (the Clone gun's controls: WASD · E / Q · R rotate · G snap · Z / X steps · F drop to surface).</item>
    /// <item>RMB commits: Revit places the type (live session, one undo step) or the file's journal records a
    /// <c>place</c>; Esc discards.</item>
    /// </list>
    /// A placement is a clone of the type's hidden template, so once committed every other gun treats it like a
    /// clone (Gizmo moves it, Clone copies it, Demolish deletes it).
    /// </summary>
    internal sealed class PlaceGun : Gun
    {
        /// <summary>How far in front of the player a new placement appears (m, plus half its plan size).</summary>
        private const float PLACE_DISTANCE = 1.6f;

        private readonly GizmoController _gizmo;
        private LibraryEntry _entry, _last;

        public PlaceGun(GameSession session) : base(session)
        {
            _gizmo = new GizmoController(session);
        }

        public override string Name => "PLACE";
        public override string HintPrimary => _gizmo.Active ? "Esc discard" : "Family library";
        public override string HintSecondary => _gizmo.Active ? (Session.EditsGoToRevit ? "Commit to Revit" : "Commit") : _last != null ? "Place again" : "—";
        public override uint Colour => UiTheme.PLACE;
        public override float PanelHeight => 140f;
        public override bool CapturesInput => _gizmo.Active;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Place(ui, cx, cy, size, colour);

        public override void ClearMarkers() { }

        public override void OnKeys(Platform.InputState input) => GizmoPanel.HandleKeys(Session, _gizmo, input);

        #region Input

        public override void Update(float dt, in AimInfo aim)
        {
            if (_gizmo.Active) { _gizmo.Update(dt, Session.Input); }
        }

        public override void OnPrimary(in AimInfo aim)
        {
            if (_gizmo.Active) { return; }
            Session.OpenLibrary();
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_gizmo.Active)
            {
                Commit();
                return;
            }
            if (_last != null) { Begin(_last); }
            else
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast("Pick a family first: LMB opens the family library");
            }
        }

        public override void OnCancel()
        {
            DynamicInstance placed = _gizmo.End();
            if (placed != null) { Session.Dynamics.Remove(placed); }
            _entry = null;
            Session.Sound.Play(SoundId.Remove);
            Session.Toast("Placement discarded");
        }

        #endregion

        #region Placing

        /// <summary>
        /// Makes a new instance of a library type in front of the player, drops it onto the surface below and locks
        /// the gizmo on in move mode. Discards a placement still being held.
        /// </summary>
        public void Begin(LibraryEntry entry)
        {
            if (entry == null) { return; }
            if (!entry.Placeable || entry.Element < 0)
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{entry.Label}: {entry.Reason ?? "not placeable"}", important: true);
                return;
            }
            if (_gizmo.Active) { OnCancel(); }

            DynamicInstance placed = Session.CreatePlacement(entry);
            if (placed == null)
            {
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{entry.Label} has no geometry in this snapshot", important: true);
                return;
            }

            // In front of the player at foot level, far enough that its footprint clears the player
            Aabb template = Session.Scene.Elements[entry.Element].Bounds;
            float reach = PLACE_DISTANCE + 0.5f * MathF.Max(template.Size.X, template.Size.Y);
            Vector3 forward = Session.Camera.Forward;
            var flat = new Vector3(forward.X, forward.Y, 0f);
            flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitX;
            Vector3 feet = Session.Player.Feet;
            Vector3 pivotToBase = placed.BasePivot - template.Min; // the template's pivot relative to its box bottom
            var target = new Vector3(feet.X + flat.X * reach, feet.Y + flat.Y * reach, feet.Z + MathF.Max(0f, pivotToBase.Z));
            Session.SetPlacement(placed, target, 0f);

            _entry = entry;
            _last = entry;
            _gizmo.Begin(placed);
            _gizmo.DropToSurface(out _); // quietly onto the floor (or desk) in front; F does it again after moving
            Session.Sound.Play(SoundId.Grab);
            Session.Toast($"{entry.Label}: move it, then RMB commits it (Esc discards it)");
        }

        /// <summary>
        /// Keeps the placement and asks the model source to create it: Revit places the type at the instance's
        /// pivot, turned by its angle; a file records a <c>place</c> entry.
        /// </summary>
        private void Commit()
        {
            LibraryEntry entry = _entry;
            DynamicInstance placed = _gizmo.End();
            _entry = null;
            if (placed == null || entry == null) { return; }
            placed.Committed = true;

            Session.Sound.Play(SoundId.Commit);
            var request = new EditRequest
            {
                Op = EditOp.Place,
                NewCloneKey = placed.CloneKey,
                TypeUniqueId = entry.TypeUniqueId,
                TypeId = entry.TypeId,
                Pivot = Session.ToRevit(placed.Pivot),
                Angle = placed.Angle,
                Label = "Place " + entry.Label
            };

            bool sent = Session.SubmitEdit(request, result =>
            {
                if (result.Success)
                {
                    if (result.NewElementId > 0)
                    {
                        placed.RevitId = result.NewElementId;
                        Session.Toast($"Placed in Revit: {entry.Label} (id {result.NewElementId})");
                    }
                    else
                    {
                        Session.Toast($"Placed: {entry.Label}");
                    }
                    return;
                }

                Session.Dynamics.Remove(placed);
                Session.Sound.Play(SoundId.Error);
                Session.Toast($"{Session.EditTargetName} refused the placement ({result.Message}). Removed.", 4f, important: true);
            });

            if (!sent) { Session.Toast($"{entry.Label} kept in the walkthrough only (not connected to Revit)"); }
        }

        #endregion

        #region Drawing

        public override void CollectHighlights(List<Highlight> highlights)
        {
            if (_gizmo.Active) { highlights.Add(new Highlight(_gizmo.Target.Element, _gizmo.Target.Id, UiTheme.PLACE, 0.3f)); }
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            if (selected) { _gizmo.Draw(overlay, UiTheme.PLACE); }
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width)
        {
            if (_gizmo.Active)
            {
                GizmoPanel.Draw(ui, Session, _gizmo, "PLACE", UiTheme.PLACE_LABEL, -1, x, y, width, Session.UiScale);
                return;
            }

            FontAtlas f = ui.Atlas;
            float s(float value) => value * Session.UiScale;
            ui.Text(f.Small, x, y, "PLACE", UiTheme.PLACE_LABEL, s(1.1f));
            y += s(20);
            if (!Session.HasLibrary)
            {
                ui.TextWrapped(f.Body, x, y, width, "No family library in this snapshot.", UiTheme.TEXT_MUTED, maxLines: 1);
                y += s(19);
                ui.TextWrapped(f.Body, x, y, width, "Revit: Options → Load → Family library, then Go.", UiTheme.TEXT_MUTED, maxLines: 2);
                return;
            }

            TextBuffer count = Session.Text.Clear().AppendGrouped(Session.Library.PlaceableCount).Append(" family types to place");
            ui.Text(f.Body, x, y, count.Span, UiTheme.TEXT);
            y += s(22);
            ui.Text(f.Body, x, y, "LMB opens the family library", UiTheme.TEXT_SOFT);
            y += s(20);
            if (_last != null)
            {
                ui.TextWrapped(f.Body, x, y, width, _last.Label, UiTheme.PLACE_LABEL, maxLines: 1);
                y += s(19);
                ui.Text(f.Body, x, y, "RMB places it again", UiTheme.TEXT_MUTED);
                y += s(19);
            }
            if (Session.EditsLocalOnly) { ui.Text(f.Body, x, y, "Not connected to Revit: walkthrough only", UiTheme.DANGER); }
        }

        #endregion
    }
}
