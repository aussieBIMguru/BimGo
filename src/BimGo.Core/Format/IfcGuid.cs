using System.Globalization;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// IFC's compressed GUID (22 characters, base 64 with IFC's own alphabet), as written by Revit's IFC exporter and
    /// read by BCF tools to find elements. The 128 bits are taken in the GUID's text order (as in
    /// "xxxxxxxx-xxxx-…"), most significant first; the first character carries the top 2 bits (0–3), each of the other
    /// 21 carries 6.
    /// </summary>
    public static class IfcGuid
    {
        /// <summary>IFC's base 64 alphabet.</summary>
        private const string CHARS = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        /// <summary>Length of a compressed GUID.</summary>
        public const int LENGTH = 22;

        /// <summary>
        /// Compresses a GUID to its 22-character IFC form.
        /// </summary>
        public static string Encode(Guid guid)
        {
            UInt128 value = UInt128.Parse(guid.ToString("N"), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            Span<char> chars = stackalloc char[LENGTH];
            for (int i = LENGTH - 1; i >= 0; i--)
            {
                chars[i] = CHARS[(int)(value & 63)];
                value >>= 6;
            }
            return new string(chars);
        }

        /// <summary>
        /// Expands a 22-character IFC GUID.
        /// </summary>
        /// <returns>False when the text isn't a valid compressed GUID.</returns>
        public static bool TryDecode(string text, out Guid guid)
        {
            guid = Guid.Empty;
            if (!IsValid(text)) { return false; }
            UInt128 value = 0;
            foreach (char c in text)
            {
                value = (value << 6) | (uint)CHARS.IndexOf(c);
            }
            guid = Guid.ParseExact(value.ToString("x32", CultureInfo.InvariantCulture), "N");
            return true;
        }

        /// <summary>
        /// True for 22 characters of the IFC alphabet whose first character is 0–3 (it only holds 2 bits).
        /// </summary>
        public static bool IsValid(string text)
        {
            if (text == null || text.Length != LENGTH || text[0] < '0' || text[0] > '3') { return false; }
            foreach (char c in text)
            {
                if (CHARS.IndexOf(c) < 0) { return false; }
            }
            return true;
        }
    }
}
