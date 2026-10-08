using BimGo.Audio;
using Gl = BimGo.Native.Gl;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// F12: saves the 3D view (no HUD, map or menus) as a PNG in Pictures\BimGo. The pixels are read on the game
    /// thread right after the scene is copied to the window; the PNG is encoded and written on a worker thread.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private bool _screenshotRequested;

        /// <summary>A message from the worker thread for the next frame's toast (null when none).</summary>
        private volatile string _screenshotMessage;

        #endregion

        /// <summary>The folder screenshots go to (Pictures\BimGo).</summary>
        private static string ScreenshotFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BimGo");

        /// <summary>
        /// F12: takes a screenshot at the end of this frame's 3D pass.
        /// </summary>
        private void RequestScreenshot()
        {
            _screenshotRequested = true;
        }

        /// <summary>
        /// Reads the window's back buffer (the scene only: called before the map and UI are drawn) and saves it.
        /// </summary>
        private unsafe void CaptureScreenshot(int width, int height)
        {
            _screenshotRequested = false;
            if (width <= 0 || height <= 0) { return; }

            byte[] pixels;
            try
            {
                pixels = new byte[width * height * 4];
                Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, 0);
                Gl.ReadBuffer(Gl.BACK);
                Gl.PixelStore(Gl.PACK_ALIGNMENT, 1);
                fixed (byte* p = pixels)
                {
                    Gl.ReadPixels(0, 0, width, height, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Screenshot failed: {ex}");
                Toast("Screenshot failed (see the log)", important: true);
                return;
            }

            string name = SafeFileName(Path.GetFileNameWithoutExtension(DocumentName)) + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png";
            string path = Path.Combine(ScreenshotFolder, name);
            Sound.Play(SoundId.UiClick);
            Flash(0x60FFFFFF, 0.12f);

            Task.Run(() =>
            {
                try
                {
                    SavePng(path, pixels, width, height);
                    _screenshotMessage = "Screenshot saved: " + path;
                    Utilities.Log_Utils.Write("Screenshot saved: " + path);
                }
                catch (Exception ex)
                {
                    Utilities.Log_Utils.Write($"Screenshot could not be saved: {ex}");
                    _screenshotMessage = "Screenshot could not be saved: " + ex.Message;
                }
            });
        }

        /// <summary>
        /// Shows the worker's message once it arrives (called every frame from the host poll).
        /// </summary>
        private void UpdateScreenshot()
        {
            string message = _screenshotMessage;
            if (message == null) { return; }
            _screenshotMessage = null;
            Toast(message, 4f);
        }

        /// <summary>
        /// Writes bottom-up BGRA pixels as an opaque PNG (rows flipped).
        /// </summary>
        private static void SavePng(string path, byte[] bgra, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = width * 4;
                byte[] row = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    // GL rows start at the bottom; force alpha opaque (the back buffer's alpha is meaningless)
                    Buffer.BlockCopy(bgra, (height - 1 - y) * stride, row, 0, stride);
                    for (int i = 3; i < stride; i += 4) { row[i] = 255; }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            string temp = path + ".tmp";
            bitmap.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            File.Move(temp, path, overwrite: true);
        }
    }
}
