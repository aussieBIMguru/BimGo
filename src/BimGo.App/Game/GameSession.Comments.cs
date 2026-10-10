using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The comment list (pause menu → COMMENTS): every comment as an issue, with its thumbnail, status, priority,
    /// assignee and reply count, filtered by level and status; GO (back to the view it was made from), OPEN (the detail
    /// view: status, priority, assignee, the reply thread, SET VIEW HERE, EDIT TEXT) and DELETE; BCF export / import.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private static readonly string[] STATUS_FILTERS = { "All", "Open", "In progress", "Closed" };
        private static readonly string[] STATUS_OPTIONS = { "Open", "In progress", "Closed" };
        private static readonly string[] PRIORITY_OPTIONS = { "Low", "Normal", "High" };

        private bool _commentsOpen;
        private int _commentLevelFilter = -1;
        private int _commentStatusFilter;   // 0 all, 1 open, 2 in progress, 3 closed
        private int _commentScroll;
        private CommentRecord _commentDeleteArmed;
        private float _commentDeleteArmedUntil;
        private string _commentsNotice;

        // Detail view (null = the list)
        private CommentRecord _commentDetail;
        private int _replyScroll;
        private CommentReply _replyDeleteArmed;
        private float _replyDeleteArmedUntil;

        // Cached labels (rebuilt when the filter or the count changes)
        private string _commentFilterLabel;
        private int _commentFilterLabelFor = int.MinValue;

        #endregion

        #region Open / close

        /// <summary>True while the comment list is showing (it replaces the pause menu).</summary>
        private bool IsCommentsPanelOpen => _commentsOpen;

        /// <summary>
        /// Opens the list, filtered to the player's level when it has comments there.
        /// </summary>
        private void OpenComments()
        {
            _commentsOpen = true;
            _commentScroll = 0;
            _commentDeleteArmed = null;
            _commentsNotice = null;
            _commentDetail = null;

            int level = Scene.Levels.Length > 0 ? LevelIndexAt(_player.Feet.Z) : -1;
            _commentLevelFilter = level >= 0 && CountCommentsOn(level) > 0 ? level : -1;
        }

        /// <summary>
        /// Esc: the detail view goes back to the list, the list closes.
        /// </summary>
        /// <returns>True if it was open.</returns>
        private bool CloseComments()
        {
            if (!_commentsOpen) { return false; }
            if (_commentDetail != null)
            {
                _commentDetail = null;
                return true;
            }
            _commentsOpen = false;
            _commentDeleteArmed = null;
            return true;
        }

        #endregion

        #region Actions

        /// <summary>
        /// Back to where a comment was made from (its saved view); older comments without one: 1.6 m in front of
        /// the marker, on its level, looking at it.
        /// </summary>
        public void TeleportToComment(CommentRecord record)
        {
            if (record == null || _player == null) { return; }
            if (Comments.TryGetView(record, out Vector3 viewFeet))
            {
                if (record.View.Section != null) { ApplySection(record.View.Section, announce: false); }
                if (record.View.Flying != _player.Flying) { _player.ToggleFly(); }
                _player.TeleportTo(viewFeet, record.View.Yaw, Math.Clamp(record.View.Pitch, -1.5f, 1.5f));
                return;
            }

            ApproachMarker(record.Local, out Vector3 feet, out float yaw, out float pitch);
            if (_player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(feet, yaw, pitch);
        }

        /// <summary>
        /// A standing spot 1.6 m from a marker on the player's side (or under it when that spot is blocked), on the
        /// marker's level, looking at it: GO for comments without a saved view, and their BCF viewpoint.
        /// </summary>
        private void ApproachMarker(Vector3 marker, out Vector3 feet, out float yaw, out float pitch)
        {
            // Approach from the player's side (so we don't end up on the far side of a wall)
            var toMarker = new Vector2(marker.X - _player.Feet.X, marker.Y - _player.Feet.Y);
            Vector2 direction = toMarker.LengthSquared() > 0.01f ? Vector2.Normalize(toMarker) : new Vector2(1f, 0f);
            Vector2 standXY = new Vector2(marker.X, marker.Y) - direction * 1.6f;

            float floorZ = Scene.Levels.Length > 0 ? Scene.Levels[LevelIndexAt(marker.Z)].Elevation : marker.Z - 1.2f;
            feet = FloorAt(standXY, floorZ);

            // Blocked (inside a wall or furniture)? Stand under the marker instead
            if (_player.Controller.Overlaps(feet + new Vector3(0f, 0f, 0.01f), CharacterController.STAND_HEIGHT))
            {
                feet = FloorAt(new Vector2(marker.X, marker.Y), floorZ);
            }

            Vector3 eye = feet + new Vector3(0f, 0f, CharacterController.STAND_EYE);
            Vector3 look = marker - eye;
            yaw = MathF.Atan2(look.Y, look.X);
            pitch = Math.Clamp(MathF.Atan2(look.Z, MathF.Max(0.01f, new Vector2(look.X, look.Y).Length())), -1.2f, 1.2f);
        }

        /// <summary>
        /// The floor under a plan point near an elevation (picked from 1.7 m above), else the elevation itself.
        /// </summary>
        private Vector3 FloorAt(Vector2 xy, float floorZ)
        {
            var feet = new Vector3(xy.X, xy.Y, floorZ + 0.02f);
            if (Pick(new Vector3(xy.X, xy.Y, floorZ + 1.7f), -Vector3.UnitZ, 2.4f, out RayHit hit) && hit.Normal.Z > 0.7f)
            {
                feet.Z = hit.Point.Z + 0.02f;
            }
            return feet;
        }

        /// <summary>
        /// Leaves the menu and goes to a comment's view (its text as a note).
        /// </summary>
        private void GoToComment(CommentRecord record)
        {
            _commentDetail = null;
            CloseComments();
            SetPaused(false);
            TeleportToComment(record);
            SelectGun(Array.IndexOf(_guns, _commentGun));
            Toast(record.Text.Length > 60 ? record.Text[..57] + "…" : record.Text, 3f);
        }

        #endregion

        #region Panel

        /// <summary>
        /// Draws and handles the comment list or a comment's detail view (in place of the pause menu).
        /// </summary>
        private void BuildCommentsPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int width = _window.Width, height = _window.Height;
            _ui.Rect(0, 0, width, height, UiTheme.MENU_BACKGROUND);

            float w = MathF.Min(S(1000), width - S(80));
            float h = height - S(96);
            float x = (width - w) * 0.5f, y = S(48);
            _ui.Panel(x, y, w, h, UiTheme.CARD, UiTheme.CARD_BORDER);

            if (_commentDetail != null && !Comments.Comments.Contains(_commentDetail)) { _commentDetail = null; }
            if (_commentDetail != null)
            {
                BuildCommentDetail(f, input, x, y, w, h, _commentDetail);
                return;
            }

            float ix = x + S(24), iw = w - S(48);
            float cy = y + S(20);
            _ui.Text(f.Small, ix, cy, "COMMENTS", UiTheme.COMMENT_LABEL, S(2f));
            Text.Clear().AppendGrouped(Comments.Comments.Count).Append(Comments.Comments.Count == 1 ? " comment · saved to " : " comments · saved to ").Append(Comments.FileName);
            _ui.TextRight(f.Small, ix + iw, cy, Text.Span, UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(30);

            // Filters: ‹ level › and status
            float filterX = ix;
            if (Scene.Levels.Length > 0)
            {
                if (SmallButton(f, input, ix, cy, S(32), S(32), "←")) { StepCommentFilter(-1); }
                _ui.Panel(ix + S(38), cy, S(240), S(32), UiTheme.CONTROL, UiTheme.CONTROL_BORDER);
                _ui.TextCentred(f.Body, ix + S(38) + S(120), cy + S(16) - f.Body.LineHeight * 0.5f, CommentFilterLabel(), UiTheme.TEXT);
                if (SmallButton(f, input, ix + S(284), cy, S(32), S(32), "→")) { StepCommentFilter(+1); }
                filterX = ix + S(336);
            }
            int status = Segmented(f, input, filterX, cy, MathF.Min(S(420), ix + iw - filterX), STATUS_FILTERS, _commentStatusFilter);
            if (status != _commentStatusFilter)
            {
                _commentStatusFilter = status;
                _commentScroll = 0;
            }
            cy += S(46);

            float buttonsY = y + h - S(24) - S(48);
            BuildCommentRows(f, input, ix, cy, iw, buttonsY - S(16));
            if (_commentDetail != null) { return; }

            if (_commentsNotice != null) { _ui.TextWrapped(f.Body, ix, buttonsY - S(30), iw, _commentsNotice, UiTheme.MEASURE_TEXT, maxLines: 1); }
            // EXPORT BCF (the comments shown) · IMPORT BCF · BCF coordinates · CLOSE
            float gap = S(10);
            float bw = MathF.Min(S(230), (iw - gap * 3) / 4f);
            float bx = ix;
            if (MenuButton(f, bx, buttonsY, bw, "EXPORT BCF…", false, false, enabled: Comments.Comments.Count > 0)) { ExportBcf(); }
            bx += bw + gap;
            if (MenuButton(f, bx, buttonsY, bw, "IMPORT BCF…", false, false)) { ImportBcf(); }
            bx += bw + gap;
            if (MenuButton(f, bx, buttonsY, bw, BcfCoordinateLabel, false, false)) { CycleBcfCoordinates(); }
            bx += bw + gap;
            if (MenuButton(f, bx, buttonsY, bw, "CLOSE", false, false)) { CloseComments(); }
        }

        /// <summary>
        /// The scrolling rows: thumbnail, header, text, issue line (status · priority · assignee · replies) and
        /// GO / OPEN / DELETE.
        /// </summary>
        private void BuildCommentRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            float rowH = S(84);
            int visible = Math.Max(1, (int)((bottom - y) / rowH));

            // Count the filtered rows (no list is built: the filter is applied while drawing)
            int total = 0;
            foreach (CommentRecord record in Comments.Comments)
            {
                if (MatchesCommentFilter(record)) { total++; }
            }

            if (total == 0)
            {
                _ui.TextWrapped(f.Body, x, y + S(8), w, Comments.Comments.Count == 0
                    ? "No comments yet. Use the Comment gun (4): LMB places a marker and opens a text box."
                    : "No comments match. Use ← → for another level, or pick All.", UiTheme.TEXT_MUTED, maxLines: 2);
                return;
            }

            int maxScroll = Math.Max(0, total - visible);
            if (input.Wheel != 0) { _commentScroll -= input.Wheel * 2; }
            _commentScroll = Math.Clamp(_commentScroll, 0, maxScroll);
            if (_commentDeleteArmed != null && _clock > _commentDeleteArmedUntil) { _commentDeleteArmed = null; }

            CommentRecord go = null, open = null, delete = null;
            int index = -1, drawn = 0;
            float buttonW = S(70), gap = S(6);
            float buttonsW = buttonW * 3 + gap * 2;
            float thumbW = S(120), thumbH = S(68);
            foreach (CommentRecord record in Comments.Comments)
            {
                if (!MatchesCommentFilter(record)) { continue; }
                index++;
                if (index < _commentScroll) { continue; }
                if (drawn >= visible) { break; }

                float ry = y + drawn * rowH;
                drawn++;
                if ((drawn & 1) == 1) { _ui.Rect(x - S(8), ry - S(4), w + S(16), rowH - S(4), Rgba.Hex(0xFFFFFF, 0.03f)); }

                // Status bar on the left edge, then the thumbnail
                uint statusColour = UiTheme.StatusColour(record.Status);
                _ui.Rect(x - S(8), ry - S(4), S(3), rowH - S(4), statusColour);
                DrawCommentThumbnail(f, record, x, ry, thumbW, thumbH);

                float textX = x + thumbW + S(14);
                float textW = w - buttonsW - thumbW - S(30);
                Text.Clear().Append(record.Header);
                if (!string.IsNullOrEmpty(record.Level)) { Text.Append(" · ").Append(record.Level); }
                _ui.TextWrapped(f.Small, textX, ry + S(2), textW, Text.Span, UiTheme.COMMENT_LABEL, maxLines: 1);
                _ui.TextWrapped(f.Body, textX, ry + S(20), textW, record.Text, record.Status == CommentStatus.CLOSED ? UiTheme.TEXT_MUTED : UiTheme.TEXT, maxLines: 1);
                IssueLine(f, record, textX, ry + S(46), textW);

                float bx = x + w - buttonsW, by = ry + S(18);
                if (SmallButton(f, input, bx, by, buttonW, S(30), "GO")) { go = record; }
                if (SmallButton(f, input, bx + buttonW + gap, by, buttonW, S(30), "OPEN")) { open = record; }
                bool armed = ReferenceEquals(_commentDeleteArmed, record);
                if (SmallButton(f, input, bx + (buttonW + gap) * 2, by, buttonW, S(30), armed ? "SURE?" : "DELETE", danger: true)) { delete = record; }
            }

            if (maxScroll > 0)
            {
                Text.Clear().Append(_commentScroll + 1).Append('–').Append(Math.Min(total, _commentScroll + visible)).Append(" of ").Append(total).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(2), Text.Span, UiTheme.TEXT_FAINT);
            }

            // Act after drawing (the list must not change while it is being walked)
            if (go != null) { GoToComment(go); }
            else if (open != null)
            {
                _commentDetail = open;
                _replyScroll = 0;
                _replyDeleteArmed = null;
                _commentsNotice = null;
            }
            else if (delete != null) { DeleteComment(delete); }
        }

        /// <summary>
        /// "● Open · High · → Sam · 3 replies" (status coloured; priority, assignee and replies only when set).
        /// </summary>
        private void IssueLine(FontAtlas f, CommentRecord record, float x, float y, float w)
        {
            uint statusColour = UiTheme.StatusColour(record.Status);
            _ui.Circle(x + S(5), y + f.Small.LineHeight * 0.5f, S(4), statusColour, 12);
            float used = S(14) + _ui.Text(f.Small, x + S(14), y, CommentStatus.Label(record.Status), statusColour, S(0.4f));
            Text.Clear();
            if (record.Priority != CommentPriority.NORMAL) { Text.Append(" · ").Append(CommentPriority.Label(record.Priority)).Append(" priority"); }
            if (record.AssignedTo != null) { Text.Append(" · → ").Append(record.AssignedTo); }
            if (record.ReplyCount > 0) { Text.Append(" · ").Append(record.ReplyCount).Append(record.ReplyCount == 1 ? " reply" : " replies"); }
            if (Text.Span.Length > 0)
            {
                _ui.TextWrapped(f.Small, x + used, y, w - used, Text.Span, record.Priority == CommentPriority.HIGH ? UiTheme.DANGER : UiTheme.TEXT_SOFT, maxLines: 1);
            }
        }

        /// <summary>
        /// A comment's thumbnail (or a "NO PICTURE" tile for older comments).
        /// </summary>
        private void DrawCommentThumbnail(FontAtlas f, CommentRecord record, float x, float y, float w, float h)
        {
            uint thumbnail = CommentThumbnailTexture(record);
            if (thumbnail != 0)
            {
                _ui.Image(thumbnail, x, y, w, h, _window.Width, _window.Height);
                _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.15f));
            }
            else
            {
                _ui.Panel(x, y, w, h, UiTheme.CONTROL, UiTheme.CONTROL_BORDER);
                _ui.TextCentred(f.Small, x + w * 0.5f, y + h * 0.5f - S(7), "NO PICTURE", UiTheme.TEXT_FAINT, S(0.4f));
            }
        }

        /// <summary>
        /// One comment in full: picture, text, status / priority / assignee, the reply thread and its actions.
        /// </summary>
        private void BuildCommentDetail(FontAtlas f, InputState input, float x, float y, float w, float h, CommentRecord record)
        {
            float ix = x + S(24), iw = w - S(48);
            float cy = y + S(20);
            _ui.Text(f.Small, ix, cy, "COMMENT", UiTheme.COMMENT_LABEL, S(2f));
            Text.Clear().Append(record.Header);
            if (!string.IsNullOrEmpty(record.Level)) { Text.Append(" · ").Append(record.Level); }
            _ui.TextRight(f.Small, ix + iw, cy, Text.Span, UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(32);

            // Picture (left) and the issue fields (right)
            float picW = MathF.Min(S(384), iw * 0.42f), picH = picW * 9f / 16f;
            DrawCommentThumbnail(f, record, ix, cy, picW, picH);
            float rx = ix + picW + S(24), rw = iw - picW - S(24);
            float textH = _ui.TextWrapped(f.Body, rx, cy, rw, record.Text, UiTheme.TEXT, maxLines: 5);
            float fy = cy + MathF.Max(textH, f.Body.LineHeight) + S(14);

            _ui.Text(f.Small, rx, fy + S(9), "STATUS", UiTheme.TEXT_MUTED, S(0.6f));
            int statusIndex = Array.IndexOf(CommentStatus.ALL, record.Status);
            int newStatus = Segmented(f, input, rx + S(90), fy, MathF.Min(S(330), rw - S(90)), STATUS_OPTIONS, Math.Max(0, statusIndex));
            if (newStatus != statusIndex && newStatus >= 0)
            {
                Comments.SetIssue(record, status: CommentStatus.ALL[newStatus]);
                _commentsNotice = Comments.LastError ?? $"Status: {STATUS_OPTIONS[newStatus]}";
            }
            fy += S(40);

            _ui.Text(f.Small, rx, fy + S(9), "PRIORITY", UiTheme.TEXT_MUTED, S(0.6f));
            int priorityIndex = Array.IndexOf(CommentPriority.ALL, record.Priority);
            int newPriority = Segmented(f, input, rx + S(90), fy, MathF.Min(S(330), rw - S(90)), PRIORITY_OPTIONS, Math.Max(0, priorityIndex));
            if (newPriority != priorityIndex && newPriority >= 0)
            {
                Comments.SetIssue(record, priority: CommentPriority.ALL[newPriority]);
                _commentsNotice = Comments.LastError ?? $"Priority: {PRIORITY_OPTIONS[newPriority]}";
            }
            fy += S(40);

            _ui.Text(f.Small, rx, fy + S(9), "ASSIGNED", UiTheme.TEXT_MUTED, S(0.6f));
            _ui.TextWrapped(f.Body, rx + S(90), fy + S(6), MathF.Max(S(60), rw - S(90) - S(130)), record.AssignedTo ?? "Nobody", record.AssignedTo == null ? UiTheme.TEXT_FAINT : UiTheme.TEXT, maxLines: 1);
            if (SmallButton(f, input, rx + rw - S(120), fy, S(120), S(30), "ASSIGN…")) { BeginCommentAssign(record); }
            fy += S(40);

            if (record.Updated.HasValue)
            {
                record.UpdatedLabel ??= $"Updated by {record.UpdatedBy ?? "?"} · {record.Updated.Value.ToLocalTime():dd MMM HH:mm}";
                _ui.TextWrapped(f.Small, rx, fy, rw, record.UpdatedLabel, UiTheme.TEXT_FAINT, maxLines: 1);
            }

            // Thread
            cy += MathF.Max(picH, fy + S(20) - cy) + S(16);
            Text.Clear().Append("REPLIES (").Append(record.ReplyCount).Append(')');
            _ui.Text(f.Small, ix, cy, Text.Span, UiTheme.COMMENT_LABEL, S(1f));
            cy += S(24);

            float buttonsY = y + h - S(24) - S(44);
            BuildReplies(f, input, record, ix, cy, iw, buttonsY - S(40));

            if (_commentsNotice != null) { _ui.TextWrapped(f.Body, ix, buttonsY - S(28), iw, _commentsNotice, UiTheme.MEASURE_TEXT, maxLines: 1); }

            float bw = S(150), bg = S(10), bx = ix;
            if (MenuButton(f, bx, buttonsY, S(110), "BACK", false, false, height: S(44))) { _commentDetail = null; return; }
            bx += S(110) + bg;
            if (MenuButton(f, bx, buttonsY, S(130), "REPLY…", true, false, height: S(44))) { BeginCommentReply(record); }
            bx += S(130) + bg;
            if (MenuButton(f, bx, buttonsY, bw, "GO TO VIEW", false, false, height: S(44))) { GoToComment(record); return; }
            bx += bw + bg;
            if (MenuButton(f, bx, buttonsY, bw, "SET VIEW HERE", false, false, height: S(44)))
            {
                // Where the player stands now (the picture is taken from the 3D view behind the menu, next frame)
                Comments.SetView(record, _player.Feet, _player.Yaw, _player.Pitch, _player.Flying, section: _section);
                _commentThumbnailFor = record;
                _commentsNotice = Comments.LastError ?? "View and picture set to where you stand";
                Sound.Play(SoundId.Commit);
            }
            bx += bw + bg;
            if (MenuButton(f, bx, buttonsY, S(130), "EDIT TEXT", false, false, height: S(44))) { BeginCommentEdit(record); }
            bx += S(130) + bg;
            bool armed = ReferenceEquals(_commentDeleteArmed, record) && _clock <= _commentDeleteArmedUntil;
            if (MenuButton(f, bx, buttonsY, S(120), armed ? "SURE?" : "DELETE", false, true, height: S(44)))
            {
                DeleteComment(record);
                if (!Comments.Comments.Contains(record)) { _commentDetail = null; }
            }
        }

        /// <summary>
        /// The reply thread (oldest first, wheel scrolls), each with its author, date and DELETE.
        /// </summary>
        private void BuildReplies(FontAtlas f, InputState input, CommentRecord record, float x, float y, float w, float bottom)
        {
            if (record.ReplyCount == 0)
            {
                _ui.Text(f.Body, x, y + S(4), "No replies yet: REPLY… adds one.", UiTheme.TEXT_MUTED);
                return;
            }

            float rowH = S(58);
            int visible = Math.Max(1, (int)((bottom - y) / rowH));
            List<CommentReply> replies = record.Replies;
            int maxScroll = Math.Max(0, replies.Count - visible);
            if (input.Wheel != 0 && Hover(input, x, y, w, bottom - y)) { _replyScroll -= input.Wheel; }
            _replyScroll = Math.Clamp(_replyScroll, 0, maxScroll);
            if (_replyDeleteArmed != null && _clock > _replyDeleteArmedUntil) { _replyDeleteArmed = null; }

            CommentReply delete = null;
            int last = Math.Min(replies.Count, _replyScroll + visible);
            for (int i = _replyScroll; i < last; i++)
            {
                CommentReply reply = replies[i];
                float ry = y + (i - _replyScroll) * rowH;
                _ui.Rect(x, ry, S(2), rowH - S(10), UiTheme.COMMENT);
                reply.Header ??= $"{reply.Author?.ToUpperInvariant() ?? "?"} · {reply.Created.ToLocalTime():dd MMM HH:mm}";
                _ui.Text(f.Small, x + S(12), ry, reply.Header, UiTheme.COMMENT_LABEL, S(0.6f));
                _ui.TextWrapped(f.Body, x + S(12), ry + S(18), w - S(110), reply.Text, UiTheme.TEXT, maxLines: 2);
                bool armed = ReferenceEquals(_replyDeleteArmed, reply);
                if (SmallButton(f, input, x + w - S(84), ry + S(4), S(84), S(28), armed ? "SURE?" : "DELETE", danger: true)) { delete = reply; }
            }
            if (maxScroll > 0)
            {
                Text.Clear().Append(_replyScroll + 1).Append('–').Append(last).Append(" of ").Append(replies.Count).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(2), Text.Span, UiTheme.TEXT_FAINT);
            }

            if (delete == null) { return; }
            if (ReferenceEquals(_replyDeleteArmed, delete))
            {
                Comments.RemoveReply(record, delete);
                _replyDeleteArmed = null;
                _commentsNotice = Comments.LastError ?? "Reply deleted";
                Sound.Play(SoundId.Remove);
            }
            else
            {
                _replyDeleteArmed = delete;
                _replyDeleteArmedUntil = _clock + 3f;
            }
        }

        /// <summary>
        /// DELETE: the first click arms it for 3 s, the second deletes.
        /// </summary>
        private void DeleteComment(CommentRecord record)
        {
            if (ReferenceEquals(_commentDeleteArmed, record) && _clock <= _commentDeleteArmedUntil)
            {
                Comments.Remove(record);
                _commentDeleteArmed = null;
                _commentsNotice = Comments.LastError ?? "Comment deleted";
                Sound.Play(SoundId.Remove);
            }
            else
            {
                _commentDeleteArmed = record;
                _commentDeleteArmedUntil = _clock + 3f;
            }
        }

        /// <summary>
        /// A compact button (list rows, filter arrows).
        /// </summary>
        private bool SmallButton(FontAtlas f, InputState input, float x, float y, float w, float h, string label, bool danger = false)
        {
            bool hover = Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, hover ? Rgba.Hex(0xFFFFFF, 0.1f) : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), danger ? Rgba.Hex(0xFCA5A5, 0.45f) : UiTheme.CONTROL_BORDER);
            _ui.TextCentred(f.Small, x + w * 0.5f, y + h * 0.5f - f.Small.LineHeight * 0.5f, label, danger ? UiTheme.DANGER : hover ? UiTheme.ACCENT : UiTheme.TEXT, S(0.8f));

            bool clicked = hover && input.LeftPressed;
            if (clicked)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
            }
            return clicked;
        }

        #endregion

        #region Filter

        /// <summary>
        /// Cycles the level filter: all levels, then each level in order.
        /// </summary>
        private void StepCommentFilter(int direction)
        {
            int count = Scene.Levels.Length + 1; // + "all"
            int current = _commentLevelFilter + 1;
            _commentLevelFilter = ((current + direction) % count + count) % count - 1;
            _commentScroll = 0;
            _commentDeleteArmed = null;
        }

        private bool MatchesCommentFilter(CommentRecord record)
        {
            if (_commentStatusFilter > 0 && record.Status != CommentStatus.ALL[_commentStatusFilter - 1]) { return false; }
            if (_commentLevelFilter < 0 || _commentLevelFilter >= Scene.Levels.Length) { return true; }
            return string.Equals(record.Level, Scene.Levels[_commentLevelFilter].Name, StringComparison.Ordinal);
        }

        private int CountCommentsOn(int level)
        {
            int count = 0;
            string name = Scene.Levels[level].Name;
            foreach (CommentRecord record in Comments.Comments)
            {
                if (string.Equals(record.Level, name, StringComparison.Ordinal)) { count++; }
            }
            return count;
        }

        /// <summary>
        /// "All levels (12)" or "Level 1 (3)", rebuilt only when the filter or the count changes.
        /// </summary>
        private string CommentFilterLabel()
        {
            int key = (_commentLevelFilter + 1) * 100_000 + Comments.Comments.Count;
            if (key == _commentFilterLabelFor && _commentFilterLabel != null) { return _commentFilterLabel; }
            _commentFilterLabelFor = key;
            _commentFilterLabel = _commentLevelFilter < 0
                ? $"All levels ({Comments.Comments.Count})"
                : $"{Scene.Levels[_commentLevelFilter].Name} ({CountCommentsOn(_commentLevelFilter)})";
            return _commentFilterLabel;
        }

        #endregion
    }
}
