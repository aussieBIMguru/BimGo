using System;
using System.Numerics;
using System.Text;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Photo round: the equirectangular mapping and the Photo Sphere XMP.
    /// </summary>
    [TestClass]
    public sealed class PanoramaTests
    {
        [TestMethod]
        public void Direction_CentreLooksAlongTheHeadingAndRightTurnsRight()
        {
            const int W = 4096, H = 2048;
            Vector3 centre = Panorama.Direction(W / 2, H / 2, W, H, 0);
            Assert.AreEqual(1f, centre.X, 1e-3f, "the middle looks along yaw 0 (+X)");
            Assert.AreEqual(0f, centre.Z, 1e-3f);

            Vector3 right = Panorama.Direction(W * 3 / 4, H / 2, W, H, 0);
            Assert.AreEqual(-1f, right.Y, 1e-3f, "a quarter to the right is −Y (yaw turns counter-clockwise)");

            Vector3 top = Panorama.Direction(10, 0, W, H, 0);
            Assert.IsTrue(top.Z > 0.999f, "the top row looks up");

            Vector3 heading = Panorama.Direction(W / 2, H / 2, W, H, Math.PI / 2);
            Assert.AreEqual(1f, heading.Y, 1e-3f, "the heading turns the middle");
        }

        [TestMethod]
        public void FaceSize_MatchesTheCentreResolution()
        {
            // 90° faces need width / π... · 2·tan 45° = width / π
            Assert.AreEqual((int)Math.Ceiling(4096 / Math.PI), Panorama.FaceSize(4096, 90));
            Assert.IsTrue(Panorama.FaceSize(8192, 96) > Panorama.FaceSize(8192, 90));
        }

        [TestMethod]
        public void PhotoSphereXmp_IsInsertedAfterTheJfifHeader()
        {
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46, 0xFF, 0xDA, 0x01, 0xFF, 0xD9 };
            byte[] tagged = Panorama.AddPhotoSphereXmp(jpeg, 4096, 2048, 90);
            Assert.AreEqual(0xFF, tagged[0]);
            Assert.AreEqual(0xD8, tagged[1]);
            Assert.AreEqual(0xE0, tagged[3], "JFIF stays first");
            Assert.AreEqual(0xFF, tagged[8]);
            Assert.AreEqual(0xE1, tagged[9], "APP1 follows it");
            string text = Encoding.UTF8.GetString(tagged);
            StringAssert.Contains(text, "http://ns.adobe.com/xap/1.0/");
            StringAssert.Contains(text, "<GPano:ProjectionType>equirectangular</GPano:ProjectionType>");
            StringAssert.Contains(text, "<GPano:FullPanoWidthPixels>4096</GPano:FullPanoWidthPixels>");
            Assert.AreEqual(0xD9, tagged[^1], "the rest of the file follows unchanged");

            int length = (tagged[10] << 8) | tagged[11];
            Assert.AreEqual(tagged.Length - jpeg.Length - 2, length, "the segment length counts its own two bytes");

            byte[] notJpeg = { 1, 2, 3, 4 };
            Assert.AreSame(notJpeg, Panorama.AddPhotoSphereXmp(notJpeg, 2, 1));
        }
    }
}
