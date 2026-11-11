// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// GLSL sources (330 core, runs on GL 3.3 and 4.x core contexts).
    /// Matrices are uploaded straight from System.Numerics, so "M * v" in GLSL matches "v * M" in C#.
    /// </summary>
    internal static class Shaders
    {
        #region Section cut (section box round)

        /// <summary>
        /// The section cut, shared by the scene and geometry pre-pass fragment shaders (after their #version line):
        /// up to 7 planes (n, d) in scene-local coordinates; a point with n·p > d is cut away. uClipCount 0 = no cut
        /// (the default for passes that ignore it: shadows, probes, the minimap).
        /// </summary>
        public const string CLIP_GLSL = @"
uniform int uClipCount;
uniform vec4 uClipPlanes[8];
bool sectionCut(vec3 p)
{
    for (int i = 0; i < uClipCount; i++)
    {
        if (dot(uClipPlanes[i].xyz, p) > uClipPlanes[i].w) return true;
    }
    return false;
}
";

        /// <summary>
        /// Section caps, stencil pass (vertex shader: <see cref="SCENE_VS"/>): the scene cut by one plane only, written
        /// to the stencil (INVERT) with no colour; pixels left odd look into a solid the plane cuts.
        /// With uCapColour.a > 0 (element-colour caps, drawn on back faces) it writes the surface's own colour, darkened.
        /// </summary>
        public const string CAP_STENCIL_FS = @"#version 330 core
in vec3 vWorld;
in vec4 vColor;
flat in vec4 vProfile;
uniform int uProfilePass;
uniform int uCapColourPass;
uniform vec4 uCapPlane;
uniform float uCapShade;
out vec4 oColor;
void main()
{
    if (dot(uCapPlane.xyz, vWorld) > uCapPlane.w) discard;
    // Colour round A fix: a colour profile's ghosted (and hidden) elements aren't solid, so they get no cap
    int code = uProfilePass > 0 ? int(vProfile.a * 255.0 + 0.5) : 0;
    if (code == 1 || code == 2 || (code >= 128 && code < 255)) discard;
    // Polish round P-C: the colour pass paints the caps of profile-coloured elements in their colour (others: flat cap)
    if (uCapColourPass > 0)
    {
        if (code != 255) discard;
        oColor = vec4(vProfile.rgb * uCapShade, 1.0);
        return;
    }
    oColor = vec4(vColor.rgb * uCapShade, 1.0);
}";

        /// <summary>Section caps, cap polygon: a face of the box or the free plane, positions in scene-local metres.</summary>
        public const string CAP_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 uViewProj;
out vec3 vWorld;
void main()
{
    vWorld = aPos;
    gl_Position = uViewProj * vec4(aPos, 1.0);
}";

        /// <summary>
        /// Section caps, cap polygon fragment shader: cut by every other plane of the cut (uSkip is its own), flat
        /// colour.
        /// </summary>
        public const string CAP_FS = "#version 330 core\n" + @"
uniform int uClipCount;
uniform vec4 uClipPlanes[7];
uniform int uSkip;
uniform vec4 uColor;
in vec3 vWorld;
out vec4 oColor;
void main()
{
    for (int i = 0; i < uClipCount; i++)
    {
        if (i != uSkip && dot(uClipPlanes[i].xyz, vWorld) > uClipPlanes[i].w + 0.0005) discard;
    }
    oColor = uColor;
}";

        #endregion

        #region Photo mode

        /// <summary>
        /// Photo exposure (vertex shader: <see cref="FULLSCREEN_VS"/>): a flat colour blended over the scene with
        /// glBlendFunc(DST_COLOR, ZERO) (darker: colour = factor) or (DST_COLOR, ONE) (brighter: colour = factor − 1).
        /// </summary>
        public const string EXPOSURE_FS = @"#version 330 core
in vec2 vNdc;
uniform vec3 uColor;
out vec4 oColor;
void main()
{
    oColor = vec4(uColor, 1.0);
}";

        #endregion

        #region Scene (static batches; uModel is identity except for moved / cloned elements)

        /// <summary>
        /// Scene vertex shader. aEmissive (location 3) is the per-vertex glow (RGB colour, A = strength / 4); models
        /// without glowing surfaces leave the attribute disabled, so it reads (0, 0, 0, 1) = no glow.
        /// aMaterial (4, the Realistic-mode material index; 65535 = none) and aUv (5, surface coordinates in metres)
        /// are only enabled for snapshots with materials; the fragment shader only reads them when uRealistic = 1.
        /// Colour round A: aElement (6, the element's index as a float: exact up to 16.7 million) is only enabled once a
        /// colour profile was shown; with uProfilePass above 0 the element's profile code is fetched here, once per
        /// vertex, from uProfileTex (one RGBA8 texel per element, PROFILE_WIDTH texels a row): rgb = colour, a = code
        /// (0 normal, 1 ghost, 2 hidden, 255 coloured).
        /// </summary>
        public const string SCENE_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aColor;
layout(location = 3) in vec4 aEmissive;
layout(location = 4) in float aMaterial;
layout(location = 5) in vec2 aUv;
layout(location = 6) in float aElement;
uniform mat4 uViewProj;
uniform mat4 uModel;
uniform int uProfilePass;
uniform sampler2D uProfileTex;
out vec3 vWorld;
invariant gl_Position; // Round Y-D2: the depth pre-pass and the scene pass must land on the same depths (EQUAL test)
out vec3 vNormal;
out vec4 vColor;
out vec3 vEmissive;
flat out int vMaterial;
out vec2 vUv;
flat out vec4 vProfile;
void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = mat3(uModel) * aNormal;
    vColor = aColor;
    vEmissive = aEmissive.rgb * (aEmissive.a * 4.0);
    vMaterial = int(aMaterial + 0.5);
    vUv = aUv;
    vProfile = vec4(0.0);
    if (uProfilePass > 0)
    {
        int e = int(aElement + 0.5);
        vProfile = texelFetch(uProfileTex, ivec2(e % " + PROFILE_WIDTH_TEXT + @", e / " + PROFILE_WIDTH_TEXT + @"), 0);
    }
    gl_Position = uViewProj * world;
}";

        /// <summary>Colour round A: texels per row of the profile texture (keep in step with the GLSL above).</summary>
        public const int PROFILE_WIDTH = 1024;

        private const string PROFILE_WIDTH_TEXT = "1024";

        /// <summary>
        /// Realistic colour mode (inserted after LIGHTS_GLSL in the scene fragment shader). The material table holds
        /// six texels per material (MaterialTextures): t0 (colour, fade) t1 (image tint, glass reflectivity) t2 (scale U,
        /// scale V, offset U, offset V) t3 (cos angle, sin angle, bucket or -1, layer) t4 (appearance tint, flags;
        /// 1 = invert the image, 2 = a proxy whose image tint always multiplies) t5 (shine, roughness, flags: 1 = metal,
        /// 2 = water; ripple strength).
        /// <para>Reflections (reflection probes round, build A, sky only): shine is rounded to the nearest 25 % tier and
        /// reflects when the tier reaches uReflectThreshold (water always). Each tier is drawn at a fixed head-on
        /// strength (metals 0.30 / 0.55 / 0.90, other surfaces half that, Fresnel adds the rest at grazing angles);
        /// roughness blurs the sky towards its average and takes away most of the grazing boost. Glass: Revit's
        /// value × 2.5, kept within 10–50 %. uReflectGain is the user's strength slider. Water tilts the normal with six
        /// travelling waves (uTime: the water clock, which runs at the ripple speed), scaled by uWaterWaves (ripple strength; 0
        /// skips the waves) and stretched by uWaveScale (wave size: the wavelengths × it, the slope unchanged) and faded with distance, and adds a sun glint (no shadow test).</para>
        /// <para>Probes (build B): where the plan grid (uProbeGrid) names a baked probe, the reflection reads it instead
        /// of the sky: box-projected against the probe's room box, the mip level from roughness, two probes blended near
        /// room boundaries. Faces use the light-shadow face table (FACE_F / FACE_U / LIGHT_PAD from LIGHTS_GLSL, which
        /// comes first). uReflectDebug 2 colours each probe's cells.</para> Images live in one array per size bucket; the gradients are taken from the
        /// metric coordinates before any branching (textureGrad), so mip selection stays correct inside the
        /// per-material branches. Keep the four buckets in step with MaterialTextures.BUCKETS.
        /// <para>Order per fragment: image → invert → image tint → fade over the colour → appearance tint over the
        /// result, all in linear light as Revit's renderer does (checked on Gavin's tint test model: a 50 % fade and an
        /// inverted image only match Revit's Realistic view when blended linearly). The result goes back to the
        /// display-referred values the rest of the lighting expects. Proxies are coloured before linearising (their
        /// tint is worked out in display values). uTintMode: 0 = Revit tint off, otherwise multiply (Revit's blend,
        /// confirmed on the same model).</para>
        /// </summary>
        /// <summary>
        /// Sky round: a procedural cloud layer (value-noise fbm on a plane above the eye, drifting by uCloudOffset;
        /// uCloudCover 0 = clear, 1 = overcast). Shared by the sky and the scene's sky reflections. Costs nothing at
        /// 0 % cover (one uniform branch).
        /// </summary>
        public const string CLOUD_GLSL = @"
uniform float uCloudCover;
uniform vec2 uCloudOffset;
float cloudHash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
float cloudNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(cloudHash(i), cloudHash(i + vec2(1.0, 0.0)), u.x),
               mix(cloudHash(i + vec2(0.0, 1.0)), cloudHash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float cloudFbm(vec2 p)
{
    float a = 0.5;
    float s = 0.0;
    for (int i = 0; i < 5; i++)
    {
        s += a * cloudNoise(p);
        p = p * 2.03 + vec2(17.1, 9.2);
        a *= 0.5;
    }
    return s / 0.96875;
}
// Clouds in a direction: rgb = colour, a = cover. light = the sun's colour (dims at night), sky = the horizon colour.
vec4 cloudLayer(vec3 dir, vec3 sunDir, vec3 light, vec3 sky)
{
    if (uCloudCover <= 0.001 || dir.z <= 0.0) return vec4(0.0);
    vec2 p = dir.xy / (dir.z + 0.06) * 1.6 + uCloudOffset;
    float n = cloudFbm(p);
    float t = mix(0.72, -0.25, uCloudCover);
    float a = smoothstep(t, t + 0.35, n) * smoothstep(0.0, 0.12, dir.z);
    float thick = clamp((n - t) * 1.5, 0.0, 1.0);
    vec3 lit = sky * 0.75 + light * 0.3;
    vec3 col = mix(lit, lit * 0.68, thick * (0.4 + 0.6 * uCloudCover));
    col += light * 0.35 * pow(max(dot(dir, sunDir), 0.0), 12.0) * (1.0 - thick);
    return vec4(col, a);
}
";

        public const string MATERIALS_GLSL = @"
uniform int uRealistic;
uniform sampler2D uMaterialTable;
uniform sampler2DArray uTex0;
uniform sampler2DArray uTex1;
uniform sampler2DArray uTex2;
uniform sampler2DArray uTex3;
uniform int uReflections;
uniform vec4 uWaterTint;
uniform float uWaterReflect;
uniform float uWaterWaves;
uniform float uWaveScale;
uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform int uTintMode;
uniform float uReflectThreshold;
uniform float uReflectGain;
uniform int uReflectDebug;
uniform float uTime;
uniform int uProbesOn;
uniform sampler2DArray uProbeArray;
uniform sampler2DArray uProbeGrid;
uniform sampler2D uProbeData;
uniform vec3 uProbeGridOrigin;
uniform vec3 uProbeGridCell;
uniform vec3 uProbeGridSize;
uniform float uProbeMaxLod;

// Revit works in linear light: display values (sRGB, approximated by gamma 2.2) in, linear blending, display out
vec3 toLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
vec3 toDisplay(vec3 c) { return pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2)); }

// Revit tint (a multiply in linear light), or none when switched off
vec3 applyTint(vec3 c, vec3 tint)
{
    return uTintMode == 0 ? c : c * toLinear(tint);
}

vec3 materialTexture(int bucket, vec3 uvl, vec2 gx, vec2 gy)
{
    if (bucket == 0) return textureGrad(uTex0, uvl, gx, gy).rgb;
    if (bucket == 1) return textureGrad(uTex1, uvl, gx, gy).rgb;
    if (bucket == 2) return textureGrad(uTex2, uvl, gx, gy).rgb;
    return textureGrad(uTex3, uvl, gx, gy).rgb;
}

// Realistic base colour (rgb) and head-on reflectivity (a) of a material at a metric surface coordinate
vec4 realisticColour(int id, vec2 uv, vec2 dx, vec2 dy)
{
    vec4 t0 = texelFetch(uMaterialTable, ivec2(0, id), 0);
    vec4 t1 = texelFetch(uMaterialTable, ivec2(1, id), 0);
    vec4 t3 = texelFetch(uMaterialTable, ivec2(3, id), 0);
    vec4 t4 = texelFetch(uMaterialTable, ivec2(4, id), 0);
    vec3 colour = toLinear(t0.rgb);
    if (t3.z >= 0.0)
    {
        vec4 t2 = texelFetch(uMaterialTable, ivec2(2, id), 0);
        mat2 turn = mat2(t3.x, t3.y, -t3.y, t3.x);
        // Image rows run top-down, V runs up the surface: flip V so images stand upright
        vec2 inv = vec2(1.0, -1.0) / t2.xy;
        vec2 st = (turn * (uv - t2.zw)) * inv;
        vec3 image = materialTexture(int(t3.z + 0.5), vec3(st, t3.w), (turn * dx) * inv, (turn * dy) * inv);
        bool proxy = mod(floor(t4.a * 0.5), 2.0) >= 1.0;
        if (proxy)
        {
            image = toLinear(clamp(image * t1.rgb, 0.0, 1.0)); // the material-colour match, always applied
        }
        else
        {
            image = toLinear(image);
            if (mod(t4.a, 2.0) >= 1.0) image = vec3(1.0) - image;
            image = applyTint(image, t1.rgb);
        }
        colour = mix(colour, clamp(image, 0.0, 1.0), t0.a);
    }
    colour = applyTint(colour, t4.rgb);
    return vec4(toDisplay(colour), t1.a);
}

