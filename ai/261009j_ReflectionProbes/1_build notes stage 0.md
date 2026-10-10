# BimGo: Reflection probes round, build notes, stage 0 (diagnostic)

**Status: experimental round, on top of materials build B.** If probes can't be made to run well, the round is dropped
and build B stands. Nothing may slow down a model with no reflective materials, or a machine with reflections off.

Claude could not compile here (no .NET SDK or Revit API in the sandbox). The C# was reviewed by eye. Most likely to need
a touch-up: the `Visual.AssetProperty*` pattern matches in `ReflectivityReader.cs` and the enum lookup by reflection
(`typeof(Visual.Asset).Assembly.GetTypes()`), which is wrapped so that a failure only loses the enum names.

---

## 1. Decisions agreed with Gavin (2026-10-08)

| Topic | Decision |
|---|---|
| Starting point | `BimGo_1.0_Materials_BuildB.zip` as handed back (Gavin's working copy). Wall D brick issue closed. |
| Stages | Stage 0 (scan) → build A (extraction, tiers, settings, debug mode, sky shine on every tier ≥ threshold, water ripples) → **hold point** → build B (probes). |
| Intent | From the material's reflection settings in Revit, **never keywords**. Glass is a special case (keeps its sky sheen; reflects its room once probes exist). |
| Strength vs blur | **Separate.** Strength (reflectivity) picks the tier; roughness / glossiness picks the blur, used continuously as the probe mip in the app. |
| Finish table | Claude's first proposal (in `ReflectivityReader.FINISH_TABLE`), judged together against the scans. |
| Threshold | **50 % default**, user can lower it to 25 %. |
| FPS | Target 60, tolerate ~50 at worst. Scenes dip to ~55 today. Probe reads are cheap; the bake gets a fixed per-frame budget (~1.5–2 ms). |
| Placement | Rooms (many small meeting rooms: one probe each) **plus a fallback grid** for unroomed areas, only where a reflective surface exists. |
| Room boundaries | **Blend** two probes across ~1 m at thresholds (2 indices + weight per grid cell). |
| Baking | **Progressive** (sky first, probes fill in over a few seconds), automatic re-bake on sun / light changes, also progressive. |
| GPU target | Mid-range workstation card (RTX A2000 / A4000, 3060 / 4060 class, 8–12 GB). 128² default, 256² "High", cap 64 probes (≈ 11 / 45 MB). |
| Water | Revit **Water schema** only (water-named materials are listed in the scan, never applied). Ripples on the frame clock (only water pixels on screen pay for them), detail fades with distance. Water-body probes are a build B stretch goal. Player-reactive ripples: no. |
| Planar reflection | Parked; decide as we go. |

## 2. What stage 0 adds

| File | What |
|---|---|
| `BimGo.Revit/Extraction/ReflectivityReader.cs` (new) | `ReflectivityInfo` (strength, roughness, metallic, glass, water, water type, mapped, map connected, source; `Tier`) and `ReflectivityReader.Read(asset, schema, material)`. Reused by the extractor in build A. |
| `BimGo.Revit/Extraction/MaterialScan.cs` | REFLECTIVITY section; a `Reflectivity:` line per used material in the dump; one summary line in the finished dialog. |
| `README.md` | Changelog entry. |

### Rules (first match wins)

| # | Rule | Strength | Roughness |
|---|---|---|---|
| 1 | Schema contains `Mirror` | 0.9, metallic | 0 |
| 2 | Schema contains `Water` | 0.6 | 0.05 (ripples add movement) |
| 3 | See-through (same test as today's glass sheen) | `glazing_reflectance` → `solidglass_reflectance` → `generic_reflectivity_at_0deg` → from `transparent_ior` → 0.06; clamped 0.02–0.6 | `surface_roughness` or 0 |
| 4 | Prism (Advanced) metal | max(`metal_f0`) × (1 − 0.5 × rough), metallic | `surface_roughness` / `layered_roughness` / `*_roughness`, else 0.5 |
| 4 | Prism dielectric | 0.6 × (1 − rough)² (rough 0.1 ≈ 50 %, 0.3 ≈ 25 %) | as above |
| 5 | Generic | `generic_reflectivity_at_0deg`, metallic if `generic_is_metal` | 1 − `generic_glossiness` |
| 6 | Finish enum (table below), concrete sealant adds epoxy +0.2 / acrylic +0.1 strength and removes the same roughness | table | table |
| 7 | Anything else (legacy 0-property presets, unknown schemas) | 0 | 1 |

Finish table (strength / roughness): flat 0 / 0.9 · eggshell 0.05 / 0.8 · platinum 0.1 / 0.7 · pearl 0.15 / 0.6 ·
semigloss 0.3 / 0.4 · gloss 0.55 / 0.15 · glossy 0.5 / 0.15 · high glossy 0.55 / 0.1 · satin 0.15 / 0.55 (ceramic satin
0.3 / 0.4) · matte 0.05 / 0.8 · unfinished 0 / 0.9 · polished 0.6 / 0.05 (concrete 0.35 / 0.25; metal 0.85 / 0.05) ·
smooth 0.1 / 0.6 · broom 0 / 0.9 · stamped 0.05 / 0.8 · metal semi-polished 0.7 / 0.2, satin 0.55 / 0.4, brushed 0.5 / 0.5 ·
metallic paint car paint 0.5 / 0.1, chrome 0.85 / 0.05, matte 0.1 / 0.6.

Tier = floor(strength × 4) / 4 → 0, 25 %, 50 %, 75 % +. The report's blur words: < 0.15 sharp, < 0.35 soft, < 0.6
blurred, else very blurred.

**Open points the scan should settle:**
- whether Prism dielectrics' roughness values make the 0.6 × (1 − r)² curve sensible (Snowdon is mostly Prism);
- what the Generic library materials set `generic_reflectivity_at_0deg` to (if plain paint sits at 0.3+, Generic needs a scale);
- whether the guessed enum ordinals match Revit's names (any `<<< MISMATCH` line);
- whether a roughness / reflectivity map connected to a slot is common (the scalar is used either way).

## 3. Test checklist for Gavin

1. Build R25 (Revit add-in only; the app and Core are unchanged).
2. On **Snowdon, BG01 and BG05**: Go → Options → MATERIALS & TEXTURES → Review textures… → **Export report…**.
3. Send back the three reports (or just their REFLECTIVITY sections plus any surprising material dumps).
4. While reading them, note:
   - materials that should reflect but land at 0 or 25 % (and roughly how shiny they should be);
   - materials that shouldn't reflect but land at 50 % + (painted plasterboard must be 0);
   - mirrors at 75 % +, glass listed as glass;
   - anything in "Schemas with no rule" you'd expect to shine;
   - any `<<< MISMATCH` in the enum list.
5. If you have a model with a pool or water feature, scan it too (the Water schema path is otherwise untested).

## 4. Next: build A (after the scans)

- **Extraction:** `ReadReflectivity` → `ReflectivityReader` for every schema; new optional `SceneMaterial` fields
  `reflectance`, `roughness`, `metallic`, `water`, `waterType`, `reflectSource` (raw values; tiering in the app).
- **Core:** `LaunchSettings.Reflections` Off / Sky / Probes (migrating from the bool), `ReflectionThreshold` (50 % default,
  25 % option), `ProbeResolution`; tests.
- **App:** material table texel for roughness / metallic / water; Fresnel sky reflection on every tier ≥ threshold with
  roughness-driven blur (sky only, so indoor shine stays subtle until probes); water ripples (time uniform, 2–3 scrolling
  noise octaves, distance fade); **Reflectivity debug colour mode** (tiers as 4 colours, water and glass flagged).
- GLSL checked in headless WebGL2 (Playwright + Chromium, `#version 330 core` → `300 es`), as in the materials round.

---

## 5. Findings from Gavin's scans (2026-10-08, Revit 2025) and stage 0.1 changes

Models: **Snowdon Towers** (199 used materials), **BG03** (44, has a Water material), **BG05** (27).

**What worked**
- Enum names resolve by reflection on every model; the ordinal guesses matched except concrete `4` (Revit: `Custom`, guessed `stamped`).
- The Metal schema's `metal_finish` (Polished / SemiPolished / Satin / Brushed) carries Gavin's metals: BG05 "Metal Chrome" 75 % +, "Metal Stainless Steel" semi-polished; BG03 chrome, satin and brushed steels.
- BG03 `BG_MAT_Site_Water` is a WaterSchema (`water_type` Lake, `water_tint_color`, `water_bump_amount` 0.1).
- Painted plasterboard / gypsum lands at 0 everywhere (Generic reflectivity 0, Wall Paint flat, Prism gypsum roughness 0.66).
- **The app does not use any of this yet.** Stage 0 is the scan only; the app still runs build B (sky sheen on glass only). Chrome and water in the app are build A.

**Problems → changes (stage 0.1)**

| Finding | Change |
|---|---|
| Flooring sat too low: stainless 0.70 → 50 %, floorboards 0.35 → 25 %; Gavin asked to round up | Tiers **round to the nearest 25 %** (≥ 12.5 % → 25, ≥ 37.5 % → 50, ≥ 62.5 % → 75 % +). Not "always up": that would make every 5 % plastic shine. |
| Snowdon: brick, cast-in-place concrete, asphalt, plywood, paper all at 25 % (Prism roughness scalar 0.2 is a library placeholder when a roughness map is connected) | Prism roughness = the **connected roughness map's average** (decoded at 32 × 32, cached by path; inverted if `unifiedbitmap_Invert`); unreadable map → max(scalar, 0.5). Curve steepened to 0.6 × (1 − r)⁴ (r ≤ 0.1 → 50 %, ≤ 0.32 → 25 %). |
| Gavin models mirrors with a glass appearance | **One keyword exception:** a material name containing "mirror" is a mirror (75 % +, sharp) whatever its schema, ahead of the glass rule. Build A draws it **opaque**. Revit's Mirror schema still goes first. |
| BG03 `BG_MAT_Plastic_Device` uses the Mirror schema with a near-black tint (screens) | Mirror tint max < 0.3 → glossy black (50 %, untinted), not a dark mirror. |
| Concrete `Custom` finish unmapped (BG05 Concrete Insitu) | `concrete_finish:custom` → 0 / 0.9 (a sealant still adds sheen); ordinal guess fixed. |
| Snowdon legacy presets with 0 properties (Mirror, Metal - Steel, Polished, Stainless, Chrome…) reflect nothing | Use the material's **graphics shininess** when raised above Revit's default 64: 128 → 85 % sharp; metallic for `Metal-…` presets. "Mirror" is caught by the name rule. Presets left at 64 still reflect nothing. |
| Glass sheen visible but weak (raw glazing 0.15, Prism glass 0.04, generic 0.2) | **Build A, display side** (raw values stay physical in the file): glass head-on = clamp(raw × 2.5, 0.10, 0.5) plus Fresnel; opaque tiers shown at fixed strengths (25 % → 0.30, 50 % → 0.55, 75 % + → 0.90); a **Reflection strength** slider (0.5–2×, default 1) in the display card. |
| Snowdon's site "Water" is a Prism Transparent (Advanced) material: no Water schema exists for Advanced materials | Open question for Gavin (see the chat): allow the "water" name on see-through materials only. |
