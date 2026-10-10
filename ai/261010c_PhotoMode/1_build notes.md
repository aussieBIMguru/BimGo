# Photo mode round: build notes

**Baseline:** section box + Fix 1. **Not compiled here** (no .NET SDK). Brackets checked; partials scanned for
duplicate members (none). **GLSL checked** in headless WebGL2: FULLSCREEN_VS + EXPOSURE_FS (new) and every program
changed in the section box round compile and link. Please build and run the Core tests (new `PanoramaTests`: 3).

## Core
- `Scene/Panorama.cs`: `Direction` (equirectangular pixel → scene direction; centre = heading, right turns right, top =
  up), `FaceSize`, `AddPhotoSphereXmp` (GPano XMP in an APP1 segment after JFIF).

## Rendering
- `FpsCamera.SetCustomView` / `ClearCustomView` (forward + up given outright: panorama views incl. straight up / down).
- `RenderTarget.ResolveTo` (MSAA resolve into another target) and `ReadPixels`.
- `SceneRenderer.Photo.cs` + `Shaders.EXPOSURE_FS`: `ApplyExposure(ev)` (DST_COLOR blends; ×2 steps to brighten).
- `Gl`: `MAX_RENDERBUFFER_SIZE`, `DST_COLOR`.

## App
- `GameSession.Render`: the 3D passes moved into `RenderScene(width, height, target, samples, photo)`; `Render` calls it
  for the window, and photo mode calls it off-screen. Photos skip probe baking, highlights, markers and the section
  gizmo; exposure applies while photo mode is open.
- `GameSession.Photo.cs`: open / close (M, Esc; restores the field of view), keys (Enter, RMB look, wheel FOV, [ ]
  sun), the shot two frames after the request (one frame shows "RENDERING…"), `TakeStill` (1–4× the window, capped
  to the GPU limit, MSAA within a 40 M sample budget), `TakePanorama` (six 96° views, stitched on a worker with
  `Parallel.For`, JPEG + XMP), encoding / writing on a worker, OPEN FOLDER (Explorer /select), the panel and the
  thirds grid.
- Photo mode, the section editor, the sun panel and the study close each other; help line for M.

## To verify
See README §8 (Photo mode).