vec3 skyColour(vec3 dir)
{
    float h = dir.z;
    if (h < 0.0) return uSkyHorizon * 0.55;
    vec3 c = mix(uSkyHorizon, uSkyZenith, pow(clamp(h, 0.0, 1.0), 0.55));
    // Sky round: the clouds in reflections (the sun's colour lights them; uSunDir / uSunColor from SUN_GLSL)
    vec4 cloud = cloudLayer(dir, uSunDir, uSun == 1 ? uSunColor * 2.6 : vec3(0.6), uSkyHorizon);
    return mix(c, cloud.rgb, cloud.a);
}

// t5: shine (0-1), roughness (0-1), flags (1 = metal, 2 = water), ripple strength
vec4 reflectionInfo(int id)
{
    return texelFetch(uMaterialTable, ivec2(5, id), 0);
}

// Shine rounded to the nearest quarter: 0, 1 (25 %), 2 (50 %), 3 (75 % +)
int reflectionTier(float shine)
{
    return int(clamp(floor(shine * 4.0 + 0.5), 0.0, 3.0));
}

// Head-on strength drawn per tier (metals; other surfaces get half, Fresnel adds the rest at grazing angles)
float tierStrength(int tier)
{
    return tier == 3 ? 0.90 : tier == 2 ? 0.55 : tier == 1 ? 0.30 : 0.0;
}

// The sky in a direction, blurred towards its average by roughness (stands in for a blurred probe until build B)
vec3 blurredSky(vec3 dir, float rough)
{
    vec3 average = mix(uSkyHorizon, uSkyZenith, 0.35);
    return mix(skyColour(dir), average, clamp(rough * 1.25, 0.0, 1.0));
}

// Water ripples: the slope of six travelling waves over the plan (metres, seconds), faded with distance so far
// water doesn't shimmer; tilts an up-facing normal
const vec2 WAVE_DIR[6] = vec2[6](vec2(0.951, 0.309), vec2(-0.588, 0.809), vec2(0.208, -0.978), vec2(-0.866, -0.500), vec2(0.743, 0.669), vec2(-0.105, 0.995));
const float WAVE_K[6] = float[6](1.05, 1.65, 2.6, 3.95, 5.85, 8.65);
const float WAVE_SPEED[6] = float[6](0.9, 1.3, 1.7, 2.2, 2.9, 3.6);
const float WAVE_AMP[6] = float[6](1.0, 0.7, 0.5, 0.35, 0.25, 0.18);
vec3 waterNormal(vec3 world, vec3 n, float amount, float dist)
{
    vec2 slope = vec2(0.0);
    for (int i = 0; i < 6; i++)
    {
        float phase = dot(WAVE_DIR[i], world.xy) * (WAVE_K[i] / uWaveScale) + uTime * WAVE_SPEED[i];
        slope += WAVE_DIR[i] * (WAVE_AMP[i] * WAVE_K[i] * cos(phase));
    }
    slope *= amount * uWaterWaves * 0.2 / (1.0 + dist * 0.06);
    return normalize(n + vec3(-slope, 0.0));
}

// The probes at a surface point: x = first probe (-1 = none: the sky), y = second (-1 none), z = the second's weight.
// The cell is looked up a little off the surface along its (viewer-facing) normal, so walls, glass, floors and
// ceilings, which lie on a room boundary, read the room they face rather than whichever room their cell straddles
// (3/4 of a plan cell, at least 0.3 m: the pushed point's cell centre then stays on the same side).
vec3 probeCell(vec3 world, vec3 n)
{
    if (uProbesOn == 0) return vec3(-1.0, -1.0, 0.0);
    vec3 c = floor((world + n * max(0.3, 0.75 * uProbeGridCell.x) - uProbeGridOrigin) / uProbeGridCell);
    if (any(lessThan(c, vec3(0.0))) || any(greaterThanEqual(c, uProbeGridSize))) return vec3(-1.0, -1.0, 0.0);
    vec4 t = texelFetch(uProbeGrid, ivec3(c), 0);
    return vec3(floor(t.r * 255.0 + 0.5) - 1.0, floor(t.g * 255.0 + 0.5) - 1.0, t.b);
}

// One probe's view along a reflection, box-projected against its room box when the point is inside it (so a
// floor reflects the walls where they are, not as if infinitely far); ok = false while the probe isn't baked.
// near (0-1): how far the box-projected hit is from the point; close hits (the floor at the foot of glass) stretch a
// tiny patch of the capture and smear whatever stood between the probe and that floor, so they're kept at least
// PROBE_MIN_HIT away and the caller fades them.
const float PROBE_MIN_HIT = 0.75;
vec3 probeSample(int i, vec3 world, vec3 dir, float lod, out bool ok, out float near)
{
    near = 1.0;
    vec4 a = texelFetch(uProbeData, ivec2(0, i), 0);
    vec4 lo = texelFetch(uProbeData, ivec2(1, i), 0);
    vec4 hi = texelFetch(uProbeData, ivec2(2, i), 0);
    ok = lo.w > 0.5;
    if (!ok) return vec3(0.0);
    vec3 v = dir;
    if (all(greaterThanEqual(world, lo.xyz - 0.05)) && all(lessThanEqual(world, hi.xyz + 0.05)))
    {
        vec3 safe = vec3(abs(dir.x) < 1e-5 ? 1e-5 : dir.x, abs(dir.y) < 1e-5 ? 1e-5 : dir.y, abs(dir.z) < 1e-5 ? 1e-5 : dir.z);
        vec3 tFar = max((hi.xyz - world) / safe, (lo.xyz - world) / safe);
        float t = max(min(min(tFar.x, tFar.y), tFar.z), 0.0);
        near = smoothstep(0.15, 1.5, t);
        t = max(t, PROBE_MIN_HIT);
        v = world + dir * t - a.xyz;
        if (dot(v, v) < 1e-6) v = dir;
    }
    vec3 m = abs(v);
    int face = (m.x >= m.y && m.x >= m.z) ? (v.x > 0.0 ? 0 : 1) : (m.y >= m.z ? (v.y > 0.0 ? 2 : 3) : (v.z > 0.0 ? 4 : 5));
    vec3 F = FACE_F[face];
    vec3 U = FACE_U[face];
    vec3 R = cross(F, U);
    float zf = max(dot(v, F), 1e-4);
    vec2 uv = vec2(dot(v, R), dot(v, U)) / (zf * LIGHT_PAD) * 0.5 + 0.5;
    return textureLod(uProbeArray, vec3(uv, a.w + float(face)), lod).rgb;
}

// The reflected environment from the probes (blended near room boundaries); have = false where there is none yet;
// weight (0.4-1) fades reflections of very close hits (see probeSample). Smooth surfaces read at least half a mip
// level down: a capture is magnified on big glass and mirrors, and the slight softening hides its texels.
vec3 probeEnvironment(vec3 world, vec3 n, vec3 dir, float rough, out bool have, out float weight)
{
    have = false;
    weight = 1.0;
    vec3 cell = probeCell(world, n);
    if (cell.x < 0.0) return vec3(0.0);
    // Round X-C1: roofs, decks and other up-facing surfaces outside rooms reflect the sky: their fallback probes sit on
    // an 8 m grid, each box-projected differently, which drew a patchwork of blocks (plainest at night)
    if (n.z > 0.3 && texelFetch(uProbeData, ivec2(2, int(cell.x)), 0).w > 0.5) return vec3(0.0);
    float lod = max(clamp(rough, 0.0, 1.0) * uProbeMaxLod, 0.5);
    bool okA, okB;
    float nearA, nearB;
    vec3 env = probeSample(int(cell.x), world, dir, lod, okA, nearA);
    float near = nearA;
    if (cell.y >= 0.0 && cell.z > 0.0)
    {
        vec3 other = probeSample(int(cell.y), world, dir, lod, okB, nearB);
        if (okA && okB) { env = mix(env, other, cell.z); near = mix(nearA, nearB, cell.z); }
        else if (okB) { env = other; okA = true; near = nearB; }
    }
    have = okA;
    weight = mix(0.4, 1.0, near);
    return env;
}

// Debug colours (probe cells): one hue per probe, blended like the reflections; grey where there is none
vec3 probeDebugColour(vec3 world, vec3 n)
{
    vec3 cell = probeCell(world, n);
    if (cell.x < 0.0) return vec3(0.45);
    vec3 a = 0.5 + 0.45 * cos(6.2832 * (cell.x * 0.618 + vec3(0.0, 0.33, 0.67)));
    if (cell.y < 0.0) return a;
    vec3 b = 0.5 + 0.45 * cos(6.2832 * (cell.y * 0.618 + vec3(0.0, 0.33, 0.67)));
    return mix(a, b, cell.z);
}

// Debug colours: glass cyan, water blue, 75 % + red, 50 % orange, 25 % yellow, none grey
vec3 reflectionDebugColour(vec4 info, float glass)
{
    if (glass > 0.0) return vec3(0.35, 0.85, 1.0);
    int flags = int(info.z + 0.5);
    if ((flags & 2) != 0) return vec3(0.10, 0.35, 1.0);
    int tier = reflectionTier(info.x);
    if (tier == 3) return vec3(0.95, 0.15, 0.15);
    if (tier == 2) return vec3(1.0, 0.55, 0.10);
    if (tier == 1) return vec3(0.95, 0.90, 0.25);
    return vec3(0.55);
}
";

        /// <summary>
        /// Round X-C1: screen-space reflections for the scene shader (after MATERIALS_GLSL; see
        /// SceneRenderer.ScreenReflections). The reflected ray is marched through this frame's half-resolution pre-pass
        /// (camera-basis normal, view depth; 0 = sky) in steps that grow from 6 cm to 60 m; a step that passes behind a
        /// surface (within a thickness that grows with the step) is refined by halving, and the hit is looked up in the
        /// previous frame's colour through the previous camera. The alpha is how far to trust it: faded at the screen
        /// edges, far along the ray, on surfaces facing away from the ray and on rough surfaces (whose blur reads a lower
        /// mip). 0 = keep the probe or sky reflection.
        /// </summary>
        public const string SSR_GLSL = @"
uniform int uSsrQuality;
uniform sampler2D uSsrGeometry;
uniform sampler2D uSsrColour;
uniform mat4 uSsrViewProj;
uniform mat4 uSsrPrevViewProj;
uniform vec3 uSsrRight;
uniform vec3 uSsrUp;
uniform vec3 uSsrForward;
uniform float uSsrMaxLod;

const float SSR_T0 = 0.06;
const float SSR_TMAX = 60.0;

vec2 ssrProject(mat4 m, vec3 p, out bool front)
{
    vec4 c = m * vec4(p, 1.0);
    front = c.w > 1e-3;
    return (c.xy / max(c.w, 1e-3)) * 0.5 + 0.5;
}

bool ssrOnScreen(vec2 uv)
{
    return uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0;
}

// The pre-pass at a screen position: xyz the normal (camera basis, facing the camera), w the view depth (0 = sky)
vec4 ssrGeometry(vec2 uv)
{
    ivec2 size = textureSize(uSsrGeometry, 0);
    return texelFetch(uSsrGeometry, clamp(ivec2(uv * vec2(size)), ivec2(0), size - 1), 0);
}

vec4 screenReflection(vec3 world, vec3 n, vec3 dir, float rough, vec3 eye)
{
    if (uSsrQuality <= 0 || rough > 0.6) return vec4(0.0);
    int steps = uSsrQuality == 1 ? 12 : uSsrQuality == 2 ? 24 : 48;
    int halvings = uSsrQuality == 1 ? 3 : uSsrQuality == 2 ? 4 : 6;
    float grow = pow(SSR_TMAX / SSR_T0, 1.0 / float(steps - 1));
    vec3 origin = world + n * 0.03;
    float t = SSR_T0, before = 0.0;
    bool hit = false;
    for (int i = 0; i < 48; i++)
    {
        if (i >= steps) break;
        vec3 q = origin + dir * t;
        bool front;
        vec2 uv = ssrProject(uSsrViewProj, q, front);
        if (!front || !ssrOnScreen(uv)) return vec4(0.0);
        float scene = ssrGeometry(uv).w;
        float depth = dot(q - eye, uSsrForward);
        float behind = depth - scene;
        if (scene > 0.0 && behind > 0.02 + 0.002 * depth && behind < max(0.3, (t - before) * 1.5))
        {
            hit = true;
            break;
        }
        before = t;
        t *= grow;
    }
    if (!hit) return vec4(0.0);

    // Halve the last step to land on the surface
    float a = before, b = t;
    for (int i = 0; i < 6; i++)
    {
        if (i >= halvings) break;
        float m = 0.5 * (a + b);
        vec3 q = origin + dir * m;
        bool front;
        vec2 uv = ssrProject(uSsrViewProj, q, front);
        float scene = ssrGeometry(uv).w;
        if (scene > 0.0 && dot(q - eye, uSsrForward) > scene) b = m; else a = m;
    }
    vec3 q = origin + dir * b;
    bool frontNow, frontThen;
    vec2 now = ssrProject(uSsrViewProj, q, frontNow);
    vec2 then = ssrProject(uSsrPrevViewProj, q, frontThen);
    if (!frontThen || !ssrOnScreen(then)) return vec4(0.0);

    // The surface met must face the ray (the pre-pass normal faces the camera)
    vec4 g = ssrGeometry(now);
    vec3 hn = g.x * uSsrRight + g.y * uSsrUp + g.z * uSsrForward;
    float facing = smoothstep(0.0, 0.25, -dot(hn, dir));
    vec2 edgeNow = min(now, 1.0 - now), edgeThen = min(then, 1.0 - then);
    float edge = smoothstep(0.0, 0.06, min(edgeNow.x, edgeNow.y)) * smoothstep(0.0, 0.06, min(edgeThen.x, edgeThen.y));
    float trust = facing * edge * (1.0 - smoothstep(SSR_TMAX * 0.4, SSR_TMAX, b)) * (1.0 - smoothstep(0.3, 0.6, rough));
    vec3 colour = textureLod(uSsrColour, then, rough * uSsrMaxLod).rgb;
    return vec4(colour, trust);
}
";

        /// <summary>
        /// Round X-C1: planar reflections for the scene shader (after SSR_GLSL; see SceneRenderer.PlanarReflections): up to
        /// eight glass / mirror planes drawn for a still from the mirrored camera into the layers of uPlanarTex. A fragment
        /// lying on one of them (within its slack: the pane's thickness, a second door track, + 3 cm; facing along it) reads
        /// the layer at its own screen position.
        /// </summary>
        public const string PLANAR_GLSL = @"
