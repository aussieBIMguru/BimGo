using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Constants and shared serialisation settings for the .bimgo format.
    ///
    /// A .bimgo file is a ZIP container (extension masked) holding JSON for metadata and a binary blob for geometry:
    /// <code>
    /// manifest.json     format version, generator, provenance, extraction settings, counts
    /// model.json        origin, bounds, site, phase, spawn, levels, rooms, categories
    /// elements.json     per element metadata and index ranges into geometry.bin
    /// parameters.json   optional extra parameters (pooled strings)
    /// geometry.bin      header + SceneVertex[] (28 B each) + uint[] indices, little-endian
    /// comments.json     comment markers
    /// journal.json      ordered edits (replayed on load; never baked into the geometry)
    /// bookmarks.json    saved viewpoints and the home viewpoint (optional; only written when there are some)
    /// sun.json          sun / shadow state: on/off, date, time, intensities (optional)
    /// visibility.json   hidden categories, links and elements (optional; only written when something is hidden)
    /// lighting.json     glowing vertex ranges and lighting-fixture lights (optional; only written when there are some)
    /// materials.json    material table for Realistic mode (optional; only when textures were extracted)
    /// material.bin      header + ushort material index per vertex + float2 surface coordinate (m) per vertex (optional)
    /// textures/…        embedded texture images (JPEG / PNG), referenced from materials.json (optional)
    /// </code>
    /// </summary>
    public static class BimGoFormat
    {
        /// <summary>The file extension (with the dot).</summary>
        public const string EXTENSION = ".bimgo";

        /// <summary>The format identifier written to the manifest.</summary>
        public const string FORMAT_NAME = "bimgo";

        /// <summary>
        /// The format version this build writes. Readers load this and older versions and refuse newer ones.
        /// Bump it for any change an older reader would misread; adding optional fields doesn't need a bump.
        /// </summary>
        public const int FORMAT_VERSION = 1;

        /// <summary>The comment sidecar suffix used beside a Revit model.</summary>
        public const string SIDECAR_SUFFIX = ".bimgo-comments.json";

        /// <summary>The bookmark sidecar suffix used beside a Revit model (next to the comments sidecar).</summary>
        public const string BOOKMARK_SIDECAR_SUFFIX = ".bimgo-bookmarks.json";

        /// <summary>The sun-state sidecar suffix used beside a Revit model (next to the comments sidecar).</summary>
        public const string SUN_SIDECAR_SUFFIX = ".bimgo-sun.json";

        /// <summary>The visibility sidecar suffix used beside a Revit model (next to the comments sidecar).</summary>
        public const string VISIBILITY_SIDECAR_SUFFIX = ".bimgo-visibility.json";

        /// <summary>The pre-BimGo sidecar suffix (migrated on first use).</summary>
        public const string LEGACY_SIDECAR_SUFFIX = ".rvtgo.json";

        /// <summary>The file dialog filter.</summary>
        public const string DIALOG_FILTER = "BimGo model (*.bimgo)|*.bimgo|All files (*.*)|*.*";

        #region Entry names

        internal const string ENTRY_MANIFEST = "manifest.json";
        internal const string ENTRY_MODEL = "model.json";
        internal const string ENTRY_ELEMENTS = "elements.json";
        internal const string ENTRY_PARAMETERS = "parameters.json";
        internal const string ENTRY_GEOMETRY = "geometry.bin";
        internal const string ENTRY_COMMENTS = "comments.json";
        internal const string ENTRY_JOURNAL = "journal.json";
        internal const string ENTRY_BOOKMARKS = "bookmarks.json";
        internal const string ENTRY_SUN = "sun.json";
        internal const string ENTRY_VISIBILITY = "visibility.json";
        internal const string ENTRY_LIGHTING = "lighting.json";
        internal const string ENTRY_MATERIALS = "materials.json";
        internal const string ENTRY_MATERIAL_STREAMS = "material.bin";

        /// <summary>The folder (entry name prefix) embedded texture images live under.</summary>
        public const string TEXTURE_FOLDER = "textures/";

        /// <summary>material.bin magic ("BMAT", little-endian).</summary>
        internal const uint MATERIAL_MAGIC = 0x54414D42;

        /// <summary>material.bin layout version.</summary>
        internal const int MATERIAL_VERSION = 1;

        /// <summary>geometry.bin magic ("BGEO", little-endian).</summary>
        internal const uint GEOMETRY_MAGIC = 0x4F454742;

        /// <summary>geometry.bin layout version.</summary>
        internal const int GEOMETRY_VERSION = 1;

        #endregion

        #region JSON

        /// <summary>Readable JSON (manifest, model, comments, journal).</summary>
        internal static readonly JsonSerializerOptions JSON_INDENTED = CreateOptions(indented: true);

        /// <summary>Compact JSON (elements, parameters: the large parts).</summary>
        internal static readonly JsonSerializerOptions JSON_COMPACT = CreateOptions(indented: false);

        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            options.Converters.Add(new Vector3JsonConverter());
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        #endregion

        #region Phase roles

        /// <summary>
        /// An element's phase role as written to elements.json (null for <see cref="PhaseRole.Existing"/>, the default).
        /// </summary>
        public static string FormatPhaseRole(PhaseRole role) => role switch
        {
            PhaseRole.New => "new",
            PhaseRole.Between => "between",
            PhaseRole.Unphased => "unphased",
            _ => null
        };

        /// <summary>
        /// Reads a phase role (missing or unknown values are existing).
        /// </summary>
        public static PhaseRole ParsePhaseRole(string value) => value?.Trim().ToLowerInvariant() switch
        {
            "new" => PhaseRole.New,
            "between" => PhaseRole.Between,
            "unphased" => PhaseRole.Unphased,
            _ => PhaseRole.Existing
        };

        #endregion

        /// <summary>
        /// True if a path has the .bimgo extension.
        /// </summary>
        public static bool HasExtension(string path) =>
            !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), EXTENSION, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes a <see cref="Vector3"/> as a compact [x, y, z] array (System.Text.Json ignores its fields otherwise).
    /// </summary>
    public sealed class Vector3JsonConverter : JsonConverter<Vector3>
    {
        /// <inheritdoc/>
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return Vector3.Zero; }
            if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException("Expected [x, y, z]."); }

            Span<float> values = stackalloc float[3];
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                float value = reader.GetSingle();
                if (count < 3) { values[count] = value; }
                count++;
            }
            return new Vector3(values[0], values[1], values[2]);
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Finite(value.X));
            writer.WriteNumberValue(Finite(value.Y));
            writer.WriteNumberValue(Finite(value.Z));
            writer.WriteEndArray();
        }

        private static float Finite(float value) => float.IsFinite(value) ? value : 0f;
    }
}
