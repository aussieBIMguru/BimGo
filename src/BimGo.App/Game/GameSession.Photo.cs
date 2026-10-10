using System.Numerics;
using BimGo.Audio;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Gl = BimGo.Native.Gl;
using Vk = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Photo mode (photo round). <b>M</b> opens it: the UI goes except the photo panel and an optional thirds grid, the
    /// player stands still (RMB-drag looks, the wheel changes the field of view), and Enter (or TAKE PHOTO) shoots.
    /// <list type="bullet">
    /// <item><b>Still</b>: the view rendered off-screen at 1–4× the window (one pass, so AO, bloom and shadows match
    /// the preview; MSAA when the GPU memory allows), PNG or JPEG.</item>
    /// <item><b>360°</b>: six 96° views from the eye (front centred on where you look, plus up and down) stitched into a
    /// 2:1 equirectangular JPEG (4K or 8K) with Photo Sphere metadata, so phones, Facebook and panorama viewers show
    /// it as a 360 photo.</item>
    /// </list>
    /// Exposure (−3 to +3 EV) applies to the preview and the photo. Files go to Pictures\BimGo (model, kind, time);
    /// encoding and stitching run on a worker thread. The camera and the window's targets are restored after a shot.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private static readonly string[] PHOTO_KINDS = { "STILL", "360°" };
        private static readonly string[] PHOTO_SCALES = { "1×", "2×", "3×", "4×" };
        private static readonly string[] PHOTO_FORMATS = { "PNG", "JPEG" };
        private static readonly string[] PANO_SIZES = { "4K", "8K" };

        /// <summary>Field of view of each panorama view (degrees): wider than 90° so the stitch never reads an edge.</summary>
        private const float PANO_FACE_FOV = 96f;

        /// <summary>Most pixels × samples one off-screen photo target may hold (keeps memory reasonable).</summary>
        private const long PHOTO_SAMPLE_BUDGET = 40_000_000;

        private bool _photoOpen;
        private int _photoKind;          // 0 still, 1 panorama
        private int _photoScale = 2;     // still: 1–4 × the window
        private bool _photoJpeg;
        private bool _photo8K;
        private bool _photoGrid = true;
        private float _photoExposure;
        private float _photoSavedFov;
        private int _photoShotIn;        // frames until the shot (one frame shows "RENDERING…" first)
        private Vector4 _photoPanelRect;
        private string _photoNotice;
        private volatile string _photoMessage;
        private volatile string _photoLastPath;
        private RenderTarget _photoTarget, _photoResolve;

        #endregion

        #region Open / close

        /// <summary>M: opens photo mode (other panels close; the field of view is restored afterwards).</summary>
        private void OpenPhotoMode()
        {
            if (_photoOpen) { return; }
            if (_paused) { SetPaused(false); }
            CloseSunPanel();
            CloseSunHours();
            CloseSectionEditor();
            ShowUi();
            _photoOpen = true;
            _photoSavedFov = _fov;
            _photoNotice = null;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Closes photo mode (the field of view goes back to what it was).</summary>
        private void ClosePhotoMode()
        {
            if (!_photoOpen) { return; }
            _photoOpen = false;
            _photoShotIn = 0;
            _fov = _photoSavedFov;
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>
        /// Keys and mouse in photo mode: M / Esc close, Enter shoots, RMB-drag looks, the wheel changes the field of
        /// view, [ ] the sun time (shadows on).
        /// </summary>
        private void UpdatePhotoMode(InputState input)
        {
            if (input.IsPressed(Vk.VK_ESCAPE) || input.IsPressed('M'))
            {
                ClosePhotoMode();
                return;
            }
            if (input.IsPressed(Vk.VK_F11)) { _window.ToggleFullscreen(); }
            if (input.IsPressed(Vk.VK_RETURN)) { RequestPhoto(); }
            if (input.RightDown) { _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY); }
            if (input.Wheel != 0 && _photoKind == 0) { _fov = Math.Clamp(_fov - input.Wheel * 2f, 30f, 120f); }
            if (ShadowsOn && input.IsPressedOrRepeated(Vk.VK_OEM_4)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? -1 : -TIME_STEP); }
            if (ShadowsOn && input.IsPressedOrRepeated(Vk.VK_OEM_6)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? 1 : TIME_STEP); }

            string message = _photoMessage;
            if (message != null)
            {
                _photoMessage = null;
                _photoNotice = message;
            }
        }

        /// <summary>TAKE PHOTO / Enter: the shot renders the frame after next (one frame shows "RENDERING…").</summary>
        private void RequestPhoto()
        {
            if (_photoShotIn > 0) { return; }
            _photoShotIn = 2;
            _photoNotice = null;
        }

        #endregion

        #region Shots

        /// <summary>
        /// Renders the pending shot off-screen (called at the start of a frame), reads it back and hands the pixels to
        /// a worker for saving. The camera and the render targets are restored for the frame.
        /// </summary>
        private void TakePhoto()
        {
            _photoShotIn = 0;
            Vector3 position = Camera.Position;
            float yaw = Camera.Yaw, pitch = Camera.Pitch, fov = Camera.HorizontalFovDegrees;
            float viewportW = Camera.ViewportWidth, viewportH = Camera.ViewportHeight, aspect = Camera.Aspect;
            try
            {
                int maxSize = Math.Min(16384, Math.Max(1024, Gl.GetInteger(Gl.MAX_RENDERBUFFER_SIZE)));
                if (_photoKind == 0) { TakeStill(maxSize); }
                else { TakePanorama(maxSize, yaw); }
                Flash(0x60FFFFFF, 0.15f);
                Sound.Play(SoundId.UiClick);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Photo failed: {ex}");
                _photoNotice = "The photo failed: " + ex.Message;
                Sound.Play(SoundId.Error);
            }
            finally
            {
                Camera.ClearCustomView();
                Camera.Position = position;
                Camera.Yaw = yaw;
                Camera.Pitch = pitch;
                Camera.HorizontalFovDegrees = fov;
                Camera.ViewportWidth = viewportW;
                Camera.ViewportHeight = viewportH;
                Camera.Aspect = aspect;
                Camera.Update();
                _photoTarget?.Dispose();
                _photoResolve?.Dispose();
                _photoTarget = _photoResolve = null;
            }
        }

        /// <summary>A still at the chosen multiple of the window (reduced to what the GPU allows).</summary>
        private void TakeStill(int maxSize)
        {
            int scale = Math.Clamp(_photoScale, 1, 4);
            int width = _window.Width * scale, height = _window.Height * scale;
            while (scale > 1 && (width > maxSize || height > maxSize))
            {
                scale--;
                width = _window.Width * scale;
                height = _window.Height * scale;
            }

            Camera.ViewportWidth = width;
            Camera.ViewportHeight = height;
            Camera.Aspect = (float)width / Math.Max(height, 1);
            Camera.Update();
            byte[] pixels = RenderPhotoView(width, height);

            bool jpeg = _photoJpeg;
            string path = PhotoPath(jpeg ? ".jpg" : ".png", $" {width}x{height}");
            string reduced = scale < _photoScale ? $" (reduced to {scale}× for this GPU)" : string.Empty;
            _photoNotice = "Saving…";
            Task.Run(() =>
            {
                try
                {
                    byte[] file = EncodeImage(pixels, width, height, bottomUp: true, jpeg);
                    WriteFile(path, file);
                    _photoLastPath = path;
                    _photoMessage = $"Saved {Path.GetFileName(path)}{reduced}";
                    Utilities.Log_Utils.Write("Photo saved: " + path);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Photo could not be saved: {ex}");
                    _photoMessage = "The photo could not be saved: " + ex.Message;
                }
            });
        }

        /// <summary>
        /// A 360° panorama: six 96° views from the eye (four around, centred on the heading, plus up and down), read back
        /// and stitched on a worker into an equirectangular JPEG with Photo Sphere metadata.
        /// </summary>
        private void TakePanorama(int maxSize, float heading)
        {
            int width = _photo8K ? 8192 : 4096, height = width / 2;
            int face = Math.Min(Panorama.FaceSize(width, PANO_FACE_FOV), maxSize);
            Vector3 eye = Camera.Position;
            var forward = new Vector3(MathF.Cos(heading), MathF.Sin(heading), 0f);
            var left = new Vector3(-forward.Y, forward.X, 0f);
            Vector3[] forwards = { forward, left, -forward, -left, Vector3.UnitZ, -Vector3.UnitZ };
            Vector3[] ups = { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, -forward, forward };

            var faces = new byte[6][];
            var matrices = new Matrix4x4[6];
            Camera.HorizontalFovDegrees = PANO_FACE_FOV;
            Camera.ViewportWidth = face;
            Camera.ViewportHeight = face;
            Camera.Aspect = 1f;
            for (int i = 0; i < 6; i++)
            {
                Camera.SetCustomView(forwards[i], ups[i]);
                Camera.Update();
                matrices[i] = Camera.ViewProjection;
                faces[i] = RenderPhotoView(face, face);
            }

            string path = PhotoPath(".jpg", $" 360 {(_photo8K ? "8K" : "4K")}");
            _photoNotice = "Stitching the 360…";
            Task.Run(() =>
            {
                try
                {
                    byte[] equirect = Stitch(faces, matrices, forwards, eye, face, width, height, heading);
                    byte[] jpeg = EncodeImage(equirect, width, height, bottomUp: false, jpeg: true);
                    WriteFile(path, Panorama.AddPhotoSphereXmp(jpeg, width, height));
                    _photoLastPath = path;
                    _photoMessage = $"Saved {Path.GetFileName(path)} (360°: open it on a phone or in a panorama viewer)";
                    Utilities.Log_Utils.Write("Panorama saved: " + path);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Panorama could not be saved: {ex}");
                    _photoMessage = "The 360 could not be saved: " + ex.Message;
                }
            });
        }

        /// <summary>
        /// Renders the camera's current view into an off-screen target (MSAA when it fits the budget), resolves it and
        /// reads it back as bottom-up BGRA.
        /// </summary>
        private byte[] RenderPhotoView(int width, int height)
        {
            long pixels = (long)width * height;
            int samples = pixels * 4 <= PHOTO_SAMPLE_BUDGET ? 4 : pixels * 2 <= PHOTO_SAMPLE_BUDGET ? 2 : 0;
            _photoTarget ??= new RenderTarget();
            _photoResolve ??= new RenderTarget();
            RenderScene(width, height, _photoTarget, samples, photo: true);
            _photoResolve.Ensure(width, height, 0);
            _photoTarget.ResolveTo(_photoResolve);
            byte[] bgra = new byte[pixels * 4];
            _photoResolve.ReadPixels(bgra);
            return bgra;
        }

        /// <summary>
        /// Builds the equirectangular image (top-down BGRA) from the six views: per pixel its direction, the view
        /// facing it most, then a bilinear read where that view's projection puts it. Parallel over rows.
        /// </summary>
        private static byte[] Stitch(byte[][] faces, Matrix4x4[] matrices, Vector3[] forwards, Vector3 eye, int face, int width, int height, float heading)
        {
            byte[] output = new byte[width * height * 4];
            Parallel.For(0, height, row =>
            {
                for (int column = 0; column < width; column++)
                {
                    Vector3 d = Panorama.Direction(column, row, width, height, heading);
                    int best = 0;
                    float bestDot = float.MinValue;
                    for (int f = 0; f < 6; f++)
                    {
                        float dot = Vector3.Dot(d, forwards[f]);
                        if (dot > bestDot)
                        {
                            bestDot = dot;
                            best = f;
                        }
                    }

                    Vector4 clip = Vector4.Transform(new Vector4(eye + d, 1f), matrices[best]);
                    float x = (clip.X / clip.W * 0.5f + 0.5f) * face - 0.5f;
                    float y = (clip.Y / clip.W * 0.5f + 0.5f) * face - 0.5f; // GL rows, bottom-up like the read-back
                    Bilinear(faces[best], face, x, y, output, (row * width + column) * 4);
                }
            });
            return output;
        }

        private static void Bilinear(byte[] image, int size, float x, float y, byte[] output, int o)
        {
            x = Math.Clamp(x, 0f, size - 1.001f);
            y = Math.Clamp(y, 0f, size - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i00 = (y0 * size + x0) * 4, i10 = i00 + 4, i01 = i00 + size * 4, i11 = i01 + 4;
            for (int c = 0; c < 3; c++)
            {
                float top = image[i00 + c] + (image[i10 + c] - image[i00 + c]) * fx;
                float bottom = image[i01 + c] + (image[i11 + c] - image[i01 + c]) * fx;
                output[o + c] = (byte)Math.Clamp((int)(top + (bottom - top) * fy + 0.5f), 0, 255);
            }
            output[o + 3] = 255;
        }

        /// <summary>
        /// Encodes BGRA pixels (bottom-up as read from GL, or top-down) as PNG or JPEG (quality 92), opaque.
        /// </summary>
        private static byte[] EncodeImage(byte[] bgra, int width, int height, bool bottomUp, bool jpeg)
        {
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = width * 4;
                byte[] row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    Buffer.BlockCopy(bgra, (bottomUp ? height - 1 - y : y) * stride, row, 0, stride);
                    for (int i = 3; i < stride; i += 4) { row[i] = 255; }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            using var stream = new MemoryStream();
            System.Drawing.Imaging.ImageCodecInfo codec = jpeg
                ? System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid)
                : null;
            if (codec != null)
            {
                using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                bitmap.Save(stream, codec, parameters);
            }
            else
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            return stream.ToArray();
        }

        private static void WriteFile(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Pictures\BimGo\&lt;model&gt; photo &lt;kind&gt; &lt;time&gt;.ext.</summary>
        private string PhotoPath(string extension, string kind) =>
            Path.Combine(ScreenshotFolder, SafeFileName(Path.GetFileNameWithoutExtension(DocumentName)) + " photo" + kind + " " +
                DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + extension);

        /// <summary>OPEN FOLDER: Explorer with the last photo selected (or the folder).</summary>
        private void OpenPhotoFolder()
        {
            try
            {
                string last = _photoLastPath;
                Directory.CreateDirectory(ScreenshotFolder);
                var start = new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    last != null && File.Exists(last) ? $"/select,\"{last}\"" : $"\"{ScreenshotFolder}\"") { UseShellExecute = true };
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Photo folder not opened: {ex.Message}");
                _photoNotice = "The folder could not be opened";
            }
        }

        #endregion

        #region Panel

        /// <summary>
        /// Photo mode's screen: the thirds grid (stills), "RENDERING…" while a shot is due, and the panel (type, size,
        /// format, field of view, exposure, grid, sun time, TAKE PHOTO, OPEN FOLDER, CLOSE).
        /// </summary>
        private void BuildPhotoOverlay(FontAtlas f, InputState input, int width, int height)
        {
            if (_photoGrid && _photoKind == 0)
            {
                uint line = Rgba.Hex(0xFFFFFF, 0.35f);
                for (int i = 1; i < 3; i++)
                {
                    _ui.Rect(width * i / 3f, 0, MathF.Max(1f, UiScale), height, line);
                    _ui.Rect(0, height * i / 3f, width, MathF.Max(1f, UiScale), line);
                }
            }
            else if (_photoKind == 1)
            {
                _ui.TextCentred(f.Body, width * 0.5f, height * 0.5f + S(20), "360° from where you stand · the centre faces this way", Rgba.Hex(0xFFFFFF, 0.8f));
            }
            if (_photoShotIn > 0)
            {
                _ui.Panel(width * 0.5f - S(120), height * 0.5f - S(30), S(240), S(60), UiTheme.PANEL_STRONG, UiTheme.ACCENT);
                _ui.TextCentred(f.Bold, width * 0.5f, height * 0.5f - S(8), _photoKind == 0 ? "RENDERING…" : "RENDERING 360…", UiTheme.TEXT);
            }
            BuildToast(f, width);

            float w = S(320), x = width - S(20) - w, y = S(20), h = S(474);
            _photoPanelRect = new Vector4(x, y, w, h);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.ACCENT);
            float ix = x + S(16), iw = w - S(32), cy = y + S(14);
            _ui.Text(f.Small, ix, cy, "PHOTO MODE", UiTheme.ACCENT, S(1.4f));
            cy += S(26);
            _ui.TextWrapped(f.Small, ix, cy, iw, "RMB-drag looks · wheel: field of view · Enter: take", UiTheme.TEXT_MUTED, maxLines: 1);
            cy += S(24);

            int kind = Segmented(f, input, ix, cy, iw, PHOTO_KINDS, _photoKind);
            if (kind != _photoKind) { _photoKind = kind; }
            cy += S(42);

            if (_photoKind == 0)
            {
                _ui.Text(f.Body, ix, cy + S(7), "Size", UiTheme.TEXT_SOFT);
                int scale = Segmented(f, input, ix + S(90), cy, iw - S(90), PHOTO_SCALES, _photoScale - 1) + 1;
                if (scale != _photoScale) { _photoScale = scale; }
                cy += S(36);
                Text.Clear().Append(_window.Width * (long)_photoScale).Append(" × ").Append(_window.Height * (long)_photoScale).Append(" px");
                _ui.Text(f.Small, ix + S(90), cy, Text.Span, UiTheme.TEXT_FAINT);
                cy += S(22);
                _ui.Text(f.Body, ix, cy + S(7), "Format", UiTheme.TEXT_SOFT);
                int format = Segmented(f, input, ix + S(90), cy, iw - S(90), PHOTO_FORMATS, _photoJpeg ? 1 : 0);
                _photoJpeg = format == 1;
                cy += S(40);
                _ui.Text(f.Body, ix, cy + S(7), "View", UiTheme.TEXT_SOFT);
                float fov = StepperValue(f, input, ix + S(90), cy, _fov, 5f, 30f, 120f, 0, "° wide");
                if (fov != _fov) { _fov = fov; }
                cy += S(38);
            }
            else
            {
                _ui.Text(f.Body, ix, cy + S(7), "Size", UiTheme.TEXT_SOFT);
                int size = Segmented(f, input, ix + S(90), cy, iw - S(90), PANO_SIZES, _photo8K ? 1 : 0);
                _photo8K = size == 1;
                cy += S(36);
                _ui.Text(f.Small, ix + S(90), cy, _photo8K ? "8192 × 4096 px JPEG" : "4096 × 2048 px JPEG", UiTheme.TEXT_FAINT);
                cy += S(22 + 78);
            }

            _ui.Text(f.Body, ix, cy + S(7), "Exposure", UiTheme.TEXT_SOFT);
            float ev = StepperValue(f, input, ix + S(90), cy, _photoExposure, 0.5f, -3f, 3f, 1, " EV");
            if (ev != _photoExposure) { _photoExposure = ev; }
            cy += S(38);

            if (ShadowsOn)
            {
                _ui.Text(f.Body, ix, cy + S(7), "Sun", UiTheme.TEXT_SOFT);
                if (SmallButton(f, input, ix + S(90), cy, S(30), S(30), "−")) { StepSunTime(-15); }
                Text.Clear().Append(_sun.Time.Minutes / 60).Append(':');
                if (_sun.Time.Minutes % 60 < 10) { Text.Append('0'); }
                Text.Append(_sun.Time.Minutes % 60);
                _ui.TextCentred(f.Bold, ix + S(90) + S(94), cy + S(6), Text.Span, UiTheme.TEXT);
                if (SmallButton(f, input, ix + S(90) + S(158), cy, S(30), S(30), "+")) { StepSunTime(15); }
            }
            else
            {
                _ui.TextWrapped(f.Small, ix, cy + S(4), iw, "Shadows off: O (or the sun panel) for sunlight", UiTheme.TEXT_FAINT, maxLines: 1);
            }
            cy += S(38);

            bool grid = Checkbox(f, input, ix, cy + S(4), iw, "Thirds grid (not in the photo)", _photoGrid);
            _photoGrid = grid;
            cy += S(34);

            if (MenuButton(f, ix, cy, iw, _photoShotIn > 0 ? "RENDERING…" : "TAKE PHOTO (ENTER)", true, false, _photoShotIn == 0, S(44))) { RequestPhoto(); }
            cy += S(52);
            if (SmallButton(f, input, ix, cy, iw, S(30), "OPEN FOLDER")) { OpenPhotoFolder(); }
            cy += S(40);
            if (_photoNotice != null) { _ui.TextWrapped(f.Small, ix, cy, iw, _photoNotice, UiTheme.MEASURE_TEXT, maxLines: 2); }
            if (SmallButton(f, input, ix, y + h - S(44), iw, S(30), "CLOSE (M / ESC)")) { ClosePhotoMode(); }
        }

        #endregion
    }
}