uniform int uPlanarCount;
uniform vec4 uPlanarPlane[8];
uniform vec4 uPlanarSlack[2];
uniform vec2 uPlanarInvScreen;
uniform sampler2DArray uPlanarTex;

bool planarReflection(vec3 world, vec3 n, out vec3 colour)
{
    colour = vec3(0.0);
    for (int i = 0; i < 8; i++)
    {
        if (i >= uPlanarCount) break;
        vec4 P = uPlanarPlane[i];
        vec4 slack4 = uPlanarSlack[i / 4];
        int j = i - (i / 4) * 4;
        float slack = j == 0 ? slack4.x : j == 1 ? slack4.y : j == 2 ? slack4.z : slack4.w;
        if (abs(dot(P.xyz, world) - P.w) < slack && abs(dot(P.xyz, n)) > 0.97)
        {
            colour = texture(uPlanarTex, vec3(gl_FragCoord.xy * uPlanarInvScreen, float(i))).rgb;
            return true;
        }
    }
    return false;
}
";

        /// <summary>
        /// Sun lighting and cascaded shadow lookup, shared by the scene and ground fragment shaders (inserted after
        /// the #version line). With uSun = 0 nothing here is evaluated and the classic fixed light is used.
        /// Shadow maps: a depth array (hardware comparison, PCF) and a transmittance array (light through glass,
        /// already limited to glass in front of the first opaque surface, so it applies to lit receivers only).
        /// </summary>
        public const string SUN_GLSL = @"
uniform int uSun;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform vec3 uSkyColor;
uniform float uShadowStrength;
uniform int uShadowsOn;
uniform int uTransmitOn;
uniform vec3 uCamForward;
uniform int uCascadeCount;
uniform vec4 uCascadeFar;
uniform vec4 uNormalOffset;
uniform mat4 uShadowMat[4];
uniform float uShadowTexel;
uniform int uPcf;
uniform float uShadowFar;
uniform sampler2DArrayShadow uShadowMap;
uniform sampler2DArray uTransmit;

vec3 sunVisibility(vec3 world, vec3 n, vec3 eye)
{
    if (uShadowsOn == 0) return vec3(1.0);
    float viewDepth = dot(world - eye, uCamForward);
    if (viewDepth > uShadowFar) return vec3(1.0);

    int c = uCascadeCount;
    for (int i = 0; i < 4; i++)
    {
        if (i < uCascadeCount && viewDepth <= uCascadeFar[i]) { c = i; break; }
    }
    if (c >= uCascadeCount) return vec3(1.0);

    vec4 s = uShadowMat[c] * vec4(world + n * uNormalOffset[c], 1.0);
    vec3 q = s.xyz / s.w * 0.5 + 0.5;
    if (q.x <= 0.0 || q.x >= 1.0 || q.y <= 0.0 || q.y >= 1.0 || q.z >= 1.0) return vec3(1.0);

    float layer = float(c);
    float lit = 0.0;
    float taps = 0.0;
    for (int y = -2; y <= 2; y++)
    {
        for (int x = -2; x <= 2; x++)
        {
            if (abs(x) > uPcf || abs(y) > uPcf) continue;
            lit += texture(uShadowMap, vec4(q.xy + vec2(float(x), float(y)) * uShadowTexel, layer, q.z - 0.0002));
            taps += 1.0;
        }
    }
    lit /= max(taps, 1.0);

    vec3 t = uTransmitOn == 1 ? texture(uTransmit, vec3(q.xy, layer)).rgb : vec3(1.0);
    float fade = smoothstep(uShadowFar * 0.85, uShadowFar, viewDepth);
    return mix(lit * t, vec3(1.0), fade);
}

// ao: ambient occlusion (1 = open); it darkens the sky (ambient) term only, never direct sun
vec3 sunLight(vec3 n, vec3 world, vec3 eye, float ao)
{
    float ndl = max(dot(n, uSunDir), 0.0);
    vec3 vis = ndl > 0.0 ? sunVisibility(world, n, eye) : vec3(1.0);
    vis = mix(vec3(1.0), vis, uShadowStrength);
    float hemi = 0.5 + 0.5 * n.z;
    return uSkyColor * (0.75 + 0.5 * hemi) * ao + uSunColor * ndl * vis;
}
";

        /// <summary>
        /// Ambient occlusion lookup, shared by the scene and ground fragment shaders (inserted after SUN_GLSL).
        /// uAoMap holds (blurred AO, view depth) at half resolution; the 2×2 half-res texels around the pixel are
        /// weighted by bilinear position and by how close their depth is to this fragment's (a joint bilateral
        /// upsample), so AO never bleeds across silhouettes. uAoOn = 0 (off, plan view, glass) returns 1.
        /// </summary>
        public const string AO_GLSL = @"
uniform int uAoOn;
uniform sampler2D uAoMap;
uniform vec3 uAoForward;
uniform vec2 uAoScale;

float ambientOcclusion(vec3 world, vec3 eye)
{
    if (uAoOn == 0) return 1.0;
    float z = dot(world - eye, uAoForward);
    ivec2 size = textureSize(uAoMap, 0);
    vec2 p = gl_FragCoord.xy * uAoScale - 0.5;
    ivec2 i0 = ivec2(floor(p));
    vec2 f = p - vec2(i0);

    float sum = 0.0, weights = 0.0;
    float nearest = 1.0, nearestDiff = 1e9;
    for (int k = 0; k < 4; k++)
    {
        ivec2 o = ivec2(k & 1, k >> 1);
        vec2 s = texelFetch(uAoMap, clamp(i0 + o, ivec2(0), size - 1), 0).rg;
        float diff = abs(s.y - z);
        if (diff < nearestDiff) { nearestDiff = diff; nearest = s.x; }
        float bilinear = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
        float depth = max(0.0, 1.0 - diff / (0.02 * z + 0.03));
        float w = (bilinear + 1e-3) * depth;
        sum += s.x * w;
        weights += w;
    }
    return weights > 1e-4 ? sum / weights : nearest;
}
";

        /// <summary>
        /// Artificial lights, shared by the scene and ground fragment shaders (inserted after AO_GLSL). Up to 32 point
        /// lights picked on the CPU each frame (nearest to the player, in view), each with a cached omnidirectional
        /// shadow map (LightShadows: 6 layers per light in one depth array; the face is picked here from the major
        /// axis, with the same face table as LightShadows.FaceForward / FaceUp). Light fades to zero at its radius
        /// (windowed inverse square); its lobe mixes omnidirectional light with a downward cosine (downlights, panels).
        /// A small share of each light also fills what it can see evenly (a stand-in for light bouncing off the floor
        /// and walls), darkened by AO, so ceilings and faces turned away aren't black; a third of that fill reaches
        /// shadowed spots (under tables), about 2 % of the light through a wall.
        /// uLightPos: xyz position, w radius. uLightColor: rgb colour × intensity, w downward share (0–1), or (Round X-D1)
        /// below 0 a light profile: -(code + 1), code = slot (5 bits) · aim octahedral u, v (6 bits each) · turn (7 bits),
        /// as LightProfiles.Encode packs it; the profile tables are the LightProfiles block (binding 2).
        /// uLightShadow: x first layer (-1 = no shadow map), y fade.
        /// Keep the 32 in step with ArtificialLighting.MAX_LIGHTS and the constants with LightShadows.
        /// </summary>
        public const string LIGHTS_GLSL = @"
uniform int uLightCount;
uniform vec4 uLightPos[32];
uniform vec4 uLightColor[32];
uniform vec4 uLightShadow[32];
uniform sampler2DArrayShadow uLightShadowMap;
uniform float uLightShadowTexel;
uniform float uEmissive;
uniform int uShoulder;
// Round X-C1: all fixtures (photos, videos, stills; live when asked): the lights beyond the shadowed ones, unshadowed
uniform int uFarCount;
layout(std140) uniform FarLights
{
    vec4 uFarPos[480];
    vec4 uFarColor[480];
};

// Round X-D1: light profiles (IES files and built-ins): 24 tables of 32 gamma × 16 C bytes (4 to a uint, 16 to a
// uvec4: 32 uvec4 per table), and per table x = the scale to an even light of the same output
layout(std140) uniform LightProfiles
{
    uvec4 uProfileData[768];
    vec4 uProfileInfo[24];
};

float profileByte(int base, int index)
{
    uvec4 q = uProfileData[base + (index >> 4)];
    int w = (index >> 2) & 3;
    uint word = w == 0 ? q.x : (w == 1 ? q.y : (w == 2 ? q.z : q.w));
    return float((word >> uint((index & 3) * 8)) & 255u);
}

vec3 profileOctDecode(vec2 o)
{
    vec3 n = vec3(o, 1.0 - abs(o.x) - abs(o.y));
    if (n.z < 0.0) n.xy = (1.0 - abs(o.yx)) * vec2(o.x >= 0.0 ? 1.0 : -1.0, o.y >= 0.0 ? 1.0 : -1.0);
    return normalize(n);
}

// How strongly a profiled light shines towards v (unit, leaving the light), relative to an even light of the same output
float profileLobe(float encoded, vec3 v)
{
    int code = int(-encoded - 0.5);
    int slot = code >> 19;
    vec3 n = profileOctDecode(vec2(float((code >> 13) & 63), float((code >> 7) & 63)) / 63.0 * 2.0 - 1.0);
    vec3 h = abs(n.z) < 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 x0 = normalize(h - n * dot(h, n));
    vec3 y0 = cross(x0, n);
    float turn = float(code & 127) * (6.2831853 / 128.0);
    vec3 c0 = cos(turn) * x0 + sin(turn) * y0;
    vec3 c90 = cross(c0, n);
    float gv = acos(clamp(dot(v, n), -1.0, 1.0)) * (31.0 / 3.14159265);
    float ch = atan(dot(v, c90), dot(v, c0)) * (16.0 / 6.2831853);
    if (ch < 0.0) ch += 16.0;
    int v0 = min(int(gv), 30);
    float fv = clamp(gv - float(v0), 0.0, 1.0);
    int h0 = int(floor(ch)) & 15;
    int h1 = (h0 + 1) & 15;
    float fh = fract(ch);
    int base = slot * 32;
    int r0 = v0 * 16, r1 = r0 + 16;
    float top = mix(profileByte(base, r0 + h0), profileByte(base, r0 + h1), fh);
    float bottom = mix(profileByte(base, r1 + h0), profileByte(base, r1 + h1), fh);
    return mix(top, bottom, fv) * (1.0 / 255.0) * uProfileInfo[slot].x;
}

const float LIGHT_BOUNCE = 0.06;
const float LIGHT_NEAR = 0.08;
const float LIGHT_PAD = 1.03;
const vec3 FACE_F[6] = vec3[6](vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, -1.0));
const vec3 FACE_U[6] = vec3[6](vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0));

// 1 = the light reaches this point, 0 = something is in the way (4-tap PCF on the face the point falls in; Round Y-D2:
// oneTap > 0.5 → one tap, the hardware's 2 × 2 comparison only)
float lightVisibility(vec3 world, vec3 n, vec3 lp, float far, float layerBase, float oneTap)
{
    if (layerBase < 0.0) return 1.0;
    vec3 d = world - lp;
    d += n * (0.015 + 0.012 * length(d));
    vec3 a = abs(d);
    int face = (a.x >= a.y && a.x >= a.z) ? (d.x > 0.0 ? 0 : 1) : (a.y >= a.z ? (d.y > 0.0 ? 2 : 3) : (d.z > 0.0 ? 4 : 5));
    vec3 F = FACE_F[face];
    vec3 U = FACE_U[face];
    vec3 R = cross(F, U);
    float zf = max(dot(d, F), LIGHT_NEAR);
    vec2 uv = vec2(dot(d, R), dot(d, U)) / (zf * LIGHT_PAD) * 0.5 + 0.5;
    float ndc = (far + LIGHT_NEAR) / (far - LIGHT_NEAR) - 2.0 * far * LIGHT_NEAR / ((far - LIGHT_NEAR) * zf);
    float ref = ndc * 0.5 + 0.5 - 0.0004;
    float layer = layerBase + float(face);
    if (oneTap > 0.5) return texture(uLightShadowMap, vec4(uv, layer, ref));
    float t = uLightShadowTexel * 0.75;
    float lit = texture(uLightShadowMap, vec4(uv + vec2(-t, -t), layer, ref))
              + texture(uLightShadowMap, vec4(uv + vec2( t, -t), layer, ref))
              + texture(uLightShadowMap, vec4(uv + vec2(-t,  t), layer, ref))
              + texture(uLightShadowMap, vec4(uv + vec2( t,  t), layer, ref));
    return lit * 0.25;
}

