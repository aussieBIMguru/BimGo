using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BimGo.Format;
using BimGo.Scene;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// The CC0 proxy textures shipped beside the app (<c>Resources\Proxies</c>): one tileable colour map per
    /// <see cref="ProxyCatalog"/> keyword. Listed in <c>proxies.json</c> (file, real-world size, source and licence);
    /// without the list, an image named after its keyword ("brick.jpg") is used with the catalog's default size.
    /// The images never go into a .bimgo: materials only store the keyword, so a file opened where the pack is missing
    /// shows plain colours. Loaded once; never throws.
    /// </summary>
    internal sealed class ProxyPack
    {
        /// <summary>One entry of proxies.json.</summary>
        private sealed class EntryDto
        {
            public string Keyword { get; set; }
            public string File { get; set; }
            public float SizeU { get; set; }
            public float SizeV { get; set; }
            public string Title { get; set; }
            public string Source { get; set; }
            public string Licence { get; set; }
        }

        /// <summary>proxies.json.</summary>
        private sealed class PackDto
        {
            public List<EntryDto> Proxies { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JSON = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static ProxyPack _shared;
        private readonly Dictionary<string, (string Path, float SizeU, float SizeV)> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder the pack was read from.</summary>
        public string Folder { get; private init; } = string.Empty;

        /// <summary>Keywords with an image.</summary>
        public int Count => _entries.Count;

        /// <summary>The pack beside the app (read on first use).</summary>
        public static ProxyPack Shared => _shared ??= Load(Path.Combine(AppContext.BaseDirectory, "Resources", "Proxies"));

        /// <summary>
        /// Reads a pack folder. Never throws (an unreadable or missing pack is empty).
        /// </summary>
        public static ProxyPack Load(string folder)
        {
            var pack = new ProxyPack { Folder = folder ?? string.Empty };
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                {
                    Utilities.Log_Utils.Write($"Proxy textures: no pack at {folder}.");
                    return pack;
                }

                string listPath = Path.Combine(folder, "proxies.json");
                if (File.Exists(listPath))
                {
                    PackDto dto = JsonSerializer.Deserialize<PackDto>(File.ReadAllText(listPath), JSON);
                    foreach (EntryDto entry in dto?.Proxies ?? new List<EntryDto>())
                    {
                        string keyword = ProxyCatalog.Normalise(entry?.Keyword);
                        if (keyword == null || string.IsNullOrWhiteSpace(entry.File)) { continue; }
                        string path = Path.Combine(folder, entry.File);
                        if (!File.Exists(path)) { continue; }
                        ProxyKeyword known = ProxyCatalog.Find(keyword);
                        pack._entries[keyword] = (path,
                            entry.SizeU > 0.01f ? entry.SizeU : known?.SizeU ?? 1f,
                            entry.SizeV > 0.01f ? entry.SizeV : known?.SizeV ?? 1f);
                    }
                }

                // Images named after a keyword fill the gaps (drop-in: "brick.jpg")
                foreach (ProxyKeyword keyword in ProxyCatalog.ALL)
                {
                    if (pack._entries.ContainsKey(keyword.Keyword)) { continue; }
                    foreach (string extension in new[] { ".jpg", ".jpeg", ".png" })
                    {
                        string path = Path.Combine(folder, keyword.Keyword + extension);
                        if (!File.Exists(path)) { continue; }
                        pack._entries[keyword.Keyword] = (path, keyword.SizeU, keyword.SizeV);
                        break;
                    }
                }
                Utilities.Log_Utils.Write($"Proxy textures: {pack.Count} of {ProxyCatalog.ALL.Count} keywords available in {folder}.");
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Proxy textures unreadable ({folder}): {ex.Message}");
            }
            return pack;
        }

        /// <summary>True if the pack has an image for a keyword.</summary>
        public bool Has(string keyword) => keyword != null && _entries.ContainsKey(keyword);

        /// <summary>
        /// A keyword's image bytes (read once), or null.
        /// </summary>
        public byte[] ImageBytes(string keyword)
        {
            if (keyword == null || !_entries.TryGetValue(keyword, out (string Path, float SizeU, float SizeV) entry)) { return null; }
            if (_bytes.TryGetValue(keyword, out byte[] cached)) { return cached; }
            try
            {
                byte[] bytes = File.ReadAllBytes(entry.Path);
                _bytes[keyword] = bytes;
                return bytes;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Proxy texture {entry.Path} unreadable: {ex.Message}");
                _bytes[keyword] = null;
                return null;
            }
        }

        /// <summary>
        /// The real-world size one repeat of a keyword's image covers (m).
        /// </summary>
        public (float U, float V) SizeOf(string keyword)
        {
            if (keyword != null && _entries.TryGetValue(keyword, out (string Path, float SizeU, float SizeV) entry)) { return (entry.SizeU, entry.SizeV); }
            ProxyKeyword known = ProxyCatalog.Find(keyword);
            return (known?.SizeU ?? 1f, known?.SizeV ?? 1f);
        }

        /// <summary>
        /// The proxy a material is drawn with, or null: none when it has an embedded image; else its stored keyword;
        /// else, for snapshots made before proxies existed (no recorded texture origin), a suggestion by name and
        /// schema for a missing or unreadable image when <paramref name="autoProxy"/> is on.
        /// </summary>
        public static string EffectiveProxy(SceneMaterial material, MaterialData data, bool autoProxy)
        {
            if (material == null) { return null; }
            if (material.Texture != null && data != null && data.Textures.ContainsKey(material.Texture)) { return null; }
            if (material.Proxy != null) { return material.Proxy; }
            if (autoProxy && material.TextureOrigin == null && material.TextureState is TextureState.Missing or TextureState.Unreadable)
            {
                return ProxyCatalog.Suggest(material.Name, material.Schema);
            }
            return null;
        }
    }

    /// <summary>
    /// Re-encodes a picked image for embedding, exactly as the Revit extraction does (longest side ≤ the cap, JPEG
    /// quality 85, same entry naming), so images added in the app dedupe with extracted ones.
    /// </summary>
    internal static class TextureEncoder
    {
        private const long JPEG_QUALITY = 85L;

        /// <summary>
        /// The entry name an image path gets at a size cap ("textures/&lt;hash&gt;.jpg").
        /// </summary>
        public static string EntryName(string path, int cap)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes((path ?? string.Empty).ToLowerInvariant() + "|" + cap));
            return BimGoFormat.TEXTURE_FOLDER + Convert.ToHexString(hash, 0, 10).ToLowerInvariant() + ".jpg";
        }

        /// <summary>
        /// Reads, resizes and JPEG-encodes an image file. Null (with a reason) if it can't be read or decoded.
        /// </summary>
        public static byte[] Encode(string path, int cap, out string error)
        {
            error = null;
            try
            {
                byte[] source = File.ReadAllBytes(path);
                using var input = new MemoryStream(source, writable: false);
                using var image = SD.Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
                int width = image.Width, height = image.Height;
                double scale = Math.Min(1.0, (double)cap / Math.Max(width, height));
                int w = Math.Max(1, (int)Math.Round(width * scale)), h = Math.Max(1, (int)Math.Round(height * scale));

                using var resized = new SD.Bitmap(w, h, SDI.PixelFormat.Format24bppRgb);
                using (var g = SD.Graphics.FromImage(resized))
                {
                    g.Clear(SD.Color.White);
                    g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingQuality = SD.Drawing2D.CompositingQuality.HighQuality;
                    using var wrap = new SDI.ImageAttributes();
                    wrap.SetWrapMode(SD.Drawing2D.WrapMode.TileFlipXY);
                    g.DrawImage(image, new SD.Rectangle(0, 0, w, h), 0, 0, width, height, SD.GraphicsUnit.Pixel, wrap);
                }

                using var output = new MemoryStream();
                SDI.ImageCodecInfo jpeg = SDI.ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == SDI.ImageFormat.Jpeg.Guid);
                if (jpeg != null)
                {
                    using var parameters = new SDI.EncoderParameters(1);
                    parameters.Param[0] = new SDI.EncoderParameter(SDI.Encoder.Quality, JPEG_QUALITY);
                    resized.Save(output, jpeg, parameters);
                }
                else
                {
                    resized.Save(output, SDI.ImageFormat.Jpeg);
                }
                return output.ToArray();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Texture {path} could not be read: {ex.Message}");
                return null;
            }
        }
    }
}
