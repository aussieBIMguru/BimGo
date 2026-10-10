using System.Text;
using System.Text.Json;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// One surface of a saved sun hours study: which element and plane it is (so the grid can be laid again), the
    /// room it was clipped to and labels for the export. Coordinates are Revit internal metres.
    /// </summary>
    public sealed class SunStudyFace
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public int Link { get; set; }
        public string ElementName { get; set; } = string.Empty;

        /// <summary>The plane normal (internal axes).</summary>
        public float Nx { get; set; }
        public float Ny { get; set; }
        public float Nz { get; set; }

        /// <summary>The plane offset in internal coordinates (dot(normal, point)).</summary>
        public double Offset { get; set; }

        public bool BothSides { get; set; }
        public bool Picked { get; set; }

        /// <summary>The room the cells were clipped to: "number|name|level" (empty = none).</summary>
        public string RoomKey { get; set; } = string.Empty;

        /// <summary>"2.05 Kitchen" for the export.</summary>
        public string RoomLabel { get; set; } = string.Empty;
    }

    /// <summary>
    /// A saved study (sun hours round 2; daylight modes since the daylight round): the settings (with the mode), the
    /// surfaces, every cell's test point and its value.
    /// </summary>
    public sealed class SunStudyDocument
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset Saved { get; set; } = DateTimeOffset.Now;
        public string SavedBy { get; set; } = Environment.UserName;
        public string Model { get; set; } = string.Empty;
        public string Units { get; set; } = "metres, Revit internal coordinates";

        /// <summary>The grid settings (cell size, offsets).</summary>
        public SunHoursSettings Grid { get; set; } = new();

        /// <summary>The run settings (day, times, step, glass, target).</summary>
        public SunHoursSettings Run { get; set; } = new();

        public int SunSamples { get; set; }
        public int TotalSamples { get; set; }

        /// <summary>The surfaces, in grid order.</summary>
        public List<SunStudyFace> Faces { get; set; } = new();

        /// <summary>Per cell, its face index.</summary>
        public int[] CellFaces { get; set; } = Array.Empty<int>();

        /// <summary>Per cell, its test point x, y, z (internal metres, flat).</summary>
        public float[] Points { get; set; } = Array.Empty<float>();

        /// <summary>Per cell, its value: hours of direct sun, daylight factor (%) or average illuminance (lux), by the run's mode.</summary>
        public float[] Hours { get; set; } = Array.Empty<float>();

        /// <summary>Illuminance studies: per cell, the share of time samples at or above the lux target (else null).</summary>
        public float[] Shares { get; set; }

        /// <summary>
        /// True when the arrays agree (one face index, three coordinates and one value per cell, face indices in range,
        /// finite numbers).
        /// </summary>
        public bool IsConsistent()
        {
            int cells = Hours?.Length ?? 0;
            if (Faces == null || CellFaces == null || Points == null || CellFaces.Length != cells || Points.Length != cells * 3) { return false; }
            foreach (int face in CellFaces) { if (face < 0 || face >= Faces.Count) { return false; } }
            foreach (float value in Points) { if (!float.IsFinite(value)) { return false; } }
            foreach (float value in Hours) { if (!float.IsFinite(value) || value < 0f) { return false; } }
            if (Shares != null)
            {
                if (Shares.Length != cells) { return false; }
                foreach (float value in Shares) { if (!float.IsFinite(value) || value < 0f || value > 1.0001f) { return false; } }
            }
            return true;
        }
    }

    /// <summary>A saved study in a model folder's list.</summary>
    /// <param name="Name">The study name (from the file name).</param>
    /// <param name="Path">The file.</param>
    /// <param name="Saved">When the file was last written.</param>
    public readonly record struct SunStudyInfo(string Name, string Path, DateTime Saved);

    /// <summary>
    /// Saved sun hours studies (sun hours round 2): JSON files in <c>sun-studies\</c> inside a model's BimGo folder
    /// (<see cref="ModelFolders"/>), one per study, named after it. Never throws.
    /// </summary>
    public static class SunStudyFiles
    {
        /// <summary>The sub-folder of a model folder.</summary>
        public const string FOLDER = "sun-studies";

        /// <summary>Longest study name.</summary>
        public const int MAX_NAME = 60;

        private static readonly JsonSerializerOptions OPTIONS = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        /// <summary>The studies folder of a model folder.</summary>
        public static string FolderIn(string modelFolder) => Path.Combine(modelFolder ?? string.Empty, FOLDER);

        /// <summary>
        /// A file-safe study name: invalid characters replaced, trimmed, at most <see cref="MAX_NAME"/> characters,
        /// "Study" when blank.
        /// </summary>
        public static string SafeName(string name)
        {
            var builder = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in (name ?? string.Empty).Trim())
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
            }
            string safe = builder.ToString().Trim(' ', '.');
            if (safe.Length > MAX_NAME) { safe = safe[..MAX_NAME].TrimEnd(' ', '.'); }
            return safe.Length == 0 ? "Study" : safe;
        }

        /// <summary>The saved studies, newest first (empty when none or the folder can't be read).</summary>
        public static List<SunStudyInfo> List(string modelFolder)
        {
            var list = new List<SunStudyInfo>();
            try
            {
                string folder = FolderIn(modelFolder);
                if (string.IsNullOrEmpty(modelFolder) || !Directory.Exists(folder)) { return list; }
                foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    list.Add(new SunStudyInfo(Path.GetFileNameWithoutExtension(file), file, File.GetLastWriteTime(file)));
                }
                list.Sort((a, b) => b.Saved.CompareTo(a.Saved));
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sun studies not listed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// Writes a study (replacing one of the same name; temp file, then replace).
        /// </summary>
        /// <param name="modelFolder">The model's BimGo folder.</param>
        /// <param name="document">The study (its name picks the file).</param>
        /// <param name="path">Out: the file written.</param>
        /// <param name="error">Out: a reason on failure.</param>
        public static bool Write(string modelFolder, SunStudyDocument document, out string path, out string error)
        {
            path = null;
            error = null;
            try
            {
                if (string.IsNullOrEmpty(modelFolder)) { error = "This model has no BimGo folder"; return false; }
                if (document == null || !document.IsConsistent()) { error = "The study is incomplete"; return false; }
                document.Name = SafeName(document.Name);
                string folder = FolderIn(modelFolder);
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, document.Name + ".json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(document, OPTIONS));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Sun study not saved: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Reads a study.
        /// </summary>
        /// <returns>The study, or null (unreadable or inconsistent; <paramref name="error"/> says why).</returns>
        public static SunStudyDocument Read(string path, out string error)
        {
            error = null;
            try
            {
                SunStudyDocument document = JsonSerializer.Deserialize<SunStudyDocument>(File.ReadAllText(path), OPTIONS);
                if (document == null || !document.IsConsistent())
                {
                    error = "The study file is damaged";
                    return null;
                }
                document.Grid = (document.Grid ?? new SunHoursSettings()).Clean(DateTime.Today.Year);
                document.Run = (document.Run ?? new SunHoursSettings()).Clean(DateTime.Today.Year);
                if (string.IsNullOrWhiteSpace(document.Name)) { document.Name = Path.GetFileNameWithoutExtension(path); }
                return document;
            }
            catch (Exception ex)
            {
                error = "The study could not be read: " + ex.Message;
                Utilities.Log_Utils.Write($"Sun study {path} unreadable: {ex.Message}");
                return null;
            }
        }

        /// <summary>Deletes a study file.</summary>
        public static bool Delete(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
                return true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sun study not deleted: {ex.Message}");
                return false;
            }
        }
    }
}