vec3 artificialLight(vec3 world, vec3 n, float ao)
{
    vec3 sum = vec3(0.0);
    for (int i = 0; i < 32; i++)
    {
        if (i >= uLightCount) break;
        vec4 lp = uLightPos[i];
        vec3 d = lp.xyz - world;
        float dist2 = dot(d, d);
        float r2 = lp.w * lp.w;
        if (dist2 >= r2) continue;
        vec4 sh = uLightShadow[i];
        float visible = lightVisibility(world, n, lp.xyz, lp.w, sh.x, sh.z);
        float window = 1.0 - (dist2 * dist2) / (r2 * r2);
        window *= window * sh.y;
        vec4 lc = uLightColor[i];
        // Fill reaches shadowed spots a little (under tables), so they aren't black
        sum += lc.rgb * (LIGHT_BOUNCE * window * ao * mix(0.35, 1.0, visible));
        if (visible <= 0.0) continue;
        window *= visible;

        vec3 l = d * inversesqrt(max(dist2, 1e-8));
        float ndl = dot(n, l);
        if (ndl <= 0.0) continue;
        // l points from the surface to the light: l.z > 0 when the surface is below it (X-D1: or the light's profile)
        float lobe = lc.w >= 0.0 ? mix(1.0, 4.0 * max(l.z, 0.0), lc.w) : profileLobe(lc.w, -l);
        sum += lc.rgb * (lobe * window * ndl / (dist2 + 0.25));
    }
    // Round X-C1: the unshadowed far lights (same falloff and lobe; their fill counts as seen)
    for (int i = 0; i < 480; i++)
    {
        if (i >= uFarCount) break;
        vec4 lp = uFarPos[i];
        vec3 d = lp.xyz - world;
        float dist2 = dot(d, d);
        float r2 = lp.w * lp.w;
        if (dist2 >= r2) continue;
        float window = 1.0 - (dist2 * dist2) / (r2 * r2);
        window *= window;
        vec4 lc = uFarColor[i];
        sum += lc.rgb * (LIGHT_BOUNCE * window * ao);
        vec3 l = d * inversesqrt(max(dist2, 1e-8));
        float ndl = dot(n, l);
        if (ndl <= 0.0) continue;
        float lobe = lc.w >= 0.0 ? mix(1.0, 4.0 * max(l.z, 0.0), lc.w) : profileLobe(lc.w, -l);
        sum += lc.rgb * (lobe * window * ndl / (dist2 + 0.25));
    }
    return sum;
}

// Rolls values above 0.8 off smoothly towards 1 (pools of light and glows don't clip flat); below 0.8 unchanged
vec3 shoulder(vec3 c)
{
    if (uShoulder == 0) return c;
    vec3 over = max(c - 0.8, 0.0);
    return min(c, 0.8) + 0.2 * (1.0 - exp(-over / 0.2));
}
";

        public const string SCENE_FS = "#version 330 core\n" + CLIP_GLSL + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + CLOUD_GLSL + MATERIALS_GLSL + SSR_GLSL + PLANAR_GLSL + WEATHER_GLSL + @"
in vec3 vWorld;
in vec3 vNormal;
in vec4 vColor;
in vec3 vEmissive;
flat in int vMaterial;
in vec2 vUv;
uniform vec3 uEye;
uniform vec3 uLightDir;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform int uWhitecard;
uniform int uPlan;
uniform vec2 uClipZ;
uniform vec4 uOverride;
uniform float uGhost;
flat in vec4 vProfile;
uniform int uProfilePass;
uniform float uProfileDetail;
out vec4 oColor;
void main()
{
    // Texture gradients first: derivatives are only defined outside the per-material branches
    vec2 uvDx = dFdx(vUv);
    vec2 uvDy = dFdy(vUv);
    if (vWorld.z < uClipZ.x || vWorld.z > uClipZ.y) discard;
    if (sectionCut(vWorld)) discard;

    // Colour round A: the profile code. Pass 1 (the normal passes) leaves ghosted and (round B) see-through coloured
    // elements to pass 2 (the blended pass, which draws nothing else); the plan minimap draws them in place instead.
    // Hidden ones never draw.
    int profileCode = uProfilePass > 0 ? int(vProfile.a * 255.0 + 0.5) : 0;
    bool profileGhost = profileCode == 1;
    bool profileBlend = profileGhost || (profileCode >= 128 && profileCode < 255);
    if (profileCode == 2) discard;
    if (uProfilePass == 2 && !profileBlend) discard;
    if (uProfilePass == 1 && profileBlend && uPlan == 0) discard;

    vec4 base = vColor;
    float reflectivity = 0.0;
    vec4 shine = vec4(0.0, 1.0, 0.0, 0.0);
    if (uRealistic == 1 && uWhitecard == 0 && vMaterial >= 0 && vMaterial < 65535)
    {
        vec4 r = realisticColour(vMaterial, vUv, uvDx, uvDy);
        base.rgb = r.rgb;
        reflectivity = r.a;
        shine = reflectionInfo(vMaterial);
        // Grass round: water's body colour (rgb, a = how much) when one is picked
        if ((int(shine.z + 0.5) & 2) != 0 && uWaterTint.a > 0.0) base.rgb = mix(base.rgb, uWaterTint.rgb, uWaterTint.a);
        if (uReflectDebug == 1) base.rgb = reflectionDebugColour(shine, reflectivity);
        else if (uReflectDebug == 2) base.rgb = probeDebugColour(vWorld, gl_FrontFacing ? normalize(vNormal) : -normalize(vNormal));
    }
    if (uWhitecard == 1)
    {
        float l = dot(base.rgb, vec3(0.299, 0.587, 0.114));
        base.rgb = base.a < 0.98 ? vec3(0.76, 0.82, 0.87) : vec3(0.80 + 0.12 * l);
    }

    // Colour round A: the profile's colour over any colour mode, keeping uProfileDetail of the surface's own shade
    // (texture, material) and all of the lighting; glass stays see-through
    if (profileCode >= 128)
    {
        float l = dot(base.rgb, vec3(0.299, 0.587, 0.114));
        vec3 detailed = vProfile.rgb * clamp(l * 1.8, 0.25, 1.6);
        base.rgb = mix(vProfile.rgb, detailed, uProfileDetail);
    }
    else if (profileGhost && uPlan == 1) base.rgb = vec3(0.86, 0.87, 0.89);

    vec3 n = normalize(vNormal);

    if (uPlan == 1)
    {
        // Top-down plan: looking into a cut solid shows its inside (back faces) = poche
        if (!gl_FrontFacing) { oColor = vec4(0.90, 0.91, 0.93, 1.0); return; }
        float up = clamp(n.z, 0.0, 1.0);
        oColor = vec4(base.rgb * (0.30 + 0.22 * up), 1.0);
        return;
    }

    if (!gl_FrontFacing) n = -n;
    int shineFlags = int(shine.z + 0.5);
    float dist = length(vWorld - uEye);
    bool reflecting = uRealistic == 1 && uReflections == 1 && uReflectDebug == 0;
    if (reflecting && (shineFlags & 2) != 0 && n.z > 0.3 && uWaterWaves > 0.001) n = waterNormal(vWorld, n, shine.w, dist);

    // Round W-B: rain and snow on surfaces open to the sky (not water, highlights or colour-profile colours)
    float wet = 0.0, snow = 0.0;
    if (uWeather.x + uWeather.y > 0.001 && (shineFlags & 2) == 0 && uOverride.a <= 0.0 && profileCode < 128 && !profileGhost)
    {
        float open = skyOpen(vWorld, n);
        snow = snowAmount(vWorld, n, open);
        wet = wetAmount(n, open) * (1.0 - snow);
        base.rgb = mix(base.rgb * mix(1.0, 0.6, wet), SNOW_COLOUR, snow);
        base.a = mix(base.a, 1.0, snow);
        reflectivity *= 1.0 - snow;
        shine.x *= 1.0 - snow;
    }
    // Round X-C1: colour by SSR: blue where a mirrored (planar) view is drawn, green where the screen-space reflection
    // finds a surface (brighter = trusted more), magenta where it falls back to the probes or sky, grey where nothing
    // reflects
    if (uReflectDebug == 3 && uRealistic == 1 && uWhitecard == 0 && vMaterial >= 0 && vMaterial < 65535)
    {
        bool reflective = reflectivity > 0.0 || ((shineFlags & 2) != 0) || (reflectionTier(shine.x) > 0 && float(reflectionTier(shine.x)) * 0.25 >= uReflectThreshold - 0.01);
        if (!reflective) base.rgb = vec3(0.55);
        else
        {
            vec3 view = normalize(vWorld - uEye);
            vec3 planar;
            if (planarReflection(vWorld, n, planar)) base.rgb = vec3(0.2, 0.55, 1.0);
            else if (n.z <= 0.9) base.rgb = vec3(0.85, 0.2, 0.75);
            else
            {
                vec4 ssr = screenReflection(vWorld, n, reflect(view, n), reflectivity > 0.0 ? 0.0 : clamp(shine.y, 0.0, 1.0), uEye);
                base.rgb = mix(vec3(0.85, 0.2, 0.75), vec3(0.2, 0.95, 0.3), ssr.a);
            }
        }
    }

    float ao = ambientOcclusion(vWorld, uEye);
    vec3 lit;
    if (uSun == 1)
    {
        lit = base.rgb * sunLight(n, vWorld, uEye, ao);
    }
    else
    {
        float diffuse = max(dot(n, uLightDir), 0.0);
        float hemi = 0.5 + 0.5 * n.z;
        lit = base.rgb * ((0.40 + 0.20 * hemi) * ao + 0.45 * diffuse);
    }
    if (uLightCount + uFarCount > 0) { lit += base.rgb * artificialLight(vWorld, n, ao); }
    lit += vEmissive * uEmissive;

    // Reflections (Realistic mode, sky only until probes): Schlick's Fresnel, stronger at grazing angles
    float alpha = base.a;
    if (reflecting && (reflectivity > 0.0 || shine.x > 0.0))
    {
        vec3 view = normalize(vWorld - uEye);
        float cosine = clamp(dot(-view, n), 0.0, 1.0);
        float grazing = pow(1.0 - cosine, 5.0);
        vec3 dir = reflect(view, n);
        float fresnel = 0.0;
        if (reflectivity > 0.0)
        {
            // Glass: Revit's head-on value lifted so the sheen reads (2.5x, 10-50 %); the room's probe inside
            float r0 = clamp(reflectivity * 2.5, 0.10, 0.5) * uReflectGain;
            fresnel = min((r0 + (1.0 - r0) * grazing) * 0.85, 0.95);
            // Round X-C1: the mirrored view where this pane's plane was drawn this frame, else the room's probe or the sky
            vec3 env;
            if (!planarReflection(vWorld, n, env))
            {
                bool have;
                float weight;
                env = probeEnvironment(vWorld, n, dir, 0.0, have, weight);
                if (have) fresnel *= weight;
                else env = skyColour(dir);
            }
            lit = mix(lit, env, fresnel);
        }
        else
        {
            int tier = reflectionTier(shine.x);
            bool water = (shineFlags & 2) != 0;
            if (tier > 0 && (water || float(tier) * 0.25 >= uReflectThreshold - 0.01))
            {
                bool metal = (shineFlags & 1) != 0;
                float rough = clamp(shine.y, 0.0, 1.0);
                float r0 = tierStrength(tier) * (metal ? 1.0 : 0.5) * uReflectGain;
                // Rough surfaces lose most of the grazing-angle boost
                fresnel = min(r0 + (1.0 - r0) * grazing * (1.0 - rough) * 0.8, 0.95);
                // The probes where baked; else the sky, toned down where AO says the surface is enclosed
                // Round X-C1: a mirror's plane drawn this frame shows the mirrored view; else the probes where baked (or
                // the sky, toned down where AO says the surface is enclosed), sharpened on floors and water (surfaces
                // facing up) by what is on screen
                vec3 env;
                bool mirrored = !water && planarReflection(vWorld, n, env);
                if (mirrored)
                {
                    // A mirror reads as a mirror (silvered glass): strong head-on, untinted
                    fresnel = max(fresnel, min(0.85 * uReflectGain, 0.95));
                }
                else
                {
                    bool have;
                    float weight;
                    env = probeEnvironment(vWorld, n, dir, rough, have, weight);
                    if (!have) env = blurredSky(dir, rough) * mix(0.55, 1.0, ao);
                    else fresnel *= weight;
                    if (n.z > 0.9) // flat floors, tops and water only (sloped roofs stay with the probes)
                    {
                        vec4 ssr = screenReflection(vWorld, n, dir, rough, uEye);
                        env = mix(env, ssr.rgb, ssr.a);
                        if (have) fresnel = mix(fresnel, fresnel / max(weight, 0.4), ssr.a); // a real hit needn't fade like a close probe hit
                    }
                }
                // Metals: the reflection takes the metal's colour (kept fairly bright: chrome is near white)
                if (metal && !mirrored) env *= mix(base.rgb, vec3(1.0), 0.5);
                if (water) fresnel *= uWaterReflect; // grass round: water's reflection strength
                lit = mix(lit, env, fresnel);
                // Water: the sun glints on the ripples (water is usually outdoors, so no shadow lookup)
                if (water)
                {
                    vec3 sunDir = uSun == 1 ? uSunDir : normalize(uLightDir);
                    vec3 sunCol = uSun == 1 ? uSunColor : vec3(0.8);
                    lit += sunCol * (pow(max(dot(dir, sunDir), 0.0), 180.0) * 1.5);
                }
            }
        }
        alpha = alpha + (1.0 - alpha) * fresnel;
    }

    // Round W-B: wet surfaces take a sheen of the sky (or the room's probe); puddles on flat ground mirror it, the
    // rain rippling them
    if (wet > 0.01)
    {
        vec3 view = normalize(vWorld - uEye);
        float puddle = puddleAmount(vWorld, n, wet, dist);
        vec3 wn = n;
        if (puddle > 0.0 && dist < 40.0)
        {
            vec2 wobble = vec2(weatherNoise(vWorld.xy * 9.0 + uWeather.z * 3.0), weatherNoise(vWorld.yx * 9.0 - uWeather.z * 3.0)) - 0.5;
            wn = normalize(n + vec3(wobble * (0.06 * puddle * (1.0 - smoothstep(20.0, 40.0, dist))), 0.0));
        }
        float cosine = clamp(dot(-view, wn), 0.0, 1.0);
        float r0 = mix(0.03, 0.2, puddle);
        float sheen = min((r0 + (1.0 - r0) * pow(1.0 - cosine, 5.0) * mix(0.3, 0.85, puddle)) * mix(wet, 1.0, puddle), 0.85);
        vec3 dir = reflect(view, wn);
        float rough = mix(0.35, 0.05, puddle);
        bool have;
        float weight;
        vec3 env = probeEnvironment(vWorld, wn, dir, rough, have, weight);
        if (!have) env = blurredSky(dir, rough) * mix(0.55, 1.0, ao);
        else sheen *= weight;
        lit = mix(lit, env, sheen);
    }

    float fog = clamp(1.0 - exp(-dist * uFogDensity), 0.0, 0.65);
    lit = shoulder(mix(lit, uFogColor, fog));

    vec4 result = vec4(lit, alpha);
    if (uOverride.a > 0.0)
    {
        result.rgb = mix(result.rgb, uOverride.rgb, uOverride.a);
        result.a = max(result.a, 0.85);
    }
    // Clash view (Round C-B): everything but the pair as faint grey glass, keeping its shading (the profile's blended
    // pass: only its ghosts; its see-through colours keep their colour at the row's opacity)
    if (uGhost > 0.0 && (uProfilePass != 2 || profileGhost))
    {
        float grey = dot(result.rgb, vec3(0.299, 0.587, 0.114));
        result = vec4(mix(vec3(grey), vec3(0.62), 0.6), uGhost);
    }
    else if (uProfilePass == 2) result.a = min(result.a, float(profileCode - 128) / 126.0);
    oColor = result;
}";

        /// <summary>Shadow passes: positions through the light matrix (and the instance model), colour for glass.</summary>
        public const string SHADOW_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 2) in vec4 aColor;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec4 vColor;
