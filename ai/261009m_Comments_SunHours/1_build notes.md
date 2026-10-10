# Comments, sun hours, find room — build notes

**Baseline:** `BimGo_NextRound_Update2.zip` (Gavin: works). **Not compiled here** (no .NET SDK or NuGet reachable).
Changed files were reviewed by eye and checked for balanced brackets. No shader changed. Please build and run the Core
tests (new `SunHoursAndCommentsTests`, 7 tests).

## 1. Comments as issues

- **Core `Format/CommentModels.cs`:** `CommentRecord` gains `Status` (`open` / `inProgress` / `closed`), `Priority`
  (`low` / `normal` / `high`), `AssignedTo`, `Updated` / `UpdatedBy`, `Replies` (`CommentReply`: id, author, created,
  text), `View` (`CommentView`: feet in Revit internal metres, yaw, pitch, flying) and `Thumbnail` (base64 JPEG);
  `Clean()` normalises them (unknown → open / normal, blanks dropped, a non-finite view dropped). `CommentStatus` and
  `CommentPriority` hold the values and labels. All optional: older files and sidecars read as before.
- **`CommentStore`:** `SetIssue` (status / priority / assignee, records who and when), `AddReply`, `RemoveReply`,
  `SetView`, `TryGetView`, `SetThumbnail`; CSV adds Status, Priority, Assigned to, Replies, Last reply and Thread.
- **New comment:** the text box's Enter stores the player's view and asks for a thumbnail next frame (the bookmark
  capture now serves comments too: `_commentThumbnailFor`, textures keyed by owner). The picture is the 3D view only.
- **Panel (`GameSession.Comments.cs`, rewritten):** rows with thumbnail, a status bar, header, text and the issue line
  (● status · priority · → assignee · n replies); filters: level (‹ ›) and status (All / Open / In progress / Closed);
  GO (to the saved view; older comments as before), OPEN, DELETE (twice). **Detail view (OPEN):** picture, full text,
  STATUS and PRIORITY segmented controls, ASSIGN… (text box; empty clears), "Updated by", the reply thread (wheel
  scrolls, DELETE per reply), BACK / REPLY… / GO TO VIEW / SET VIEW HERE (view + new picture from where you stand) /
  EDIT TEXT / DELETE. Esc goes detail → list → menu.
- **Text box over the menu:** reply and assignee boxes (and EDIT TEXT) open over the paused panel; while one is open the
  panel gets no clicks (`Render`: `ConsumeClicks` then the editor on top).
- **Markers** coloured by status (open: violet as before, in progress: amber, closed: green and smaller); the hover
  label adds the issue line.

## 2. Find room (`GameSession.Rooms.cs`)

- Pause menu FIND ROOM (when the model has rooms) or **Ctrl+F**. Rows "number · name" and "level · area m² (· link)",
  sorted by level then name, built once. Typing filters (every word in number, name or level); an exact number
  match goes first; Enter takes the first row; click a row to go.
- `TeleportToRoom`: 0.4 m grid of plan points inside the room ≥ 0.45 m from its boundary, nearest the box centre
  first; the floor under each (`FloorAt`), skipping spots where the standing capsule overlaps; faces the middle (or
  along the longer side when at the middle). Falls back to any interior point for tiny rooms; says so when nothing fits.
- Core `RoomInfo.Contains(Vector2)` and `DistanceToBoundary(Vector2)` (even-odd over all loops; holes count).

## 3. Direct sun hours study

- **Core `Scene/SunHours.cs`:** `SunHoursSettings` (day, from / to, step 5 / 10 / 15, DST, grid 0.1 / 0.25 / 0.5 / 1,
  floor offset 0–2 m, wall offset 0–1 m, glass blocks; `Clean`), `SunHours.SunDirections` (samples at mid-step,
  above the horizon only, scene axes via `SolarPosition` + true north; Sydney when the model has no site) and
  `LegendColour` (Ladybug's default 10-colour gradient over 0–7 h).
- **BVH:** a per-triangle `Transparent` flag (the element's transparent range) and `Raycast(…, opaqueOnly)`, also in
  `DynamicSet.Raycast`: glass passes unless "glass blocks sun".
- **`SunHoursStudy`:** faces → grid: square cells in each face's plane (U horizontal on walls, X on floors; snapped to
  the plane's own origin), kept where the cell centre is on a triangle; room-derived faces keep cells inside the room
  (walls: within 6 cm of the boundary, the cell faces whichever side is in the room, so a thin partition's far face
  isn't taken); test point = centre + normal × (offset + 2 cm); ≤ 80 000 cells. Run: per cell, per sun sample with the
  sun in front of the surface (cos > 0.02), one ray (≤ 1.5 km) against visible static geometry and moved / placed
  instances; time-sliced on the game thread (10 ms per frame) so the BVH needs no locking. Statistics: average, min,
  max, share ≥ 2 h and ≥ 3 h.
- **Mode (`GameSession.SunHours.cs`):** J or pause menu SUN HOURS STUDY. Player frozen, cursor free, RMB-drag looks,
  a click on the model (outside the panel) adds the clicked surface (element triangles in that plane, the side facing
  you, clipped to the room it faces) or removes it if selected. Panel: date (« month ‹ day › month »), From / To
  (±15 min, Shift ±1 h), sample step, DST (from the sun panel), glass blocks, grid, floor / wall offsets, surface and
  cell counts, THIS ROOM / CLEAR SURFACES, RUN (or progress + CANCEL), summary, EXPORT CSV / SCREENSHOT / CLEAR, CLOSE
  (results stay). Grid settings rebuild the grid; time settings mark results stale ("RUN (settings changed)").
- **Drawing:** a second `Overlay3D` holds the cells (rebuilt only when the study changes), depth-tested after the
  markers; grey preview while picking, grey "not yet" cells while running, legend colours when done. Shown with the UI
  hidden too (it's content). Legend (bottom left, also when the panel is closed): Ladybug bar 0–7+ h with whole-hour
  ticks and the study's date / times / step / glass mode.
- **Exports:** CSV (settings block, blank line, then Cell, Surface, Element id, Element, Room, X / Y / Z, normal,
  Sun hours; Revit internal metres); SCREENSHOT = the 3D view plus the legend (drawn and flushed before the capture),
  `Pictures\BimGo\<model> sun hours <time>.png`.

## 4. Other changes
- Pause menu: FIND ROOM, SUN HOURS STUDY. Help: Ctrl+F, J. The sun panel and the study close each other.
- `Rgba.FromFloat`; `CaptureScreenshot(…, suffix)`.

## 5. To verify (README §8)
- Thumbnail capture after a new comment and after SET VIEW HERE; the text box over the paused panel.
- Sun hours: wall cells in rooms bounded at wall finish (rooms bounded at wall centres lose their wall cells: click
  them instead); study time on a big room at 0.1 m; compare one simple case with Ladybug (same day, times, location,
  true north).
- Find room on L-shaped and furnished rooms; linked rooms.

## 6. Ideas noted for later
- BCF 2.1 export / import of comments (Gavin: later).
- Sun hours: save a study with the model, a date range (several days averaged), a % of time view, comparison of two
  options.

## 7. Fix 1 and status
- Build error: `MONTHS` was declared in both `GameSession.Sun.cs` and `GameSession.SunHours.cs` (CS0102 / CS0229);
  the copy in `SunHours` was removed. A scan of all `GameSession` partials found no other duplicate members.
- Gavin: builds and works; the sun hours study confirmed good. Next: `2_Next round_Handoff.md`.

