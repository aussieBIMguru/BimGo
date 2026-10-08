// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// GLSL sources (330 core, runs on GL 3.3 and 4.x core contexts).
    /// Matrices are uploaded straight from System.Numerics, so "M * v" in GLSL matches "v * M" in C#.
    /// </summary>
    internal static class Shaders
    {
        #region Scene (static batches; uModel is identity except for moved / cloned elements)

        /// <summary>
        /// Scene vertex shader. aEmissive (location 3) is the per-vertex glow (RGB colour, A = strength / 4); models
        /// without glowing surfaces leave the attribute disabled, so it reads (0, 0, 0, 1) = no glow.
        /// aMaterial (4, the Realistic-mode material index; 65535 = none) and aUv (5, surface coordinates in metres)
        /// are only enabled for snapshots with materials; the fragment shader only reads them when uRealistic = 1.
        /// </summary>
        public const string SCENE_VS = @"#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aColor;
layout(location = 3) in vec4 aEmissive;
layout(location = 4) in float aMaterial;
layout(location = 5) in vec2 aUv;
uniform mat4 uViewProj;
uniform mat4 uModel;
out vec3 vWorld;
out vec3 vNormal;
out vec4 vColor;
out vec3 vEmissive;
flat out int vMaterial;
out vec2 vUv;
void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = mat3(uModel) * aNormal;
    vColor = aColor;
    vEmissive = aEmissive.rgb * (aEmissive.a * 4.0);
    vMaterial = int(aMaterial + 0.5);
    vUv = aUv;
    gl_Position = uViewProj * world;
}";

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
        /// travelling waves (uTime), faded with distance, and adds a sun glint (no shadow test).</para>
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
        public const string MATERIALS_GLSL = @"
uniform int uRealistic;
uniform sampler2D uMaterialTable;
uniform sampler2DArray uTex0;
uniform sampler2DArray uTex1;
uniform sampler2DArray uTex2;
uniform sampler2DArray uTex3;
uniform int uReflections;
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
    return h >= 0.0 ? mix(uSkyHorizon, uSkyZenith, pow(clamp(h, 0.0, 1.0), 0.55)) : uSkyHorizon * 0.55;
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
        float phase = dot(WAVE_DIR[i], world.xy) * WAVE_K[i] + uTime * WAVE_SPEED[i];
        slope += WAVE_DIR[i] * (WAVE_AMP[i] * WAVE_K[i] * cos(phase));
    }
    slope *= amount * 0.2 / (1.0 + dist * 0.06);
    return normalize(n + vec3(-slope, 0.0));
}

