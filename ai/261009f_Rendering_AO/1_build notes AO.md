# BimGo — Build notes: rendering round 1 (ambient occlusion)

**Status:** written, not compiled (no .NET SDK in the authoring environment). GLSL compiled, linked and run in WebGL2 (ANGLE / SwiftShader) on a test room; C# reviewed by eye. Gavin to build and check in Visual Studio.

## Why this round

After 1.0 we reviewed GI, artificial lighting and texturing for feasibility (Gavin, 2026-10-09). Agreed approach, in order, each round reversible:

1. **SSAO** (this round): most of the "GI look" in a BIM walkthrough for a fraction of the cost; no format change.
2. **HDR target + tone mapping / exposure, proper night state**: prerequisite for anything emissive.
3. **Material table**: optional `materials.json` entry (name, colour, transparency, emissive flag, texture class, luminance) + a per-vertex / per-batch material index, additive (the reader skips unknown optional entries; `formatVersion` stays 1 if possible).
4. **Emissive by keyword** (lamp / LED / light / emissive, Lighting Fixtures category; optional bloom) and **keyword-mapped triplanar textures** (a dozen CC0 tileables, world-space projection tinted by material colour; no UVs, no extraction rewrite).
5. **Point lights from Lighting Fixtures**: clustered / tiled light list, short falloff radius and / or room-limited, no point-light shadows.

Deferred, probably indefinitely: probe-grid sun bounce GI, faithful Revit textures (`CustomExporter` UVs + appearance-asset bitmaps), real-time GI.

## What changed

| File | Change |
|---|---|
| `Rendering/AmbientOcclusion.cs` (new) | Half-res geometry target (RGBA32F view normal + view depth, own depth RB), AO + two blur programs, RG16F ping-pong, 1×1 placeholder, `Ensure` / `Release` / `BeginGeometry` / `Compute` / `Bind`. Never throws; `LastError` on failure. Constants `RADIUS` 0.6 m, `INTENSITY` 3.5, `MAX_DEPTH` 120 m, `UNIT` 3. |
| `Rendering/Shaders.cs` | `AO_GLSL` (joint bilateral 2×2 upsample) in scene + ground FS; `sunLight(n, world, eye, ao)` multiplies the sky term only; classic light multiplies its ambient part; ground without sun `*= mix(1, ao, 0.6)`. New `GEOMETRY_FS`, `GEOMETRY_GROUND_FS`, `AO_FS`, `AO_BLUR_FS`. |
| `Rendering/SceneRenderer.cs` | Geometry programs, `UpdateAmbientOcclusion` (opaque batches + dynamics + ground with the camera's culling, then `Compute`), `DisableAmbientOcclusion`, `ApplyAo` (on for opaque + highlights, off for glass and plan). `GROUND_HALF` const. |
| `Game/GameSession.Render.cs` | AO step after shadows, before binding the scene target; `OnAmbientOcclusionFailure` (switch off, error sound, toast). |
| `Game/GameSession(.Menu).cs` | `_ambientOcclusion` loaded / saved; checkbox under Show FPS; card 452 → 480. |
| `Native/Gl.cs` | `FramebufferTexture2D` (+ required entry point), `RGBA32F`, `RG16F`, `RG`, `HALF_FLOAT`. |
| `BimGo.Core/Scene/LaunchSettings.cs` | `AmbientOcclusion` (default true). |
| `tests/.../LaunchSettingsTests.cs` | Default on; older settings JSON reads as on; false round-trips. |

No new packages. BimGo.Revit untouched (no Options dialog entry; the pause-menu toggle is enough for now).

## Design notes

- **Pre-pass, not post-process.** Applying AO before lighting lets it darken only ambient light (a post multiply would also dim sunlit surfaces and glass). It also sidesteps MSAA: the scene target's renderbuffers can't be sampled, and the pre-pass has its own single-sample target.
- **View-space basis** (right, up, forward) from `FpsCamera`, the same basis `ShadowMaps.Fit` uses; normals are flipped to face the eye (robust to Revit winding).
- **Estimator:** SAO-style spiral taps with a bounded per-tap term `max(dot(v,n) − bias, 0) / |v| × (1 − |v|²/r²)`, so `INTENSITY` reads directly (3.5 ≈ 0.6 in a 90° inside corner on the test room). Screen radius capped at 12% of the height so walls at arm's length don't thrash the cache.
- **Noise:** 4×4 ordered rotation; the ±4-tap separable blur spans its period, so flat areas come out clean.
- **Edges:** blur and upsample both weight by relative depth (`0.02·z + 0.03` m), so AO doesn't bleed across silhouettes; the upsample falls back to the closest-depth texel.

## Test results (WebGL2 harness, 960 × 540)

- Flat floor / wall: 254–255 / 255 (no self-occlusion).
- Inside corners: ~0.6 at intensity 3.5 (1.6 gave ~0.8: too subtle).
- Box on floor: soft contact shadow, crisp against the wall behind (no halo).

## To check in Visual Studio / on hardware

1. Builds (C# never compiled). Likely trouble spots: none known; the local static function `ApplyGeometry` after `return` in `UpdateAmbientOcclusion` is valid C#.
2. Core tests pass (new `AmbientOcclusion_OnByDefaultAndForOlderSettingsFiles`).
3. Visual: corners, stairs, furniture, ceilings; with sun on and off; whitecard and material colour; MSAA 0 / 2 / 4; window resize; minimap unaffected; glass unaffected.
4. **Cost:** FPS with AO on vs off on the largest model. If the second geometry pass hurts, options: skip far chunks in the pre-pass, lower `MAX_DEPTH`, or quarter resolution.
5. If too strong / weak: `INTENSITY` (and `RADIUS` for the reach). Thin-wall halos: lower the blur tolerance.
6. A GPU that can't make RGBA32F targets should toast and switch off, not crash.
