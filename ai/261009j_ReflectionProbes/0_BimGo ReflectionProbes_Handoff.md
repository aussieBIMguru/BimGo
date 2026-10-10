# BimGo — Handoff Brief: reflection probes (experimental, tiered reflectivity)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**), built on his own PC in Visual Studio.
This brief starts a new chat. It's an **experiment on top of materials build B**. If it can't be made to run well, it's
dropped and build B stands as it is. Nothing here may slow down a model that has no reflective materials, or a machine
with reflections off.

**Read first:**
1. This brief.
2. `ai/261009i_Materials/4_build notes build B.md`, including §4b (tint test model results: Revit blends in linear light).
3. `README.md` §5 (how rendering works), §6 (`materials.json`), §8 (limitations).
4. **Ask Gavin for a fresh zip of his working copy before editing**, plus:
   - any compile fixes to build B;
   - the result of the open "wall D" question (the plain brick drawing grey-green): the exported `.bimgo` and `BG_Masonry_Brickwork-Brown1.jpg`, if not already sorted;
   - FPS and GPU in Realistic mode on Snowdon and BG01, as a baseline.

   His copy is the source of truth. The last zip from Claude was `BimGo_1.0_Materials_BuildB.zip` (linear-light update).

---

## 1. Where things stand

| Item | State |
|---|---|
| Transparency | **Done** (since v6 and materials build A): Revit material transparency → vertex alpha; opaque and transparent batches; glass transmits sunlight into the sun shadows (tinted); see-through materials get a Fresnel **sky** sheen in Realistic mode ("Sky reflections on glass"). |
| Reflectivity read from Revit | **Only for see-through materials** (`SceneExtractor.Materials.cs` → `ReadReflectivity`): `glazing_reflectance`, `generic_reflectivity_at_0deg`, or from `transparent_ior`, clamped 0.02–0.6. Opaque materials get 0 on purpose: without probes a polished floor would reflect the sky indoors. |
| Reflections of the model itself | **None.** The sky colour is the only thing reflected. |
| Renderer constraints | OpenGL **3.3 core** (no `samplerCubeArray`, no compute); texture units 1–10 already used (shadows, transmit, AO, glow, light shadows, material table, four texture buckets); `SceneRenderer` already renders the scene into extra targets (AO / glow pre-pass at half resolution, cached shadow maps per light). |

## 2. Gavin's idea (his words, condensed)

> Try reflection probes on reflective materials, with a **tolerant threshold**: floor the Revit reflectivity to steps of
> **25 %** (0 / 25 / 50 / 75 % +, up to mirrored). It's going to be perf heavy, so keep it switchable.

So:

- Reflectivity is read per material from Revit (all schemas, not just glass).
- It's **floored to quarter tiers**. Anything below 25 % reflects nothing (no cost), which is most materials in a typical model.
- Each tier gets a fixed strength and blur, so the shader has four cases rather than a continuous physically-based model.

## 3. Proposed design (for discussion with Gavin, not decided)

### 3.1 Data from Revit (extraction, additive format)

New optional fields on `SceneMaterial` (strings and numbers only; **no new enum values** in existing fields):

- `reflectance` (0–1): head-on reflectance as read;
- `roughness` (0–1): 0 is a mirror, 1 is matt;
- `metallic` (bool, written only when true): metals tint their reflection by their colour;
- `reflectSource` (string): which property it came from (diagnostics).

Store the **raw values**; tiering happens in the app, so the threshold can change without re-extracting.

Where the values likely come from. **These are unconfirmed names: confirm them with the scan report's PROPERTY NAMES BY
SCHEMA section on Snowdon, BG01 and BG05 before writing the reader.**