// The probes at a point: x = first probe (-1 = none: the sky), y = second (-1 none), z = the second's weight
vec3 probeCell(vec3 world)
{
    if (uProbesOn == 0) return vec3(-1.0, -1.0, 0.0);
    vec3 c = floor((world - uProbeGridOrigin) / uProbeGridCell);
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
vec3 probeEnvironment(vec3 world, vec3 dir, float rough, out bool have, out float weight)
{
    have = false;
    weight = 1.0;
    vec3 cell = probeCell(world);
    if (cell.x < 0.0) return vec3(0.0);
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
vec3 probeDebugColour(vec3 world)
{
    vec3 cell = probeCell(world);
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
        /// uLightPos: xyz position, w radius. uLightColor: rgb colour × intensity, w downward share.
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

const float LIGHT_BOUNCE = 0.06;
const float LIGHT_NEAR = 0.08;
const float LIGHT_PAD = 1.03;
const vec3 FACE_F[6] = vec3[6](vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, -1.0));
const vec3 FACE_U[6] = vec3[6](vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0));

// 1 = the light reaches this point, 0 = something is in the way (4-tap PCF on the face the point falls in)
float lightVisibility(vec3 world, vec3 n, vec3 lp, float far, float layerBase)
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
        float visible = lightVisibility(world, n, lp.xyz, lp.w, sh.x);
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
        // l points from the surface to the light: l.z > 0 when the surface is below it
        float lobe = mix(1.0, 4.0 * max(l.z, 0.0), lc.w);
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

        public const string SCENE_FS = "#version 330 core\n" + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + MATERIALS_GLSL + @"
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
out vec4 oColor;
void main()
{
    // Texture gradients first: derivatives are only defined outside the per-material branches
    vec2 uvDx = dFdx(vUv);
    vec2 uvDy = dFdy(vUv);
    if (vWorld.z < uClipZ.x || vWorld.z > uClipZ.y) discard;

    vec4 base = vColor;
    float reflectivity = 0.0;
    vec4 shine = vec4(0.0, 1.0, 0.0, 0.0);
    if (uRealistic == 1 && uWhitecard == 0 && vMaterial >= 0 && vMaterial < 65535)
    {
        vec4 r = realisticColour(vMaterial, vUv, uvDx, uvDy);
        base.rgb = r.rgb;
        reflectivity = r.a;
        shine = reflectionInfo(vMaterial);
        if (uReflectDebug == 1) base.rgb = reflectionDebugColour(shine, reflectivity);
        else if (uReflectDebug == 2) base.rgb = probeDebugColour(vWorld);
    }
    if (uWhitecard == 1)
    {
        float l = dot(base.rgb, vec3(0.299, 0.587, 0.114));
        base.rgb = base.a < 0.98 ? vec3(0.76, 0.82, 0.87) : vec3(0.80 + 0.12 * l);
    }

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
    if (reflecting && (shineFlags & 2) != 0 && n.z > 0.3) n = waterNormal(vWorld, n, shine.w, dist);
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
    if (uLightCount > 0) { lit += base.rgb * artificialLight(vWorld, n, ao); }
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
            bool have;
            float weight;
            vec3 env = probeEnvironment(vWorld, dir, 0.0, have, weight);
            if (have) fresnel *= weight;
            lit = mix(lit, have ? env : skyColour(dir), fresnel);
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
                bool have;
                float weight;
                vec3 env = probeEnvironment(vWorld, dir, rough, have, weight);
                if (!have) env = blurredSky(dir, rough) * mix(0.55, 1.0, ao);
                else fresnel *= weight;
                // Metals: the reflection takes the metal's colour (kept fairly bright: chrome is near white)
                if (metal) env *= mix(base.rgb, vec3(1.0), 0.5);
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

    float fog = clamp(1.0 - exp(-dist * uFogDensity), 0.0, 0.65);
    lit = shoulder(mix(lit, uFogColor, fog));

    vec4 result = vec4(lit, alpha);
    if (uOverride.a > 0.0)
    {
        result.rgb = mix(result.rgb, uOverride.rgb, uOverride.a);
        result.a = max(result.a, 0.85);
    }
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

        public const string SKY_FS = @"#version 330 core
in vec2 vNdc;
uniform mat4 uInvViewProj;
uniform vec3 uEye;
uniform int uSun;
uniform vec3 uSunDir;
uniform vec3 uZenith;
uniform vec3 uHorizon;
uniform vec3 uSunDisc;
out vec4 oColor;
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
    if (uSun == 1)
    {
        float d = max(dot(dir, uSunDir), 0.0);
        c += uSunDisc * (smoothstep(0.99955, 0.99975, d) + 0.35 * pow(d, 64.0) + 0.15 * pow(d, 8.0));
    }
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

        public const string GROUND_FS = "#version 330 core\n" + SUN_GLSL + AO_GLSL + LIGHTS_GLSL + @"
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
    vec3 c = albedo;
    float ao = ambientOcclusion(vWorld, uEye);
    if (uSun == 1) { c *= sunLight(vec3(0.0, 0.0, 1.0), vWorld, uEye, ao); }
    else { c *= mix(1.0, ao, 0.6); }
    if (uLightCount > 0) { c += albedo * artificialLight(vWorld, vec3(0.0, 0.0, 1.0), ao); }
    c = mix(c, uFogColor, clamp(d / 1200.0, 0.0, 1.0));
    oColor = vec4(shoulder(c), 1.0);
}";

        #endregion

        #region Ambient occlusion

        /// <summary>
        /// Geometry pre-pass outputs. 0: the view-space normal (right, up, forward), turned to face the eye, and the
        /// view depth (metres along the camera's forward axis); cleared to 0 = nothing there (sky). 1: glow (emissive
        /// colour × uGlow, already hidden behind whatever is in front), the bloom's source; only bound when glow is on.
        /// </summary>
        private const string GEOMETRY_GLSL = @"#version 330 core
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

        /// <summary>Geometry pre-pass for scene batches (vertex shader: <see cref="SCENE_VS"/>).</summary>
        public const string GEOMETRY_FS = GEOMETRY_GLSL + @"
in vec3 vWorld;
in vec3 vNormal;
in vec3 vEmissive;
void main()
{
    writeGeometry(vWorld, vNormal);
    oGlow = vec4(vEmissive * uGlow, 1.0);
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
