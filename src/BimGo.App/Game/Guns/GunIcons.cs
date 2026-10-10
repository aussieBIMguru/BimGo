using System.Numerics;
using BimGo.Rendering;

// The class belongs to the Guns namespace
namespace BimGo.Game.Guns
{
    /// <summary>
    /// The gun bar symbols, drawn procedurally with <see cref="UiBatch"/> primitives (no image assets, crisp at any DPI).
    /// Each icon is laid out on a 24-unit grid centred on (cx, cy); one unit = size / 24.
    /// </summary>
    internal static class GunIcons
    {
        #region Icons

        /// <summary>Scan: a reticle.</summary>
        public static void Scan(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            ui.Ring(cx, cy, 6.5f * u, w, colour, 28);
            ui.Line(cx, cy - 11f * u, cx, cy - 7.5f * u, w, colour);
            ui.Line(cx, cy + 7.5f * u, cx, cy + 11f * u, w, colour);
            ui.Line(cx - 11f * u, cy, cx - 7.5f * u, cy, w, colour);
            ui.Line(cx + 7.5f * u, cy, cx + 11f * u, cy, w, colour);
            ui.Circle(cx, cy, 1.8f * u, colour, 10);
        }

        /// <summary>Measure: a dimension line with arrows and extension lines.</summary>
        public static void Measure(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            ui.Line(cx - 10f * u, cy - 7f * u, cx - 10f * u, cy + 7f * u, w, colour);
            ui.Line(cx + 10f * u, cy - 7f * u, cx + 10f * u, cy + 7f * u, w, colour);
            ui.Line(cx - 7f * u, cy, cx + 7f * u, cy, w, colour);
            Arrowhead(ui, cx - 8.5f * u, cy, -1f, 0f, 4.5f * u, colour);
            Arrowhead(ui, cx + 8.5f * u, cy, 1f, 0f, 4.5f * u, colour);
            for (int i = -1; i <= 1; i++) { ui.Line(cx + i * 4f * u, cy - 4f * u, cx + i * 4f * u, cy - 2f * u, 1.4f * u, colour); }
        }

        /// <summary>Portal: an upright oval with a glow.</summary>
        public static void Portal(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f;
            EllipseFill(ui, cx, cy, 4.5f * u, 8f * u, Rgba.WithAlpha(colour, 0.25f));
            EllipseRing(ui, cx, cy, 6.5f * u, 10.5f * u, 2.2f * u, colour);
            EllipseRing(ui, cx, cy, 3.5f * u, 6.5f * u, 1.3f * u, Rgba.WithAlpha(colour, 0.7f));
        }

        /// <summary>Comment: a speech bubble with three dots.</summary>
        public static void Comment(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            float left = cx - 10f * u, right = cx + 10f * u, top = cy - 8f * u, bottom = cy + 5f * u;
            ui.Line(left, top, right, top, w, colour);
            ui.Line(right, top - u, right, bottom + u, w, colour);
            ui.Line(left, top - u, left, bottom + u, w, colour);
            ui.Line(left, bottom, cx - 3f * u, bottom, w, colour);
            ui.Line(cx + 1f * u, bottom, right, bottom, w, colour);
            ui.Line(cx - 3f * u, bottom, cx - 5f * u, cy + 10f * u, w, colour);
            ui.Line(cx - 5f * u, cy + 10f * u, cx + 1.5f * u, bottom - 0.5f * u, w, colour);
            for (int i = -1; i <= 1; i++) { ui.Circle(cx + i * 4.5f * u, cy - 1.5f * u, 1.5f * u, colour, 8); }
        }

