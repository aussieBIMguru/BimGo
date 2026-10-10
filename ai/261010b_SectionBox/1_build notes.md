# Section box round: build notes

**Baseline:** daylight build B + Fix 1. **Not compiled here** (no .NET SDK). Brackets checked; `GameSession` and
`SceneRenderer` partials scanned for duplicate members (none new). **GLSL checked** in headless WebGL2 (Playwright +
Chromium / SwiftShader, `#version 330 core` → `300 es`): SCENE_VS + SCENE_FS, SCENE_VS + GEOMETRY_FS,
GROUND_VS + GEOMETRY_GROUND_FS, GROUND_VS + GROUND_FS, SCENE_VS + CAP_STENCIL_FS, CAP_VS + CAP_FS all compile and link.
Please build and run the Core tests (new `SectionTests`: 7 tests).

## Core
- `Scene/SectionCut.cs`: box (min / max) + free plane (point, normal towards the cut side), internal metres; `Clean`,
  `LocalPlanes` (scene-local (n, d), box faces first), `IsCut`, `ToPlanes` / `FromPlanes` (six axis planes → box,
  else the first plane).
- `VisibilitySettings.Section`, `BookmarkRecord.Section`, `CommentView.Section` (optional; older files unaffected).
- BCF: `BcfViewpoint.ClippingPlanes` (`BcfClippingPlane` location + direction), written after the camera and read;
  `BcfMapping.ToClippingPlanes` / `ToSection` through the coordinate frame.

## Rendering
- `Native/Gl.cs`: stencil entry points (`glStencilFunc`, `glStencilOp`, `glStencilMask`, `glClearStencil`) and
  constants; `RenderTarget` depth is now `DEPTH24_STENCIL8` on `DEPTH_STENCIL_ATTACHMENT`.
- `Shaders.cs`: `CLIP_GLSL` (≤ 7 planes) in SCENE_FS and the geometry pre-pass; `CAP_STENCIL_FS`, `CAP_VS`, `CAP_FS`.
- `SceneRenderer` is now partial; `SceneRenderer.Section.cs`: `SetSection`, `ApplyClip` (scene passes with
  `SceneDrawParams.Section`, the AO pre-pass), `DrawSectionCaps` (per facing plane: stencil parity pass, optional
  back faces in element colour, cap polygon). Shadows, light shadows, probes and the minimap pass no cut.

## App
- `GameSession.Section.cs`: state, `ApplySection` / `RestoreSection` / `ClearSection`, `QuickSectionPlane` (follows the
  view ray through the aimed element), fits (level / room / model), the editor (handles, drag along the axis with
  0.05 m snap, Shift free), the gizmo (box edges, plane ring, handle dots) and the panel.
- `Pick` / `PickExcluding` skip cut hits (`PickOnce` is the old single pick); the study's surface clicks use
  `PickStaticVisible`.
- Keys P / Shift+P / Ctrl+P; help line; the editor, sun panel and study close each other.
- Bookmarks and comment views store the cut and GO restores it; BCF export / import carries it.
- Visibility save / restore includes the cut.

## To verify
See README §8 (Section box).

## Fix 1
- Build error CS0136 in `BcfFile.ReadViewpoint` (`direction` reused for clipping planes): renamed `clipDirection`.
- Element-colour caps removed: they drew the cut solids' back faces cut by one plane only, so colour leaked past the
  other box faces (Gavin's screenshot). Caps are now always one **flat colour**, chosen in the panel (swatch,
  CHANGE… = Windows colour picker via `FileDialogs.PickColour`, RESET = #3D4045), saved as
  `LaunchSettings.SectionCapColour`.