void main()
{
    vColor = aColor;
    gl_Position = uViewProj * (uModel * vec4(aPos, 1.0));
}";

        /// <summary>Opaque casters: depth only (colour writes are masked off).</summary>
        public const string SHADOW_DEPTH_FS = @"#version 330 core
out vec4 oColor;
void main()
{
    oColor = vec4(1.0);
}";

        /// <summary>
        /// Glass: multiplies the light that gets through into the transmittance layer (blend ZERO, SRC_COLOR).
        /// Transmission = (1 - opacity) x a normalised tint of the material colour, scaled by the Glass slider.
        /// </summary>
        public const string SHADOW_TRANSMIT_FS = @"#version 330 core
in vec4 vColor;
uniform float uGlass;
uniform int uWhitecard;
out vec4 oColor;
void main()
{
    float a = clamp(vColor.a, 0.0, 1.0);
    float peak = max(vColor.r, max(vColor.g, vColor.b));
    vec3 tint = vColor.rgb / max(peak, 0.05);
    tint = uWhitecard == 1 ? vec3(1.0) : mix(vec3(1.0), tint, 0.65);
    oColor = vec4(clamp(tint * (1.0 - a) * uGlass, 0.0, 1.0), 1.0);
}";

        #endregion

        #region Sky and ground

        public const string FULLSCREEN_VS = @"#version 330 core
out vec2 vNdc;
void main()
{
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

        public const string SKY_FS = "#version 330 core\n" + CLOUD_GLSL + @"
in vec2 vNdc;
uniform mat4 uInvViewProj;
uniform vec3 uEye;
uniform int uSun;
uniform vec3 uSunDir;
uniform vec3 uZenith;
uniform vec3 uHorizon;
uniform vec3 uSunDisc;
uniform vec3 uSunShape;
// Round W-A: the night sky. uStars = (density, brightness, twinkle, Milky Way; density 0 = no stars); the celestial
// rows turn a sky direction into equatorial axes (the stars turn with the sidereal time); uNight 0 day – 1 night.
uniform vec3 uMoonDir;
uniform vec3 uMoonSun;  // towards the light on the moon: the sun, or a chosen phase's
uniform vec3 uMoonDisc;
uniform vec3 uMoonGlow;
uniform float uNight;
uniform vec4 uStars;
uniform float uTime;
uniform vec3 uCelestialX;
uniform vec3 uCelestialY;
uniform vec3 uCelestialZ;
// Round W-C: lightning. uLightning = (towards the strike, brightness 0–1); uBolt = (shape seed, 1 = a visible bolt,
// the lit zone's radius (rad), 0)
uniform vec4 uLightning;
uniform vec4 uBolt;
out vec4 oColor;

const float MOON_RADIUS = 0.0085;

// Round W-C: the bolt, in the zone's frame (units of the zone's radius; y up): a zigzag channel from inside the cloud
// down to the horizon and one fork. Returns the distance to the nearest segment.
float boltHash(float n)
{
    return fract(sin(n * 12.9898 + 4.1414) * 43758.5453);
}
float segmentDistance(vec2 p, vec2 a, vec2 b)
{
    vec2 ab = b - a;
    float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
    return length(p - a - ab * t);
}
vec2 boltPoint(int i, float top, float segStep, float seed)
{
    if (i <= 0) return vec2(0.0, top);
    float x = (boltHash(seed * 91.0 + float(i)) - 0.5) * 0.22 + (boltHash(seed * 37.0 + float(i) * 0.5) - 0.5) * 0.08 * float(i);
    return vec2(x, top - float(i) * segStep);
}
float boltDistance(vec2 q, float seed, float bottom)
{
    const int SEGMENTS = 12;
    float top = 0.45;
    float segStep = (top - bottom) / float(SEGMENTS);
    int i = int(clamp(floor((top - q.y) / segStep), 0.0, float(SEGMENTS - 1)));
    float d = 1e3;
    for (int k = -1; k <= 1; k++)
    {
        int j = clamp(i + k, 0, SEGMENTS - 1);
        d = min(d, segmentDistance(q, boltPoint(j, top, segStep, seed), boltPoint(j + 1, top, segStep, seed)));
    }
    // One fork from a third of the way down, leaning out to one side, five shorter segments
    int forkAt = 3 + int(boltHash(seed * 13.0) * 3.0);
    vec2 a = boltPoint(forkAt, top, segStep, seed);
    float side = boltHash(seed * 5.0) < 0.5 ? -1.0 : 1.0;
    for (int f = 0; f < 5; f++)
    {
        vec2 b = a + vec2(side * (0.05 + 0.06 * boltHash(seed * 17.0 + float(f))), -segStep * (0.8 + 0.4 * boltHash(seed * 23.0 + float(f))));
        d = min(d, segmentDistance(q, a, b) + 0.004 * float(f)); // thinner towards its tip
        a = b;
    }
    return d;
}
// The sky lit by a strike: the zone's clouds lit from inside (a faint wash on clear sky), the whole cloud deck a
// little, and the bolt (thin core, soft halo; the part inside the cloud dimmed)
vec3 lightningSky(vec3 dir, vec3 c, float cloudA)
{
    vec3 l = normalize(uLightning.xyz);
    float b = uLightning.w;
    float zone = max(uBolt.z, 0.05);
    vec3 right = normalize(cross(l, vec3(0.0, 0.0, 1.0)));
    vec3 up = cross(right, l);
    float facing = dot(dir, l);
    vec2 q = vec2(dot(dir, right), dot(dir, up)) / zone;
    // The lit patch: wider than tall (the cloud deck), broken up by the clouds' own billows
    float spread = facing > 0.0 ? exp(-(q.x * q.x * 0.55 + q.y * q.y * 1.4)) : 0.0;
    vec2 billow = dir.xy / (max(dir.z, 0.0) + 0.06) * 2.6 + uBolt.x * 17.0;
    float patches = smoothstep(0.25, 0.85, cloudFbm(billow));
    float glow = spread * (0.25 + 0.75 * patches);
    // Weaker against a bright day sky (the same flash reads far stronger at night)
    float dark = 1.0 - clamp(dot(c, vec3(0.3, 0.59, 0.11)), 0.0, 1.0);
    vec3 tint = vec3(0.78, 0.82, 1.0);
    c += tint * b * glow * (0.12 + 0.75 * cloudA) * (0.3 + 0.7 * dark);
    c += tint * b * 0.04 * cloudA * (0.3 + 0.7 * dark);
    if (uBolt.y > 0.5 && facing > 0.0 && abs(q.x) < 1.2 && dir.z > 0.0)
    {
        float bottom = -asin(clamp(l.z, 0.0, 1.0)) / zone;
        float d = boltDistance(q, uBolt.x, bottom);
        float px = max(length(fwidth(dir)) / zone, 1e-4);
        float core = 1.0 - smoothstep(px * 0.5, px * 1.6, d);
        float halo = exp(-d / max(px * 5.0, 0.004)) * 0.25;
        float inCloud = smoothstep(0.15, 0.4, q.y) * cloudA;
        c += vec3(0.86, 0.89, 1.0) * b * (core * 0.5 + halo) * (1.0 - 0.6 * inCloud) * (0.5 + 0.5 * dark);
    }
    return c;
}
const vec3 GALACTIC_POLE = vec3(-0.86766, -0.19807, 0.45599);
const vec3 GALACTIC_CENTRE = vec3(-0.05488, -0.87341, -0.48386);

float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}
vec3 hash33(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx);
}
float skyNoise3(vec3 p)
{
    vec3 i = floor(p), f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), u.x), mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), u.x), u.y),
               mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), u.x), mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), u.x), u.y), u.z);
}

// One layer of stars on the celestial sphere: cells of 1 / scale, at most one star in each (the 27 cells round the
// direction, so none is cut off); px = the angle a pixel covers (keeps them a pixel or two wide at any zoom)
vec3 starLayer(vec3 c, float scale, float chance, float bright, float px)
{
    vec3 p = c * scale;
    vec3 home = floor(p);
    vec3 sum = vec3(0.0);
    float radius = max(px * 0.75, 1e-5);
    for (int z = -1; z <= 1; z++)
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++)
    {
        vec3 cell = home + vec3(x, y, z);
        float h = hash13(cell + 11.7);
        if (h > chance) continue;
        vec3 r = hash33(cell);
        vec3 star = normalize(cell + 0.15 + 0.7 * r);
        float d = length(c - star);
        if (d > radius * 3.0) continue;
        float magnitude = bright * (0.08 + 0.92 * pow(r.x, 7.0));
        float twinkle = 1.0 - uStars.z * 0.45 * (0.5 + 0.5 * sin(uTime * (2.0 + 6.0 * r.y) + h * 61.0));
        vec3 tint = mix(vec3(0.72, 0.80, 1.00), vec3(1.00, 0.86, 0.68), r.z);
        sum += tint * magnitude * twinkle * exp(-d * d / (radius * radius));
    }
    return sum;
}

// The stars and the Milky Way in a direction (above the horizon, by night)
vec3 nightSky(vec3 dir, float px)
{
    vec3 c = vec3(dot(dir, uCelestialX), dot(dir, uCelestialY), dot(dir, uCelestialZ));
    float extinction = smoothstep(0.0, 0.18, dir.z);
    float wash = clamp(length(uMoonGlow) * 2.5, 0.0, 1.0); // a bright moon hides the faint stars
    float density = uStars.x;
    vec3 stars = starLayer(c, 40.0, 0.16 * density, 2.2, px) + starLayer(c, 95.0, 0.10 * density, 0.35 * (1.0 - 0.6 * wash), px);
    float g = dot(c, GALACTIC_POLE);
    float band = exp(-g * g / 0.014);
    float core = 0.55 + 0.9 * pow(max(dot(c, GALACTIC_CENTRE), 0.0), 3.0);
    float lane = 1.0 - 0.55 * smoothstep(0.45, 0.7, skyNoise3(c * 18.0)) * exp(-g * g / 0.0025);
    float milky = band * core * lane * (0.5 + 0.5 * skyNoise3(c * 7.0)) * uStars.w * (1.0 - 0.8 * wash);
    return (stars * uStars.y * (1.0 - 0.5 * wash) + vec3(0.50, 0.56, 0.72) * 0.11 * milky) * extinction * uNight;
}

void main()
{
    vec4 far = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 dir = normalize(far.xyz / far.w - uEye);
    float h = dir.z;
    vec3 zenith = uSun == 1 ? uZenith : vec3(0.34, 0.50, 0.70);
    vec3 horizon = uSun == 1 ? uHorizon : vec3(0.80, 0.85, 0.89);
    vec3 ground = uSun == 1 ? horizon * 0.55 : vec3(0.46, 0.48, 0.50);
    vec3 c = h >= 0.0
        ? mix(horizon, zenith, pow(clamp(h, 0.0, 1.0), 0.55))
        : mix(horizon * 0.88, ground, clamp(-h * 5.0, 0.0, 1.0));
    float line = 1.0 - smoothstep(0.0, 0.0035, abs(h));
    c = mix(c, horizon * 0.72, line * 0.7);
    // Sky round: uSunShape = (cos of the disc's outer edge, cos of its inner edge, glare multiplier)
    if (uSun == 1)
    {
        float d = max(dot(dir, uSunDir), 0.0);
        float glare = uSunShape.z * (1.0 - 0.75 * uCloudCover);
        c += uSunDisc * (smoothstep(uSunShape.x, uSunShape.y, d) + glare * (0.35 * pow(d, 64.0) + 0.15 * pow(d, 8.0)));

        // W-A: stars, the Milky Way, then the moon (its lit side faces the sun: the phase draws itself)
        if (h > 0.0)
        {
            float px = max(length(fwidth(dir)), 1e-5);
            if (uNight > 0.01 && uStars.x > 0.0) { c += nightSky(dir, px); }
            float m = dot(dir, uMoonDir);
            if (uMoonDisc.x > 0.0 && m > 0.99)
            {
                vec3 q = (dir - uMoonDir * m) / MOON_RADIUS;
                float q2 = dot(q, q);
                float edge = px / MOON_RADIUS;
                float inside = (1.0 - smoothstep(1.0 - edge, 1.0 + edge, sqrt(q2))) * step(0.0, m);
                vec3 n = q - uMoonDir * sqrt(max(1.0 - q2, 0.0));
                float lit = smoothstep(-0.06, 0.10, dot(n, uMoonSun));
                float maria = 0.86 + 0.14 * skyNoise3(n * 5.0 + 3.0) - 0.16 * smoothstep(0.5, 0.75, skyNoise3(n * 2.2 + 9.0));
                vec3 moon = uMoonDisc * (lit * maria + 0.03);
                c = c * (1.0 - inside * uNight * 0.75) + moon * inside;
            }
            // The moon's glow: fades smoothly to nothing (no edge), tight round the disc, a faint wider haze
            if (uMoonDisc.x > 0.0 && m > 0.0)
            {
                float angle = sqrt(max(2.0 * (1.0 - m), 0.0));
                c += uMoonGlow * (0.45 * exp(-angle / 0.012) + 0.05 * exp(-angle / 0.05));
            }
        }
    }
    vec4 cloud = cloudLayer(dir, uSunDir, uSun == 1 ? uSunDisc * 0.9 + uMoonGlow * 1.6 : vec3(0.6), horizon);
    c = mix(c, cloud.rgb, cloud.a);
    if (uLightning.w > 0.001 && h > -0.05) c = lightningSky(dir, c, cloud.a); // Round W-C
    oColor = vec4(c, 1.0);
}";

        public const string GROUND_VS = @"#version 330 core
