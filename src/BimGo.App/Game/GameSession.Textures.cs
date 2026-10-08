using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The pause menu's Textures panel (Realistic mode, no Revit needed): the materials whose image is missing or that
    /// are drawn with a proxy, and per material PICK IMAGE / PROXY / PLAIN / UNDO. FIND IN FOLDER deep-scans a folder
    /// for the missing images one stage at a time (exact name → other extension → loose name; exact hits ticked,
    /// loose ones ticked by hand).
    /// <list type="bullet">
    /// <item>Changes go into a new <see cref="MaterialData"/> (<see cref="MaterialData.With"/>: the per-vertex streams
    /// are shared, only the table and images change) and the renderer rebuilds its table and arrays.</item>
    /// <item>File mode: the document becomes dirty and Save embeds the picked images in the .bimgo.</item>
    /// <item>Live session: the same choices also go to the model's override file, so the next Go / F5 in Revit
    /// uses them.</item>
    /// </list>
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private MaterialData _materialData;
        private int _materialsRevision, _savedMaterialsRevision;
        private readonly HashSet<int> _changedMaterials = new();

        private bool _texturesOpen;
        private int _textureScroll;
        private int _textureFilter;                 // 0 missing, 1 proxy, 2 all
        private string _texturesNotice;
        private int _proxyPickerFor = -1;           // material index whose proxy is being picked
        private string _proxyPickerTitle;
        private string[] _proxyPickerLabels;
        private readonly List<int> _textureRows = new();
        private int _textureRowsFor = -1;           // (filter, revision) the rows were built for
        private readonly string[] _textureFilterLabels = new string[3];
        private int _textureLabelsFor = -1;
        private string _texturesMenuLabel;
        private int _texturesMenuLabelFor = -1;

        // Per-material display cache (rebuilt when the materials or the proxy setting change, not per frame)
        private string[] _textureProxy = Array.Empty<string>();
        private string[] _textureStatus = Array.Empty<string>();
        private uint[] _textureStatusColour = Array.Empty<uint>();
        private string[] _textureDetail = Array.Empty<string>();
        private int _textureCacheFor = -1;
        private int _textureMissing, _textureProxied;


        // Deep scan
        private TextureFolderIndex _scanIndex;
        private TextureMatchStage _scanStage;
        private List<string> _scanRemaining = new();
        private List<ScanRow> _scanRows;
        private bool _scanRemember;
        private int _scanScroll, _scanAccepted;

        /// <summary>One deep-scan hit.</summary>
        private sealed class ScanRow
        {
            public TextureSearchResult Result;
            public int Choice;
            public bool Accepted;
            public int Materials;
            public string Name;
            public string Stage;

            public string Selected => Result.Candidates.Count == 0 ? null : Result.Candidates[Math.Clamp(Choice, 0, Result.Candidates.Count - 1)];
        }

        #endregion

        #region State

        /// <summary>The materials as drawn now (the snapshot's, or as changed in the Textures panel).</summary>
        private MaterialData CurrentMaterials => _materialData ?? Scene.Materials;

        /// <summary>True once the Textures panel changed a material (Save writes the changed set).</summary>
        private bool MaterialsChanged => _materialsRevision != 0;

        /// <summary>True if the snapshot has materials (the TEXTURES button shows).</summary>
        private bool HasTexturePanel => Scene.Materials != null && !Scene.Materials.IsEmpty;

        /// <summary>True while the Textures panel is showing (it replaces the pause menu).</summary>
        private bool IsTexturesPanelOpen => _texturesOpen;

        /// <summary>
        /// Called once the renderer is up: the starting material set and a toast about missing images.
        /// </summary>
        private void InitialiseTextures()
        {
            _materialData = Scene.Materials;
            if (!HasTexturePanel || !_realistic) { return; }
            int missing = MissingCount();
            if (missing > 0 && _renderer.MaterialWarning == null)
            {
                Toast($"{missing} material{(missing == 1 ? " is" : "s are")} missing an image: Esc → TEXTURES to find them.", 5f);
            }
        }

        private static bool IsMissing(SceneMaterial m) => m.Texture == null && m.TextureState is TextureState.Missing or TextureState.Unreadable;

        /// <summary>
        /// Rebuilds the per-material proxy, status and detail strings and the counts when something changed.
        /// </summary>
        private void RefreshTextureCache()
        {
            int stamp = _materialsRevision * 2 + (_proxyMissing ? 1 : 0);
            if (stamp == _textureCacheFor) { return; }
            _textureCacheFor = stamp;
            SceneMaterial[] table = CurrentMaterials.Materials;
            int n = table.Length;
            if (_textureProxy.Length != n)
            {
                _textureProxy = new string[n];
                _textureStatus = new string[n];
                _textureStatusColour = new uint[n];
                _textureDetail = new string[n];
            }
            _textureMissing = _textureProxied = 0;
            for (int i = 0; i < n; i++)
            {
                SceneMaterial m = table[i];
                _textureProxy[i] = ProxyPack.EffectiveProxy(m, CurrentMaterials, _proxyMissing);
                _textureStatus[i] = StatusOf(i, m, _textureProxy[i], out _textureStatusColour[i]);
                _textureDetail[i] = DetailOf(m);
                if (IsMissing(m)) { _textureMissing++; }
                if (_textureProxy[i] != null) { _textureProxied++; }
            }
        }

        /// <summary>Materials missing an image and not covered by a proxy.</summary>
        private int MissingCount()
        {
            RefreshTextureCache();
            int count = 0;
            SceneMaterial[] table = CurrentMaterials.Materials;
            for (int i = 0; i < table.Length; i++)
            {
                if (IsMissing(table[i]) && _textureProxy[i] == null) { count++; }
            }
            return count;
        }

        /// <summary>"TEXTURES (n MISSING)" (rebuilt only when the count changes).</summary>
        private string TexturesMenuLabel()
        {
            int stamp = _materialsRevision * 2 + (_proxyMissing ? 1 : 0);
            if (_texturesMenuLabel == null || stamp != _texturesMenuLabelFor)
            {
                _texturesMenuLabelFor = stamp;
                int missing = MissingCount();
                _texturesMenuLabel = missing == 0 ? "TEXTURES" : $"TEXTURES ({missing} MISSING)";
            }
            return _texturesMenuLabel;
        }

        private void OpenTextures()
        {
            _texturesOpen = true;
            _textureScroll = 0;
            _proxyPickerFor = -1;
            _texturesNotice = null;
            _textureRowsFor = -1;
        }

        /// <summary>
        /// Esc: closes the proxy picker, then the scan, then the panel.
        /// </summary>
        /// <returns>True if something was open.</returns>
        private bool CloseTextures()
        {
            if (!_texturesOpen) { return false; }
            if (_proxyPickerFor >= 0) { _proxyPickerFor = -1; return true; }
            if (_scanRows != null) { EndScan(remember: false); return true; }
            _texturesOpen = false;
            return true;
        }

        /// <summary>
        /// The rows for the current filter (rebuilt only when the filter or the materials change).
        /// </summary>
        private List<int> TextureRows()
        {
            int stamp = (_materialsRevision * 3 + _textureFilter) * 2 + (_proxyMissing ? 1 : 0);
            if (stamp == _textureRowsFor) { return _textureRows; }
            _textureRowsFor = stamp;
            RefreshTextureCache();
            _textureRows.Clear();
            SceneMaterial[] table = CurrentMaterials.Materials;
            for (int i = 0; i < table.Length; i++)
            {
                SceneMaterial m = table[i];
                bool include = _textureFilter switch
                {
                    0 => IsMissing(m) || _changedMaterials.Contains(i),
                    1 => _textureProxy[i] != null,
                    _ => true
                };
                if (include) { _textureRows.Add(i); }
            }
            _textureRows.Sort((a, b) =>
            {
                int c = IsMissing(table[b]).CompareTo(IsMissing(table[a]));
                return c != 0 ? c : string.Compare(table[a].Name, table[b].Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return _textureRows;
        }

        #endregion

        #region Panel

        /// <summary>
        /// Draws and handles the Textures panel (in place of the pause menu).
        /// </summary>
        private void BuildTexturesPanel()
        {
            FontAtlas f = _ui.Atlas;
            InputState input = _window.Input;
            int width = _window.Width, height = _window.Height;
            _ui.Rect(0, 0, width, height, UiTheme.MENU_BACKGROUND);

            float w = MathF.Min(S(1080), width - S(80));
            float h = height - S(96);
            float x = (width - w) * 0.5f, y = S(48);
            _ui.Panel(x, y, w, h, UiTheme.CARD, UiTheme.CARD_BORDER);

            float ix = x + S(24), iw = w - S(48);
            float cy = y + S(20);
            _ui.Text(f.Small, ix, cy, "TEXTURES · REALISTIC MODE", UiTheme.ACCENT, S(2f));
            RefreshTextureCache();
            Text.Clear().AppendGrouped(CurrentMaterials.Materials.Length).Append(" materials · ").Append(_textureMissing).Append(" missing an image · proxy pack: ")
                .Append(ProxyPack.Shared.Count).Append(" textures");
            _ui.TextRight(f.Small, ix + iw, cy, Text.Span, UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(34);

            // Display options for the Realistic mode
            float colW = MathF.Min(S(360), iw * 0.4f);
            bool tint = Checkbox(f, input, ix, cy + S(4), colW, "Apply Revit tint", _tintMode != TintMode.Off);
            _tintMode = tint ? TintMode.Multiply : TintMode.Off;
            _ui.TextWrapped(f.Small, ix + S(26), cy + S(30), colW - S(26), "As Revit's Realistic view (off: images untinted)", UiTheme.TEXT_MUTED, maxLines: 1);
            float ox = ix + colW + S(32), ow = iw - colW - S(32);
            bool proxies = Checkbox(f, input, ox, cy + S(4), ow, "Proxy textures for missing images", _proxyMissing);
            bool proxyColour = Checkbox(f, input, ox, cy + S(32), ow, "Proxies take the material's colour", _proxyMaterialColour);
            if (proxies != _proxyMissing || proxyColour != _proxyMaterialColour)
            {
                _proxyMissing = proxies;
                _proxyMaterialColour = proxyColour;
                _renderer.AutoProxy = _proxyMissing;
                _renderer.ProxyMaterialColour = _proxyMaterialColour;
                ReloadRendererMaterials();
                _textureRowsFor = -1;
                _texturesMenuLabelFor = -1;
            }
            cy += S(70);

            float buttonsY = y + h - S(24) - S(48);
            if (_scanRows != null)
            {
                BuildScanRows(f, input, ix, cy, iw, buttonsY - S(16));
                BuildScanButtons(f, input, ix, buttonsY, iw);
                return;
            }

            // Filter
            int missing = _textureMissing, proxied = _textureProxied;
            int labelStamp = missing * 100003 + proxied * 7 + CurrentMaterials.Materials.Length;
            if (labelStamp != _textureLabelsFor)
            {
                _textureLabelsFor = labelStamp;
                _textureFilterLabels[0] = $"Missing ({missing})";
                _textureFilterLabels[1] = $"Proxy ({proxied})";
                _textureFilterLabels[2] = $"All ({CurrentMaterials.Materials.Length})";
            }
            int filter = Segmented(f, input, ix, cy, MathF.Min(S(420), iw), _textureFilterLabels, _textureFilter);
            if (filter != _textureFilter)
            {
                _textureFilter = filter;
                _textureScroll = 0;
                _proxyPickerFor = -1;
            }
            cy += S(46);

            if (_proxyPickerFor >= 0) { BuildProxyPicker(f, input, ix, cy, iw, buttonsY - S(16)); }
            else { BuildTextureRows(f, input, ix, cy, iw, buttonsY - S(16)); }

            if (_texturesNotice != null) { _ui.TextWrapped(f.Body, ix, buttonsY - S(30), iw, _texturesNotice, UiTheme.MEASURE_TEXT, maxLines: 1); }
            if (MenuButton(f, ix, buttonsY, S(260), "FIND IN FOLDER…", false, false)) { StartScan(); }
            if (MenuButton(f, ix + S(276), buttonsY, S(160), "CLOSE", false, false)) { _texturesOpen = false; }
            _ui.TextRight(f.Small, ix + iw, buttonsY + S(18), IsFileMode
                ? (IsDirty ? "Save (Ctrl+S) embeds the picked images in the file" : "Picked images are embedded in the file when you save")
                : "Choices are remembered for this model: the next Go in Revit uses them", UiTheme.TEXT_FAINT, S(0.4f));
        }

        /// <summary>
        /// The scrolling material rows: swatch, name, status, image path and IMAGE / PROXY / PLAIN / UNDO.
        /// </summary>
        private void BuildTextureRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            List<int> rows = TextureRows();
            SceneMaterial[] table = CurrentMaterials.Materials;
            float rowH = S(52);
            int visible = Math.Max(1, (int)((bottom - y) / rowH));
            if (rows.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y + S(8), w, _textureFilter == 0
                    ? "No material is missing its image."
                    : _textureFilter == 1 ? "No material is drawn with a proxy." : "No materials.", UiTheme.TEXT_MUTED, maxLines: 2);
                return;
            }

            int maxScroll = Math.Max(0, rows.Count - visible);
            if (input.Wheel != 0) { _textureScroll -= input.Wheel * 2; }
            _textureScroll = Math.Clamp(_textureScroll, 0, maxScroll);

            int pick = -1, proxy = -1, plain = -1, undo = -1;
            float wide = S(84), gap = S(6);
            float buttonsW = wide * 4 + gap * 3;
            int last = Math.Min(rows.Count, _textureScroll + visible);
            for (int r = _textureScroll; r < last; r++)
            {
                int index = rows[r];
                SceneMaterial m = table[index];
                float ry = y + (r - _textureScroll) * rowH;
                if (((r - _textureScroll) & 1) == 0) { _ui.Rect(x - S(8), ry - S(4), w + S(16), rowH - S(4), Rgba.Hex(0xFFFFFF, 0.03f)); }

                // Swatch in the colour drawn under (or instead of) the image
                float sw = S(36);
                _ui.Rect(x, ry, sw, sw, PackColour(m.Colour));
                _ui.Outline(x, ry, sw, sw, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.15f));

                float textX = x + sw + S(12), textW = w - buttonsW - sw - S(36);
                _ui.TextWrapped(f.Bold, textX, ry + S(2), textW * 0.55f, m.Name, UiTheme.TEXT, maxLines: 1);
                _ui.TextWrapped(f.Small, textX + textW * 0.57f, ry + S(5), textW * 0.43f, _textureStatus[index], _textureStatusColour[index], maxLines: 1);
                _ui.TextWrapped(f.Small, textX, ry + S(24), textW, _textureDetail[index], UiTheme.TEXT_MUTED, maxLines: 1);

                float bx = x + w - buttonsW, by = ry + S(4), bh = S(30);
                if (SmallButton(f, input, bx, by, wide, bh, "IMAGE…")) { pick = index; }
                bx += wide + gap;
                if (SmallButton(f, input, bx, by, wide, bh, "PROXY")) { proxy = index; }
                bx += wide + gap;
                if (SmallButton(f, input, bx, by, wide, bh, "PLAIN")) { plain = index; }
                bx += wide + gap;
                if (_changedMaterials.Contains(index) && SmallButton(f, input, bx, by, wide, bh, "UNDO")) { undo = index; }
            }

            if (maxScroll > 0)
            {
                Text.Clear().Append(_textureScroll + 1).Append('–').Append(last).Append(" of ").Append(rows.Count).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(2), Text.Span, UiTheme.TEXT_FAINT);
            }

            // Act after drawing (the rows must not change while they are being walked)
            if (pick >= 0) { PickImage(pick); }
            else if (proxy >= 0) { OpenProxyPicker(proxy); }
            else if (plain >= 0) { ApplyChoice(new[] { plain }, TextureOverride.ForColourOnly(table[plain].Name)); }
            else if (undo >= 0) { UndoMaterial(undo); }
        }

        private string StatusOf(int index, SceneMaterial m, string proxy, out uint colour)
        {
            bool yours = _changedMaterials.Contains(index) || m.TextureOrigin == TextureOrigins.OVERRIDE;
            colour = yours ? UiTheme.ACCENT : UiTheme.TEXT_SOFT;
            if (m.Texture != null)
            {
                colour = yours ? UiTheme.ACCENT : UiTheme.GOOD;
                return m.TextureOrigin switch
                {
                    TextureOrigins.OVERRIDE => "Your image",
                    TextureOrigins.SEARCH => "Found by search",
                    _ => "Image"
                };
            }
            if (proxy != null)
            {
                if (!yours) { colour = UiTheme.MEASURE_TEXT; }
                string label = ProxyCatalog.Find(proxy)?.Label ?? proxy;
                return ProxyPack.Shared.Has(proxy) ? $"Proxy: {label}" : $"Proxy: {label} (not in this app's pack)";
            }
            if (IsMissing(m))
            {
                colour = UiTheme.DANGER;
                return m.TextureState == TextureState.Unreadable ? "Unreadable → colour" : "Missing → colour";
            }
            return m.TextureOrigin == TextureOrigins.OVERRIDE ? "Plain colour (yours)" : m.TextureState == TextureState.Procedural ? "Procedural → colour" : "Plain colour";
        }

        private static uint PackColour(Vector3 c)
        {
            uint r = (uint)Math.Clamp((int)(c.X * 255f + 0.5f), 0, 255);
            uint g = (uint)Math.Clamp((int)(c.Y * 255f + 0.5f), 0, 255);
            uint b = (uint)Math.Clamp((int)(c.Z * 255f + 0.5f), 0, 255);
            return r | (g << 8) | (b << 16) | 0xFF000000u;
        }

        private static string DetailOf(SceneMaterial m)
        {
            string schema = string.IsNullOrEmpty(m.Schema) ? "no appearance" : m.Schema.EndsWith("Schema", StringComparison.Ordinal) ? m.Schema[..^6] : m.Schema;
            string source = string.IsNullOrWhiteSpace(m.TextureSource) ? "no image in Revit" : m.TextureSource;
            return $"{schema} · {source}";
        }

        /// <summary>
        /// The proxy keyword grid for one material (suggested keyword first).
        /// </summary>
        private void BuildProxyPicker(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            SceneMaterial m = CurrentMaterials.Materials[_proxyPickerFor];
            _ui.TextWrapped(f.Bold, x, y, w, _proxyPickerTitle, UiTheme.TEXT, maxLines: 1);

            int columns = 4;
            float gap = S(8), bw = (w - gap * (columns - 1)) / columns, bh = S(34);
            float gy = y + S(34);
            string chosen = null;
            for (int i = 0; i < ProxyCatalog.ALL.Count; i++)
            {
                ProxyKeyword keyword = ProxyCatalog.ALL[i];
                float bx = x + (i % columns) * (bw + gap), by = gy + (i / columns) * (bh + gap);
                if (by + bh > bottom) { break; }
                if (SmallButton(f, input, bx, by, bw, bh, _proxyPickerLabels[i])) { chosen = keyword.Keyword; }
            }
            if (MenuButton(f, x, bottom - S(48), S(160), "CANCEL", false, false)) { _proxyPickerFor = -1; return; }
            if (chosen != null)
            {
                int index = _proxyPickerFor;
                _proxyPickerFor = -1;
                ApplyChoice(new[] { index }, TextureOverride.ForProxy(chosen, m.Name));
            }
        }

        /// <summary>
        /// Opens the keyword grid for a material (labels built once here, not per frame).
        /// </summary>
        private void OpenProxyPicker(int index)
        {
            SceneMaterial m = CurrentMaterials.Materials[index];
            string suggested = ProxyCatalog.Suggest(m.Name, m.Schema);
            _proxyPickerTitle = $"Proxy for “{m.Name}”{(suggested != null ? $" · suggested: {ProxyCatalog.Find(suggested)?.Label ?? suggested}" : string.Empty)}";
            _proxyPickerLabels = ProxyCatalog.ALL.Select(k =>
                ((k.Keyword == suggested ? "» " : string.Empty) + k.Label + (ProxyPack.Shared.Has(k.Keyword) ? string.Empty : " (no image)")).ToUpperInvariant()).ToArray();
            _proxyPickerFor = index;
        }

        #endregion

        #region Applying choices

        private void PickImage(int index)
        {
            SceneMaterial m = CurrentMaterials.Materials[index];
            string path = FileDialogs.ShowOpen(_window.Handle, $"Image for “{m.Name}”",
                "Images (*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.gif|All files (*.*)|*.*", null);
            _window.Input.ReleaseAll();
            if (path == null) { return; }
            ApplyImages(new Dictionary<int, string> { [index] = path });
        }

        /// <summary>
        /// Embeds images (one per material; an image used by several is encoded once) and applies them.
        /// </summary>
        /// <returns>How many materials were given an image.</returns>
        private int ApplyImages(Dictionary<int, string> images)
        {
            if (images.Count == 0) { return 0; }
            int cap = CurrentMaterials.TextureMaxSize;
            var encoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var failed = new List<string>();

            // Encoding is the slow part: a progress screen when there is more than a couple
            List<string> paths = images.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var progress = new Utilities.OperationProgress();
            progress.Begin("Reading the images", 0.0, 1.0);
            Dictionary<string, byte[]> byPath;
            try
            {
                byPath = paths.Count <= 2
                    ? EncodeAll(paths, cap, failed, null)
                    : ProgressScreen.Run(_window, _ui, $"Embedding {paths.Count} images", progress, () => EncodeAll(paths, cap, failed, progress));
            }
            catch (OperationCanceledException)
            {
                _window.Input.ReleaseAll();
                _texturesNotice = "Cancelled: no images were added.";
                return 0;
            }
            _window.Input.ReleaseAll();

            SceneMaterial[] table = CopyTable();
            var applied = new List<int>();
            foreach ((int index, string path) in images)
            {
                if (!byPath.TryGetValue(path, out byte[] bytes) || bytes == null) { continue; }
                string entry = TextureEncoder.EntryName(path, cap);
                encoded[entry] = bytes;
                SceneMaterial m = table[index];
                m.Texture = entry;
                m.TextureState = TextureState.Embedded;
                m.TextureOrigin = TextureOrigins.OVERRIDE;
                m.Proxy = null;
                if (m.RenderColour is Vector3 render)
                {
                    m.Colour = render; // the appearance's own base colour again, now that there is an image over it
                    m.RenderColour = null;
                }
                applied.Add(index);
            }

            if (applied.Count > 0)
            {
                Commit(table, encoded, applied, i => TextureOverride.ForImage(images[i], table[i].Name));
            }
            _texturesNotice = failed.Count == 0
                ? $"{applied.Count} material{(applied.Count == 1 ? "" : "s")} given an image"
                : $"{applied.Count} given an image; {failed.Count} couldn't be read: {string.Join(", ", failed.Select(p => Path.GetFileName(p)).Take(3))}";
            Sound.Play(failed.Count == 0 ? SoundId.Commit : SoundId.Error);
            return applied.Count;
        }

        private static Dictionary<string, byte[]> EncodeAll(List<string> paths, int cap, List<string> failed, Utilities.OperationProgress progress)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                progress?.ThrowIfCancelled();
                progress?.Step(i, paths.Count);
                progress?.Detail(Path.GetFileName(paths[i]));
                byte[] bytes = TextureEncoder.Encode(paths[i], cap, out _);
                if (bytes == null) { lock (failed) { failed.Add(paths[i]); } }
                result[paths[i]] = bytes;
            }
            return result;
        }

        /// <summary>
        /// A proxy or plain colour for materials.
        /// </summary>
        private void ApplyChoice(IReadOnlyList<int> indices, TextureOverride choice)
        {
            SceneMaterial[] table = CopyTable();
            foreach (int index in indices)
            {
                SceneMaterial m = table[index];
                if (m.Texture != null)
                {
                    m.Texture = null;
                    m.TextureState = m.TextureSource == null ? TextureState.None : TextureState.Missing;
                }
                if (choice.ColourOnly)
                {
                    m.Proxy = null;
                    m.TextureOrigin = TextureOrigins.OVERRIDE;
                }
                else
                {
                    m.Proxy = ProxyCatalog.Normalise(choice.Proxy);
                    m.TextureOrigin = TextureOrigins.PROXY;
                }
            }
            Commit(table, null, indices, i => choice.ColourOnly ? TextureOverride.ForColourOnly(table[i].Name) : TextureOverride.ForProxy(choice.Proxy, table[i].Name));
            _texturesNotice = choice.ColourOnly ? "Plain colour" : $"Proxy: {ProxyCatalog.Find(choice.Proxy)?.Label ?? choice.Proxy}";
            Sound.Play(SoundId.Commit);
        }

        /// <summary>
        /// Puts a material back as it came in the snapshot (and forgets its override).
        /// </summary>
        private void UndoMaterial(int index)
        {
            SceneMaterial[] table = CopyTable();
            table[index] = Scene.Materials.Materials[index].Clean();
            _changedMaterials.Remove(index);
            Commit(table, null, new[] { index }, _ => null, keepChanged: true);
            _texturesNotice = $"“{table[index].Name}” is back as it came from Revit";
            Sound.Play(SoundId.UiClick);
        }

        private SceneMaterial[] CopyTable()
        {
            SceneMaterial[] source = CurrentMaterials.Materials;
            var copy = new SceneMaterial[source.Length];
            for (int i = 0; i < source.Length; i++) { copy[i] = source[i].Clean(); }
            return copy;
        }

        /// <summary>
        /// Makes a changed table current: the renderer rebuilds, the file becomes dirty, and in a live session the
        /// choices go to the model's override file.
        /// </summary>
        private void Commit(SceneMaterial[] table, Dictionary<string, byte[]> added, IEnumerable<int> changed, Func<int, TextureOverride> overrideFor, bool keepChanged = false)
        {
            List<int> indices = changed.ToList();
            _materialData = CurrentMaterials.With(table, added);
            if (!keepChanged) { foreach (int i in indices) { _changedMaterials.Add(i); } }
            _materialsRevision++;
            ReloadRendererMaterials();
            UpdateTitle();
            if (_live != null) { WriteOverrides(indices, overrideFor); }
        }

        private void ReloadRendererMaterials()
        {
            string warning = _renderer.ReloadMaterials(CurrentMaterials);
            if (warning != null) { Toast(warning, 5f); }
        }

        /// <summary>
        /// Live session: the same choices in the model's override file (read by the next extraction in Revit).
        /// </summary>
        private void WriteOverrides(IReadOnlyList<int> indices, Func<int, TextureOverride> overrideFor)
        {
            // The model's BimGo folder (where the comments sidecar is), else the older per-key file
            string hostKey = Scene.Provenance?.ModelKey;
            string modelFolder = ModelFolders.FolderOf(Scene.CommentsPath);
            if (modelFolder == null && string.IsNullOrWhiteSpace(hostKey))
            {
                _texturesNotice = "Applied here only: this snapshot has no model key, so Revit can't pick the choice up.";
                return;
            }
            TextureOverrideSet overrides = TextureOverrideSet.LoadFromModelFolder(modelFolder, hostKey);
            overrides.ModelTitle ??= Scene.ModelTitle;
            foreach (int i in indices)
            {
                SceneMaterial m = CurrentMaterials.Materials[i];
                overrides.Set(TextureOverrideSet.DocumentKeyOf(m, Scene.Links), m.UniqueId, m.Name, overrideFor(i));
            }
            if (!overrides.Save()) { _texturesNotice = "The choice is shown here but couldn't be saved for Revit (see the log)."; }
        }

        #endregion

        #region Deep scan

        /// <summary>
        /// Picks a folder, indexes it behind a progress screen and runs the first stage on the missing images.
        /// </summary>
        private void StartScan()
        {
            List<string> raws = CurrentMaterials.Materials
                .Where(m => m.Texture == null && m.TextureState == TextureState.Missing && m.TextureSource != null)
                .Select(m => m.TextureSource)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (raws.Count == 0)
            {
                _texturesNotice = "No material is missing its image.";
                return;
            }

            string folder = FileDialogs.ShowFolder(_window.Handle, $"Folder to search for {raws.Count} missing image{(raws.Count == 1 ? "" : "s")} (sub-folders included)", null);
            _window.Input.ReleaseAll();
            if (folder == null) { return; }

            var progress = new Utilities.OperationProgress();
            progress.Begin("Listing images", 0.0, 1.0);
            try
            {
                _scanIndex = ProgressScreen.Run(_window, _ui, $"Searching {folder}", progress, () => TextureFolderIndex.Build(folder, progress));
            }
            catch (OperationCanceledException)
            {
                _window.Input.ReleaseAll();
                _texturesNotice = "Search cancelled.";
                return;
            }
            _window.Input.ReleaseAll();
            Utilities.Log_Utils.Write($"Texture search of {folder}: {_scanIndex.FileCount} images in {_scanIndex.FolderCount} folders{(_scanIndex.Truncated ? " (capped)" : "")}.");

            _scanStage = TextureMatchStage.Exact;
            _scanRemaining = raws;
            _scanRemember = false;
            _scanAccepted = 0;
            RunScanStage();
        }

        private void RunScanStage()
        {
            IReadOnlyList<TextureSearchResult> results = TextureSearch.RunStage(_scanIndex, _scanRemaining, _scanStage);
            _scanRows = results.Where(r => r.Stage != TextureMatchStage.None).Select(r => new ScanRow
            {
                Result = r,
                Accepted = r.PreTicked,
                Materials = CurrentMaterials.Materials.Count(m => m.Texture == null && string.Equals(m.TextureSource, r.Raw, StringComparison.OrdinalIgnoreCase)),
                Name = TextureSearch.FileNamesOf(r.Raw).FirstOrDefault() ?? r.Raw,
                Stage = r.Stage switch
                {
                    TextureMatchStage.Exact => r.IsAmbiguous ? $"EXACT ×{r.Candidates.Count}" : "EXACT",
                    TextureMatchStage.Extension => r.IsAmbiguous ? $"EXT ×{r.Candidates.Count}" : "EXTENSION",
                    _ => "LOOSE"
                }
            }).ToList();
            _scanRemaining = results.Where(r => r.Stage == TextureMatchStage.None).Select(r => r.Raw).ToList();
            _scanScroll = 0;
        }

        private void BuildScanRows(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            string stage = _scanStage switch
            {
                TextureMatchStage.Exact => "STAGE 1 OF 3 · EXACT FILE NAMES",
                TextureMatchStage.Extension => "STAGE 2 OF 3 · SAME NAME, OTHER EXTENSION",
                _ => "STAGE 3 OF 3 · LOOSE NAMES"
            };
            _ui.Text(f.Bold, x, y, stage, UiTheme.TEXT, S(1f));
            Text.Clear().Append(_scanRows.Count).Append(" found · ").Append(_scanRemaining.Count).Append(" still missing · ").AppendGrouped(_scanIndex.FileCount).Append(" images in ").Append(_scanIndex.Folder);
            _ui.TextWrapped(f.Small, x, y + S(24), w, Text.Span, UiTheme.TEXT_MUTED, maxLines: 1);
            _ui.TextWrapped(f.Body, x, y + S(44), w, _scanStage == TextureMatchStage.Loose
                ? "Loose matches are proposals: tick the right ones. Bump, cutout and reflection maps are never offered."
                : "Exact matches are ticked. Where several files share a name, click the file to cycle through them, then tick it.", UiTheme.TEXT_SOFT, maxLines: 1);
            y += S(76);

            float rowH = S(30);
            int visible = Math.Max(1, (int)((bottom - y) / rowH));
            if (_scanRows.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y, w, "Nothing found at this stage.", UiTheme.TEXT_MUTED, maxLines: 1);
                return;
            }
            int maxScroll = Math.Max(0, _scanRows.Count - visible);
            if (input.Wheel != 0) { _scanScroll -= input.Wheel * 2; }
            _scanScroll = Math.Clamp(_scanScroll, 0, maxScroll);

            int last = Math.Min(_scanRows.Count, _scanScroll + visible);
            for (int i = _scanScroll; i < last; i++)
            {
                ScanRow row = _scanRows[i];
                float ry = y + (i - _scanScroll) * rowH;
                float nameW = w * 0.28f, stageW = S(110), countW = S(90);
                row.Accepted = Checkbox(f, input, x, ry + S(4), nameW, row.Name, row.Accepted);
                _ui.Text(f.Small, x + nameW + S(8), ry + S(6), row.Stage, row.Result.Stage == TextureMatchStage.Loose ? UiTheme.MEASURE_TEXT : UiTheme.GOOD, S(0.6f));

                float fileX = x + nameW + stageW, fileW = w - nameW - stageW - countW;
                bool hover = row.Result.Candidates.Count > 1 && Hover(input, fileX, ry, fileW, rowH - S(4));
                _ui.TextWrapped(f.Body, fileX, ry + S(4), fileW, row.Selected ?? "—", hover ? UiTheme.ACCENT : UiTheme.TEXT, maxLines: 1);
                if (hover && input.LeftPressed)
                {
                    row.Choice = (row.Choice + 1) % row.Result.Candidates.Count;
                    row.Accepted = true;
                    input.ConsumeClicks();
                    Sound.Play(SoundId.UiClick);
                }
                Text.Clear().Append(row.Materials).Append(row.Materials == 1 ? " material" : " materials");
                _ui.TextRight(f.Small, x + w, ry + S(6), Text.Span, UiTheme.TEXT_FAINT);
            }
            if (maxScroll > 0)
            {
                Text.Clear().Append(_scanScroll + 1).Append('–').Append(last).Append(" of ").Append(_scanRows.Count).Append(" · wheel to scroll");
                _ui.TextRight(f.Small, x + w, bottom + S(2), Text.Span, UiTheme.TEXT_FAINT);
            }
        }

        private void BuildScanButtons(FontAtlas f, InputState input, float x, float y, float w)
        {
            bool canNext = _scanStage < TextureMatchStage.Loose && _scanRemaining.Count > 0;
            if (MenuButton(f, x, y, S(280), "ACCEPT TICKED, NEXT STAGE", primary: canNext, danger: false, enabled: canNext))
            {
                AcceptScan();
                _scanStage++;
                RunScanStage();
                return;
            }
            if (MenuButton(f, x + S(296), y, S(240), "ACCEPT TICKED, DONE", primary: !canNext, danger: false))
            {
                AcceptScan();
                EndScan(_scanRemember);
                return;
            }
            if (MenuButton(f, x + S(552), y, S(130), "STOP", false, false)) { EndScan(remember: false); return; }
            _scanRemember = Checkbox(f, input, x + S(700), y + S(16), w - S(700), "Remember this folder for all models", _scanRemember);
        }

        /// <summary>
        /// Embeds the ticked hits for every material that is missing that image.
        /// </summary>
        private void AcceptScan()
        {
            var images = new Dictionary<int, string>();
            SceneMaterial[] table = CurrentMaterials.Materials;
            foreach (ScanRow row in _scanRows)
            {
                if (!row.Accepted || row.Selected == null) { continue; }
                for (int i = 0; i < table.Length; i++)
                {
                    if (table[i].Texture == null && string.Equals(table[i].TextureSource, row.Result.Raw, StringComparison.OrdinalIgnoreCase)) { images[i] = row.Selected; }
                }
            }
            _scanAccepted += ApplyImages(images);
        }

        private void EndScan(bool remember)
        {
            if (remember && _scanIndex != null)
            {
                LaunchSettings settings = LaunchSettings.LoadOrDefault();
                if (settings.AddTextureSearchFolder(_scanIndex.Folder)) { settings.Save(); }
            }
            if (_scanIndex != null)
            {
                _texturesNotice = $"{_scanAccepted} material{(_scanAccepted == 1 ? "" : "s")} given an image{(remember ? "; the folder is remembered for every model" : "")}";
                Utilities.Log_Utils.Write($"Texture search finished: {_scanAccepted} material(s) given an image{(remember ? "; folder remembered" : "")}.");
            }
            _scanIndex = null;
            _scanRows = null;
            _scanRemaining = new List<string>();
        }

        #endregion
    }
}
