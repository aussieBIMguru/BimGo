using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Vk = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Sun, shadows and time of day.
    /// <list type="bullet">
    /// <item><b>O</b> toggles shadows (off by default; off frees the maps and restores the classic light).</item>
    /// <item><b>Shift+O</b>, or clicking the sun icon (bottom right) while the cursor is free, opens the sun panel:
    /// the cursor is released and the player stands still while the scene keeps rendering live. Esc or O closes it.</item>
    /// <item>Panel: shadows on/off and quality, time slider (5 min steps, Shift = 1 min, play), month / day boxes
    /// (clamped), daylight saving, and sun / sky / shadow / glass intensities. <b>[ ]</b> step the time.</item>
    /// </list>
    /// The state is saved with the model (sun.json in a .bimgo, or a sidecar beside the Revit model, written a moment
    /// after the last change); quality is a per-machine setting.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Constants

        private const int TIME_STEP = 5;          // minutes per slider / [ ] step
        private const float PLAY_SPEED = 60f;      // clock minutes per real second while playing
        private const float SIDECAR_DELAY = 1.5f;  // seconds after the last change before the live sidecar is written

        // Slider ids (the pause menu uses 0–2)
        private const int SLIDER_TIME = 10, SLIDER_SUN = 11, SLIDER_SKY = 12, SLIDER_SHADOW = 13, SLIDER_GLASS = 14;

        private static readonly string[] QUALITY_OPTIONS = { "Low", "Medium", "High" };
        private static readonly string[] COMPASS = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        private static readonly string[] MONTHS = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        private static readonly string[] MONTHS_UPPER = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

        #endregion

        #region Fields

        private SunSettings _sun;
        private GeoLocation _location;
        private bool _locationKnown;
        private double _northAngle;
        private Vector3 _sunDirection = Vector3.UnitZ;
        private double _sunAltitude, _sunAzimuth;   // degrees
        private ShadowQuality _shadowQuality = ShadowQuality.Medium;

        private bool _sunPanelOpen;
        private bool _sunPlaying;
        private float _playMinutes;

        private int _sunRevision, _savedSunRevision, _sidecarSunRevision;
        private float _sunSidecarTimer;
        private string _sunSidecarPath;

        // Month / day text boxes: 0 = none focused, 1 = month, 2 = day
        private int _sunField;
        private readonly char[] _sunFieldChars = new char[2];
        private int _sunFieldLength;

        // Shadow invalidation: bumped when static elements hide / show or categories toggle
        private long _sceneRevision;

        // Cached label
        private string _sunPlaceLabel;

        #endregion

        #region State

        /// <summary>True when shadows (sun lighting) are on.</summary>
        public bool ShadowsOn => _sun?.Enabled == true;

        /// <summary>True if the sun state changed since the file was saved (file mode).</summary>
        private bool SunDirty => _sunRevision != _savedSunRevision;

        #endregion

        #region Setup

        /// <summary>
        /// Loads the sun state (file, else live sidecar, else defaults from the model's site) and the location.
        /// </summary>
        private void InitialiseSun()
        {
            _shadowQuality = Scene.Settings?.ShadowQuality ?? ShadowQuality.Medium;
            _location = SolarPosition.LocationOf(Scene.Site, out _locationKnown);
            _northAngle = SolarPosition.NorthAngle(Scene.Site);

            if (IsFileMode)
            {
                _sun = _options.Document?.Sun?.Copy().Clean();
            }
            else
            {
                _sunSidecarPath = SunFiles.SidecarFor(Scene.CommentsPath);
                _sun = SunFiles.Read(_sunSidecarPath, out string error);
                if (error != null) { Utilities.Log_Utils.Write(error); }
            }
            _sun ??= SunSettings.DefaultsFor(Scene.Site).Clean();

            _sunPlaceLabel = _locationKnown
                ? $"{_location.Name} · UTC{FormatZone(_location.TimeZone)}"
                : "No site location in this file: Sydney is assumed (export again from Revit to use the model's location)";
            RecomputeSun();
        }

        private static string FormatZone(double hours)
        {
            string sign = hours < 0 ? "−" : "+";
            double abs = Math.Abs(hours);
            int h = (int)abs, m = (int)Math.Round((abs - h) * 60.0);
            return m == 0 ? $"{sign}{h}" : $"{sign}{h}:{m:00}";
        }

        #endregion

        #region Sun state

        /// <summary>
        /// Recomputes the sun direction from the date, time and location.
        /// </summary>
        private void RecomputeSun()
        {
            SunTime time = _sun.Time;
            double minutes = _sunPlaying ? _playMinutes : time.Minutes;
            SolarPosition.Compute(_location, DateTime.Today.Year, time.Month, time.Day, minutes, time.DaylightSaving,
                out double altitude, out double azimuth);
            _sunAltitude = altitude * 180.0 / Math.PI;
            _sunAzimuth = azimuth * 180.0 / Math.PI;
            _sunDirection = SolarPosition.ToModel(SolarPosition.Direction(altitude, azimuth), _northAngle);
        }

        /// <summary>
        /// Records a change to the sun state (dirty file / delayed sidecar write) and recomputes the direction.
        /// </summary>
        private void SunChanged()
        {
            _sun.Clean();
            _sunRevision++;
            _sunSidecarTimer = SIDECAR_DELAY;
            RecomputeSun();
            UpdateTitle();
        }

        /// <summary>
        /// O: shadows on / off.
        /// </summary>
        private void ToggleShadows()
        {
            _sun.Enabled = !_sun.Enabled;
            if (!_sun.Enabled) { _sunPlaying = false; }
            SunChanged();
            Sound.Play(SoundId.UiClick);
            Toast(_sun.Enabled ? $"Shadows on · {DescribeSunTime()} (Shift+O opens the sun panel)" : "Shadows off");
        }

        /// <summary>
        /// Moves the clock by a number of minutes (wrapping within the day).
        /// </summary>
        private void StepSunTime(int minutes)
        {
            if (_sunPlaying)
            {
                _sunPlaying = false;
                _sun.Time.Minutes = Math.Clamp((int)MathF.Round(_playMinutes), 0, 1439);
            }
            int value = ((_sun.Time.Minutes + minutes) % 1440 + 1440) % 1440;
            _sun.Time.Minutes = value;
            SunChanged();

            // Say which time is now shown (short, so holding the key reads as a ticking clock)
            if (!_sunPanelOpen) { Toast($"{DescribeSunTime()} · sun {SunHeightText()}", 1.4f); }
        }

        /// <summary>
        /// "32° high, NW" or "below the horizon" (allocates: toasts only).
        /// </summary>
        private string SunHeightText()
        {
            if (_sunAltitude <= 0.0) { return "below the horizon"; }
            int sector = (int)Math.Round(((_sunAzimuth % 360.0) + 360.0) % 360.0 / 45.0) % 8;
            return $"{_sunAltitude:0}° high in the {COMPASS[sector]}";
        }

        /// <summary>
        /// Sets the date and time (bookmarks), turning shadows on.
        /// </summary>
        private void ApplySunTime(SunTime time)
        {
            if (time == null) { return; }
            _sunPlaying = false;
            _sun.Time = time.Copy();
            _sun.Enabled = true;
            SunChanged();
        }

        /// <summary>
        /// "21 Jun 14:35" (allocates: toasts only).
        /// </summary>
        private string DescribeSunTime()
        {
            SunTime t = _sun.Time;
            return $"{t.Day} {MONTHS[Math.Clamp(t.Month, 1, 12) - 1]} {t.Minutes / 60:00}:{t.Minutes % 60:00}{(t.DaylightSaving ? " DST" : string.Empty)}";
        }

        /// <summary>
        /// Per frame: animation, the delayed sidecar write, and this frame's lighting for the renderer.
        /// </summary>
        private void UpdateSun(float dt)
        {
            if (_sunPlaying && _sun.Enabled)
            {
                _playMinutes += dt * PLAY_SPEED;
                if (_playMinutes >= 1440f) { _playMinutes -= 1440f; }
                RecomputeSun();
            }

            // Live sessions: write the sidecar once changes settle
            if (_sunSidecarPath != null && _sidecarSunRevision != _sunRevision && !_sunPlaying)
            {
                _sunSidecarTimer -= dt;
                if (_sunSidecarTimer <= 0f) { FlushSunSidecar(); }
            }
        }

        /// <summary>
        /// Writes the live sidecar if it is behind (also on shutdown).
        /// </summary>
        private void FlushSunSidecar()
        {
            if (_sunSidecarPath == null || _sidecarSunRevision == _sunRevision) { return; }
            _sidecarSunRevision = _sunRevision;
            if (!SunFiles.Write(_sunSidecarPath, _sun, out string error)) { Toast(error, 4f, important: true); }
        }

        /// <summary>
        /// The renderer's lighting for this frame.
        /// </summary>
        private SunLighting CurrentLighting()
        {
            if (!_sun.Enabled) { return default; }
            return SunLighting.Create(_sunDirection, _sun.SunIntensity, _sun.SkyIntensity, _sun.ShadowIntensity, _sun.GlassTransmission);
        }

        /// <summary>
        /// Changes whenever shadow casters change: hidden elements, category toggles, whitecard, moved / cloned elements.
        /// Allocation-free (a struct hash over the active dynamic instances).
        /// </summary>
        private long ShadowSceneKey()
        {
            var hash = new HashCode();
            hash.Add(_whitecard);
            foreach (Physics.DynamicInstance instance in Dynamics.Instances)
            {
                if (!Dynamics.IsActive(instance)) { continue; }
                hash.Add(instance.Id);
                hash.Add(instance.Offset);
                hash.Add(instance.Angle);
            }
            return (_sceneRevision << 32) ^ (uint)hash.ToHashCode();
        }

        /// <summary>
        /// Shows a shadow failure and switches shadows off (called by the render loop).
        /// </summary>
        private void OnShadowFailure(string reason)
        {
            _sun.Enabled = false;
            _sunPlaying = false;
            SunChanged();
            Sound.Play(SoundId.Error);
            Toast(reason, 6f, important: true);
        }

        #endregion

        #region Panel: open, close, keys

        /// <summary>True while the sun panel is open (cursor free, player still).</summary>
        private bool IsSunPanelOpen => _sunPanelOpen;

        private void OpenSunPanel()
        {
            if (_sunPanelOpen) { return; }
            ShowUi(); // the panel is UI: Shift+O while hidden brings everything back
            _sunPanelOpen = true;
            _sunField = 0;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
        }

        private void CloseSunPanel()
        {
            if (!_sunPanelOpen) { return; }
            CommitSunField();
            _sunPanelOpen = false;
            _activeSlider = -1;
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>
        /// Keys while the panel is open: Esc / O close (Esc first leaves a text box), [ ] step the time, typing goes
        /// to a focused month / day box.
        /// </summary>
        private void UpdateSunPanelKeys(InputState input)
        {
            if (_sunField != 0)
            {
                foreach (char c in input.Chars)
                {
                    if (c >= '0' && c <= '9')
                    {
                        if (_sunFieldLength >= _sunFieldChars.Length) { _sunFieldLength = 0; }
                        _sunFieldChars[_sunFieldLength++] = c;
                    }
                    else if (c == '\b' && _sunFieldLength > 0) { _sunFieldLength--; }
                }
                if (input.IsPressed(Vk.VK_RETURN) || input.IsPressed(Vk.VK_TAB))
                {
                    int next = input.IsPressed(Vk.VK_TAB) && _sunField == 1 ? 2 : 0;
                    CommitSunField();
                    if (next != 0) { FocusSunField(next); }
                    return;
                }
                if (input.IsPressed(Vk.VK_ESCAPE))
                {
                    _sunField = 0;
                    return;
                }
                if (input.IsPressedOrRepeated(Vk.VK_UP) || input.IsPressedOrRepeated(Vk.VK_DOWN))
                {
                    int delta = input.IsPressedOrRepeated(Vk.VK_UP) ? 1 : -1;
                    int field = _sunField;
                    CommitSunField();
                    if (field == 1) { _sun.Time.Month = (_sun.Time.Month - 1 + delta + 12) % 12 + 1; }
                    else { _sun.Time.Day += delta; }
                    SunChanged();
                    FocusSunField(field);
                }
                return;
            }

            if (input.IsPressed(Vk.VK_ESCAPE) || input.IsPressed('O'))
            {
                CloseSunPanel();
                return;
            }
            if (input.IsPressedOrRepeated(Vk.VK_OEM_4)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? -1 : -TIME_STEP); }
            if (input.IsPressedOrRepeated(Vk.VK_OEM_6)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? 1 : TIME_STEP); }
            if (input.IsPressed(Vk.VK_SPACE)) { TogglePlay(); }
        }

        private void TogglePlay()
        {
            if (!_sun.Enabled) { return; }
            if (_sunPlaying)
            {
                // Stop on the nearest minute and keep it
                _sunPlaying = false;
                _sun.Time.Minutes = Math.Clamp((int)MathF.Round(_playMinutes), 0, 1439);
                SunChanged();
            }
            else
            {
                _sunPlaying = true;
                _playMinutes = _sun.Time.Minutes;
            }
            Sound.Play(SoundId.UiClick);
        }

        private void FocusSunField(int field)
        {
            _sunField = field;
            int value = field == 1 ? _sun.Time.Month : _sun.Time.Day;
            _sunFieldLength = 0;
            if (value >= 10) { _sunFieldChars[_sunFieldLength++] = (char)('0' + value / 10); }
            _sunFieldChars[_sunFieldLength++] = (char)('0' + value % 10);
        }

        /// <summary>
        /// Applies a focused month / day box (clamped: month 1–12, day to the month's length; empty keeps the value).
        /// </summary>
        private void CommitSunField()
        {
            if (_sunField == 0) { return; }
            int field = _sunField;
            _sunField = 0;
            if (_sunFieldLength == 0) { return; }

            int value = 0;
            for (int i = 0; i < _sunFieldLength; i++) { value = value * 10 + (_sunFieldChars[i] - '0'); }
            if (field == 1) { _sun.Time.Month = Math.Clamp(value, 1, 12); }
            else { _sun.Time.Day = Math.Max(value, 1); }
            SunChanged(); // Clean() clamps the day to the month
        }

        #endregion

        #region HUD: icon and panel

        /// <summary>
        /// The sun icon's square (bottom right, the size of a gun slot).
        /// </summary>
        private void SunIconRect(out float x, out float y, out float size)
        {
            size = S(52);
            x = _window.Width - S(20) - size;
            y = _window.Height - S(20) - size;
        }

        /// <summary>True if the mouse is over the sun icon.</summary>
        private bool HoverSunIcon(InputState input)
        {
            SunIconRect(out float x, out float y, out float size);
            return Hover(input, x, y, size, size);
        }

        /// <summary>
        /// Draws the sun icon: grey when shadows are off, amber when on; a click (cursor free) opens / closes the panel.
        /// </summary>
        private void BuildSunIcon(FontAtlas f, InputState input)
        {
            SunIconRect(out float x, out float y, out float size);
            bool on = ShadowsOn;
            bool hover = !_window.IsCaptured && Hover(input, x, y, size, size);
            uint colour = on ? UiTheme.SUN : hover ? UiTheme.TEXT : UiTheme.TEXT_MUTED;

            _ui.Rect(x, y, size, size, Rgba.Hex(0x0C0E12, _sunPanelOpen || hover ? 0.9f : 0.6f));
            _ui.Outline(x, y, size, size, S(2), _sunPanelOpen ? UiTheme.SUN : Rgba.Hex(0xFFFFFF, 0.14f));

            float cx = x + size * 0.5f, cy = y + size * 0.5f + S(2);
            _ui.Circle(cx, cy, S(7), colour, 16);
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4f;
                float c = MathF.Cos(a), s = MathF.Sin(a);
                _ui.Line(cx + c * S(10), cy + s * S(10), cx + c * S(14), cy + s * S(14), S(2), colour);
            }
            _ui.Text(f.Small, x + S(5), y + S(3), "O", on ? UiTheme.SUN : UiTheme.TEXT_FAINT);

            // Clicks only reach here while the cursor is free (panel open): the icon closes the panel
            if (_sunPanelOpen && hover && input.LeftPressed)
            {
                input.ConsumeClicks();
                CloseSunPanel();
            }
        }

        /// <summary>
        /// The expanded panel above the icon: shadows, quality, time, date, daylight saving, intensities.
        /// </summary>
        private void BuildSunPanel(FontAtlas f, InputState input)
        {
            if (!input.LeftDown) { _activeSlider = -1; }

            float w = S(340), h = S(600);
            SunIconRect(out _, out float iconY, out _);
            float x = _window.Width - S(20) - w;
            float y = MathF.Max(S(20), iconY - S(10) - h);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, Rgba.WithAlpha(UiTheme.SUN, 0.5f));

            // A click anywhere outside a month / day box leaves it
            if (input.LeftPressed && _sunField != 0) { CommitSunField(); }

            float ix = x + S(16), iw = w - S(32);
            float cy = y + S(14);

            // Header
            _ui.Text(f.Small, ix, cy, "SUN, SHADOWS & LIGHTS", UiTheme.SUN_LABEL, S(2f));
            _ui.TextRight(f.Small, ix + iw, cy, "ESC · O CLOSE", UiTheme.TEXT_FAINT, S(0.6f));
            cy += S(22);
            _ui.TextWrapped(f.Small, ix, cy, iw, _sunPlaceLabel, _locationKnown ? UiTheme.TEXT_MUTED : UiTheme.MEASURE_LABEL, maxLines: 1);
            cy += S(24);

            // Shadows on / off + quality
            bool enabled = Checkbox(f, input, ix, cy + S(6), S(110), "Shadows", _sun.Enabled);
            if (enabled != _sun.Enabled) { ToggleShadows(); }
            int quality = Segmented(f, input, ix + S(120), cy, iw - S(120), QUALITY_OPTIONS, (int)_shadowQuality);
            if (quality != (int)_shadowQuality)
            {
                _shadowQuality = (ShadowQuality)quality;
                ShadowMaps.Preset preset = ShadowMaps.PresetFor(_shadowQuality);
                Toast($"Shadow quality: {QUALITY_OPTIONS[quality]} ({preset.Cascades} × {preset.Size} px, {preset.Distance:0} m)");
            }
            cy += S(46);

            // Time of day: slider (5 min steps, Shift = 1 min) and play / pause
            int shownMinutes = _sunPlaying ? (int)_playMinutes : _sun.Time.Minutes;
            Text.Clear();
            AppendTwoDigits(shownMinutes / 60).Append(':');
            AppendTwoDigits(shownMinutes % 60);
            float playSize = S(28);
            float sliderW = iw - playSize - S(10);
            float value = Slider(f, input, SLIDER_TIME, ix, cy, sliderW, "Time of day", Text.Span, shownMinutes, 0f, 1439f);
            if (_activeSlider == SLIDER_TIME && input.LeftDown)
            {
                int step = input.IsDown(Vk.VK_SHIFT) ? 1 : TIME_STEP;
                int minutes = Math.Clamp((int)MathF.Round(value / step) * step, 0, 1439);
                if (_sunPlaying) { _sunPlaying = false; }
                if (minutes != _sun.Time.Minutes)
                {
                    _sun.Time.Minutes = minutes;
                    SunChanged();
                }
            }
            if (PlayButton(input, ix + iw - playSize, cy + S(16), playSize)) { TogglePlay(); }
            cy += S(56);

            // Date: month / day boxes (type, Enter / Tab; ↑ ↓ step) and daylight saving
            _ui.Text(f.Body, ix, cy + S(6), "Date", UiTheme.TEXT);
            if (NumberBox(f, input, ix + S(52), cy, S(48), 1, _sun.Time.Month)) { FocusSunField(1); }
            _ui.Text(f.Small, ix + S(106), cy + S(8), MONTHS_UPPER[Math.Clamp(_sun.Time.Month, 1, 12) - 1], UiTheme.TEXT_MUTED);
            if (NumberBox(f, input, ix + S(142), cy, S(48), 2, _sun.Time.Day)) { FocusSunField(2); }
            bool dst = Checkbox(f, input, ix + S(204), cy + S(6), iw - S(204), "+1 h DST", _sun.Time.DaylightSaving);
            if (dst != _sun.Time.DaylightSaving)
            {
                _sun.Time.DaylightSaving = dst;
                SunChanged();
            }
            cy += S(40);

            // Where the sun is
            BuildSunInfo();
            _ui.Text(f.Small, ix, cy, Text.Span, _sunAltitude > 0 ? UiTheme.TEXT_MUTED : UiTheme.MEASURE_LABEL, S(0.4f));
            cy += S(24);
            _ui.Rect(ix, cy, iw, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.1f));
            cy += S(12);

            // Intensities
            IntensitySlider(f, input, SLIDER_SUN, ix, cy, iw, "Sunlight", _sun.SunIntensity, 2f);
            cy += S(54);
            IntensitySlider(f, input, SLIDER_SKY, ix, cy, iw, "Sky / diffuse light", _sun.SkyIntensity, 2f);
            cy += S(54);
            IntensitySlider(f, input, SLIDER_SHADOW, ix, cy, iw, "Shadow intensity", _sun.ShadowIntensity, 1f);
            cy += S(54);
            IntensitySlider(f, input, SLIDER_GLASS, ix, cy, iw, "Light through glass", _sun.GlassTransmission, 2f);
            cy += S(54);

            // Artificial lights (saved with the settings, not the model). Reflections live in the pause menu.
            cy += BuildLightControls(f, input, ix, cy, iw);

            if (SmallButton(f, input, ix, cy, S(150), S(28), "RESET LIGHTING"))
            {
                _sun.SunIntensity = _sun.SkyIntensity = _sun.ShadowIntensity = _sun.GlassTransmission = 1f;
                _lightIntensity = _bloomIntensity = 1f;
                SunChanged();
            }
            _ui.TextRight(f.Small, ix + iw, cy + S(8), "[ ] TIME · SPACE PLAY", UiTheme.TEXT_FAINT, S(0.4f));
        }

        /// <summary>
        /// A 0–max percentage slider (5 % steps) for one of the sun settings; records a change when the value moves.
        /// </summary>
        private void IntensitySlider(FontAtlas f, InputState input, int id, float x, float y, float w, string label, float value, float max)
        {
            Text.Clear().Append((long)MathF.Round(value * 100f)).Append(" %");
            float result = Slider(f, input, id, x, y, w, label, Text.Span, value, 0f, max);
            result = MathF.Round(result * 20f) / 20f; // 5 % steps
            if (MathF.Abs(result - value) > 1e-4f)
            {
                // Assign before SunChanged so Clean() sees the new value
                switch (id)
                {
                    case SLIDER_SUN: _sun.SunIntensity = result; break;
                    case SLIDER_SKY: _sun.SkyIntensity = result; break;
                    case SLIDER_SHADOW: _sun.ShadowIntensity = result; break;
                    case SLIDER_GLASS: _sun.GlassTransmission = result; break;
                }
                SunChanged();
            }
        }

        /// <summary>
        /// A small numeric text box (month or day). Shows the typed digits and a caret while focused.
        /// </summary>
        /// <returns>True when clicked (to focus it).</returns>
        private bool NumberBox(FontAtlas f, InputState input, float x, float y, float w, int field, int value)
        {
            float h = S(30);
            bool focused = _sunField == field;
            bool hover = Hover(input, x, y, w, h);
            _ui.Rect(x, y, w, h, focused ? Rgba.Hex(0xFFFFFF, 0.08f) : UiTheme.CONTROL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, UiScale), focused ? UiTheme.SUN : hover ? UiTheme.ACCENT : UiTheme.CONTROL_BORDER);

            float textY = y + h * 0.5f - f.Mono.LineHeight * 0.5f;
            if (focused)
            {
                float used = _ui.Text(f.Mono, x + S(10), textY, _sunFieldChars.AsSpan(0, _sunFieldLength), UiTheme.TEXT);
                if ((_clock % 1f) < 0.55f) { _ui.Rect(x + S(11) + used, y + S(7), S(1.5f), h - S(14), UiTheme.SUN); }
            }
            else
            {
                Text.Clear();
                AppendTwoDigits(value);
                _ui.Text(f.Mono, x + S(10), textY, Text.Span, UiTheme.TEXT);
            }

            bool clicked = hover && input.LeftPressed;
            if (clicked)
            {
                input.ConsumeClicks();
                Sound.Play(SoundId.UiClick);
            }
            return clicked;
        }

        /// <summary>
        /// Play / pause toggle (triangle or two bars).
        /// </summary>
        private bool PlayButton(InputState input, float x, float y, float size)
        {
            bool hover = Hover(input, x, y, size, size);
            bool enabled = _sun.Enabled;
            uint colour = !enabled ? UiTheme.TEXT_FAINT : hover || _sunPlaying ? UiTheme.SUN : UiTheme.TEXT;
            _ui.Rect(x, y, size, size, hover && enabled ? Rgba.Hex(0xFFFFFF, 0.1f) : UiTheme.CONTROL);
            _ui.Outline(x, y, size, size, MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);

            float cx = x + size * 0.5f, cy = y + size * 0.5f, r = size * 0.26f;
            if (_sunPlaying)
            {
                _ui.Rect(cx - r, cy - r, r * 0.7f, r * 2f, colour);
                _ui.Rect(cx + r * 0.3f, cy - r, r * 0.7f, r * 2f, colour);
            }
            else
            {
                _ui.Triangle(cx - r * 0.8f, cy - r, cx - r * 0.8f, cy + r, cx + r, cy, colour);
            }

            bool clicked = enabled && hover && input.LeftPressed;
            if (clicked)
            {
                input.ConsumeClicks();
            }
            return clicked;
        }

        /// <summary>
        /// Writes "SUN 42° HIGH · BEARING 310° (NW, TRUE NORTH)" or "SUN BELOW THE HORIZON" into <see cref="Text"/>
        /// (no allocation: it changes every frame while playing).
        /// </summary>
        private void BuildSunInfo()
        {
            int alt = (int)Math.Round(_sunAltitude), az = (int)Math.Round(_sunAzimuth) % 360;
            if (alt <= 0)
            {
                Text.Clear().Append("SUN BELOW THE HORIZON (").Append((float)alt, 0).Append("°) · SKY LIGHT ONLY");
                return;
            }
            Text.Clear().Append("SUN ").Append(alt).Append("° HIGH · BEARING ").Append(az).Append("° (")
                .Append(COMPASS[(int)Math.Round(az / 45.0) % 8]).Append(", TRUE NORTH)");
        }

        private TextBuffer AppendTwoDigits(int value)
        {
            value = Math.Clamp(value, 0, 99);
            return Text.Append((char)('0' + value / 10)).Append((char)('0' + value % 10));
        }

        #endregion
    }
}
