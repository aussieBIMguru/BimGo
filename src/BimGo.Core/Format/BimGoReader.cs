using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BimGo.Edits;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Reads .bimgo files into a <see cref="BimGoDocument"/>. Loads this format version and older ones; refuses newer
    /// ones with a clear message. Validates index ranges so a damaged file can't crash the engine. Never throws.
    /// </summary>
    public static class BimGoReader
    {
        /// <summary>
        /// Reads only the manifest (fast: for recent lists and checks before loading).
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="error">A short reason on failure.</param>
        /// <returns>The manifest, or null.</returns>
        public static ManifestDto ReadManifest(string path, out string error)
        {
            error = null;
            try
            {
                using ZipArchive zip = ZipFile.OpenRead(path);
                return ReadJson<ManifestDto>(zip, BimGoFormat.ENTRY_MANIFEST, required: true);
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return null;
            }
        }

        /// <summary>
        /// Reads a whole file.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="settings">
        /// The viewer's settings (display options, step height...). Files only carry extraction options, so the
        /// in-game settings always come from the person opening the file.
        /// </param>
        /// <param name="error">A short reason on failure.</param>
        /// <param name="progress">
        /// Optional progress / cancellation (the caller starts the stage with <see cref="Utilities.OperationProgress.Begin"/>).
        /// A cancelled read returns null with "Cancelled.".
        /// </param>
        /// <returns>The document, or null.</returns>
        public static BimGoDocument Read(string path, LaunchSettings settings, out string error, Utilities.OperationProgress progress = null)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { throw new FileNotFoundException("The file does not exist."); }

                using ZipArchive zip = ZipFile.OpenRead(path);
                ManifestDto manifest = ReadJson<ManifestDto>(zip, BimGoFormat.ENTRY_MANIFEST, required: true);
                if (!string.Equals(manifest.Format, BimGoFormat.FORMAT_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("This is not a BimGo model.");
                }
                if (manifest.FormatVersion > BimGoFormat.FORMAT_VERSION)
                {
                    throw new InvalidDataException($"This file was made by a newer BimGo (format {manifest.FormatVersion}; this version reads up to {BimGoFormat.FORMAT_VERSION}). Please update BimGo.");
                }

                ModelDto model = ReadJson<ModelDto>(zip, BimGoFormat.ENTRY_MODEL, required: true);
                ElementsDto elements = ReadJson<ElementsDto>(zip, BimGoFormat.ENTRY_ELEMENTS, required: true);
                ParametersDto parameters = ReadJson<ParametersDto>(zip, BimGoFormat.ENTRY_PARAMETERS, required: false);
                CommentDocument comments = ReadJson<CommentDocument>(zip, BimGoFormat.ENTRY_COMMENTS, required: false) ?? new CommentDocument();
                JournalDto journal = ReadJson<JournalDto>(zip, BimGoFormat.ENTRY_JOURNAL, required: false) ?? new JournalDto();
                BookmarkDocument bookmarks = (ReadJson<BookmarkDocument>(zip, BimGoFormat.ENTRY_BOOKMARKS, required: false) ?? new BookmarkDocument()).Clean();
                SunSettings sun = ReadJson<SunSettings>(zip, BimGoFormat.ENTRY_SUN, required: false)?.Clean();
                VisibilitySettings visibility = ReadJson<VisibilitySettings>(zip, BimGoFormat.ENTRY_VISIBILITY, required: false)?.Clean();
                LightingDto lighting = ReadJson<LightingDto>(zip, BimGoFormat.ENTRY_LIGHTING, required: false);
                MaterialsDto materials = ReadOptionalJson<MaterialsDto>(zip, BimGoFormat.ENTRY_MATERIALS);
                progress?.Step(0.1);
                progress?.ThrowIfCancelled();
                ReadGeometry(zip, out SceneVertex[] vertices, out uint[] indices, progress);
                progress?.ThrowIfCancelled();

                MaterialData materialData = ReadMaterials(zip, materials, vertices.Length);
                SceneData scene = BuildScene(path, manifest, model, elements, parameters, vertices, indices, settings ?? new LaunchSettings(), lighting, materialData);
                comments.Comments ??= new List<CommentRecord>();
                comments.Comments.RemoveAll(c => c == null || string.IsNullOrWhiteSpace(c.Text));

                Utilities.Log_Utils.Write($"Read {path}: {scene.Elements.Length} elements, {scene.TriangleCount} triangles, " +
                    $"{comments.Comments.Count} comments, {journal.Entries?.Count ?? 0} journal entries, {bookmarks.Bookmarks.Count} bookmarks (format {manifest.FormatVersion}).");

                return new BimGoDocument
                {
                    Scene = scene,
                    Comments = comments,
                    Journal = new EditJournal(journal.Entries),
                    Bookmarks = bookmarks,
                    Sun = sun,
                    Visibility = visibility,
                    CreatedUtc = manifest.CreatedUtc,
                    Kind = manifest.Kind,
                    Path = path,
                    ReadFormatVersion = manifest.FormatVersion
                };
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                Utilities.Log_Utils.Write($"Could not read {path}: {ex}");
                return null;
            }
        }

        #region Scene

        private static SceneData BuildScene(string path, ManifestDto manifest, ModelDto model, ElementsDto elementsDto, ParametersDto parametersDto,
            SceneVertex[] vertices, uint[] indices, LaunchSettings settings, LightingDto lightingDto, MaterialData materials)
        {
            IReadOnlyList<CategoryDef> catalog = CategoryCatalog.All;
            int genericIndex = CategoryCatalog.Find(CategoryCatalog.KEY_GENERIC)?.Index ?? 0;

            // Map the file's categories onto this build's catalog (by key)
            List<CategoryDto> fileCategories = model.Categories ?? new List<CategoryDto>();
            int[] categoryMap = new int[fileCategories.Count];
            bool[] loaded = new bool[catalog.Count];
            int[] counts = new int[catalog.Count];
            for (int i = 0; i < fileCategories.Count; i++)
            {
                CategoryDef def = CategoryCatalog.Find(fileCategories[i]?.Key);
                if (def == null && fileCategories[i] != null)
                {
                    Utilities.Log_Utils.Write($"Unknown category '{fileCategories[i].Key}' in file; shown as generic models.");
                }
                categoryMap[i] = def?.Index ?? genericIndex;
                if (fileCategories[i]?.Loaded == true) { loaded[categoryMap[i]] = true; }
            }

            // Links: renumbered 1..n in file order (element / room link numbers outside that range read as host)
            LinkInfo[] links = (model.Links ?? new List<LinkInfo>()).Where(l => l != null).ToArray();
            for (int i = 0; i < links.Length; i++) { links[i].Index = i + 1; }

            // Elements (index ranges validated against the geometry)
            List<ElementDto> list = elementsDto.Elements ?? new List<ElementDto>();
            var records = new ElementRecord[list.Count];
            for (int e = 0; e < list.Count; e++)
            {
                ElementDto dto = list[e] ?? new ElementDto();
                int category = (uint)dto.Category < (uint)categoryMap.Length ? categoryMap[dto.Category] : genericIndex;
                (int opaqueStart, int opaqueCount) = ValidRange(dto.Opaque, indices.Length);
                (int transparentStart, int transparentCount) = ValidRange(dto.Transparent, indices.Length);

                records[e] = new ElementRecord
                {
                    ElementId = dto.Id,
                    UniqueId = dto.UniqueId ?? string.Empty,
                    HostId = dto.HostId,
                    Name = dto.Name ?? "(unnamed)",
                    CategoryName = dto.CategoryName ?? catalog[category].Label,
                    FamilyType = dto.FamilyType ?? "—",
                    LevelName = dto.Level ?? "—",
                    CategoryIndex = category,
                    OpaqueStart = opaqueStart,
                    OpaqueCount = opaqueCount,
                    TransparentStart = transparentStart,
                    TransparentCount = transparentCount,
                    Bounds = new Aabb(dto.BoundsMin, dto.BoundsMax),
                    IsProxy = dto.Proxy,
                    Movable = dto.Movable,
                    MoveBlockReason = dto.Movable ? null : (dto.MoveBlockReason ?? "Not movable"),
                    Pivot = dto.Pivot,
                    Phase = BimGoFormat.ParsePhaseRole(dto.Phase),
                    Link = ValidLink(dto.Link, links.Length)
                };
                counts[category]++;
                loaded[category] = true;
            }

            // Indices must point at real vertices
            uint vertexCount = (uint)vertices.Length;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= vertexCount) { throw new InvalidDataException("The geometry is damaged (index out of range)."); }
            }

            // Levels, rooms, spawn
            LevelInfo[] levels = (model.Levels ?? new List<LevelDto>())
                .Where(l => l != null)
                .Select(l => new LevelInfo(l.Name ?? "Level", l.Elevation))
                .OrderBy(l => l.Elevation)
                .ToArray();

            var rooms = new List<RoomInfo>();
            foreach (RoomDto room in model.Rooms ?? new List<RoomDto>())
            {
                if (room?.Loops == null) { continue; }
                var loops = new List<Vector2[]>();
                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                foreach (float[] flat in room.Loops)
                {
                    if (flat == null || flat.Length < 6) { continue; }
                    var loop = new Vector2[flat.Length / 2];
                    for (int i = 0; i < loop.Length; i++)
                    {
                        loop[i] = new Vector2(flat[i * 2], flat[i * 2 + 1]);
                        min = Vector2.Min(min, loop[i]);
                        max = Vector2.Max(max, loop[i]);
                    }
                    loops.Add(loop);
                }
                if (loops.Count == 0) { continue; }
                rooms.Add(new RoomInfo
                {
                    Number = room.Number ?? "—",
                    Name = room.Name ?? "Room",
                    Loops = loops.ToArray(),
                    Min = min,
                    Max = max,
                    BottomZ = room.BottomZ,
                    TopZ = room.TopZ,
                    Link = ValidLink(room.Link, links.Length)
                });
            }

            SpawnInfo spawn = model.Spawn == null ? null : new SpawnInfo
            {
                Eye = model.Spawn.Eye,
                Yaw = model.Spawn.Yaw,
                Pitch = model.Spawn.Pitch,
                Source = model.Spawn.Source ?? "saved view"
            };

            Aabb bounds = new(model.BoundsMin, model.BoundsMax);
            if (!bounds.IsValid)
            {
                bounds = Aabb.Empty;
                foreach (ElementRecord record in records) { bounds.Include(record.Bounds); }
                if (!bounds.IsValid) { bounds = new Aabb(new Vector3(-10, -10, 0), new Vector3(10, 10, 3)); }
            }

            ExtractionDto extraction = manifest.Extraction ?? new ExtractionDto();
            return new SceneData
            {
                Vertices = vertices,
                Indices = indices,
                Elements = records,
                Levels = levels,
                Rooms = rooms.ToArray(),
                Links = links,
                PhaseId = model.PhaseId,
                PhaseName = model.PhaseName,
                ExistingPhaseId = model.ExistingPhaseId,
                ExistingPhaseName = model.ExistingPhaseName,
                PhaseNote = model.PhaseNote,
                Spawn = spawn,
                Bounds = bounds,
                OriginOffset = model.OriginOffset,
                ModelTitle = string.IsNullOrWhiteSpace(manifest.Title) ? System.IO.Path.GetFileNameWithoutExtension(path) : manifest.Title,
                CommentsPath = manifest.Kind == FileKinds.SNAPSHOT ? manifest.CommentsSidecar : null,
                Provenance = manifest.Provenance ?? new ModelProvenance(),
                Site = model.Site ?? new SiteInfo(),
                Parameters = BuildParameters(parametersDto, records.Length),
                Lighting = BuildLighting(lightingDto, vertices.Length, records.Length),
                Materials = materials ?? MaterialData.Empty,
                CategoryLoaded = loaded,
                CategoryElementCounts = counts,
                Settings = settings,
                SourceView = extraction.ActiveView,
                ProxyCount = extraction.ProxyCount,
                SkippedCount = extraction.SkippedCount,
                ExtractionTime = TimeSpan.FromSeconds(Math.Max(0, extraction.ExtractionSeconds))
            };
        }

        /// <summary>
        /// A link number from the file, or 0 (host) when absent or out of range.
        /// </summary>
        private static int ValidLink(int? link, int linkCount) => link is int n && n > 0 && n <= linkCount ? n : 0;

        /// <summary>
        /// A [start, count] pair clamped to the index buffer (and to whole triangles); empty if invalid.
        /// </summary>
        private static (int Start, int Count) ValidRange(int[] range, int indexCount)
        {
            if (range == null || range.Length < 2) { return (0, 0); }
            int start = range[0], count = range[1];
            if (start < 0 || count <= 0 || start > indexCount || (long)start + count > indexCount) { return (0, 0); }
            return (start, count - count % 3);
        }

        /// <summary>
        /// The optional lighting entry, validated: runs inside the vertex array, in order and not overlapping; lights on
        /// real elements with finite values. Anything damaged is dropped (lighting is decoration, never worth a failed load).
        /// </summary>
        private static LightingData BuildLighting(LightingDto dto, int vertexCount, int elementCount)
        {
            if (dto == null) { return LightingData.Empty; }

            var runs = new List<EmissiveRun>();
            long next = 0;
            foreach (long[] run in dto.Emissive ?? new List<long[]>())
            {
                if (run == null || run.Length < 3) { continue; }
                long start = run[0], count = run[1];
                if (start < next || count <= 0 || start + count > vertexCount || run[2] < 0 || run[2] > uint.MaxValue) { continue; }
                runs.Add(new EmissiveRun((int)start, (int)count, (uint)run[2]));
                next = start + count;
            }

            var lights = new List<LightSource>();
            foreach (LightDto light in dto.Lights ?? new List<LightDto>())
            {
                if (light == null || light.Element < 0 || light.Element >= elementCount) { continue; }
                Vector3 p = light.Position;
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) { continue; }
                lights.Add(new LightSource
                {
                    Element = light.Element,
                    Position = p,
                    Lumens = float.IsFinite(light.Lumens) ? Math.Clamp(light.Lumens, 10f, 100000f) : 1000f,
                    Kelvin = float.IsFinite(light.Kelvin) ? Math.Clamp(light.Kelvin, 1000f, 15000f) : 3500f,
                    Downward = float.IsFinite(light.Downward) ? Math.Clamp(light.Downward, 0f, 1f) : 0.7f,
                    Estimated = light.Estimated
                });
            }

            return runs.Count == 0 && lights.Count == 0
                ? LightingData.Empty
                : new LightingData { Emissive = runs.ToArray(), Lights = lights.ToArray() };
        }

        /// <summary>
        /// The optional material table, per-vertex streams and images, validated: the streams must match the vertex
        /// count (else everything is dropped), indices past the table read as no material, and a texture whose image
        /// entry is missing reads as missing. Never throws: materials are decoration, never worth a failed load.
        /// </summary>
        private static MaterialData ReadMaterials(ZipArchive zip, MaterialsDto dto, int vertexCount)
        {
            if (dto?.Materials == null || dto.Materials.Count == 0) { return MaterialData.Empty; }
            try
            {
                SceneMaterial[] table = dto.Materials
                    .Take(MaterialData.MAX_MATERIALS)
                    .Select(m => (m ?? new SceneMaterial()).Clean())
                    .ToArray();

                ZipArchiveEntry entry = zip.GetEntry(BimGoFormat.ENTRY_MATERIAL_STREAMS);
                if (entry == null) { return MaterialData.Empty; }
                ushort[] indices;
                Vector2[] uvs = Array.Empty<Vector2>();
                using (Stream stream = entry.Open())
                using (var header = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (header.ReadUInt32() != BimGoFormat.MATERIAL_MAGIC) { throw new InvalidDataException("bad material.bin header"); }
                    if (header.ReadInt32() > BimGoFormat.MATERIAL_VERSION) { throw new InvalidDataException("material.bin from a newer BimGo"); }
                    int count = header.ReadInt32();
                    int flags = header.ReadInt32();
                    if (count != vertexCount) { throw new InvalidDataException($"material.bin has {count} vertices, the geometry {vertexCount}"); }

                    indices = new ushort[count];
                    stream.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
                    if ((flags & 1) != 0)
                    {
                        uvs = new Vector2[count];
                        stream.ReadExactly(MemoryMarshal.AsBytes(uvs.AsSpan()));
                        for (int i = 0; i < uvs.Length; i++)
                        {
                            if (!float.IsFinite(uvs[i].X) || !float.IsFinite(uvs[i].Y)) { uvs[i] = Vector2.Zero; }
                        }
                    }
                }
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] >= table.Length) { indices[i] = MaterialData.NONE; }
                }

                // Images: only referenced entries under textures/, each read once
                var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (SceneMaterial material in table)
                {
                    if (material.Texture == null) { continue; }
                    if (!textures.ContainsKey(material.Texture))
                    {
                        byte[] bytes = material.Texture.StartsWith(BimGoFormat.TEXTURE_FOLDER, StringComparison.Ordinal)
                            ? ReadBytes(zip, material.Texture)
                            : null;
                        if (bytes != null) { textures[material.Texture] = bytes; }
                    }
                    if (!textures.ContainsKey(material.Texture))
                    {
                        material.Texture = null;
                        if (material.TextureState == TextureState.Embedded) { material.TextureState = TextureState.Missing; }
                    }
                }

                return new MaterialData
                {
                    Materials = table,
                    VertexMaterial = indices,
                    VertexUv = uvs,
                    Textures = textures,
                    TextureMaxSize = MaterialData.NearestTextureSize(dto.TextureMaxSize)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Utilities.Log_Utils.Write($"Materials ignored (damaged): {ex.Message}");
                return MaterialData.Empty;
            }
        }

        /// <summary>An entry's bytes (capped at 64 MB), or null if absent or unreadable.</summary>
        private static byte[] ReadBytes(ZipArchive zip, string name)
        {
            try
            {
                ZipArchiveEntry entry = zip.GetEntry(name);
                if (entry == null || entry.Length <= 0 || entry.Length > 64L * 1024 * 1024) { return null; }
                byte[] bytes = new byte[entry.Length];
                using Stream stream = entry.Open();
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Texture {name} unreadable: {ex.Message}");
                return null;
            }
        }

        private static ParameterTable BuildParameters(ParametersDto dto, int elementCount)
        {
            if (dto?.Names == null || dto.Names.Length == 0) { return ParameterTable.Empty; }
            int[][] rows = new int[elementCount][];
            if (dto.Rows != null)
            {
                Array.Copy(dto.Rows, rows, Math.Min(dto.Rows.Length, elementCount));
            }
            return new ParameterTable(dto.Names, dto.Values ?? Array.Empty<string>(), rows);
        }

        #endregion

        #region Entries

        private static void ReadGeometry(ZipArchive zip, out SceneVertex[] vertices, out uint[] indices, Utilities.OperationProgress progress)
        {
            ZipArchiveEntry entry = zip.GetEntry(BimGoFormat.ENTRY_GEOMETRY) ?? throw new InvalidDataException("The file has no geometry.");
            using Stream stream = entry.Open();
            using var header = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (header.ReadUInt32() != BimGoFormat.GEOMETRY_MAGIC) { throw new InvalidDataException("The geometry is damaged (bad header)."); }
            int version = header.ReadInt32();
            if (version > BimGoFormat.GEOMETRY_VERSION) { throw new InvalidDataException("The geometry was written by a newer BimGo."); }
            int vertexSize = header.ReadInt32();
            int vertexCount = header.ReadInt32();
            int indexCount = header.ReadInt32();
            header.ReadInt32(); // reserved

            if (vertexSize != SceneVertex.SIZE) { throw new InvalidDataException($"Unsupported vertex layout ({vertexSize} bytes)."); }
            if (vertexCount < 0 || indexCount < 0 || indexCount % 3 != 0) { throw new InvalidDataException("The geometry is damaged (bad counts)."); }

            vertices = new SceneVertex[vertexCount];
            indices = new uint[indexCount];
            // Geometry is most of the work: it fills the bar from 10 % to 92 %
            long total = (long)vertexCount * SceneVertex.SIZE + (long)indexCount * sizeof(uint);
            long done = 0;
            ReadChunked(stream, MemoryMarshal.AsBytes(vertices.AsSpan()), progress, ref done, total);
            ReadChunked(stream, MemoryMarshal.AsBytes(indices.AsSpan()), progress, ref done, total);
        }

        /// <summary>
        /// Fills a span from a stream (deflate streams return partial reads).
        /// </summary>
        private static void ReadChunked(Stream stream, Span<byte> bytes, Utilities.OperationProgress progress, ref long done, long total)
        {
            const int CHUNK = 1 << 20;
            while (bytes.Length > 0)
            {
                int length = Math.Min(CHUNK, bytes.Length);
                stream.ReadExactly(bytes[..length]);
                bytes = bytes[length..];
                done += length;
                if (progress != null)
                {
                    progress.Step(0.1 + 0.82 * (total <= 0 ? 1.0 : (double)done / total));
                    progress.ThrowIfCancelled();
                }
            }
        }

        /// <summary>
        /// An optional entry that must never fail the load (damaged JSON reads as absent, and is logged).
        /// </summary>
        private static T ReadOptionalJson<T>(ZipArchive zip, string name) where T : class
        {
            try { return ReadJson<T>(zip, name, required: false); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                Utilities.Log_Utils.Write($"{name} ignored (damaged): {ex.Message}");
                return null;
            }
        }

        private static T ReadJson<T>(ZipArchive zip, string name, bool required) where T : class
        {
            ZipArchiveEntry entry = zip.GetEntry(name);
            if (entry == null)
            {
                if (required) { throw new InvalidDataException($"The file is incomplete ({name} is missing)."); }
                return null;
            }

            using Stream stream = entry.Open();
            T value = JsonSerializer.Deserialize<T>(stream, BimGoFormat.JSON_INDENTED);
            if (value == null && required) { throw new InvalidDataException($"The file is damaged ({name} is empty)."); }
            return value;
        }

        /// <summary>
        /// A user-facing reason for a read failure.
        /// </summary>
        private static string Describe(Exception ex) => ex switch
        {
            OperationCanceledException => "Cancelled.",
            InvalidDataException => ex.Message,
            FileNotFoundException => "The file does not exist.",
            DirectoryNotFoundException => "The folder does not exist.",
            UnauthorizedAccessException => "Access to the file was denied.",
            JsonException => "The file is damaged (unreadable data).",
            EndOfStreamException => "The file is damaged (it ends early).",
            IOException io when io.HResult == unchecked((int)0x80070020) => "The file is open in another program.",
            _ => ex.Message
        };

        #endregion
    }
}