uniform mat4 uViewProj;
uniform vec3 uCenter;
uniform float uHalf;
out vec3 vWorld;
const vec2 CORNERS[6] = vec2[6](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, -1.0), vec2(1.0, 1.0), vec2(-1.0, 1.0));
void main()
{
    vec3 w = vec3(uCenter.xy + CORNERS[gl_VertexID] * uHalf, uCenter.z);
    vWorld = w;
    gl_Position = uViewProj * vec4(w, 1.0);
}";

        public const string GROUND_FS = "#version 330 core\n" + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + WEATHER_GLSL + @"
in vec3 vWorld;
uniform vec3 uEye;
uniform vec3 uFogColor;
out vec4 oColor;
float gridLine(vec2 p, float spacing)
{
    vec2 q = p / spacing;
    vec2 g = abs(fract(q - 0.5) - 0.5) / max(fwidth(q), vec2(1e-4));
    return 1.0 - min(min(g.x, g.y), 1.0);
}
void main()
{
    float d = length(vWorld.xy - uEye.xy);
    float minor = gridLine(vWorld.xy, 1.0) * (1.0 - smoothstep(25.0, 90.0, d));
    float major = gridLine(vWorld.xy, 10.0) * (1.0 - smoothstep(150.0, 600.0, d));
    vec3 albedo = vec3(0.57, 0.59, 0.61) * (1.0 - 0.10 * minor - 0.20 * major);

    // Round W-B: wet or snowy where open to the sky (the grid lines show faintly through the snow)
    vec3 up = vec3(0.0, 0.0, 1.0);
    float wet = 0.0;
    if (uWeather.x + uWeather.y > 0.001)
    {
        float open = skyOpen(vWorld, up);
        float snow = snowAmount(vWorld, up, open);
        wet = wetAmount(up, open) * (1.0 - snow);
        albedo = mix(albedo * mix(1.0, 0.6, wet), SNOW_COLOUR * (1.0 - 0.03 * minor - 0.06 * major), snow);
    }
    vec3 c = albedo;
    float ao = ambientOcclusion(vWorld, uEye);
    if (uSun == 1) { c *= sunLight(up, vWorld, uEye, ao); }
    else { c *= mix(1.0, ao, 0.6); }
    if (uLightCount + uFarCount > 0) { c += albedo * artificialLight(vWorld, up, ao); }
    if (wet > 0.01)
    {
        // A sheen of the horizon, stronger at grazing angles and in the puddles
        float puddle = puddleAmount(vWorld, up, wet, length(uEye - vWorld));
        float cosine = clamp((uEye.z - vWorld.z) / max(length(uEye - vWorld), 1e-3), 0.0, 1.0);
        float r0 = mix(0.03, 0.2, puddle);
        float sheen = min((r0 + (1.0 - r0) * pow(1.0 - cosine, 5.0) * mix(0.3, 0.85, puddle)) * mix(wet, 1.0, puddle), 0.85);
        c = mix(c, uFogColor * mix(0.75, 1.0, ao), sheen);
    }
    c = mix(c, uFogColor, clamp(d / 1200.0, 0.0, 1.0));
    oColor = vec4(shoulder(c), 1.0);
}";

        #endregion

        #region Grass (grass round, build B)

        /// <summary>
        /// Grass blades: one instance per blade on a world-anchored grid around the eye (so blades stay put as you
        /// walk), placed from the grass map (RG32F: height, palette index + 1; 0 = no grass). Seven vertices per blade
        /// (a three-segment tapering strip, bent by its lean and the wind). Blades with no grass under them, behind the
        /// eye or past the distance collapse to nothing. A second, sparser grid with wider blades covers the far ring.
        /// </summary>
        public const string GRASS_VS = @"#version 330 core
uniform mat4 uViewProj;
uniform vec3 uEye;
uniform sampler2D uGrassMap;
uniform vec3 uGrassArea;
uniform vec2 uGrassSize;
uniform vec3 uGrassPalette[16];
uniform float uSpacing;
uniform int uSide;
uniform float uInner;
uniform float uFadeIn;
uniform float uFadeOut;
uniform float uRadius;
uniform float uBladeHeight;
uniform float uBladeWidth;
uniform float uSway;
uniform float uTime;
uniform float uSnowShrink;
out vec3 vWorld;
out vec3 vNormal;
out vec3 vColour;
out float vT;
float bladeHash(vec2 p, float k)
{
    p = fract(p * vec2(0.1031, 0.1030) + k * 0.0973);
    p += dot(p, p.yx + 33.33);
    return fract((p.x + p.y) * p.x);
}
void collapse()
{
    gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    vWorld = vec3(0.0);
    vNormal = vec3(0.0, 0.0, 1.0);
    vColour = vec3(0.0);
    vT = 0.0;
}
void main()
{
    // Nine vertices per blade (the first and last repeated: degenerate joins in one strip), a clump per instance
    int blade = gl_VertexID / 9;
    int v = clamp(gl_VertexID - blade * 9 - 1, 0, 6);
    float k = float(blade) * 7.13;

    // The clump's grid cell (world-anchored), each blade jittered inside it
    int ix = gl_InstanceID % uSide;
    int iy = gl_InstanceID / uSide;
    vec2 cell = floor(uEye.xy / uSpacing) + vec2(float(ix - uSide / 2), float(iy - uSide / 2));
    float h0 = bladeHash(cell, 1.0 + k), h1 = bladeHash(cell, 2.0 + k), h2 = bladeHash(cell, 3.0 + k), h3 = bladeHash(cell, 4.0 + k);
    vec2 root = (cell + vec2(h0, h1)) * uSpacing;
    float d = length(root - uEye.xy);
    if (d < uInner || d > uRadius) { collapse(); return; }

    // The grass map: nothing here, or the height and colour
    vec2 g = (root - uGrassArea.xy) / uGrassArea.z;
    ivec2 t = ivec2(floor(g));
    if (t.x < 0 || t.y < 0 || t.x >= int(uGrassSize.x) || t.y >= int(uGrassSize.y)) { collapse(); return; }
    vec2 texel = texelFetch(uGrassMap, t, 0).rg;
    if (texel.g < 0.5) { collapse(); return; }

    // Behind the eye or well off screen (a margin for tall blades): skip
    vec4 clipRoot = uViewProj * vec4(root, texel.r, 1.0);
    if (clipRoot.w < -0.5 || abs(clipRoot.x) > clipRoot.w * 1.3 + 1.0 || abs(clipRoot.y) > clipRoot.w * 1.3 + 1.0) { collapse(); return; }

    // Shape: grows in over the inner band and shrinks over the outer one (the two grids cross-fade; the far edge
    // blends into the textured ground), varied per blade
    float fade = smoothstep(uInner, uFadeIn, d) * (1.0 - smoothstep(uFadeOut, uRadius, d));
    if (uFadeIn <= uInner) fade = 1.0 - smoothstep(uFadeOut, uRadius, d);
    float height = uBladeHeight * (0.55 + 0.9 * h2 * h2) * fade * (1.0 - uSnowShrink); // W-B: snow buries the blades
    if (height < 0.004) { collapse(); return; }
    float width = uBladeWidth * (0.7 + 0.6 * h3);
    float yaw = h0 * 6.2831853;
    vec3 right = vec3(cos(yaw), sin(yaw), 0.0);
    vec3 facing = vec3(-right.y, right.x, 0.0);
    float lean = (h1 - 0.5) * 0.9;
    float sway = (sin(uTime * 1.7 + root.x * 0.35 + root.y * 0.22) * 0.16 + sin(uTime * 3.1 + h2 * 6.28) * 0.05) * uSway;
    vec3 bend = facing * lean + vec3(0.71, 0.71, 0.0) * sway;

    // Vertex: two per level (0, 0.35, 0.7), then the tip; a curved taper
    float level = v >= 6 ? 1.0 : float(v / 2) * 0.35;
    float side = v >= 6 ? 0.0 : (v % 2 == 0 ? -0.5 : 0.5);
    vec3 world = vec3(root, texel.r)
        + right * (side * width * (1.0 - level * level * 0.8))
        + vec3(0.0, 0.0, height * level)
        + bend * (height * level * level);
    vWorld = world;
    vec3 along = normalize(vec3(0.0, 0.0, 1.0) + bend * (2.0 * level));
    vNormal = normalize(mix(normalize(cross(right, along)), vec3(0.0, 0.0, 1.0), 0.55));
    int palette = clamp(int(texel.g + 0.5) - 1, 0, 15);
    vec3 base = uGrassPalette[palette] * (0.8 + 0.4 * h3);
    vColour = base * vec3(1.0 + 0.16 * (h1 - 0.5), 1.0 + 0.06 * (h0 - 0.5), 1.0 - 0.15 * h0);
    vT = level;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

        /// <summary>
        /// Grass blades' colours: darker at the root, the sun (with shadows), a little light through the blade when
        /// looking towards the sun, the artificial lights, fog; both sides lit alike.
        /// </summary>
        public const string GRASS_FS = "#version 330 core\n" + CLIP_GLSL + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + WEATHER_GLSL + @"
in vec3 vWorld;
in vec3 vNormal;
in vec3 vColour;
in float vT;
uniform vec3 uEye;
uniform vec3 uFogColor;
uniform float uFogDensity;
out vec4 oColor;
void main()
{
    if (sectionCut(vWorld)) discard;
    vec3 n = normalize(vNormal);
    float ao = mix(0.35, 1.0, vT);
    // Darker at the root, lighter and a little yellower at the tip
    vec3 albedo = mix(vColour * 0.55, vColour * 1.15 + vec3(0.05, 0.05, -0.01), vT);
    // Round W-B: darker when wet, snow on the blades (more towards the tips) where open to the sky
    if (uWeather.x + uWeather.y > 0.001)
    {
        vec3 up = vec3(0.0, 0.0, 1.0);
        float open = skyOpen(vWorld, up);
        float snow = snowAmount(vWorld, up, open);
        albedo = mix(albedo * mix(1.0, 0.75, wetAmount(up, open)), SNOW_COLOUR, snow * (0.45 + 0.55 * vT));
    }
    vec3 c;
    if (uSun == 1)
    {
        c = albedo * sunLight(n, vWorld, uEye, ao);
        vec3 view = normalize(vWorld - uEye);
        c += albedo * uSunColor * (0.35 * pow(max(dot(view, uSunDir), 0.0), 3.0) * vT);
    }
    else
    {
        c = albedo * (0.45 * ao + 0.45 * max(n.z, 0.0));
    }
    if (uLightCount + uFarCount > 0) c += albedo * artificialLight(vWorld, n, ao);
    float dist = length(vWorld - uEye);
    float fog = clamp(1.0 - exp(-dist * uFogDensity), 0.0, 0.65);
    oColor = vec4(shoulder(mix(c, uFogColor, fog)), 1.0);
}";

        #endregion

        #region Billboards (Round X-B: entourage)

        /// <summary>
        /// Round X-B: billboards (trees, people, cars…): one instanced quad each, standing on its base and turned about
        /// the vertical to face the eye (or, for the shadow maps, the sun: uFaceSun = 1 with uFaceDir the horizontal
        /// direction to the sun, so the silhouette casts its full shadow). Per instance: aPos = base xyz, height; aShape =
        /// width, flip (±1), picture layer, textured (1) or tinted (0); aTint = colour, 1 / mean luminance; aUv = the
        /// picture's content box (u0, v0 top, u1, v1 bottom; the layers are uploaded top row first).
        /// </summary>
        public const string BILLBOARD_VS = @"#version 330 core
layout(location = 0) in vec4 aPos;
layout(location = 1) in vec4 aShape;
layout(location = 2) in vec4 aTint;
layout(location = 3) in vec4 aUv;
uniform mat4 uViewProj;
uniform vec3 uEye;
uniform int uFaceSun;
uniform vec3 uFaceDir;
out vec3 vWorld;
out vec3 vUv;
out vec3 vNormal;
out float vT;
flat out vec4 vTint;
flat out float vTextured;
flat out float vPush;
void main()
{
    int v = gl_VertexID;
    float sx = (v == 1 || v == 3) ? 1.0 : 0.0;
    float sy = v >= 2 ? 1.0 : 0.0;
    vec3 base = aPos.xyz;
    vec3 toward = uFaceSun == 1 ? uFaceDir : uEye - base;
    toward.z = 0.0;
    toward = dot(toward, toward) > 1e-8 ? normalize(toward) : vec3(0.0, -1.0, 0.0);
    vec3 right = vec3(-toward.y, toward.x, 0.0);
    float w = aShape.x;
    vec3 world = base + right * ((sx - 0.5) * w) + vec3(0.0, 0.0, sy * aPos.w);
    float u = aShape.y < 0.0 ? 1.0 - sx : sx;
    vUv = vec3(mix(aUv.x, aUv.z, u), mix(aUv.w, aUv.y, sy), aShape.z);
    vWorld = world;
    vNormal = toward;
    vT = sy;
    vTint = aTint;
    vTextured = aShape.w;
    vPush = 0.5 * w + 0.05;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

        /// <summary>
        /// Round X-B: a billboard's colour: the picture's own (textured) or the category colour shaded by the picture's
        /// luminance (tinted); alpha-tested at 0.5. Lit like foliage: the sky (more towards the top), the sun wrapped
        /// round (light gets through leaves), its shadows looked up a little towards the sun (past the billboard's own
        /// sun-facing caster), the artificial lights, fog.
        /// </summary>
        public const string BILLBOARD_FS = "#version 330 core\n" + CLIP_GLSL + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + @"
in vec3 vWorld;
in vec3 vUv;
in vec3 vNormal;
in float vT;
flat in vec4 vTint;
flat in float vTextured;
flat in float vPush;
uniform sampler2DArray uBillboards;
uniform vec3 uEye;
uniform vec3 uFogColor;
uniform float uFogDensity;
out vec4 oColor;
void main()
{
    if (sectionCut(vWorld)) discard;
    vec4 t = texture(uBillboards, vUv);
    if (t.a < 0.5) discard;
    vec3 albedo;
    if (vTextured > 0.5)
    {
        albedo = t.rgb;
    }
    else
    {
        float lum = dot(t.rgb, vec3(0.299, 0.587, 0.114)) * vTint.a;
        albedo = vTint.rgb * clamp(mix(1.0, lum, 0.7), 0.45, 1.5);
    }
    vec3 n = normalize(vNormal);
    vec3 c;
    if (uSun == 1)
    {
        vec3 level = vec3(uSunDir.x, uSunDir.y, 0.0);
        level = dot(level, level) > 1e-8 ? normalize(level) : vec3(0.0);
        vec3 vis = sunVisibility(vWorld + level * vPush, uSunDir, uEye);
        vis = mix(vec3(1.0), vis, uShadowStrength * 0.85); // a little light gets through leaves and past edges
        float wrap = 0.6 + 0.4 * max(dot(n, uSunDir), 0.0);
        c = albedo * (uSkyColor * (0.8 + 0.35 * vT) + uSunColor * (0.8 * wrap) * vis);
    }
    else
    {
        c = albedo * (0.62 + 0.3 * vT);
    }
    if (uLightCount + uFarCount > 0) c += albedo * artificialLight(vWorld, n, 1.0);
    float dist = length(vWorld - uEye);
    float fog = clamp(1.0 - exp(-dist * uFogDensity), 0.0, 0.65);
    oColor = vec4(shoulder(mix(c, uFogColor, fog)), 1.0);
}";

        /// <summary>Round X-B: a billboard in the sun's shadow maps: depth only, cut out by the picture's alpha.</summary>
        public const string BILLBOARD_SHADOW_FS = @"#version 330 core
in vec3 vUv;
uniform sampler2DArray uBillboards;
out vec4 oColor;
void main()
{
    if (texture(uBillboards, vUv).a < 0.5) discard;
    oColor = vec4(1.0);
}";

        #endregion

        #region Weather (Round W-B: rain and snow)

        /// <summary>
        /// Round W-B: the weather block shared by the scene, ground, grass and falling rain / snow shaders. uCoverMap
        /// (R32F, nearest) holds the highest surface over each cell of the site (CoverMap); uCoverArea = (min x, min y,
        /// cell, 1 = a map), uCoverSize = cells. uWeather = (wet 0–1, snow 0–1, clock (s, puddle ripples), 0); all zero = no weather (one
        /// uniform branch). A surface is open to the sky when nothing in its cell (looked up a cell out along its
        /// normal, so wall faces read the cell outside the wall) is more than a slope-scaled tolerance above it.
        /// </summary>
        public const string WEATHER_GLSL = @"
uniform sampler2D uCoverMap;
uniform vec4 uCoverArea;
uniform vec2 uCoverSize;
uniform vec4 uWeather;
const vec3 SNOW_COLOUR = vec3(0.90, 0.92, 0.95);
float weatherHash(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}
float weatherNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(weatherHash(i), weatherHash(i + vec2(1.0, 0.0)), u.x),
               mix(weatherHash(i + vec2(0.0, 1.0)), weatherHash(i + vec2(1.0, 1.0)), u.x), u.y);
}
// The highest surface over a point, or -1e9 off the map / without one
float coverTop(vec2 p)
{
    if (uCoverArea.w < 0.5) return -1e9;
    ivec2 c = ivec2(floor((p - uCoverArea.xy) / uCoverArea.z));
    if (c.x < 0 || c.y < 0 || c.x >= int(uCoverSize.x) || c.y >= int(uCoverSize.y)) return -1e9;
    return texelFetch(uCoverMap, c, 0).r;
}
// 1 = open to the sky, 0 = under cover
float skyOpen(vec3 world, vec3 n)
{
    if (uCoverArea.w < 0.5) return 1.0;
    float cell = uCoverArea.z;
    float top = coverTop(world.xy + n.xy * cell);
    float slope = length(n.xy) / max(abs(n.z), 0.2);
    float tolerance = 0.06 + cell * min(slope, 2.0);
    return 1.0 - smoothstep(tolerance, tolerance + 0.15, top - world.z);
}
// How much snow lies on a surface (0–1): flat open faces first, patchy while the cover is light, slopes last
float snowAmount(vec3 world, vec3 n, float open)
{
    if (uWeather.y <= 0.001 || n.z < 0.2 || open <= 0.0) return 0.0;
    float up = smoothstep(0.3, 0.9, n.z);
    vec2 p = world.xy;
    vec2 q = vec2(p.x * 0.8 - p.y * 0.6, p.x * 0.6 + p.y * 0.8);
    float drift = weatherNoise(p * 0.45) * 0.45 + weatherNoise(q * 1.7 + 11.0) * 0.35 + weatherNoise(p * 5.3 + 3.0) * 0.2;
    float need = drift * 0.55 + (1.0 - up) * 0.6;
    return smoothstep(need - 0.1, need + 0.1, uWeather.y * 1.2) * open;
}
// How wet a surface is (0–1): open faces, walls less than floors and roofs
float wetAmount(vec3 n, float open)
{
    return uWeather.x * open * mix(0.35, 1.0, smoothstep(0.0, 0.7, n.z));
}
// Puddles on flat wet ground (0–1); the fine edge detail fades out with distance (no shimmer far off)
float puddleAmount(vec3 world, vec3 n, float wet, float dist)
{
    float fine = mix(weatherNoise(world.xy * 2.1), 0.5, smoothstep(15.0, 45.0, dist));
    return wet * smoothstep(0.93, 0.99, n.z) * smoothstep(0.56, 0.76, weatherNoise(world.xy * 0.45) * 0.75 + fine * 0.25);
}
";

        /// <summary>
        /// Round W-B: falling rain and snow. One instance per drop / flake, in a box round the eye: each has a fixed
        /// place in an endlessly repeating world-anchored pattern moving with the fall velocity, wrapped into the box
        /// (so drops stay put as you walk). Rain: a thin streak along its velocity, turned to face the eye; snow: a
        /// round flake that flutters. Fades towards the box's sides and right at the eye.
        /// </summary>
        public const string PRECIP_VS = @"#version 330 core
