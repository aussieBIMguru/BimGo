# BimGo — First-Person BIM Walkthroughs

BimGo (formerly **RvtGo**) turns a Revit model into an FPS-style, first-person walkthrough with collision, gravity, walkable stairs, a room readout and eight tool guns: **Scan**, **Measure**, **Portal**, **Comment**, **Teleport**, **Demolish**, **Gizmo** and **Clone**. It renders with a small custom OpenGL engine (own renderer, window and input; GL function bindings from Silk.NET, see §10).

It comes in two parts:

- **BimGo for Revit**, a slim add-in (extractor + session bridge; no engine code). **Go** opens the model in the app as a *live session*: Demolish / Gizmo / Clone edits go back into Revit, Scan → R selects elements in Revit, and changes made in Revit prompt a refresh. **Export .bimgo** writes a standalone extract. **Live** shows the session status.
- **BimGo**, the app (`BimGo.exe`). It joins live Revit sessions, or opens `.bimgo` files with no Revit, keeping edits in the file's journal (with undo) and saving them again. A file's edits can later be **pushed into the Revit model** they came from (dry-run preview, one undo step in Revit).

Demolition works between two phases picked in the Options dialog: the walkthrough shows the model as it stands in the **new** phase, the hammer demolishes **existing** elements in the new phase, new work can only be deleted, and clones are created in the new phase.

The v3 design brief is `ai/261005_V3/1_BimGo v3_Handoff.md`. Decisions and the changelog are in `ai/261005_V3/2_build notes v3.md` (phase 0 + 1) and `ai/261005_V3/3_build notes v3 phase 2.md`. Phase 4 (push), the existing/new phases and the v4 QoL items are in `ai/261007_V5/1_build notes v5.md`.

## For AI assistants (e.g. Claude)

1. **Read the handoff and build notes first.** This README documents what is built and where it deviates.
2. **Keep this README current**, especially the Changelog and *Known limitations / to verify*.
3. **Dependencies: own what differentiates BimGo, rent the commodity plumbing.**
   - Own: renderer architecture, shadows, picking, physics, the gun / tool system, the UI look, the `.bimgo` format, the journal, the Revit bridge and live protocol. The window / input stack (`Platform/GameWindow.cs`, `Native/Win32.cs`, `Platform/InputState.cs`, `Native/Wgl.cs`), waveOut audio and the font atlas also stay hand-written until they cause real pain.
   - Rent: OpenGL function bindings (Silk.NET.OpenGL behind the `Native/Gl.cs` facade: renderer code calls `Gl.Xxx` with `uint` constants; add new GL calls as facade wrappers, not new P/Invoke).
   - **BimGo.Revit stays dependency-free at run time** (it loads inside Revit beside other add-ins). Packages go in BimGo.App, in dev / build tooling, or nowhere. Revit API references stay HintPaths to the installed Revit.
   - Every package needs Gavin's explicit yes, a permissive licence (MIT / Apache / BSD / zlib), a pinned version and a line in the dependency table (§10). No UI toolkits (they would change the look). Ask before adding folders.
