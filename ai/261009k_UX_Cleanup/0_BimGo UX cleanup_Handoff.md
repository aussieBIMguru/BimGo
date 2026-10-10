# BimGo — Handoff Brief: UX and UI cleanup round (after reflection probes)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**), built on his own PC in Visual Studio.
This brief starts a new chat. It's a **UX and simple-UI round, not an overhaul**: reorganise and tidy what exists, add
a hide-UI mode and quality profiles, and move the model sidecars into the app's own folder. The reflection probe logic
is **not** touched this round (see §7, revisited afterwards).

**Read first:**
1. This brief.
2. `README.md` §3 (controls), §5 (how rendering works), §9 changelog entries of 2026-10-09 (reflection probes stage 0 → B.1).
3. `ai/261009j_ReflectionProbes/` notes 1–3 (what the reflection round built and why).
4. **Ask Gavin for a fresh zip of his working copy before editing**, with any compile fixes to build B / B.1. His copy is
   the source of truth. The last zip from Claude was `BimGo_1.0_Reflections_BuildB1.zip`.

---

## 1. Where things stand (end of the reflection probes round)

| Item | State |
|---|---|
| Reflectivity from Revit | Done. `ReflectivityReader` (Revit) reads strength and roughness for every schema; name rules for "mirror" (any schema, drawn opaque) and "water" (see-through materials only). Stored in `materials.json` (`shine`, `roughness`, `metallic`, `water`, `waterBump`). |
| Tiers | Shine rounded to the nearest 25 %; threshold 50 % by default (25 % optional). |
| Sky reflections | Done (build A); glass sheen lifted ×2.5. Water ripples + sun glint (waves doubled in B). |
| Probes | Working in Gavin's build (build B): per-room probes, fallback probes outside rooms, progressive bake, auto re-bake. B.1 (softer reflections at the foot of glass, probes at 1.7 m) **not yet tested**. |
| Known issue | **Probes leak between rooms** (a room shows a neighbouring room's probe). Analysis in §7; fix deferred to after this round. |
| UI today | Sun panel (Shift+O) has a **Reflect** row (Off / Sky / Probes / Probes HQ, status, REFRESH). Pause menu → World & display has *Reflections (Realistic)* Off / Shiny (50 %+) / All (25 %+), *Reflection strength*, and a DEBUG row (Off / Reflection tiers / Probes). |

## 2. Gavin's requests for this round (his list, condensed)

1. **Hide-UI mode** in the viewer: all UI hidden but every control still works; **Esc** brings the UI back. For clean
   walkaround views without UI in the way.
2. **Options window (Go / Export, Revit):** it has the right content but is one long scroll. Break it into **tabs /
   areas** so it's easier to navigate before launch.
3. **Reflection settings move to the pause menu**, together with the probe settings. The sun panel goes back to being
   about the sun and light balance only.
4. **Wording:** Debug becomes **Off / Reflection / Probes**; the Reflections control drops "(Realistic)" and becomes
   **Off / Some / All**.
5. **Quality profiles** to pick quickly: **Basic** (whitecard, only AO and AA, everything else off or low), **Medium**
   (material colour, medium settings, some reflections), **Realistic / High** (everything maxed).
6. **Sidecar JSONs** (comments, bookmarks, sun, visibility) saved to **BimGo's own settings area** while working from
   Revit, rather than beside the model, because models are often in the cloud or read-only. Data travelling outside Revit
   stays in the `.bimgo` file itself.

## 3. What exists today (for orientation)

| Area | Where |
|---|---|
| Options window (Revit, WPF) | `BimGo.Revit/Forms/OptionsWindow.xaml(.cs)`, about 290 lines of XAML, one scroll. Sections in order: WHAT TO LOAD · CATEGORIES TO LOAD · GEOMETRY · MATERIALS & TEXTURES · PHASES · LINKED MODELS · EXTRA PARAMETERS · PLAYER & DISPLAY. Sub-window: `TextureReviewWindow` (Review textures…, Export report…). |
| Pause menu (app, immediate-mode UI) | `BimGo.App/Game/GameSession.Menu.cs`: left column buttons, middle category cards, right **World & display** card (`BuildDisplayCard`, fixed height `S(668)`). Widgets: `Slider`, `Segmented`, `Checkbox`, `SmallButton` (Comments.cs). |
| Sun panel | `GameSession.Sun.cs` → `BuildSunPanel` (fixed height `S(674)`); lights rows in `GameSession.Lights.cs` → `BuildLightControls`; reflection row in `GameSession.Reflections.cs` → `BuildReflectionControls`. |
| Settings | `BimGo.Core/Scene/LaunchSettings.cs` (JSON in `%LocalAppData%\BimGo`), `Sanitise()` clamps; the session reads them in `GameSession.cs` and writes them in `SaveSettings()`. |
| HUD | `GameSession.Render.cs` → `BuildHud` (status panel, minimap, gun bar, help panel F1, toasts, sun icon). |
| Sidecars | `BimGoFormat.SIDECAR_SUFFIX` `.bimgo-comments.json`, plus `.bimgo-bookmarks.json`, `.bimgo-sun.json`, `.bimgo-visibility.json` (`SidecarJson.BesideComments`). Path chosen in `SceneExtractor.ResolveCommentsPath()`: **beside the .rvt**, else `%LocalAppData%\BimGo\Comments\<title>.bimgo-comments.json` for cloud or unsaved models. Legacy `.rvtgo.json` migrated. A `.bimgo` file keeps its own `comments.json` / `bookmarks.json` / `sun.json` inside the zip. |

## 4. Proposed approach per item (for discussion, not decided)

### 4.1 Hide-UI mode
- A toggle key hides the HUD, minimap, gun bar, help, toasts, sun icon, measure panels and crosshair. Movement, guns,
  hotkeys and F12 screenshots keep working.
- **Esc** while hidden shows the UI again (and doesn't also open the pause menu on that press).
- A one-off toast on entering: "UI hidden · Esc to show".
- Not saved: every session starts with the UI shown.

### 4.2 Options window tabs
Same controls, regrouped into tabs (WPF `TabControl` styled like the cards), with the primary button and the spawn
summary always visible below the tabs:

| Tab | Contents |
|---|---|
| **Load** | What to load (active view only, ticked categories), Phases |
| **Categories** | Categories to load |
| **Geometry** | Geometry (triangle threshold, helpers…) |
| **Materials** | Materials & textures (extract, size, proxies, Review textures…) |
| **Links** | Linked models |
| **Parameters** | Extra parameters |
| **Player** | Player & display, plus the quality profile (§4.5) |

The window remembers the last tab used.

### 4.3 Reflections in the pause menu
- A **REFLECTIONS** card or block in the pause menu (beside World & display), holding:
  - Reflections: **Off / Some / All** (Some = tiers 50 %+, All = 25 %+);
  - Source: **Sky / Probes / Probes HQ**;
  - Strength slider;
  - probe status line and **REFRESH**.
- **DEBUG Colours: Off / Reflection / Probes** goes to the bottom of that card or its own small card.
- Sun panel: drop the Reflect row and restore the height (`S(600)`) and the "SUN, SHADOWS & LIGHTS" title.

### 4.4 Wording
The labels from §2.4. Also check the toasts and the README controls table for the old names.

### 4.5 Quality profiles
A **Profile: Basic / Medium / Realistic** segmented control at the top of World & display (and in the Options window's
Player tab). Picking one sets the values below. Any later manual change shows the profile as **Custom**.

| Setting | Basic | Medium | Realistic |
|---|---|---|---|
| Colour mode | Whitecard | Material | Realistic (Material if the snapshot has no textures) |
| Anti-aliasing | 2x | 2x | 4x |
| Ambient occlusion | On | On | On |
| Shadows | Off | On, Medium | On, High |
| Artificial lights | Off | Glow + light | Glow + light |
| Bloom | 0 | 100 % | 100 % |
| Reflections | Off | Some, Sky | All, Probes |
| Probes | — | — | 128 (HQ stays a manual choice) |

### 4.6 Sidecars in the app's folder
- New location: `%LocalAppData%\BimGo\Models\<model key>\` holding `comments.json`, `bookmarks.json`, `sun.json`,
  `visibility.json`, plus a small `model.json` (title, path, key) so a folder can be recognised.
- Model key, most stable first:
  - cloud model GUID;
  - else the workshared central path;
  - else the full local path;
  - hashed to a safe folder name.
- **Migration:** on first use, if sidecars exist beside the model (or in the old `Comments` folder), copy them into the
  new folder and leave the originals untouched as a backup.
- `.bimgo` files: unchanged (their data lives inside the file). Exporting a `.bimgo` from Revit copies the current
  sidecar data into it, as today.

## 5. Questions for Gavin (with a recommended answer first)

1. **Hide-UI key:** **U** (free today; H is Home, F1 help, F10 is taken by Windows for the menu bar)? Or another key?
   Should the crosshair also hide? *(Recommended: U; the crosshair hides too, for clean screenshots.)*
2. **Hide-UI and toasts:** suppress all toasts while hidden, or show critical ones (errors) only?
   *(Recommended: errors only.)*
3. **Options window tabs:** is the 7-tab grouping in §4.2 right, or fewer tabs (e.g. Load · Content · Materials ·
   Player)? *(Recommended: as proposed; Categories and Parameters lists are long enough to deserve their own tab.)*
4. **Reflections card placement:** a new card beside World & display, or should the pause menu's right side become
   tabbed (Display · Reflections · Debug) to avoid more fixed-height growth? *(Recommended: tabbed right column, since
   the display card is already 668 px tall and small windows clip it.)*
5. **Profiles:** are the values in §4.5 right? Should the **Realistic** profile at Go also tick "Extract materials and
   textures"? Is Probes HQ ever part of a profile? *(Recommended: yes it ticks extraction; HQ stays manual.)*
6. **Profiles and saving:** remember the last profile per machine, with manual tweaks saved as Custom?
   *(Recommended: yes.)*
7. **Sidecar key:** OK to key by cloud GUID → central path → local path? A model opened from two different local
   copies would then have two folders. *(Recommended: yes; simple and predictable.)*
8. **Sharing:** sidecars beside the model let colleagues see each other's comments. Keep an optional **"Also write
   beside the model when the folder is writable"** setting, or app folder only? *(Recommended: app folder only by
   default, with that option, off.)*
9. **Migration:** copy and leave the originals, or move them (delete beside the model)?
   *(Recommended: copy and leave them.)*
10. **Texture overrides** (per-model texture choices from the materials round): move them into the same per-model
    folder for consistency? *(Recommended: yes, if they aren't already in `%LocalAppData%`.)*

## 6. Likely files touched

| Area | Files |
|---|---|
| App | `Game/GameSession.Menu.cs` (profiles, reflections card / tabs, debug wording), `Game/GameSession.Reflections.cs` (move the row into the pause menu), `Game/GameSession.Sun.cs` (remove the Reflect row, height, title), `Game/GameSession.Render.cs` + `GameSession.cs` (hide-UI flag, Esc handling, key binding), `Game/GameSession.Sun.cs` / `.Bookmarks.cs` / `.Visibility.cs` / comment store (sidecar paths) |
| Core | `Scene/LaunchSettings.cs` (`QualityProfile`, last options tab), `Format/SidecarJson.cs` / `BimGoFormat.cs` (new folder layout, migration helper), tests |
| Revit | `Forms/OptionsWindow.xaml(.cs)` (tabs, profile), `Extraction/SceneExtractor.cs` (`ResolveCommentsPath` → model key folder) |

## 7. Deferred: probes leaking between rooms (next round)

Gavin sees neighbouring rooms' probes inside a room. Likely causes, in order:

1. **Blending ignores walls.** `ReflectionProbes.BuildGrid` blends the neighbour's probe into every cell within ~1 m of a
   room boundary, so each room shows its neighbour's capture up to 1 m from every wall, not only at doorways.
   - *Fix:* blend only where the boundary is open, by checking door / opening elements, or by casting through the
     geometry; otherwise blend nothing, or only at 0.25 m.
2. **Surfaces sample the cell they sit on.** Walls, glass and floors lie *on* the room boundary, so their cell may belong
   to the next room (0.5 m cells straddle walls).
   - *Fix:* look up the cell at `world + normal × 0.3` (pushed into the room the surface faces).
3. **Height bands.** Cells accept a room when their 1 m band overlaps the room's height ±0.3 m, so a floor slab's cell
   can take the room below's probe.
   - *Fix:* use the room whose BottomZ is nearest below the cell centre, and halve the band height.
4. **Coarse cells on big models** (the cell size grows above 4 M cells).
   - *Fix:* log the final cell size, and use a sparse grid if needed.

Debug colours → Probes (pause menu) show exactly which probe each surface uses: check it first.

## 8. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations**.
- **BimGo.Revit stays package-free;** no new packages in the app without Gavin's yes.
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`, show a toast or dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`. Nothing changes the
  Revit model.
- Format and protocol stay backward compatible (`formatVersion` 1; additive optional fields). Settings migrate quietly.
- Run the Core tests after any Core change (MSTest 4: `Assert.ThrowsExactly`, not `ThrowsException`).
- Keep `README.md` and the round's `ai/` notes current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
- Claude can't compile here. GLSL is checked in headless WebGL2 (Playwright + the pre-installed Chromium:
  `extract.py` pulls the shader strings out of `Shaders.cs`, `#version 330 core` → `300 es`; give every sampler its own
  unit in the test). Say so in the notes.

## 9. Decisions (Gavin, 2026-10-08)

**Baseline:** `BimGo_1.0_Reflections_BuildB1` zip is the source of truth; B / B.1 compile with no major issues. Probe
leaks through walls stay deferred (§7). Core key bindings get a broad review later, before v1.

| # | Topic | Decision |
|---|---|---|
| 1 | Hide-UI key | **U** (crosshair hides too). |
| 2 | Toasts while hidden | **Errors only**: `Toast()` gains an "important" flag; others suppressed. |
| 2b | Esc while hidden | Esc only shows the UI (even when Gizmo / Clone captures input); the next Esc cancels / pauses as usual. |
| 3 | Options window | **7 tabs** as §4.2; last tab remembered. Colour mode radios move from Geometry to the **Materials** tab. |
| 4 | Pause menu right side | **Tabbed column**: Display · Reflections · Debug. **Profile selector above the tabs** (always visible). |
| 5 | Profile values | As §4.5; Probes HQ never in a profile. Profiles set **shadow quality only** (Basic = Low); shadows on/off (O, per-model sun state) is left alone. Realistic in the Options window **ticks "Extract materials and textures"**. |
| 6 | Profile saving | `QualityProfile` per machine in LaunchSettings, shared by Options window and pause menu; manual tweak → **Custom**. |
| 7 | Sidecar key | Path-based: cloud model GUID → central path → local path, hashed; folder `<title>_<8-char hash>` under `%LocalAppData%\BimGo\Models\`, with `model.json`. (Not `ProjectInformation.UniqueId`: shared by copies and template-derived projects.) |
| 8 | Sharing | App folder only by default; **"Also write beside the model when the folder is writable"** option, off, in the Options window **Player** tab. |
| 9 | Migration | **Copy**, leave the originals (beside the model, old `Comments` folder). |
| 10 | Texture overrides | **Move** into the same per-model folder (Local, new key); migrate from `%AppData%\BimGo\texture-overrides\<UniqueId>.json` by copying, old file left as backup. |
| — | Delivery | **Two builds.** A: app only (hide-UI, wording, tabbed pause menu, profiles, sun panel back to "SUN, SHADOWS & LIGHTS" / `S(600)`). B: Revit Options tabs + sidecar / texture-override folder move + Core tests. |