        /// <summary>Teleport: an arc landing on a target disc.</summary>
        public static void Teleport(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            EllipseFill(ui, cx + 4f * u, cy + 7.5f * u, 6.5f * u, 2.6f * u, Rgba.WithAlpha(colour, 0.3f));
            EllipseRing(ui, cx + 4f * u, cy + 7.5f * u, 7f * u, 3f * u, 1.6f * u, colour);

            // Parabolic arc from lower left to the disc
            Vector2 previous = Arc(0f);
            for (int i = 1; i <= 10; i++)
            {
                Vector2 next = Arc(i / 10f);
                ui.Line(previous.X, previous.Y, next.X, next.Y, w, colour);
                previous = next;
            }
            Vector2 end = Arc(1f), before = Arc(0.88f);
            Vector2 direction = Vector2.Normalize(end - before);
            Arrowhead(ui, end.X, end.Y, direction.X, direction.Y, 5f * u, colour);

            Vector2 Arc(float t)
            {
                float x = cx - 10f * u + t * 14f * u;
                float y = cy + 4f * u - 4f * 13f * u * t * (1f - t) + t * 1.5f * u;
                return new Vector2(x, y);
            }
        }

        /// <summary>Demolition hammer: a sledgehammer, head up-right.</summary>
        public static void Hammer(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f;
            var axis = Vector2.Normalize(new Vector2(1f, -1f));
            var side = new Vector2(-axis.Y, axis.X);
            var centre = new Vector2(cx, cy);

            // Handle
            Vector2 a = centre - axis * 10f * u, b = centre + axis * 4f * u;
            ui.Line(a.X, a.Y, b.X, b.Y, 2.6f * u, colour);

            // Head: a block across the handle's end
            Vector2 headCentre = centre + axis * 6f * u;
            Vector2 h0 = headCentre - side * 7f * u - axis * 3f * u;
            Vector2 h1 = headCentre + side * 7f * u - axis * 3f * u;
            Vector2 h2 = headCentre + side * 7f * u + axis * 3f * u;
            Vector2 h3 = headCentre - side * 7f * u + axis * 3f * u;
            ui.Triangle(h0.X, h0.Y, h1.X, h1.Y, h2.X, h2.Y, colour);
            ui.Triangle(h0.X, h0.Y, h2.X, h2.Y, h3.X, h3.Y, colour);

            // Impact sparks
            ui.Line(cx - 9f * u, cy - 4f * u, cx - 6f * u, cy - 6f * u, 1.4f * u, Rgba.WithAlpha(colour, 0.7f));
            ui.Line(cx - 10f * u, cy, cx - 7f * u, cy, 1.4f * u, Rgba.WithAlpha(colour, 0.7f));
        }

        /// <summary>Gizmo: four move arrows inside a rotate arc.</summary>
        public static void Gizmo(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            ui.Line(cx - 6f * u, cy, cx + 6f * u, cy, w, colour);
            ui.Line(cx, cy - 6f * u, cx, cy + 6f * u, w, colour);
            Arrowhead(ui, cx + 6.5f * u, cy, 1f, 0f, 3.6f * u, colour);
            Arrowhead(ui, cx - 6.5f * u, cy, -1f, 0f, 3.6f * u, colour);
            Arrowhead(ui, cx, cy - 6.5f * u, 0f, -1f, 3.6f * u, colour);
            Arrowhead(ui, cx, cy + 6.5f * u, 0f, 1f, 3.6f * u, colour);

            // Rotate arc (about 270°) with an arrowhead
            float r = 10.5f * u;
            float start = -MathF.PI * 0.15f, end = start + MathF.PI * 1.45f;
            ArcStroke(ui, cx, cy, r, start, end, 1.6f * u, Rgba.WithAlpha(colour, 0.85f));
            var tip = new Vector2(cx + MathF.Cos(end) * r, cy + MathF.Sin(end) * r);
            var tangent = new Vector2(-MathF.Sin(end), MathF.Cos(end));
            Arrowhead(ui, tip.X, tip.Y, tangent.X, tangent.Y, 3.4f * u, Rgba.WithAlpha(colour, 0.85f));
        }

        /// <summary>Clone: two overlapping squares with a plus.</summary>
        public static void Clone(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            ui.Outline(cx - 10f * u, cy - 10f * u, 13f * u, 13f * u, w, Rgba.WithAlpha(colour, 0.6f));
            ui.Rect(cx - 3f * u, cy - 3f * u, 13f * u, 13f * u, Rgba.WithAlpha(colour, 0.22f));
            ui.Outline(cx - 3f * u, cy - 3f * u, 13f * u, 13f * u, w, colour);
            ui.Line(cx + 3.5f * u, cy + 0.5f * u, cx + 3.5f * u, cy + 6.5f * u, w, colour);
            ui.Line(cx + 0.5f * u, cy + 3.5f * u, cx + 6.5f * u, cy + 3.5f * u, w, colour);
        }