4. **Project boundaries:**
   - **BimGo.Core** has no Revit, GL or UI code.
   - **BimGo.App** never references the Revit API.
   - **BimGo.Revit** is the only project that touches the Revit API, and it holds no engine code. API calls happen in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs` (`RevitEditor.cs` live edits, `RevitEditor.Push.cs` journal push) and `Live/LiveDispatcher.cs` (on the Revit thread). `Live/SessionHost.cs` timers and watchers do file IO only.
5. **Edits go through `IModelSource`.** Guns call `GameSession.SubmitEdit`. The source is a live Revit session (`LiveSessionSource`) or the file (`FileEditSource`). Every accepted edit is recorded in the `EditJournal`.
6. **Template conventions still apply** in BimGo.Revit:
   - Commands live in `Commands/Cmds_<Group>.cs` as `Cmd_<Button>`.
   - Extensions live in `Extensions/TypeName_Ext.cs`.
   - Tooltips and icons resolve from the command's base name (`BimGo_Launch`, `BimGo_Export`).
7. **Name clashes:** WPF, WinForms and Revit's `DB`/`UI` namespaces are global usings in BimGo.Revit, and WinForms + System.Drawing are global in BimGo.App. Avoid unqualified `Color`, `Point`, `Plane`, `View`, `Panel`, `CheckBox`, `TextBox`, `TaskDialog`… (use the `DB.`, `UI.`, `SD.`, `Wpf.`, `Win.` and `WinForms` aliases).
8. **Zip handoff:** zip the repo minus `bin/`, `obj/`, `.vs/` and `artifacts/`.
9. **Tests:** `tests/BimGo.Core.Tests` (MSTest) covers BimGo.Core. Run it after Core changes (Test Explorer or `dotnet test`).

## 1. Overview

| Item | Decision |
|---|---|
| Solution | `src/BimGo.sln`: **BimGo.Core** (net8.0), **BimGo.App** (net8.0-windows, `BimGo.exe`), **BimGo.Revit** (Revit 2025/2026 on net8.0-windows, 2027 on net10.0-windows) |
| Renderer | OpenGL 4.1 core (falls back to 3.3), GLSL 330, Win32 window |
| File format | `.bimgo`: a ZIP holding JSON metadata, binary geometry, comments and an edit journal (see §6) |
| Revit link | **Live sessions** over a watched folder per document (`%LocalAppData%\BimGo\Sessions\<id>\`): JSON message files both ways, heartbeats, `.bimgo` snapshots for geometry. One `ExternalEvent` runs all Revit-side work. |
| Model folder | Live sessions keep their data in BimGo's own folder per model, `%LocalAppData%\BimGo\Models\<title>_<hash>\`: `comments.json`, `bookmarks.json`, `sun.json`, `visibility.json`, `texture-overrides.json` and `model.json` (which model it is). Key, most stable first: cloud model GUID, else the workshared central path, else the local path (two local copies = two folders). Older sidecars beside the model (`<model>.bimgo-*.json`, `.rvtgo.json`), in the old `Comments` folder, or texture overrides in `%AppData%\BimGo\texture-overrides\` are **copied** in once; the originals stay. Options → Player can also write them beside the model to share (off by default). |
| Comments / bookmarks | Revit: in the model folder (above). Files: inside the `.bimgo` (`comments.json`, `bookmarks.json`). |
| Sun & shadows | Off by default. Cascaded shadow maps with glass transmittance; sun from the Revit site location (captured at export) and a date / time the user scrubs. State saved with the model (`sun.json` in the file or the model folder); quality is per machine |
| Settings / logs | `%AppData%\BimGo\settings.json` (migrated once from RvtGo) · `%LocalAppData%\BimGo\Logs\BimGo.Revit.log` / `BimGo.App.log` |
| App install | The App build copies itself to `%LocalAppData%\Programs\BimGo\`, where every Revit year's add-in looks for `BimGo.exe`. That copy registers `.bimgo` (HKCU, ">>" icon) and a Start-menu shortcut on start; `BimGo.exe --register` / `--unregister` [`--quiet`] do it on demand |
| Phases | **Existing** and **new** phase picked in Options (saved by name). The walkthrough shows the new phase; demolish = Phase Demolished → new phase, existing elements only; clones are created in the new phase |
| Version | **1.0.0** (all three assemblies; shown on the home screen, F1 help, the Options title and in the logs) |
| Active view only | Options → WHAT TO LOAD: **off by default**. When ticked, every model element the active view shows comes in (its V/G, filters, section box, hidden elements, design options and phase filter decide; category ticks, phases and design-option rules don't); ticked links contribute what the view shows of them (Revit 2024+ view + link collector). A 3D view's subcategory visibility and detail level apply to host geometry. Unlisted categories land in **Other (active view)**. F5 reuses the active view, else the last one used for that model |
| Helper geometry | Options → GEOMETRY: **on by default**. Leaves out the Light Source subcategory (IES / photometric cones) and any *subcategory* whose name contains a keyword (default: light source, clearance, zone, cone, photometric; editable) |
| Ground plane | 100 mm below the lowest level by default (clear of slab faces on that level); the pause menu slider still moves it |
| Linked models | **None by default.** Options → LINKED MODELS lists every Revit link instance; ticked (loaded) instances are extracted with the host's categories, baked into scene coordinates with the instance's total transform, in the link phase named like the host's (else the link's last). The choice is remembered per host model (`LinkedModels` in settings) and reused by F5. Linked elements are **read-only** (Scan / Measure / Comment / Teleport / Portal only) and can be shown / hidden per link in the pause menu |

## 2. Getting started

1. Open `src/BimGo.sln` in Visual Studio 2022 (.NET desktop workload).
2. Pick a configuration (`Debug R25`, `Debug R26`, `Debug R27`, or Release). Core and App build as Debug/Release under each.
3. Build the solution (the app must be built too: Go launches it). The add-in deploys to `%AppData%\Autodesk\Revit\Addins\<year>\BimGo\` (with `BimGo.addin`). The app installs to `%LocalAppData%\Programs\BimGo\`.
   The first build restores the NuGet packages (§10), so it needs internet access once.
4. **Tests:** Test → Test Explorer → Run All (or `dotnet test tests/BimGo.Core.Tests`). They use temp folders only and log to `%LocalAppData%\BimGo\Logs\BimGo.Tests.log`.
5. **Remove the old `RvtGo.addin`** from the Addins folder. It has a different AddInId, so both tabs would load.
6. In Revit, press **BimGo → Go** for a live walkthrough (the app starts, or the running app asks to switch), or **Export .bimgo**, then open the file in BimGo.
7. To debug the app, set **BimGo.App** as the startup project and pass a `.bimgo` path, or `--session <id>` (the id is the folder name under `%LocalAppData%\BimGo\Sessions`). Running sessions also appear on the home screen.

## 3. Controls

| Input | Action |
|---|---|
| WASD / arrows | Move |
| Mouse | Look (click the window first to capture the mouse) |
| Space | Jump (ascend in fly mode) |
| Shift | Run |
| Ctrl | Crouch (descend in fly mode) |
| V | Toggle fly / no-clip |
| 1–8, mouse wheel | Select gun |
| LMB / RMB | Gun primary / secondary |
| N | Measure gun: toggle normal projection |
| T | Demolish gun: toggle phase demolish (default; existing elements only) / delete |
| **E** | Comment gun: edit the hovered comment |
| WASD · E / Q | Gizmo / Clone, move mode (every lock starts here): move in plan (view-relative) · E up / Q down (player frozen) |
| **R** · A / D | Gizmo / Clone while locked on: switch move ↔ rotate · in rotate mode, turn CCW / CW on the XY plane |
| Shift · Ctrl | Gizmo / Clone while locked on: fine control · invert snap mode while held |
| RMB · Esc | Gizmo / Clone while locked on: commit · cancel |
| **G** | Gizmo / Clone: toggle snap mode (moves / turns step by the increment; Ctrl held inverts) |
| **Z / X** | Gizmo / Clone while locked on: the current mode's increment down / up (move 5 mm … 1 m, rotate 1° … 90°) |
| **R** | Scan gun, live session: select and show the target in Revit (a linked element is selected inside its link) |
| **F5** | Live session: ask Revit for a fresh snapshot (reloads where you stand) |
| Page Up / Page Down | Teleport up / down one level |
| Tab | Toggle minimap |
| H / Shift+H | Return home / set home (saved with the model: walkthroughs start there) |
| X | Clear current gun's markers (Comment gun: press twice to delete all comments) |
| **Ctrl+S / Ctrl+Shift+S** | Save / Save as (file). In Revit: save the walkthrough as a new `.bimgo` |
| **Ctrl+Z** | Undo the last edit (files only; in a live session, undo in Revit, then F5) |
| **Ctrl+Y / Ctrl+Shift+Z** | Redo the last undone edit (files only; a new edit ends the redo history) |
| **B** | Bookmark this viewpoint (type a name, Enter; Esc keeps "View n") |
| **Ctrl+1–9** | Jump to bookmark 1–9 |
| **L** | Coordinate readout: off → shared → project → internal (crosshair point, or your feet when aiming at nothing) |
| **O** | Shadows on / off (sun lighting; off restores the classic light and frees the shadow maps) |
| **Shift+O** · click the sun icon | Open the sun panel (bottom right; frees the cursor, the player stands still, the scene keeps rendering). Esc / O close it |
| **[ / ]** | Sun time −/+ 5 min (Shift: 1 min) while shadows are on; a short note shows the date, time and sun position at each step. Space plays / pauses the day in the sun panel |
| **I · Shift+I** | Scan gun: hide the target in the walkthrough only · isolate its category (again: restore). Pause menu SHOW ALL brings everything back |
| **U** | Hide the UI: HUD, minimap, crosshair, markers and ordinary messages go (errors still show); every control keeps working. Esc or U brings it back (Esc then does nothing else). Not saved |
| F1 | Toggle controls help (shows the version) |
| F11 | Borderless fullscreen |
| **F12** | Screenshot of the 3D view (no HUD) to `Pictures\BimGo\<model> <date time>.png` |
| Esc | Pause menu (save, push to Revit, comments list, category toggles, quality profile, display / reflection settings…); cancels the gizmo when locked on; closes the push / comments panel; shows the UI again when hidden (U) |

App home screen: running **Live Revit sessions** (click to join), **Open .bimgo…** (Ctrl+O), recent files (right-click removes one), or drop a file on the window. During a walkthrough, dropped / double-clicked files wait until you close the model; a Go from Revit for another model asks before switching.

Pause menu extras:
- **SHOW ALL (n HIDDEN)** (when anything is hidden): elements hidden with I, categories and links switched off, and a Shift+I isolation all come back. Hidden things are saved with the model (`visibility.json` in a file, or in the model folder in live sessions) and count as unsaved changes in files.
- **PUSH TO REVIT (n)…** (files): pushes the edits not yet in Revit into the live session of the model the file came from (open it in Revit and press Go; answer No to the switch prompt). A dry run previews every edit (will apply / conflict / skipped / will fail / already in Revit); conflicts (moved in Revit since the file was made, > 5 mm) are skipped unless "apply anyway" is ticked. The real push is one undo step in Revit; pushed entries are marked in the file (Save keeps that) and are never sent again. EXPORT REPORT… writes a CSV.
- **COMMENTS (n)**: every comment, filtered by level (← →), with GO (stand in front of it), EDIT, DELETE (click twice) and EXPORT CSV….
- **LINKED MODELS** (under the category cards, when the model has links): one toggle per extracted link instance (drawing, picking, collision, shadows) with its element count.
- **Right column:** **QUALITY PROFILE** Basic / Medium / Realistic (shows CUSTOM once anything it sets was changed by hand; saved per machine), then three tabs. **Display**: ground plane, colour mode, anti-aliasing, FOV, mouse sensitivity, VSync, Invert Y, Show FPS, ambient occlusion. **Reflections**: *Reflections* Off / Some (shine 50 %+) / All (25 %+), *Source* Sky / Probes / Probes HQ, *Reflection strength*, the probe status and REFRESH. **Debug**: *Debug colours* Off / Reflection / Probes (not saved).
- **BOOKMARKS (n)**: saved viewpoints in Ctrl+number order, with GO, RENAME, SET HERE (move it to where you are), ↑ ↓ (reorder), DELETE (click twice) and ADD THIS VIEW. Shown as blue dots on the minimap.

Sun panel (Shift+O): **Shadows** on/off and **quality** (Low 1 × 2048 px / 60 m, Medium 3 × 2048 px / 120 m, High 4 × 3072 px / 200 m; per machine), **time of day** slider (5-minute steps, Shift = 1 minute) with play (one hour per second), **month** and **day** boxes (type digits, Enter / Tab, ↑ ↓ step; clamped to the month), **+1 h DST**, the sun's height and bearing, and sliders for **sunlight**, **sky / diffuse light**, **shadow intensity** and **light through glass**, the artificial **Lights** mode with *Light* / *Bloom* sliders, plus RESET LIGHTING. The site comes from Revit's Location (latitude, longitude, time zone); the start date / time from the launch view's sun settings (else today 12:00). Glass lets light through by its Revit transparency, tinted by its colour. Bookmarks saved with shadows on remember the date / time and GO restores it.

Coordinate readout (L, remembered in settings): **Shared** = survey coordinates (E / N / elevation) from the model's shared site, as Revit's spot coordinates relative to the survey point; **Project** = relative to the project base point on project-north axes; **Internal** = Revit internal metres. Files exported before v5.1 derive shared coordinates from the survey point stored in float precision (marked "≈", can be ~0.5 m out on large grid coordinates): export again for millimetres.

### Guns

| # | Gun | LMB | RMB | In Revit | In a .bimgo file |
|---|---|---|---|---|---|
| 1 | Scan | Lock target | Clear | Info panel + extra parameters; R selects in Revit | Same (no R) |
| 2 | Measure | Start / end point | Remove last | | Same |
| 3 | Portal | Blue portal | Red portal | | Same |
| 4 | Comment | Place + type (E edits) | Remove marker | Saved in BimGo's model folder | Saved in the file |
| 5 | Teleport | Blink to marker | Step back | | Same |
| 6 | Demolish | Prime / demolish primed | Un-prime | Demolish in the new phase (existing elements only; T = delete) in Revit | Journal `hide`; hosted inserts go too |
| 7 | Gizmo | Lock on (FFE) | Commit | Moves / raises / rotates the element in Revit (R move ↔ rotate, G snap, Z/X increments) | Journal `transform` |
| 8 | Clone | Clone in place (FFE) | Commit | Copies the element in Revit (same snap keys) | Journal `clone` |

## 4. Project structure

```
src/
├── BimGo.sln
├── BimGo.Core/                    # net8.0: no Revit, no GL, no UI
│   ├── Scene/                     #   SceneData, CategoryCatalog, LaunchSettings, ModelInfo (provenance, site, ParameterTable), LinkInfo, SiteCoordinates, SolarPosition, MaterialData, TextureSearch (deep scan), ProxyCatalog, TextureOverrides
│   ├── Edits/                     #   EditRequest/EditResult/EditChannel, EditJournal + JournalEntry
│   ├── Sources/                   #   IModelSource, FileEditSource
│   ├── Live/                      #   protocol (session.json, envelopes, message types), FolderChannel, LiveSessions, LiveSessionSource + ILiveLink, JournalPush (temporary push channel)
│   ├── Format/                    #   .bimgo: BimGoFormat, BimGoReader/Writer, DTOs, BimGoDocument, SunModels, comment / bookmark / sun sidecars
│   └── Utilities/Log_Utils.cs
├── BimGo.App/                     # BimGo.exe and the engine (no Revit)
│   ├── Program.cs                 #   entry point, single instance, DPI awareness, --register / --unregister
│   ├── Shell/                     #   AppShell (home ↔ walkthrough loop, switch / reload), HomeScreen, OpenTarget, RecentFiles, AppInstance (mutex + inbox), FileAssociation
│   ├── Game/                      #   GameSession (+Render, +Menu, +Edits, +Document, +Live, +Push, +Comments, +Bookmarks, +Coordinates, +Sun, +Lights, +Textures), CommentStore, BookmarkStore, SessionOptions, guns
│   ├── Rendering/                 #   SceneRenderer (+ shadow and AO / glow pre-passes), ShadowMaps (cascades), ScreenEffects (AO + bloom), ArtificialLighting, SunLighting, MaterialTextures + ProxyPack (Realistic mode), Shaders, UI
│   ├── Resources/Proxies/         #   CC0 proxy textures + proxies.json (copied beside the exe)
│   ├── Physics/ Platform/ Native/ Audio/
└── BimGo.Revit/                   # the add-in (template configs R25–R27)
    ├── Application.cs             #   ribbon: BimGo tab → Walkthrough → Go, Export .bimgo, Live (status)
    ├── Commands/Cmds_BimGo.cs     #   Cmd_Launch (Go → live session + app), Cmd_Export (.bimgo), Cmd_Status
    ├── Extraction/                #   SceneExtractor (host + ticked links; +Materials, +Review), TextureLocator, MaterialScan (report), LinkResolver (link instances, saved choice), CategoryResolver, ParameterScanner, PhaseResolver
    ├── Live/                      #   SessionHost (folder, heartbeat, snapshots), LiveDispatcher (ExternalEvent, registry, doc events, ribbon status)
    ├── Bridge/RevitEditor.cs      #   applies edits: transactions, failure swallowing, clone key map, phases
    ├── Bridge/RevitEditor.Push.cs #   journal.apply: one TransactionGroup, dry run, staleness check, push-local clone map
    ├── Forms/OptionsWindow        #   WPF options (categories, phases, extra parameters, display, gizmo snap, materials)
    ├── Forms/TextureReviewWindow  #   WPF "Review textures…" (per-material choices, staged deep scan)
    └── Extensions/ General/ Utilities/ Resources/   # template (+ App_Utils: find / start BimGo.exe)
