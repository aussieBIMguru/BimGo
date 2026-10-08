# BimGo UX cleanup — build A (app): hide-UI, quality profiles, reflections in the pause menu

**Baseline:** `BimGo_1.0_Reflections_BuildB1` (Gavin's confirmed working copy). Decisions: handoff §9.
**Not compiled here** (no .NET SDK reachable from Claude's sandbox). The changed files were checked by reading the
diff and for balanced braces / brackets only. Expect possibly a small compile fix; please run the Core tests
(new `QualityProfileTests`, 7 tests).

## What changed

### 1. Hide-UI mode (U)
- `GameSession.cs`: `_uiHidden`, `ToggleUiHidden()`, `ShowUi()`. **U** toggles (checked after the pause gate,
  before the gun-capture branch, so it works while Gizmo / Clone hold the keys; no gun uses U).
- **Esc while hidden** only shows the UI (`escape = false` for that press), so a locked Gizmo is not cancelled and the
  menu doesn't open. Pausing (`SetPaused(true)`, incl. focus loss) and opening the sun panel also show the UI.
- Hidden: status panel, coordinate panel, minimap (2D frame and the 3D plan draw), crosshair and "Click to look"
  hint, gun panel, gun bar, help, sun icon, room / live banners, gun world markers (`_overlay`) and labels, gun
  highlight tints. Kept: the portal flash, the comment / bookmark text box, important toasts.
  Gizmo / Clone keep their tint while they capture input (so a held element stays visible).
- F12 screenshots and bookmark thumbnails are unchanged (they never had UI).

### 2. Important toasts
- `Toast(message, seconds = 2.6f, important = false)`. While hidden, ordinary toasts are **dropped** (not queued),
  so they can't replace an error still on screen; `BuildHiddenHud` draws the current toast only if important.
- Marked important (about 25 call sites): error-sound toasts in the guns (can't move / clone / demolish, Revit refused,
  invalid teleport), save failures (not a cancelled save), sidecar write failures (sun, visibility, comments,
  bookmarks), shadow / light-shadow / screen-effects / probe failures, screenshot failure, Revit notices, "Not
  connected to Revit", redo-not-in-walkthrough, "moved elements can't be hidden".
- Deliberately **not** important: the screenshot-saved confirmation, comment / bookmark saved, snap changes, sun time
  steps. If silent F12 feels wrong while hidden, flip `GameSession.Screenshot.cs` line ~89 to `important: true`.

### 3. Quality profiles
- Core `Scene/QualityProfiles.cs`: `enum QualityProfile { Custom, Basic, Medium, Realistic }` and
  `QualityProfiles.Apply / Matches / Detect` (allocation-free). `LaunchSettings.QualityProfile` (new, additive),
  re-detected in `Sanitise()` and in `Save()`, so it always matches the values whoever saved (app or Revit).
- Values as handoff §4.5, with §9's decisions: profiles set **shadow quality only** (Basic Low / Medium / High);
  shadows on/off (O, per-model) untouched. Probes HQ never in a profile (choosing HQ after Realistic reads Custom).
  Basic turns reflections off but keeps the threshold / source underneath.
- Realistic on a snapshot without textures: the Realistic flag is kept (the app already shows material colours
  and says why), so the next Go remembers Realistic. This differs slightly from §4.5's "Material if no textures";
  the screen result is the same.
- App `GameSession.Profiles.cs`: `WriteProfileValues`, `CurrentProfile()` (reuses one scratch `LaunchSettings`),
  `ApplyProfile()`. The shown profile is recognised from the live values each frame, so any manual change shows
  **CUSTOM**; settings save whichever it is.

### 4. Pause menu right column (tabbed)
- `GameSession.Menu.cs`: `BuildDisplayCard` → `BuildRightColumn`: **QUALITY PROFILE** label (+ CUSTOM tag) and a
  Basic / Medium / Realistic segmented control (no selection when Custom), then a `Tabs` widget
  (**DISPLAY · REFLECTIONS · DEBUG**, per session, not saved), then one card of fixed height `S(440)` (was `S(668)`;
  the whole column is now ~588 px scaled instead of ~742).
- Display tab: unchanged controls (ground, colour, AA, FOV, sensitivity, VSync, Invert Y, Show FPS, AO).
- Reflections tab (`GameSession.Reflections.cs` → `BuildReflectionsTab`): *Reflections* **Off / Some / All**,
  *Source* **Sky / Probes / Probes HQ**, *Reflection strength*, probe status + REFRESH, a short note.
  The old combined Reflect row (Off / Sky / Probes / HQ) is split into the two controls; same fields underneath.
- Debug tab: *Debug colours* **Off / Reflection / Probes** with the legend as text (the old legend toasts are gone).

### 5. Sun panel
- Reflect row removed; height `S(600)`; title "SUN, SHADOWS & LIGHTS".

### 6. Docs
- README §3 (U row, Esc row, right column), sun panel paragraph (lights), changelog entry. Help panel: U row.

## Test checklist (Gavin)
1. U hides everything; walk, fire guns, F12 → clean PNG; U or Esc brings it back; Esc didn't also pause.
2. Gizmo locked on, U, Esc → UI back, element still held; Esc again cancels.
3. While hidden: try to Gizmo a locked element → the error toast shows. Bookmark (B) → name box shows.
4. Pause menu: pick each profile, look at Display / Reflections tabs and the sun panel's quality and lights.
   Change one value → CUSTOM. Restart → the same profile / Custom shows.
5. Reflections tab: Some / All, Sky / Probes / HQ, REFRESH; Debug tab colours.
6. Small window (e.g. 1280 × 720 at 125 %): right column fits.

## Files
- App: `GameSession.cs`, `.Menu.cs`, `.Reflections.cs`, `.Render.cs`, `.Sun.cs`, new `.Profiles.cs`; error flags in
  `.Document.cs`, `.Live.cs`, `.Screenshot.cs`, `.Visibility.cs`, `Guns/{Clone,Comment,Gizmo,Hammer,Teleport}Gun.cs`.
- Core: `Scene/LaunchSettings.cs`, new `Scene/QualityProfiles.cs`. Tests: new `QualityProfileTests.cs`.

## Next: build B (Revit)
Options window tabs (7, last tab remembered; colour mode → Materials tab; profile + "also write beside the model"
option in Player; Realistic profile ticks extraction), sidecars and texture overrides in
`%LocalAppData%\BimGo\Models\<title>_<hash>\` with copy-migration, Core tests.
