// The class belongs to the Live namespace
namespace BimGo.Live
{
    /// <summary>
    /// Round Z-E: BimGo for Revit and BimGo.exe are installed together but can get out of step (a developer build, an
    /// older add-in in another Revit year). The live protocol copes with that, but features may be missing, so Go (in
    /// Revit) and the app (on joining a session) say so plainly.
    /// </summary>
    public static class VersionCheck
    {
        /// <summary>
        /// True when the two versions are the same release (major.minor.patch; a fourth part or a "+suffix" is ignored),
        /// or when either can't be read (nothing to warn about).
        /// </summary>
        public static bool SameRelease(string a, string b)
        {
            if (!TryParse(a, out Version va) || !TryParse(b, out Version vb)) { return true; }
            return va.Major == vb.Major && va.Minor == vb.Minor && Math.Max(va.Build, 0) == Math.Max(vb.Build, 0);
        }

        /// <summary>The release part of a version ("X.0.0.0" → "X.0.0"; unreadable → the text as given).</summary>
        public static string Release(string version) =>
            TryParse(version, out Version v) ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : (version ?? string.Empty).Trim();

        /// <summary>The message both sides show when the releases differ.</summary>
        public static string Mismatch(string addinVersion, string appVersion) =>
            $"BimGo for Revit is version {Release(addinVersion)} and the BimGo app is version {Release(appVersion)}. " +
            "Install the same version of both (run the latest BimGo setup), or some features may not work.";

        private static bool TryParse(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) { return false; }
            string core = text.Trim();
            int cut = core.IndexOfAny(new[] { '+', '-', ' ' });
            if (cut > 0) { core = core[..cut]; }
            if (!Version.TryParse(core, out version)) { return false; }
            return true;
        }
    }
}
