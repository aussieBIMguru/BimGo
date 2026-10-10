using System.Numerics;
using BimGo.Audio;
using BimGo.Edits;
using BimGo.Game.Guns;
using BimGo.Native;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The family library (next round): a pause-menu panel to browse, search and filter the loadable family types
    /// Revit sent with a live snapshot (Options → Load → Family library), and picking one hands it to the Place gun.
    /// Placeable types have a hidden template element; a placement is a clone of it (see <see cref="CreatePlacement"/>).
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Constants and fields

        /// <summary>Preview images decoded per frame at most (keeps scrolling smooth on a big library).</summary>
        private const int PREVIEW_DECODES_PER_FRAME = 6;

        /// <summary>Longest search text.</summary>
        private const int LIBRARY_SEARCH_MAX = 48;

        private PlaceGun _placeGun;
        private bool _libraryOpen;
        private int _libraryScroll, _libraryFamilyScroll;
        private readonly char[] _librarySearch = new char[LIBRARY_SEARCH_MAX];
        private int _librarySearchLength;
        private int _libraryCategory = -1;      // catalog index, -1 = all
        private string _libraryFamily;          // null = all families
        private bool _libraryPlaceableOnly;
        private string _libraryNotice;

        // Filtered rows (entry indices) and the family list, rebuilt only when a filter changes
        private readonly List<int> _libraryRows = new();
        private readonly List<string> _libraryFamilies = new();
        private readonly List<int> _libraryCategories = new();
        private readonly List<string> _libraryCategoryLabels = new();
        private bool _libraryRowsDirty = true;
        private LibraryEntry _libraryNoticeFor;

        // Preview textures by entry name (0 = undecodable), made on first sight
        private readonly Dictionary<string, uint> _previewTextures = new(StringComparer.Ordinal);
        private int _previewDecodes;

        private string _libraryMenuLabel;

        #endregion

        #region State

        /// <summary>True when the snapshot carries a family library.</summary>
        public bool HasLibrary => !Scene.Library.IsEmpty;

        /// <summary>The library of this snapshot (never null).</summary>
        public LibraryData Library => Scene.Library;

        /// <summary>True while the library panel is showing (it replaces the pause menu).</summary>
        private bool IsLibraryPanelOpen => _libraryOpen;

        /// <summary>"FAMILY LIBRARY (n)" for the pause menu.</summary>
        private string LibraryMenuLabel() => _libraryMenuLabel ??= $"FAMILY LIBRARY ({Scene.Library.Entries.Length})";

        #endregion

        #region Open / close

        /// <summary>
        /// Opens the library (pausing the walkthrough). Without a library, says how to get one.
        /// </summary>
        public void OpenLibrary()
        {
            if (!HasLibrary)
            {
                Sound.Play(SoundId.Error);
                Toast(_live != null
                    ? "No family library in this snapshot: tick Options → Load → “Family library” in Revit, then press Go (or F5)"
                    : "This file has no family library (it comes with live Revit sessions: Options → Load → “Family library”)", 5f);
                return;
            }
            if (!_paused) { SetPaused(true); }
            _libraryOpen = true;
            _libraryNotice = null;
        }

        /// <summary>
        /// Closes the library panel (back to the pause menu).
        /// </summary>
        /// <returns>True if it was open.</returns>
        private bool CloseLibrary()
        {
            if (!_libraryOpen) { return false; }
            _libraryOpen = false;
            return true;
        }

        /// <summary>
        /// A card was picked: back to the walkthrough with the Place gun holding a new instance in front of the player.
        /// </summary>
        private void PickFromLibrary(LibraryEntry entry)
        {
            CloseLibrary();
            SetPaused(false);
            int slot = Array.IndexOf(_guns, _placeGun);
            if (slot >= 0) { SelectGun(slot); }
            _placeGun.Begin(entry);
        }

        #endregion

        #region Placement

        /// <summary>
        /// A new (uncommitted) instance of a library type: a clone of its hidden template.
        /// </summary>
        /// <param name="entry">A placeable entry.</param>
        /// <param name="cloneKey">The key to use (journal replay), or 0 for the next free key.</param>
        /// <returns>The instance, or null when the entry has no template.</returns>
        public DynamicInstance CreatePlacement(LibraryEntry entry, int cloneKey = 0)
        {
            if (entry == null || entry.Element < 0 || entry.Element >= Scene.Elements.Length || !Scene.Elements[entry.Element].IsLibraryTemplate) { return null; }
            return CreateClone(entry.Element, null, cloneKey);
        }

        /// <summary>
        /// Puts a placement where a pivot (scene-local) and angle say (the template's own pivot is where Revit placed
        /// the temporary instance, 2 km below the model).
        /// </summary>
        public void SetPlacement(DynamicInstance instance, Vector3 pivot, float angle)
        {
            Dynamics.SetTransform(instance, pivot - instance.BasePivot, angle);
        }

        /// <summary>
        /// Journal replay of a placement: the type's template cloned under the entry's key, where the entry put it.
        /// </summary>
        private bool ApplyPlaceEntry(JournalEntry entry)
        {
            LibraryEntry type = Scene.Library.Find(entry.TypeUniqueId);
            DynamicInstance placed = CreatePlacement(type, entry.NewCloneKey);
            if (placed == null)
            {
                Utilities.Log_Utils.Write($"Journal entry {entry.Seq}: family type {entry.TypeUniqueId} is not in this snapshot's library; the placement is not shown.");
                _nextCloneKey = Math.Max(_nextCloneKey, entry.NewCloneKey);
                return false;
            }
            SetPlacement(placed, entry.Pivot - Scene.OriginOffset, entry.Angle);
            placed.Committed = true;
            placed.RevitId = entry.RevitElementId;
            return true;
        }

        #endregion

        #region Panel

        /// <summary>
        /// Draws and handles the library (in place of the pause menu): search, category chips, a family list on the
        /// left and a grid of preview cards. Typing goes into the search box.
        /// </summary>
        private void BuildLibraryPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int width = _window.Width, height = _window.Height;
            _previewDecodes = 0;
            _ui.Rect(0, 0, width, height, UiTheme.MENU_BACKGROUND);

            float w = MathF.Min(S(1180), width - S(80));
            float h = height - S(96);
            float x = (width - w) * 0.5f, y = S(48);
            _ui.Panel(x, y, w, h, UiTheme.CARD, UiTheme.CARD_BORDER);

            LibraryData library = Scene.Library;
            float ix = x + S(24), iw = w - S(48);
            float cy = y + S(20);
            _ui.Text(f.Small, ix, cy, "FAMILY LIBRARY", UiTheme.PLACE_LABEL, S(2f));
            Text.Clear().AppendGrouped(library.Entries.Length).Append(" types · ").AppendGrouped(library.PlaceableCount).Append(" placeable · from Revit");
            _ui.TextRight(f.Small, ix + iw, cy, Text.Span, UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(32);

            // Search box (typing anywhere in the panel goes here)
            TypeIntoSearch(input);
            float searchW = MathF.Min(S(360), iw * 0.4f), boxH = S(34);
            _ui.Panel(ix, cy, searchW, boxH, UiTheme.CONTROL, UiTheme.CONTROL_BORDER);
            if (_librarySearchLength == 0)
            {
                _ui.Text(f.Body, ix + S(10), cy + boxH * 0.5f - f.Body.LineHeight * 0.5f, "Type to search family, type or category", UiTheme.TEXT_FAINT);
            }
            else
            {
                float used = _ui.Text(f.Body, ix + S(10), cy + boxH * 0.5f - f.Body.LineHeight * 0.5f, _librarySearch.AsSpan(0, _librarySearchLength), UiTheme.TEXT);
                if (((int)(_clock * 2f) & 1) == 0) { _ui.Rect(ix + S(11) + used, cy + S(8), S(2), boxH - S(16), UiTheme.TEXT_SOFT); }
            }
            float after = ix + searchW + S(16);
            bool placeable = Checkbox(f, input, after, cy + S(9), S(200), "Placeable only", _libraryPlaceableOnly);
            if (placeable != _libraryPlaceableOnly) { _libraryPlaceableOnly = placeable; _libraryScroll = 0; _libraryRowsDirty = true; }
            cy += boxH + S(14);

            RefreshLibraryRows();

            // Category chips (ALL + the categories present), wrapping
            float chipX = ix, chipH = S(28);
            cy = CategoryChip(f, input, ref chipX, cy, ix, iw, chipH, -1, "ALL");
            for (int i = 0; i < _libraryCategories.Count; i++)
            {
                cy = CategoryChip(f, input, ref chipX, cy, ix, iw, chipH, _libraryCategories[i], _libraryCategoryLabels[i]);
            }
            cy += chipH + S(16);

            // Footer: notice and CLOSE
            float buttonsY = y + h - S(24) - S(44);
            if (_libraryNotice != null) { _ui.TextWrapped(f.Body, ix, buttonsY - S(28), iw, _libraryNotice, UiTheme.MEASURE_TEXT, maxLines: 1); }
            _ui.Text(f.Small, ix + S(176), buttonsY + S(14), "Pick a card: it appears in front of you on the Place gun (9) · RMB commits · Esc discards", UiTheme.TEXT_MUTED);
            if (MenuButton(f, ix, buttonsY, S(160), "CLOSE", false, false, height: S(44))) { CloseLibrary(); return; }

            // Families on the left, cards on the right
            float listW = MathF.Min(S(240), iw * 0.24f);
            float bottom = buttonsY - S(40);
            BuildFamilyList(f, input, ix, cy, listW, bottom);
            LibraryEntry picked = BuildLibraryCards(f, input, ix + listW + S(20), cy, iw - listW - S(20), bottom);
            if (picked != null) { PickFromLibrary(picked); }
        }

        /// <summary>
        /// One category chip (wraps to a new line when the row is full); clicking it filters (again: all).
        /// </summary>
        /// <returns>The chip row's top (moves down when it wrapped).</returns>
        private float CategoryChip(FontAtlas f, InputState input, ref float x, float y, float left, float width, float h, int category, string label)
        {
            float w = UiBatch.Measure(f.Small, label, S(0.6f)) + S(24);
            if (x > left && x + w > left + width)
            {
                x = left;
                y += h + S(8);
            }
            bool on = _libraryCategory == category;
            bool hover = Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, on ? UiTheme.PLACE : hover ? Rgba.Hex(0xFFFFFF, 0.08f) : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), on ? UiTheme.PLACE : UiTheme.CONTROL_BORDER);
            _ui.TextCentred(f.Small, x + w * 0.5f, y + h * 0.5f - f.Small.LineHeight * 0.5f, label, on ? Rgba.Hex(0x1F1300) : UiTheme.TEXT, S(0.6f));
            if (hover && input.LeftPressed)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
                _libraryCategory = on && category >= 0 ? -1 : category;
                _libraryFamily = null;
                _libraryScroll = _libraryFamilyScroll = 0;
                _libraryRowsDirty = true;
            }
            x += w + S(8);
            return y;
        }

        /// <summary>
        /// The family list (ALL FAMILIES, then each family of the current category and search); click to filter.
        /// </summary>
        private void BuildFamilyList(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            _ui.Panel(x, y, w, bottom - y, Rgba.Hex(0xFFFFFF, 0.02f), UiTheme.CARD_BORDER);
            float rowH = S(28);
            int visible = Math.Max(1, (int)((bottom - y - S(8)) / rowH));
            int total = _libraryFamilies.Count + 1;
            int maxScroll = Math.Max(0, total - visible);
            if (input.Wheel != 0 && Hover(input, x, y, w, bottom - y)) { _libraryFamilyScroll -= input.Wheel * 3; }
            _libraryFamilyScroll = Math.Clamp(_libraryFamilyScroll, 0, maxScroll);

            int last = Math.Min(total, _libraryFamilyScroll + visible);
            for (int i = _libraryFamilyScroll; i < last; i++)
            {
                string family = i == 0 ? null : _libraryFamilies[i - 1];
                float ry = y + S(4) + (i - _libraryFamilyScroll) * rowH;
                bool on = string.Equals(_libraryFamily, family, StringComparison.Ordinal);
                bool hover = Hover(input, x + S(4), ry, w - S(8), rowH - S(2));
                if (on || hover) { _ui.Rect(x + S(4), ry, w - S(8), rowH - S(2), on ? Rgba.WithAlpha(UiTheme.PLACE, 0.25f) : Rgba.Hex(0xFFFFFF, 0.06f)); }
                _ui.TextWrapped(f.Body, x + S(12), ry + rowH * 0.5f - f.Body.LineHeight * 0.5f - S(1), w - S(24), family ?? "All families", on ? UiTheme.PLACE_LABEL : UiTheme.TEXT, maxLines: 1);
                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    Sound.Play(SoundId.UiClick);
                    _libraryFamily = family;
                    _libraryScroll = 0;
                    _libraryRowsDirty = true;
                }
            }
        }

        /// <summary>
        /// The grid of cards: preview, type, family and status (placeable: how many are in the model; listed only:
        /// why it can't be placed yet, greyed). Wheel scrolls.
        /// </summary>
        /// <returns>The entry clicked this frame, or null.</returns>
        private LibraryEntry BuildLibraryCards(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            LibraryEntry[] entries = Scene.Library.Entries;
            if (_libraryRows.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y + S(8), w, "Nothing matches. Clear the search (Backspace) or pick ALL.", UiTheme.TEXT_MUTED, maxLines: 2);
                return null;
            }

            float cardW = S(168), cardH = S(214), gap = S(12);
            int columns = Math.Max(1, (int)((w + gap) / (cardW + gap)));
            int rowsVisible = Math.Max(1, (int)((bottom - y + gap) / (cardH + gap)));
            int rowCount = (_libraryRows.Count + columns - 1) / columns;
            int maxScroll = Math.Max(0, rowCount - rowsVisible);
            if (input.Wheel != 0 && Hover(input, x, y, w, bottom - y)) { _libraryScroll -= input.Wheel; }
            _libraryScroll = Math.Clamp(_libraryScroll, 0, maxScroll);

            LibraryEntry picked = null;
            int first = _libraryScroll * columns, last = Math.Min(_libraryRows.Count, first + rowsVisible * columns);
            for (int i = first; i < last; i++)
            {
                LibraryEntry entry = entries[_libraryRows[i]];
                int slot = i - first;
                float cx = x + (slot % columns) * (cardW + gap), cyy = y + (slot / columns) * (cardH + gap);
                bool usable = entry.Placeable && entry.Element >= 0;
                bool hover = usable && Hover(input, cx, cyy, cardW, cardH);
                _ui.Panel(cx, cyy, cardW, cardH, hover ? Rgba.Hex(0xFFFFFF, 0.07f) : UiTheme.CONTROL, hover ? UiTheme.PLACE : UiTheme.CONTROL_BORDER);

                // Preview (Revit's own, on a light tile like Revit's browser)
                float pad = S(10), image = cardW - pad * 2f;
                _ui.Rect(cx + pad, cyy + pad, image, image, Rgba.Hex(0xF4F5F7, usable ? 1f : 0.35f));
                uint texture = PreviewTexture(entry);
                if (texture != 0) { _ui.Image(texture, cx + pad, cyy + pad, image, image, _window.Width, _window.Height, usable ? 0xFFFFFFFF : Rgba.Hex(0xFFFFFF, 0.4f)); }
                else { _ui.TextCentred(f.Small, cx + cardW * 0.5f, cyy + pad + image * 0.5f - S(7), "NO PREVIEW", Rgba.Hex(0x6B7280), S(0.4f)); }

                float ty = cyy + pad + image + S(6);
                _ui.TextWrapped(f.Bold, cx + pad, ty, image, entry.Type, usable ? UiTheme.TEXT : UiTheme.TEXT_FAINT, maxLines: 1);
                _ui.TextWrapped(f.Small, cx + pad, ty + S(20), image, entry.Family, UiTheme.TEXT_MUTED, maxLines: 1);
                if (usable)
                {
                    Text.Clear();
                    if (entry.Placed > 0) { Text.AppendGrouped(entry.Placed).Append(" in the model"); }
                    else { Text.Append("Not placed in the model yet"); }
                    _ui.TextWrapped(f.Small, cx + pad, ty + S(38), image, Text.Span, UiTheme.PLACE_LABEL, maxLines: 1);
                }
                else
                {
                    _ui.TextWrapped(f.Small, cx + pad, ty + S(38), image, entry.Reason ?? "Not placeable", UiTheme.TEXT_FAINT, maxLines: 1);
                }

                if (hover && input.LeftPressed)
                {
                    input.ConsumeClicks();
                    Sound.Play(SoundId.UiClick);
                    picked = entry;
                }
                else if (!usable && Hover(input, cx, cyy, cardW, cardH) && !ReferenceEquals(_libraryNoticeFor, entry))
                {
                    _libraryNoticeFor = entry;
                    _libraryNotice = $"{entry.Label}: {entry.Reason ?? "not placeable"}";
                }
            }

            if (maxScroll > 0)
            {
                Text.Clear().Append(first + 1).Append('–').Append(last).Append(" of ").Append(_libraryRows.Count).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(6), Text.Span, UiTheme.TEXT_FAINT);
            }
            return picked;
        }

        /// <summary>
        /// Typed characters go into the search: printable ones are added, Backspace deletes (Ctrl+Backspace clears).
        /// </summary>
        private void TypeIntoSearch(InputState input)
        {
            foreach (char c in input.Chars)
            {
                if (c == '\b')
                {
                    if (input.IsDown(Win32.VK_CONTROL)) { _librarySearchLength = 0; }
                    else if (_librarySearchLength > 0) { _librarySearchLength--; }
                    _libraryScroll = 0;
                    _libraryRowsDirty = true;
                }
                else if (c >= ' ' && c != 127 && _librarySearchLength < LIBRARY_SEARCH_MAX)
                {
                    _librarySearch[_librarySearchLength++] = c;
                    _libraryScroll = 0;
                    _libraryRowsDirty = true;
                }
            }
        }

        /// <summary>
        /// Rebuilds the filtered rows, the family list and the category list when the search or a filter changed.
        /// </summary>
        private void RefreshLibraryRows()
        {
            if (!_libraryRowsDirty) { return; }
            _libraryRowsDirty = false;
            string search = new string(_librarySearch, 0, _librarySearchLength).Trim();

            LibraryEntry[] entries = Scene.Library.Entries;
            string[] words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _libraryRows.Clear();
            var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var categories = new SortedSet<int>();
            for (int i = 0; i < entries.Length; i++)
            {
                LibraryEntry entry = entries[i];
                if (_libraryPlaceableOnly && !(entry.Placeable && entry.Element >= 0)) { continue; }
                if (!Matches(entry, words)) { continue; }
                if (entry.CategoryIndex >= 0) { categories.Add(entry.CategoryIndex); }
                if (_libraryCategory >= 0 && entry.CategoryIndex != _libraryCategory) { continue; }
                families.Add(entry.Family);
                if (_libraryFamily != null && !string.Equals(entry.Family, _libraryFamily, StringComparison.Ordinal)) { continue; }
                _libraryRows.Add(i);
            }
            _libraryFamilies.Clear();
            _libraryFamilies.AddRange(families);
            _libraryCategories.Clear();
            _libraryCategories.AddRange(categories);
            _libraryCategoryLabels.Clear();
            foreach (int category in categories) { _libraryCategoryLabels.Add(CategoryCatalog.All[category].Label.ToUpperInvariant()); }
            if (_libraryFamily != null && !families.Contains(_libraryFamily)) { _libraryFamily = null; }
        }

        /// <summary>Every search word appears in the family, type or category name.</summary>
        private static bool Matches(LibraryEntry entry, string[] words)
        {
            foreach (string word in words)
            {
                string category = entry.CategoryIndex >= 0 ? CategoryCatalog.All[entry.CategoryIndex].Label : entry.Category;
                if (entry.Family.Contains(word, StringComparison.CurrentCultureIgnoreCase)) { continue; }
                if (entry.Type.Contains(word, StringComparison.CurrentCultureIgnoreCase)) { continue; }
                if (category != null && category.Contains(word, StringComparison.CurrentCultureIgnoreCase)) { continue; }
                return false;
            }
            return true;
        }

        #endregion

        #region Previews

        /// <summary>
        /// The GL texture of an entry's preview (decoded on first sight, a few per frame), or 0.
        /// </summary>
        public unsafe uint PreviewTexture(LibraryEntry entry)
        {
            string name = entry?.Preview;
            if (name == null) { return 0; }
            if (_previewTextures.TryGetValue(name, out uint cached)) { return cached; }
            if (_previewDecodes >= PREVIEW_DECODES_PER_FRAME || !Scene.Library.Previews.TryGetValue(name, out byte[] png)) { return 0; }
            _previewDecodes++;

            uint texture = 0;
            try
            {
                using var stream = new MemoryStream(png);
                using var decoded = new System.Drawing.Bitmap(stream);
                using var bitmap = new System.Drawing.Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.Clear(System.Drawing.Color.FromArgb(0xF4, 0xF5, 0xF7));
                    graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
                }

                System.Drawing.Imaging.BitmapData bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    texture = Gl.GenTexture();
                    Gl.BindTexture(Gl.TEXTURE_2D, texture);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
                    Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
                    Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
                    Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, bitmap.Width, bitmap.Height, Gl.BGRA, Gl.UNSIGNED_BYTE, (void*)bits.Scan0);
                    Gl.BindTexture(Gl.TEXTURE_2D, 0);
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Family library preview unreadable ({entry.Label}): {ex.Message}");
                if (texture != 0) { Gl.DeleteTexture(texture); }
                texture = 0;
            }
            _previewTextures[name] = texture;
            return texture;
        }

        /// <summary>
        /// Frees every preview texture (session end).
        /// </summary>
        private void ReleaseLibraryPreviews()
        {
            foreach (uint texture in _previewTextures.Values)
            {
                if (texture != 0) { Gl.DeleteTexture(texture); }
            }
            _previewTextures.Clear();
        }

        #endregion
    }
}