uniform mat4 uViewProj;
uniform vec3 uEye;
uniform vec3 uBox;
uniform vec3 uFall;
uniform float uTime;
uniform int uSnow;
uniform vec2 uSize;
uniform vec3 uRight;
uniform vec3 uUp;
out vec3 vWorld;
out vec2 vQuad;
out float vFade;
uint dropHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}
float dropRandom(uint id, uint k)
{
    return float(dropHash(id * 5u + k) & 0xFFFFFFu) / 16777216.0;
}
void main()
{
    uint id = uint(gl_InstanceID);
    vec3 seed = vec3(dropRandom(id, 0u), dropRandom(id, 1u), dropRandom(id, 2u));
    float speed = 0.85 + 0.3 * dropRandom(id, 3u);
    float size = 0.7 + 0.6 * dropRandom(id, 4u);
    vec3 velocity = uFall * speed;
    vec3 p = seed * uBox + velocity * uTime;
    if (uSnow == 1)
    {
        float phase = seed.x * 40.0 + seed.y * 17.0;
        p.xy += vec2(sin(uTime * 1.3 + phase), cos(uTime * 1.1 + phase * 1.7)) * 0.3;
    }
    vec3 low = uEye - uBox * 0.5;
    p = low + mod(p - low, uBox);

    vec3 offset = p - uEye;
    float d = length(offset);
    vec3 edge = abs(offset) / (uBox * 0.5);
    vFade = (1.0 - smoothstep(0.65, 1.0, max(edge.x, edge.y))) * (1.0 - smoothstep(0.75, 1.0, edge.z))
          * smoothstep(0.35, 1.2, d);

    int corner = gl_VertexID;
    vec2 q = vec2(corner == 1 || corner == 3 ? 1.0 : -1.0, corner >= 2 ? 1.0 : 0.0);
    vec3 world;
    if (uSnow == 1)
    {
        vec2 c = vec2(q.x, q.y * 2.0 - 1.0);
        float r = uSize.x * size;
        world = p + (uRight * c.x + uUp * c.y) * r;
        vQuad = c;
    }
    else
    {
        vec3 axis = normalize(uFall);
        vec3 side = cross(axis, normalize(uEye - p));
        side = length(side) > 1e-4 ? normalize(side) : uRight;
        float width = uSize.x * (1.0 + d * 0.06);
        world = p + side * (q.x * width * 0.5) - axis * (q.y * uSize.y * size);
        vQuad = q;
    }
    vWorld = world;
    gl_Position = uViewProj * vec4(world, 1.0);
}";

        /// <summary>
        /// Round W-B: a drop or flake's colour: nothing under the cover map's surfaces (indoors, under roofs and canopies)
        /// or below the ground; a soft streak / disc of uColour at uAlpha.
        /// </summary>
        public const string PRECIP_FS = "#version 330 core\n" + WEATHER_GLSL + @"
in vec3 vWorld;
in vec2 vQuad;
in float vFade;
uniform int uSnow;
uniform vec3 uColour;
uniform float uAlpha;
uniform float uGround;
out vec4 oColor;
void main()
{
    if (vFade <= 0.003 || vWorld.z < uGround) discard;
    if (vWorld.z < coverTop(vWorld.xy)) discard;
    float a;
    if (uSnow == 1) a = 1.0 - smoothstep(0.3, 1.0, length(vQuad));
    else a = (1.0 - abs(vQuad.x)) * smoothstep(0.0, 0.3, vQuad.y) * (1.0 - smoothstep(0.75, 1.0, vQuad.y));
    a *= uAlpha * vFade;
    if (a <= 0.003) discard;
    oColor = vec4(uColour, a);
}";

        /// <summary>
        /// Round W-C: drops (and melting snowflakes) on the lens. One instance per drop: uDrops[i] = (centre u, v (0–1),
        /// radius (a fraction of the frame's height), opacity); uDropShapes[i] = (shape seed, 1 = a snowflake, how far it
        /// has melted 0–1, trail length in radii). The quad covers the drop and the trail above it.
        /// </summary>
        public const string LENS_VS = @"#version 330 core
uniform vec4 uDrops[32];
uniform vec4 uDropShapes[32];
uniform float uAspect;
out vec2 vLocal;
out vec2 vUv;
flat out vec4 vDrop;
flat out vec4 vShape;
void main()
{
    vec4 d = uDrops[gl_InstanceID];
    vec4 s = uDropShapes[gl_InstanceID];
    int corner = gl_VertexID;
    vec2 q = vec2(corner == 1 || corner == 3 ? 1.3 : -1.3, corner >= 2 ? 1.3 + s.w : -1.3);
    vec2 uv = d.xy + q * vec2(d.z / uAspect, d.z);
    vLocal = q;
    vUv = uv;
    vDrop = d;
    vShape = s;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
}";

        /// <summary>
        /// Round W-C: a lens drop: a lumpy round lens (a thinner trail above a drop that has slid) showing the scene
        /// behind it upside down, magnified and soft, a darker rim and a small highlight. A snowflake starts as a frosted
        /// six-armed crystal and melts into a drop.
        /// </summary>
        public const string LENS_FS = @"#version 330 core