| Schema | Reflectance | Roughness / gloss |
|---|---|---|
| Generic | `generic_reflectivity_at_0deg` (and `_at_90deg`) | `generic_glossiness` (gloss → roughness = 1 − gloss) |
| Prism opaque / layered | (derived from `opaque_f0` / IOR if present) | `surface_roughness` |
| Prism metal | `metal_f0` colour (metallic) | `surface_roughness` |
| Mirror | 0.9+ (`mirror_tintcolor` tints it) | 0 |
| Simple schemas: Ceramic, WallPaint, Metal, PlasticVinyl, Stone, Concrete, Masonry, Hardwood | — | A `*_finish` enum (gloss / semi-gloss / satin / matte, polished / brushed…) mapped through a small table, e.g. polished ≈ 50 %, gloss ≈ 50 %, semi-gloss ≈ 25 %, satin / matte → 0 |
| Glass (already read) | As now | 0 |

Gavin's call on the mapping table (an Advanced polished floor should land in 25–50 %, a mirror in 75 %+, plasterboard paint
at 0).

### 3.2 Tiers (app side)

`tier = floor(effective × 4) / 4`, where `effective` combines reflectance and gloss:

| Tier | Strength | Blur (probe mip) | Typical |
|---|---|---|---|
| 0 (< 25 %) | None (no probe read) | — | Most materials |
| 25 % | Low, Fresnel-weighted | Heavily blurred | Polished concrete, satin tiles, sealed timber |
| 50 % | Medium | Blurred | Gloss tiles, polished stone, lacquer |
| 75 % + | High | Sharp | Mirrors, polished metal, glass (interior reflections) |

Settings:

- `Reflections` becomes an enum: **Off / Sky on glass (today) / Probes**.
- `ReflectionThreshold` (25 % default; 50 % option, for "only shiny things").
- `ProbeResolution` (128 / 256, per machine).
- All in the pause menu's display card, with a status line in the F1 panel (probes, bake time, MB).

### 3.3 Probes

- **What:** static environment captures. Each one renders the scene once in 6 directions from a point, and is stored **octahedrally** in one RGBA16F `sampler2DArray` (one layer per probe, mipmapped). Plain cubemaps aren't used because cube arrays need GL 4.0 and we're on 3.3; octahedral mapping is the standard workaround.
- **Where:** one probe per **Revit room** (rooms are already extracted with plan loops and Z range) at its centroid, about 1.5 m above the floor. Large rooms (longest side > ~12 m) get a grid of probes. Places with no room (outdoors, unroomed areas) fall back to the sky.
  - **Optional:** only bake rooms that actually contain a reflective (tier ≥ 25 %) surface. On a typical model that's a handful of probes, not hundreds.
- **Lookup:** a small plan-grid texture (≈ 0.5 m cells, per level band) holds the probe index per cell, so a fragment finds its probe with one fetch. There's no per-vertex attribute to add and no CPU work per frame.
  - **Box projection** (parallax correction) against the room's box keeps floor and wall reflections roughly in place as you move. This is what makes a polished floor look right.
- **Baking:** at load, behind the progress screen (after batches and the BVH):
  - **What's drawn:** each face is drawn with the existing scene path at low resolution, with no AO and no glow, in Realistic or Material colours.
  - **Re-bakes:** on a sun-panel time change (debounced, about 1 s after the slider stops), when lights are toggled, and on demand (a hotkey or menu button). Edits (moved / cloned / demolished elements) mark probes stale. Re-baking near the player first is a stretch goal.
  - **Budget:** about 6 draws × N probes at 128². Measure Snowdon and BG01 and log the bake time. Stop with a toast when a time or memory cap is hit (e.g. 64 probes / 50 MB).
- **Shading:**
  - Probes are sampled only when tier ≥ the threshold. The tier picks the strength and the mip (blur).
  - Schlick Fresnel, as for glass today.
  - Metals tint the reflection by their base colour.
  - Glass in a room reflects that room instead of the sky.
  - Everything blends in linear light, consistent with build B.

### 3.4 Alternatives (for the record)

