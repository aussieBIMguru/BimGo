# BimGo next round — build notes (probe leaks, family library + Place gun, drop to surface, README)

**Baseline:** `BimGo_UX_BuildB.zip` (Gavin: no local changes; UX build B not compiled yet when this round started).
**Not compiled here:** no .NET SDK or NuGet reachable from Claude's workspace, and no Revit API. The C# was reviewed
by eye, and every changed file was checked for balanced brackets; the Options XAML parses as XML. The GLSL was
compiled and linked in headless WebGL2 (Chromium + SwiftShader: scene, ground and shadow programs), and the new probe
cell lookup was run on a test grid (a wall on a cell boundary reads the room it faces from both sides). Please build,
then run the Core tests (new `LibraryTests`, 9 tests).

Decisions (Gavin, this chat): all of the handoff's recommendations for the family library (loaded types only, FFE /
services categories, rolled-back temporary instances behind an opt-in tick with a cap, Revit previews, level-based
only with hosted types greyed, live sessions only, journal op `place`); drop to floor = **the first hit under the
bottom centre of the box** (so things land on desks), also with aim + F on the Gizmo gun, and lifts sunk elements.
Dates: everything dated after today (2026-10-09) became 2026-10-09; same-day `ai/` folders got a/b/c… suffixes.

## 1. Probe leaks between rooms (`Rendering/ReflectionProbes.cs`, `Rendering/Shaders.cs`)

All four causes from the handoff, none with a per-frame cost (the grid is built once per placement; the shader adds
one multiply-add):