        /// <summary>Place: a box (a family) dropping onto a floor line, with a down arrow.</summary>
        public static void Place(UiBatch ui, float cx, float cy, float size, uint colour)
        {
            float u = size / 24f, w = 2f * u;
            ui.Line(cx - 10f * u, cy + 9f * u, cx + 10f * u, cy + 9f * u, w, Rgba.WithAlpha(colour, 0.7f));
            ui.Rect(cx - 6f * u, cy - 1f * u, 12f * u, 8f * u, Rgba.WithAlpha(colour, 0.22f));
            ui.Outline(cx - 6f * u, cy - 1f * u, 12f * u, 8f * u, w, colour);
            ui.Line(cx, cy - 10f * u, cx, cy - 5f * u, w, colour);
            Arrowhead(ui, cx, cy - 2.5f * u, 0f, 1f, 3.4f * u, colour);
        }

        #endregion

        #region Helpers

        /// <summary>A filled arrowhead with its tip at (x, y) pointing along (dx, dy).</summary>
        public static void Arrowhead(UiBatch ui, float x, float y, float dx, float dy, float length, uint colour)
        {
            float nx = -dy, ny = dx;
            float bx = x - dx * length, by = y - dy * length;
            float half = length * 0.6f;
            ui.Triangle(x, y, bx + nx * half, by + ny * half, bx - nx * half, by - ny * half, colour);
        }

        /// <summary>An elliptical ring.</summary>
        public static void EllipseRing(UiBatch ui, float cx, float cy, float rx, float ry, float thickness, uint colour, int segments = 28)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = i * MathF.Tau / segments, a1 = (i + 1) * MathF.Tau / segments;
                float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0), c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
                float h = thickness * 0.5f;
                float ix0 = cx + c0 * (rx - h), iy0 = cy + s0 * (ry - h), ox0 = cx + c0 * (rx + h), oy0 = cy + s0 * (ry + h);
                float ix1 = cx + c1 * (rx - h), iy1 = cy + s1 * (ry - h), ox1 = cx + c1 * (rx + h), oy1 = cy + s1 * (ry + h);
                ui.Triangle(ix0, iy0, ox0, oy0, ox1, oy1, colour);
                ui.Triangle(ix0, iy0, ox1, oy1, ix1, iy1, colour);
            }
        }

        /// <summary>A filled ellipse.</summary>
        public static void EllipseFill(UiBatch ui, float cx, float cy, float rx, float ry, uint colour, int segments = 24)
        {
            float px = cx + rx, py = cy;
            for (int i = 1; i <= segments; i++)
            {
                float a = i * MathF.Tau / segments;
                float nx = cx + MathF.Cos(a) * rx, ny = cy + MathF.Sin(a) * ry;
                ui.Triangle(cx, cy, px, py, nx, ny, colour);
                px = nx;
                py = ny;
            }
        }

        /// <summary>A stroked circular arc (angles in radians, screen Y down).</summary>
        public static void ArcStroke(UiBatch ui, float cx, float cy, float r, float start, float end, float thickness, uint colour, int segments = 20)
        {
            float step = (end - start) / segments;
            float h = thickness * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float a0 = start + i * step, a1 = a0 + step;
                float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0), c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
                ui.Triangle(cx + c0 * (r - h), cy + s0 * (r - h), cx + c0 * (r + h), cy + s0 * (r + h), cx + c1 * (r + h), cy + s1 * (r + h), colour);
                ui.Triangle(cx + c0 * (r - h), cy + s0 * (r - h), cx + c1 * (r + h), cy + s1 * (r + h), cx + c1 * (r - h), cy + s1 * (r - h), colour);
            }
        }

        #endregion
    }
}
