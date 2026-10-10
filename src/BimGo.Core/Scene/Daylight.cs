using System.Numerics;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// What the study panel (J) computes (daylight round).
    /// </summary>
    public enum StudyMode
    {
        /// <summary>Hours of direct sun over a day's time range.</summary>
        SunHours = 0,

        /// <summary>Daylight factor (%): CIE overcast sky, no date or time.</summary>
        DaylightFactor = 1,

        /// <summary>Illuminance (lux): CIE clear sky (+ direct sun) over a day's time range.</summary>
        Illuminance = 2
    }

    /// <summary>
    /// Daylight maths (daylight round), pure and allocation-free per call where it matters:
    /// <list type="bullet">
    /// <item>A fixed sky of 192 patches (8 altitude bands of 11.25° × 24 azimuth bins of 15°, scene axes, Z up): rays are
    /// binned into patches once per cell, then any sky (overcast, or the clear sky at each time sample) is a 192-term sum.</item>
    /// <item>CIE overcast and CIE clear sky luminance distributions; the clear sky is scaled to the IES clear-sky diffuse
    /// horizontal illuminance, and the sun's direct normal illuminance follows the IES extinction formula.</item>
    /// <item>BRE split-flux internally reflected component, reflectance from a colour, and cosine-weighted ray directions.</item>
    /// </list>
    /// The results are early design indicators (geometric sky and sun components, one-bounce external reflection, an
    /// average internal reflection per room), not a validated simulation such as Radiance.
    /// </summary>
    public static class Daylight
    {
        #region Constants

        /// <summary>Sky patch layout.</summary>
        public const int ALT_BANDS = 8, AZ_BINS = 24, PATCHES = ALT_BANDS * AZ_BINS;

        /// <summary>Visible transmittance used for glazing (one layer).</summary>
        public const float GLASS_VLT = 0.7f;

        /// <summary>Ground reflectance outside (light ground seen below the horizon).</summary>
        public const float GROUND_REFLECTANCE = 0.2f;

        /// <summary>Reflectance of external obstructions (other buildings, the model's own outside faces).</summary>
        public const float OBSTRUCTION_REFLECTANCE = 0.2f;

        /// <summary>Standard reflectances (ceiling, walls, floor) used when colours aren't (and as fallbacks).</summary>
        public const float CEILING_REFLECTANCE = 0.7f, WALL_REFLECTANCE = 0.5f, FLOOR_REFLECTANCE = 0.2f;

        /// <summary>BRE's C for an unobstructed view (the split-flux formula's obstruction term).</summary>
        public const float SPLIT_FLUX_C = 39f;

        /// <summary>Legend tops: daylight factor (%) and illuminance (lux).</summary>
        public const float DF_LEGEND_MAX = 5f, LUX_LEGEND_MAX = 2000f;

        private const double BAND = Math.PI / 2.0 / ALT_BANDS;
        private const double BIN = 2.0 * Math.PI / AZ_BINS;

        #endregion

        #region Sky patches

        /// <summary>The patch a direction above the horizon falls in (directions at or below it: -1).</summary>
        public static int PatchOf(Vector3 direction)
        {
            if (direction.Z <= 0f) { return -1; }
            double altitude = Math.Asin(Math.Clamp(direction.Z, 0f, 1f));
            int band = Math.Min(ALT_BANDS - 1, (int)(altitude / BAND));
            double azimuth = Math.Atan2(direction.Y, direction.X);
            if (azimuth < 0) { azimuth += 2.0 * Math.PI; }
            int bin = Math.Min(AZ_BINS - 1, (int)(azimuth / BIN));
            return band * AZ_BINS + bin;
        }

        /// <summary>The unit direction through a patch's centre.</summary>
        public static Vector3 PatchCentre(int patch)
        {
            double altitude = (patch / AZ_BINS + 0.5) * BAND;
            double azimuth = (patch % AZ_BINS + 0.5) * BIN;
            double c = Math.Cos(altitude);
            return new Vector3((float)(c * Math.Cos(azimuth)), (float)(c * Math.Sin(azimuth)), (float)Math.Sin(altitude));
        }

        /// <summary>A patch's solid angle (sr).</summary>
        public static double PatchSolidAngle(int patch)
        {
            int band = patch / AZ_BINS;
            return BIN * (Math.Sin((band + 1) * BAND) - Math.Sin(band * BAND));
        }

        /// <summary>
        /// The illuminance a set of patch luminances gives an unobstructed horizontal plane (Σ L · sin(altitude) · Ω).
        /// </summary>
        public static double HorizontalIlluminance(float[] luminance)
        {
            double sum = 0;
            for (int p = 0; p < PATCHES; p++)
            {
                sum += luminance[p] * PatchCentre(p).Z * PatchSolidAngle(p);
            }
            return sum;
        }

        #endregion

        #region Skies

        /// <summary>CIE overcast sky luminance relative to the zenith: (1 + 2 sin altitude) / 3.</summary>
        public static float OvercastLuminance(Vector3 direction) => (1f + 2f * Math.Max(0f, direction.Z)) / 3f;

        /// <summary>
        /// Fills the overcast sky's patch luminances, scaled so the unobstructed horizontal illuminance is 1 (a daylight
        /// factor is then the illuminance itself).
        /// </summary>
        public static void OvercastPatches(float[] luminance)
        {
            for (int p = 0; p < PATCHES; p++) { luminance[p] = OvercastLuminance(PatchCentre(p)); }
            Scale(luminance, 1.0 / HorizontalIlluminance(luminance));
        }

        /// <summary>
        /// CIE clear sky luminance relative to the zenith for a sky direction and a sun direction (both unit, Z up):
        /// f(χ) φ(Z) / (f(Zs) φ(0)), f(χ) = 0.91 + 10 e^(−3χ) + 0.45 cos² χ, φ(Z) = 1 − e^(−0.32 / cos Z).
        /// </summary>
        public static double ClearLuminance(Vector3 direction, Vector3 sun)
        {
            double cosZ = Math.Max(0.01, direction.Z);
            double chi = Math.Acos(Math.Clamp(Vector3.Dot(direction, sun), -1f, 1f));
            double zs = Math.Acos(Math.Clamp(sun.Z, -1f, 1f));
            double f = 0.91 + 10.0 * Math.Exp(-3.0 * chi) + 0.45 * Math.Cos(chi) * Math.Cos(chi);
            double fs = 0.91 + 10.0 * Math.Exp(-3.0 * zs) + 0.45 * Math.Cos(zs) * Math.Cos(zs);
            double phi = 1.0 - Math.Exp(-0.32 / cosZ);
            double phi0 = 1.0 - Math.Exp(-0.32);
            return f * phi / (fs * phi0);
        }

        /// <summary>
        /// Fills the clear sky's patch luminances (cd/m²) for a sun direction, scaled to the clear-sky diffuse horizontal
        /// illuminance (<see cref="DiffuseHorizontalClear"/>). Returns that illuminance (lux), 0 with the sun down.
        /// </summary>
        public static double ClearPatches(Vector3 sun, float[] luminance)
        {
            double diffuse = DiffuseHorizontalClear(sun.Z);
            if (diffuse <= 0)
            {
                Array.Clear(luminance, 0, PATCHES);
                return 0;
            }
            for (int p = 0; p < PATCHES; p++) { luminance[p] = (float)ClearLuminance(PatchCentre(p), sun); }
            Scale(luminance, diffuse / HorizontalIlluminance(luminance));
            return diffuse;
        }

        /// <summary>Clear-sky diffuse horizontal illuminance (lux, IES): 800 + 15 500 √(sin altitude); 0 with the sun down.</summary>
        public static double DiffuseHorizontalClear(double sinAltitude) =>
            sinAltitude <= 0 ? 0 : 800.0 + 15500.0 * Math.Sqrt(sinAltitude);

        /// <summary>
        /// Clear-sky direct normal illuminance of the sun (lux, IES): 127 500 · e^(−0.21 / sin altitude); 0 below 1°.
        /// </summary>
        public static double DirectNormalClear(double sinAltitude) =>
            sinAltitude <= 0.0175 ? 0 : 127500.0 * Math.Exp(-0.21 / sinAltitude);

        private static void Scale(float[] values, double factor)
        {
            for (int i = 0; i < values.Length; i++) { values[i] = (float)(values[i] * factor); }
        }

        #endregion

        #region Rays

        /// <summary>
        /// <paramref name="count"/> cosine-weighted directions over a hemisphere around +Z (Hammersley points:
        /// deterministic, evenly spread). With them, the illuminance from a luminance field is π/N · Σ L(d).
        /// </summary>
        public static Vector3[] CosineDirections(int count)
        {
            var directions = new Vector3[Math.Max(1, count)];
            for (int i = 0; i < directions.Length; i++)
            {
                double u1 = (i + 0.5) / directions.Length;
                double u2 = RadicalInverse((uint)i);
                double r = Math.Sqrt(u1), angle = 2.0 * Math.PI * u2;
                directions[i] = new Vector3((float)(r * Math.Cos(angle)), (float)(r * Math.Sin(angle)), (float)Math.Sqrt(Math.Max(0.0, 1.0 - u1)));
            }
            return directions;
        }

        private static double RadicalInverse(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10;
        }

        #endregion

        #region Rooms

        /// <summary>
        /// BRE split-flux internally reflected component (% of the outdoor horizontal illuminance):
        /// IRC = 0.85 W / (A (1 − R)) · (C Rfw + 5 Rcw).
        /// </summary>
        /// <param name="windowArea">Glazed area W (m²).</param>
        /// <param name="totalArea">All room surfaces A, windows included (m²).</param>
        /// <param name="averageReflectance">Area-weighted reflectance R of all surfaces.</param>
        /// <param name="lowerReflectance">Rfw: floor and walls below the window's mid-height.</param>
        /// <param name="upperReflectance">Rcw: ceiling and walls above it.</param>
        /// <param name="c">The obstruction term (39 unobstructed).</param>
        public static float InternalReflectedPercent(double windowArea, double totalArea, double averageReflectance, double lowerReflectance, double upperReflectance, double c = SPLIT_FLUX_C)
        {
            if (windowArea <= 0 || totalArea <= 0) { return 0f; }
            double r = Math.Clamp(averageReflectance, 0.0, 0.95);
            return (float)(0.85 * windowArea / (totalArea * (1.0 - r)) * (c * Math.Clamp(lowerReflectance, 0, 1) + 5.0 * Math.Clamp(upperReflectance, 0, 1)));
        }

        /// <summary>
        /// Illuminance (lux) from light entering a room and landing on the floor first: F · Rfw / (A (1 − R)).
        /// </summary>
        public static float FloorBounceLux(double flux, double totalArea, double averageReflectance, double lowerReflectance)
        {
            if (flux <= 0 || totalArea <= 0) { return 0f; }
            return (float)(flux * Math.Clamp(lowerReflectance, 0, 1) / (totalArea * (1.0 - Math.Clamp(averageReflectance, 0.0, 0.95))));
        }

        /// <summary>
        /// A surface reflectance estimated from its colour: the relative luminance of the linearised sRGB colour
        /// (0.2126 R + 0.7152 G + 0.0722 B), kept within 0.05–0.9.
        /// </summary>
        public static float ReflectanceOf(byte r, byte g, byte b)
        {
            double y = 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
            return (float)Math.Clamp(y, 0.05, 0.9);

            static double Linear(byte value)
            {
                double c = value / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
        }

        #endregion

        #region Legends

        /// <summary>The legend colour of a daylight factor (0 → blue, 5 %+ → red; Ladybug's gradient).</summary>
        public static Vector3 FactorColour(float percent) => SunHours.LegendColour(percent / DF_LEGEND_MAX * SunHours.LEGEND_MAX);

        /// <summary>The legend colour of an illuminance (0 → blue, 2000 lux+ → red).</summary>
        public static Vector3 LuxColour(float lux) => SunHours.LegendColour(lux / LUX_LEGEND_MAX * SunHours.LEGEND_MAX);

        #endregion
    }
}
