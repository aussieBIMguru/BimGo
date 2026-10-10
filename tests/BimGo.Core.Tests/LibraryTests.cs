using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using BimGo.Edits;
using BimGo.Format;
using BimGo.Live;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// The family library (next round): library.json, previews and template elements round-trip; damaged or
    /// inconsistent parts are dropped without failing the load; the journal's <c>place</c> entries.
    /// </summary>
    [TestClass]
    public sealed class LibraryTests
    {
        private static readonly byte[] PNG = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        /// <summary>A placeable chair (template = element 2) and a wall-hosted basin listed without geometry.</summary>
        private static LibraryData Sample() => new()
        {
            Entries = new[]
            {
                new LibraryEntry
                {
                    TypeId = 3001, TypeUniqueId = "chair-type", Family = "Chair", Type = "Chair", Category = "furniture",
                    Placement = LibraryPlacement.LEVEL_BASED, Placeable = true, Element = 2, Preview = "library/0.png", Placed = 4
                },
                new LibraryEntry
                {
                    TypeId = 3002, TypeUniqueId = "basin-type", Family = "Basin", Type = "Wall Hung", Category = "plumbing",
                    Placement = LibraryPlacement.HOSTED, Placeable = false, Reason = "Needs a host", Preview = "library/1.png"
                }
            },
            Previews = new Dictionary<string, byte[]> { ["library/0.png"] = PNG },
            VertexStart = 9
        };

        private static BimGoDocument WriteAndRead(BimGoDocument document, TempFolder folder, string name = "lib.bimgo")
        {
            string path = folder.File(name);
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            return read;
        }

        private static BimGoDocument WithLibrary(LibraryData library) => TestData.BuildDocument(library: library);

        [TestMethod]
        public void Library_RoundTrip()
        {
            using var folder = new TempFolder();
            SceneData scene = WriteAndRead(WithLibrary(Sample()), folder).Scene;
            LibraryData got = scene.Library;

            Assert.AreEqual(2, got.Entries.Length);
            Assert.AreEqual(1, got.PlaceableCount);
            Assert.AreEqual(9, got.VertexStart);
            Assert.AreEqual(9, scene.ModelVertexCount);
            Assert.AreEqual(12, scene.Vertices.Length);

            LibraryEntry chair = got.Find("chair-type");
            Assert.IsNotNull(chair);
            Assert.AreEqual(3001L, chair.TypeId);
            Assert.AreEqual("Chair : Chair", chair.Label);
            Assert.AreEqual(LibraryPlacement.LEVEL_BASED, chair.Placement);
            Assert.IsTrue(chair.Placeable);
            Assert.AreEqual(2, chair.Element);
            Assert.AreEqual(4, chair.Placed);
            Assert.AreEqual(CategoryCatalog.Find("furniture").Index, chair.CategoryIndex);
            CollectionAssert.AreEqual(PNG, got.Previews["library/0.png"].ToArray());

            LibraryEntry basin = got.Find("basin-type");
            Assert.IsFalse(basin.Placeable);
            Assert.AreEqual(-1, basin.Element);
            Assert.AreEqual("Needs a host", basin.Reason);
            Assert.IsNull(basin.Preview, "a preview whose image isn't in the file is dropped");

            Assert.IsTrue(scene.Elements[2].IsLibraryTemplate);
            Assert.IsFalse(scene.Elements[0].IsLibraryTemplate);
            Assert.AreEqual(0, scene.CategoryElementCounts[CategoryCatalog.Find("furniture").Index], "templates aren't counted as model elements");
            Assert.IsFalse(scene.CategoryLoaded[CategoryCatalog.Find("furniture").Index]);
        }

        [TestMethod]
        public void Library_AbsentIsEmptyAndNotWritten()
        {
            using var folder = new TempFolder();
            string path = folder.File("none.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                Assert.IsNull(zip.GetEntry("library.json"));
            }
            SceneData scene = BimGoReader.Read(path, null, out error).Scene;
            Assert.IsTrue(scene.Library.IsEmpty);
            Assert.AreEqual(scene.Vertices.Length, scene.ModelVertexCount);
            Assert.IsNull(scene.Library.Find("chair-type"));
        }

        [TestMethod]
        public void Library_EntryOnAModelElementIsListedOnly()
        {
            using var folder = new TempFolder();
            LibraryData library = Sample();
            library.Entries[0].Element = 0; // the wall: not a template
            LibraryEntry chair = WriteAndRead(WithLibrary(library), folder).Scene.Library.Find("chair-type");
            Assert.AreEqual(-1, chair.Element);
            Assert.IsFalse(chair.Placeable);
            Assert.IsNotNull(chair.Reason);
        }

        [TestMethod]
        public void Library_UnknownCategoryIsDropped()
        {
            using var folder = new TempFolder();
            LibraryData library = Sample();
            library.Entries[1].Category = "spaceships";
            LibraryData got = WriteAndRead(WithLibrary(library), folder).Scene.Library;
            Assert.AreEqual(1, got.Entries.Length);
            Assert.AreEqual("chair-type", got.Entries[0].TypeUniqueId);
        }

        [TestMethod]
        public void Library_DamagedJsonIsIgnoredButTheFileLoads()
        {
            using var folder = new TempFolder();
            string path = folder.File("damaged.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, WithLibrary(Sample()), TestData.Writer, FileKinds.SAVE, out string error), error);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("library.json").Delete();
                using var writer = new StreamWriter(zip.CreateEntry("library.json").Open());
                writer.Write("{ \"entries\": [ { \"typeUniqueId\": ");
            }

            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.IsNotNull(read, error);
            Assert.IsTrue(read.Scene.Library.IsEmpty);
            Assert.AreEqual(3, read.Scene.Elements.Length, "the template element still loads (hidden by the app)");
        }

        [TestMethod]
        public void Journal_PlaceEntryRoundTripsAndCreatesAKey()
        {
            using var folder = new TempFolder();
            BimGoDocument document = WithLibrary(Sample());
            document.Journal.Add(new JournalEntry
            {
                Op = JournalOps.PLACE, NewCloneKey = 12, TypeUniqueId = "chair-type", TypeId = 3001,
                Pivot = new Vector3(15, 25, 30), Angle = 0.5f, Label = "Place Chair : Chair"
            });

            JournalEntry place = WriteAndRead(document, folder).Journal.Entries.Last();
            Assert.AreEqual(JournalOps.PLACE, place.Op);
            Assert.AreEqual("chair-type", place.TypeUniqueId);
            Assert.AreEqual(3001L, place.TypeId);
            Assert.AreEqual(12, place.NewCloneKey);
            Assert.AreEqual(new Vector3(15, 25, 30), place.Pivot);
            Assert.AreEqual(0.5f, place.Angle);

            Assert.IsTrue(JournalOps.Creates(JournalOps.PLACE));
            Assert.IsTrue(JournalOps.Creates(JournalOps.CLONE));
            Assert.IsFalse(JournalOps.Creates(JournalOps.TRANSFORM));
            Assert.IsTrue(document.Journal.MaxCloneKey() >= 12);
        }

        [TestMethod]
        public void Journal_OtherEntriesDontWriteTypeFields()
        {
            using var folder = new TempFolder();
            string path = folder.File("journal.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(), TestData.Writer, FileKinds.SAVE, out string error), error);
            using ZipArchive zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("journal.json").Open());
            string json = reader.ReadToEnd();
            Assert.IsFalse(json.Contains("typeUniqueId", StringComparison.Ordinal));
            Assert.IsFalse(json.Contains("typeId", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BuildPushRequest_PlacementsInRevitAreKnownClones()
        {
            var journal = new EditJournal();
            journal.Add(new JournalEntry { Op = JournalOps.PLACE, NewCloneKey = 3, TypeUniqueId = "chair-type" });
            journal.Add(new JournalEntry { Op = JournalOps.TRANSFORM, TargetCloneKey = 3, Offset = Vector3.UnitX });
            journal.Add(new JournalEntry { Op = JournalOps.PLACE, NewCloneKey = 4, TypeUniqueId = "chair-type" });
            journal.MarkApplied(1, 7001);

            JournalApplyPayload request = JournalPush.BuildRequest(journal, dryRun: false, applyConflicts: false, "key", null, null, "f.bimgo");
            Assert.AreEqual(2, request.Entries.Count);
            Assert.AreEqual(1, request.KnownClones.Count);
            Assert.AreEqual(3, request.KnownClones[0].CloneKey);
            Assert.AreEqual(7001L, request.KnownClones[0].ElementId);
        }

        [TestMethod]
        public void Settings_FamilyLibraryDefaultsAndClamp()
        {
            var settings = new LaunchSettings();
            Assert.IsFalse(settings.FamilyLibrary);
            Assert.AreEqual(200, settings.FamilyLibraryMax);

            settings.FamilyLibraryMax = 5;
            settings.Sanitise();
            Assert.AreEqual(10, settings.FamilyLibraryMax);
            settings.FamilyLibraryMax = 999_999;
            settings.Sanitise();
            Assert.AreEqual(LaunchSettings.MAX_FAMILY_LIBRARY, settings.FamilyLibraryMax);
            settings.FamilyLibraryMax = 0;
            settings.Sanitise();
            Assert.AreEqual(200, settings.FamilyLibraryMax);
        }
    
        [TestMethod]
        public void Visibility_GroundOffsetRoundTripsAndCleans()
        {
            using var folder = new TempFolder();
            string path = folder.File("ground.json");
            Assert.IsTrue(VisibilityFiles.Write(path, new VisibilitySettings { GroundOffset = 1.25f }, out string error), error);
            VisibilitySettings read = VisibilityFiles.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(1.25f, read.GroundOffset);
            Assert.IsFalse(read.IsEmpty, "a moved ground alone is worth saving");

            Assert.IsNull(new VisibilitySettings { GroundOffset = float.NaN }.Clean().GroundOffset);
            Assert.IsNull(new VisibilitySettings { GroundOffset = 0f }.Clean().GroundOffset);
            Assert.AreEqual(VisibilitySettings.MAX_GROUND_OFFSET, new VisibilitySettings { GroundOffset = 500f }.Clean().GroundOffset);
            Assert.IsTrue(new VisibilitySettings().IsEmpty);
        }

        [TestMethod]
        public void Library_WorkPlaneEntryRoundTrips()
        {
            using var folder = new TempFolder();
            LibraryData library = Sample();
            library.Entries[0].Placement = LibraryPlacement.WORK_PLANE;
            LibraryEntry chair = WriteAndRead(WithLibrary(library), folder).Scene.Library.Find("chair-type");
            Assert.AreEqual("workPlane", chair.Placement);
            Assert.IsTrue(chair.Placeable);
            Assert.AreEqual(2, chair.Element);
        }
    }
}
