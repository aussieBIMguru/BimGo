using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Quality profiles (Basic / Medium / Realistic) in the pause menu. The values live in
    /// <see cref="QualityProfiles"/> (Core), shared with the Revit Options window; this part maps them to and from the
    /// session's own fields. The profile shown is recognised from the current values, so any manual change reads as
    /// Custom, and the settings file saves whichever it is.
    /// </summary>
    internal sealed partial class GameSession
    {
        // Reused for recognising the profile each frame the menu is open (no per-frame allocation)
        private LaunchSettings _profileScratch;

        /// <summary>
        /// Writes the session's profile-governed values (and only those) into a settings object.
        /// </summary>
        private void WriteProfileValues(LaunchSettings settings)
        {
            settings.Colour = _whitecard ? ColourMode.Whitecard : _realistic ? ColourMode.Realistic : ColourMode.Material;
            settings.Msaa = _msaa;
            settings.AmbientOcclusion = _ambientOcclusion;
            settings.ShadowQuality = _shadowQuality;
            settings.ArtificialLights = _lightMode;
            settings.BloomIntensity = _bloomIntensity;
            settings.Reflections = _reflections;
            settings.ReflectionThreshold = _reflectThreshold;
            settings.ReflectionProbes = _reflectProbes;
            settings.ProbeResolution = _probeHigh ? 256 : QualityProfiles.PROBE_RESOLUTION;
        }

        /// <summary>
        /// The profile the current values match, else Custom.
        /// </summary>
        private QualityProfile CurrentProfile()
        {
            _profileScratch ??= new LaunchSettings();
            WriteProfileValues(_profileScratch);
            return QualityProfiles.Detect(_profileScratch);
        }

        /// <summary>
        /// Applies a profile to the session (it takes effect this frame and is saved with the settings).
        /// Shadows on / off is left alone (a per-model choice); only their quality changes.
        /// </summary>
        private void ApplyProfile(QualityProfile profile)
        {
            _profileScratch ??= new LaunchSettings();
            WriteProfileValues(_profileScratch);
            QualityProfiles.Apply(_profileScratch, profile);
            LaunchSettings s = _profileScratch;

            _whitecard = s.Colour == ColourMode.Whitecard;
            _realistic = s.Colour == ColourMode.Realistic;
            _msaa = s.Msaa;
            _ambientOcclusion = s.AmbientOcclusion;
            _shadowQuality = s.ShadowQuality;
            _lightMode = s.ArtificialLights;
            _bloomIntensity = s.BloomIntensity;
            _reflections = s.Reflections;
            _reflectThreshold = s.ReflectionThreshold <= 37 ? 25 : 50;
            bool probesWereOn = _reflectProbes;
            _reflectProbes = s.ReflectionProbes;
            _probeHigh = s.ProbeResolution >= 192;
            if (_reflectProbes && !probesWereOn) { _renderer.RetryProbes(); }

            // (the segmented control already clicked)
            if (_realistic && !_renderer.HasMaterials)
            {
                // Realistic is kept (it is what the next Go should extract for); this snapshot shows material colours
                Toast("Realistic profile: no textures in this snapshot, so material colours show. Tick “Extract materials and textures” at Go.", 5f);
            }
            else
            {
                Toast(profile switch
                {
                    QualityProfile.Basic => "Basic: whitecard, AO and 2x anti-aliasing; lights, bloom and reflections off",
                    QualityProfile.Medium => "Medium: material colours, medium shadows, lights, sky reflections",
                    _ => "Realistic: textures, high shadows, lights, probe reflections, 4x anti-aliasing"
                }, 3f);
            }
        }
    }
}
