using BimGo.Native;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Photo mode (photo round): exposure, a flat multiply over the finished scene in the current target (−2 to +2 EV,
    /// the same in the preview and in the photo).
    /// </summary>
    internal sealed unsafe partial class SceneRenderer
    {
        private ShaderProgram _exposureProgram;
        private int _exposureColour;

        private void InitialisePhoto()
        {
            _exposureProgram = ShaderProgram.Create("photo exposure", Shaders.FULLSCREEN_VS, Shaders.EXPOSURE_FS);
            _exposureColour = _exposureProgram.Uniform("uColor");
            Gl.UseProgram(0);
        }

        private void DisposePhoto() => _exposureProgram?.Dispose();

        /// <summary>
        /// Scales the colours already in the bound target by 2^<paramref name="ev"/> (no-op at 0). Brightening is done
        /// in steps of at most ×2 (each a DST_COLOR, ONE blend).
        /// </summary>
        public void ApplyExposure(float ev)
        {
            if (MathF.Abs(ev) < 0.01f) { return; }
            float factor = MathF.Pow(2f, Math.Clamp(ev, -3f, 3f));
            Gl.Disable(Gl.DEPTH_TEST);
            Gl.DepthMask(false);
            Gl.Enable(Gl.BLEND);
            _exposureProgram.Use();
            Gl.BindVertexArray(_emptyVao);
            if (factor < 1f)
            {
                Gl.BlendFunc(Gl.DST_COLOR, Gl.ZERO);
                Gl.Uniform3(_exposureColour, factor, factor, factor);
                Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            }
            else
            {
                Gl.BlendFunc(Gl.DST_COLOR, Gl.ONE);
                while (factor > 1.001f)
                {
                    float step = MathF.Min(factor, 2f);
                    Gl.Uniform3(_exposureColour, step - 1f, step - 1f, step - 1f);
                    Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
                    factor /= step;
                }
            }
            Gl.BindVertexArray(0);
            Gl.Disable(Gl.BLEND);
            Gl.DepthMask(true);
            Gl.Enable(Gl.DEPTH_TEST);
        }
    }
}
