using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using BimGo.Edits;
using BimGo.Format;
using BimGo.Scene;
using BimGo.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Core's logger and the channel timers are process-wide: run tests one at a time
[assembly: DoNotParallelize]

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Runs once before any test: sends Core's log lines to their own file so a test run never resets the app's log.
    /// </summary>
    [TestClass]
    public sealed class TestSetup
    {
        /// <summary>
        /// Points the Core logger at %LocalAppData%\BimGo\Logs\BimGo.Tests.log.
        /// </summary>
        [AssemblyInitialize]
        public static void Initialise(TestContext context)
        {
            Log_Utils.Initialise("BimGo.Tests");
        }
    }

    /// <summary>
    /// A throwaway folder under %TEMP% that deletes itself when disposed.
    /// </summary>
    internal sealed class TempFolder : IDisposable
    {
        /// <summary>The folder path.</summary>
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BimGo.Tests", Guid.NewGuid().ToString("N"));

        /// <summary>Creates the folder.</summary>
        public TempFolder()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>A path inside the folder.</summary>
        public string File(string name) => System.IO.Path.Combine(Path, name);

        /// <inheritdoc/>
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* a locked file must not fail the test run */ }
        }
    }

    /// <summary>
    /// Builders for small scenes, documents and hand-made .bimgo files.
    /// </summary>
    internal static class TestData
    {
        /// <summary>geometry.bin magic ("BGEO"); mirrors the internal BimGoFormat.GEOMETRY_MAGIC.</summary>
        public const uint GEOMETRY_MAGIC = 0x4F454742;

        /// <summary>Catalog index of walls (first catalog entry).</summary>
        public static int WallsIndex => CategoryCatalog.Find("walls").Index;

        /// <summary>Catalog index of doors.</summary>
        public static int DoorsIndex => CategoryCatalog.Find(CategoryCatalog.KEY_DOORS).Index;

        /// <summary>
        /// A two-element scene: a host wall (opaque) and a door inside a linked model (opaque + transparent),
        /// one level, one room, one link, a parameter table and a geo-located site.
        /// </summary>
        /// <param name="library">
        /// Optional family library: a third, template element (one triangle, vertices 9..11, flagged
        /// <see cref="ElementRecord.IsLibraryTemplate"/>) is added for it (its entries should point at element 2).
        /// </param>
        public static SceneData BuildScene(LightingData lighting = null, MaterialData materials = null, LibraryData library = null)
        {
            // Three triangles: wall (0..2), door opaque (3..5), door glass (6..8); with a library a template (9..11)
            bool template = library != null;
            var vertices = new SceneVertex[template ? 12 : 9];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new SceneVertex(new Vector3(i, i * 0.5f, i * 0.25f), Vector3.UnitZ, 0xFF000000u | (uint)(i * 1000));
            }
            uint[] indices = template ? new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 } : new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

            int categories = CategoryCatalog.All.Count;
            bool[] loaded = new bool[categories];
            int[] counts = new int[categories];
            loaded[WallsIndex] = true; counts[WallsIndex] = 1;
            loaded[DoorsIndex] = true; counts[DoorsIndex] = 1;

            var parameters = new ParameterTableBuilder();
            parameters.Add("Fire Rating", "60/60/60");
            parameters.EndElement();
            parameters.Add("Fire Rating", "-/30/30");
            parameters.EndElement();

            return new SceneData
            {
                Lighting = lighting ?? LightingData.Empty,
                Materials = materials ?? MaterialData.Empty,
                Vertices = vertices,
                Indices = indices,
                Elements = new[]
                {
                    new ElementRecord
                    {
                        ElementId = 101, UniqueId = "wall-uid", HostId = 0, Name = "Basic Wall", CategoryName = "Walls",
                        FamilyType = "Generic - 200mm", LevelName = "Level 1", CategoryIndex = WallsIndex,
                        OpaqueStart = 0, OpaqueCount = 3, Bounds = new Aabb(new Vector3(0, 0, 0), new Vector3(2, 1, 0.5f)),
                        Movable = true, Pivot = new Vector3(1, 0.5f, 0), Phase = PhaseRole.Existing
                    },
                    new ElementRecord
                    {
                        ElementId = 202, UniqueId = "door-uid", HostId = 101, Name = "Single Door", CategoryName = "Doors",
                        FamilyType = "900 x 2100", LevelName = "Level 1", CategoryIndex = DoorsIndex,
                        OpaqueStart = 3, OpaqueCount = 3, TransparentStart = 6, TransparentCount = 3,
                        Bounds = new Aabb(new Vector3(3, 1.5f, 0.75f), new Vector3(8, 4, 2)),
                        Movable = false, MoveBlockReason = "Linked element", Phase = PhaseRole.New, Link = 1
                    }
                }.Concat(template ? new[]
                {
                    new ElementRecord
                    {
                        ElementId = 0, UniqueId = string.Empty, Name = "Chair", CategoryName = "Furniture", FamilyType = "Chair : Chair",
                        LevelName = "—", CategoryIndex = CategoryCatalog.Find("furniture").Index, OpaqueStart = 9, OpaqueCount = 3,
                        Bounds = new Aabb(new Vector3(9, 4.5f, -1997.75f), new Vector3(11, 5.5f, -1997.25f)), Movable = true,
                        Pivot = new Vector3(10, 5, -1997.75f), Phase = PhaseRole.New, IsLibraryTemplate = true
                    }
                } : Array.Empty<ElementRecord>()).ToArray(),
                Library = library ?? LibraryData.Empty,
                Levels = new[] { new LevelInfo("Level 1", 0f), new LevelInfo("Level 2", 3.5f) },
                Rooms = new[]
                {
                    new RoomInfo
                    {
                        Number = "G01", Name = "Lobby", BottomZ = 0f, TopZ = 3f,
                        Loops = new[] { new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 3), new Vector2(0, 3) } },
                        Min = new Vector2(0, 0), Max = new Vector2(4, 3)
                    }
                },
                Links = new[]
                {
                    new LinkInfo
                    {
                        Index = 1, Name = "Structure.rvt : 1", Title = "Structure", InstanceId = 555, InstanceUniqueId = "link-uid",
                        ModelKey = "structure-key", ModelPath = @"C:\Models\Structure.rvt",
                        OriginX = 123456.789012, OriginY = -98765.4321, OriginZ = 1.25,
                        PhaseName = "New Construction", ExistingPhaseName = "Existing", ElementCount = 1, RoomCount = 0
                    }
                },
                PhaseId = 42, PhaseName = "New Construction", ExistingPhaseId = 41, ExistingPhaseName = "Existing",
                Spawn = new SpawnInfo { Eye = new Vector3(1, 2, 1.7f), Yaw = 0.5f, Pitch = -0.1f, Source = "3D view" },
                Bounds = new Aabb(Vector3.Zero, new Vector3(8, 4, 2)),
                OriginOffset = new Vector3(10, 20, 30),
                ModelTitle = "Test Model",
                Provenance = new ModelProvenance { ModelTitle = "Test Model", ModelKey = "host-key", RevitVersion = "2026" },
                Site = new SiteInfo
                {
                    HasLocation = true, Latitude = -34.9285, Longitude = 138.6007, TimeZone = 9.5, PlaceName = "Adelaide",
                    SunStart = "2026-03-20T15:00", HasSharedTransform = true, SharedEast = 280000.123456, SharedNorth = 6130000.654321,
                    SharedElevation = 50.5, SharedAngle = 0.3
                },
                Parameters = parameters.Build(),
                CategoryLoaded = loaded,
                CategoryElementCounts = counts,
                Settings = new LaunchSettings(),
                SourceView = "{3D}",
                ProxyCount = 2,
                SkippedCount = 1,
                ExtractionTime = TimeSpan.FromSeconds(12.34)
            };
        }

        /// <summary>
        /// A document holding <see cref="BuildScene"/> plus every optional part: comments, a three-entry journal,
        /// bookmarks with a home viewpoint and thumbnail, sun settings and visibility.
        /// </summary>
        public static BimGoDocument BuildDocument(LightingData lighting = null, MaterialData materials = null, LibraryData library = null)
        {
            var journal = new EditJournal();
            journal.Add(new JournalEntry
            {
                Op = JournalOps.HIDE, Mode = JournalOps.MODE_DEMOLISH, ElementId = 101, UniqueId = "wall-uid",
                Label = "Demolish wall", Utc = new DateTime(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc), User = "gavin"
            });
            journal.Add(new JournalEntry
            {
                Op = JournalOps.TRANSFORM, ElementId = 202, UniqueId = "door-uid", Pivot = new Vector3(1, 2, 3),
                Offset = new Vector3(0.5f, -0.25f, 0), Angle = 1.5707964f, Label = "Move door", AppliedToRevit = true
            });
            journal.Add(new JournalEntry
            {
                Op = JournalOps.CLONE, ElementId = 202, UniqueId = "door-uid", NewCloneKey = 7, Offset = new Vector3(2, 0, 0),
                Label = "Clone door", AppliedToRevit = true, RevitElementId = 9001
            });

            return new BimGoDocument
            {
                Scene = BuildScene(lighting, materials, library),
                Comments = new CommentDocument
                {
                    Model = "Test Model",
                    Comments = new List<CommentRecord>
                    {
                        new() { Id = "c1", Author = "gavin", Text = "Check this door swing", X = 1.5, Y = 2.5, Z = 1.0, ElementId = 202, Level = "Level 1" },
                        new() { Id = "blank", Text = "   " } // dropped on read
                    }
                },
                Journal = journal,
                Bookmarks = new BookmarkDocument
                {
                    Model = "Test Model",
                    Bookmarks = new List<BookmarkRecord>
                    {
                        new()
                        {
                            Id = "b1", Name = "Entry", X = 1.25, Y = 2.5, Z = 1.7, Yaw = 0.75f, Pitch = -0.2f, Flying = true,
                            Level = "Level 1", Thumbnail = "iVBORw0KGgo=", Sun = new SunTime { Month = 2, Day = 28, Minutes = 600, DaylightSaving = true }
                        }
                    },
                    Home = new BookmarkRecord { Id = "home", Name = "Home", X = 0.5, Y = 0.5, Z = 1.7, Thumbnail = "AAAA" }
                },
                Sun = new SunSettings
                {
                    Enabled = true, Time = new SunTime { Month = 2, Day = 28, Minutes = 900 },
                    SunIntensity = 1.5f, SkyIntensity = 0.5f, ShadowIntensity = 0.75f, GlassTransmission = 1.25f
                },
                Visibility = new VisibilitySettings
                {
                    HiddenCategories = new List<string> { "furniture" },
                    HiddenLinks = new List<string> { "link-uid" },
                    HiddenElements = new List<HiddenElement> { new() { UniqueId = "wall-uid", Id = 101 } }
                },
                CreatedUtc = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc),
                Kind = FileKinds.EXPORT
            };
        }

        /// <summary>The writer recorded in test files.</summary>
        public static WriterInfo Writer => new("BimGo.Tests", "1.0.0");

        /// <summary>
        /// Writes a .bimgo by hand: the given JSON entries plus a geometry.bin made from the vertex and index arrays.
        /// Used for older-layout and damaged files the current writer would never produce.
        /// </summary>
        /// <param name="path">The file to create.</param>
        /// <param name="jsonEntries">Entry name → JSON text.</param>
        /// <param name="vertices">Vertices for geometry.bin (null: no geometry entry).</param>
        /// <param name="indices">Indices for geometry.bin.</param>
        /// <param name="geometryVersion">The geometry.bin layout version to write.</param>
        public static void WriteRawFile(string path, IDictionary<string, string> jsonEntries, SceneVertex[] vertices, uint[] indices, int geometryVersion = 1)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (KeyValuePair<string, string> entry in jsonEntries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Key).Open(), new UTF8Encoding(false));
                writer.Write(entry.Value);
            }

            if (vertices == null) { return; }
            using var geometry = new BinaryWriter(zip.CreateEntry("geometry.bin").Open());
            geometry.Write(GEOMETRY_MAGIC);
            geometry.Write(geometryVersion);
            geometry.Write(SceneVertex.SIZE);
            geometry.Write(vertices.Length);
            geometry.Write(indices.Length);
            geometry.Write(0);
            foreach (SceneVertex v in vertices)
            {
                geometry.Write(v.Position.X); geometry.Write(v.Position.Y); geometry.Write(v.Position.Z);
                geometry.Write(v.Normal.X); geometry.Write(v.Normal.Y); geometry.Write(v.Normal.Z);
                geometry.Write(v.Colour);
            }
            foreach (uint index in indices) { geometry.Write(index); }
        }

        /// <summary>One triangle's worth of vertices.</summary>
        public static SceneVertex[] Triangle() => new[]
        {
            new SceneVertex(new Vector3(0, 0, 0), Vector3.UnitZ, 0xFFFFFFFFu),
            new SceneVertex(new Vector3(1, 0, 0), Vector3.UnitZ, 0xFFFFFFFFu),
            new SceneVertex(new Vector3(0, 1, 0), Vector3.UnitZ, 0xFFFFFFFFu)
        };

        /// <summary>The entry names inside a zip.</summary>
        public static List<string> EntryNames(string path)
        {
            using ZipArchive zip = ZipFile.OpenRead(path);
            return zip.Entries.Select(e => e.FullName).ToList();
        }
    }
}
