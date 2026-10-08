// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// A quick choice of display quality (<see cref="QualityProfiles"/>). Any manual change to a setting a profile
    /// governs makes the choice <see cref="Custom"/>.
    /// </summary>
    public enum QualityProfile
    {
        /// <summary>The settings don't match a profile (manual tweaks, or settings saved before profiles).</summary>
        Custom = 0,

        /// <summary>Whitecard, 2x anti-aliasing and ambient occlusion; everything else off or low.</summary>
        Basic = 1,

        /// <summary>Material colours, medium shadows, lights and bloom, sky reflections on the shiniest surfaces.</summary>
        Medium = 2,

        /// <summary>Textures, 4x anti-aliasing, high shadows, lights and bloom, probe reflections on all reflective tiers.</summary>
        Realistic = 3
    }

    /// <summary>
    /// The values each <see cref="QualityProfile"/> sets, applied to and recognised from a <see cref="LaunchSettings"/>.
    /// Shared by the app's pause menu and the Revit Options window so both mean the same thing.
    /// <para>Governed settings: colour mode, anti-aliasing, ambient occlusion, shadow <i>quality</i> (whether shadows
    /// are on is a per-model choice, O, and is left alone), artificial lights, bloom, reflections (on, tier threshold,
    /// sky or probes, probe resolution). Probes HQ is never part of a profile.</para>
    /// <para>Allocation-free: the app recognises the current profile every frame the pause menu is open.</para>
    /// </summary>
    public static class QualityProfiles
    {
        /// <summary>Labels for a segmented control, indexed by <see cref="QualityProfile"/>.</summary>
        public static readonly string[] LABELS = { "Custom", "Basic", "Medium", "Realistic" };

        /// <summary>The profiles a user can pick (Custom is only ever shown).</summary>
        public static readonly QualityProfile[] PICKABLE = { QualityProfile.Basic, QualityProfile.Medium, QualityProfile.Realistic };

        /// <summary>Probe resolution the Realistic profile uses (HQ, 256, stays a manual choice).</summary>
        public const int PROBE_RESOLUTION = 128;

        /// <summary>
        /// Sets the governed values of a profile (Custom changes nothing).
        /// </summary>
        /// <param name="settings">The settings to change.</param>
        /// <param name="profile">The profile.</param>
        public static void Apply(LaunchSettings settings, QualityProfile profile)
        {
            if (settings == null) { return; }
            switch (profile)
            {
                case QualityProfile.Basic:
                    settings.Colour = ColourMode.Whitecard;
                    settings.Msaa = 2;
                    settings.AmbientOcclusion = true;
                    settings.ShadowQuality = ShadowQuality.Low;
                    settings.ArtificialLights = ArtificialLightMode.Off;
                    settings.BloomIntensity = 0f;
                    settings.Reflections = false; // threshold and probe choices are kept for when they come back on
                    break;

                case QualityProfile.Medium:
                    settings.Colour = ColourMode.Material;
                    settings.Msaa = 2;
                    settings.AmbientOcclusion = true;
                    settings.ShadowQuality = ShadowQuality.Medium;
                    settings.ArtificialLights = ArtificialLightMode.Lights;
                    settings.BloomIntensity = 1f;
                    settings.Reflections = true;
                    settings.ReflectionThreshold = 50;
                    settings.ReflectionProbes = false;
                    break;

                case QualityProfile.Realistic:
                    settings.Colour = ColourMode.Realistic;
                    settings.Msaa = 4;
                    settings.AmbientOcclusion = true;
                    settings.ShadowQuality = ShadowQuality.High;
                    settings.ArtificialLights = ArtificialLightMode.Lights;
                    settings.BloomIntensity = 1f;
                    settings.Reflections = true;
                    settings.ReflectionThreshold = 25;
                    settings.ReflectionProbes = true;
                    settings.ProbeResolution = PROBE_RESOLUTION;
                    break;
            }
            settings.QualityProfile = profile == QualityProfile.Custom ? Detect(settings) : profile;
        }

        /// <summary>
        /// True if every value the profile governs is as the profile sets it.
        /// </summary>
        public static bool Matches(LaunchSettings settings, QualityProfile profile)
        {
            if (settings == null) { return false; }
            bool bloomOn = settings.BloomIntensity > 0.999f && settings.BloomIntensity < 1.001f;
            bool bloomOff = settings.BloomIntensity < 0.001f;
            bool sharedDisplay = settings.AmbientOcclusion;
            return profile switch
            {
                QualityProfile.Basic => sharedDisplay
                    && settings.Colour == ColourMode.Whitecard
                    && settings.Msaa == 2
                    && settings.ShadowQuality == ShadowQuality.Low
                    && settings.ArtificialLights == ArtificialLightMode.Off
                    && bloomOff
                    && !settings.Reflections,

                QualityProfile.Medium => sharedDisplay
                    && settings.Colour == ColourMode.Material
                    && settings.Msaa == 2
                    && settings.ShadowQuality == ShadowQuality.Medium
                    && settings.ArtificialLights == ArtificialLightMode.Lights
                    && bloomOn
                    && settings.Reflections
                    && settings.ReflectionThreshold > 37
                    && !settings.ReflectionProbes,

                QualityProfile.Realistic => sharedDisplay
                    && settings.Colour == ColourMode.Realistic
                    && settings.Msaa == 4
                    && settings.ShadowQuality == ShadowQuality.High
                    && settings.ArtificialLights == ArtificialLightMode.Lights
                    && bloomOn
                    && settings.Reflections
                    && settings.ReflectionThreshold <= 37
                    && settings.ReflectionProbes
                    && settings.ProbeResolution < 192,

                _ => false
            };
        }

        /// <summary>
        /// The profile the settings match, else <see cref="QualityProfile.Custom"/>.
        /// </summary>
        public static QualityProfile Detect(LaunchSettings settings)
        {
            foreach (QualityProfile profile in PICKABLE)
            {
                if (Matches(settings, profile)) { return profile; }
            }
            return QualityProfile.Custom;
        }
    }
}