in vec2 vLocal;
in vec2 vUv;
flat in vec4 vDrop;
flat in vec4 vShape;
uniform sampler2D uScene;
uniform float uAspect;
out vec4 oColor;
void main()
{
    float seed = vShape.x * 40.0;
    vec2 p = vLocal;
    float angle = atan(p.y, p.x);
    float melt = vShape.y > 0.5 ? clamp(vShape.z, 0.0, 1.0) : 1.0;
    float radius = 1.0 + 0.07 * sin(angle * 3.0 + seed) + 0.04 * sin(angle * 5.0 + seed * 1.7);
    radius *= vShape.y > 0.5 ? mix(1.0, 0.7, melt) : 1.0;
    // The trail: a capsule up from the centre, narrowing
    float trail = vShape.w;
    float along = clamp(p.y, 0.0, trail);
    float taper = trail > 0.0 ? 1.0 - 0.6 * along / max(trail, 1e-3) : 1.0;
    float d = length(vec2(p.x, p.y - along)) / taper;
    float inside = 1.0 - smoothstep(radius - 0.12, radius, d);
    if (inside <= 0.002) discard;

    // Refraction: the view behind, flipped through the drop's centre and magnified (softened by a few taps)
    float r = vDrop.z;
    vec2 flipped = vDrop.xy - vec2(p.x, min(p.y, 1.0)) * vec2(r / uAspect, r) * 1.25;
    vec2 texel = vec2(r / uAspect, r) * 0.18;
    vec3 c = texture(uScene, flipped).rgb * 0.4
           + texture(uScene, flipped + vec2(texel.x, 0.0)).rgb * 0.15 + texture(uScene, flipped - vec2(texel.x, 0.0)).rgb * 0.15
           + texture(uScene, flipped + vec2(0.0, texel.y)).rgb * 0.15 + texture(uScene, flipped - vec2(0.0, texel.y)).rgb * 0.15;
    float edge = clamp(d / radius, 0.0, 1.0);
    float h = sqrt(max(1.0 - edge * edge, 0.0));
    c *= mix(1.04, 0.62, smoothstep(0.55, 1.0, edge));
    vec3 n = normalize(vec3(p / max(radius, 0.1) * 0.9, h + 0.15));
    float spec = pow(max(dot(n, normalize(vec3(-0.35, 0.55, 1.0))), 0.0), 40.0);
    c += vec3(0.9) * spec * 0.6;

    // A snowflake: frosted and white with six arms, giving way to the drop as it melts
    if (vShape.y > 0.5)
    {
        float arms = pow(abs(cos(angle * 3.0 + seed)), 6.0);
        float frost = (0.55 + 0.45 * arms) * (1.0 - smoothstep(0.0, 0.7, melt));
        c = mix(c, vec3(0.93, 0.95, 0.98), frost);
    }
    oColor = vec4(c, inside * vDrop.w);
}";

        #endregion

        #region Ambient occlusion

        /// <summary>
        /// Geometry pre-pass outputs. 0: the view-space normal (right, up, forward), turned to face the eye, and the
        /// view depth (metres along the camera's forward axis); cleared to 0 = nothing there (sky). 1: glow (emissive
        /// colour × uGlow, already hidden behind whatever is in front), the bloom's source; only bound when glow is on.
        /// </summary>
        private const string GEOMETRY_GLSL = "#version 330 core\n" + CLIP_GLSL + @"
uniform vec3 uEye;
uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uForward;
uniform float uGlow;
layout(location = 0) out vec4 oGeometry;
layout(location = 1) out vec4 oGlow;
void writeGeometry(vec3 world, vec3 n)
{
    vec3 d = world - uEye;
    n = normalize(n);
    if (dot(n, d) > 0.0) n = -n;
    oGeometry = vec4(dot(n, uRight), dot(n, uUp), dot(n, uForward), dot(d, uForward));
    oGlow = vec4(0.0);
}
";

        /// <summary>
        /// Geometry pre-pass for scene batches (vertex shader: <see cref="SCENE_VS"/>). Colour round A: ghosted and hidden
        /// profile elements are left out (they don't occlude what is behind them).
        /// </summary>
        public const string GEOMETRY_FS = GEOMETRY_GLSL + @"
in vec3 vWorld;
in vec3 vNormal;
in vec3 vEmissive;
flat in vec4 vProfile;
uniform int uProfilePass;
void main()
{
    if (sectionCut(vWorld)) discard;
    if (uProfilePass > 0)
    {
        int code = int(vProfile.a * 255.0 + 0.5);
        if (code == 1 || code == 2 || (code >= 128 && code < 255)) discard;
    }
    writeGeometry(vWorld, vNormal);
    oGlow = vec4(vEmissive * uGlow, 1.0);
}";

        /// <summary>
        /// Round Y-D2: the main view's depth pre-pass (vertex shader: <see cref="SCENE_VS"/>, so depths match the scene
        /// pass exactly). Drops exactly what the scene pass's opaque draw drops: section cuts, hidden profile elements and
        /// (pass 1) ghosted / see-through coloured ones; colour writes are masked off.
        /// </summary>
        public const string DEPTH_PREPASS_FS = "#version 330 core\n" + CLIP_GLSL + @"
in vec3 vWorld;
flat in vec4 vProfile;
uniform int uProfilePass;
out vec4 oColor;
void main()
{
    if (sectionCut(vWorld)) discard;
    if (uProfilePass > 0)
    {
        int code = int(vProfile.a * 255.0 + 0.5);
        if (code == 1 || code == 2 || (code >= 128 && code < 255)) discard;
    }
    oColor = vec4(1.0);
}";

        /// <summary>Geometry pre-pass for the ground plane (vertex shader: <see cref="GROUND_VS"/>).</summary>
        public const string GEOMETRY_GROUND_FS = GEOMETRY_GLSL + @"
in vec3 vWorld;
void main()
{
    writeGeometry(vWorld, vec3(0.0, 0.0, 1.0));
}";

        /// <summary>
        /// Screen-space ambient obscurance at half resolution (the spiral sampling of McGuire et al.'s SAO with a
        /// bounded per-tap estimator). Taps spiral out from the pixel over a world-space radius; each tap's view
        /// position is rebuilt from its depth and counts by the cosine of its direction above the surface's tangent
        /// plane, fading to nothing at the radius, so each tap adds 0..1 and uIntensity reads directly. A 4×4 ordered
        /// rotation per pixel turns banding into fine noise that the 9-tap blur removes.
        /// Output: (AO, view depth) for the blur and the upsample.
        /// </summary>
        public const string AO_FS = @"#version 330 core
uniform sampler2D uGeometry;
uniform vec2 uTan;
uniform float uProjScale;
uniform float uRadius;
uniform float uIntensity;
uniform float uMaxDepth;
out vec2 oAo;

const int SAMPLES = 12;
const float TURNS = 7.0;
const float TAU = 6.2831853;
const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

vec3 viewPosition(ivec2 px, float z, vec2 size)
{
    vec2 ndc = (vec2(px) + 0.5) / size * 2.0 - 1.0;
    return vec3(ndc * uTan * z, z);
}

void main()
{
    ivec2 size = textureSize(uGeometry, 0);
    vec2 fsize = vec2(size);
    ivec2 px = ivec2(gl_FragCoord.xy);
    vec4 g = texelFetch(uGeometry, px, 0);
    float z = g.w;
    if (z <= 0.0 || z > uMaxDepth) { oAo = vec2(1.0, z); return; }

    vec3 n = g.xyz;
    vec3 p = viewPosition(px, z, fsize);

    // Screen radius of the world radius here (capped so close-up walls don't thrash the cache)
    float radiusPx = min(uRadius * uProjScale / z, fsize.y * 0.12);
    if (radiusPx < 1.5) { oAo = vec2(1.0, z); return; }
    float radius = radiusPx * z / uProjScale;
    float r2 = radius * radius;

    float phi = (BAYER[(px.x & 3) + 4 * (px.y & 3)] + 0.5) / 16.0 * TAU;
    float bias = 0.01 + 0.002 * z;
    float sum = 0.0;
    for (int i = 0; i < SAMPLES; i++)
    {
        float a = (float(i) + 0.5) / float(SAMPLES);
        float angle = a * TURNS * TAU + phi;
        ivec2 q = px + ivec2(round(vec2(cos(angle), sin(angle)) * a * radiusPx));
        if (q.x < 0 || q.y < 0 || q.x >= size.x || q.y >= size.y) continue;
        float qz = texelFetch(uGeometry, q, 0).w;
        if (qz <= 0.0) continue;
        vec3 v = viewPosition(q, qz, fsize) - p;
        float vv = dot(v, v);
        float falloff = max(1.0 - vv / r2, 0.0);
        sum += max(dot(v, n) - bias, 0.0) / (sqrt(vv) + 0.01) * falloff;
    }

    float ao = clamp(1.0 - uIntensity * sum / float(SAMPLES), 0.0, 1.0);
    ao = mix(ao, 1.0, smoothstep(uMaxDepth * 0.6, uMaxDepth, z));
    oAo = vec2(ao, z);
}";

        /// <summary>
        /// Separable depth-aware Gaussian (9 taps along uDir): smooths the AO noise without crossing depth edges.
        /// </summary>
        public const string AO_BLUR_FS = @"#version 330 core
uniform sampler2D uAoInput;
uniform vec2 uDir;
out vec2 oAo;
void main()
{
    ivec2 size = textureSize(uAoInput, 0);
    ivec2 px = ivec2(gl_FragCoord.xy);
    ivec2 dir = ivec2(uDir);
    vec2 centre = texelFetch(uAoInput, px, 0).rg;
    float z = centre.y;
    if (z <= 0.0) { oAo = centre; return; }

    float tolerance = 0.02 * z + 0.03;
    float sum = centre.x, weights = 1.0;
    for (int i = -4; i <= 4; i++)
    {
        if (i == 0) continue;
        vec2 s = texelFetch(uAoInput, clamp(px + dir * i, ivec2(0), size - 1), 0).rg;
        float w = exp(-float(i * i) / 8.0) * max(0.0, 1.0 - abs(s.y - z) / tolerance);
        sum += s.x * w;
        weights += w;
    }
    oAo = vec2(sum / weights, z);
}";

        /// <summary>
        /// Bloom: half-resolution glow → quarter resolution, a 4-tap box (bilinear taps cover 4×4 source texels).
        /// </summary>
        public const string GLOW_DOWN_FS = @"#version 330 core
in vec2 vNdc;
uniform sampler2D uGlowInput;
uniform vec2 uTexel;
out vec4 oColor;
void main()
{
    vec2 uv = vNdc * 0.5 + 0.5;
    vec3 c = texture(uGlowInput, uv + vec2(-uTexel.x, -uTexel.y)).rgb
           + texture(uGlowInput, uv + vec2( uTexel.x, -uTexel.y)).rgb
           + texture(uGlowInput, uv + vec2(-uTexel.x,  uTexel.y)).rgb
           + texture(uGlowInput, uv + vec2( uTexel.x,  uTexel.y)).rgb;
    oColor = vec4(c * 0.25, 1.0);
}";

        /// <summary>
        /// Bloom: separable Gaussian at quarter resolution (13 taps along uStep, sigma 4 texels: about 64 screen px wide).
        /// </summary>
        public const string GLOW_BLUR_FS = @"#version 330 core
in vec2 vNdc;
uniform sampler2D uGlowInput;
uniform vec2 uStep;
out vec4 oColor;
void main()
{
    vec2 uv = vNdc * 0.5 + 0.5;
    vec3 sum = vec3(0.0);
    float weights = 0.0;
    for (int i = -6; i <= 6; i++)
    {
        float w = exp(-float(i * i) / 32.0);
        sum += texture(uGlowInput, uv + uStep * float(i)).rgb * w;
        weights += w;
    }
    oColor = vec4(sum / weights, 1.0);
}";

        /// <summary>
        /// Bloom: added over the finished scene (blend ONE, ONE), bilinear upsampled from quarter resolution.
        /// </summary>
        public const string GLOW_COMPOSITE_FS = @"#version 330 core
in vec2 vNdc;
uniform sampler2D uGlowInput;
uniform float uStrength;
out vec4 oColor;
void main()
{
    vec3 glow = texture(uGlowInput, vNdc * 0.5 + 0.5).rgb * uStrength;
    oColor = vec4(glow / (1.0 + glow), 0.0);
}";

        #endregion

        #region Overlay and UI

        /// <summary>
        /// Grids round A: grid lines and level outlines. Each segment is a quad whose corners share the segment's end
        /// point and direction; the vertex shader pushes them sideways to a constant pixel width (screen-facing, like
        /// Overlay3D.Line). aAlong is metres along the grid (for the dash-dot pattern; negative = solid, the level
        /// outlines).
        /// </summary>
        public const string GRID_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aDir;
layout(location = 2) in float aAlong;
layout(location = 3) in float aSide;
layout(location = 4) in vec4 aColor;
uniform mat4 uViewProj;
uniform vec3 uEye;
uniform float uHalfWidth;
out vec4 vColor;
out float vAlong;
void main()
{
    vec3 toEye = uEye - aPos;
    vec3 side = cross(aDir, toEye);
    float sideLength = length(side);
    side = sideLength > 1e-6 ? side / sideLength : vec3(0.0);
    float hw = uHalfWidth * max(length(toEye), 0.05);
    vColor = aColor;
    vAlong = aAlong;
    gl_Position = uViewProj * vec4(aPos + side * (aSide * hw), 1.0);
}";

        /// <summary>
        /// Grids round A: a Revit-like dash-dot (2.4 m dash, 0.5 m gap, 0.3 m dot, 0.5 m gap) in world metres; once
        /// the dot would be under ~3 pixels (far away) the line turns solid so it doesn't shimmer. uDashed 0 (grids round
        /// B, the default): solid lines.
        /// </summary>
        public const string GRID_FS = @"#version 330 core
in vec4 vColor;
in float vAlong;
uniform float uAlpha;
uniform int uDashed;
out vec4 oColor;
void main()
{
    if (uDashed == 1 && vAlong >= 0.0 && fwidth(vAlong) < 0.1)
    {
        float t = mod(vAlong, 3.7);
        bool on = t < 2.4 || (t >= 2.9 && t < 3.2);
        if (!on) discard;
    }
    oColor = vec4(vColor.rgb, vColor.a * uAlpha);
}";

        public const string OVERLAY_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;
uniform mat4 uViewProj;
out vec4 vColor;
void main()
{
    vColor = aColor;
    gl_Position = uViewProj * vec4(aPos, 1.0);
}";

        public const string OVERLAY_FS = @"#version 330 core
in vec4 vColor;
uniform float uAlpha;
out vec4 oColor;
void main()
{
    oColor = vec4(vColor.rgb, vColor.a * uAlpha);
}";

        public const string UI_VS = @"#version 330 core
layout(location = 0) in vec2 aPos;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;
uniform vec2 uScreen;
out vec2 vUv;
out vec4 vColor;
void main()
{
    vUv = aUv;
    vColor = aColor;
    gl_Position = vec4(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0, 0.0, 1.0);
}";

        public const string UI_FS = @"#version 330 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uAtlas;
out vec4 oColor;
void main()
{
    oColor = vColor * texture(uAtlas, vUv);
}";

        #endregion
    }
}