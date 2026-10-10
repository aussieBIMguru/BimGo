# BimGo: Handoff for a new chat (feature rounds A–C → hotkeys → polish → installer)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**, **MIT-0** (relicensed from MIT 2026-10-10), C# / Visual Studio, .NET 8,
custom OpenGL renderer over Silk.NET). He works in **rounds**: discuss → stage questions with AskUserQuestion
(recommended option first) → build → deliver a zip + `ai/<date>_<topic>/` notes + README + a project doc.

**Read first:**
1. This brief.
2. `README.md`: *For AI assistants*, §3 controls, §5 how it works, §8 limitations / to verify, §9 history.
3. The last three rounds' notes: `ai/261010a_BCF_SunHours`, `ai/261010b_SectionBox`, `ai/261010c_PhotoMode`.
4. `claude/BimGo_Installer_Handoff.md` (project doc): the installer plan, decisions and open questions. Still valid.
5. **Ask Gavin for a fresh zip of his working copy before editing.** His copy is the source of truth.

---

## 1. Where BimGo is now (all confirmed working by Gavin, 2026-10-10)

| Round | What it added |
|---|---|
| BCF + sun hours (`261010a`) | Plain BCF 2.1 export / import of comments (`.bcf`, PNG snapshots, merge by GUID, shared / project / internal coordinates); sun study save / load |
| Daylight (build B) | Study modes **Sun hours / Daylight % / Lux** (CIE overcast / clear sky + sun, 0.7 m editable work plane), general **PASS / FAIL** toggle (no regional terms), colour overrides |
| Section box (`261010b`) | Box + one free plane, stencil caps in one flat user colour (picker), P / Shift+P / Ctrl+P, cuts saved in visibility, bookmarks, comment views and BCF clipping planes |
| Photo mode (`261010c`) | M: clean frame, stills 1–4× (PNG / JPEG), 360° equirectangular 4K / 8K with Photo Sphere XMP, exposure ±3 EV, FOV, sun time, thirds grid, `Pictures\BimGo` + OPEN FOLDER |

Minimap stays as is (no room names, no teleport from it).

## 2. Plan for the next chat (agreed with Gavin, 2026-10-10)

Features were reviewed and approved here. Build them in **Rounds A and B**, then the stretch goal **Round C (clash detection)**, then the hotkey review, polish and installer.
Stage the detailed questions for each round with AskUserQuestion (recommendation first) before building.