- **Screen-space reflections:** no bake, dynamic, but only reflects what's on screen (edges fade, things behind the camera vanish). Skipped in the materials round.
- **Planar reflection for big floors:** an exact mirror of the scene for one plane (render the scene a second time mirrored). It looks best on a large polished floor but costs a full extra scene draw per frame. A possible later "high" option for the floor the player stands on.
- **Probes plus SSR** (SSR where it can, probe where it can't) is what games do. Too much for an experiment.

## 4. Suggested stages

1. **Stage 0, diagnostic:** extend the Material scan report with a REFLECTIVITY section. For each used material it lists the candidate properties found (reflectivity, gloss, roughness, finish enums, f0) and the tier the proposed mapping would give. Gavin runs it on Snowdon, BG01 and BG05 and agrees the mapping table. A **"Reflectivity" debug colour mode** in the app (tiers as 4 colours) would check it visually.
2. **Build A:** extraction + format fields + tiers + settings + debug mode. The shader still reflects only the sky, but now on every tier ≥ the threshold. This is the cheap version: decide whether the sky-only result is already good enough outdoors.
3. **Build B:** probes (placement, bake, octahedral array, plan-grid lookup, box projection, re-bake triggers), menu, perf logging.

## 5. Likely files touched

| Area | Files |
|---|---|
| Core | `Scene/MaterialData.cs` (`reflectance`, `roughness`, `metallic`, `reflectSource`), `Scene/LaunchSettings.cs` (`Reflections` enum with migration from the bool, `ReflectionThreshold`, `ProbeResolution`), tests |
| Revit | `Extraction/SceneExtractor.Materials.cs` (`ReadReflectivity` → all schemas, finish table), `Extraction/MaterialScan.cs` (REFLECTIVITY section) |
| App | `Rendering/ReflectionProbes.cs` (new: placement, bake, array, grid), `Rendering/SceneRenderer.cs` (bake hook, uniforms, units 11–12), `Rendering/Shaders.cs` (probe sampling, octahedral decode, box projection), `Rendering/MaterialTextures.cs` (roughness / metallic in the table's 5th texel, or a 6th), `Game/GameSession*.cs` (menu, re-bake triggers, status), `Native/Gl.cs` (any missing entry points: `glFramebufferTextureLayer` exists from v6; check the RGBA16F mip render) |

## 6. Questions for Gavin (ask, don't assume)

1. **Scope:** all three stages, or stop after build A (sky on every reflective surface) if that already reads well?
2. **Mapping:** which materials in his models *should* reflect (polished concrete floors? tiles? mirrors and stainless only?), and the finish-enum table values.
3. **Threshold default:** 25 % (more shine, more cost) or 50 %?
4. **Probe placement:** per room (needs rooms in the model; unroomed areas get sky), or a regular grid as well?
5. **Re-bake:** automatic on sun / lights changes, or manual only (a "Refresh reflections" button)?
6. **Budget:** acceptable bake time at load (e.g. ≤ 3 s on BG01) and a GPU memory cap.
7. **Planar reflection** of the floor as a later "high" option: interested?

## 7. Test plan (to firm up with the stages)

- The scan report's REFLECTIVITY section on Snowdon, BG01 and BG05 matches Gavin's expectations (mirror → 75 %+, plasterboard → 0).
- **Debug mode:** the tier colours sit on the right surfaces.
- **Build A:** shiny floors outside reflect the sky; indoors no sky shows (only tier ≥ threshold and Fresnel). Off / Sky / Probes switch instantly.
- **Build B:**
  - a polished floor reflects the walls and furniture of its room and stays roughly in place while walking (box projection); a mirror shows the room;
  - a sun time change re-bakes within ~1 s of stopping;
  - with probes on and no reflective materials, nothing is baked and FPS is unchanged;
  - bake time and FPS are logged on Snowdon and BG01;
  - running out of memory falls back to sky with a toast.

## 8. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations**.
- **BimGo.Revit stays package-free;** no new packages in the app without Gavin's yes (none expected).
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`, show a toast or dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`. Nothing changes the Revit model.
- Format and protocol stay backward compatible (`formatVersion` 1; additive optional fields; no new values in existing enums; the `reflections` setting migrates from bool).
- Run the Core tests after any Core change.
- Keep `README.md` and the round's `ai/` notes current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
- Claude can't compile here. Check the GLSL in headless WebGL2: Playwright + the pre-installed Chromium, with the `extract.py` / `test.html` approach from the materials round (extract shader strings from `Shaders.cs`, convert `#version 330 core` → `300 es`, compile, link and run a small functional test). Say so in the notes.
