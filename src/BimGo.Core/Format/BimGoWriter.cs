using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using BimGo.Edits;
using BimGo.Scene;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Writes .bimgo files. Writes go to a temporary file beside the target which then replaces it, so a failed or
    /// interrupted save never leaves a half-written model behind. Never throws: failures return false with a reason.
    /// </summary>
    public static class BimGoWriter
    {
        /// <summary>
        /// Writes a document.
        /// </summary>
        /// <param name="path">The target path (normally ending in .bimgo).</param>
        /// <param name="document">The document to write.</param>
        /// <param name="writer">Who is writing (recorded in the manifest).</param>
        /// <param name="kind">How the file came about (<see cref="FileKinds"/>).</param>
        /// <param name="error">A short reason on failure, else null.</param>
        /// <param name="geometryCompression">
        /// Compression of geometry.bin: <see cref="CompressionLevel.Fastest"/> for saved files,
        /// <see cref="CompressionLevel.NoCompression"/> for throwaway live snapshots where speed matters more.
        /// </param>
        /// <param name="progress">
        /// Optional progress / cancellation (the caller starts the stage with <see cref="Utilities.OperationProgress.Begin"/>).
        /// A cancelled write leaves the target untouched and returns false with "Cancelled.".
        /// </param>
        /// <returns>True on success.</returns>
        public static bool Write(string path, BimGoDocument document, WriterInfo writer, string kind, out string error,
            CompressionLevel geometryCompression = CompressionLevel.Fastest, Utilities.OperationProgress progress = null)
        {
            error = null;
            string temp = path + ".tmp";
            try
            {
                if (document?.Scene == null) { throw new ArgumentException("There is no model to save."); }

                string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
                if (File.Exists(temp)) { File.Delete(temp); }

                SceneData scene = document.Scene;
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    WriteJson(zip, BimGoFormat.ENTRY_MANIFEST, BuildManifest(document, writer, kind), BimGoFormat.JSON_INDENTED);
                    WriteJson(zip, BimGoFormat.ENTRY_MODEL, BuildModel(scene), BimGoFormat.JSON_INDENTED);
                    WriteJson(zip, BimGoFormat.ENTRY_ELEMENTS, BuildElements(scene), BimGoFormat.JSON_COMPACT);
                    progress?.Step(0.12);
                    progress?.ThrowIfCancelled();
                    if (!scene.Parameters.IsEmpty)
                    {
                        var parameters = new ParametersDto { Names = scene.Parameters.Names, Values = scene.Parameters.Values, Rows = scene.Parameters.Rows };
                        WriteJson(zip, BimGoFormat.ENTRY_PARAMETERS, parameters, BimGoFormat.JSON_COMPACT);
                    }
                    WriteGeometry(zip, scene, geometryCompression, progress);
                    progress?.ThrowIfCancelled();
                    WriteJson(zip, BimGoFormat.ENTRY_COMMENTS, document.Comments ?? new CommentDocument(), BimGoFormat.JSON_INDENTED);
                    var journal = new JournalDto { Entries = document.Journal?.Entries.ToList() ?? new List<JournalEntry>() };
                    WriteJson(zip, BimGoFormat.ENTRY_JOURNAL, journal, BimGoFormat.JSON_INDENTED);

                    // Optional part: older readers ignore entries they don't know, so no format bump is needed
                    if (document.Bookmarks != null && !document.Bookmarks.IsEmpty)
                    {
                        WriteJson(zip, BimGoFormat.ENTRY_BOOKMARKS, document.Bookmarks, BimGoFormat.JSON_INDENTED);
                    }
                    if (document.Visibility != null && !document.Visibility.IsEmpty)
                    {
                        WriteJson(zip, BimGoFormat.ENTRY_VISIBILITY, document.Visibility, BimGoFormat.JSON_INDENTED);
                    }
                    if (document.Sun != null)
                    {
                        WriteJson(zip, BimGoFormat.ENTRY_SUN, document.Sun, BimGoFormat.JSON_INDENTED);
                    }
                    if (scene.Lighting != null && !scene.Lighting.IsEmpty)
                    {
                        WriteJson(zip, BimGoFormat.ENTRY_LIGHTING, BuildLighting(scene.Lighting), BimGoFormat.JSON_COMPACT);
                    }
                    MaterialData materials = document.Materials ?? scene.Materials;
                    if (materials != null && !materials.IsEmpty && materials.VertexMaterial.Length == scene.Vertices.Length)
                    {
                        WriteMaterials(zip, materials, geometryCompression);
                    }
                }

                // Past this point the write completes: the replace is quick and must not be half done
                if (progress != null) { progress.CanCancel = false; }
                File.Move(temp, path, overwrite: true);
                progress?.Step(1.0);
                Utilities.Log_Utils.Write($"Wrote {path} ({scene.Elements.Length} elements, {document.Journal?.Count ?? 0} journal entries).");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"Could not write {path}: {ex}");
                try { if (File.Exists(temp)) { File.Delete(temp); } }
                catch { /* best effort */ }
                return false;
            }
        }

        #region Parts

        private static ManifestDto BuildManifest(BimGoDocument document, WriterInfo writer, string kind)
        {
            SceneData scene = document.Scene;
            LaunchSettings settings = scene.Settings ?? new LaunchSettings();
            return new ManifestDto
            {
                Generator = writer.Generator ?? string.Empty,
                GeneratorVersion = writer.Version ?? string.Empty,
                Kind = kind ?? document.Kind ?? FileKinds.SAVE,
                Title = scene.ModelTitle ?? string.Empty,
                CreatedUtc = document.CreatedUtc == default ? DateTime.UtcNow : document.CreatedUtc,
                SavedUtc = DateTime.UtcNow,
                SavedBy = Environment.UserName,
                Provenance = scene.Provenance ?? new ModelProvenance(),
                CommentsSidecar = kind == FileKinds.SNAPSHOT ? scene.CommentsPath : null,
                Extraction = new ExtractionDto
                {
                    EnabledCategories = settings.EnabledCategories?.ToList() ?? new List<string>(),
                    TriangleThreshold = settings.TriangleThreshold,
                    OverLimit = settings.OverLimit.ToString(),
                    ExtraParameters = scene.Parameters.Names.ToList(),
                    ProxyCount = scene.ProxyCount,
                    SkippedCount = scene.SkippedCount,
                    ExtractionSeconds = Math.Round(scene.ExtractionTime.TotalSeconds, 2),
                    ActiveView = scene.SourceView
                },
                Counts = new CountsDto
                {
                    Elements = scene.Elements.Length,
                    Vertices = scene.Vertices.Length,
                    Indices = scene.Indices.Length,
                    Levels = scene.Levels.Length,
                    Rooms = scene.Rooms.Length,
                    Comments = document.Comments?.Comments?.Count ?? 0,
                    JournalEntries = document.Journal?.Count ?? 0,
                    Bookmarks = document.Bookmarks?.Bookmarks?.Count ?? 0,
                    Links = scene.Links?.Length ?? 0
                }
            };
        }

        private static ModelDto BuildModel(SceneData scene)
        {
            var model = new ModelDto
            {
                OriginOffset = scene.OriginOffset,
                BoundsMin = scene.Bounds.Min,
                BoundsMax = scene.Bounds.Max,
                Site = scene.Site ?? new SiteInfo(),
                PhaseId = scene.PhaseId,
                PhaseName = scene.PhaseName,
                ExistingPhaseId = scene.ExistingPhaseId,
                ExistingPhaseName = scene.ExistingPhaseName,
                PhaseNote = scene.PhaseNote,
                Links = scene.Links != null && scene.Links.Length > 0 ? scene.Links.ToList() : null,
                Spawn = scene.Spawn == null ? null : new SpawnDto
                {
                    Eye = scene.Spawn.Eye,
                    Yaw = scene.Spawn.Yaw,
                    Pitch = scene.Spawn.Pitch,
                    Source = scene.Spawn.Source
                }
            };

            foreach (LevelInfo level in scene.Levels)
            {
                model.Levels.Add(new LevelDto { Name = level.Name, Elevation = level.Elevation });
            }

            foreach (RoomInfo room in scene.Rooms)
            {
                var dto = new RoomDto { Number = room.Number, Name = room.Name, BottomZ = room.BottomZ, TopZ = room.TopZ, Link = room.Link > 0 ? room.Link : null };
                foreach (System.Numerics.Vector2[] loop in room.Loops)
                {
                    float[] flat = new float[loop.Length * 2];
                    for (int i = 0; i < loop.Length; i++)
                    {
                        flat[i * 2] = loop[i].X;
                        flat[i * 2 + 1] = loop[i].Y;
                    }
                    dto.Loops.Add(flat);
                }
                model.Rooms.Add(dto);
            }

            // Category list in catalog order: element category indices refer to positions in this list
            foreach (CategoryDef def in CategoryCatalog.All)
            {
                model.Categories.Add(new CategoryDto
                {
                    Key = def.Key,
                    Loaded = def.Index < scene.CategoryLoaded.Length && scene.CategoryLoaded[def.Index],
                    Count = def.Index < scene.CategoryElementCounts.Length ? scene.CategoryElementCounts[def.Index] : 0
                });
            }
            return model;
        }

        private static LightingDto BuildLighting(LightingData lighting)
        {
            var dto = new LightingDto();
            foreach (EmissiveRun run in lighting.Emissive)
            {
                dto.Emissive.Add(new long[] { run.Start, run.Count, run.Emissive });
            }
            foreach (LightSource light in lighting.Lights)
            {
                dto.Lights.Add(new LightDto
                {
                    Element = light.Element,
                    Position = light.Position,
                    Lumens = light.Lumens,
                    Kelvin = light.Kelvin,
                    Downward = light.Downward,
                    Estimated = light.Estimated
                });
            }
            return dto;
        }

        private static ElementsDto BuildElements(SceneData scene)
        {
            var dto = new ElementsDto { Elements = new List<ElementDto>(scene.Elements.Length) };
            foreach (ElementRecord record in scene.Elements)
            {
                dto.Elements.Add(new ElementDto
                {
                    Id = record.ElementId,
                    UniqueId = string.IsNullOrEmpty(record.UniqueId) ? null : record.UniqueId,
                    Name = record.Name,
                    Category = record.CategoryIndex,
                    CategoryName = record.CategoryName,
                    FamilyType = record.FamilyType,
                    Level = record.LevelName,
                    HostId = record.HostId,
                    Proxy = record.IsProxy,
                    Movable = record.Movable,
                    MoveBlockReason = record.MoveBlockReason,
                    Pivot = record.Pivot,
                    Phase = BimGoFormat.FormatPhaseRole(record.Phase),
                    Link = record.Link > 0 ? record.Link : null,
                    BoundsMin = record.Bounds.Min,
                    BoundsMax = record.Bounds.Max,
                    Opaque = record.OpaqueCount > 0 ? new[] { record.OpaqueStart, record.OpaqueCount } : null,
                    Transparent = record.TransparentCount > 0 ? new[] { record.TransparentStart, record.TransparentCount } : null
                });
            }
            return dto;
        }

        /// <summary>
        /// geometry.bin: a small header, then the raw vertex and index arrays (little-endian, as in memory on x64).
        /// </summary>
        private static void WriteGeometry(ZipArchive zip, SceneData scene, CompressionLevel compression, Utilities.OperationProgress progress)
        {
            ZipArchiveEntry entry = zip.CreateEntry(BimGoFormat.ENTRY_GEOMETRY, compression);
            using Stream stream = entry.Open();
            using (var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                header.Write(BimGoFormat.GEOMETRY_MAGIC);
                header.Write(BimGoFormat.GEOMETRY_VERSION);
                header.Write(SceneVertex.SIZE);
                header.Write(scene.Vertices.Length);
                header.Write(scene.Indices.Length);
                header.Write(0); // reserved
            }

            // Geometry is most of the work: it fills the bar from 12 % to 95 %
            long total = (long)scene.Vertices.Length * SceneVertex.SIZE + (long)scene.Indices.Length * sizeof(uint);
            long done = 0;
            WriteChunked(stream, MemoryMarshal.AsBytes(scene.Vertices.AsSpan()), progress, ref done, total);
            WriteChunked(stream, MemoryMarshal.AsBytes(scene.Indices.AsSpan()), progress, ref done, total);
        }

        /// <summary>
        /// materials.json (the table), material.bin (index + surface coordinate per vertex) and the referenced images
        /// under textures/ (stored as-is: they are already JPEG / PNG).
        /// </summary>
        private static void WriteMaterials(ZipArchive zip, MaterialData materials, CompressionLevel compression)
        {
            var dto = new MaterialsDto { TextureMaxSize = materials.TextureMaxSize, Materials = materials.Materials.ToList() };
            WriteJson(zip, BimGoFormat.ENTRY_MATERIALS, dto, BimGoFormat.JSON_COMPACT);

            bool hasUv = materials.VertexUv.Length == materials.VertexMaterial.Length;
            ZipArchiveEntry entry = zip.CreateEntry(BimGoFormat.ENTRY_MATERIAL_STREAMS, compression);
            using (Stream stream = entry.Open())
            {
                using (var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    header.Write(BimGoFormat.MATERIAL_MAGIC);
                    header.Write(BimGoFormat.MATERIAL_VERSION);
                    header.Write(materials.VertexMaterial.Length);
                    header.Write(hasUv ? 1 : 0); // flags: bit 0 = surface coordinates follow
                }
                stream.Write(MemoryMarshal.AsBytes(materials.VertexMaterial.AsSpan()));
                if (hasUv) { stream.Write(MemoryMarshal.AsBytes(materials.VertexUv.AsSpan())); }
            }

            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (SceneMaterial material in materials.Materials)
            {
                string name = material?.Texture;
                if (name == null || !written.Add(name)) { continue; }
                if (!name.StartsWith(BimGoFormat.TEXTURE_FOLDER, StringComparison.Ordinal)) { continue; }
                if (!materials.Textures.TryGetValue(name, out byte[] bytes) || bytes == null || bytes.Length == 0) { continue; }
                ZipArchiveEntry image = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using Stream stream = image.Open();
                stream.Write(bytes);
            }
        }

        /// <summary>
        /// Writes a large span in pieces (keeps the deflate stream's buffers small).
        /// </summary>
        private static void WriteChunked(Stream stream, ReadOnlySpan<byte> bytes, Utilities.OperationProgress progress, ref long done, long total)
        {
            const int CHUNK = 1 << 20;
            while (bytes.Length > 0)
            {
                int length = Math.Min(CHUNK, bytes.Length);
                stream.Write(bytes[..length]);
                bytes = bytes[length..];
                done += length;
                if (progress != null)
                {
                    progress.Step(0.12 + 0.83 * (total <= 0 ? 1.0 : (double)done / total));
                    progress.ThrowIfCancelled();
                }
            }
        }

        private static void WriteJson<T>(ZipArchive zip, string name, T value, JsonSerializerOptions options)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            JsonSerializer.Serialize(stream, value, options);
        }

        #endregion
    }
}
