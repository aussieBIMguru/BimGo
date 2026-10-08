using System.Numerics;
using BimGo.Format;
using BimGo.Audio;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// Gun 4: LMB places a marker and opens a text box; RMB removes the marker under the crosshair; E edits it.
    /// Hovering any marker shows its comment. Comments persist to the JSON sidecar (Revit sessions) or the file.
    /// The pause menu lists them all (filter by level, go to, edit, delete, export CSV).
    /// </summary>
    internal sealed class CommentGun : Gun
    {
        private const float HOVER_PIXELS = 34f;
        private const float MAX_HOVER_DISTANCE = 60f;

        private CommentRecord _hovered;
        private float _clearConfirmUntil;
        private float _clock;

        public CommentGun(GameSession session) : base(session) { }

        public override string Name => "COMMENT";
        public override string HintPrimary => "Place + type comment";
        public override string HintSecondary => "Remove marker";
        public override uint Colour => UiTheme.COMMENT;

        public override void DrawIcon(UiBatch ui, float cx, float cy, float size, uint colour) => GunIcons.Comment(ui, cx, cy, size, colour);

        public override float PanelHeight => 96f;

        /// <summary>The marker under the crosshair (any gun).</summary>
        public CommentRecord Hovered => _hovered;

        public override void Tick(float dt)
        {
            _clock += dt;
            _hovered = FindHovered();
        }

        public override void OnPrimary(in AimInfo aim)
        {
            if (!aim.HasHit)
            {
                Session.Sound.Play(SoundId.Error);
                return;
            }

            ElementRecord element = Session.Scene.Elements[aim.Hit.Element];
            // Linked elements' ids belong to another model: the comment records no element there
            Session.BeginCommentEdit(aim.Hit.Point + aim.Hit.Normal * 0.06f, element.IsLinked ? -1 : element.ElementId, Session.LevelNameAt(aim.Hit.Point.Z));
            Session.Sound.Play(SoundId.UiClick);
        }

        public override void OnKeys(InputState input)
        {
            if (!input.IsPressed('E') || _hovered == null) { return; }
            Session.BeginCommentEdit(_hovered);
            Session.Sound.Play(SoundId.UiClick);
        }

        public override void OnSecondary(in AimInfo aim)
        {
            if (_hovered == null) { return; }
            Session.Comments.Remove(_hovered);
            _hovered = null;
            Session.Sound.Play(SoundId.Remove);
            ReportSaveError();
        }

        public override void ClearMarkers()
        {
            if (Session.Comments.Comments.Count == 0) { return; }

            if (_clock < _clearConfirmUntil)
            {
                int count = Session.Comments.Comments.Count;
                Session.Comments.Clear();
                Session.Toast($"Deleted {count} comment{(count == 1 ? string.Empty : "s")}");
                Session.Sound.Play(SoundId.Remove);
                _clearConfirmUntil = 0f;
                ReportSaveError();
            }
            else
            {
                _clearConfirmUntil = _clock + 2.5f;
                Session.Toast($"Press X again to delete all {Session.Comments.Comments.Count} comments");
            }
        }

        private void ReportSaveError()
        {
            if (Session.Comments.LastError != null) { Session.Toast(Session.Comments.LastError, important: true); }
        }

        /// <summary>
        /// The visible marker nearest the crosshair, within a few pixels.
        /// </summary>
        private CommentRecord FindHovered()
        {
            FpsCamera camera = Session.Camera;
            var centre = new Vector2(camera.ViewportWidth * 0.5f, camera.ViewportHeight * 0.5f);
            float limit = S(HOVER_PIXELS);
            CommentRecord best = null;
            float bestDistance = float.MaxValue;

            foreach (CommentRecord record in Session.Comments.Comments)
            {
                float distance = Vector3.Distance(camera.Position, record.Local);
                if (distance > MAX_HOVER_DISTANCE) { continue; }
                if (!camera.WorldToScreen(record.Local, out Vector2 screen)) { continue; }

                float pixels = Vector2.Distance(screen, centre);
                if (pixels < limit && pixels < bestDistance)
                {
                    best = record;
                    bestDistance = pixels;
                }
            }

            // Occlusion check for the winner only
            if (best != null)
            {
                Vector3 toMarker = best.Local - camera.Position;
                float distance = toMarker.Length();
                if (distance > 1e-3f && Session.Pick(camera.Position, toMarker / distance, distance - 0.12f, out RayHit _))
                {
                    best = null;
                }
            }
            return best;
        }

        public override void DrawWorld(Overlay3D overlay, bool selected)
        {
            foreach (CommentRecord record in Session.Comments.Comments)
            {
                bool hovered = ReferenceEquals(record, _hovered);
                uint colour = hovered ? UiTheme.COMMENT_LABEL : UiTheme.COMMENT;
                overlay.Line(record.Local, record.Local - new Vector3(0f, 0f, 0.3f), 2.5f, Rgba.WithAlpha(colour, 0.9f));
                overlay.Dot(record.Local, hovered ? 11f : 9f, UiTheme.TEXT);
                overlay.Dot(record.Local, hovered ? 9f : 7f, colour);
            }

            if (Session.IsEditingComment)
            {
                overlay.Dot(Session.EditPoint, 11f, UiTheme.TEXT);
                overlay.Dot(Session.EditPoint, 9f, UiTheme.COMMENT);
            }
        }

        public override void DrawLabels(UiBatch ui, bool selected)
        {
            if (_hovered == null || Session.IsEditingComment) { return; }
            if (!Session.Camera.WorldToScreen(_hovered.Local, out Vector2 screen)) { return; }

            FontAtlas f = ui.Atlas;
            float width = S(250);
            float textHeight = ui.TextWrapped(f.Body, 0, 0, width - S(24), _hovered.Text, 0, maxLines: 8, draw: false);
            float height = S(32) + textHeight;
            float x = MathF.Min(screen.X + S(20), Session.ScreenWidth - width - S(10));
            float y = MathF.Max(S(10), screen.Y - S(36));

            ui.Panel(x, y, width, height, UiTheme.PANEL_STRONG, UiTheme.COMMENT);
            ui.Text(f.Small, x + S(12), y + S(9), _hovered.Header, UiTheme.COMMENT_LABEL, S(0.8f));
            ui.TextWrapped(f.Body, x + S(12), y + S(27), width - S(24), _hovered.Text, UiTheme.TEXT, maxLines: 8);
        }

        public override void DrawPanel(UiBatch ui, float x, float y, float width)
        {
            FontAtlas f = ui.Atlas;
            ui.Text(f.Small, x, y, "COMMENTS", UiTheme.COMMENT_LABEL, S(1.1f));
            y += S(20);

            int onLevel = 0;
            string level = Session.CurrentLevelName;
            foreach (CommentRecord record in Session.Comments.Comments)
            {
                if (record.Level == level) { onLevel++; }
            }

            Session.Text.Clear().Append(onLevel).Append(" on this level · ").Append(Session.Comments.Comments.Count).Append(" total");
            ui.Text(f.Body, x, y, Session.Text.Span, UiTheme.TEXT);
            y += S(22);

            if (Session.Comments.LastError != null)
            {
                ui.TextWrapped(f.Body, x, y, width, Session.Comments.LastError, UiTheme.DANGER, maxLines: 2);
            }
            else
            {
                Session.Text.Clear().Append("Hover a marker to read it, E to edit. Saved to ").Append(Session.Comments.FileName);
                ui.TextWrapped(f.Body, x, y, width, Session.Text.Span, UiTheme.TEXT_MUTED, maxLines: 2);
            }
        }
    }
}
