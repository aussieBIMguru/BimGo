using System.Numerics;
using BimGo.Native;
using BimGo.Physics;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// The section cut (section box round): the scene and geometry pre-pass shaders discard what the cut's planes
    /// remove (<see cref="SetSection"/>, passes with <see cref="SceneDrawParams.Section"/>), and <see cref="DrawSectionCaps"/>
    /// fills the cut solids. Shadows, light shadows and reflection probes ignore the cut (the whole building still
    /// casts and reflects).
    /// <para>Caps, per plane whose cut-away side holds the eye (the only planes whose caps can face it): (1) the opaque
    /// scene cut by that plane alone is drawn into the stencil with INVERT, no colour, no depth test, so a pixel
    /// stays odd when its ray, from the plane on, ends inside a solid; (2) the cap polygon (the box face, or a large
    /// square on the free plane, cut by the other planes) is drawn where the stencil is odd, depth-tested, in one flat
    /// colour (the user's choice). Cost: one extra opaque pass per facing plane (often none when inside the
    /// box).</para>
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        #region Fields

        private ShaderProgram _capStencilProgram, _capProgram;
        private int _capStencilViewProj, _capStencilModel, _capStencilPlane, _capStencilShade;
        private int _capViewProj, _capClipCount, _capClipPlanes, _capSkip, _capColour;
        private uint _capVao, _capVbo;
        private readonly Vector3[] _capCorners = new Vector3[6];

        private Vector4[] _clipPlanes = new Vector4[BimGo.Scene.SectionCut.MAX_PLANES];
        private int _clipCount;
        private bool _clipBox;

        #endregion

        #region Setup

        private void InitialiseSection()
        {
            _capStencilProgram = ShaderProgram.Create("section stencil", Shaders.SCENE_VS, Shaders.CAP_STENCIL_FS);
            _capStencilViewProj = _capStencilProgram.Uniform("uViewProj");
            _capStencilModel = _capStencilProgram.Uniform("uModel");
            _capStencilPlane = _capStencilProgram.Uniform("uCapPlane");
            _capStencilShade = _capStencilProgram.Uniform("uCapShade");

            _capProgram = ShaderProgram.Create("section cap", Shaders.CAP_VS, Shaders.CAP_FS);
            _capViewProj = _capProgram.Uniform("uViewProj");
            _capClipCount = _capProgram.Uniform("uClipCount");
            _capClipPlanes = _capProgram.Uniform("uClipPlanes");
            _capSkip = _capProgram.Uniform("uSkip");
            _capColour = _capProgram.Uniform("uColor");
            Gl.UseProgram(0);

            _capVao = Gl.GenVertexArray();
            Gl.BindVertexArray(_capVao);
            _capVbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _capVbo);
            Gl.BufferData(Gl.ARRAY_BUFFER, 6 * 12, null, Gl.STREAM_DRAW);
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 3, Gl.FLOAT, false, 12, 0);
            Gl.BindVertexArray(0);
        }

        private void DisposeSection()
        {
            _capStencilProgram?.Dispose();
            _capProgram?.Dispose();
            if (_capVbo != 0) { Gl.DeleteBuffer(_capVbo); }
            if (_capVao != 0) { Gl.DeleteVertexArray(_capVao); }
        }

        #endregion

        #region Cut

        /// <summary>
        /// Sets the cut for the coming frames (planes copied; scene-local (n, d), n·p &gt; d cut away).
        /// </summary>
        /// <param name="planes">The planes (box faces first when <paramref name="box"/>, then the free plane).</param>
        /// <param name="count">How many (0 = no cut).</param>
        /// <param name="box">True when the first six planes are the box's faces (+X, −X, +Y, −Y, +Z, −Z).</param>
        public void SetSection(Vector4[] planes, int count, bool box)
        {
            _clipCount = Math.Clamp(count, 0, _clipPlanes.Length);
            for (int i = 0; i < _clipCount; i++) { _clipPlanes[i] = planes[i]; }
            _clipBox = box && _clipCount >= 6;
        }

        /// <summary>True when a cut is set.</summary>
        public bool HasSection => _clipCount > 0;

        /// <summary>Uploads the cut (or none) to the current program.</summary>
        private void ApplyClip(int countLocation, int planesLocation, bool on)
        {
            int count = on ? _clipCount : 0;
            Gl.Uniform1(countLocation, count);
            if (count > 0) { Gl.Uniform4(planesLocation, count, _clipPlanes); }
        }

        #endregion

        #region Caps

        /// <summary>
        /// Fills the cut solids (after the opaque scene, before the ground; the scene target must have a stencil).
        /// </summary>
        /// <param name="viewProjection">The player's view-projection.</param>
        /// <param name="frustum">Frustum planes for chunk culling.</param>
        /// <param name="eye">The eye (scene-local).</param>
        /// <param name="groupVisible">Per visibility group.</param>
        /// <param name="dynamics">Moved / placed elements.</param>
        /// <param name="colour">The caps' flat colour (RGB 0–1).</param>
        /// <param name="extent">Half size of the free plane's cap square (m): larger than the model.</param>
        public void DrawSectionCaps(in Matrix4x4 viewProjection, Vector4[] frustum, Vector3 eye, bool[] groupVisible, DynamicSet dynamics,
            Vector3 colour, float extent)
        {
            if (_clipCount == 0) { return; }
            FlushDynamic();
            Gl.Enable(Gl.STENCIL_TEST);
            Gl.StencilMask(0xFF);
            for (int i = 0; i < _clipCount; i++)
            {
                Vector4 plane = _clipPlanes[i];
                if (plane.X * eye.X + plane.Y * eye.Y + plane.Z * eye.Z <= plane.W) { continue; } // the eye is on the kept side

                // (1) Parity of the scene cut by this plane alone
                Gl.ClearStencil(0);
                Gl.Clear(Gl.STENCIL_BUFFER_BIT);
                Gl.ColorMask(false, false, false, false);
                Gl.DepthMask(false);
                Gl.Disable(Gl.DEPTH_TEST);
                Gl.StencilFunc(Gl.ALWAYS, 0, 1);
                Gl.StencilOp(Gl.KEEP, Gl.KEEP, Gl.INVERT);
                DrawCapScene(viewProjection, frustum, groupVisible, dynamics, plane, 1f);

                Gl.ColorMask(true, true, true, true);
                Gl.Enable(Gl.DEPTH_TEST);
                Gl.DepthFunc(Gl.LESS);
                Gl.StencilFunc(Gl.EQUAL, 1, 1);
                Gl.StencilOp(Gl.KEEP, Gl.KEEP, Gl.KEEP);

                // (2) The cap polygon in the flat cap colour
                if (!CapPolygon(i, eye, extent)) { continue; }
                Gl.DepthMask(true);
                _capProgram.Use();
                Gl.UniformMatrix4(_capViewProj, viewProjection);
                Gl.Uniform1(_capClipCount, _clipCount);
                Gl.Uniform4(_capClipPlanes, _clipCount, _clipPlanes);
                Gl.Uniform1(_capSkip, i);
                Gl.Uniform4(_capColour, colour.X, colour.Y, colour.Z, 1f);
                Gl.BindVertexArray(_capVao);
                Gl.BindBuffer(Gl.ARRAY_BUFFER, _capVbo);
                fixed (Vector3* corners = _capCorners)
                {
                    Gl.BufferSubData(Gl.ARRAY_BUFFER, 0, 6 * 12, corners);
                }
                Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
                Gl.BindVertexArray(0);
                Gl.ColorMask(true, true, true, true);
            }

            Gl.Disable(Gl.STENCIL_TEST);
            Gl.DepthMask(true);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
        }

        /// <summary>The opaque scene (static and moved) with the cap stencil program, cut by one plane.</summary>
        private void DrawCapScene(in Matrix4x4 viewProjection, Vector4[] frustum, bool[] groupVisible, DynamicSet dynamics, Vector4 plane, float shade)
        {
            _capStencilProgram.Use();
            Gl.UniformMatrix4(_capStencilViewProj, viewProjection);
            Gl.UniformMatrix4(_capStencilModel, Matrix4x4.Identity);
            Gl.Uniform4(_capStencilPlane, plane.X, plane.Y, plane.Z, plane.W);
            Gl.Uniform1(_capStencilShade, shade);
            DrawBatches(frustum, groupVisible, transparent: false, countStats: false);
            if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, frustum, transparent: false, _capStencilModel); }
        }

        /// <summary>
        /// The cap polygon of plane i as two triangles in <see cref="_capCorners"/>: a box face (from the box planes),
        /// or a square of half size <paramref name="extent"/> on a free plane around the eye's foot on it.
        /// </summary>
        private bool CapPolygon(int i, Vector3 eye, float extent)
        {
            if (_clipBox && i < 6)
            {
                var min = new Vector3(-_clipPlanes[1].W, -_clipPlanes[3].W, -_clipPlanes[5].W);
                var max = new Vector3(_clipPlanes[0].W, _clipPlanes[2].W, _clipPlanes[4].W);
                int axis = i / 2;
                float at = i % 2 == 0 ? max[axis] : min[axis];
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                Vector3 Corner(float a, float b)
                {
                    Vector3 c = Vector3.Zero;
                    c[axis] = at;
                    c[u] = a;
                    c[v] = b;
                    return c;
                }
                Quad(Corner(min[u], min[v]), Corner(max[u], min[v]), Corner(max[u], max[v]), Corner(min[u], max[v]));
                return true;
            }

            var normal = new Vector3(_clipPlanes[i].X, _clipPlanes[i].Y, _clipPlanes[i].Z);
            if (normal.LengthSquared() < 1e-8f) { return false; }
            Vector3 centre = eye - normal * (Vector3.Dot(normal, eye) - _clipPlanes[i].W);
            Vector3 tangent = MathF.Abs(normal.Z) < 0.9f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal)) : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, normal));
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            Vector3 a = tangent * extent, b = bitangent * extent;
            Quad(centre - a - b, centre + a - b, centre + a + b, centre - a + b);
            return true;
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            _capCorners[0] = a;
            _capCorners[1] = b;
            _capCorners[2] = c;
            _capCorners[3] = a;
            _capCorners[4] = c;
            _capCorners[5] = d;
        }

        #endregion
    }
}
