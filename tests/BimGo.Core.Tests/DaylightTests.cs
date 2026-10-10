using System;
using System.Linq;
using System.Numerics;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Daylight round: sky patches, the overcast and clear skies, ray directions and the room formulas.
    /// </summary>
    [TestClass]
    public sealed class DaylightTests
    {
        [TestMethod]
        public void Patches_CoverTheSkyAndRoundTrip()
        {
            double total = Enumerable.Range(0, Daylight.PATCHES).Sum(Daylight.PatchSolidAngle);
            Assert.AreEqual(2.0 * Math.PI, total, 1e-9, "the patches cover the hemisphere");

            for (int p = 0; p < Daylight.PATCHES; p++)
            {
                Assert.AreEqual(p, Daylight.PatchOf(Daylight.PatchCentre(p)));
            }
            Assert.AreEqual(-1, Daylight.PatchOf(new Vector3(1, 0, -0.1f)));
            Assert.AreEqual(-1, Daylight.PatchOf(new Vector3(1, 0, 0)));
        }

        [TestMethod]
        public void Overcast_IsScaledToAUnitHorizontalIlluminance()
        {
            var luminance = new float[Daylight.PATCHES];
            Daylight.OvercastPatches(luminance);
            Assert.AreEqual(1.0, Daylight.HorizontalIlluminance(luminance), 1e-6);

            // The zenith is three times as bright as the horizon (2.5× between the top and bottom band centres)
            float top = luminance[(Daylight.ALT_BANDS - 1) * Daylight.AZ_BINS], bottom = luminance[0];
            Assert.IsTrue(top / bottom > 2.4f && top / bottom < 3f, $"zenith / horizon = {top / bottom}");
        }

        [TestMethod]
        public void UnobstructedCell_SeesTheWholeSky()
        {
            // π/N Σ L(d) over cosine-weighted rays reproduces the horizontal illuminance (a DF of 100 %)
            var luminance = new float[Daylight.PATCHES];
            Daylight.OvercastPatches(luminance);
            Vector3[] rays = Daylight.CosineDirections(4096);
            double sum = 0;
            foreach (Vector3 ray in rays) { sum += luminance[Daylight.PatchOf(ray)]; }
            Assert.AreEqual(1.0, Math.PI / rays.Length * sum, 0.03);
        }

        [TestMethod]
        public void CosineDirections_AreUnitUpwardAndCosineWeighted()
        {
            Vector3[] rays = Daylight.CosineDirections(1024);
            Assert.IsTrue(rays.All(r => r.Z > 0f && MathF.Abs(r.Length() - 1f) < 1e-4f));
            Assert.AreEqual(2.0 / 3.0, rays.Average(r => r.Z), 0.01, "mean cosine of a cosine-weighted hemisphere");
            Assert.AreEqual(0.0, rays.Average(r => r.X), 0.02);
        }

        [TestMethod]
        public void ClearSky_IsBrightNearTheSunAndScaledToTheDiffuseHorizontal()
        {
            Vector3 sun = Vector3.Normalize(new Vector3(0, 1, 1)); // 45° up, due +Y
            var luminance = new float[Daylight.PATCHES];
            double diffuse = Daylight.ClearPatches(sun, luminance);
            Assert.AreEqual(Daylight.DiffuseHorizontalClear(sun.Z), diffuse, 1e-6);
            Assert.AreEqual(diffuse, Daylight.HorizontalIlluminance(luminance), diffuse * 1e-5);

            float nearSun = luminance[Daylight.PatchOf(sun)];
            float opposite = luminance[Daylight.PatchOf(Vector3.Normalize(new Vector3(0, -1, 1)))];
            Assert.IsTrue(nearSun > 3f * opposite, $"near the sun {nearSun}, opposite {opposite}");

            Assert.AreEqual(0.0, Daylight.ClearPatches(new Vector3(0, 1, -0.1f), luminance), "sun down");
        }

        [TestMethod]
        public void ClearSky_IlluminanceValuesAreSensible()
        {
            double high = Daylight.DirectNormalClear(Math.Sin(60 * Math.PI / 180));
            Assert.IsTrue(high > 90000 && high < 110000, $"direct normal at 60°: {high}");
            Assert.AreEqual(0.0, Daylight.DirectNormalClear(0.01));
            double diffuse = Daylight.DiffuseHorizontalClear(Math.Sin(30 * Math.PI / 180));
            Assert.IsTrue(diffuse > 10000 && diffuse < 13000, $"diffuse at 30°: {diffuse}");
        }

        [TestMethod]
        public void SplitFlux_MatchesAWorkedExample()
        {
            // 4 m² of glass in a 5 × 4 × 2.7 m room (A = 2·20 + 18·2.7 = 88.6 m²), R 0.5, Rfw 0.4, Rcw 0.6:
            // 0.85·4 / (88.6·0.5) · (39·0.4 + 5·0.6) = 1.4275… %
            float irc = Daylight.InternalReflectedPercent(4, 88.6, 0.5, 0.4, 0.6);
            Assert.AreEqual(1.4275, irc, 0.001);
            Assert.AreEqual(0f, Daylight.InternalReflectedPercent(0, 88.6, 0.5, 0.4, 0.6));

            Assert.AreEqual(10000.0 * 0.4 / (88.6 * 0.5), Daylight.FloorBounceLux(10000, 88.6, 0.5, 0.4), 0.01);
        }

        [TestMethod]
        public void ReflectanceOf_FollowsLuminance()
        {
            Assert.AreEqual(0.9f, Daylight.ReflectanceOf(255, 255, 255), "clamped");
            Assert.AreEqual(0.05f, Daylight.ReflectanceOf(0, 0, 0), "clamped");
            float grey = Daylight.ReflectanceOf(188, 188, 188); // sRGB 188 ≈ 50 % linear
            Assert.AreEqual(0.5f, grey, 0.02f);
        }

        [TestMethod]
        public void Legends_RunFromBlueToRed()
        {
            Assert.AreEqual(SunHours.LegendColour(0f), Daylight.FactorColour(0f));
            Assert.AreEqual(SunHours.LegendColour(SunHours.LEGEND_MAX), Daylight.FactorColour(Daylight.DF_LEGEND_MAX));
            Assert.AreEqual(SunHours.LegendColour(SunHours.LEGEND_MAX), Daylight.LuxColour(5000f));
        }
    }
}
