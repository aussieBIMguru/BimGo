using System.Numerics;
using System.Runtime.InteropServices;
using BimGo.Native;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Colour helpers (RGBA8 packed as R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24).
    /// </summary>
    internal static class Rgba
    {
        /// <summary>Packs from a 0xRRGGBB hex value and an alpha (0..1).</summary>
        public static uint Hex(uint rgb, float alpha = 1f)
        {
            uint r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            uint a = (uint)Math.Clamp((int)(alpha * 255f + 0.5f), 0, 255);
            return r | (g << 8) | (b << 16) | (a << 24);
        }

        /// <summary>Packs from 0..1 components (clamped).</summary>
        public static uint FromFloat(float r, float g, float b, float a)
        {
            static uint Byte(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
            return Byte(r) | (Byte(g) << 8) | (Byte(b) << 16) | (Byte(a) << 24);
        }

        /// <summary>Replaces the alpha of a packed colour.</summary>
        public static uint WithAlpha(uint colour, float alpha)
        {
            uint a = (uint)Math.Clamp((int)(alpha * 255f + 0.5f), 0, 255);
            return (colour & 0x00FFFFFF) | (a << 24);
        }

        /// <summary>Unpacks to a 0..1 vector.</summary>
        public static Vector4 ToVector(uint colour) => new(
            (colour & 0xFF) / 255f, ((colour >> 8) & 0xFF) / 255f, ((colour >> 16) & 0xFF) / 255f, (colour >> 24) / 255f);
    }

    /// <summary>
    /// A vertex of the 2D UI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct UiVertex
    {
        /// <summary>Size in bytes.</summary>
        public const int SIZE = 20;

        public float X, Y, U, V;
        public uint Colour;
    }

    /// <summary>
    /// Immediate-mode 2D batcher: solid shapes and text from one atlas texture, drawn in a single call per flush.
    /// Coordinates are pixels with the origin at the top-left.
    /// </summary>
    internal sealed unsafe class UiBatch : IDisposable
    {
        private UiVertex[] _vertices = new UiVertex[32768];
        private int _count;
        private int _capacityOnGpu;
        private uint _vao, _vbo;
        private ShaderProgram _program;
        private int _screen, _atlasUniform;
        private float _wu, _wv;

        /// <summary>The atlas (fonts).</summary>
        public FontAtlas Atlas { get; private set; }

        /// <summary>UI scale factor (DPI / 96).</summary>
        public float Scale { get; private set; } = 1f;

        /// <summary>
        /// Creates GL objects and the font atlas.
        /// </summary>
        public void Initialise(float scale)
        {
            Scale = scale;
            Atlas = new FontAtlas();
            Atlas.Build(scale);
            _wu = Atlas.WhiteU;
            _wv = Atlas.WhiteV;
            Atlas.Resized += OnAtlasResized;

            _program = ShaderProgram.Create("ui", Shaders.UI_VS, Shaders.UI_FS);
            _screen = _program.Uniform("uScreen");
            _atlasUniform = _program.Uniform("uAtlas");

            _vao = Gl.GenVertexArray();
            Gl.BindVertexArray(_vao);
            _vbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            _capacityOnGpu = _vertices.Length;
            Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_capacityOnGpu * UiVertex.SIZE, null, Gl.STREAM_DRAW);
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 2, Gl.FLOAT, false, UiVertex.SIZE, 0);
            Gl.EnableVertexAttribArray(1);
            Gl.VertexAttribPointer(1, 2, Gl.FLOAT, false, UiVertex.SIZE, 8);
            Gl.EnableVertexAttribArray(2);
            Gl.VertexAttribPointer(2, 4, Gl.UNSIGNED_BYTE, true, UiVertex.SIZE, 16);
            Gl.BindVertexArray(0);
        }

        #region Shapes

        /// <summary>A filled rectangle.</summary>
        public void Rect(float x, float y, float w, float h, uint colour)
        {
            if (_clipDepth > 0)
            {
                Quad(x, y, x + w, y + h, _wu, _wv, _wu, _wv, colour);
                return;
            }
            Push(x, y, _wu, _wv, colour);
            Push(x + w, y, _wu, _wv, colour);
            Push(x + w, y + h, _wu, _wv, colour);
            Push(x, y, _wu, _wv, colour);
            Push(x + w, y + h, _wu, _wv, colour);
            Push(x, y + h, _wu, _wv, colour);
        }

        /// <summary>A rectangle outline.</summary>
        public void Outline(float x, float y, float w, float h, float t, uint colour)
        {
            Rect(x, y, w, t, colour);
            Rect(x, y + h - t, w, t, colour);
            Rect(x, y + t, t, h - 2 * t, colour);
            Rect(x + w - t, y + t, t, h - 2 * t, colour);
        }

        /// <summary>A panel: translucent fill with a hairline border.</summary>
        public void Panel(float x, float y, float w, float h, uint fill, uint border)
        {
            Rect(x, y, w, h, fill);
            Outline(x, y, w, h, MathF.Max(1f, MathF.Round(Scale)), border);
        }

        /// <summary>A thick line.</summary>
        public void Line(float x0, float y0, float x1, float y1, float width, uint colour)
        {
            float dx = x1 - x0, dy = y1 - y0;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1e-4f) { return; }
            float nx = -dy / length * width * 0.5f, ny = dx / length * width * 0.5f;
            Triangle(x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, colour);
            Triangle(x0 + nx, y0 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny, colour);
        }

        /// <summary>A filled triangle.</summary>
        public void Triangle(float x0, float y0, float x1, float y1, float x2, float y2, uint colour)
        {
            if (_clipDepth > 0)
            {
                ClippedTriangle(x0, y0, x1, y1, x2, y2, colour);
                return;
            }
            Push(x0, y0, _wu, _wv, colour);
            Push(x1, y1, _wu, _wv, colour);
            Push(x2, y2, _wu, _wv, colour);
        }

        /// <summary>A filled circle.</summary>
        public void Circle(float cx, float cy, float r, uint colour, int segments = 20)
        {
            float px = cx + r, py = cy;
            for (int i = 1; i <= segments; i++)
            {
                float a = i * MathF.Tau / segments;
                float nx = cx + MathF.Cos(a) * r, ny = cy + MathF.Sin(a) * r;
                Triangle(cx, cy, px, py, nx, ny, colour);
                px = nx;
                py = ny;
            }
        }

        /// <summary>A circular ring.</summary>
        public void Ring(float cx, float cy, float r, float thickness, uint colour, int segments = 28)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0), c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
                float ri = r - thickness * 0.5f, ro = r + thickness * 0.5f;
                Triangle(cx + c0 * ri, cy + s0 * ri, cx + c0 * ro, cy + s0 * ro, cx + c1 * ro, cy + s1 * ro, colour);
                Triangle(cx + c0 * ri, cy + s0 * ri, cx + c1 * ro, cy + s1 * ro, cx + c1 * ri, cy + s1 * ri, colour);
            }
        }

        /// <summary>A filled pie wedge (view cone).</summary>
        public void Wedge(float cx, float cy, float r, float angleStart, float angleEnd, uint colour, int segments = 12)
        {
            float step = (angleEnd - angleStart) / segments;
            for (int i = 0; i < segments; i++)
            {
                float a0 = angleStart + i * step, a1 = a0 + step;
                Triangle(cx, cy, cx + MathF.Cos(a0) * r, cy + MathF.Sin(a0) * r, cx + MathF.Cos(a1) * r, cy + MathF.Sin(a1) * r, colour);
            }
        }

        #endregion

        #region Text

        /// <summary>
        /// Draws text with its top-left at (x, y).
        /// </summary>
        /// <returns>The advance width.</returns>
        public float Text(UiFont font, float x, float y, ReadOnlySpan<char> text, uint colour, float tracking = 0f)
        {
            float pen = MathF.Round(x);
            float top = MathF.Round(y);
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint = UiFont.NextCodePoint(text, ref i);
                ref readonly Glyph glyph = ref font.Get(codePoint);
                if (!glyph.Valid) { continue; }

                if (codePoint != ' ')
                {
                    float gx = MathF.Round(pen + glyph.OffsetX), gy = top + glyph.OffsetY;
                    float gx1 = gx + glyph.Width, gy1 = gy + glyph.Height;
                    Quad(gx, gy, gx1, gy1, glyph.U0, glyph.V0, glyph.U1, glyph.V1, colour);
                }
                pen += glyph.Advance + tracking;
            }
            return pen - MathF.Round(x);
        }

        /// <summary>
        /// Measures text width.
        /// </summary>
        public static float Measure(UiFont font, ReadOnlySpan<char> text, float tracking = 0f)
        {
            float width = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                width += font.Get(UiFont.NextCodePoint(text, ref i)).Advance + tracking;
            }
            return width;
        }

        /// <summary>
        /// Draws text right-aligned to x.
        /// </summary>
        public float TextRight(UiFont font, float right, float y, ReadOnlySpan<char> text, uint colour, float tracking = 0f)
        {
            float width = Measure(font, text, tracking);
            Text(font, right - width, y, text, colour, tracking);
            return width;
        }

        /// <summary>
        /// Draws text centred on x.
        /// </summary>
        public float TextCentred(UiFont font, float centreX, float y, ReadOnlySpan<char> text, uint colour, float tracking = 0f)
        {
            float width = Measure(font, text, tracking);
            Text(font, centreX - width * 0.5f, y, text, colour, tracking);
            return width;
        }

        /// <summary>
        /// Draws text wrapped to a width (word wrap, honours '\n'); returns the height used.
        /// Pass draw = false to measure only.
        /// </summary>
        public float TextWrapped(UiFont font, float x, float y, float maxWidth, ReadOnlySpan<char> text, uint colour, int maxLines = 6, bool draw = true)
        {
            float lineHeight = font.LineHeight * 1.15f;
            int start = 0, lines = 0;

            while (start < text.Length && lines < maxLines)
            {
                while (start < text.Length && text[start] == ' ') { start++; }
                if (start >= text.Length) { break; }

                float width = 0f;
                int i = start, lastSpace = -1;
                while (i < text.Length && text[i] != '\n')
                {
                    int next = i;
                    float advance = font.Get(UiFont.NextCodePoint(text, ref next)).Advance;
                    if (width + advance > maxWidth && i > start) { break; }
                    if (text[i] == ' ') { lastSpace = i; }
                    width += advance;
                    i = next + 1;
                }

                int end = (i >= text.Length || text[i] == '\n') ? i : (lastSpace > start ? lastSpace : i);
                if (draw) { Text(font, x, y + lines * lineHeight, text[start..end], colour); }
                lines++;
                start = end < text.Length && text[end] == '\n' ? end + 1 : end;
            }
            return lines * lineHeight;
        }

        /// <summary>
        /// Runs the same word wrap as <see cref="TextWrapped"/> and reports the line count and the width of the last line
        /// (used to place a text caret).
        /// </summary>
        public static void WrapEnd(UiFont font, float maxWidth, ReadOnlySpan<char> text, int maxLines, out int lines, out float lastWidth)
        {
            lines = 0;
            lastWidth = 0f;
            int start = 0;

            while (start < text.Length && lines < maxLines)
            {
                while (start < text.Length && text[start] == ' ') { start++; }
                if (start >= text.Length) { break; }

                float width = 0f;
                int i = start, lastSpace = -1;
                while (i < text.Length && text[i] != '\n')
                {
                    int next = i;
                    float advance = font.Get(UiFont.NextCodePoint(text, ref next)).Advance;
                    if (width + advance > maxWidth && i > start) { break; }
                    if (text[i] == ' ') { lastSpace = i; }
                    width += advance;
                    i = next + 1;
                }

                int end = (i >= text.Length || text[i] == '\n') ? i : (lastSpace > start ? lastSpace : i);
                lastWidth = Measure(font, text[start..end]);
                lines++;
                start = end < text.Length && text[end] == '\n' ? end + 1 : end;
            }
        }

        #endregion

        #region Clipping

        // The clip stack (panel shell, polish round P-A1): each entry is the clip rectangle in force before a push.
        // Clipping is done on the CPU as shapes are queued (no GL scissor, so the single batch is kept): rectangles,
        // glyphs and images are cut with their texture coordinates; triangles (lines, circles) are cut to polygons.
        private const int MAX_CLIP_DEPTH = 16;
        private readonly float[] _clipStack = new float[MAX_CLIP_DEPTH * 4];
        private int _clipDepth;
        private float _clipX0, _clipY0, _clipX1, _clipY1;

        /// <summary>
        /// Clips what is queued next to a rectangle (intersected with the clip already in force) until
        /// <see cref="PopClip"/>.
        /// </summary>
        public void PushClip(float x, float y, float w, float h)
        {
            if (_clipDepth >= MAX_CLIP_DEPTH) { throw new InvalidOperationException("UI clip stack overflow."); }
            int at = _clipDepth * 4;
            _clipStack[at] = _clipX0;
            _clipStack[at + 1] = _clipY0;
            _clipStack[at + 2] = _clipX1;
            _clipStack[at + 3] = _clipY1;

            float x0 = x, y0 = y, x1 = x + MathF.Max(w, 0f), y1 = y + MathF.Max(h, 0f);
            if (_clipDepth > 0)
            {
                x0 = MathF.Max(x0, _clipX0);
                y0 = MathF.Max(y0, _clipY0);
                x1 = MathF.Min(x1, _clipX1);
                y1 = MathF.Min(y1, _clipY1);
            }
            _clipX0 = x0;
            _clipY0 = y0;
            _clipX1 = MathF.Max(x1, x0);
            _clipY1 = MathF.Max(y1, y0);
            _clipDepth++;
        }

        /// <summary>Ends the innermost <see cref="PushClip"/>.</summary>
        public void PopClip()
        {
            if (_clipDepth == 0) { return; }
            _clipDepth--;
            int at = _clipDepth * 4;
            _clipX0 = _clipStack[at];
            _clipY0 = _clipStack[at + 1];
            _clipX1 = _clipStack[at + 2];
            _clipY1 = _clipStack[at + 3];
        }

        /// <summary>Drops every clip (a frame that ended without popping them all).</summary>
        public void ResetClip() => _clipDepth = 0;

        /// <summary>
        /// The clip rectangle in force (polish round P-A2: gamepad focus targets keep the part that shows).
        /// </summary>
        /// <returns>False when no clip is in force (the outputs are then unbounded).</returns>
        public bool GetClip(out float x0, out float y0, out float x1, out float y1)
        {
            if (_clipDepth == 0)
            {
                x0 = y0 = float.MinValue;
                x1 = y1 = float.MaxValue;
                return false;
            }
            x0 = _clipX0;
            y0 = _clipY0;
            x1 = _clipX1;
            y1 = _clipY1;
            return true;
        }

        /// <summary>True when a point is inside the clip in force (always true without one).</summary>
        public bool ClipContains(float x, float y) =>
            _clipDepth == 0 || (x >= _clipX0 && x < _clipX1 && y >= _clipY0 && y < _clipY1);

        /// <summary>
        /// An axis-aligned textured quad, cut to the clip with its texture coordinates (two triangles).
        /// </summary>
        private void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, uint colour)
        {
            if (x1 < x0)
            {
                (x0, x1) = (x1, x0);
                (u0, u1) = (u1, u0);
            }
            if (y1 < y0)
            {
                (y0, y1) = (y1, y0);
                (v0, v1) = (v1, v0);
            }
            if (_clipDepth > 0)
            {
                if (x1 <= _clipX0 || x0 >= _clipX1 || y1 <= _clipY0 || y0 >= _clipY1) { return; }
                float w = x1 - x0, h = y1 - y0;
                if (x0 < _clipX0) { u0 += (u1 - u0) * (_clipX0 - x0) / w; x0 = _clipX0; }
                if (x1 > _clipX1) { u1 -= (u1 - u0) * (x1 - _clipX1) / (x1 - x0); x1 = _clipX1; }
                if (y0 < _clipY0) { v0 += (v1 - v0) * (_clipY0 - y0) / h; y0 = _clipY0; }
                if (y1 > _clipY1) { v1 -= (v1 - v0) * (y1 - _clipY1) / (y1 - y0); y1 = _clipY1; }
            }
            Push(x0, y0, u0, v0, colour);
            Push(x1, y0, u1, v0, colour);
            Push(x1, y1, u1, v1, colour);
            Push(x0, y0, u0, v0, colour);
            Push(x1, y1, u1, v1, colour);
            Push(x0, y1, u0, v1, colour);
        }

        /// <summary>
        /// A flat-coloured triangle cut to the clip (Sutherland-Hodgman against the four edges, then a fan). Triangles
        /// wholly inside or outside skip the cutting.
        /// </summary>
        private void ClippedTriangle(float x0, float y0, float x1, float y1, float x2, float y2, uint colour)
        {
            float minX = MathF.Min(x0, MathF.Min(x1, x2)), maxX = MathF.Max(x0, MathF.Max(x1, x2));
            float minY = MathF.Min(y0, MathF.Min(y1, y2)), maxY = MathF.Max(y0, MathF.Max(y1, y2));
            if (maxX <= _clipX0 || minX >= _clipX1 || maxY <= _clipY0 || minY >= _clipY1) { return; }
            if (minX >= _clipX0 && maxX <= _clipX1 && minY >= _clipY0 && maxY <= _clipY1)
            {
                Push(x0, y0, _wu, _wv, colour);
                Push(x1, y1, _wu, _wv, colour);
                Push(x2, y2, _wu, _wv, colour);
                return;
            }

            // A triangle cut by four edges has at most seven corners
            Span<float> ax = stackalloc float[8], ay = stackalloc float[8];
            Span<float> bx = stackalloc float[8], by = stackalloc float[8];
            ax[0] = x0; ay[0] = y0;
            ax[1] = x1; ay[1] = y1;
            ax[2] = x2; ay[2] = y2;
            int count = 3;
            count = ClipEdge(ax, ay, count, bx, by, 0, _clipX0);
            count = ClipEdge(bx, by, count, ax, ay, 1, _clipX1);
            count = ClipEdge(ax, ay, count, bx, by, 2, _clipY0);
            count = ClipEdge(bx, by, count, ax, ay, 3, _clipY1);
            for (int i = 1; i + 1 < count; i++)
            {
                Push(ax[0], ay[0], _wu, _wv, colour);
                Push(ax[i], ay[i], _wu, _wv, colour);
                Push(ax[i + 1], ay[i + 1], _wu, _wv, colour);
            }
        }

        /// <summary>
        /// One Sutherland-Hodgman pass: keeps the part of a polygon on the inside of one clip edge.
        /// </summary>
        /// <param name="edge">0 left (x >= value), 1 right (x <= value), 2 top (y >= value), 3 bottom (y <= value).</param>
        /// <returns>The corner count written to the output.</returns>
        private static int ClipEdge(Span<float> inX, Span<float> inY, int count, Span<float> outX, Span<float> outY, int edge, float value)
        {
            int written = 0;
            if (count == 0) { return 0; }
            float px = inX[count - 1], py = inY[count - 1];
            bool pIn = Inside(px, py, edge, value);
            for (int i = 0; i < count; i++)
            {
                float cx = inX[i], cy = inY[i];
                bool cIn = Inside(cx, cy, edge, value);
                if (cIn != pIn && written < outX.Length)
                {
                    // The crossing point on the edge
                    float t = edge < 2 ? (value - px) / (cx - px) : (value - py) / (cy - py);
                    outX[written] = px + (cx - px) * t;
                    outY[written] = py + (cy - py) * t;
                    written++;
                }
                if (cIn && written < outX.Length)
                {
                    outX[written] = cx;
                    outY[written] = cy;
                    written++;
                }
                px = cx;
                py = cy;
                pIn = cIn;
            }
            return written;
        }

        private static bool Inside(float x, float y, int edge, float value) => edge switch
        {
            0 => x >= value,
            1 => x <= value,
            2 => y >= value,
            _ => y <= value
        };

        #endregion

        #region Flush

        /// <summary>
        /// The atlas grew (new characters filled it): what is already queued used the old texture coordinates, which
        /// all scale by the same factor (images flush before and after themselves, so the queue only holds atlas UVs).
        /// </summary>
        private void OnAtlasResized(float factor)
        {
            _wu = Atlas.WhiteU;
            _wv = Atlas.WhiteV;
            for (int i = 0; i < _count; i++)
            {
                _vertices[i].U *= factor;
                _vertices[i].V *= factor;
            }
        }

        private void Push(float x, float y, float u, float v, uint colour)
        {
            if (_count == _vertices.Length) { Array.Resize(ref _vertices, _vertices.Length * 2); }
            ref UiVertex vertex = ref _vertices[_count++];
            vertex.X = x;
            vertex.Y = y;
            vertex.U = u;
            vertex.V = v;
            vertex.Colour = colour;
        }

        /// <summary>
        /// Draws everything queued and clears the queue.
        /// </summary>
        public void Flush(int screenWidth, int screenHeight) => Flush(screenWidth, screenHeight, 0);

        /// <summary>
        /// Draws a texture (e.g. a bookmark thumbnail) as a rectangle, in order with everything batched so far: what
        /// was queued before is drawn first (under it), what is queued after is drawn over it.
        /// </summary>
        /// <param name="texture">The GL texture (RGBA, top row first).</param>
        /// <param name="tint">Multiplied colour (white = as is).</param>
        public void Image(uint texture, float x, float y, float w, float h, int screenWidth, int screenHeight, uint tint = 0xFFFFFFFF)
        {
            if (texture == 0) { return; }
            Flush(screenWidth, screenHeight, 0);
            Quad(x, y, x + w, y + h, 0f, 0f, 1f, 1f, tint);
            Flush(screenWidth, screenHeight, texture);
        }

        /// <summary>
        /// Draws what has been queued with the atlas (texture 0) or another texture.
        /// </summary>
        private void Flush(int screenWidth, int screenHeight, uint texture)
        {
            if (_count == 0) { return; }

            // Characters first seen this frame were rasterised on the CPU: upload them before the atlas is sampled
            if (texture == 0) { Atlas.Sync(); }

            Gl.BindVertexArray(_vao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            if (_vertices.Length > _capacityOnGpu)
            {
                _capacityOnGpu = _vertices.Length;
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)_capacityOnGpu * UiVertex.SIZE, null, Gl.STREAM_DRAW);
            }
            fixed (UiVertex* data = _vertices)
            {
                Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, (nint)_count * UiVertex.SIZE, data);
            }

            _program.Use();
            Gl.Uniform2(_screen, screenWidth, screenHeight);
            Gl.Uniform1(_atlasUniform, 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindTexture(Gl.TEXTURE_2D, texture != 0 ? texture : Atlas.Texture);

            Gl.Disable(Gl.DEPTH_TEST);
            Gl.Enable(Gl.BLEND);
            Gl.BlendFuncSeparate(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, Gl.ONE, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.Viewport(0, 0, screenWidth, screenHeight);

            Gl.DrawArrays(Gl.TRIANGLES, 0, _count);

            Gl.Disable(Gl.BLEND);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.BindVertexArray(0);
            _count = 0;
        }

        /// <summary>
        /// Releases GL resources.
        /// </summary>
        public void Dispose()
        {
            _program?.Dispose();
            if (Atlas != null) { Atlas.Resized -= OnAtlasResized; }
            Atlas?.Dispose();
            Gl.DeleteBuffer(_vbo);
            Gl.DeleteVertexArray(_vao);
        }

        #endregion
    }
}
