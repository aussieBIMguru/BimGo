using BimGo.Format;
using Gl = BimGo.Native.Gl;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Bookmark thumbnails: a small picture of the view (192 × 108, JPEG, base64 in the bookmark) taken the frame after
    /// a bookmark is made or moved (B, ADD THIS VIEW, SET HERE), from the 3D view only (no HUD or menus), and shown in
    /// the BOOKMARKS list. Comments get the same thumbnail plus a larger picture (up to 1280 px wide, JPEG) used as
    /// their BCF snapshot. Textures are made on first use and freed with the session.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Constants

        private const int THUMB_WIDTH = 192, THUMB_HEIGHT = 108;
        private const long THUMB_JPEG_QUALITY = 72L;

        /// <summary>Widest comment picture kept for BCF snapshots (BCF round; 16:9, never wider than the window).</summary>
        private const int SNAPSHOT_MAX_WIDTH = 1280;
        private const long SNAPSHOT_JPEG_QUALITY = 82L;

        #endregion

        #region Fields

        /// <summary>The bookmark whose thumbnail is taken at the end of this frame's 3D pass, or null.</summary>
        private BookmarkRecord _thumbnailFor;

        /// <summary>The comment whose thumbnail is taken at the end of this frame's 3D pass, or null.</summary>
        private CommentRecord _commentThumbnailFor;

        /// <summary>True when a thumbnail is due this frame (bookmark or comment).</summary>
        private bool ThumbnailDue => _thumbnailFor != null || _commentThumbnailFor != null;

        // Uploaded thumbnails (bookmarks and comments): the base64 they came from (re-uploaded when it changes) and the
        // GL texture (0 = unreadable)
        private readonly Dictionary<object, (string Data, uint Texture)> _thumbnailTextures = new();

        #endregion

        #region Capture

        /// <summary>
        /// Reads the window's back buffer (scene only), crops it to 16:9 and stores a small JPEG on the bookmark.
        /// Called right after the scene is copied to the window, before the map and UI are drawn.
        /// </summary>
        private unsafe void CaptureThumbnail(int width, int height)
        {
            BookmarkRecord record = _thumbnailFor;
            CommentRecord comment = _commentThumbnailFor;
            _thumbnailFor = null;
            _commentThumbnailFor = null;
            if ((record == null && comment == null) || width < 16 || height < 16) { return; }

            try
            {
                byte[] pixels = new byte[width * height * 4];
                Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, 0);
                Gl.ReadBuffer(Gl.BACK);
                Gl.PixelStore(Gl.PACK_ALIGNMENT, 1);
                fixed (byte* p = pixels)
                {
                    Gl.ReadPixels(0, 0, width, height, Gl.BGRA, Gl.UNSIGNED_BYTE, p);
                }

                string data = Convert.ToBase64String(EncodeJpeg(pixels, width, height, THUMB_WIDTH, THUMB_HEIGHT, THUMB_JPEG_QUALITY));
                if (record != null)
                {
                    if (Bookmarks.Bookmarks.Contains(record)) { Bookmarks.SetThumbnail(record, data); }
                    else { record.Thumbnail = data; } // pending (B): saved when its name is confirmed
                }
                if (comment != null)
                {
                    // Comments also keep a larger picture for BCF snapshots (one save for both)
                    int snapshotWidth = Math.Min(SNAPSHOT_MAX_WIDTH, Math.Min(width, height * 16 / 9));
                    byte[] snapshot = snapshotWidth >= THUMB_WIDTH
                        ? EncodeJpeg(pixels, width, height, snapshotWidth, snapshotWidth * 9 / 16, SNAPSHOT_JPEG_QUALITY)
                        : null;
                    Comments.SetPictures(comment, data, snapshot);
                }
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Thumbnail failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Box-filters the centre part of a bottom-up BGRA image (cropped to the output's aspect) down to the output
        /// size and encodes it as a JPEG (PNG if no JPEG encoder is found).
        /// </summary>
        private static byte[] EncodeJpeg(byte[] bgra, int width, int height, int outWidth, int outHeight, long quality)
        {
            // Centre crop to the output's aspect
            float aspect = (float)outWidth / outHeight;
            int cropW = width, cropH = height;
            if ((float)width / height > aspect) { cropW = (int)(height * aspect); }
            else { cropH = (int)(width / aspect); }
            int x0 = (width - cropW) / 2, y0 = (height - cropH) / 2;

            // Average a 3 × 3 grid of samples per output pixel (cheap, smooth enough for these sizes)
            byte[] thumb = new byte[outWidth * outHeight * 4];
            for (int ty = 0; ty < outHeight; ty++)
            {
                for (int tx = 0; tx < outWidth; tx++)
                {
                    int b = 0, g = 0, r = 0;
                    for (int sy = 0; sy < 3; sy++)
                    {
                        // Thumbnail rows go top-down; GL rows bottom-up
                        int y = y0 + (int)((ty + (sy + 0.5f) / 3f) * cropH / outHeight);
                        int row = height - 1 - Math.Clamp(y, 0, height - 1);
                        for (int sx = 0; sx < 3; sx++)
                        {
                            int x = Math.Clamp(x0 + (int)((tx + (sx + 0.5f) / 3f) * cropW / outWidth), 0, width - 1);
                            int i = (row * width + x) * 4;
                            b += bgra[i];
                            g += bgra[i + 1];
                            r += bgra[i + 2];
                        }
                    }
                    int o = (ty * outWidth + tx) * 4;
                    thumb[o] = (byte)(b / 9);
                    thumb[o + 1] = (byte)(g / 9);
                    thumb[o + 2] = (byte)(r / 9);
                    thumb[o + 3] = 255;
                }
            }

            using var bitmap = new System.Drawing.Bitmap(outWidth, outHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, outWidth, outHeight),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < outHeight; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(thumb, y * outWidth * 4, data.Scan0 + y * data.Stride, outWidth * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            System.Drawing.Imaging.ImageCodecInfo jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var stream = new MemoryStream();
            if (jpeg != null)
            {
                using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bitmap.Save(stream, jpeg, parameters);
            }
            else
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }
            return stream.ToArray();
        }

        #endregion

        #region Textures

        /// <summary>
        /// The GL texture of a bookmark's thumbnail (made on first use, remade when the thumbnail changes), or 0.
        /// </summary>
        private uint ThumbnailTexture(BookmarkRecord record) => ThumbnailTexture(record, record?.Thumbnail, record?.Name);

        /// <summary>The GL texture of a comment's thumbnail (made on first use), or 0.</summary>
        private uint CommentThumbnailTexture(CommentRecord record) => ThumbnailTexture(record, record?.Thumbnail, "comment");

        /// <summary>
        /// The GL texture of a thumbnail owned by a bookmark or a comment (made on first use, remade when the data
        /// changes), or 0.
        /// </summary>
        private unsafe uint ThumbnailTexture(object owner, string data, string name)
        {
            if (owner == null || string.IsNullOrEmpty(data)) { return 0; }
            object record = owner;
            if (_thumbnailTextures.TryGetValue(record, out (string Data, uint Texture) cached))
            {
                if (ReferenceEquals(cached.Data, data)) { return cached.Texture; }
                if (cached.Texture != 0) { Gl.DeleteTexture(cached.Texture); }
            }

            uint texture = 0;
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(data));
                using var decoded = new System.Drawing.Bitmap(stream);
                using var bitmap = new System.Drawing.Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) { graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height); }

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
                Utilities.Log_Utils.Write($"Thumbnail unreadable ({name}): {ex.Message}");
                if (texture != 0) { Gl.DeleteTexture(texture); }
                texture = 0;
            }

            _thumbnailTextures[record] = (data, texture);
            return texture;
        }

        /// <summary>
        /// Frees every thumbnail texture (session end).
        /// </summary>
        private void ReleaseThumbnails()
        {
            foreach ((string _, uint texture) in _thumbnailTextures.Values)
            {
                if (texture != 0) { Gl.DeleteTexture(texture); }
            }
            _thumbnailTextures.Clear();
        }

        #endregion
    }
}
