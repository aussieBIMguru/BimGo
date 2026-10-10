# BimGo 1.0 build notes: active view only, helper geometry, hide / isolate, screenshots

Follows `ai/261009b_V7/`. Gavin confirmed v7 (linked models) builds and works as intended (2026-10-09); his only fix was the missing `Gl.SRC_COLOR` constant, applied here. Written without a .NET SDK or the Revit API: **not compiled yet**.

## Decisions (Gavin, 2026-10-09)

| Topic | Decision |
|---|---|
| Active view only | Option, **off by default**. When on, **the view decides everything**: every model element it shows, whatever its category tick, phase or design option. Links: what the host view shows of each ticked link |
| IES cones | **Filter at extraction**: Light Source subcategory always; plus subcategories matching editable keywords; view subcategory visibility respected in active-view mode (3D views) |
| Ground plane | 100 mm under the lowest level by default |
| Release extras | Version 1.0 + about; F12 screenshot; visibility remembered in the file; Scan I hide / Shift+I isolate + SHOW ALL |

## Core

- `Scene/LaunchSettings.cs`: `ActiveViewOnly` (false), `SkipHelperGeometry` (true), `HelperSubcategoryKeywords` (light source, clearance, zone, cone, photometric; sanitised, ≤ 40).
- `Scene/CategoryCatalog.cs`: `KEY_OTHER` / "Other (active view)" (FFE group, no built-in categories: only active-view extraction fills it; the Options dialog doesn't offer it).
- `Scene/SceneData.cs`: `SourceView`. `Format/FileModels.cs`: `extraction.activeView`.
- `Format/VisibilityModels.cs` (new): `VisibilitySettings` (hidden category keys, link instance UniqueIds, `HiddenElement` link / uniqueId / id), `VisibilityFiles` sidecar. `visibility.json` entry (written only when something is hidden), `BimGoDocument.Visibility`, `.bimgo-visibility.json` sidecar suffix.

## Revit

- `Extraction/ViewScope.cs` (new): `ShowsModel(view)` (3D, plans, ceiling / engineering / area plans, sections, elevations, details; not templates), `Resolve(uiDoc, doc)` (active view, else the last one used for that model: F5 from another document), `CollectHost`, `CollectLink` (`FilteredElementCollector(doc, viewId, linkId)`, 2024+), `HidesLink` (Revit Links category hidden or the instance hidden in the view), `Count`, `DefinitionOf(element)` (model categories only; not view-specific; excludes rooms, areas, spaces, HVAC zones, link instances, model groups, assemblies, cameras, model lines, light sources and component stair containers; catalog lookup by built-in category, else Other).
- `Extraction/SceneExtractor.cs`: active-view mode gathers per source with `GatherVisible` (bucketed by definition); links hidden in the view are skipped; in a 3D view the host's geometry options use the view (`Options.View`). Helper filter in `Walk`: `IsHelperStyle(GraphicsStyleId)` → `OST_LightingFixtureSource`, or a subcategory (`Category.Parent != null`) whose name contains a keyword; cached per source, logged once per style. `SourceView` recorded.
- `Forms/OptionsWindow`: WHAT TO LOAD (view-only box + description: view name, element estimate, 3D hint; greys the category cards and the heavy box), GEOMETRY → Helper geometry (box + keyword text box), title shows the add-in version, `ViewChoice`. `Cmds_BimGo.ShowOptions` fills it.
- `General/Globals.cs`: version string `Major.Minor.Build`.

## App

- `Native/Gl.cs`: `SRC_COLOR` (build fix), `PACK_ALIGNMENT`, `glReadPixels`. `Native/Win32.cs`: `VK_F12`.
- `Game/GameSession.cs`: ground 100 mm below the lowest level (`GROUND_BELOW_LOWEST_LEVEL`); F12; `_userHidden` in the masks; `InitialiseVisibility`; sidecar flush on dispose.
- `Game/GameSession.Visibility.cs` (new): user hide (`SetUserHidden`, `HideElement`), `ToggleIsolateCategory`, `ShowAll`, `HiddenThingsCount`, load (file / sidecar; host by UniqueId else id, links by instance UniqueId, linked elements by (link, UniqueId)), save (`ToVisibilitySettings`), dirty + delayed sidecar like the sun.
- `Game/GameSession.Screenshot.cs` (new): back buffer read after the scene blit (no HUD), PNG via `System.Drawing` on a worker thread, toast on the next frame.
- `Game/GameSession.Edits.cs` / `Document.cs` / `Menu.cs` / `Render.cs`: user-hidden combined with edit hides (renderer, masks, `IsTargetPresent`); dirty + save include visibility; SHOW ALL button (label cached); category / link toggles record a visibility change; help rows (I · Shift+I, F11 · F12, version).
- `Game/Guns/ScanGun.cs`: I / Shift+I.
- `Program.cs`: version string `1.0.0`. All csproj: `Version` 1.0.0, `AssemblyVersion` / `FileVersion` 1.0.0.0.

## To verify when building

- Revit API: `FilteredElementCollector(Document, ElementId viewId, ElementId linkId)`; `Options.View` (detail level must not be set with it, as done); `GraphicsStyle.GraphicsStyleCategory`, `Category.Parent`, `BuiltInCategory.OST_LightingFixtureSource`, `OST_HVAC_Zones`, `OST_Assemblies`; `View.GetCategoryHidden(ElementId)`, `Element.IsHidden(View)`.
- App: `glReadPixels` with `GL_BGRA` from the back buffer after the blit; `System.Drawing.Bitmap` (System.Drawing.Common is in-box on Windows for net8-windows with WinForms).
- That the light-source cones really sit on `OST_LightingFixtureSource` / a "Light Source" subcategory in Gavin's families (the log lists every subcategory left out). If a family puts them on its main category, a keyword won't catch them: add the subcategory name or fix the family.

## Test checklist

- Options: WHAT TO LOAD shows the active view and estimate; ticking greys the categories; untickable from a schedule / sheet; helper keywords editable and saved.
- Active view only: a 3D view with a section box and hidden categories → only what is visible comes in (design options and phase filter as shown); links: hidden link skipped, visible link contributes only its visible elements; elements in unlisted categories show under Other (active view) in the pause menu; F5 from another document reuses the view.
- IES cones gone from lighting fixtures / devices (log: "helper geometry on … left out"); untick the option → they come back.
- Ground plane 100 mm below the lowest level; no z-fighting at the level's slab faces.
- Scan: I hides (picking / collision / shadows too); Shift+I isolates, again restores; moved / cloned target → message. Pause menu SHOW ALL (n HIDDEN). Save / reopen a .bimgo → hidden state back; live: sidecar written ~1.5 s later, F5 keeps it; files mark unsaved (*).
- F12: PNG in Pictures\BimGo without HUD, correct orientation and colours; toast with the path.
- Version 1.0.0 on the home screen, F1, Options title, logs and file manifests.

---

# 1.0 polish round (2026-10-09)

Gavin: 1.0 builds and works; smoke tested on R25 / R26 / R27 (he will test each from VS before installer / wiki work). Requests: a time note on [ ], a wider help box, progress bars with Cancel for long tasks, and a wording review of all UI text.

## Changes

- `Core/Utilities/OperationProgress.cs` (new): thread-safe stage / bar / detail + cancel, stages as bar ranges (`Begin`, `Step`, `Detail`, `ThrowIfCancelled`, `CanCancel`).
- `BimGoReader.Read` / `BimGoWriter.Write`: optional `progress` (geometry chunks fill the bar; cancel between chunks; a cancelled write deletes its temp file and leaves the target untouched; the final replace can't be cancelled). `Describe` maps cancellation to "Cancelled.".
- Revit: `Forms/ProgressWindow.cs` (new): WPF window on its own STA thread, polls every 100 ms, shows after 0.5 s, Cancel / X cancels, centred over Revit but **not owned** by it (a cross-thread owner would share Revit's busy input queue and freeze Cancel), topmost. `SceneExtractor.Extract(…, progress)`: "Finding elements" (0–8 %), "Reading levels and rooms" (8–12 %), "Extracting geometry" (12–85 %, every 32 elements: bar, "n of N elements · category · link", cancel check); callers write the file in 85–100 %. Used by Go, Export and `LiveDispatcher.Refresh` (cancel → `extract.failed` "The refresh was cancelled in Revit."). `SessionHost.WriteSnapshot` / `Announce` pass the progress on. The progress window closes before any message box.
- App: `Game/ProgressScreen.cs` (new): runs work on a worker thread, draws BIMGO / title / stage / bar / detail / CANCEL (ESC) until done; closing the window cancels (except while saving). Used for opening a file, joining / reloading a live snapshot, preparing the scene (batches, then BVH; cancel returns to the home screen) and saving.
- Sun: [ ] toasts "21 Jun 14:35 · sun 32° high in the NW" (or "below the horizon"), not while the sun panel is open.
- Help panel: columns measured from the text (per UI scale).
- Wording: help rows, Demolish gun (not hammer), "not connected to Revit" (not "no Revit link"), portals "connect", visibility toasts, menu notes about categories, Options dialog notes / validation, Revit status dialog, ribbon tooltips, live read-only notice.

## To verify

- Revit progress window: appears on a slow extraction, Cancel responds while Revit is busy, closes cleanly, no focus fight with the following message box; F5 cancel shows the notice in the app.
- App progress screen: Esc / CANCEL during opening returns home with "Opening … was cancelled."; during scene preparation returns home; during saving shows "Save cancelled…" and the old file is intact; closing the window while saving still saves.

---

# Stair climbing fix (2026-10-09)

Gavin: all Revit versions work; stair climbing "isn't really working" and the step height setting doesn't help.

**Cause.** `CharacterController` is a capsule (radius 0.3 m). A riser top contacts the rounded bottom with a normal of Z ≈ 0.4 (< 0.7 walkable), so it was a wall. The step-up raised the feet by the step height, moved across only this tick's ~3 cm, then dropped: the sphere still hung on the nosing (contact normal Z ≈ 0.46 → wall, no ground), so every attempt was refused, whatever the step height.

**Fix (`Physics/CharacterController.cs`).**
- **Edge riding** in `PushOut` (now an instance method): a contact on the bottom sphere (closest segment point = the bottom centre, contact ≥ 2 cm below it), rising 4 mm–`StepHeight` above the feet, on a triangle whose highest vertex is within `StepHeight` of the feet → lift straight up until the sphere clears the contact point (`centre.Z = contact.Z + √(R² − h²)`), counted as ground. Risers, nosings (square, sloped, rounded), open risers, kerbs and skirting under the step height ride smoothly (≈ 33° ramp feel for 180 mm risers); walls and steep slopes are excluded by the triangle-top test; descending rolls off the edge and the snap-down catches it.
- **Step-up with look-ahead** for risers taller than the sphere can ride (step heights above ~0.27 m): when blocked to < 50 % of the move, probe `max(move, R + 5 cm)` ahead at step height, drop to a floor; if the landing is up by 1 cm–step height, lift the feet to it and make only this tick's move.
- `StepHeight` (Options → max step height) is now the real limit for both.

**Verify.** 150–190 mm stairs walked straight and diagonally, up and down, at walk and run; spiral / winder stairs; monolithic and assembled stairs (open risers); kerbs and thresholds; a 200 mm step height refuses a 250 mm step; no climbing of steep roofs, ramps over 45° or walls; low furniture plinths under the step height are stepped onto (expected).

---

# Saved home, bookmark thumbnails, bookmark cancel (2026-10-09)

Gavin: remember home in the .bimgo and start there; acknowledge Set home; thumbnails in the bookmark list; Esc on a new bookmark must not create it.

- **Home:** `BookmarkDocument.Home` (a `BookmarkRecord`, optional; `IsEmpty` decides whether bookmarks.json is written) / `BookmarkStore.Home` + `SetHome`. `GameSession.SetHomeHere` (Shift+H, SET HOME HERE): player home + store (sidecar saved at once; files marked unsaved), sound, flash, toast, menu label HOME SAVED HERE for 2 s. `Spawn`: reload pose → saved home → active 3D view → random; the load toast says when it started at the saved home. Export carries the bookmarks sidecar (bookmarks + home) into the .bimgo.
- **Thumbnails:** `GameSession.Thumbnails.cs`: `_thumbnailFor` set by B / ADD THIS VIEW / SET HERE; `CaptureThumbnail` after the scene blit (no HUD): back buffer → centre 16:9 crop → 3 × 3 box samples → 192 × 108 → JPEG q72 → base64 `BookmarkRecord.Thumbnail` (`SetThumbnail` saves; pending bookmarks just keep it). `ThumbnailTexture` decodes on first use (System.Drawing → BGRA → GL texture), re-uploads when the string changes, freed on dispose. `UiBatch.Image` flushes, draws the textured quad, continues. List rows 70 px with a 110 × 62 picture (NO PICTURE placeholder).
- **Cancel:** `BookmarkStore.CreatePending` / `AddPending`; B makes a pending record and opens the name box; Enter adds it (named), Esc discards it (no save, no dirty flag); hint "ENTER save the bookmark · ESC cancel it".

To verify: JPEG encoder availability (falls back to PNG), thumbnail orientation and colours, texture upload alignment, the menu label, starting at home in files and live sessions, Esc after B leaves no bookmark and no `*`.
