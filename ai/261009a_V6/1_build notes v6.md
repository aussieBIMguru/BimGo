# BimGo v6 build notes: sun, shadows and time of day

Follows `ai/261008_V5.1/0_BimGo v5.1_Handoff.md` (v5.1 built and confirmed by Gavin, 2026-10-05; this round started from Claude's v5.1 copy as Gavin reported no build changes). Spec agreed in chat: project doc `claude/BimGo_v6_Sun_Spec.md`. Written without a .NET SDK, the Revit API or a GLSL validator: **not compiled yet**.

## Decisions (Gavin, 2026-10-05)

| Topic | Decision |
|---|---|
| Default | Shadows **off**; O toggles; off stops all shadow work and restores the classic light |
| Panel | Bottom-right sun icon expanding into a panel; **Shift+O** (or clicking the icon with the cursor free) opens it and frees the cursor; scene keeps rendering live |
| Time | Slider, 5-minute steps (Shift = 1 min), fluid; play animates the day |
| Date | Month and day text boxes, clamped |
| Sun source | Revit **SiteLocation** + the launch view's **Sun Settings** start, fallback today 12:00; DST as a "+1 h" checkbox |
| Persistence | Sun state **in the file** (+ live sidecar); **bookmarks keep sun time**; **quality per machine** (settings + Options dialog) |
| Glass | **Per-material, tinted** transmission from Revit transparency, scaled by a global slider |
| Controls | Sunlight, sky / diffuse, shadow intensity, light through glass |
| Maths | Gavin's `Pickles.Helpers.SunPosition`, ported to Core with NOAA declination / equation of time |

## Core

- `Scene/SolarPosition.cs`: `GeoLocation` (lat, lon, standard time zone, name; `Fallback` = Sydney), `SolarPosition.Compute` (altitude, azimuth clockwise from true north; NOAA fractional-year series; same azimuth formula as `GetSunVector`), `Direction` (X east, Y north, Z up), `ToModel` (rotate by −internal→shared angle, matching `GetRevitSunVector`'s −true north), `NorthAngle(site)` (the v5.1 extraction-verified `SharedAngle`, else derived), `LocationOf(site)`.
  - Checked in Python against Gavin's formula: Sydney 21 Dec 12:00 → 79.4° / 351° (Gavin 79.5° / 352°); London 21 Jun 12:00 → 61.95°; Adelaide 20 Mar 15:00 within 0.3°.
- `Scene/ModelInfo.cs` `SiteInfo` (additive): `HasLocation`, `Latitude`, `Longitude`, `TimeZone`, `PlaceName`, `SunStart` ("yyyy-MM-ddTHH:mm").
- `Scene/LaunchSettings.cs`: `ShadowQuality` enum (Low / Medium / High) + setting (default Medium, sanitised).
- `Format/SunModels.cs`: `SunTime` (month, day, minutes, DST; `Clamped`, `StartFor(site)`), `SunSettings` (enabled, time, sun / sky / shadow / glass; `Clean`, `Copy`, `DefaultsFor`).
- `Format/SidecarJson.cs`: shared sidecar read / atomic write (`BesideComments`), `SunFiles` (`<model>.bimgo-sun.json`). `BookmarkFiles` now uses it.
- Format (additive, `formatVersion` 1): `sun.json` entry (written when the document has a sun state), `BimGoDocument.Sun`; `BookmarkRecord.Sun` (optional `SunTime`).

## Revit

- `SceneExtractor.BuildSite(uiDoc)` → `CaptureLocation`: `Document.SiteLocation` (radians → degrees, `TimeZone`, `PlaceName`) and `ActiveView.SunAndShadowSettings.StartDateAndTime` (converted from UTC by the site time zone when `Kind == Utc`). Logged.
- Options dialog: PLAYER & DISPLAY row 6 **Shadow quality** (Low / Medium / High).

## App: rendering

- `Native/Gl.cs`: `TEXTURE_2D_ARRAY`, depth-compare constants, `NONE`, `OUT_OF_MEMORY`; `TexImage3D`, `FramebufferTextureLayer`, `ColorMask`, `DrawBuffer`, `ReadBuffer`.
- `Rendering/ShadowMaps.cs`: presets (Low 1 × 2048 / 60 m / hard; Medium 3 × 2048 / 120 m / 3×3 PCF; High 4 × 3072 / 200 m / 5×5 PCF); depth array (`DEPTH_COMPONENT24`, `COMPARE_REF_TO_TEXTURE`, linear = hardware PCF) + RGBA8 transmittance array (only when the model has transparent batches; else 1×1 white); 1×1 placeholders until first use; `Ensure` / `Release`; `Fit` (practical split λ 0.8, bounding sphere per slice, radius rounded, centre snapped to texels in light space, depth from scene casters ±5 m margin); dirty per cascade (sun / scene key / glass → all; walking → cascade c every (c+1)th frame); shaders sample with `RenderedMatrices`. FBO with draw/read buffer NONE when there is no glass (GL 3.3 completeness). Allocation errors → `LastError` (OOM message suggests a lower quality).
- `Rendering/SunLighting.cs`: per-frame sun / sky colours from altitude (warm low sun, twilight / night sky gradients, ambient never below 0.12, sun disc), intensities, shadow strength, glass.
- `Rendering/Shaders.cs`: shared `SUN_GLSL` block (cascade pick by view depth, normal offset, PCF loop, transmittance, distance fade, `sunLight`) inserted into `SCENE_FS` and `GROUND_FS`; sky gets uniforms for gradient + sun disc; new `SHADOW_VS`, `SHADOW_DEPTH_FS`, `SHADOW_TRANSMIT_FS` (transmission = (1 − alpha) × normalised tint (65 %) × glass slider; whitecard = untinted).
- `Rendering/SceneRenderer.cs`: `Lighting` (set per frame), `FogColour`, `UpdateShadows(...)` (before the scene target is bound), `RenderCascade` (opaque depth with polygon offset 1.5 / 3, then glass with depth LESS, no depth writes, blend ZERO / SRC_COLOR), `ApplyLight` for scene + ground (transparent pass doesn't sample transmittance, so glass doesn't tint itself); `DrawBatches` / `DrawDynamicInstances` shared by the colour and shadow passes. `SceneDrawParams.Sun` (false for the minimap).

## App: session

- `Game/GameSession.Sun.cs`: state load (file → live sidecar → defaults from the site), `RecomputeSun`, `SunChanged` (revision, title, delayed sidecar), `ToggleShadows` (O), `StepSunTime` ([ ]), `ApplySunTime` (bookmarks), play (60 clock-min / s), `ShadowSceneKey` (static hide / category revision + whitecard + hash of active dynamic instances, allocation-free), `OnShadowFailure` (switch off + toast).
- Panel: sun icon (bottom right, amber when on; click with the cursor free opens; click again closes), panel above it (header + place, shadows checkbox + quality segmented, time slider + play, month / day boxes + month name + DST, sun height / bearing, four intensity sliders in 5 % steps, RESET LIGHTING). While open: cursor free, player frozen, gun context panel hidden; keys Esc / O close, [ ] step, Space play, digits / Backspace / Enter / Tab / ↑ ↓ in the date boxes, F11 still works.
- `GameSession.cs`: O / Shift+O, [ ] (shadows on), panel key routing, sun icon click in `UpdateMouseLook`, `SetPaused` keeps the cursor free while the panel is open, frozen physics, `ShadowQuality` saved to settings, sidecar flushed on dispose. `RefreshMasks` / `SetStaticHidden` bump `_sceneRevision`.
- `GameSession.Render.cs`: lighting + `UpdateShadows` before the scene pass; clear colour = `FogColour`; help rows.
- `GameSession.Document.cs`: dirty / save include the sun state; `UpdateSun(dt)` in `PollHost`.
- Bookmarks: `Add` / `Update` take the sun time when shadows are on (SET HERE with shadows off clears it); GO restores it and turns shadows on; list detail shows "SUN d/m hh:mm".

## To verify when building

- Revit: `SiteLocation.PlaceName`, `View.SunAndShadowSettings`, `StartDateAndTime` kind / time zone (the log line "Site: … sun start …" vs Revit's Sun Settings dialog).
- GL entry points above; GLSL compile on NVIDIA / AMD / Intel (no validator was available): `sampler2DArrayShadow` `texture(…, vec4)`, dynamic indexing of `uShadowMat[c]` and `uCascadeFar[i]`, texture sampling inside the `ndl > 0` branch (no mipmaps, so derivatives don't matter).
- `HashCode.Add<Vector3>` / `<float>`; `Enum.IsDefined(ShadowQuality)` where the property shares the type's name.
- Sun direction: compare with a Revit sun study (same date / time / location) on a model with project north ≠ true north. If shadows point the wrong way, the sign of `SolarPosition.ToModel` is the place to look (it uses the v5.1 shared angle, verified against the survey point).

## Test checklist

- O on / off: classic light returns exactly when off; VRAM drops (maps freed).
- Shift+O: panel opens, cursor free, player still; Esc / O / icon close; walking resumes with the mouse captured.
- Time slider: drag smoothly; Shift for 1 min; [ ] steps; play runs the day, Space / button pause on the nearest minute; sun disc and sky colour follow; below the horizon only sky light.
- Date boxes: type 13 → 12, day 31 in Feb → 28/29, Tab month → day, ↑ ↓ step, click away commits.
- DST: shadows shift by one hour.
- Intensities: sunlight 0 → no direct light; shadow intensity 0 → no visible shadows; glass 0 → windows cast full shadows, 200 % → brighter patches; tinted glass tints the floor; whitecard → untinted.
- Quality: Low / Medium / High switch live; check seams between cascades, acne on floors and walls, peter-panning at wall bases, shimmering while walking (should be none), performance on a large model while walking vs standing still (standing still should cost nothing extra).
- Moved / cloned / demolished elements update their shadows; category toggles too.
- Save, reopen → sun state back; older BimGo opens the file. Live: `<model>.bimgo-sun.json` written ~1.5 s after changes; F5 keeps the state.
- Bookmarks: with shadows on, B stores the time; GO restores it; SET HERE with shadows off clears it.
- Options dialog: Shadow quality saved and used by the walkthrough.
- A file exported before v6: panel says "Sydney assumed".
