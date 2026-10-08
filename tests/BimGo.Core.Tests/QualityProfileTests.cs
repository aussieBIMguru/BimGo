using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Quality profiles: applying sets the documented values, recognising them round-trips, and a manual change reads
    /// as Custom. Never touches the real settings.json.
    /// </summary>
    [TestClass]
    public sealed class QualityProfileTests
    {
        [TestMethod]
        public void Apply_ThenDetect_RoundTripsEveryProfile()
        {
            foreach (QualityProfile profile in QualityProfiles.PICKABLE)
            {
                var settings = new LaunchSettings();
                QualityProfiles.Apply(settings, profile);
                Assert.AreEqual(profile, QualityProfiles.Detect(settings), profile.ToString());
                Assert.AreEqual(profile, settings.QualityProfile, profile.ToString());
            }
        }

        [TestMethod]
        public void Apply_Basic_SetsWhitecardAndTurnsTheExtrasOff()
        {
            var settings = new LaunchSettings { Reflections = true, ReflectionThreshold = 25, ReflectionProbes = true };
            QualityProfiles.Apply(settings, QualityProfile.Basic);

            Assert.AreEqual(ColourMode.Whitecard, settings.Colour);
            Assert.AreEqual(2, settings.Msaa);
            Assert.IsTrue(settings.AmbientOcclusion);
            Assert.AreEqual(ShadowQuality.Low, settings.ShadowQuality);
            Assert.AreEqual(ArtificialLightMode.Off, settings.ArtificialLights);
            Assert.AreEqual(0f, settings.BloomIntensity);
            Assert.IsFalse(settings.Reflections);

            // The reflection choices underneath are kept for when reflections come back on
            Assert.AreEqual(25, settings.ReflectionThreshold);
            Assert.IsTrue(settings.ReflectionProbes);
        }

        [TestMethod]
        public void Apply_Medium_UsesSkyReflectionsOnSomeSurfaces()
        {
            var settings = new LaunchSettings();
            QualityProfiles.Apply(settings, QualityProfile.Medium);

            Assert.AreEqual(ColourMode.Material, settings.Colour);
            Assert.AreEqual(ShadowQuality.Medium, settings.ShadowQuality);
            Assert.AreEqual(ArtificialLightMode.Lights, settings.ArtificialLights);
            Assert.AreEqual(1f, settings.BloomIntensity);
            Assert.IsTrue(settings.Reflections);
            Assert.AreEqual(50, settings.ReflectionThreshold);
            Assert.IsFalse(settings.ReflectionProbes);
        }

        [TestMethod]
        public void Apply_Realistic_UsesProbesAt128()
        {
            var settings = new LaunchSettings { ProbeResolution = 256 };
            QualityProfiles.Apply(settings, QualityProfile.Realistic);

            Assert.AreEqual(ColourMode.Realistic, settings.Colour);
            Assert.AreEqual(4, settings.Msaa);
            Assert.AreEqual(ShadowQuality.High, settings.ShadowQuality);
            Assert.AreEqual(25, settings.ReflectionThreshold);
            Assert.IsTrue(settings.ReflectionProbes);
            Assert.AreEqual(QualityProfiles.PROBE_RESOLUTION, settings.ProbeResolution);
        }

        [TestMethod]
        public void Detect_ManualChange_IsCustom()
        {
            var settings = new LaunchSettings();
            QualityProfiles.Apply(settings, QualityProfile.Realistic);

            settings.ProbeResolution = 256; // Probes HQ is a manual choice, never a profile
            Assert.AreEqual(QualityProfile.Custom, QualityProfiles.Detect(settings));

            settings.ProbeResolution = 128;
            settings.ShadowQuality = ShadowQuality.Medium;
            Assert.AreEqual(QualityProfile.Custom, QualityProfiles.Detect(settings));
        }

        [TestMethod]
        public void Detect_IgnoresSettingsNoProfileGoverns()
        {
            var settings = new LaunchSettings();
            QualityProfiles.Apply(settings, QualityProfile.Medium);
            settings.FieldOfView = 100f;
            settings.ReflectionStrength = 1.5f;
            settings.ArtificialLightIntensity = 0.5f;
            Assert.AreEqual(QualityProfile.Medium, QualityProfiles.Detect(settings));
        }

        [TestMethod]
        public void Sanitise_RecognisesTheProfileFromTheValues()
        {
            var settings = new LaunchSettings();
            QualityProfiles.Apply(settings, QualityProfile.Basic);
            settings.QualityProfile = QualityProfile.Realistic; // a stale or hand-edited value
            settings.Sanitise();
            Assert.AreEqual(QualityProfile.Basic, settings.QualityProfile);

            var defaults = new LaunchSettings();
            defaults.Sanitise();
            Assert.AreEqual(QualityProfile.Custom, defaults.QualityProfile);
        }
    }
}
