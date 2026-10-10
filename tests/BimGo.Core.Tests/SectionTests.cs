using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Section box round: the cut's planes, cleaning, persistence and BCF clipping planes.
    /// </summary>
    [TestClass]
    public sealed class SectionTests
    {
        private static SectionCut Box() => new()
        {
            BoxOn = true,
            BoxMin = new Vector3(0, 0, 0),
            BoxMax = new Vector3(10, 8, 3)
        };

        [TestMethod]
        public void LocalPlanes_CutOutsideTheBoxAndBeyondThePlane()
        {
            SectionCut cut = Box();
            cut.PlaneOn = true;
            cut.PlanePoint = new Vector3(5, 0, 0);
            cut.PlaneNormal = new Vector3(1, 0, 0); // cut away x > 5

            var planes = new Vector4[SectionCut.MAX_PLANES];
            var origin = new Vector3(100, 200, 10);
            int count = cut.LocalPlanes(origin, planes);
            Assert.AreEqual(7, count);

            Vector3 Local(float x, float y, float z) => new Vector3(x, y, z) - origin;
            Assert.IsFalse(SectionCut.IsCut(planes, count, Local(2, 2, 1)), "inside the box, before the plane");
            Assert.IsTrue(SectionCut.IsCut(planes, count, Local(7, 2, 1)), "past the free plane");
            Assert.IsTrue(SectionCut.IsCut(planes, count, Local(2, 9, 1)), "outside the box");
            Assert.IsTrue(SectionCut.IsCut(planes, count, Local(2, 2, 3.5f)), "above the box");
            Assert.AreEqual(0, new SectionCut().LocalPlanes(origin, planes), "nothing on: no planes");
        }

        [TestMethod]
        public void Clean_OrdersCornersAndNormalises()
        {
            SectionCut cut = new SectionCut
            {
                BoxOn = true, MinX = 5, MaxX = 1, MinY = 0, MaxY = 0.05f, MinZ = float.NaN, MaxZ = 2,
                PlaneOn = true, NormalX = 0, NormalY = 3, NormalZ = 0
            }.Clean();
            Assert.AreEqual(1f, cut.MinX);
            Assert.AreEqual(5f, cut.MaxX);
            Assert.AreEqual(SectionCut.MIN_SIZE, cut.MaxY - cut.MinY, 1e-6f);
            Assert.AreEqual(0f, cut.MinZ);
            Assert.AreEqual(1f, cut.NormalY, 1e-6f);

            SectionCut zero = new SectionCut { PlaneOn = true, NormalX = 0, NormalY = 0, NormalZ = 0 }.Clean();
            Assert.IsFalse(zero.PlaneOn, "a zero normal switches the plane off");
        }

        [TestMethod]
        public void Planes_RoundTripBoxAndPlane()
        {
            SectionCut cut = Box();
            cut.PlaneOn = true;
            cut.PlanePoint = new Vector3(1, 2, 3);
            cut.PlaneNormal = Vector3.Normalize(new Vector3(1, 1, 0));

            SectionCut back = SectionCut.FromPlanes(cut.ToPlanes(), out int dropped);
            Assert.AreEqual(0, dropped);
            Assert.IsTrue(back.BoxOn && back.PlaneOn);
            Assert.AreEqual(cut.BoxMin, back.BoxMin);
            Assert.AreEqual(cut.BoxMax, back.BoxMax);
            Assert.AreEqual(cut.PlanePoint, back.PlanePoint);
            Assert.AreEqual(cut.PlaneNormal.X, back.PlaneNormal.X, 1e-5f);

            Assert.IsNull(SectionCut.FromPlanes(new List<(Vector3, Vector3)>(), out _));
        }

        [TestMethod]
        public void FromPlanes_IncompleteBoxBecomesAPlane()
        {
            var planes = new List<(Vector3, Vector3)> { (new Vector3(0, 0, 2.5f), Vector3.UnitZ), (Vector3.Zero, -Vector3.UnitZ) };
            SectionCut cut = SectionCut.FromPlanes(planes, out int dropped);
            Assert.IsFalse(cut.BoxOn);
            Assert.IsTrue(cut.PlaneOn);
            Assert.AreEqual(1, dropped, "only one free plane is kept");
        }

        [TestMethod]
        public void Bcf_ClippingPlanesRoundTripThroughSharedCoordinates()
        {
            var site = new SiteInfo { HasSharedTransform = true, SharedEast = 280000, SharedNorth = 6130000, SharedElevation = 50, SharedAngle = 0.4 };
            BcfFrame frame = BcfFrame.Resolve(site, BcfCoordinates.Shared, out _);
            SectionCut cut = Box();

            List<BcfClippingPlane> planes = BcfMapping.ToClippingPlanes(cut, frame);
            Assert.AreEqual(6, planes.Count);
            Assert.IsTrue(planes[0].Location.X > 100000, "written in shared coordinates");

            SectionCut back = BcfMapping.ToSection(planes, frame, out int dropped);
            Assert.AreEqual(0, dropped);
            Assert.IsTrue(back.BoxOn, "rotated back to internal axes, the six planes make a box again");
            Assert.AreEqual(10f, back.MaxX, 1e-3f);
            Assert.AreEqual(3f, back.MaxZ, 1e-3f);
        }

        [TestMethod]
        public void Bcf_FileKeepsClippingPlanes()
        {
            using var folder = new TempFolder();
            string path = folder.File("cut" + BcfFile.EXTENSION);
            var topic = new BcfTopic { Title = "Cut", Viewpoint = new BcfViewpoint { Position = new BcfVector(1, 2, 3), Direction = new BcfVector(0, 1, 0) } };
            topic.Viewpoint.ClippingPlanes.Add(new BcfClippingPlane(new BcfVector(0, 0, 2.5), new BcfVector(0, 0, 1)));
            Assert.IsTrue(BcfFile.Write(path, new BcfProject(), new[] { topic }, out string error), error);

            BcfReadResult result = BcfFile.Read(path, out error);
            Assert.IsNotNull(result, error);
            BcfClippingPlane plane = result.Topics.Single().Viewpoint.ClippingPlanes.Single();
            Assert.AreEqual(2.5, plane.Location.Z, 1e-9);
            Assert.AreEqual(1.0, plane.Direction.Z, 1e-9);
        }

        [TestMethod]
        public void Visibility_SavesTheCutAndDropsAnInactiveOne()
        {
            var settings = new VisibilitySettings { Section = Box() }.Clean();
            Assert.IsFalse(settings.IsEmpty, "a cut is worth saving");
            Assert.IsNotNull(settings.Section);

            settings = new VisibilitySettings { Section = new SectionCut() }.Clean();
            Assert.IsNull(settings.Section, "a cut with nothing on isn't kept");
            Assert.IsTrue(settings.IsEmpty);
        }
    }
}
