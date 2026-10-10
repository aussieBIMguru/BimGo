using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Sun hours round 2 / daylight round: pass / fail settings, saved studies and the folder a file's studies go to.
    /// </summary>
    [TestClass]
    public sealed class SunStudyTests
    {
        [TestMethod]
        public void Passes_UsesTheTargetWithATolerance()
        {
            Assert.IsTrue(SunHours.Passes(2f, 2f));
            Assert.IsTrue(SunHours.Passes(1.99995f, 2f));
            Assert.IsFalse(SunHours.Passes(1.9f, 2f));
        }

        [TestMethod]
        public void Clean_KeepsAndClampsTheTestAndDaylightSettings()
        {
            SunHoursSettings clean = new SunHoursSettings { Target = (SunTarget)2, TargetHours = 40f }.Clean(2026);
            Assert.AreEqual(SunTarget.On, clean.Target, "an earlier build's preset reads as on");
            Assert.IsTrue(clean.PassFail);
            Assert.AreEqual(12f, clean.TargetHours);

            clean = new SunHoursSettings { TargetHours = 2.6f, Mode = (StudyMode)9, Rays = 300, WorkPlane = 5f, FactorTarget = 2.2f, LuxTarget = 333f, LuxShare = 0.04f }.Clean(2026);
            Assert.AreEqual(SunTarget.Off, clean.Target);
            Assert.AreEqual(2.5f, clean.TargetHours, "quarter hours");
            Assert.AreEqual(StudyMode.SunHours, clean.Mode);
            Assert.AreEqual(256, clean.Rays);
            Assert.AreEqual(2f, clean.WorkPlane);
            Assert.AreEqual(2f, clean.FactorTarget);
            Assert.AreEqual(350f, clean.LuxTarget);
            Assert.AreEqual(0.1f, clean.LuxShare, 1e-6f);

            var daylight = new SunHoursSettings { Mode = StudyMode.DaylightFactor, FloorOffset = 0.2f };
            Assert.AreEqual(0.7f, daylight.HorizontalOffset, "daylight modes test the work plane");
            Assert.AreEqual(0.2f, new SunHoursSettings { FloorOffset = 0.2f }.HorizontalOffset);
        }

        private static SunStudyDocument Study(string name) => new()
        {
            Name = name,
            Model = "Test Model",
            Run = new SunHoursSettings { Target = SunTarget.On, Mode = StudyMode.Illuminance },
            Shares = new[] { 0.5f, 1f },
            SunSamples = 72,
            TotalSamples = 72,
            Faces = new List<SunStudyFace> { new() { UniqueId = "wall-uid", ElementId = 101, Nx = 1, Offset = 12.5, BothSides = true, RoomKey = "1|Living|0|30.0" } },
            CellFaces = new[] { 0, 0 },
            Points = new[] { 1f, 2f, 3f, 1f, 2.25f, 3f },
            Hours = new[] { 2.5f, 1f }
        };

        [TestMethod]
        public void Studies_WriteListReadDelete()
        {
            using var folder = new TempFolder();
            Assert.IsTrue(SunStudyFiles.Write(folder.Path, Study("Living: 21 Jun"), out string path, out string error), error);
            Assert.AreEqual("Living_ 21 Jun.json", Path.GetFileName(path), "invalid file name characters are replaced");

            List<SunStudyInfo> list = SunStudyFiles.List(folder.Path);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Living_ 21 Jun", list[0].Name);

            SunStudyDocument read = SunStudyFiles.Read(path, out error);
            Assert.IsNotNull(read, error);
            Assert.AreEqual(SunTarget.On, read.Run.Target);
            Assert.AreEqual(StudyMode.Illuminance, read.Run.Mode);
            CollectionAssert.AreEqual(new[] { 0.5f, 1f }, read.Shares);
            Assert.AreEqual(1, read.Faces.Count);
            Assert.AreEqual(12.5, read.Faces[0].Offset, 1e-9);
            CollectionAssert.AreEqual(new[] { 2.5f, 1f }, read.Hours);
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3f, 1f, 2.25f, 3f }, read.Points);

            Assert.IsTrue(SunStudyFiles.Delete(path));
            Assert.AreEqual(0, SunStudyFiles.List(folder.Path).Count);
        }

        [TestMethod]
        public void Studies_RefuseInconsistentData()
        {
            using var folder = new TempFolder();
            SunStudyDocument broken = Study("Broken");
            broken.Points = new[] { 1f, 2f };
            Assert.IsFalse(SunStudyFiles.Write(folder.Path, broken, out _, out string error));
            Assert.IsNotNull(error);

            string path = Path.Combine(SunStudyFiles.FolderIn(folder.Path), "bad.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{\"hours\":[1,2],\"points\":[1],\"cellFaces\":[0,0],\"faces\":[]}");
            Assert.IsNull(SunStudyFiles.Read(path, out error));
            Assert.IsNotNull(error);
        }

        [TestMethod]
        public void SafeName_TrimsAndFallsBack()
        {
            Assert.AreEqual("Study", SunStudyFiles.SafeName("   "));
            Assert.AreEqual("a_b", SunStudyFiles.SafeName("a/b"));
            Assert.AreEqual(SunStudyFiles.MAX_NAME, SunStudyFiles.SafeName(new string('x', 200)).Length);
        }

        [TestMethod]
        public void KeySourceFor_PrefersCloudThenModelPathThenFile()
        {
            var cloud = new ModelProvenance { IsCloud = true, CloudProjectId = "p-guid", CloudModelId = "m-guid", ModelPath = "ignored" };
            Assert.AreEqual("cloud:p-guid/m-guid", ModelFolders.KeySourceFor(cloud, @"C:\a.bimgo"));
            Assert.AreEqual(@"local:C:\Models\House.rvt", ModelFolders.KeySourceFor(new ModelProvenance { ModelPath = @"C:\Models\House.rvt" }, @"C:\a.bimgo"));
            Assert.AreEqual(@"file:C:\a.bimgo", ModelFolders.KeySourceFor(new ModelProvenance(), @"C:\a.bimgo"));
            Assert.AreEqual(@"file:C:\a.bimgo", ModelFolders.KeySourceFor(null, @"C:\a.bimgo"));
        }
    }
}
