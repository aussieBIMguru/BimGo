# BimGo: Reflection probes round, build notes, build A (sky reflections on every tier, water, debug colours)

**Status: experimental, hold point after this build.** Decide whether sky-only shine reads well enough outdoors, and
check the tiers with the debug colours, before probes (build B).

Claude could not compile the C# here (no .NET SDK or Revit API). The **scene shader was compiled, linked and run in
headless WebGL2** (Playwright + Chromium, `#version 330 core` → `300 es`, `extract.py` pulls the strings out of
`Shaders.cs`): a plain surface is unchanged; a 75 % chrome reflects the sky tinted by its colour; a 50 % dielectric gets a
sheen; a 25 % surface reflects only with the threshold at 25 %; water ripples move with `uTime`; the debug colours
show; reflections off restores the plain surface. No GL errors.

## 1. Decisions (2026-10-08)

| Topic | Decision |
|---|---|
| Name rules | Two exceptions to "no keywords": **"mirror"** in a material name = mirror (75 % +, sharp, drawn opaque) whatever its appearance; **"water"** in the name of a **see-through** material = water (Advanced materials have no Water schema). |
| Tiers | Rounded to the nearest 25 % (stage 0.1). Threshold 50 % by default, 25 % optional. Water always reflects. |
| Glass | Revit's head-on value is physical (4–20 %) and read too faint against the sky: drawn × 2.5, kept within 10–50 %. |
| Strength | One user slider, 50–200 %. |
| Settings | `Reflections` stays a bool (on / off) for now; the Off / Sky / Probes choice comes with build B. |

## 2. What changed

| Area | Files |
|---|---|
| Revit | `Extraction/ReflectivityReader.cs` (water name rule, `WaterBump`), `Extraction/SceneExtractor.Materials.cs` (`ApplyReflectivity`, roughness map averages cached by path), `Extraction/SceneExtractor.cs` (mirror-named materials opaque), `Extraction/MaterialScan.cs` (wording) |
| Core | `Scene/MaterialData.cs` (`Shine`, `Roughness`, `Metallic`, `Water`, `WaterBump`, `ReflectSource`, `Clean`), `Scene/LaunchSettings.cs` (`ReflectionThreshold`, `ReflectionStrength`, `Sanitise`), `tests/…/MaterialTests.cs` (3 tests) |
| App | `Rendering/MaterialTextures.cs` (6th texel, shiny / water counts in the log), `Rendering/Shaders.cs` (tiers, blurred sky, metals, water ripples and glint, debug colours), `Rendering/SceneRenderer.cs` (`SceneDrawParams.ReflectThreshold / ReflectGain / ReflectDebug / Time`, uniforms), `Game/GameSession*.cs` (state, settings, menu, draw params) |

Format: additive optional fields in `materials.json` only (`formatVersion` 1). An older app ignores them (glass sheen only);
a file made before this build reads with no shine, so **re-extract (Go or Export with materials ticked)** to see it.

## 3. Shader model (sky only)

- `tier = round(shine × 4)` (0–3). Reflects when `tier × 25 % ≥ threshold`, or water.
- Head-on strength: metals 0.30 / 0.55 / 0.90 by tier, other surfaces half; × the user's strength.
- `fresnel = r0 + (1 − r0) × (1 − cosθ)⁵ × (1 − roughness) × 0.8`, capped at 0.95; it also raises alpha (glass, water).
- Environment: the sky, blurred towards its average by roughness, × mix(0.55, 1, AO). Metals × mix(colour, white, 0.5).
- Water: six travelling waves (2–17 rad/m, 0.9–3.6 rad/s) on up-facing surfaces, slope × ripple strength × 0.1 /
  (1 + 0.06 × distance); sun glint `pow(dot(r, sun), 180) × 1.5`.

## 4. Test checklist for Gavin

1. Build R25 and the app; run the Core tests.
2. **Re-extract** BG03, BG05 and Snowdon (Go with "Extract materials and textures" ticked): older snapshots have no shine.
3. Realistic mode, pause menu → World & display:
   - **Show reflection tiers (debug colours)**: check BG05 chrome / mirror red, stainless red, porcelain orange, BG03 water
     blue, glass cyan, painted plasterboard grey.
   - Debug off: chrome and mirrors clearly reflect the sky; glass sheen stronger than before; BG03 water ripples and
     glints in the sun.
   - Reflections Off / Shiny / All switch instantly; the strength slider changes the look live.
4. A mirror modelled with a glass appearance and named "…Mirror…" is now opaque.
5. FPS in Realistic mode on Snowdon and BG01 with reflections Off vs Shiny vs All (expect no measurable change: a few
   ALU ops per pixel, no extra passes or textures).
6. **Hold point:** does sky-only shine look right outdoors? How odd is it indoors (sky on a polished floor / chrome)?
   That decides whether build B (probes) goes ahead.