| # | Cause | Fix |
|---|---|---|
| 1 | The neighbour's probe blended ~1 m in from every wall | `BuildGrid` blends only across **open** boundaries: a horizontal ray across the gap at the band's height (kept 0.15 m above the higher floor, below the lower top of the two rooms) hits nothing in the occluder set, or the gap's middle lies in a **door's box** (grown 0.15 m in plan). Occluders = the static BVH with a mask from `GameSession.ProbeOccluderMask()`: everything except doors and movable elements. Two probes of one large room always blend. Boundaries are found across up to 1 m of roomless cells (wall thickness), +X / +Y only, spread both ways |
| 2 | Walls / glass / floors sample the cell they straddle | `probeCell(world, n)` looks the cell up at `world + n × max(0.3, 0.75 × cell)` (n = the viewer-facing normal; water's rippled normal too). Box projection still uses the true point. The debug colours (Probes) use the same lookup |
| 3 | Height bands took any room within ±0.3 m | New `AssignRooms`: every room (with or without a probe) claims the cells whose centre is inside its plan; the room whose floor is nearest **below** the centre wins (5 cm tolerance below, up to 1 m above its top). Bands halved to 0.5 m. Cells in a room only take that room's probes (a room without one shows the sky); fallback probes only take roomless cells. `RoomIndex.Find` (reflective voxels → room) also prefers the nearest floor below |
| 4 | Coarse cells on big models | The grid log line now reports size, cell, band, open / closed boundary counts, ray or doors-only mode and build time; a separate line when the cells grew past 0.75 m. No sparse grid yet (not needed unless the log shows big cells) |

Memory while building: up to ~64 MB of temporary arrays at the 4 M-cell cap (released after).
**Check first:** pause menu → Debug → Probes on the test model: walls should change colour exactly at the wall, doorways
should show a soft blend, room separation lines too (no geometry there).

## 2. Drop / lift to surface (F)

- `GizmoController.DropToSurface`: one ray straight down from the **bottom centre of the box**, starting
  `min(0.3 m, half the height)` above the base (so a sunk element is lifted), up to 10 m below; static scene (pick
  mask) and other dynamic instances, never the element itself (`GameSession.PickExcluding`, `DynamicSet.Raycast(…,
  exclude)`). The first hit wins: desk, bench, then floor. Nothing below → "Nothing below to drop onto"; already there
  (< 0.5 mm) → says so.
- Held (Gizmo / Clone / Place): stays uncommitted; RMB commits an ordinary `transform`. Snapping leaves the dropped
  height alone (`_zFree`) until E / Q steps it.
- Gizmo gun, aiming (not locked on): F locks on, drops, and commits at once (Ctrl+Z undoes in a file); nothing to do →
  the lock is released untouched.
- Panel hints and F1 help updated.

## 3. Family library and the Place gun

### Revit (`Extraction/SceneExtractor.Library.cs`, Options, `Bridge/RevitEditor*.cs`)
- Options → **Geometry** → *Family library*: tick (`LaunchSettings.FamilyLibrary`, off) and a cap
  (`FamilyLibraryMax`, 200, 10–1000). Only **Go and F5** build it (`SceneExtractor.Extract(…, liveSession: true)`);
  Export never does.
- Types: `FamilySymbol`s whose category is a ticked (extracted) FFE / services category, in-place families excluded,
  capped with types already used in the model first, then placeable, then by name; listed by category, family, type.
- Placement classes (`Family.FamilyPlacementType`): `OneLevelBased` → placeable; `OneLevelBasedHosted` → hosted,
  `WorkPlaneBased` → face-based, others → listed with the reason.
- Previews: `GetPreviewImage(128 × 128)` → PNG (`library/<n>.png`).
- Geometry: one transaction "BimGo: family library (temporary, rolled back)" with the editor's `SwallowFailures`
  (now `internal`): activate symbols, place one instance of each placeable type at the scene origin on the lowest
  level, one `Regenerate`, `ExtractElement` each (whole geometry, never through the active view: `_libraryPass`), turn
  the record into a template (`AsTemplate`: no ids, movable, new phase, `IsLibraryTemplate`), then **always roll
  back** (finally). Templates and their lights are moved 2 km down (`TEMPLATE_DROP`). Failures leave the type listed
  with a reason; a read-only / busy document lists everything without geometry. Progress: 78–85 % of the Go bar.
- Live `place` edit (`RevitEditor.Place`): symbol by UniqueId (ElementId fallback), level-based only, level = the
  highest at or below the point (`ProjectElevation`), `NewFamilyInstance(point, symbol, level, NonStructural)`, then
  moved so its location point is exactly the requested point, rotated about it, new phase (`SetNewWork`), registered
  in the clone-key map so later moves / copies / deletes of it work.
- Push: `place` entries go through the same `Place` (no staleness check); `createdBy` / known clones cover placements
  (`JournalOps.Creates`).

### Core
- `Scene/FamilyLibrary.cs`: `LibraryEntry`, `LibraryData` (entries, previews, `VertexStart`), `LibraryPlacement`.
- `SceneData.Library`, `SceneData.ModelVertexCount`, `ElementRecord.IsLibraryTemplate`.
- `EditOp.Place` (+ `EditRequest.TypeUniqueId`, `TypeId`); `JournalOps.PLACE`, `JournalOps.Creates`,
  `JournalEntry.TypeUniqueId` / `TypeId` (not written for other ops).
- Format (additive, version 1): `library.json`, `library/*.png`, `elements[].library`. The reader validates entries
  (template must exist and be flagged, unknown categories dropped, missing previews cleared) and never counts templates
  in category counts or bounds; a damaged `library.json` is ignored.

### App
- Templates are never part of a batch (`SceneBatches` puts their indices after all chunks, ranges kept for the dynamic
  draw), permanently `_hidden` (masks, `SetStaticHidden` and `ResetEdits` leave them alone), not in the id maps, not
  used for probes (`ModelVertexCount`) or door boxes.
- `GameSession.Library.cs`: pause menu **FAMILY LIBRARY (n)** panel: search box (type anywhere; Backspace, Ctrl+Backspace
  clears), *Placeable only*, category chips, family list (left), preview cards (wheel scrolls; greyed cards show why
  on hover). Previews are decoded lazily (6 per frame) and freed at session end. Picking a card → Place gun.
- `Guns/PlaceGun.cs` (gun 9, amber): LMB opens the library, RMB places the last type again; a pick appears 1.6 m (+
  half its size) in front of the player at foot level, dropped onto the surface below, in the gizmo's move mode
  (all Gizmo / Clone keys incl. F). RMB commits (`EditOp.Place`; Revit's new id is kept), Esc discards.
- Journal: `RecordEdit` writes `place` with the type; replay (`ApplyPlaceEntry`) clones the template under the entry's key
  at its pivot and angle (a type missing from the snapshot's library is logged and skipped).

## 4. README and dates
- README rewritten shorter (255 lines, was 524): AI rules (now including the conventions from the handoffs),
  overview, controls, structure, how it works (the design facts that only lived in the changelog moved here), format,
  protocol, to verify / limitations, a one-line-per-round history table, dependencies. The detailed changelog lives on
  in each round's `ai/` notes.
- Dates after 2026-10-09 → 2026-10-09 in the README and `ai/` notes; folders renamed `261009a_V6` … `261009k_UX_Cleanup`
  (references updated). This round: `261009l_NextRound`.

## 5. To verify (also README §8)
- Revit: `GetPreviewImage`, `FamilyPlacementType`, `NewFamilyInstance` height handling, `Activate` in the rolled-back
  transaction, that the rollback causes no MODEL CHANGED, worksharing, Go time with 200 types; the Options row layout.
- App: library panel on small screens; System.Drawing PNG decode; place → move → clone → demolish a placement live and in
  a file; save / reopen / undo / redo; push of `place`; Gizmo aim + F.
- Probes: the debug colours on the test model; the log line.
- An older add-in can't read `place` requests (enum value unknown): the app times out with the usual hint.

## 6. Files
New: `Core/Scene/FamilyLibrary.cs`, `App/Game/GameSession.Library.cs`, `App/Game/Guns/PlaceGun.cs`,
`Revit/Extraction/SceneExtractor.Library.cs`, `tests/…/LibraryTests.cs`.
Changed: `ReflectionProbes.cs`, `Shaders.cs`, `SceneBatches.cs`, `DynamicSet.cs`, `GizmoController.cs`, `GizmoGun.cs`,
`GunIcons.cs`, `UiTheme.cs`, `GameSession.cs` / `.Edits` / `.Document` / `.Menu` / `.Render`, `SceneData.cs`,
`LaunchSettings.cs`, `EditMessages.cs`, `EditJournal.cs`, `JournalPush.cs`, `BimGoFormat.cs`, `FileModels.cs`,
`BimGoReader.cs`, `BimGoWriter.cs`, `SceneExtractor.cs`, `RevitEditor.cs`, `RevitEditor.Push.cs`, `LiveDispatcher.cs`,
`Cmds_BimGo.cs`, `OptionsWindow.xaml(.cs)`, `TestData.cs`, `README.md`, `ai/` folder names and dates.

## 7. Update 1 (after Gavin's first test)

Gavin: probe changes are decent (not perfect, but as good as it gets without a performance hit). Two requests:

- **Work-plane-based families placeable** (most of his families are). New `Revit/Extraction/FamilyPlacer.cs`, used by
  the template pass and by live / pushed placements so both are made the same way: level-based → `NewFamilyInstance(XYZ,
  symbol, level, NonStructural)`; `WorkPlaneBased` (covers face-based too) → hosted on the level's plane reference
  (`Level.GetPlaneReference()`), else a sketch plane of the level (`SketchPlane.Create(doc, levelId)`), with reference
  direction +X; last resort the level overload. The caller still moves the instance onto the exact point (sets its
  offset when above the level) and rotates it. Library entries for these types: `placement` = `workPlane`, placeable.
  Wall-hosted (`OneLevelBasedHosted`) stays listed only. Snapshots from the first build list work-plane types as
  `faceBased` (not placeable): press F5 / Go again.
- **Ground plane override remembered**: `VisibilitySettings.GroundOffset` (m from the default ground, null = default,
  clamped ±10 m), so it is saved where the hidden elements are: `visibility.json` in a file (counts as an unsaved
  change), the live model folder's sidecar (written a moment after the slider stops). Restored on open / F5 / Go.
  Only a slider drag marks a change.
- Tests: `Visibility_GroundOffsetRoundTripsAndCleans`, `Library_WorkPlaneEntryRoundTrips` (11 in `LibraryTests`).
- To verify: work-plane placement on Revit 2025–2027 (which of the three routes works, logged only on failure), the
  instance's "Host" / offset in Revit after a placement above the level, and that templates and placed instances face
  the same way.


## 8. Update 2: "the model is read-only" on every library type

- Cause: `Cmd_Launch` (Go) was `[Transaction(TransactionMode.ReadOnly)]`, so during Go `Document.IsReadOnly` is true and
  the library's temporary (rolled-back) transaction can't start: every type was listed without geometry. Refreshes (F5)
  run in the ExternalEvent and weren't affected.
- Fix: Go is now `TransactionMode.Manual`. It still commits nothing (the library transaction is always rolled back).
  Export and Live stay ReadOnly. The reason text now says F5 retries if it ever happens again (a genuinely read-only model).