### Round A: presentation
1. **Bookmark tour.** Plays the bookmarks in order, hands-free.
   - **Transitions:** fade out / in between stops (Gavin's preference over flying between them). A glide option can
     be offered, but fade is the default.
   - **Optional swivel** at each stop: a slow pan, with an adjustable pace (and probably the pan angle and direction).
   - **UI included or UI-free** toggle.
   - **Hold time** per stop (time between changes); per-bookmark override is a question to ask.
   - Start / stop / next / previous controls; Esc stops. Sections, sun time and visibility saved with each bookmark
     apply at each stop.
2. **Walkthrough video = record the tour.** One button renders the tour to **MP4** using its fades, pacing, swivel and
   UI setting. Render off-screen at a fixed frame rate (not real time), so it's smooth on any PC; reuse photo mode's
   `RenderScene(..., photo: true)` path and targets. Encode with **Windows Media Foundation** (H.264, in-box, no NuGet
   package; P/Invoke or COM interop in BimGo.App). Questions: resolution (window / 1080p / 4K), frame rate (30 / 60),
   output `Videos\BimGo` vs `Pictures\BimGo`, a progress bar with cancel.
3. **Eye-height presets.** Standing (current), seated / wheelchair, child; a selector (pause menu and / or a key) and
   a setting; collision, crouch and step-up stay correct. Heights editable; no regional standard names.

### Round B: review tools + quality of life
1. **Measure extras** (Measure gun): area (closed polyline on a plane), angle (three points), floor-to-ceiling
   clearance (vertical from the aimed point). Keep N (normal projection); a mode key or RMB cycle is a question.
2. **Snapshot markup:** draw arrows, circles / rectangles, freehand and text over a comment's picture before saving;
   colour choice; undo. Burn the markup into the saved PNG (so BCF snapshots carry it); question whether to keep an
   editable layer too.
3. **Metric / imperial units:** one setting for measurements, coordinates, study heights / work plane, the section
   panel and readouts. Imperial as feet-inches and decimal feet is a question; internal maths stays in metres.
4. **Key rebinding:** an action map (actions → keys, with Shift / Ctrl variants) in settings, a **Controls** tab to
   remap with conflict warnings and reset to defaults; F1 help and the README generated from the same map. This
   builds the system; the hotkey review afterwards picks the **default** map.
5. **Gamepad:** Xbox-style controllers via **XInput** (`xinput1_4.dll`, in-box, no package): move / look, jump,
   crouch, run, gun select and fire, menus. It uses the same action map (rebindable is a question).
6. **Start screen:** when BimGo opens without a file, recent `.bimgo` files with thumbnails, OPEN…, and the last
   live session if one exists. Recent list kept in settings.

### Round C (stretch goal): clash detection (agreed 2026-10-10)

Runs **in the BimGo app** on the snapshot's triangles (files and live sessions alike), on a worker with progress and
cancel; no Revit needed. Data per element is already there: triangles, bounds, category, `FamilyType`, level, link,
`UniqueId`, `IfcGuid`, extra parameters.

1. **Tests:** **hard** (intersection beyond a tolerance, e.g. ignore ≤ 5 mm) and **clearance** (closer than a
   distance). Each test = set A × set B (or A × itself), tolerance / distance, name. Broad phase on element bounds
   (sweep and prune or a grid), narrow phase triangle–triangle (intersection; distance for clearance). Skip an element
   against itself and host ↔ hosted inserts (`HostId`); flag proxies (bounding-box stand-ins) as approximate.
   Containment and duplicates were considered and left out.
2. **Search sets, worked in progressively** (Gavin: wants all, in stages):
   - **C1:** rules on category / family / type (equals / contains, include / exclude, AND / OR) + level + linked model.
   - **Later:** extra-parameter rules (only the parameters chosen at export), and hand-picked selection sets (Scan
     gun add / remove).
3. **Storage:** tests, sets and results saved **in the `.bimgo`** (a new optional part, e.g. `clashes.json`; older
   readers ignore it, no format bump) **and in the model's BimGo folder** (shared like comments). **EXPORT / IMPORT
   REPORT** (BimGo JSON) to compare against any externally cached run. No Navisworks import; other tools' clashes can
   still arrive as BCF topics.
4. **Revisiting:** a clash is matched across runs by its **element pair (UniqueIds, plus link key) and test**. Status
   **New / Active / Reviewed / Approved / Resolved**; a clash gone on re-run becomes Resolved (and comes back as Active
   if it reappears). Assignee and notes kept; run history with counts per status (did we clean them up?).
5. **Review:** a **clash panel** (list, filters by test / level / status) with **GO**: fly to the clash point,
   auto section box around it, the two elements in red / green, the rest ghosted. **To comments / BCF:** turn chosen
   clashes into comments (view + picture), so the existing EXPORT BCF carries them.
6. **Grouping logic** (Gavin: handy, to review during development): e.g. group by element (one duct hitting many
   beams), by level, or by proximity (clashes within a distance). Prototype and choose with Gavin.

Questions to stage at the start of Round C: tolerance defaults, panel key (only C / Y free unless the rebinding work
frees more), per-run size limits, how ghosting looks, whether GO's section box should stay after leaving the panel.

### Then
1. **Hotkey review:** choose the default keyboard and gamepad map with Gavin (§3 is the current map), then update
   help and README §3.
2. **Final polish:** small fixes Gavin raises, plus the open README §8 "to verify" items.
3. **Installer:** follow `claude/BimGo_Installer_Handoff.md` (Inno Setup, per-user, self-contained app, Revit
   2025 / 2026 / 2027 `.bundle`, no auto-update, no signing for 1.0). Its §4 questions are still open.

**Considered and left out (Gavin, 2026-10-10):** phase before / after view, element search, headroom check.
**Minimap stays as is.**

## 2a. Licensing review (2026-10-10): to do in the polish / installer rounds

Audit result: nothing GPL / copyleft, no copied third-party code, no shipped proprietary files. Shipped third-party
parts are Silk.NET + transitive .NET packages (MIT) and ambientCG textures (CC0), all already in
`THIRD-PARTY-NOTICES.txt`. Revit API dlls are referenced with `Private=false` (never shipped). UI fonts (Bahnschrift,
Segoe UI, Consolas) are the user's installed Windows fonts, rasterised at run time (no font files shipped). Published
methods are implemented from their formulas (formulas aren't copyrightable): NOAA solar position (US public
domain), CIE overcast / clear sky, IES clear-sky illuminance, BRE split-flux, McGuire et al. SAO-style AO (own GLSL),
Möller–Trumbore, Hammersley points, Schlick Fresnel, PCF; open formats: BCF 2.1 (buildingSMART), IFC GUID, Google
Photo Sphere XMP.

Add:
1. **Trademark notice** (done in `THIRD-PARTY-NOTICES.txt`, 2026-10-10; still to add to README, About / help, installer, `.bundle` description): "Autodesk and Revit are registered
   trademarks of Autodesk, Inc. BCF is a buildingSMART International standard. BimGo is an independent project, not
   affiliated with or endorsed by Autodesk or buildingSMART." Name the add-in as "BimGo for Revit" (descriptive use),
   never Autodesk logos or "Autodesk BimGo".
2. **Acknowledgements section**: done in `THIRD-PARTY-NOTICES.txt` (2026-10-10), with a licence-review / contact statement (FOSSA cross-check); keep it current: the methods and formats above
   with references.
3. **Recheck the publish folder** against the notices at installer time (transitive System.* / Microsoft.* packages,
   the self-contained .NET runtime: MIT, plus its own THIRD-PARTY-NOTICES which the runtime ships; include it).
4. **Keep the study disclaimer** ("early design indicator, not a compliance simulation") in the UI and CSV.
5. **Round A / B new parts stay in-box:** Media Foundation H.264 encoder and XInput are Windows components (Microsoft
   covers the codec licence for in-box use); no codec or library is bundled. If a bundled encoder (e.g. FFmpeg,
   x264 = GPL) is ever proposed, stop and ask Gavin.
6. **Licence is MIT-0** (Gavin, 2026-10-10: no attribution required; disclaimer kept). Installer licence page shows the
   MIT-0 `LICENSE`; `claude/BimGo_Installer_Handoff.md` still says MIT (§3 row 5): MIT-0 replaces it. Third-party
   MIT notices must still ship beside `BimGo.exe`.

## 3. Current key map (from code + README §3)

**Global**

| Key | Action |
|---|---|
| WASD / arrows, mouse | Move, look |
| Space · Shift · Ctrl | Jump · run · crouch (fly: up / down) |
| V | Fly / no-clip |
| 1–9, wheel · LMB / RMB | Gun select · gun primary / secondary |
| Page Up / Down | Teleport up / down a level |
| H / Shift+H | Go home / set home |
| B · Ctrl+1–9 | Bookmark view · jump to bookmark |
| L | Coordinate readout (off → shared → project → internal) |
| K | Artificial lights (off / glow / glow + light) |
| O · Shift+O | Shadows · sun panel |
| J | Sun / daylight study panel |
| P · Shift+P · Ctrl+P | Section box editor · quick plane behind aimed surface · clear cuts |
| [ / ] | Sun time −/+ 5 min (Shift: 1 min) |
| M | Photo mode |
| Tab | Minimap |
| U | Hide UI |
| X | Clear this gun's markers (Comment gun: twice = delete all) |
| Ctrl+F | Find room |
| Ctrl+S · Ctrl+Shift+S | Save · save as |
| Ctrl+Z · Ctrl+Y / Ctrl+Shift+Z | Undo · redo |
| F1 · F5 · F11 · F12 | Help · live refresh from Revit · fullscreen · quick screenshot |
| Esc | Pause menu / cancel / close panel / show UI |

**Gun-context keys:** N (Measure: normal projection), T (Demolish: demolish / delete), E (Comment: edit hovered),
R / I / Shift+I (Scan: select in Revit / hide / isolate category), F (Gizmo aiming: drop onto surface).
Gizmo / Clone / Place locked on: WASD plan move, E / Q up / down, R move ↔ rotate, G snap (Ctrl inverts), Z / X
increment, F drop / lift, RMB commit, Esc cancel.

**Panel-local keys:** photo mode (Enter shoot, RMB look, wheel FOV, [ ] sun, M / Esc close); section editor
(drag handles, Shift = no snap, P / Esc close); study (RMB look, clicks pick surfaces).

**Free plain letters:** **C** and **Y** only (Y is used only as Ctrl+Y). Plus unused function keys (F2–F4,
F6–F10), Home / End / Insert / Delete, backtick, and most Alt combinations. Points for the review:
E, F, R, X, Z, Q, G, T, N, I mean different things per gun / mode; O / Shift+O and J are related sun features on
different letters; F12 screenshot vs M photo mode.

## 4. Things to watch

- `GameSession` and `SceneRenderer` are split across partial files: scan all partials for duplicate member names
  before adding any.
- The mode check order in `GameSession.Update` matters (photo → section editor → study → …); panels close each other
  (`ClosePhotoMode`, etc.).
- Shaders: check new / changed GLSL in headless WebGL2 (Playwright + Chromium) where possible; C# can't be compiled
  in the sandbox, so say so.
- For simple build errors Gavin wants **just the fix**, not a new zip.
- No regional terminology in features or UI.

## 5. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; no per-frame allocations.
- BimGo.Revit stays package-free; any new package needs Gavin's yes (README *For AI assistants*, §10).
- No exceptions to the user: log via `Log_Utils.Write`, show a toast / dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- `.bimgo` `formatVersion` 1 and live protocol 1 stay backward compatible.
- Run the Core tests after any Core change.
- Keep `README.md` and `ai/<date>_<topic>/` notes current; zip minus `bin/`, `obj/`, `.vs/`, `artifacts/`;
  write a `claude/BimGo_<Round>_BuildNotes.md` project doc each round.
