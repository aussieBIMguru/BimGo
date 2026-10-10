# BimGo — Build notes: rendering round 2 (artificial lights and glow)

**Status:** written, not compiled (no .NET SDK or Revit API in the authoring environment). GLSL compiled, linked and run in WebGL2 (ANGLE / SwiftShader) on a two-room test scene; C# reviewed by eye. Gavin to build in Visual Studio, run the Core tests, and try on a model with lighting fixtures.

Round 1 (AO) built and confirmed working (Gavin, 2026-10-09).

## Asked for (Gavin, 2026-10-09)

- Glow from Revit's own emissive / self-illumination channel where materials have one.
- An option at startup and while running for artificial light glows.
- Casting: limited per room or quick falloff is fine as long as it looks reasonably realistic; balance of performance vs realism left to Claude.

## What was chosen

| Piece | Choice | Why |
|---|---|---|
| Glow source | Appearance-asset self-illumination (Generic luminance / filter / colour temperature; Advanced `opaque_luminance` unless `opaque_emission` off), anywhere | Revit's own channel, as asked |
| Fallback 1 | Material-name keywords, **inside Lighting Fixtures only** | Most library fixtures have no self-illumination set; restricting to fixtures avoids "Light Grey Paint" glowing |
| Fallback 2 | Raised fixture with neither: bottom 15 % downward faces = lens | Downlights / panels still glow; floor lamps excluded (≤ 1.2 m above level) |
| Light per fixture | One point light at the glowing surface's centre, downward share from its facing | Cheap, predictable, one per element (follows moves / hides / clones) |
| Output / colour | Parsed from "Initial Intensity" / "Initial Color" display text (lm, cd, W @ lm/W; K), else 1000 lm / 3500 K | Parameter display text is the only route stable across 2025–2027 without opening families |
| Light count | Nearest 32 in view per frame, farthest fade when more | Fixed cost per pixel; no clustered lighting needed |
| Containment | Clip each light to its room's box (8 cm past walls) | Stops light through walls without shadow maps; rooms are already extracted |
| Look | Windowed inverse square, omni + downward cosine, 6 % room fill (bounce stand-in, × AO), 0.8 shoulder | Night interiors read lit, ceilings not black, pools don't clip flat |
| Bloom | Pre-pass second target (MRT) → quarter res → 13-tap blur → additive | Reuses the AO pre-pass (no extra geometry pass); occluded glows don't bloom |
| Daylight | Light × 0.35, glow × 0.6 at full day | Fixtures don't wash out sunlit interiors |
| Calibration | 100 lx ≈ full albedo | Night-adapted eye; 1 500 lm panel gives a convincing pool (test scene) |

## Files

| File | Change |
|---|---|
| `BimGo.Core/Scene/LightingData.cs` (new) | `LightingData`, `EmissiveRun`, `LightSource`; `PackEmissive`, `StrengthFromLuminance`, `KelvinToRgb` |
| `BimGo.Core/Scene/SceneData.cs` | `Lighting` (never null) |
| `BimGo.Core/Scene/LaunchSettings.cs` | `ArtificialLightMode` enum; `ArtificialLights` (default Lights), `ArtificialLightIntensity` (0–2), `EmissiveKeywords`; sanitised |
| `BimGo.Core/Format/*` | Optional `lighting.json` (`LightingDto`, `LightDto`); writer only when non-empty; reader validates (ordered, in-range runs; real elements; finite, clamped values) |
| `BimGo.Revit/Extraction/SceneExtractor.cs` | partial; per-vertex emissive list; `MaterialLook` cache (colour + self-illumination + keyword); emissive meshes forced opaque; hooks for fixtures |
| `BimGo.Revit/Extraction/SceneExtractor.Lighting.cs` (new) | asset reading, keywords, fallback lens, emissive runs, fixture lights, parameter parsing |
| `BimGo.Revit/Forms/OptionsWindow.xaml(.cs)` | Display → **Artificial lights** (Off / Glow only / Glow + light) |
| `BimGo.App/Rendering/ScreenEffects.cs` (was `AmbientOcclusion.cs`) | + glow target (MRT), bloom down / blur / composite; AO unchanged |
| `BimGo.App/Rendering/ArtificialLighting.cs` (new) | per-frame packed light arrays, emissive and bloom strengths |
| `BimGo.App/Rendering/Shaders.cs` | `aEmissive` (location 3), `LIGHTS_GLSL` (lights, bounce, shoulder) in scene + ground, glow output in the pre-pass, `GLOW_*` shaders |
| `BimGo.App/Rendering/SceneRenderer.cs` | emissive VBO on both VAOs; `UpdateScreenEffects(ao, glow)`; `CompositeGlow`; light uniforms |
| `BimGo.App/Game/GameSession.Lights.cs` (new) | mode / brightness, room boxes, per-frame pick, K, sun-panel rows |
| `BimGo.App/Game/GameSession(.Render/.Sun).cs` | init / save, render order (lights → effects → scene → glass → bloom), failure handling, K, help row, sun panel 600 high |
| `BimGo.App/Native/Gl.cs` | `DrawBuffers`, `Uniform4(location, count, Vector4[])`, `RGBA16F`, `COLOR_ATTACHMENT1` |
| `tests/BimGo.Core.Tests/LightingTests.cs` (new), `TestData.cs` | lighting round-trip etc.; `BuildScene/BuildDocument(lighting)` |

