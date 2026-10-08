using BimGo.Audio;
using BimGo.Platform;
using BimGo.Rendering;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Reflections (reflection probes round, build B): sky or probes, the probes' bake and re-bake triggers, and the
    /// pause menu's Reflections tab.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>The Reflections tab's source: the sky only, probes (128 px), probes at 256 px.</summary>
        private static readonly string[] REFLECTION_SOURCE_OPTIONS = { "Sky", "Probes", "Probes HQ" };

        /// <summary>Seconds without further change before stale probes re-bake (a sun slider drag is one change).</summary>
        private const float PROBE_SETTLE = 1f;

        // Re-bake trigger: a hash of what the probes captured, and when it last changed (-1 = settled)
        private long _probeKey;
        private float _probeKeyChangedAt = -1f;

        // Bake timing for the log (first full bake, then each full re-bake)
        private float _probeBakeStartedAt = -1f;

        #endregion

        /// <summary>
        /// Places and bakes the reflection probes this frame (a couple of faces), and marks them stale a moment after
        /// the sun, the lights, the colour mode or the model changed. Probes are only kept while the Realistic mode
        /// shows them (or the probe debug colours); otherwise their memory is freed.
        /// </summary>
        private void UpdateReflectionProbes()
        {
            // Kept while probes are chosen (switching colour modes doesn't re-place them); baked only while shown
            bool wanted = (_reflections && _reflectProbes) || _reflectDebug == 2;
            bool shown = wanted && _realistic && !_whitecard;
            var template = new SceneDrawParams
            {
                Whitecard = _whitecard,
                Realistic = _realistic,
                Tint = _tintMode,
                ReflectThreshold = _reflectThreshold / 100f,
                ReflectGain = _reflectStrength,
                Time = _clock,
                FogDensity = 0.0022f,
                Sun = true
            };

            string error = _renderer.UpdateReflectionProbes(wanted, shown, _probeHigh ? 256 : 128, Camera.Position, template, _groupVisible, Dynamics, _groundZ);
            if (error != null)
            {
                _reflectProbes = false;
                Sound.Play(SoundId.Error);
                Toast(error, 6f, important: true);
            }

            ReflectionProbes probes = _renderer.Probes;
            if (!probes.Ready)
            {
                _probeKey = 0;
                _probeKeyChangedAt = _probeBakeStartedAt = -1f;
                return;
            }

            // Stale after a change, once things have settled for a moment
            long key = ProbeSceneKey();
            if (_probeKey == 0) { _probeKey = key; }
            else if (key != _probeKey)
            {
                _probeKey = key;
                _probeKeyChangedAt = _clock;
            }
            if (_probeKeyChangedAt >= 0f && _clock - _probeKeyChangedAt > PROBE_SETTLE)
            {
                _probeKeyChangedAt = -1f;
                _renderer.InvalidateProbes();
            }

            // Log how long a full (re)bake took
            if (probes.PendingCount > 0 && _probeBakeStartedAt < 0f) { _probeBakeStartedAt = _clock; }
            else if (probes.PendingCount == 0 && _probeBakeStartedAt >= 0f)
            {
                Utilities.Log_Utils.Write($"Reflection probes: {probes.Count} baked in {_clock - _probeBakeStartedAt:0.0} s " +
                    $"({ReflectionProbes.FACES_PER_FRAME} faces per frame, {probes.Size} px).");
                _probeBakeStartedAt = -1f;
            }
        }

        /// <summary>
        /// Everything a probe capture depends on: the model (hidden / moved / cloned elements, category toggles), the
        /// sun and sky, the artificial lights, the colour mode and Revit tint.
        /// </summary>
        private long ProbeSceneKey()
        {
            SunLighting l = _renderer.Lighting;
            var hash = new HashCode();
            hash.Add(l.Enabled);
            hash.Add(l.SunDirection);
            hash.Add(l.SunColour);
            hash.Add(l.SkyColour);
            hash.Add(l.Zenith);
            hash.Add(l.Horizon);
            hash.Add(l.ShadowStrength);
            hash.Add(l.Glass);
            hash.Add(_lightMode);
            hash.Add(_lightIntensity);
            hash.Add(_realistic);
            hash.Add(_tintMode);
            long key = ShadowSceneKey() ^ ((long)hash.ToHashCode() << 17);
            return key == 0 ? 1 : key;
        }

        /// <summary>
        /// The pause menu's Reflections tab: which surfaces reflect (off / some / all), the source (sky, probes,
        /// probes HQ), the strength, and the probes' status with REFRESH. Saved with the settings.
        /// </summary>
        private void BuildReflectionsTab(FontAtlas f, InputState input, float x, float y, float w)
        {
            // Which surfaces: off, some (shine 50 %+), all (25 %+)
            _ui.Text(f.Body, x, y, "Reflections", UiTheme.TEXT);
            if (!_realistic) { _ui.TextRight(f.Small, x + w, y + S(3), "REALISTIC MODE ONLY", UiTheme.MEASURE_LABEL, S(0.8f)); }
            int reflectNow = !_reflections ? 0 : _reflectThreshold <= 25 ? 2 : 1;
            int reflect = Segmented(f, input, x, y + S(22), w, REFLECTION_OPTIONS, reflectNow);
            if (reflect != reflectNow)
            {
                _reflections = reflect != 0;
                if (reflect != 0) { _reflectThreshold = reflect == 2 ? 25 : 50; }
                if (_reflections && !_realistic) { Toast("Reflections show in the Realistic colour mode (Display tab).", 4f); }
            }
            _ui.Text(f.Small, x, y + S(60), "SOME: SHINE 50 %+ · ALL: SHINE 25 %+", UiTheme.TEXT_FAINT, S(0.4f));
            y += S(82);

            // Source: the sky, or baked probes (128 px, or 256 px HQ)
            _ui.Text(f.Body, x, y, "Source", _reflections ? UiTheme.TEXT : UiTheme.TEXT_MUTED);
            int sourceNow = !_reflectProbes ? 0 : _probeHigh ? 2 : 1;
            int source = Segmented(f, input, x, y + S(22), w, REFLECTION_SOURCE_OPTIONS, sourceNow);
            if (source != sourceNow)
            {
                if (source == 0) { _reflectProbes = false; }
                else
                {
                    bool wasOn = _reflectProbes;
                    _reflectProbes = true;
                    _probeHigh = source == 2;
                    if (!wasOn) { _renderer.RetryProbes(); }
                }
            }
            y += S(64);

            // Strength
            Text.Clear().Append((long)MathF.Round(_reflectStrength * 100f)).Append(" %");
            float strength = Slider(f, input, SLIDER_REFLECT, x, y, w, "Reflection strength", Text.Span, _reflectStrength, 0.5f, 2f);
            _reflectStrength = MathF.Round(strength * 20f) / 20f;
            y += S(62);

            // Probe status and refresh
            _ui.Rect(x, y, w, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.1f));
            y += S(12);
            ReflectionProbes probes = _renderer.Probes;
            Text.Clear();
            if (!_reflections) { Text.Append("Reflections off"); }
            else if (!_reflectProbes) { Text.Append("Sky only (no probes)"); }
            else if (!_realistic) { Text.Append("Probes: Realistic mode only"); }
            else if (!probes.Ready) { Text.Append(probes.LastError != null ? "Probes unavailable: sky" : "No reflective surfaces: nothing to bake"); }
            else
            {
                Text.Append("Probes ").Append((long)probes.BakedCount).Append(" / ").Append((long)probes.Count);
                if (probes.PendingCount > 0) { Text.Append(" · baking ").Append((long)probes.PendingCount); }
                Text.Append(" · ").Append(probes.GpuBytes / (1024 * 1024)).Append(" MB");
            }
            _ui.TextWrapped(f.Body, x, y + S(4), w - S(104), Text.Span, UiTheme.TEXT_MUTED, maxLines: 2);
            if (probes.Ready && SmallButton(f, input, x + w - S(96), y, S(96), S(26), "REFRESH"))
            {
                _renderer.InvalidateProbes();
                Sound.Play(SoundId.UiClick);
            }
            y += S(44);

            _ui.TextWrapped(f.Body, x, y, w,
                "Probes capture each room once and re-bake a moment after the sun, lights or model change. HQ uses 256 px captures (4× the memory).",
                UiTheme.TEXT_FAINT, maxLines: 4);
        }
    }
}
