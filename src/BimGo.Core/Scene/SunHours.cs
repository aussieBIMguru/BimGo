using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// The inputs of a direct sun hours study (sun hours round): the day, the time range and how the test grid is laid
    /// out. Times are local clock minutes after midnight, as in the sun panel.
    /// </summary>
    public sealed class SunHoursSettings
    {
        /// <summary>Month, 1–12 (default: mid-winter in the southern hemisphere, 21 June).</summary>
        public int Month { get; set; } = 6;

        /// <summary>Day of the month (clamped to the month).</summary>
        public int Day { get; set; } = 21;

        /// <summary>Start of the time range (minutes after midnight; default 9:00).</summary>
        public int StartMinutes { get; set; } = 9 * 60;

        /// <summary>End of the time range (minutes after midnight; default 15:00).</summary>
        public int EndMinutes { get; set; } = 15 * 60;

        /// <summary>Minutes between sun positions (5, 10 or 15; default 5).</summary>
        public int StepMinutes { get; set; } = 5;

        /// <summary>True when the clock is on daylight saving.</summary>
        public bool DaylightSaving { get; set; }

        /// <summary>Test grid cell size (m): 0.1, 0.25, 0.5 or 1 (default 0.25).</summary>
        public float GridSize { get; set; } = 0.25f;

        /// <summary>Floors (and other upward faces) are tested this high above the surface (m, 0–2).</summary>
        public float FloorOffset { get; set; }

        /// <summary>Walls (and other faces) are tested this far off the surface (m, 0–1).</summary>
        public float WallOffset { get; set; }

        /// <summary>True when glass stops direct sun (default false: sun passes through glazing).</summary>
        public bool GlassBlocks { get; set; }

        /// <summary>The pass / fail test: off (values and their legend) or on (cells pass or fail the mode's target).</summary>
        public SunTarget Target { get; set; } = SunTarget.Off;

        /// <summary>True when the pass / fail test is on.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool PassFail => Target != SunTarget.Off;

        /// <summary>Sun hours: hours of direct sun a cell needs to pass (0.5–12, default 2).</summary>
        public float TargetHours { get; set; } = 2f;

        /// <summary>What the study computes (daylight round).</summary>
        public StudyMode Mode { get; set; } = StudyMode.SunHours;

        /// <summary>Daylight modes: horizontal faces are tested this high above them (m, 0–2; default 0.7 work plane).</summary>
        public float WorkPlane { get; set; } = 0.7f;

        /// <summary>Daylight modes: rays per cell towards the sky (128, 256 or 512; default 256).</summary>
        public int Rays { get; set; } = 256;

        /// <summary>Illuminance: include direct sun (off: the sky only, no sun patches).</summary>
        public bool DirectSun { get; set; } = true;

        /// <summary>Daylight modes: standard reflectances (ceiling 0.7, walls 0.5, floor 0.2) instead of the colours'.</summary>
        public bool StandardReflectance { get; set; }

        /// <summary>Daylight factor: the % a cell needs to pass (0.5–10, default 2).</summary>
        public float FactorTarget { get; set; } = 2f;

        /// <summary>Illuminance: the lux a cell must reach (50–5000, default 300) ...</summary>
        public float LuxTarget { get; set; } = 300f;

        /// <summary>... for at least this share of the time samples to pass (0.1–1, default 0.5).</summary>
        public float LuxShare { get; set; } = 0.5f;

        /// <summary>The offset horizontal faces are tested at: the floor offset (sun hours) or the work plane (daylight).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public float HorizontalOffset => Mode == StudyMode.SunHours ? FloorOffset : WorkPlane;

        /// <summary>The grid sizes offered.</summary>
        public static readonly float[] GRID_SIZES = { 0.1f, 0.25f, 0.5f, 1f };

        /// <summary>The time steps offered (minutes).</summary>
        public static readonly int[] STEPS = { 5, 10, 15 };

        /// <summary>The ray counts offered for daylight modes.</summary>
        public static readonly int[] RAY_COUNTS = { 128, 256, 512 };

        /// <summary>A copy with every value clamped to something sensible (end after start, known grid and step).</summary>
        public SunHoursSettings Clean(int year)
        {
            int month = Math.Clamp(Month, 1, 12);
            int start = Math.Clamp(StartMinutes, 0, 24 * 60 - 5);
            return new SunHoursSettings
            {
                Month = month,
                Day = Math.Clamp(Day, 1, DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month)),
                StartMinutes = start,
                EndMinutes = Math.Clamp(EndMinutes, start + 5, 24 * 60),
                StepMinutes = STEPS.OrderBy(s => Math.Abs(s - StepMinutes)).First(),
                DaylightSaving = DaylightSaving,
                GridSize = GRID_SIZES.OrderBy(g => MathF.Abs(g - GridSize)).First(),
                FloorOffset = float.IsFinite(FloorOffset) ? Math.Clamp(FloorOffset, 0f, 2f) : 0f,
                WallOffset = float.IsFinite(WallOffset) ? Math.Clamp(WallOffset, 0f, 1f) : 0f,
                GlassBlocks = GlassBlocks,
                Target = Target == SunTarget.Off ? SunTarget.Off : SunTarget.On, // older studies' presets (1, 2) read as on
                TargetHours = Snap(TargetHours, 0.5f, 12f, 0.25f, 2f),
                Mode = Enum.IsDefined(Mode) ? Mode : StudyMode.SunHours,
                WorkPlane = float.IsFinite(WorkPlane) ? Math.Clamp(WorkPlane, 0f, 2f) : 0.7f,
                Rays = RAY_COUNTS.OrderBy(r => Math.Abs(r - Rays)).First(),
                DirectSun = DirectSun,
                StandardReflectance = StandardReflectance,
                FactorTarget = Snap(FactorTarget, 0.5f, 10f, 0.5f, 2f),
                LuxTarget = Snap(LuxTarget, 50f, 5000f, 50f, 300f),
                LuxShare = Snap(LuxShare, 0.1f, 1f, 0.1f, 0.5f)
            };
        }

        /// <summary>A finite value clamped to a range and rounded to a step (else the default).</summary>
        private static float Snap(float value, float min, float max, float step, float fallback) =>
            float.IsFinite(value) ? MathF.Round(Math.Clamp(value, min, max) / step) * step : fallback;
    }

    /// <summary>
    /// The study's pass / fail test. Stored as a number: 1 and 2 (presets in an earlier build) read as on.
    /// </summary>
    public enum SunTarget
    {
        /// <summary>No test: cells show their values on the legend.</summary>
        Off = 0,

        /// <summary>Cells pass or fail the mode's target.</summary>
        On = 3
    }

    /// <summary>
    /// Direct sun hours (sun hours round): the sun positions of a study and the Ladybug legend colours.
    /// A test point gets <see cref="SunHoursSettings.StepMinutes"/> of sun for every sample whose sun is above the
    /// horizon, in front of the surface and not blocked; samples sit in the middle of each step
    /// (9:00–15:00 at 5 min = 72 samples, 9:02:30 … 14:57:30).
    /// </summary>
    public static class SunHours
    {
        /// <summary>The legend's top (h): Ladybug's usual 0–7 h sun hours range; more reads as the top colour.</summary>
        public const float LEGEND_MAX = 7f;

        /// <summary>
        /// Ladybug's default ("original") legend colours, low to high (RGB 0–255).
        /// </summary>
        public static readonly (byte R, byte G, byte B)[] LADYBUG =
        {
            (75, 107, 169), (115, 147, 202), (170, 200, 247), (193, 213, 208), (245, 239, 103),
            (252, 230, 74), (239, 156, 21), (234, 123, 0), (234, 74, 0), (234, 38, 0)
        };

        /// <summary>
        /// The sun directions (unit vectors towards the sun, in the model's scene axes) of the samples whose sun is
        /// above the horizon. Uses the model's site (Revit Location) and true north; without a site, Sydney.
        /// </summary>
        /// <param name="site">The model's site.</param>
        /// <param name="year">The calendar year (leap years).</param>
        /// <param name="settings">The study.</param>
        /// <param name="samples">Out: every sample in the range (above the horizon or not), for the "of n" readout.</param>
        /// <param name="locationKnown">Out: false when the fallback location was used.</param>
        public static List<Vector3> SunDirections(SiteInfo site, int year, SunHoursSettings settings, out int samples, out bool locationKnown)
        {
            SunHoursSettings s = settings.Clean(year);
            GeoLocation location = SolarPosition.LocationOf(site, out locationKnown);
            double north = SolarPosition.NorthAngle(site);
            var directions = new List<Vector3>();
            samples = 0;
            for (double minutes = s.StartMinutes + s.StepMinutes * 0.5; minutes < s.EndMinutes; minutes += s.StepMinutes)
            {
                samples++;
                SolarPosition.Compute(location, year, s.Month, s.Day, minutes, s.DaylightSaving, out double altitude, out double azimuth);
                if (altitude <= 0.0) { continue; }
                directions.Add(SolarPosition.ToModel(SolarPosition.Direction(altitude, azimuth), north));
            }
            return directions;
        }

        /// <summary>Pass colour (green, RGB 0–1) for pass / fail colouring.</summary>
        public static readonly Vector3 PASS = new(0.20f, 0.72f, 0.36f);

        /// <summary>Fail colour (red, RGB 0–1) for pass / fail colouring.</summary>
        public static readonly Vector3 FAIL = new(0.86f, 0.22f, 0.20f);

        /// <summary>True when a cell's hours meet the target (with a small tolerance for sample rounding).</summary>
        public static bool Passes(float hours, float targetHours) => hours >= targetHours - 1e-4f;

        /// <summary>
        /// The legend colour of a number of hours (0 → blue, <see cref="LEGEND_MAX"/> and over → red), interpolated
        /// between Ladybug's colours; RGB 0–1.
        /// </summary>
        public static Vector3 LegendColour(float hours)
        {
            float t = float.IsFinite(hours) ? Math.Clamp(hours / LEGEND_MAX, 0f, 1f) : 0f;
            float position = t * (LADYBUG.Length - 1);
            int i = Math.Min((int)position, LADYBUG.Length - 2);
            float f = position - i;
            (byte r0, byte g0, byte b0) = LADYBUG[i];
            (byte r1, byte g1, byte b1) = LADYBUG[i + 1];
            return new Vector3(r0 + (r1 - r0) * f, g0 + (g1 - g0) * f, b0 + (b1 - b0) * f) / 255f;
        }
    }
}