No new packages. BimGo.Revit stays package-free.

## Test renders (WebGL2 harness, 960 × 540)

Two 5 × 8 m rooms, partition with a doorway, a 600 × 600 ceiling panel (1 500 lm, 3 500 K, downward 0.8) in each; AO on.

- Night, glow only: panel glows with bloom; room dark (sky ambient only).
- Night, glow + light: warm pool on the partition and table, ceiling softly filled, the other room lit through the door, partition's far side unaffected by this room's light.
- Day: lights add a subtle warm tint; panel still glows.
- First calibration (300 lx = full) was too dim; raised to 100 lx; then added the 6 % room fill (black ceilings looked fake).

## To check in Visual Studio / Revit

1. Builds; Core tests pass.
2. On a model with lighting fixtures, read the log line `Lighting: N fixture lights (M estimated), … self-illuminated materials, … guessed lenses`. If almost all are estimated, the parameter names / text differ: send a sample "Initial Intensity" display string.
3. Glow: library downlights / panels glow; nothing else does unexpectedly (if a keyword is too broad, edit `EmissiveKeywords` in settings.json).
4. K cycles; sun panel mode and brightness; Revit Options launch mode; settings persist.
5. Night (sun on, after dark) vs day; whitecard and material colour; MSAA; moved / cloned / demolished fixtures; hidden Lighting Fixtures category turns lights off.
6. FPS with lights on vs off on a large model (32-light loop per pixel; the pre-pass is already paid for when AO is on; with AO off, glow alone still runs the pre-pass).
7. Tuning: `LIGHT_SCALE` (overall), radius `√lm × 0.16` clamped 2.5–9 m, `LIGHT_BOUNCE` 0.06 (shader), bloom 0.9 × glow, daylight factors — all in `GameSession.Lights.cs` / `Shaders.LIGHTS_GLSL`.

## Next

Round 3 per the agreed order: HDR target + tone mapping (would replace the 0.8 shoulder), then the material table and triplanar textures. Point-light shadows remain out of scope.


## Revision (2026-10-09): shadow maps replace room clipping; bloom control

Gavin: room clipping gave very pronounced door thresholds. Options talked through: soft room edges (threshold only blurred), falloff only (leaks through walls), cached shadow maps. Chosen: **cached shadow maps**.

- `Rendering/LightShadows.cs` (new): 32 slots × 6 faces × 256 px, DEPTH_COMPONENT16 texture array with comparison (~25 MB), unit 5. Faces: ±X, ±Y (up Z), ±Z (up Y), 90° × 1.03 padding, near 0.08 m, far = light radius. Slots keyed by light identity (light index; moved / cloned: instance id << 32 | index), LRU reuse. Re-render when never rendered (first), or when moved / scene key changed (stale map used meanwhile); 4 lights per frame; a light without a map is left out and fades in over 8 frames.
- `SceneRenderer.UpdateLightShadows` renders the faces with the sun-shadow depth program (opaque batches + dynamics, chunk-culled to the face frustum, polygon offset 1.5 / 3), then drops lights without maps. GPU refusal → lights unshadowed, toast once.
- `LIGHTS_GLSL`: room-box test removed; `lightVisibility` picks the face from the major axis, normal offset 1.5 cm + 1.2 % of distance, 4-tap PCF. Fill: `0.06 × window × ao × mix(0.35, 1, visible)`.
- `ArtificialLighting`: `BoxMin/BoxMax` → `Shadow` (x first layer or -1, y fade) and `Key`; `RemoveAt`.
- `GameSession.Lights`: room boxes gone; keys per picked light; bloom slider (`SLIDER_BLOOM`), `_bloomIntensity` saved as `BloomIntensity`; RESET LIGHTING resets both.
- WebGL test: table casts a soft shadow, light from the next room spills through the doorway in a natural wedge, partition blocks it, no acne.
- To check: FPS while walking into new areas (4 lights × 6 faces per frame at most) and while dragging a fixture with the gizmo (its map re-renders every frame); shadow acne / peter-panning on real geometry (tune the normal offset and the 0.0004 depth bias in `lightVisibility`, polygon offset in `UpdateLightShadows`).