tests/
└── BimGo.Core.Tests/              # MSTest (dev-only, references BimGo.Core only): format round-trips, older / damaged files,
                                   #   journal, sun position, settings, progress, live channel, sidecars
```

## 5. How it works

- **Phases.** `PhaseResolver` resolves the **existing** and **new** phases from the saved names (Options), else new = the launch view's phase (else the last) and existing = the phase before it. Each element gets a role: *existing* (there in the existing phase, still standing in the new one: demolishable), *new* (created in the new phase), *between* (built in between) or *unphased*. Demolition sets Phase Demolished to the new phase and is refused for anything but existing elements (the hammer says so before sending; Revit checks again). Copies get Phase Created = new phase.
- **Extraction (Revit thread).** For each ticked category, elements are filtered (no view-specific elements or secondary design options, and only what stands in the new phase: nothing demolished by it or built after it). They are tessellated, coloured from their materials and converted to metres around a scene origin (the median element centre, rounded). Each element also records:
  - its ElementId, **UniqueId** and **host id**;
  - its movability and pivot;
  - the **extra parameters** picked in Options (instance value, else the type's).

  The extraction also captures:
  - **Linked models** ticked in Options (`LinkResolver`): each loaded instance is a *source* with its own document, total transform, phases (matched by name to the host's) and caches (materials, categories, levels). Its elements follow the host's (so host elements keep the lowest indices) with `link` = n, are never movable (`MoveBlockReason` "In linked model … (read-only)") and record no host id. Its rooms are added with `link` = n (host rooms win in the readout). Levels and the level list stay the host's.
  - **Provenance:** title, path, `ProjectInformation.UniqueId` as the model key, cloud GUIDs, Revit version, user and time.
  - **Site:** true north, project base point and survey point.
- **Sessions.** One `GameSession` runs both modes. Only the `IModelSource` differs:
  - Live: edits are `edit` messages to Revit; `LiveDispatcher` applies them with `RevitEditor` (one transaction each) and answers `edit.result`. They are optimistic: the walkthrough changes at once and is reverted if Revit refuses or contact is lost.
  - In a file, `FileEditSource` accepts every edit at once. For removals it adds the hosted elements.
- **Live sessions.** Go extracts, writes an uncompressed snapshot `.bimgo` into the session folder, records it in `session.json`, announces `extract.ready` and runs `BimGo.exe --session <id>` (a running app gets an inbox request instead).
  - The app attaches (purges stale messages, writes `app.json`, says `hello`), reads the snapshot, and walks it. The snapshot carries the comments path (in the model folder), so the app reads and writes the model's comments, bookmarks, sun and visibility there.
  - Revit's heartbeat (2 s) keeps `session.json` fresh and flushes counted `model.changed` events (anything except BimGo's own committed `BimGo: …` transactions). The app shows **MODEL CHANGED · F5**; F5 sends `extract.request`, Revit re-extracts, and the app reloads the new snapshot where the player stands (pose carried in Revit coordinates). Pressing Go again does the same.
  - Scan → R sends `select.elements`; Revit selects, shows and comes to the front (if that model is the active one).
  - Closing the document / Revit sends `session.closing`; the app goes read-only (save as `.bimgo` still works). A stale heartbeat (> 10 s) pauses edits until it recovers.
- **The journal.** Accepted edits are appended to the `EditJournal` in both modes. Targets are stored by UniqueId (with the ElementId as a fallback), or by clone key for clones made in a walkthrough. Each entry also stores the request's pivot, offset, angle and label, and `appliedToRevit` if the edit already reached Revit.
  - Opening a file **replays** the journal on the untouched geometry, reusing the v2 hide and dynamic-instance machinery.
  - **Undo** removes the last entry, resets all edits and replays the rest.
  - **Saving** writes the snapshot, the comments and the journal.
- **Push (`journal.apply`).** The app finds a live session whose `modelKey` equals the file's, opens a temporary `FolderChannel` on it (no `app.json`, no hello) and sends the entries not yet in Revit, plus the clones already there (`knownClones`). `LiveDispatcher` hands them to `RevitEditor.ApplyJournal`: one `TransactionGroup` ("BimGo: Push N edits from file.bimgo"), each entry in its own transaction; targets by UniqueId (clones by a push-local key map); moves and clones compare the element's location point with the entry's pivot (5 mm); hides honour the recorded mode (delete, or demolish in the file's new phase, matched by name). Dry run → roll back the group; real push → assimilate (one undo). Requests over 3 MB travel as `snapshots/push-<id>.json`. The app times out after 30 s + 0.25 s per entry ("update the add-in / Revit busy").
- **The app.** One window runs a single-threaded loop that alternates between the home screen and walkthroughs. A second launch, or Revit's Go / "Open in BimGo", drops a request in `%LocalAppData%\BimGo\App\inbox\` (`{ "open": path }` or `{ "attach": sessionId }`) and brings the window forward.
- **Engine** (unchanged from v2):
  - Batched and frustum-culled `glMultiDrawElements`.
  - An off-screen MSAA target.
  - Fixed 120 Hz physics.
  - A static BVH shared by picking and collision.
  - Degenerate-index hiding.
  - `DynamicInstance`s for moved and cloned elements.
- **Ambient occlusion** (pause menu → Ambient occlusion, on by default, saved in settings): before the scene pass, the opaque batches, moved / cloned elements and the ground are drawn at half resolution into a geometry target (view-space normal + view depth, RGBA32F) by `ScreenEffects`. A screen-space AO pass (12 spiral taps over 0.6 m, 4×4 ordered rotation) and a 9-tap depth-aware blur in each direction leave (AO, depth) on texture unit 3. The scene and ground shaders upsample it with a joint bilateral 2×2 lookup and multiply only the **ambient** (sky) term, so direct sun and shadows are untouched. Off for glass, the plan minimap and beyond 120 m (fading from 72 m). Independent of MSAA (separate target). A GPU that refuses the targets switches it off with a toast.
- **Artificial lights** (K cycles off / glow / glow + light; sun panel: mode and brightness; Revit Options: the launch mode; saved in settings).
  - *Extraction* (`SceneExtractor.Lighting.cs`): a material **glows** when its appearance asset has self-illumination (Generic `generic_self_illum_luminance` > 0, with its filter colour and colour temperature; Advanced / Physical `opaque_luminance` unless `opaque_emission` is off), anywhere in the model. Inside **Lighting Fixtures** elements, materials whose name contains an `EmissiveKeywords` entry (lamp, bulb, LED, lens, diffuser…) glow too; a raised fixture (bottom > 1.2 m above its level) with neither gets its bottom, downward-facing faces as a guessed lens. Glowing meshes are always opaque. Each fixture gets **one light** at the area-weighted centre of its glowing triangles (5 cm in front, downward share from which way they face), else near the top of the fixture (floor / table lamps); lumens and kelvin come from its "Initial Intensity" / "Initial Color" (or similarly named) parameters when their text parses (lm, cd, W @ lm/W; K), else 1000 lm / 3500 K (logged as estimated).
  - *Rendering:* glow is a per-vertex RGBA8 stream (attribute 3; only uploaded when the model has some) added to the surface colour and written by the AO pre-pass into a second target, so the **bloom** (quarter resolution, 13-tap blur, added over the scene after glass) is hidden behind whatever is in front. Each frame `GameSession.Lights` picks the **nearest 32 lights whose sphere touches the view** (fixtures where they stand, moved fixtures where they went, clones' copies; hidden ones dark), fading the farthest when more are in range. Each light has a **cached omnidirectional shadow map** (`LightShadows`: 6 × 256 px faces per light in one 16-bit depth array of 32 × 6 layers, ~25 MB; the shader picks the face from the major axis, so GL 3.3 is enough). Maps are rendered once and kept while the light stays picked; a moved light or a scene change (hide, demolish, move, category toggle) re-renders them, at most 4 lights per frame (new ones first; stale maps are used meanwhile); a light joins, fading in over 8 frames, once its map exists. Windowed inverse-square falloff (radius 2.5–9 m from √lumens), an omni + downward-cosine lobe, 4-tap PCF, and 6 % of each light filling what it sees evenly (a stand-in for bounce, darkened by AO; a third of it reaches shadowed spots). Values above 0.8 roll off smoothly instead of clipping. With the sun up the fixtures matter less (light × 0.35, glow × 0.6 at full day).

## 6. The `.bimgo` format (version 1)

A ZIP container with the extension masked:

| Entry | Content |
|---|---|
| `manifest.json` | `format`, `formatVersion`, generator, kind (`revit-export` / `session-save` / `save` / `live-snapshot`, which also records the comment sidecar path), title, created/saved UTC, units, **provenance**, extraction options, counts |
| `model.json` | origin offset, bounds, site, new phase (`phaseId`/`phaseName`), existing phase (`existingPhaseId`/`existingPhaseName`, optional), `phaseNote` (optional), spawn, levels, rooms (flattened loops), categories (by catalog key) |
| `elements.json` | per element: id, uniqueId, name, category index, family/type, level, hostId, proxy, movable/reason, pivot, `phase` (optional: `new` / `between` / `unphased`; absent = existing), `link` (optional: n = `model.links[n-1]`; absent = host), bounds, `[start,count]` index ranges |
| `parameters.json` | optional: pooled `names`, pooled `values`, per-element `rows` of name/value index pairs |
| `geometry.bin` | header (`BGEO`, version, vertex size 28, counts), then `SceneVertex[]` and `uint[]` indices, little-endian |
| `comments.json` | comment markers (Revit internal metres; optional `edited` / `editedBy`) |
| `journal.json` | ordered edit entries |
| `visibility.json` | optional (only when something is hidden): `hiddenCategories` (catalog keys), `hiddenLinks` (link instance UniqueIds), `hiddenElements[]` (`link` instance UniqueId or absent, `uniqueId`, `id`) |
| `sun.json` | optional: `enabled`, `time` (`month`, `day`, `minutes`, `daylightSaving`), `sunIntensity`, `skyIntensity`, `shadowIntensity`, `glassTransmission`. Bookmarks may carry `sun` (same `time` shape) |
| `lighting.json` | optional (written when there are lights or glowing surfaces): `emissive[]` = `[vertexStart, vertexCount, packed RGBA]` runs (RGB = colour, A = strength / 4), `lights[]` with `element` (index), `position` (scene-local metres), `lumens`, `kelvin`, `downward` (0–1), `estimated`. Damaged runs / lights are dropped on read |
| `materials.json` | optional (only when textures were extracted): `textureMaxSize`, `materials[]` with `name`, `link`, `materialId`, `schema`, `colour` (render colour 0–1), `texture` (entry name under `textures/` or absent), `textureState` (`none` / `embedded` / `missing` / `procedural` / `unreadable`), `textureSource` (the appearance's own path), `autodesk`, `scaleU` / `scaleV` / `offsetU` / `offsetV` (m), `angle` (°), `fade`, `tint`, `reflectivity`. Build B adds (all optional, absent when unset): `uniqueId` (Revit material UniqueId), `renderColour` (the appearance's colour when `colour` fell back to the shading colour), `assetTint` (the appearance's own tint), `invert` (only written when true), `textureOrigin` (`asset` / `search` / `override` / `proxy`, a string, never an enum), `proxy` (a CC0 keyword; the image ships with the app, not the file). Reflection probes build A adds (optional): `shine` (opaque reflection strength 0–1, raw; the app rounds it to 25 % tiers), `roughness` (0 sharp – 1 matt), `metallic`, `water`, `waterBump` (all only written when set), `reflectSource` (diagnostics). Glass keeps `reflectivity` |
| `material.bin` | optional, with `materials.json`: header (`BMAT`, version, vertex count, flags bit 0 = coordinates follow), then a `ushort` material index per vertex (65535 = none) and a float2 surface coordinate per vertex (m). A count that doesn't match the geometry drops the materials (the file still loads) |
| `textures/*.jpg` | optional: the embedded images (re-encoded, longest side ≤ `textureMaxSize`), one per distinct image |
| `bookmarks.json` | optional (written when there are bookmarks or a home): `bookmarks[]` with id, name, author, created, `x`/`y`/`z` (feet, Revit internal metres), `yaw`/`pitch` (radians), `flying`, `level`, optional `sun` and `thumbnail` (base64 JPEG); optional `home` (same shape: where walkthroughs start). List order = Ctrl+1–9 order |

`model.site` also carries (v5.1, additive) `hasSharedTransform`, `sharedEast`, `sharedNorth`, `sharedElevation` (shared position of the internal origin, double precision) and `sharedAngle` (internal → shared rotation): shared = Rz(sharedAngle) · internal + (east, north, elevation). The manifest's `counts` gained `bookmarks`. v6 adds `model.site.hasLocation`, `latitude`, `longitude` (degrees, east / north positive), `timeZone` (hours), `placeName` and `sunStart` (`yyyy-MM-ddTHH:mm`, the launch view's sun-study start). Settings gained `ShadowQuality` (Low / Medium / High).

Materials build B: settings gained `RevitTint` (`Off` / `Multiply` / `KeepLightness`), `ProxyMissingTextures`, `ProxyMaterialColour` and `TextureSearchFolders` (≤ 20, global). Per-model texture choices live outside the file in the model folder's `texture-overrides.json` (before UX build B: `%AppData%\BimGo\texture-overrides\<host model key>.json`, copied in once) (`documents` → `host` or a link's model key → material UniqueId or `name:…` → `{ image | proxy | colourOnly }`).

v8 (1.0) adds `visibility.json`, `manifest.extraction.activeView` (the view name when extracted with "active view only") and the catalog key `other`. Settings gained `ActiveViewOnly`, `SkipHelperGeometry` and `HelperSubcategoryKeywords`.

v7 adds `model.links[]` (optional; one per extracted link instance: `index`, `name`, `title`, `instanceId`, `instanceUniqueId`, `modelKey`, `modelPath`, `originX/Y/Z` (host internal metres, double), `basisX/Y/Z`, `phaseName`, `existingPhaseName`, `elementCount`, `roomCount`), `elements[].link`, `model.rooms[].link` and `manifest.counts.links`. Ids and unique ids are only unique within one model: readers must key elements by (link, id). Older readers ignore the fields and show linked elements as ordinary (non-movable) elements. Settings gained `LinkedModels` (host model key → ticked link instance UniqueIds).

Readers load this version and older ones, and refuse newer ones with a message. Writes are atomic: a `.tmp` file, then a replace. JSON entries are readable (camelCase). The large entries are compact.

## 7. The live session protocol (version 1)

| Direction | Type | Payload |
|---|---|---|
| app → Revit | `hello` | app pid, version |
| Revit → app | `hello.ack` | document title, Revit version, new phase, existing phase |
| app → Revit | `edit` | `EditRequest` (ticket, op, ElementId or clone key, pivot, translation, angle, label) |
| Revit → app | `edit.result` | `EditResult` (ticket, success, message, affected ids, new id, clone key) |
| app → Revit | `extract.request` | reason |
| Revit → app | `extract.ready` / `extract.failed` | snapshot path, number, counts, seconds, reason (`go` / `refresh`) / message |
| app → Revit | `select.elements` | ElementIds; optional `linked[]` (`linkInstanceId`, `elementId`): a new add-in selects those by link reference (`Selection.SetReferences`) and zooms to them, an older one selects the link instances in ElementIds |
| Revit → app | `select.result` | success, message |
| Revit → app | `model.changed` | added / modified / deleted counts since the last flush |
| Revit → app | `session.closing` | reason |
| app → Revit | `detach` | none |
| app → Revit | `journal.apply` | `JournalApplyPayload`: requestId, dryRun, toleranceMm (5), applyConflicts, modelKey, phaseName, existingPhaseName, fileName, knownClones, entries (or `payloadPath` for big requests) |
| Revit → app | `journal.result` | `JournalResultPayload`: requestId, dryRun, success, message, phases used, undoLabel, results (seq, status `applied` / `skipped` / `conflict` / `failed` / `alreadyApplied`, message, newElementId, affected), totals |

Additive to protocol 1: an older add-in ignores `journal.apply` and the app times out with a hint to update it. `session.json` also carries `existingPhaseName`.

Envelope: `protocol`, `id`, `seq`, `sessionId`, `type`, `replyTo`, `sentUtc`, `payload`. Files are named `<utc>-<seq>-<type>.json` so name order is send order; written as `.tmp` then renamed; read by a `FileSystemWatcher` plus a 1 s poll; validated (session id, protocol, 4 MB cap), de-duplicated by id and deleted.

## 8. Known limitations / to verify

- **v5 not compiled yet** (`ElementOnPhaseStatus.NotApplicable` does not exist and was removed in v5.1; written without a .NET SDK or the Revit API assemblies; phase 2 has since been built and fixed). Check `PhaseResolver` (`Element.GetPhaseStatus`, `ElementOnPhaseStatus` names), `RevitEditor.Push.cs` (`TransactionGroup.Assimilate`), `DBEvents.UndoOperation.TransactionGroupRolledBack`, the COM `IShellLinkW` interop in `FileAssociation` and the new XAML rows in `OptionsWindow`.
- Show in Revit works when the session's model is Revit's active document (otherwise the app is told to switch).
- One app window walks one model; other sessions wait on the home screen.
- Revit API calls to verify on 2025–2027:
  - `Mesh.DistributionOfNormals` / `GetNormal`, `Element.DemolishedPhaseId`, `Document.IsModelInCloud`, `Level.ProjectElevation`;
  - `WorksharingUtils.GetCheckoutStatus`, `Element.GetDependentElements`;
  - new in v3: `Document.GetCloudModelPath`, `BasePoint.GetProjectBasePoint/GetSurveyPoint`, `ProjectLocation.GetProjectPosition`.
- Standalone demolish removes hosted elements with their host (from `HostId`). Revit's own rules for face-hosted families may differ.
- Undo / redo are file-only. In a live session, undo in Revit, then F5 (undoing BimGo's own edits counts as a model change). In a file, undo stops at edits already in Revit (pushed or made live). The redo history lives in memory only (not saved) and ends with any new edit.
- **v5.1 not compiled yet** either (redo, bookmarks, coordinate readout). Revit side: `ProjectPosition` (`EastWest`, `NorthSouth`, `Elevation`, `Angle`) in `SceneExtractor.BuildSite`. The internal → shared rotation sign is checked against the survey / base point at extraction (logged when flipped); verify the shared readout against a Revit spot coordinate on a rotated, georeferenced model.
- **v6 not compiled yet** (sun / shadows). Verify: `Document.SiteLocation` (`Latitude` / `Longitude` in radians, `TimeZone`, `PlaceName`), `View.SunAndShadowSettings.StartDateAndTime` (UTC or local? the code converts when `Kind` is UTC; compare the start time in the log with Revit's Sun Settings); GL `glTexImage3D` / `glFramebufferTextureLayer` (wglGetProcAddress) and `glColorMask` / `glDrawBuffer` / `glReadBuffer` (opengl32 exports); the GLSL (`sampler2DArrayShadow`, dynamic uniform-array indexing) on the target drivers. Compare the sun direction with a Revit sun study on a rotated model.
- Shadows: one glass layer model (all glass in front of the first opaque surface multiplies; glass beyond it is ignored). No cascade blending (a faint seam can show where cascades meet). Shadow acne / peter-panning tuned by a fixed polygon offset and a 1.5-texel normal offset. DST is a manual +1 h checkbox (no regional rules). Video memory: Low ~16 MB, Medium ~50 MB, High ~150 MB (doubled for models with glass).
- Project coordinates are relative to the project base point on project-north axes (the base point's own "angle to true north" is not applied).
- Refresh is a full re-extract (incremental refresh later).
- Push: the staleness check needs a location point (only point-based families are movable, so moves and clones always have one). A hide whose element is gone counts as already applied for deletes and skipped for demolitions. Clones have no duplicate guard beyond the journal flag: if the app loses the answer to a real push (timeout), check Revit before pushing again.
- Phases are saved by name in the shared settings; a model without those names falls back to the defaults (and the walkthrough says so once). Clones made before v5 kept their source's phase.
- Saving from a Revit session leaves out edits still waiting for Revit (a toast says so).
- Stairs, railings, roof edges and orthographic spawn behave as in v2 (see the build notes).
- **1.0 not compiled yet** (active view only, helper geometry, hide / isolate, visibility file, screenshots). Verify: `FilteredElementCollector(doc, viewId, linkId)` (2024+), `Options.View` with a 3D view, `GraphicsStyle.GraphicsStyleCategory` / `Category.Parent`, `View.GetCategoryHidden`, `Element.IsHidden(View)`, `glReadPixels` (opengl32 export), `System.Drawing.Bitmap` PNG save.
- Active view only: what a plan or section shows depends on its view range / far clip (a 3D view is the reliable choice); temporary hide / isolate in Revit may or may not be honoured by the view collector; elements Revit draws only in plan (symbolic lines) have no 3D geometry.
- **v7** (linked models) built and works (Gavin, 2026-10-11).
- Linked models: only top-level link instances are offered (nested links are not extracted); unloaded links are listed but can't be ticked; link phases are matched by name (Revit's per-link phase mapping isn't exposed in the API); a link's levels name its elements but don't join the level list (PgUp / PgDn); linked elements are read-only and never enter the journal or push; comments on them record no element id. A link reloaded in Revit shows as MODEL CHANGED (F5 re-extracts it).
- More than 9 guns will need a rethink of the number keys.
- **Dependencies round** (Silk.NET GL bindings, Core tests, MIT) built, all tests pass and UX checked (Gavin, 2026-10-13). Next: installer round, `ai/261013_Installer/0_BimGo Installer_Handoff.md`.
- Silk.NET.Core brings Microsoft.Extensions.DependencyModel 9.x, which may in turn copy a newer `System.Text.Json.dll` (9.x) beside `BimGo.exe`; the app (and BimGo.Core inside it) would then use it instead of the 8.0 framework copy. Check the build output; JSON behaviour should be identical for BimGo's DTOs, and the Revit add-in is unaffected.
- **Lighting round** (glow, bloom, lights with cached shadow maps) and **gizmo modes** built and working (Gavin, 2026-10-16). Originally written without a .NET SDK or the Revit API. The GLSL was compiled, linked and run in WebGL2 (two rooms, a doorway, ceiling panels, night and day). Verify: Revit `Autodesk.Revit.DB.Visual` (`Asset.FindByName`, `AssetPropertyDouble/Float/Boolean`, `AssetPropertyDoubleArray4d.GetValueAsDoubles`), `Parameter.AsValueString` text for "Initial Intensity" / "Initial Color" on real fixtures (check the log's "estimated" count), `glDrawBuffers` / `glUniform4fv`, RGBA16F targets, the sun panel height (600) on small windows.
- Artificial-light shadows are 256 px per face (soft, ~2.5 cm texels at 3 m); glass doesn't cast them; geometry within 8 cm of a light never shadows it. A GPU that can't make the maps lights without shadows (toast once). Keyword glow only applies inside Lighting Fixtures; self-illuminated materials glow anywhere. Light intensity is calibrated for a night-adapted eye (100 lx ≈ full albedo) and is not photometric.
- **AO round not compiled yet** (written without a .NET SDK). The GLSL was compiled, linked and run in WebGL2 (ANGLE / SwiftShader) on a test room; the C# was reviewed by eye only. Verify: `glFramebufferTexture2D` (wglGetProcAddress), RGBA32F / RG16F render targets, the menu card height, and the FPS cost on a large model (the pre-pass draws the opaque scene a second time at half resolution). Tuning constants are in `ScreenEffects` (`RADIUS`, `INTENSITY`, `MAX_DEPTH`). AO built and confirmed working (Gavin, 2026-10-14).
- AO is screen-space: occluders off screen or hidden behind the nearest surface don't count, so occlusion near the screen edges can fade as the view turns. Highlights on glass sample the AO of the surface behind.
- **Materials round, build B not compiled yet** (written without a .NET SDK or the Revit API; the C# was reviewed by eye). The GLSL was compiled, linked and run in WebGL2: plain colour, image, inverted image, proxy in the material's colour, appearance tint and image tint, in all three tint modes. Verify: WPF `Microsoft.Win32.OpenFolderDialog` (.NET 8+) in the review window, `Element.GetMaterialIds` on the review's element set, `new ElementId(long)`, WinForms `FolderBrowserDialog.InitialDirectory` / `UseDescriptionForTitle` in the app, the Textures panel layout on small screens, and the review pass time on Snowdon (stage 0 took ~8 s with image headers).
- Materials build B, checked on Gavin's tint test model (7 walls, Revit 2025): appearance tint = multiply over the whole look (after the image fade); fade and invert happen in linear light; invert applies to the colour image. A plain untinted brick (wall D) drew grey-green in BimGo but brown in Revit: open question, waiting on the exported `.bimgo` and the source JPEG. Tint colours were pure red only, so their colour space (`*_colorspace` = 2) is assumed sRGB. Proxies are drawn at the pack's own real-world size, in the material's shading colour by default. A build A `.bimgo` keeps its white placeholder colours for missing images (re-export to get the shading-colour fallback). A file opened where the proxy pack is missing shows plain colours.
- **Materials round, build A** built and works for its goals (Gavin, 2026-10-17). Originally written without a .NET SDK or the Revit API. The GLSL was compiled, linked and run in WebGL2 (a textured wall: upright, scaled and on the right material). Verify: Revit `AssetProperty.GetSingleConnectedAsset`, `AssetPropertyDistance.GetUnitTypeId` with `UnitUtils.Convert`, `AssetPropertyDoubleArray3d.GetValueAsXYZ`, `Face.ComputeDerivatives` / `GetBoundingBox` / `Project`; GL `glTexSubImage3D` / `glGenerateMipmap`, the anisotropy enum; the menu card height (508). Experimental: rolled back to the pre-materials zip if it doesn't work out.
- Realistic mode: no bump / normal maps, no cutouts (leaves and grilles are opaque), no reflections on opaque materials (glass only: a mirror would need a reflection probe). Texture alignment matches Revit closely on walls and floors (same size, rotation and coursing direction) but the start point of each pattern is world-anchored, not Revit's per-face origin. Missing textures (not found on the extracting machine) show the shading colour, or a CC0 proxy when the name suggests one (build B).
- Journal replay onto the scene lives in BimGo.App (`GameSession`), so the Core tests cover the journal's own rules (numbering, undo / redo, clone keys, push request) but not replay.

## 9. Changelog

### 2026-10-19: UX cleanup round, build B (Revit): tabbed Options window, model folders

- **Options window** in seven tabs: **Load** (what to load, phases) · **Categories** · **Geometry** · **Materials** (the colour mode moved here, above the extraction it depends on) · **Links** · **Parameters** · **Player**. The start position and Launch stay visible; the last tab is remembered (`LastOptionsTab`); a validation message switches to the tab of the field it is about. 760 × 720 instead of 740 × 900.
- **Player tab:** **Quality profile** (Custom / Basic / Medium / Realistic, the same `QualityProfiles` as the pause menu). Picking one sets the colour mode, anti-aliasing, shadow quality and lights here, ticks *Extract materials and textures* for Realistic, and carries AO, bloom and reflections to the walkthrough; changing any of those shows Custom. **"Also write them beside the model when its folder is writable"** (`SidecarsBesideModel`, off).
- **Model folders** (`Format/ModelFolders.cs`, Revit `Extraction/ModelFolderResolver.cs`): comments, bookmarks, sun and visibility of live sessions move from beside the model to `%LocalAppData%\BimGo\Models\<title>_<hash>\` with `model.json`. Older sidecars (beside the model, the old `Comments` folder, RvtGo `.rvtgo.json`) are copied in once and left as a backup. With sharing on, every write is mirrored beside the model and newer files there are taken at each Go. `.bimgo` files are unchanged; Export copies the folder's comments and bookmarks into the file as before.
- **Texture overrides** move into the same folder (`texture-overrides.json`), keyed by the model folder instead of `ProjectInformation.UniqueId` (shared by copies and template-derived projects); the old `%AppData%` file is copied in once. Older snapshots without a model folder still use the old file.
- Tests: `ModelFolderTests` (9).

### 2026-10-19: UX cleanup round, build A (app): hide-UI, quality profiles, reflections in the pause menu

- **Hide-UI mode (U):** hides the HUD, minimap, crosshair, gun bar, help, sun icon, gun markers / labels / tints and ordinary toasts; errors still show (`Toast(…, important: true)`). Every control keeps working, F12 screenshots as before. **Esc** (or U) shows the UI again and does nothing else on that press, even with Gizmo / Clone locked on. Pausing or opening the sun panel also shows it. Not saved. Gizmo / Clone keep their element tint while locked on.
- **Quality profiles** (pause menu, top of the right column): **Basic** (whitecard, 2x AA, AO, shadow quality Low, lights off, bloom 0, reflections off), **Medium** (material colours, 2x, AO, shadows Medium, glow + light, bloom 100 %, reflections Some + Sky), **Realistic** (Realistic colours, 4x, AO, shadows High, glow + light, bloom 100 %, reflections All + Probes 128). Profiles set shadow *quality* only; shadows on / off (O) stays the model's own choice. Probes HQ is never in a profile. The shown profile is recognised from the values, so any manual change reads **CUSTOM**. Core: `QualityProfiles` (Apply / Matches / Detect) and `LaunchSettings.QualityProfile` (re-detected on load).
- **Pause menu right column** is tabbed: **Display · Reflections · Debug** under the profile (one fixed card height, 440 px scaled, instead of the 668 px World & display card).
- **Reflections moved** from the sun panel to the Reflections tab; wording: *Reflections* **Off / Some / All** (was "Reflections (Realistic)" Off / Shiny (50 %+) / All (25 %+)), *Source* **Sky / Probes / Probes HQ** (was the sun panel's Reflect row), *Debug colours* **Off / Reflection / Probes** (was "Reflection tiers").
- **Sun panel** back to "SUN, SHADOWS & LIGHTS", 600 px tall.

### 2026-10-18: Reflection probes round, build B (experimental): reflection probes

- **Probes** (`Rendering/ReflectionProbes.cs`): one per Revit room holding a reflective surface (shine tier 25 %+ or water), a grid every ~8 m in rooms longer than 12 m, and fallback probes (8 m cells) for reflective surfaces outside rooms (outdoor water). Each is six 90° faces in one mipmapped RGBA8 texture array (128 px, or 256 px "HQ"; at most 64 probes / 64 MB). A plan lookup grid (0.5 m × 1 m bands) names up to two probes per cell with a blend weight (≈ 1 m across room boundaries); positions and room boxes are in a small float texture.
- **Shader:** reflective surfaces (and glass from inside) read their cell's baked probe instead of the sky, box-projected against the room box, mip level from roughness, blended at room edges; no probe yet = the sky as in build A.
- **Baking:** progressive, 2 faces per frame (the normal scene draw at probe resolution, culled to the probe's reach), nearest unbaked probe first. Re-bakes a second after the sun, sky, lights, colour mode / tint or the model (hide, move, clone, demolish, categories) change; old captures stay until replaced. A probe baked while the player was > 20 m away is refreshed once when the player comes within 10 m (lights and sun shadows are fitted around the player). Bake time logged.
- **Sun panel** (now "Sun, lights & reflections"): **Reflect** Off / Sky / Probes / Probes HQ, a status line (baked / total, baking, MB) and **REFRESH**. Saved: `ReflectionProbes` (on) and `ProbeResolution` (128).
- **Pause menu → World & display → DEBUG Colours:** Off / Reflection tiers / Probes (one colour per probe's cells, blended at edges; grey = sky). Not saved.
- **Water:** waves twice the size (less repetitive), same ripple strength.
- **B.1 (after the first look):** reflections at the foot of glass smeared (box projection stretches the floor close to the glass, and paints furniture flat onto it). Box-projected hits are now kept at least 0.75 m away and faded to 40 % when closer than ~1.5 m; smooth surfaces read probes half a mip down; probes sit at 1.7 m (above benchtops).

### 2026-10-18: Reflection probes round, build A (experimental): tiered sky reflections, water, debug colours

- **Revit:** extraction uses `ReflectivityReader` for every material (it replaces the glass-only `ReadReflectivity`). Glass keeps `reflectivity`; every other reflective material gets `shine`, `roughness`, `metallic`, and water gets `water` + `waterBump`. Roughness maps on Advanced materials are averaged once per image (cached). A material named "mirror" is drawn **opaque** whatever its appearance (mirrors modelled as glass). A **see-through** material named "water" is water (Advanced materials have no Water schema); opaque ones are left alone.
- **App:** the material table gains a sixth texel (shine, roughness, flags, ripple strength). In Realistic mode: shine rounds to the nearest 25 % tier and reflects when it reaches the threshold (50 % by default, 25 % optional; water always). Tier strengths head-on: metals 0.30 / 0.55 / 0.90, other surfaces half (Fresnel adds the rest at grazing angles); roughness blurs the sky and removes most of the grazing boost; metals tint the reflection with their colour; sky reflections are toned down by AO. Glass: Revit's value × 2.5, kept within 10–50 % (the sheen was too faint). **Water:** six travelling waves on the frame clock tilt the normal (faded with distance), plus a sun glint.
- **Pause menu → World & display:** *Reflections (Realistic)*: Off / Shiny (50 %+) / All (25 %+); *Reflection strength* 50–200 %; *Show reflection tiers (debug colours)*: red 75 %+, orange 50 %, yellow 25 %, grey none, cyan glass, blue water (not saved). The log's `Materials:` line counts shiny and water materials.
- **Core:** `SceneMaterial.Shine / Roughness / Metallic / Water / WaterBump / ReflectSource` (optional), `LaunchSettings.ReflectionThreshold` (50) and `ReflectionStrength` (1). Tests: `Materials_ReflectionFieldsRoundTrip`, `SceneMaterial_CleanClampsReflectionFields`, `Settings_ReflectionDefaultsAndSanitise`.
- Sky only: indoors, shiny surfaces still reflect the sky (dimmed by AO) until the probes of build B.

### 2026-10-18: Reflection probes round, stage 0 (experimental): reflectivity in the material scan

- **Revit:** new `Extraction/ReflectivityReader.cs` reads every material's reflection **strength** (0–1, the intent; floored to 25 % tiers) and **roughness** (0 sharp – 1 matt, the blur) from its appearance, by schema: Mirror → Water → see-through (glass, as today) → Prism (Advanced) → Generic → a finish enum on the simple schemas (Wall Paint, Ceramic, Stone, Concrete + sealant, Hardwood, Masonry/CMU, Metal, Metallic Paint, Plastic/Vinyl) → nothing. Finish enums are matched by the Revit enum member name, found by reflection, with ordinal guesses as the fallback. No keywords: water is the Water schema only.
- **Material scan report** (Options → Review textures… → Export report…) has a new **REFLECTIVITY** section: tier counts and how many materials / element uses would reflect at 50 % and 25 %, the finish enum values seen (Revit name vs the table's guess, mismatches flagged), water-named materials without the Water schema (listed only), schemas with no rule, and every used material with its tier, strength, roughness, flags and source properties. Each material in the dump gets a `Reflectivity:` line. The finished dialog adds a one-line count.
- Nothing else changes: no extraction, format or app changes yet (that is build A).
- **Stage 0.1 (after the scans):** tiers round to the nearest 25 %; Prism roughness from the connected roughness map's average, steeper curve; "mirror" in a material name makes it a mirror whatever its schema (mirrors modelled as glass); dark-tinted Mirror schema = glossy black; concrete Custom finish mapped; legacy 0-property presets use the graphics shininess when raised above 64.

### 2026-10-17: Materials round, build B (experimental): texture reconciliation, deep scan, proxies, tint and invert

- **Fallback colour:** a material whose image is missing, unreadable or procedural now draws in its **shading colour** (Revit's "shaded" look) instead of the appearance's render colour, which is often a white placeholder when a bitmap is connected. The render colour is kept (`renderColour`) and comes back when an image is added later.
- **Tint and invert:** the appearance's own tint (`common_Tint_toggle` on the asset) now applies to the whole look, colour and image (`assetTint`), on top of the bitmap tint. `unifiedbitmap_Invert` is honoured per material in the shader (a fifth table texel), not baked into shared images. The Textures panel has an **Apply Revit tint** switch. After the tint test model: tint is a multiply, and fade, invert and tint are blended in linear light like Revit's renderer (the trial "keep lightness" mode was dropped).
- **Review textures… (Revit, Options → MATERIALS & TEXTURES):** a resolve-only pass over exactly what the next Go would load (ticked categories or the active view, ticked links), with thumbnail, status and image path per material. Per material: Browse image…, Use proxy ▸ (suggested first), Plain colour, Clear override. **Scan a folder…** runs the deep scan one stage at a time (exact name → other extension → loose name; exact hits pre-ticked, loose ones ticked by hand; bump / cutout / reflection maps never offered; same-name files reported for you to choose), with "Remember this folder for all models". Choices go to the model's override file; nothing in the Revit model changes. **Export report…** writes the material scan report (now with a TINT AND INVERT section); the temporary ribbon button is gone.
- **Every extraction** reads the model's overrides first, then the locator with a new last stage: exact file names in the remembered search folders (indexed once and cached while each folder is unchanged). Where each image came from is recorded (`textureOrigin`).
- **App, Textures panel (pause menu → TEXTURES (n MISSING)):** the missing and proxy materials with IMAGE… / PROXY / PLAIN / UNDO, and FIND IN FOLDER… with the same staged scan. Picked images are encoded exactly as Revit would and embedded: Save writes them into the `.bimgo` (works without Revit). In a live session the choices also go to the override file, so the next F5 brings them back from Revit.
- **CC0 proxy pack** (`BimGo.App/Resources/Proxies`, 21 ambientCG colour maps at 512 px, `proxies.json` with real-world sizes and sources, THIRD-PARTY-NOTICES entry). Applied automatically to missing / unreadable images when the name or schema suggests a keyword ("Proxy textures for missing images", on by default); plain-colour materials only get one when you pick it. Proxies take the material's colour by default (the pattern keeps its contrast).
- **Core:** `TextureSearch` / `TextureFolderIndex` (the staged matcher, capped at 8 levels and 50 000 images, cancellable), `ProxyCatalog` (keywords, aliases, schema fallbacks, sizes), `TextureOverrideSet` (per-model choices), `MaterialData.With(…)` (a changed table that shares the vertex streams), `BimGoDocument.Materials` (the writer saves the changed set), `LaunchSettings.Clone()`. Tests: `TextureSearchTests`, `ProxyCatalogTests`, `TextureOverrideTests`, build B additions to `MaterialTests` (new fields round-trip, a build A table still loads, unknown fields are ignored, `With`, settings).

### 2026-10-17: Materials round, build A (experimental): Realistic colour mode, textures, glass reflections

- **Opt-in at Go / Export:** a new Options section MATERIALS & TEXTURES with "Extract materials and textures" (off by default: light models stay light, and nothing changes when off) and a max texture size (256 / 512 / 1024 / 2048 px). It shows whether the Autodesk Material Library and Revit's additional render appearance paths were found on this machine. Colour gains a third choice, **Realistic (textures)**, which ticks the extraction.
- **Extraction** (`SceneExtractor.Materials.cs`): per used material, the render colour and colour texture from its appearance schema (Generic `generic_diffuse` + image fade, Advanced `opaque_albedo`, Metal `metal_f0`, Layered `layered_diffuse`, Hardwood `hardwood_color`, simple schemas' `*_color`; Prism `surface_albedo` is the reflection map and is never used as the colour). Placement comes from the bitmap (real-world size and offset in any unit → metres, angle, tint). Images are found with `TextureLocator`, which reads Revit.ini and the registry each time (nothing hard-coded). They are downscaled to the cap, re-encoded as JPEG, embedded once each and cached for the Revit session, so F5 reuses them. Surface coordinates per vertex: planar walls U horizontal / V up, floors and roofs plan X / Y (world-anchored, so coursing and boards line up across faces), curved faces from the face parameters scaled to metres, free meshes box-mapped. Coordinates come from each mesh's own points, so textures move with families.
- **Format** (additive, `formatVersion` stays 1): `materials.json`, `material.bin`, `textures/`. Older builds ignore them. Damaged or mismatched parts are repaired or dropped without failing the load.
- **App:** the Realistic colour mode (pause menu: Whitecard / Material / Realistic). Images go into mipmapped texture arrays by size (256² … 2048²) with anisotropic filtering where available; the material table is an RGBA32F texture; one draw path for every material (`MaterialTextures.cs`, `Shaders.MATERIALS_GLSL`). **Sky reflections on glass**: Fresnel-weighted sky colour (follows the sun panel's time of day), switchable in the menu. A snapshot without materials shows material colours in Realistic mode (toast), and the choice is kept for the next one.
- Core tests: `MaterialTests` (round-trip, shared image stored once, absent, wrong count not written, damaged parts, clean-up, settings).

### 2026-10-17: Materials round, stage 0 (experimental): material scan diagnostic

- New temporary ribbon button **Material scan** (Revit). It writes a read-only report of the model's and its loaded links' materials to `%LocalAppData%\BimGo\Logs\MaterialScans\`. The report covers usage counts, shading colour, appearance schema, a full property dump (connected texture assets included), where each bitmap was found (or that it's missing), whether a path looks Autodesk-supplied, image sizes and formats, and estimates of the embedded and GPU size at 256–2048 px.
- New `Extraction/TextureLocator.cs`, reused by the material extraction later. It probes for the Autodesk Material Library (standard folders, registry) and Revit.ini additional render appearance paths, and never assumes they exist. It resolves bitmap paths in stages: absolute → library root → additional paths → model folder → file name in the library's `n\Mats` folders.
- No format, protocol or app change. Decisions for the round: `ai/261017_Materials/1_build notes stage 0.md`.

### 2026-10-16: Gizmo / Clone: separate move and rotate modes, vertical moves

- Locking on (and every new clone) starts in **move** mode: WASD move in plan relative to the view, **E raises, Q lowers**. **R** switches to **rotate** mode: **A / D** turn CCW / CW about the vertical axis through the pivot (XY plane only: families stay level). RMB commits, Esc cancels, as before.
- **Z / X** step the current mode's snap increment (move distance or angle); C / V no longer used. Snapped E / Q step by the move increment (Z clamped to whole increments like X / Y).
- Gizmo drawing follows the mode (move: plan arrows + up / down arrows, faint ring; rotate: bold ring and heading tick); the panel shows MOVE / ROTATE, and the Δ readout adds Z when raised or lowered.
- No format or protocol change: edits already carried a 3D translation (Revit `MoveElement` and the file journal apply Z). Revit may refuse or adjust a vertical move for some hosted / level-constrained families (the walkthrough reverts it with Revit's message).

### 2026-10-16: Lights: cached shadow maps instead of room clipping; bloom control

- Room clipping gave hard cut-offs at door thresholds (Gavin). Each light now has a cached omnidirectional shadow map (`Rendering/LightShadows`), so light goes through doorways and stops at walls and under furniture. Room boxes removed.
- Sun panel: *Light* and *Bloom* sliders side by side; `LaunchSettings.BloomIntensity` (0–2, default 1; 0 = no bloom).

### 2026-10-15: Rendering round 2: artificial lights and glow

- **Glow:** materials with Revit self-illumination glow anywhere; inside Lighting Fixtures, lamp / LED / lens / diffuser… materials (`LaunchSettings.EmissiveKeywords`) and, failing those, the bottom faces of raised fixtures glow too. A bloom spreads it (half-res pre-pass target → quarter-res blur → added over the scene).
- **Lights:** one per lighting fixture (output and colour temperature from its parameters when readable), nearest 32 in view each frame, soft falloff, downward lobe, a little fill for bounce. Daylight dims them; night shows them off.
- **Controls:** K cycles off / glow / glow + light; sun panel (now SUN, SHADOWS & LIGHTS) has the mode and *Light* / *Bloom* sliders; Revit Options → **Artificial lights** sets the launch mode. Settings: `ArtificialLights` (default glow + light), `ArtificialLightIntensity`, `EmissiveKeywords`.
- **Format:** optional `lighting.json` (no version bump; older readers ignore it). `SceneData.Lighting` (Core `LightingData`, `EmissiveRun`, `LightSource`).
- `AmbientOcclusion` → `ScreenEffects` (the pre-pass now also writes glow; AO unchanged). `Gl`: `DrawBuffers`, `Uniform4` arrays, `RGBA16F`, `COLOR_ATTACHMENT1`.
- Tests: `LightingTests` (round-trip, absent, damaged entries, packing, luminance, kelvin, settings).

### 2026-10-14: Rendering round 1: ambient occlusion

- **Ambient occlusion** (SSAO): darkens corners, junctions, skirting, furniture against walls and objects on floors. Half-resolution geometry pre-pass, AO and depth-aware blur in the new `Rendering/AmbientOcclusion`; applied to the ambient term only in both the sun and classic lighting, and to the ground. Pause menu → WORLD & DISPLAY → **Ambient occlusion** (on by default; `LaunchSettings.AmbientOcclusion`, older settings files read as on).
- `Gl`: `FramebufferTexture2D` and the float format constants. `sunLight()` takes the AO factor.
- Tests: the new setting's default and older settings files.
- First of the rendering rounds agreed after 1.0 (AO → HDR / tone mapping → material table → emissive + triplanar textures → point lights); see `ai/261014_Rendering_AO/1_build notes AO.md`.

### 2026-10-13: Dependencies round: Silk.NET GL bindings, Core tests, MIT licence

- **OpenGL bindings:** `Native/Gl.cs` is now a thin facade over **Silk.NET.OpenGL 2.23.0** (BimGo.App only). Same `Gl.Xxx` names, signatures and `uint` constants, so no renderer or UI code changed; the ~75 hand-written function pointers and their load table are gone. Every entry point BimGo uses is still checked at startup with the same "Update the graphics driver" message; Silk.NET then resolves each one lazily through the same `wglGetProcAddress` / opengl32 lookup (`Gl.GetProc`, still used by `Wgl`). Forwarding allocates nothing. `Native/Wgl.cs` (context creation, 4.1 → 3.3 fallback, swap interval) is unchanged.
- **Tests:** new `tests/BimGo.Core.Tests` (MSTest.Sdk 4.4.1, net8.0, BimGo.Core only, in the solution's `tests` folder): `.bimgo` round-trips (geometry, elements, links, site, journal, bookmarks with home and thumbnail, sun, visibility, comments, parameters), optional entries, atomic replace and cancelled save / read, early-layout files, newer / foreign / damaged files, journal rules and the push request, sun position against an independent reference (Sydney, London, Adelaide), settings sanitising and link choices, progress maths and cancellation, the live folder channel (round-trip, order, wrong session, newer protocol, 4 MB cap, duplicates) and the sidecars. Tests log to `BimGo.Tests.log` and use temp folders only.
- **Fix (found by the tests):** `LaunchSettings.SetLinksFor` kept at most 200 models' link choices by removing `Keys.First()`, but a `Dictionary` reuses freed slots, so once full it dropped the choice just made instead of the oldest. It now rebuilds the dictionary in recency order.
- **Licence:** MIT (© Aussie BIM Guru) replaces the Unlicense. `THIRD-PARTY-NOTICES.txt` lists Silk.NET and its MIT dependencies; both files are copied beside `BimGo.exe` on build (`LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`).
- README: the dependency policy replaces the old "No dependencies" rule (AI item 3), dependency table (§10).

### 2026-10-12: 1.0.0: saved home, bookmark thumbnails, bookmark cancel

- **Saved home:** Shift+H / SET HOME HERE now saves home with the model (`bookmarks.json` → `home` in a .bimgo, or the bookmarks sidecar beside the Revit model), and walkthroughs of that model start there (before the active 3D view or a random spot; a reload still keeps where you stood). Setting home is acknowledged with a sound, a flash, a note and, in the pause menu, the button reading HOME SAVED HERE for two seconds. Export .bimgo now carries the model's bookmarks and home too (like its comments).
- **Bookmark thumbnails:** a 192 × 108 JPEG (base64 `thumbnail`, a few kB) of the 3D view (no HUD) is taken the frame after B, ADD THIS VIEW or SET HERE, and shown in the BOOKMARKS list (older bookmarks show NO PICTURE until SET HERE). `UiBatch.Image` draws textures in order with the batch.
- **Esc cancels a new bookmark:** B now prepares the bookmark and only adds it when the name is confirmed with Enter; Esc throws it away (nothing saved, the file isn't marked changed).

### 2026-10-12: 1.0.0: stair climbing

- **Stairs climb reliably.** The capsule's rounded bottom used to hang on the nosing: the old step-up probed only one tick (~3 cm) ahead, its landing contact read as a wall, and the climb was refused, so the step height setting seemed to do nothing. Now a contact on the rounded bottom that is no higher than the **max step height** above the feet (and whose triangle doesn't reach higher, so steep slopes and walls stay walls) lifts the player straight up, rolling over nosings, riser tops, kerbs and stringer edges like a short ramp, for square, sloped, open or rounded risers and at any angle of approach. Risers taller than the rounded bottom can ride (step height above ~0.27 m) use a step-up that probes a capsule radius ahead. The max step height (Options, default 200 mm) now means exactly that.

### 2026-10-12: 1.0.0 polish: progress bars, sun time readout, help panel, wording

- **Progress with Cancel** for the long tasks. Revit: Go, Export and refreshes (F5 / Send a fresh snapshot) show a progress window (stage, element count, bar, Cancel; it appears after half a second, on its own thread so it stays responsive while Revit works). Cancelling stops the extraction or the file write; nothing in the model changes, an export or snapshot is not written, and a cancelled refresh tells the walkthrough. App: opening a file, joining / reloading a live session, preparing the scene and saving show a progress screen (CANCEL or Esc; a cancelled save leaves the file on disk untouched).
- **[ ]** now shows the date and time reached on each step, with the sun's height and direction (e.g. "21 Jun 14:35 · sun 32° high in the NW").
- **F1 help panel** sizes itself to its text.
- **Wording review** across the app, the Options dialog and the Revit messages: one name per thing (Demolish gun, not hammer; "not connected to Revit" instead of "no Revit link", so it can't be confused with linked models; portals "connect"), full sentences in dialogs, the same Esc → BUTTON pattern for menu hints.
- Smoke tested on Revit 2025, 2026 and 2027 (Gavin, 2026-10-11).

### 2026-10-11: 1.0.0: active view only, helper geometry, hide / isolate, screenshots

- **Build fix:** `Gl.SRC_COLOR` (used by the glass shadow pass) was missing.
- **Active view only** (Options, off by default): the view decides what comes in, for the host and ticked links (`ViewScope`, Revit 2024+ link view collector); unlisted model categories go to the new catalog entry **Other (active view)**; 3D views read host geometry through the view. Rooms, spaces, areas, link instances, model groups, assemblies, cameras and model lines are never geometry.
- **Helper geometry** (Options, on by default): Light Source subcategory (IES cones) always left out, plus subcategories matching editable keywords; logged once per subcategory.
- **Ground plane** defaults to 100 mm below the lowest level.
- **Hide / isolate** (Scan gun): I hides the target in the walkthrough only, Shift+I isolates its category; pause menu SHOW ALL. Category / link toggles and hidden elements are **saved with the model** (`visibility.json` / live sidecar) and restored on open.
- **F12 screenshot** to Pictures\BimGo (scene only, PNG encoded on a worker thread).
- **Version 1.0.0** on all assemblies (`Version`, `AssemblyVersion`, `FileVersion`); version strings are now `1.0.0`; Options title and F1 help show it.

### 2026-10-10: v7: linked models

- **Options → LINKED MODELS:** every link instance, grouped by file (a file tick box sets all its instances); none ticked by default; unloaded links greyed out; the choice is saved per host model and reused by F5 / Send a fresh snapshot. The footer estimate counts the ticked links.
- **Extraction:** ticked instances are extracted with the host's categories and triangle limit, through their total transform, in the link's phase named like the host's new phase (else its last). Per-source material, category and level caches (ids are per document). Link rooms feed the room readout where the host has none. The scene origin takes linked elements into account.
- **Format (additive):** `model.links[]`, `elements[].link`, `rooms[].link`, `counts.links`.
- **App:** linked elements are read-only (Demolish refuses with the link's name; Gizmo / Clone show it as the reason), skipped by the id / unique-id / host lookups (no clashes with host ids), and comments on them record no element id. Scan shows a **Model** row; R selects the element inside its link in Revit (older add-ins select the link). Pause menu **LINKED MODELS** toggles per link (render batches are now per model and category, so a hidden link costs nothing). The load toast counts the links.

### 2026-10-09: v6: sun, shadows and time of day

- **Shadows** (O, off by default): cascaded shadow maps (depth texture array, hardware PCF, texel-snapped bounding-sphere cascades, normal-offset bias), cast and received by the static scene, moved / cloned elements and the ground. Only cascades whose fit, the sun or the casters changed re-render; far cascades refresh every 2nd–4th frame while walking. Off frees the maps.
- **Glass:** a transmittance layer per cascade (glass multiplied in front of the first opaque surface), from each material's transparency and tint, scaled by the glass slider.
- **Sun panel** (Shift+O or the bottom-right sun icon): shadows and quality, time slider with play, month / day boxes, DST, sun height / bearing, sunlight / sky / shadow / glass intensities. Sky colours, the sun disc, fog and ambient follow the sun's height (night keeps a little sky light).
- **Solar maths** in Core (`SolarPosition`): Gavin's `SunPosition` with the NOAA declination / equation-of-time series; true north from the extraction-verified shared angle.
- **Revit:** extraction captures `SiteLocation` and the launch view's sun-study start; Options dialog has **Shadow quality**.
- Saved with the model (`sun.json`, live sidecar written ~1.5 s after the last change); bookmarks keep the sun time.

### 2026-10-08: v5.1: redo, viewpoint bookmarks, coordinate readout

- **Fix:** `PhaseResolver` no longer uses `ElementOnPhaseStatus.NotApplicable` (not in the Revit API); `None` covers unphased elements and failed lookups.
- **Redo** (files): Ctrl+Y or Ctrl+Shift+Z puts the last undone edit back (replays that one entry); any new edit ends the redo history; undone clone keys stay reserved so a redo never collides.
- **Viewpoint bookmarks:** B saves the viewpoint and asks for a name; Ctrl+1–9 jump; pause menu BOOKMARKS list (GO, RENAME, SET HERE, reorder, DELETE, ADD THIS VIEW); blue minimap dots. Saved in `.bimgo` (`bookmarks.json`) or the `<model>.bimgo-bookmarks.json` sidecar in live sessions; count towards unsaved changes in files.
- **Coordinate readout** (L): shared / project / internal coordinates of the crosshair point (double precision). Extraction now captures the internal → shared transform (`model.site.shared*`); older files fall back to the survey point ("≈").
- Pause menu buttons tighten on short screens so the extra entry fits above END SESSION.

### 2026-10-07: v5: push to Revit, existing / new phases, file association, comments QoL, snap defaults

- **Phase 4, `journal.apply`:** pause menu → PUSH TO REVIT (files). Matching live session by model key, temporary channel, dry-run preview with per-edit status, conflicts skipped by default (5 mm, "apply anyway" re-checks), one Revit undo step (`TransactionGroup.Assimilate`), report panel + CSV, pushed entries marked in the journal. Large requests by file. The switch prompt mentions pushing when the arriving model is the file's.
- **Existing / new phases** (Options → PHASES): the walkthrough shows the new phase (elements built later or demolished by it are left out); demolish = new phase, existing elements only (hammer explains and suggests T for others; Revit enforces); clones created in the new phase; Scan shows each element's phase role; home screen shows "Existing → New". Stored in `.bimgo` (`model.existingPhase*`, `elements[].phase`), `session.json` and `hello.ack`.
- **File association / --register:** HKCU `.bimgo` → `BimGo.Model` (icon from the exe), "Open with" entry, Start-menu shortcut; the installed copy self-registers on start (unless `--unregister` opted out).
- **Comments QoL:** E edits the hovered comment (edited / editedBy recorded); pause menu COMMENTS list with level filter, GO (teleport in front), EDIT, DELETE, EXPORT CSV.
- **Gizmo snap defaults** in the Options dialog (checkbox + move / angle increments).
- Undo in files no longer removes edits that are already in Revit.

### 2026-10-06: App icon, build fixes, v4 handoff

- **">>" icon** everywhere the apps show one: `BimGo.exe` (`ApplicationIcon`, `BimGo.App/Resources/BimGo.ico`), the game window's title bar and taskbar, the Revit **Go** button and the Options dialog, and the home-screen title mark.
- Build fixes: `DBEvents.UndoOperation` in `LiveDispatcher`; the sessions panel in `HomeScreen` is always drawn (a `??=` short-circuit left `used` unassigned).
- Remaining work handed over in `ai/261006_V4/0_BimGo v4_Handoff.md` (Phase 4 `journal.apply` first).

### 2026-10-06: v3 phase 2 (+ phase 3 QoL): live sessions

- **Live sessions replace the in-Revit game.** Go → snapshot in the document's session folder → BimGo.exe joins (or the running app asks to switch). The add-in no longer references the engine.
- Core `Live/`: protocol, `FolderChannel` (atomic files, watcher + poll, de-dupe), `LiveSessions` (discovery, cleanup), `LiveSessionSource` (edits, heartbeats, notices, refresh, select).
- Revit `Live/`: `SessionHost` (session.json heartbeat, app attachment, snapshots, change counting), `LiveDispatcher` (one ExternalEvent for all sessions, doc closing / changed events, ribbon status). `RevitBridge` became `RevitEditor`.
- App: home screen lists running sessions; `--session <id>`; inbox `attach` requests; switch prompt; reload on new snapshots keeping the player's pose; LIVE / OFFLINE badge; model-changed banner + F5 refresh; Scan → R shows in Revit.
- Ribbon **Live** button (text shows off / ready / attached): bring the app forward, send a fresh snapshot, end the session.
- **Gizmo snap increments:** G toggles snap mode (persisted), Ctrl inverts while held, Z/X and C/V step the move (5 mm–1 m) and angle (1°–90°) increments; snapped moves step per key press along the nearest world axis.

### 2026-10-05: v3 phase 0 + 1: BimGo (restructure, .bimgo, standalone app)

- **Rename and split:**
  - RvtGo → BimGo in three projects: Core, App (`BimGo.exe`) and Revit.
  - New AddInId; BimGo ribbon tab with **Go** and **Export .bimgo**.
  - Settings and logs move to `BimGo` folders, and the RvtGo settings and comment sidecar migrate automatically.
- **.bimgo format:** a ZIP of JSON and binary geometry, with versioning, validation and atomic writes.
- **Export .bimgo:**
  - Options, then a save location, then extraction.
  - Comments are embedded in the file.
  - Offers to open the file in BimGo.
- **Extra parameters:** a picker in Options (Scan model, filter, up to 24) feeds the Scan gun. Values go into a pooled string table.
- **Extraction:** UniqueId, host id, provenance (model key, cloud ids), true north, base and survey points.
- **Standalone app:**
  - Home screen, Open, recent files and drag-and-drop.
  - Single instance with an open-request inbox.
  - Close model returns to Home; unsaved changes prompt before closing.
- **Edits:**
  - `IModelSource` replaces the direct bridge, and the `EditJournal` records every accepted edit.
  - Journal replay on load; Ctrl+Z undo (files); Ctrl+S / Ctrl+Shift+S save.
  - Save as `.bimgo` from a Revit session.
  - HUD badge shows REVIT or FILE (`*` when unsaved).

### 2026-10-04: QoL: room readout, symbol gun bar, four new guns, Revit write-back

- Room readout, symbol gun bar, Teleport / Demolish / Gizmo / Clone guns, and the `Bridge/` write-back via an `ExternalEvent`.
- Engine: degenerate-index hiding, dynamic instances, dynamic picking and collision, multi-highlight, and input capture for guns.

### 2026-10-01: Doors simplified, global usings

- Door open/close system removed: doors render as modelled and are always no-clip.

### 2026-10-01: v1 (first full pass)

- Template fork, WPF options, extraction, engine (GL, batching, MSAA, sky, minimap, HUD, pause menu), physics, and the Scan / Measure / Portal / Comment guns.

## 10. Dependencies

The policy is in *For AI assistants* item 3. BimGo.Revit ships no packages. Licence texts are in `THIRD-PARTY-NOTICES.txt`.

| Package | Version | Licence | Used in | Why |
|---|---|---|---|---|
| Silk.NET.OpenGL | 2.23.0 (pinned) | MIT | BimGo.App (`Native/Gl.cs` only) | OpenGL function bindings: no hand-written unmanaged signatures for new GL calls |
| Silk.NET.Core, Silk.NET.Maths | 2.23.0 (transitive) | MIT | BimGo.App | Required by Silk.NET.OpenGL (loader / vtable; maths types unused by BimGo) |
| Microsoft.DotNet.PlatformAbstractions, Microsoft.Extensions.DependencyModel (+ small System.* packages) | transitive | MIT | BimGo.App | Required by Silk.NET.Core |
| MSTest.Sdk | 4.4.1 (pinned in the Sdk attribute) | MIT | `tests/BimGo.Core.Tests` (dev-only) | Test framework and runner; ships nothing |
