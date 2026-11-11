using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using BimGo.Edits;
using BimGo.Format;
using BimGo.Scene;
using BimGo.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// .bimgo write → read: everything the writer stores comes back unchanged.
    /// </summary>
    [TestClass]
    public sealed class BimGoRoundTripTests
    {
        private static BimGoDocument WriteAndRead(BimGoDocument document, TempFolder folder, string kind = FileKinds.SAVE)
        {
            string path = folder.File("model.bimgo");
            bool written = BimGoWriter.Write(path, document, TestData.Writer, kind, out string writeError);
            Assert.IsTrue(written, writeError);

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out string readError);
            Assert.IsNotNull(read, readError);
            return read;
        }

        [TestMethod]
        public void Manifest_RecordsWriterKindCountsAndKeepsCreatedTime()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            string path = folder.File("model.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);

            ManifestDto manifest = BimGoReader.ReadManifest(path, out error);
            Assert.IsNotNull(manifest, error);
            Assert.AreEqual(BimGoFormat.FORMAT_NAME, manifest.Format);
            Assert.AreEqual(BimGoFormat.FORMAT_VERSION, manifest.FormatVersion);
            Assert.AreEqual("BimGo.Tests", manifest.Generator);
            Assert.AreEqual("0.0.0-test", manifest.GeneratorVersion);
            Assert.AreEqual(FileKinds.SAVE, manifest.Kind);
            Assert.AreEqual("Test Model", manifest.Title);
            Assert.AreEqual(document.CreatedUtc, manifest.CreatedUtc);
            Assert.AreEqual(2, manifest.Counts.Elements);
            Assert.AreEqual(9, manifest.Counts.Vertices);
            Assert.AreEqual(9, manifest.Counts.Indices);
            Assert.AreEqual(2, manifest.Counts.Levels);
            Assert.AreEqual(1, manifest.Counts.Rooms);
            Assert.AreEqual(3, manifest.Counts.JournalEntries);
            Assert.AreEqual(1, manifest.Counts.Bookmarks);
            Assert.AreEqual(1, manifest.Counts.Links);
            Assert.AreEqual("{3D}", manifest.Extraction.ActiveView);
        }

        [TestMethod]
        public void Geometry_RoundTripsExactly()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            BimGoDocument read = WriteAndRead(document, folder);

            CollectionAssert.AreEqual(document.Scene.Indices, read.Scene.Indices);
            Assert.AreEqual(document.Scene.Vertices.Length, read.Scene.Vertices.Length);
            for (int i = 0; i < document.Scene.Vertices.Length; i++)
            {
                Assert.AreEqual(document.Scene.Vertices[i].Position, read.Scene.Vertices[i].Position);
                Assert.AreEqual(document.Scene.Vertices[i].Normal, read.Scene.Vertices[i].Normal);
                Assert.AreEqual(document.Scene.Vertices[i].Colour, read.Scene.Vertices[i].Colour);
            }
            Assert.AreEqual(3, read.Scene.TriangleCount);
        }

        [TestMethod]
        public void Elements_RoundTripWithRangesPhasesAndLinks()
        {
            using var folder = new TempFolder();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(), folder);
            ElementRecord wall = read.Scene.Elements[0];
            ElementRecord door = read.Scene.Elements[1];

            Assert.AreEqual(101L, wall.ElementId);
            Assert.AreEqual("wall-uid", wall.UniqueId);
            Assert.AreEqual(TestData.WallsIndex, wall.CategoryIndex);
            Assert.AreEqual(0, wall.OpaqueStart);
            Assert.AreEqual(3, wall.OpaqueCount);
            Assert.AreEqual(0, wall.TransparentCount);
            Assert.IsTrue(wall.Movable);
            Assert.IsNull(wall.MoveBlockReason);
            Assert.AreEqual(new Vector3(1, 0.5f, 0), wall.Pivot);
            Assert.AreEqual(PhaseRole.Existing, wall.Phase);
            Assert.AreEqual(0, wall.Link);

            Assert.AreEqual(202L, door.ElementId);
            Assert.AreEqual(101L, door.HostId);
            Assert.AreEqual(TestData.DoorsIndex, door.CategoryIndex);
            Assert.AreEqual("900 x 2100", door.FamilyType);
            Assert.AreEqual(3, door.OpaqueStart);
            Assert.AreEqual(6, door.TransparentStart);
            Assert.AreEqual(3, door.TransparentCount);
            Assert.IsFalse(door.Movable);
            Assert.AreEqual("Linked element", door.MoveBlockReason);
            Assert.AreEqual(PhaseRole.New, door.Phase);
            Assert.AreEqual(1, door.Link);
            Assert.AreEqual(new Vector3(8, 4, 2), door.Bounds.Max);

            Assert.IsTrue(read.Scene.CategoryLoaded[TestData.WallsIndex]);
            Assert.AreEqual(1, read.Scene.CategoryElementCounts[TestData.DoorsIndex]);
        }

        [TestMethod]
        public void Model_RoundTripsLevelsRoomsLinksSitePhasesAndSpawn()
        {
            using var folder = new TempFolder();
            SceneData scene = WriteAndRead(TestData.BuildDocument(), folder).Scene;

            Assert.AreEqual(2, scene.Levels.Length);
            Assert.AreEqual("Level 2", scene.Levels[1].Name);
            Assert.AreEqual(3.5f, scene.Levels[1].Elevation);

            Assert.AreEqual(1, scene.Rooms.Length);
            Assert.AreEqual("G01", scene.Rooms[0].Number);
            Assert.AreEqual(4, scene.Rooms[0].Loops[0].Length);
            Assert.AreEqual(new Vector2(4, 3), scene.Rooms[0].Max);

            Assert.AreEqual(1, scene.Links.Length);
            LinkInfo link = scene.Links[0];
            Assert.AreEqual(1, link.Index);
            Assert.AreEqual("link-uid", link.InstanceUniqueId);
            Assert.AreEqual(123456.789012, link.OriginX); // double precision survives
            Assert.AreEqual(-98765.4321, link.OriginY);
            Assert.AreSame(link, scene.LinkOf(scene.Elements[1]));

            Assert.IsTrue(scene.Site.HasLocation);
            Assert.AreEqual(-34.9285, scene.Site.Latitude);
            Assert.AreEqual(9.5, scene.Site.TimeZone);
            Assert.AreEqual("2026-03-20T15:00", scene.Site.SunStart);
            Assert.AreEqual(280000.123456, scene.Site.SharedEast);
            Assert.AreEqual(0.3, scene.Site.SharedAngle);

            Assert.AreEqual(42L, scene.PhaseId);
            Assert.AreEqual("Existing", scene.ExistingPhaseName);
            Assert.AreEqual(new Vector3(10, 20, 30), scene.OriginOffset);
            Assert.AreEqual(new Vector3(1, 2, 1.7f), scene.Spawn.Eye);
            Assert.AreEqual("3D view", scene.Spawn.Source);
            Assert.AreEqual("{3D}", scene.SourceView);
            Assert.AreEqual(2, scene.ProxyCount);
            Assert.AreEqual(12.34, scene.ExtractionTime.TotalSeconds, 0.001);
        }

        [TestMethod]
        public void Parameters_RoundTrip()
        {
            using var folder = new TempFolder();
            SceneData scene = WriteAndRead(TestData.BuildDocument(), folder).Scene;

            Assert.IsFalse(scene.Parameters.IsEmpty);
            Assert.IsTrue(scene.Parameters.TryGet(1, 0, out string name, out string value));
            Assert.AreEqual("Fire Rating", name);
            Assert.AreEqual("-/30/30", value);
        }

        [TestMethod]
        public void Journal_RoundTripsInOrder()
        {
            using var folder = new TempFolder();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(), folder);
            EditJournal journal = read.Journal;

            Assert.AreEqual(3, journal.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, journal.Entries.Select(e => e.Seq).ToArray());

            JournalEntry hide = journal.Entries[0];
            Assert.AreEqual(JournalOps.HIDE, hide.Op);
            Assert.AreEqual(JournalOps.MODE_DEMOLISH, hide.Mode);
            Assert.AreEqual("wall-uid", hide.UniqueId);
            Assert.AreEqual(new DateTime(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc), hide.Utc);
            Assert.IsFalse(hide.AppliedToRevit);

            JournalEntry move = journal.Entries[1];
            Assert.AreEqual(JournalOps.TRANSFORM, move.Op);
            Assert.AreEqual(new Vector3(1, 2, 3), move.Pivot);
            Assert.AreEqual(new Vector3(0.5f, -0.25f, 0), move.Offset);
            Assert.AreEqual(1.5707964f, move.Angle);

            JournalEntry clone = journal.Entries[2];
            Assert.AreEqual(7, clone.NewCloneKey);
            Assert.AreEqual(9001L, clone.RevitElementId);
            Assert.AreEqual(7, journal.MaxCloneKey());
            Assert.AreEqual(1, journal.CountNotInRevit());
        }

        [TestMethod]
        public void Bookmarks_RoundTripWithHomeThumbnailAndSun()
        {
            using var folder = new TempFolder();
            BookmarkDocument bookmarks = WriteAndRead(TestData.BuildDocument(), folder).Bookmarks;

            Assert.AreEqual(1, bookmarks.Bookmarks.Count);
            BookmarkRecord entry = bookmarks.Bookmarks[0];
            Assert.AreEqual("b1", entry.Id);
            Assert.AreEqual("Entry", entry.Name);
            Assert.AreEqual(1.25, entry.X);
            Assert.AreEqual(0.75f, entry.Yaw);
            Assert.IsTrue(entry.Flying);
            Assert.AreEqual("iVBORw0KGgo=", entry.Thumbnail);
            Assert.IsNotNull(entry.Sun);
            Assert.AreEqual(2, entry.Sun.Month);
            Assert.AreEqual(600, entry.Sun.Minutes);
            Assert.IsTrue(entry.Sun.DaylightSaving);

            Assert.IsNotNull(bookmarks.Home);
            Assert.AreEqual("home", bookmarks.Home.Id);
            Assert.AreEqual("AAAA", bookmarks.Home.Thumbnail);
        }

        [TestMethod]
        public void SunVisibilityAndComments_RoundTrip()
        {
            using var folder = new TempFolder();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(), folder);

            Assert.IsNotNull(read.Sun);
            Assert.IsTrue(read.Sun.Enabled);
            Assert.AreEqual(2, read.Sun.Time.Month);
            Assert.AreEqual(28, read.Sun.Time.Day);
            Assert.AreEqual(900, read.Sun.Time.Minutes);
            Assert.AreEqual(1.5f, read.Sun.SunIntensity);
            Assert.AreEqual(1.25f, read.Sun.GlassTransmission);

            Assert.IsNotNull(read.Visibility);
            CollectionAssert.AreEqual(new[] { "furniture" }, read.Visibility.HiddenCategories);
            CollectionAssert.AreEqual(new[] { "link-uid" }, read.Visibility.HiddenLinks);
            Assert.AreEqual("wall-uid", read.Visibility.HiddenElements.Single().UniqueId);

            // The blank comment is dropped on read
            Assert.AreEqual(1, read.Comments.Comments.Count);
            Assert.AreEqual("Check this door swing", read.Comments.Comments[0].Text);
            Assert.AreEqual(202L, read.Comments.Comments[0].ElementId);
        }

        [TestMethod]
        public void OptionalEntries_AreOnlyWrittenWhenThereIsSomething()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            var bare = new BimGoDocument { Scene = document.Scene, CreatedUtc = document.CreatedUtc };
            string path = folder.File("bare.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, bare, TestData.Writer, FileKinds.SAVE, out string error), error);

            var names = TestData.EntryNames(path);
            CollectionAssert.Contains(names, "manifest.json");
            CollectionAssert.Contains(names, "geometry.bin");
            CollectionAssert.Contains(names, "journal.json");
            CollectionAssert.DoesNotContain(names, "bookmarks.json");
            CollectionAssert.DoesNotContain(names, "sun.json");
            CollectionAssert.DoesNotContain(names, "visibility.json");

            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.IsNotNull(read, error);
            Assert.IsTrue(read.Bookmarks.IsEmpty);
            Assert.IsNull(read.Sun);
            Assert.IsNull(read.Visibility);
            Assert.AreEqual(0, read.Journal.Count);
        }

        [TestMethod]
        public void Write_SecondTimeReplacesTheFileAndLeavesNoTemp()
        {
            using var folder = new TempFolder();
            string path = Path.Combine(folder.Path, "sub", "model.bimgo"); // folder is created
            BimGoDocument document = TestData.BuildDocument();

            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            document.Journal.Add(new JournalEntry { Op = JournalOps.HIDE, Mode = JournalOps.MODE_DELETE, ElementId = 202, UniqueId = "door-uid" });
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out error), error);

            Assert.IsFalse(File.Exists(path + ".tmp"));
            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.AreEqual(4, read.Journal.Count);
            Assert.AreEqual(JournalOps.MODE_DELETE, read.Journal.Entries[3].Mode);
        }

        [TestMethod]
        public void Write_WithoutScene_FailsWithReason()
        {
            using var folder = new TempFolder();
            bool written = BimGoWriter.Write(folder.File("empty.bimgo"), new BimGoDocument(), TestData.Writer, FileKinds.SAVE, out string error);
            Assert.IsFalse(written);
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(File.Exists(folder.File("empty.bimgo")));
        }

        [TestMethod]
        public void Write_Cancelled_LeavesTheExistingFileUntouched()
        {
            using var folder = new TempFolder();
            string path = folder.File("model.bimgo");
            BimGoDocument document = TestData.BuildDocument();
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            byte[] before = File.ReadAllBytes(path);

            var progress = new OperationProgress();
            progress.Begin("Saving", 0, 1);
            progress.Cancel();
            document.Journal.Add(new JournalEntry { Op = JournalOps.HIDE, Mode = JournalOps.MODE_DELETE, ElementId = 101 });
            bool written = BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out error, CompressionLevel.Fastest, progress);

            Assert.IsFalse(written);
            Assert.AreEqual("Cancelled.", error);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [TestMethod]
        public void Read_Cancelled_ReturnsNullWithReason()
        {
            using var folder = new TempFolder();
            string path = folder.File("model.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);

            var progress = new OperationProgress();
            progress.Begin("Reading", 0, 1);
            progress.Cancel();
            Assert.IsNull(BimGoReader.Read(path, null, out error, progress));
            Assert.AreEqual("Cancelled.", error);
        }

        [TestMethod]
        public void UncompressedSnapshot_ReadsTheSame()
        {
            using var folder = new TempFolder();
            string path = folder.File("snapshot.bimgo");
            BimGoDocument document = TestData.BuildDocument();
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SNAPSHOT, out string error, CompressionLevel.NoCompression), error);

            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(FileKinds.SNAPSHOT, read.Kind);
            CollectionAssert.AreEqual(document.Scene.Indices, read.Scene.Indices);
        }
    }
}
