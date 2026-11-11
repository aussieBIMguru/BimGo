using System.Globalization;
using BimGo.Native;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using SDT = System.Drawing.Text;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// One glyph's atlas rectangle and metrics (pixels).
    /// </summary>
    internal struct Glyph
    {
        public float U0, V0, U1, V1;
        public float Width, Height;
        public float Advance;
        public float OffsetX, OffsetY;
        public bool Valid;
    }

    /// <summary>
    /// A rasterised font inside the shared atlas. Latin-1 is baked at startup; every other character (Greek, Cyrillic,
    /// CJK, Hangul, Thai, symbols...) is rasterised the first time it is drawn, from the first installed Windows font
    /// that has it (see <see cref="FontAtlas"/>), and cached for the rest of the session.
    /// </summary>
    internal sealed class UiFont
    {
        private readonly FontAtlas _atlas;
        private readonly Glyph[] _latin;
        private readonly Dictionary<int, int> _map = new();
        private Glyph[] _dynamic = new Glyph[64];
        private int _dynamicCount;

        /// <summary>Line height in pixels.</summary>
        public float LineHeight { get; }

        /// <summary>The GDI+ font (primary family) the glyphs are drawn with.</summary>
        internal SD.Font Font { get; }

        /// <summary>The primary font's family name.</summary>
        internal string FamilyName { get; }

        /// <summary>Pixel size and style, for the fallback fonts.</summary>
        internal float PixelSize { get; }

        /// <summary>Pixel size and style, for the fallback fonts.</summary>
        internal SD.FontStyle Style { get; }

        /// <summary>Ascent / descent of the primary font (pixels), so fallback glyphs share its baseline.</summary>
        internal float Ascent { get; }

        /// <summary>Ascent / descent of the primary font (pixels), so fallback glyphs share its baseline.</summary>
        internal float Descent { get; }

        /// <summary>Fallback fonts by family (null when the family is not installed or has no usable style).</summary>
        internal Dictionary<string, SD.Font> Fallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);

        internal UiFont(FontAtlas atlas, SD.Font font, float lineHeight, Glyph[] latin)
        {
            _atlas = atlas;
            Font = font;
            LineHeight = lineHeight;
            _latin = latin;
            FamilyName = font.FontFamily.Name;
            PixelSize = font.Size;
            Style = font.Style;
            (Ascent, Descent) = FontAtlas.Metrics(font);
        }

        /// <summary>
        /// Gets a glyph ('?' for anything no installed font can draw).
        /// </summary>
        public ref readonly Glyph Get(char c) => ref Get((int)c);

        /// <summary>
        /// Gets a glyph by code point (supplementary characters come from <see cref="NextCodePoint"/>).
        /// </summary>
        public ref readonly Glyph Get(int codePoint)
        {
            if ((uint)codePoint < 256)
            {
                if (_latin[codePoint].Valid) { return ref _latin[codePoint]; }
                return ref _latin['?'];
            }

            lock (_atlas.SyncRoot)
            {
                if (!_map.TryGetValue(codePoint, out int index))
                {
                    Glyph glyph = _atlas.CreateGlyph(this, codePoint, out bool useQuestionMark);
                    index = Add(useQuestionMark ? _latin['?'] : glyph);
                    _map[codePoint] = index;
                }
                return ref _dynamic[index];
            }
        }

        /// <summary>
        /// Reads one code point at <paramref name="i"/>; a surrogate pair is combined and <paramref name="i"/> is left
        /// on its low half (so a caller's i++ steps past both). A lone surrogate comes back as itself and draws as '?'.
        /// </summary>
        public static int NextCodePoint(ReadOnlySpan<char> text, ref int i)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                return char.ConvertToUtf32(c, text[i]);
            }
            return c;
        }

        private int Add(Glyph glyph)
        {
            if (_dynamicCount == _dynamic.Length) { Array.Resize(ref _dynamic, _dynamic.Length * 2); }
            _dynamic[_dynamicCount] = glyph;
            return _dynamicCount++;
        }

        /// <summary>
        /// Scales every glyph's texture coordinates (the atlas grew).
        /// </summary>
        internal void ScaleUv(float factor)
        {
            static void Scale(ref Glyph g, float f)
            {
                g.U0 *= f;
                g.V0 *= f;
                g.U1 *= f;
                g.V1 *= f;
            }
            for (int i = 0; i < _latin.Length; i++) { Scale(ref _latin[i], factor); }
            for (int i = 0; i < _dynamicCount; i++) { Scale(ref _dynamic[i], factor); }
        }

        /// <summary>
        /// Releases the GDI+ fonts.
        /// </summary>
        internal void DisposeFonts()
        {
            Font.Dispose();
            foreach (SD.Font font in Fallbacks.Values) { font?.Dispose(); }
            Fallbacks.Clear();
        }
    }

    /// <summary>
    /// The UI font atlas (white glyphs on transparent, plus a white block for solid fills). Latin-1 is rasterised with
    /// GDI+ at startup; other characters are added on demand (<see cref="UiFont.Get(int)"/>) into a CPU copy of the
    /// atlas and uploaded before the next draw (<see cref="Sync"/>). When the atlas fills it doubles in size (texture
    /// coordinates already handed out are rescaled through <see cref="Resized"/>).
    /// </summary>
    internal sealed unsafe class FontAtlas : IDisposable
    {
        /// <summary>Non-Latin-1 characters the HUD uses itself, rasterised up front so the first frames don't hitch.</summary>
        private static readonly char[] WARM = { 'Δ', '−', '—', '–', '…', '•', '↑', '↓', '←', '→', '“', '”', '’', '≈', '≤', '≥', '‹', '›', '↔' };

        /// <summary>The atlas never grows past this (or the driver's limit).</summary>
        private const int MAX_SIZE = 8192;

        private readonly List<UiFont> _fonts = new();
        private byte[] _alpha;
        private Packer _packer;
        private int _maxSize = MAX_SIZE;
        private bool _fullUpload;
        private int _dirtyX0 = int.MaxValue, _dirtyY0 = int.MaxValue, _dirtyX1, _dirtyY1;
        private bool _fullWarned;

        // Scratch surface for on-demand glyphs (grown when a glyph needs more room)
        private SD.Bitmap _scratch;
        private SD.Graphics _scratchGraphics;
        private SD.StringFormat _format;

        /// <summary>Guards the on-demand glyph cache.</summary>
        internal object SyncRoot { get; } = new();

        /// <summary>The GL texture.</summary>
        public uint Texture { get; private set; }

        /// <summary>Atlas size in pixels.</summary>
        public int Size { get; private set; }

        /// <summary>UV of the solid white block.</summary>
        public float WhiteU { get; private set; }

        /// <summary>UV of the solid white block.</summary>
        public float WhiteV { get; private set; }

        /// <summary>Raised with the UV scale factor when the atlas grows (UV already queued must be scaled by it).</summary>
        public event Action<float> Resized;

        /// <summary>Small caps labels (11 px).</summary>
        public UiFont Small { get; private set; }

        /// <summary>Body text (14 px).</summary>
        public UiFont Body { get; private set; }

        /// <summary>Emphasised body (16 px semibold).</summary>
        public UiFont Bold { get; private set; }

        /// <summary>Large title (40 px bold).</summary>
        public UiFont Title { get; private set; }

        /// <summary>Monospace readouts (13 px).</summary>
        public UiFont Mono { get; private set; }

        /// <summary>Large monospace readouts (26 px).</summary>
        public UiFont MonoLarge { get; private set; }

        /// <summary>
        /// Rasterises all fonts and uploads the atlas (GL context current).
        /// </summary>
        /// <param name="scale">UI scale (DPI / 96).</param>
        public void Build(float scale)
        {
            Size = scale <= 1.01f ? 1024 : scale <= 2.01f ? 2048 : 4096;
            int glMax = Gl.GetInteger(Gl.MAX_TEXTURE_SIZE);
            if (glMax > 0) { _maxSize = Math.Min(MAX_SIZE, glMax); }

            _format = (SD.StringFormat)SD.StringFormat.GenericTypographic.Clone();
            _format.FormatFlags |= SD.StringFormatFlags.MeasureTrailingSpaces | SD.StringFormatFlags.NoClip;

            using (var bitmap = new SD.Bitmap(Size, Size, SDI.PixelFormat.Format32bppArgb))
            {
                using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
                {
                    g.Clear(SD.Color.Transparent);
                    g.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
                    g.FillRectangle(SD.Brushes.White, 0, 0, 4, 4);
                    WhiteU = 2f / Size;
                    WhiteV = 2f / Size;

                    _packer = new Packer { X = 6, Y = 0, RowHeight = 6 };
                    Small = Rasterise(g, new[] { "Bahnschrift SemiBold", "Segoe UI Semibold" }, 11f * scale, SD.FontStyle.Regular);
                    Body = Rasterise(g, new[] { "Bahnschrift", "Segoe UI" }, 14f * scale, SD.FontStyle.Regular);
                    Bold = Rasterise(g, new[] { "Bahnschrift SemiBold", "Segoe UI Semibold" }, 16f * scale, SD.FontStyle.Regular);
                    Title = Rasterise(g, new[] { "Bahnschrift", "Segoe UI" }, 40f * scale, SD.FontStyle.Bold);
                    Mono = Rasterise(g, new[] { "Consolas", "Courier New" }, 13f * scale, SD.FontStyle.Regular);
                    MonoLarge = Rasterise(g, new[] { "Consolas", "Courier New" }, 26f * scale, SD.FontStyle.Bold);
                }
                _alpha = ReadAlpha(bitmap);
            }

            // The HUD's own symbols, through the on-demand path
            foreach (UiFont font in _fonts)
            {
                foreach (char c in WARM) { _ = font.Get(c); }
            }

            Texture = Gl.GenTexture();
            Gl.BindTexture(Gl.TEXTURE_2D, Texture);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.BindTexture(Gl.TEXTURE_2D, 0);
            _fullUpload = true;
            Sync();
        }

        /// <summary>
        /// Shelf packer state.
        /// </summary>
        private struct Packer
        {
            public int X, Y, RowHeight;
        }

        /// <summary>
        /// Rasterises one font's Latin-1 into the startup atlas.
        /// </summary>
        private UiFont Rasterise(SD.Graphics g, string[] families, float pixelSize, SD.FontStyle style)
        {
            SD.Font font = CreateFont(families, pixelSize, style);

            float lineHeight = MathF.Ceiling(font.GetHeight(g));
            int cellHeight = (int)lineHeight + 2;
            var latin = new Glyph[256];

            for (int c = 32; c < 256; c++)
            {
                if (c >= 127 && c < 160) { continue; }
                string text = ((char)c).ToString();
                float advance = g.MeasureString(text, font, SD.PointF.Empty, _format).Width;
                int cellWidth = (int)MathF.Ceiling(advance) + 5;
                if (!Allocate(cellWidth, cellHeight, out int x, out int y, allowGrow: false)) { continue; }

                g.DrawString(text, font, SD.Brushes.White, x + 2, y + 1, _format);
                latin[c] = MakeGlyph(x, y, cellWidth, cellHeight, advance, -2f, -1f);
            }

            // Next font starts on a fresh row
            _packer.X = 0;
            _packer.Y += _packer.RowHeight + 2;
            _packer.RowHeight = 0;

            var result = new UiFont(this, font, lineHeight, latin);
            _fonts.Add(result);
            return result;
        }

        private Glyph MakeGlyph(int x, int y, int w, int h, float advance, float offsetX, float offsetY) => new()
        {
            U0 = (float)x / Size,
            V0 = (float)y / Size,
            U1 = (float)(x + w) / Size,
            V1 = (float)(y + h) / Size,
            Width = w,
            Height = h,
            Advance = advance,
            OffsetX = offsetX,
            OffsetY = offsetY,
            Valid = true
        };

        #region On-demand glyphs

        /// <summary>
        /// Rasterises one character for a font into the atlas.
        /// </summary>
        /// <param name="useQuestionMark">Set when no installed font has the character (the caller shows '?').</param>
        internal Glyph CreateGlyph(UiFont owner, int codePoint, out bool useQuestionMark)
        {
            useQuestionMark = false;

            // Lone surrogates and invalid values
            if (codePoint is >= 0xD800 and <= 0xDFFF || codePoint > 0x10FFFF)
            {
                useQuestionMark = true;
                return default;
            }

            // Invisible characters: no ink, no advance (zero-width joiners, direction marks, variation selectors...)
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                || codePoint is >= 0xFE00 and <= 0xFE0F || codePoint is >= 0xE0100 and <= 0xE01EF)
            {
                return default;
            }

            SD.Font font = FontFor(owner, codePoint);
            if (font == null)
            {
                useQuestionMark = true;
                return default;
            }

            string text = char.ConvertFromUtf32(codePoint);
            bool mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
            (float ascent, float descent) = Metrics(font);

            EnsureScratch(64);
            float measured = _scratchGraphics.MeasureString(text, font, SD.PointF.Empty, _format).Width;

            // A combining mark sits over the previous character: no advance, and room on the left for its ink
            int padX = mark ? (int)MathF.Ceiling(owner.LineHeight * 0.5f) : 2;
            float extraTop = MathF.Max(0f, ascent - owner.Ascent);
            float extraBottom = MathF.Max(0f, descent - owner.Descent);
            int textTop = 1 + (int)MathF.Ceiling(extraTop);
            int cellWidth = padX + (int)MathF.Ceiling(MathF.Max(measured, 1f)) + 3 + (mark ? padX : 0);
            int cellHeight = textTop + (int)MathF.Ceiling(owner.LineHeight + extraBottom) + 1;

            if (!Allocate(cellWidth, cellHeight, out int x, out int y, allowGrow: true))
            {
                if (!_fullWarned)
                {
                    _fullWarned = true;
                    Utilities.Log_Utils.Write($"UI font atlas full at {Size} px: further new characters show as '?'.");
                }
                useQuestionMark = true;
                return default;
            }

            // Draw on the scratch surface with the baseline matched to the primary font, then copy the alpha across
            EnsureScratch(Math.Max(cellWidth, cellHeight));
            _scratchGraphics.Clear(SD.Color.Transparent);
            _scratchGraphics.DrawString(text, font, SD.Brushes.White, padX, textTop + owner.Ascent - ascent, _format);
            CopyScratch(cellWidth, cellHeight, x, y);

            return MakeGlyph(x, y, cellWidth, cellHeight, mark ? 0f : measured, -padX, -textTop);
        }

        /// <summary>
        /// The first font in the owner's chain (its own family, then the Windows fallbacks) that has the character.
        /// </summary>
        private SD.Font FontFor(UiFont owner, int codePoint)
        {
            if (codePoint <= 0xFFFF && FontCoverage.Has(owner.FamilyName, codePoint)) { return owner.Font; }

            foreach (string family in FontCoverage.FallbackChain(codePoint))
            {
                if (codePoint <= 0xFFFF && !FontCoverage.Has(family, codePoint)) { continue; }
                SD.Font font = Fallback(owner, family);
                if (font != null) { return font; }
            }
            return null;
        }

        /// <summary>
        /// The owner's size and style in another family (cached; null when it is not installed).
        /// </summary>
        private static SD.Font Fallback(UiFont owner, string family)
        {
            if (owner.Fallbacks.TryGetValue(family, out SD.Font cached)) { return cached; }

            SD.Font font = null;
            if (FontCoverage.IsInstalled(family))
            {
                try
                {
                    // Keep the owner's weight when the family has it (Title is bold), else its regular face
                    using var test = new SD.FontFamily(family);
                    SD.FontStyle? style = test.IsStyleAvailable(owner.Style) ? owner.Style
                        : test.IsStyleAvailable(SD.FontStyle.Regular) ? SD.FontStyle.Regular
                        : null;
                    if (style.HasValue) { font = new SD.Font(family, owner.PixelSize, style.Value, SD.GraphicsUnit.Pixel); }
                }
                catch (ArgumentException)
                {
                    // Listed but unusable by GDI+
                }
            }
            owner.Fallbacks[family] = font;
            return font;
        }

        /// <summary>
        /// Ascent and descent in pixels (fonts are created in pixel units).
        /// </summary>
        internal static (float Ascent, float Descent) Metrics(SD.Font font)
        {
            SD.FontFamily family = font.FontFamily;
            float em = family.GetEmHeight(font.Style);
            if (em <= 0f) { return (font.Size, font.Size * 0.25f); }
            return (font.Size * family.GetCellAscent(font.Style) / em, font.Size * family.GetCellDescent(font.Style) / em);
        }

        private void EnsureScratch(int size)
        {
            if (_scratch != null && _scratch.Width >= size) { return; }
            int side = Math.Max(256, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)size));
            _scratchGraphics?.Dispose();
            _scratch?.Dispose();
            _scratch = new SD.Bitmap(side, side, SDI.PixelFormat.Format32bppArgb);
            _scratchGraphics = SD.Graphics.FromImage(_scratch);
            _scratchGraphics.TextRenderingHint = SDT.TextRenderingHint.AntiAliasGridFit;
        }

        private void CopyScratch(int w, int h, int x, int y)
        {
            var rect = new SD.Rectangle(0, 0, w, h);
            SDI.BitmapData data = _scratch.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            try
            {
                for (int row = 0; row < h; row++)
                {
                    byte* src = (byte*)data.Scan0 + row * data.Stride;
                    int dst = (y + row) * Size + x;
                    for (int col = 0; col < w; col++) { _alpha[dst + col] = src[col * 4 + 3]; }
                }
            }
            finally
            {
                _scratch.UnlockBits(data);
            }

            _dirtyX0 = Math.Min(_dirtyX0, x);
            _dirtyY0 = Math.Min(_dirtyY0, y);
            _dirtyX1 = Math.Max(_dirtyX1, x + w);
            _dirtyY1 = Math.Max(_dirtyY1, y + h);
        }

        #endregion

        #region Packing and growth

        /// <summary>
        /// Finds room for a cell (shelf packing), doubling the atlas when it is full and growth is allowed.
        /// </summary>
        private bool Allocate(int w, int h, out int x, out int y, bool allowGrow)
        {
            while (true)
            {
                if (_packer.X + w >= Size) { _packer.X = 0; _packer.Y += _packer.RowHeight + 1; _packer.RowHeight = 0; }
                if (_packer.Y + h < Size && w < Size)
                {
                    x = _packer.X;
                    y = _packer.Y;
                    _packer.X += w + 1;
                    _packer.RowHeight = Math.Max(_packer.RowHeight, h);
                    return true;
                }
                if (!allowGrow || !Grow())
                {
                    x = y = 0;
                    return false;
                }
            }
        }

        /// <summary>
        /// Doubles the atlas: the pixels keep their place, so every texture coordinate halves.
        /// </summary>
        private bool Grow()
        {
            int newSize = Size * 2;
            if (newSize > _maxSize) { return false; }

            var alpha = new byte[newSize * newSize];
            for (int row = 0; row < Size; row++)
            {
                Buffer.BlockCopy(_alpha, row * Size, alpha, row * newSize, Size);
            }

            float factor = (float)Size / newSize;
            _alpha = alpha;
            Size = newSize;
            WhiteU *= factor;
            WhiteV *= factor;
            foreach (UiFont font in _fonts) { font.ScaleUv(factor); }
            Resized?.Invoke(factor);

            _fullUpload = true;
            Utilities.Log_Utils.Write($"UI font atlas grown to {newSize} px for new characters.");
            return true;
        }

        #endregion

        #region Upload

        private static byte[] ReadAlpha(SD.Bitmap bitmap)
        {
            int size = bitmap.Width;
            var alpha = new byte[size * size];
            var rect = new SD.Rectangle(0, 0, size, size);
            SDI.BitmapData data = bitmap.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < size; y++)
                {
                    byte* src = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < size; x++) { alpha[y * size + x] = src[x * 4 + 3]; }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return alpha;
        }

        /// <summary>
        /// Uploads glyphs added since the last call (GL context current; called before every atlas draw).
        /// </summary>
        public void Sync()
        {
            lock (SyncRoot)
            {
                if (!_fullUpload && _dirtyX1 <= _dirtyX0) { return; }

                int x0 = 0, y0 = 0, w = Size, h = Size;
                if (!_fullUpload)
                {
                    x0 = _dirtyX0;
                    y0 = _dirtyY0;
                    w = _dirtyX1 - _dirtyX0;
                    h = _dirtyY1 - _dirtyY0;
                }

                // White RGB, alpha from the CPU copy (BGRA order)
                byte[] pixels = new byte[w * h * 4];
                for (int row = 0; row < h; row++)
                {
                    int src = (y0 + row) * Size + x0;
                    int dst = row * w * 4;
                    for (int col = 0; col < w; col++)
                    {
                        pixels[dst] = 255;
                        pixels[dst + 1] = 255;
                        pixels[dst + 2] = 255;
                        pixels[dst + 3] = _alpha[src + col];
                        dst += 4;
                    }
                }

                Gl.BindTexture(Gl.TEXTURE_2D, Texture);
                Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
                fixed (byte* p = pixels)
                {
                    if (_fullUpload) { Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA8, Size, Size, Gl.BGRA, Gl.UNSIGNED_BYTE, p); }
                    else { Gl.TexSubImage2D(Gl.TEXTURE_2D, 0, x0, y0, w, h, Gl.BGRA, Gl.UNSIGNED_BYTE, p); }
                }
                Gl.BindTexture(Gl.TEXTURE_2D, 0);

                _fullUpload = false;
                _dirtyX0 = _dirtyY0 = int.MaxValue;
                _dirtyX1 = _dirtyY1 = 0;
            }
        }

        #endregion

        /// <summary>
        /// Creates the first available font family from a list.
        /// </summary>
        private static SD.Font CreateFont(string[] families, float pixelSize, SD.FontStyle style)
        {
            foreach (string family in families)
            {
                try
                {
                    using var test = new SD.FontFamily(family);
                    if (test.IsStyleAvailable(style))
                    {
                        return new SD.Font(family, pixelSize, style, SD.GraphicsUnit.Pixel);
                    }
                }
                catch (ArgumentException)
                {
                    // Not installed; try the next
                }
            }
            return new SD.Font(SD.FontFamily.GenericSansSerif, pixelSize, style, SD.GraphicsUnit.Pixel);
        }

        /// <summary>
        /// Deletes the texture and the GDI+ objects.
        /// </summary>
        public void Dispose()
        {
            Gl.DeleteTexture(Texture);
            Texture = 0;
            foreach (UiFont font in _fonts) { font.DisposeFonts(); }
            _fonts.Clear();
            _scratchGraphics?.Dispose();
            _scratch?.Dispose();
            _format?.Dispose();
            _scratchGraphics = null;
            _scratch = null;
            _format = null;
        }
    }

    /// <summary>
    /// Which installed Windows fonts can draw a character (GDI's glyph lookup, no font linking), and the fallback order.
    /// No font files ship with BimGo: everything here is a font that comes with Windows 10 / 11 (Arial Unicode MS is
    /// used only if something else installed it).
    /// </summary>
    internal static unsafe class FontCoverage
    {
        private const uint GGI_MARK_NONEXISTING_GLYPHS = 0x0001;
        private const ushort MISSING = 0xFFFF;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, nint> Handles = new(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _installed;
        private static nint _dc;
        private static string[] _chain;

        /// <summary>True when the family is installed (checked once per run).</summary>
        public static bool IsInstalled(string family)
        {
            lock (Gate)
            {
                if (_installed == null)
                {
                    _installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using var collection = new SDT.InstalledFontCollection();
                    foreach (SD.FontFamily f in collection.Families) { _installed.Add(f.Name); }
                }
                return _installed.Contains(family);
            }
        }

        /// <summary>
        /// True when the family has a real glyph for a BMP character.
        /// </summary>
        public static bool Has(string family, int codePoint)
        {
            if (codePoint > 0xFFFF || !IsInstalled(family)) { return false; }
            lock (Gate)
            {
                if (_dc == 0) { _dc = Win32.CreateCompatibleDC(0); }
                if (_dc == 0) { return true; } // No DC: trust the font and let GDI+ draw what it can

                if (!Handles.TryGetValue(family, out nint font))
                {
                    font = Win32.CreateFontW(-32, 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 0, 0, family);
                    Handles[family] = font;
                }
                if (font == 0) { return false; }

                nint old = Win32.SelectObject(_dc, font);
                char c = (char)codePoint;
                ushort index = MISSING;
                uint result = Win32.GetGlyphIndicesW(_dc, &c, 1, &index, GGI_MARK_NONEXISTING_GLYPHS);
                Win32.SelectObject(_dc, old);
                return result != uint.MaxValue && index != MISSING;
            }
        }

        /// <summary>
        /// Fallback families for a character, best first. CJK order follows the Windows display language, so Japanese,
        /// Korean and Traditional Chinese users get their own forms of shared (Han) characters.
        /// </summary>
        public static IEnumerable<string> FallbackChain(int codePoint)
        {
            // Supplementary planes: GDI can't test coverage there, so pick by block
            if (codePoint >= 0x1F000 && codePoint <= 0x1FAFF) { return new[] { "Segoe UI Emoji", "Segoe UI Symbol" }; }
            if (codePoint >= 0x20000 && codePoint <= 0x3FFFF) { return new[] { "SimSun-ExtB", "MingLiU-ExtB", "SimSun-ExtG" }; }
            if (codePoint > 0xFFFF) { return new[] { "Segoe UI Historic", "Segoe UI Symbol", "Segoe UI Emoji" }; }

            lock (Gate)
            {
                return _chain ??= BuildChain(CultureInfo.CurrentUICulture);
            }
        }

        private static string[] BuildChain(CultureInfo culture)
        {
            string name = culture.Name;
            string[] cjk = name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? new[] { "Yu Gothic UI", "Meiryo UI", "Microsoft YaHei UI", "Malgun Gothic", "Microsoft JhengHei UI" }
                : name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Malgun Gothic", "Microsoft YaHei UI", "Yu Gothic UI", "Microsoft JhengHei UI" }
                    : name is "zh-TW" or "zh-HK" or "zh-MO" || name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "Microsoft JhengHei UI", "Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic" }
                        : new[] { "Microsoft YaHei UI", "Yu Gothic UI", "Malgun Gothic", "Microsoft JhengHei UI" };

            var chain = new List<string> { "Segoe UI", "Bahnschrift" };
            chain.AddRange(cjk);
            chain.AddRange(new[]
            {
                "Nirmala UI",             // Indic
                "Leelawadee UI",          // Thai, Lao, Khmer, Buginese
                "Ebrima",                 // Ethiopic, N'Ko, Tifinagh, Vai
                "Gadugi",                 // Cherokee, Canadian syllabics
                "Myanmar Text",
                "Javanese Text",
                "Mongolian Baiti",
                "Microsoft Himalaya",     // Tibetan
                "Microsoft Yi Baiti",
                "Microsoft New Tai Lue",
                "Microsoft Tai Le",
                "Sylfaen",                // Armenian, Georgian
                "Segoe UI Historic",
                "Segoe UI Symbol",
                "Segoe UI Emoji",
                "Cambria Math",
                "Arial Unicode MS",
                "Arial",
                "Tahoma"
            });
            return chain.ToArray();
        }
    }
}
