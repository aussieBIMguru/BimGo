using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Viewpoint bookmarks: B saves where the player stands and looks (then asks for a name), Ctrl+1..9 jump to the
    /// first nine, and the pause menu's BOOKMARKS list has Go, Rename, Set here (update), reorder and Delete.
    /// Bookmarks are saved inside a .bimgo (bookmarks.json) or, in a live Revit session, beside the model.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private bool _bookmarksOpen;
        private int _bookmarkScroll;
        private BookmarkRecord _bookmarkDeleteArmed;
        private float _bookmarkDeleteArmedUntil;
        private string _bookmarksNotice;

        private string _bookmarksMenuLabel;
        private int _bookmarksMenuLabelFor = -1;

        #endregion

        /// <summary>Saved viewpoints.</summary>
        public BookmarkStore Bookmarks { get; private set; }

        #region Setup

        /// <summary>
        /// Creates the bookmark store: embedded in a .bimgo, else a sidecar beside the Revit model's comments sidecar.
        /// </summary>
        private void InitialiseBookmarks()
        {
            if (IsFileMode)
            {
                Bookmarks = new BookmarkStore(null, Scene.ModelTitle, Scene.OriginOffset);
                Bookmarks.LoadFrom(_options.Document?.Bookmarks);
            }
            else
            {
                Bookmarks = new BookmarkStore(BookmarkFiles.SidecarFor(Scene.CommentsPath), Scene.ModelTitle, Scene.OriginOffset);
                Bookmarks.Load();
            }
            Bookmarks.Changed += UpdateTitle;
        }

        #endregion

        #region Actions

        /// <summary>
        /// B: prepares a bookmark of the current viewpoint (default name, thumbnail taken next frame) and opens the name
        /// box: Enter adds it to the list, Esc throws it away.
        /// </summary>
        private void AddBookmarkHere()
        {
            if (_player == null) { return; }
            BookmarkRecord record = Bookmarks.CreatePending(null, _player.Feet, _player.Yaw, _player.Pitch, _player.Flying, CurrentLevelName, BookmarkSunTime(), _section);
            _thumbnailFor = record;
            Sound.Play(SoundId.CommentPlace);
            BeginBookmarkRename(record, isNew: true);
        }

        /// <summary>
        /// Ctrl+1..9: jumps to the bookmark at that place in the list.
        /// </summary>
        /// <param name="index">0-based list index.</param>
        private void GoToBookmarkAt(int index)
        {
            if (index < 0 || index >= Bookmarks.Bookmarks.Count)
            {
                Toast(Bookmarks.Bookmarks.Count == 0
                    ? "No bookmarks yet: press B to save this viewpoint"
                    : $"Only {Bookmarks.Bookmarks.Count} bookmark{(Bookmarks.Bookmarks.Count == 1 ? string.Empty : "s")} (Esc → BOOKMARKS lists them)");
                return;
            }
            GoToBookmark(Bookmarks.Bookmarks[index]);
        }

        /// <summary>
        /// How to get back to a bookmark, for toasts: "Ctrl+3", or the list past the ninth (allocates; not per frame).
        /// </summary>
        private string BookmarkHotkey(BookmarkRecord record)
        {
            int index = Bookmarks.Bookmarks.IndexOf(record);
            return index >= 0 && index < 9 ? $"Ctrl+{index + 1}" : "Esc → BOOKMARKS";
        }

        /// <summary>
        /// The sun time a bookmark should keep: the current date / time when shadows are on, else null.
        /// </summary>
        private SunTime BookmarkSunTime()
        {
            if (!ShadowsOn) { return null; }
            SunTime time = _sun.Time.Copy();
            if (_sunPlaying) { time.Minutes = Math.Clamp((int)_playMinutes, 0, 1439); }
            return time;
        }

        /// <summary>
        /// Puts the player at a bookmark (walk / fly mode as saved) and, if it kept a sun time, restores that time with
        /// shadows on.
        /// </summary>
        private void GoToBookmark(BookmarkRecord record)
        {
            if (record == null || _player == null) { return; }
            if (record.Sun != null) { ApplySunTime(record.Sun); }
            if (record.Section != null) { ApplySection(record.Section, announce: false); }
            if (record.Flying != _player.Flying) { _player.ToggleFly(); }
            _player.TeleportTo(record.Local, record.Yaw, Math.Clamp(record.Pitch, -1.5f, 1.5f));
            Sound.Play(SoundId.Teleport);
            Flash(UiTheme.BOOKMARK, 0.25f);
            Toast(record.Name);
        }

        #endregion

        #region Open / close

        /// <summary>True while the bookmark list is showing (it replaces the pause menu).</summary>
        private bool IsBookmarksPanelOpen => _bookmarksOpen;

        private void OpenBookmarks()
        {
            _bookmarksOpen = true;
            _bookmarkScroll = 0;
            _bookmarkDeleteArmed = null;
            _bookmarksNotice = null;
        }

        /// <summary>
        /// Closes the list.
        /// </summary>
        /// <returns>True if it was open.</returns>
        private bool CloseBookmarks()
        {
            if (!_bookmarksOpen) { return false; }
            _bookmarksOpen = false;
            _bookmarkDeleteArmed = null;
            return true;
        }

        /// <summary>"BOOKMARKS (n)" (rebuilt only when the count changes).</summary>
        private string BookmarksMenuLabel()
        {
            int count = Bookmarks?.Bookmarks.Count ?? 0;
            if (count != _bookmarksMenuLabelFor || _bookmarksMenuLabel == null)
            {
                _bookmarksMenuLabelFor = count;
                _bookmarksMenuLabel = count == 0 ? "BOOKMARKS" : $"BOOKMARKS ({count})";
            }
            return _bookmarksMenuLabel;
        }

        #endregion

        #region Panel

        /// <summary>
        /// Draws and handles the bookmark list (in place of the pause menu).
        /// </summary>
        private void BuildBookmarksPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int width = _window.Width, height = _window.Height;
            _ui.Rect(0, 0, width, height, UiTheme.MENU_BACKGROUND);

            float w = MathF.Min(S(920), width - S(80));
            float h = height - S(96);
            float x = (width - w) * 0.5f, y = S(48);
            _ui.Panel(x, y, w, h, UiTheme.CARD, UiTheme.CARD_BORDER);

            float ix = x + S(24), iw = w - S(48);
            float cy = y + S(20);
            _ui.Text(f.Small, ix, cy, "BOOKMARKS", UiTheme.BOOKMARK_LABEL, S(2f));
            Text.Clear().AppendGrouped(Bookmarks.Bookmarks.Count).Append(Bookmarks.Bookmarks.Count == 1 ? " viewpoint · saved to " : " viewpoints · saved to ").Append(Bookmarks.FileName);
            _ui.TextRight(f.Small, ix + iw, cy, Text.Span, UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(30);
            _ui.Text(f.Body, ix, cy, "B saves where you stand · CTRL+1–9 jump to the first nine", UiTheme.TEXT_MUTED);
            cy += S(34);

            float buttonsY = y + h - S(24) - S(48);
            BuildBookmarkRows(f, input, ix, cy, iw, buttonsY - S(16));

            if (_bookmarksNotice != null) { _ui.TextWrapped(f.Body, ix, buttonsY - S(30), iw, _bookmarksNotice, UiTheme.MEASURE_TEXT, maxLines: 1); }
            if (MenuButton(f, ix, buttonsY, S(240), "ADD THIS VIEW", false, false))
            {
                BookmarkRecord added = Bookmarks.Add(null, _player.Feet, _player.Yaw, _player.Pitch, _player.Flying, CurrentLevelName, BookmarkSunTime(), _section);
                _thumbnailFor = added;
                _bookmarksNotice = Bookmarks.LastError ?? $"Added “{added.Name}” (RENAME to change it)";
                _bookmarkScroll = Bookmarks.Bookmarks.Count; // show the end of the list (clamped while drawing)
            }
            if (MenuButton(f, ix + S(256), buttonsY, S(160), "CLOSE", false, false)) { CloseBookmarks(); }
        }

        /// <summary>
        /// The scrolling rows: number, name, details and Go / Rename / Set here / ↑ / ↓ / Delete.
        /// </summary>
        private void BuildBookmarkRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            List<BookmarkRecord> list = Bookmarks.Bookmarks;
            float rowH = S(70);
            int visible = Math.Max(1, (int)((bottom - y) / rowH));

            if (list.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y + S(8), w, "No bookmarks yet. Resume, stand where you want and press B (or use ADD THIS VIEW below).", UiTheme.TEXT_MUTED, maxLines: 2);
                return;
            }

            int maxScroll = Math.Max(0, list.Count - visible);
            if (input.Wheel != 0) { _bookmarkScroll -= input.Wheel * 2; }
            _bookmarkScroll = Math.Clamp(_bookmarkScroll, 0, maxScroll);
            if (_bookmarkDeleteArmed != null && _clock > _bookmarkDeleteArmedUntil) { _bookmarkDeleteArmed = null; }

            BookmarkRecord go = null, rename = null, here = null, up = null, down = null, delete = null;
            float small = S(34), wide = S(84), gap = S(6);
            float buttonsW = wide * 4 + small * 2 + gap * 5;
            int last = Math.Min(list.Count, _bookmarkScroll + visible);
            for (int i = _bookmarkScroll; i < last; i++)
            {
                BookmarkRecord record = list[i];
                float ry = y + (i - _bookmarkScroll) * rowH;
                if (((i - _bookmarkScroll) & 1) == 0) { _ui.Rect(x - S(8), ry - S(4), w + S(16), rowH - S(4), Rgba.Hex(0xFFFFFF, 0.03f)); }

                // Number (Ctrl+n for the first nine), thumbnail, name, then "Level · author · date"
                Text.Clear().Append(i + 1);
                _ui.Text(f.Mono, x, ry + S(4), Text.Span, i < 9 ? UiTheme.BOOKMARK_LABEL : UiTheme.TEXT_FAINT);
                float thumbX = x + S(30), thumbY = ry, thumbW = S(110), thumbH = S(62);
                uint thumbnail = ThumbnailTexture(record);
                if (thumbnail != 0)
                {
                    _ui.Image(thumbnail, thumbX, thumbY, thumbW, thumbH, _window.Width, _window.Height);
                    _ui.Outline(thumbX, thumbY, thumbW, thumbH, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.15f));
                }
                else
                {
                    _ui.Panel(thumbX, thumbY, thumbW, thumbH, UiTheme.CONTROL, UiTheme.CONTROL_BORDER);
                    _ui.TextCentred(f.Small, thumbX + thumbW * 0.5f, thumbY + thumbH * 0.5f - S(7), "NO PICTURE", UiTheme.TEXT_FAINT, S(0.4f));
                }
                float textX = thumbX + thumbW + S(14);
                float textW = w - buttonsW - S(46) - thumbW - S(14);
                _ui.TextWrapped(f.Bold, textX, ry + S(10), textW, record.Name, UiTheme.TEXT, maxLines: 1);
                _ui.TextWrapped(f.Small, textX, ry + S(34), textW, record.Detail, UiTheme.TEXT_MUTED, maxLines: 1);

                float bx = x + w - buttonsW, by = ry + S(14), bh = S(30);
                if (SmallButton(f, input, bx, by, wide, bh, "GO")) { go = record; }
                bx += wide + gap;
                if (SmallButton(f, input, bx, by, wide, bh, "RENAME")) { rename = record; }
                bx += wide + gap;
                if (SmallButton(f, input, bx, by, wide, bh, "SET HERE")) { here = record; }
                bx += wide + gap;
                if (SmallButton(f, input, bx, by, small, bh, "↑") && i > 0) { up = record; }
                bx += small + gap;
                if (SmallButton(f, input, bx, by, small, bh, "↓") && i < list.Count - 1) { down = record; }
                bx += small + gap;
                bool armed = ReferenceEquals(_bookmarkDeleteArmed, record);
                if (SmallButton(f, input, bx, by, wide, bh, armed ? "SURE?" : "DELETE", danger: true)) { delete = record; }
            }

            if (maxScroll > 0)
            {
                Text.Clear().Append(_bookmarkScroll + 1).Append('–').Append(last).Append(" of ").Append(list.Count).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(2), Text.Span, UiTheme.TEXT_FAINT);
            }

            // Act after drawing (the list must not change while it is being walked)
            if (go != null)
            {
                CloseBookmarks();
                SetPaused(false);
                GoToBookmark(go);
            }
            else if (rename != null)
            {
                CloseBookmarks();
                SetPaused(false);
                BeginBookmarkRename(rename, isNew: false);
            }
            else if (here != null)
            {
                Bookmarks.Update(here, _player.Feet, _player.Yaw, _player.Pitch, _player.Flying, CurrentLevelName, BookmarkSunTime(), _section);
                _thumbnailFor = here;
                _bookmarksNotice = Bookmarks.LastError ?? $"“{here.Name}” now points at where you are";
                Sound.Play(SoundId.Commit);
            }
            else if (up != null || down != null)
            {
                Bookmarks.Move(up ?? down, up != null ? -1 : +1);
                _bookmarksNotice = Bookmarks.LastError;
            }
            else if (delete != null)
            {
                if (ReferenceEquals(_bookmarkDeleteArmed, delete))
                {
                    Bookmarks.Remove(delete);
                    _bookmarkDeleteArmed = null;
                    _bookmarksNotice = Bookmarks.LastError ?? $"Deleted “{delete.Name}”";
                    Sound.Play(SoundId.Remove);
                }
                else
                {
                    _bookmarkDeleteArmed = delete;
                    _bookmarkDeleteArmedUntil = _clock + 3f;
                }
            }
        }

        #endregion
    }
}
