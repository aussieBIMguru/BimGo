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
                ref readonly Glyph glyph = ref font.Get(text[i]);
                if (!glyph.Valid) { continue; }

                if (text[i] != ' ')
                {
                    float gx = MathF.Round(pen + glyph.OffsetX), gy = top + glyph.OffsetY;
                    float gx1 = gx + glyph.Width, gy1 = gy + glyph.Height;
                    Push(gx, gy, glyph.U0, glyph.V0, colour);
                    Push(gx1, gy, glyph.U1, glyph.V0, colour);
                    Push(gx1, gy1, glyph.U1, glyph.V1, colour);
                    Push(gx, gy, glyph.U0, glyph.V0, colour);
                    Push(gx1, gy1, glyph.U1, glyph.V1, colour);
                    Push(gx, gy1, glyph.U0, glyph.V1, colour);
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
                width += font.Get(text[i]).Advance + tracking;
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
                    float advance = font.Get(text[i]).Advance;
                    if (width + advance > maxWidth && i > start) { break; }
                    if (text[i] == ' ') { lastSpace = i; }
                    width += advance;
                    i++;
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
                    float advance = font.Get(text[i]).Advance;
                    if (width + advance > maxWidth && i > start) { break; }
                    if (text[i] == ' ') { lastSpace = i; }
                    width += advance;
                    i++;
                }

                int end = (i >= text.Length || text[i] == '\n') ? i : (lastSpace > start ? lastSpace : i);
                lastWidth = Measure(font, text[start..end]);
                lines++;
                start = end < text.Length && text[end] == '\n' ? end + 1 : end;
            }
        }

        #endregion

        #region Flush

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
            Push(x, y, 0f, 0f, tint);
            Push(x + w, y, 1f, 0f, tint);
            Push(x + w, y + h, 1f, 1f, tint);
            Push(x, y, 0f, 0f, tint);
            Push(x + w, y + h, 1f, 1f, tint);
            Push(x, y + h, 0f, 1f, tint);
            Flush(screenWidth, screenHeight, texture);
        }

        /// <summary>
        /// Draws what has been queued with the atlas (texture 0) or another texture.
        /// </summary>
        private void Flush(int screenWidth, int screenHeight, uint texture)
        {
            if (_count == 0) { return; }

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
            Atlas?.Dispose();
            Gl.DeleteBuffer(_vbo);
            Gl.DeleteVertexArray(_vao);
        }

        #endregion
    }
}
