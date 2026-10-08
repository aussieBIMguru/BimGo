# BimGo: Reflection probes round, build notes, build B (probes)

**Status: experimental.** Gavin's verdict on build A: water works and reflectivity is picked up, but sky-only reflection
isn't realistic enough indoors → probes, switchable sky / probes mid-session in the lighting panel; keep the debug
colours in a debug area of the pause menu; water waves bigger (less repetitive).

Claude could not compile the C# here. The **scene shader was compiled, linked and run in headless WebGL2** (Playwright +
Chromium): every build A case unchanged; a chrome floor under a test probe reflects the probe's faces with box projection
(the +Y wall seen where expected); an unbaked probe falls back to the sky; the probe debug colours show; no GL errors.
The CPU placement, grid and bake scheduling were reviewed by eye only: the log lines below are the first thing to check.

## 1. Decisions

| Topic | Decision |
|---|---|
| Switch | Sun panel row **Reflect: Off / Sky / Probes / Probes HQ**, mid-session, saved per machine. Probes on by default. |
| Debug | Pause menu → World & display → **DEBUG Colours: Off / Reflection tiers / Probes** (not saved). |
| Placement | Rooms with a reflective surface (tier 25 %+ or water, so the 25 % threshold works without re-placing), grid in large rooms, fallback cells outside rooms. Glass alone doesn't place a probe (nearly every room has windows) but uses one where it exists. |
| Storage | Six faces per probe in a 2D texture array (light-shadow face table) rather than octahedral maps: no conversion pass on GL 3.3. Mips by 2× blits per face. |
| Budget | 128 px default (≈ 0.5 MB / probe → 64 probes ≈ 33 MB); HQ 256 px (≈ 2 MB / probe → 30 probes within 64 MB). Most probes kept by reflective surface area. |
| Bake | Progressive, 2 faces per frame, nearest first; capture far plane = room box diagonal + 15 m (25–80 m), so a capture draws little of a large model. |
| Re-bake | Automatic, 1 s after changes settle; REFRESH button; provisional refresh when the player approaches a probe baked from afar. |

## 2. Files

| Area | Files |
|---|---|
| App | `Rendering/ReflectionProbes.cs` (new: gather reflective voxels, room index, placement, lookup grid with blending, GL resources, bake queue, mips), `Rendering/SceneRenderer.cs` (`UpdateReflectionProbes`, `CaptureProbeFaces`, sky / ground draws for any view, probe uniforms, units 11–13), `Rendering/Shaders.cs` (`probeCell`, `probeSample` with box projection, `probeEnvironment`, `probeDebugColour`; waves ×2), `Game/GameSession.Reflections.cs` (new: per-frame update, re-bake key, sun panel row), `Game/GameSession.Sun.cs` (panel row, height), `Game/GameSession.Menu.cs` (DEBUG section), `Game/GameSession.cs` / `.Render.cs` (state, settings, hook) |
| Core | `Scene/LaunchSettings.cs` (`ReflectionProbes`, `ProbeResolution`), test additions |

No format change: probes are built at load from the materials (`shine`, `water`) and rooms already in the file.

## 3. Trade-offs (as planned)

- **Static:** the player and moving things never appear; edits show after the 1 s re-bake.
- **Box projection** assumes box rooms: L-shaped rooms place reflections approximately.
- **Face seams** at blurry mips on rough surfaces (faces are filtered separately).
- **Lighting of a capture** uses the player's current lights and sun-shadow cascades: far probes are provisional and refresh when approached.
- **Rooms needed** for good indoor results; unroomed reflective surfaces get coarse fallback probes.
- **Bake cost** while baking: ~2 small scene draws per frame (watch FPS during the first seconds and after sun changes).

## 4. Test checklist for Gavin

1. Build R25 and the app; run the Core tests. Reuse the build A snapshots (no re-extract needed).
2. Realistic mode, sun panel (Shift+O): **Reflect → Probes**. The status line counts up "Probes n / m · baking k". Log:
   `Reflection probes: N placed (… in rooms, … fallback) …` and `… baked in X s`.
3. Pause menu → DEBUG Colours → **Probes**: each room with shiny surfaces has its own colour, soft blends at doors, grey
   elsewhere. Then **Reflection tiers** as before.
4. BG05 / BG03: chrome, mirrors, porcelain and tiles reflect their room; walking across a polished floor the reflection
   stays roughly in place; doorways blend instead of popping. BG03 water reflects its surroundings (fallback probe).
5. Change the sun time: about a second after letting go, probes re-bake (status line). Toggle lights (K): same.
6. FPS: Off vs Sky vs Probes after baking (expect close to Sky), and during baking (expect a dip). Probes HQ: sharper
   mirrors, fewer probes. Snowdon (many rooms) for the bake time and memory.
7. A model with nothing reflective: status "No reflective surfaces: nothing to bake", no cost.

## 5. B.1: distortion at the foot of glass (2026-10-08)

Gavin's screenshot (BG03 dining, glazing at night reflecting the kitchen): reflections work but distort near the bottom
of the glass. Cause: box projection. Near the glass's base the reflection hits the floor only centimetres away, so a tiny
patch of the capture is stretched over a large part of the screen, and whatever stood between the probe and that floor
(chairs, the island) is painted flat onto it. Changes:

- Box-projected hits are kept at least `PROBE_MIN_HIT` = 0.75 m from the point (less stretch, slightly misplaced).
- Close hits fade: the reflection's weight goes from 1 (hit ≥ 1.5 m) to 0.4 (hit ≤ 0.15 m).
- Smooth surfaces read probes at least half a mip level down (softens magnified texels on big glass and mirrors).
- Probes moved from 1.5 m to 1.7 m above the floor (sees over benchtops, less floor hidden behind furniture).

Inherent and left as is: furniture in a room is never in the right place in a box-projected reflection. The real fix
for big glazing and mirrors is a planar reflection (one extra scene draw per frame for the nearest big mirror-like
plane), parked as a later "High" option.
