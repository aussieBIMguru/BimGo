# BimGo — Handoff Brief: Materials round, build B (texture reconciliation and proxies)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**), built on his own PC in Visual Studio.
This brief starts the next chat. It continues the **experimental materials round**: if it can't be made to work, the
whole round is rolled back to `BimGo_1.0_Handoff_Materials.zip` and the next chat starts from the original materials
brief as if it never happened.

**Read first:**
1. This brief.
2. `ai/261009i_Materials/1_build notes stage 0.md`: decisions agreed with Gavin and the **scan findings** (schemas, slot
   names, path styles, units, sizes).
3. `ai/261009i_Materials/2_build notes build A.md`: what build A built, the design notes, the test checklist.
4. `README.md` §6 (format: `materials.json`, `material.bin`, `textures/`), §8 (limitations), §9 (latest changelog).
5. **Ask Gavin for a fresh zip of his working copy before editing**, plus:
   - any compile fixes he made to build A;
   - the `Materials:` and `Textures:` lines from `%LocalAppData%\BimGo\Logs\BimGo.Revit.log`;
   - screenshots of Realistic mode (Snowdon, BG01, BG05).

   His copy is the source of truth. The last zip from Claude was `BimGo_1.0_Materials_BuildA.zip`.

---

## 1. Where things stand (2026-10-09)

| Step | State |
|---|---|
| Stage 0: Material scan diagnostic (temporary ribbon button) | Built, run on Snowdon, BG01, BG05; findings recorded |
| **Build A**: Realistic mode, embedded textures, Revit-like UVs, sky reflection on glass | Delivered, **not yet compiled or tested by Gavin**. GLSL checked in WebGL2 |
| **Build B** (this brief): reconcile missing textures, deep scan, in-app Textures panel, CC0 proxies | Not started |
| Installer round | Still parked behind the rendering rounds |

### What build A already gives build B

- **`TextureLocator`** (`BimGo.Revit/Extraction/TextureLocator.cs`) probes this machine each time it runs and records
  every step in `Notes`. Nothing is hard-coded.
  - Autodesk library: standard Common Files folders, then the registry
    `HKLM\SOFTWARE\Autodesk\ADSKTextureLibraryNew\{1,2,3}\Textures` → `LibraryPaths`.
  - `Revit.ini` `[Directories] AdditionalRenderAppearancePaths=` (UTF-16; `|` or `;` separated). Gavin's machine:
    `D:\RV Revit\BG\Support Files\Materials\Maps\`.
  - Resolution stages: absolute → library root (relative path) → additional paths (relative, then file name) → model
    folder → file name in `n\Mats`.
  - `LooksAutodeskLibrary()` recognises library paths from their syntax.
- **Surface coordinates are metric and material-independent.** Scale, offset and angle live in the material table, so
  an image can be swapped or a proxy assigned **without re-extracting** and without touching the geometry.
- **`SceneMaterial`** keeps `TextureSource` (the appearance's raw path), `TextureState`
  (`none / embedded / missing / procedural / unreadable`) and `Autodesk`: everything a reconcile UI needs.
- **App:** `MaterialTextures.Initialise / Release` rebuilds the GPU side from a `MaterialData`, so after a change the
  renderer only needs a re-upload.

---

## 2. Gavin's requirements for build B (his words, condensed)

1. **Reconcile missing or proxy textures** either in a **sub-menu before extraction** (Revit) or in a **secondary UI
   after it** (the app). Decided: **both**. The app's panel is the one that works without Revit.
2. **Deep scan a nominated folder**, stage by stage, so the gaps can be filled when Revit isn't finding textures but the
   user knows where they are. Decided:
   - **missing textures only**;
   - **loose matching only where it clearly suits**, otherwise explicit hits only;
   - anything that doesn't reconcile **falls back to the shaded appearance**.
3. **Proxy texturing** when Autodesk / own textures aren't available: a shipped **CC0 keyword pack** (decided yes) plus
   a **user-picked image** per material.
4. **Remembering:** picks and proxies **per model**; search folders **globally** (decided).
5. Never assume the Autodesk Material Library is installed; check for it (done in build A).

---

## 3. Fix first: build A carry-overs

1. **Missing texture → the shading colour, not the render colour.** When a texture is connected, the appearance's
   colour value is often a white placeholder (Snowdon: `opaque_albedo = [1, 1, 1, 1]` with a bitmap connected). So in
   build A a material whose texture is missing draws **white** in Realistic mode. Gavin's rule is "falls back to shaded
   appearance".
   - **Recommended:** at extraction, when the state ends up `Missing` / `Unreadable` / `Procedural`, set
     `SceneMaterial.Colour` to the material's shading colour.
   - Keep the render colour in a new optional field `renderColour`, so a texture added later (build B) can use the
     right base again.
   - **Ask Gavin to confirm.**
2. Whatever Gavin's build and tests turned up (compile errors, wrong orientation, scale, performance).
3. Optional, if Gavin wants it: start each pattern at the face origin, as Revit does, instead of world-anchored. Ask
   first: world-anchored keeps coursing continuous across joins.
4. **Revit's tint overlay** (Gavin flagged it).

   **What build A does:** in `SceneExtractor.ReadTexture`, it multiplies the image by the **bitmap's** tint, but only
   when `common_Tint_toggle` is on (`common_Tint_color`; stage 0 found it on in 18 Snowdon slots), and by
   `unifiedbitmap_RGBAmount`. Hardwood's `hardwood_tint_color` (when `hardwood_tint_enabled`) also goes into `Tint`.

   **Still to handle:**
   - **Tint on the appearance asset itself**, not only on its bitmap. In Revit's Appearance tab, "Tint" is ticked on
     the asset and applies to the whole look: plain-colour materials as well as textured ones. Check whether the asset
     carries its own `common_Tint_toggle` / `common_Tint_color` (stage 0 only saw them on bitmaps), and, if so, apply it
     to the colour too, not only to the image.
   - **How Revit blends the tint:** a straight multiply, or a hue/saturation shift that keeps lightness? Compare one
     material in Realistic view against BimGo with tint on and off. A plain multiply darkens light images more than
     Revit does if Revit preserves luminance.
   - **Precedence when both are on** (asset tint and bitmap tint): multiply both, or does the asset tint win?
   - **The diffuse "image fade":** confirm the tint applies to the image only, before it's blended with the colour
     (that's how build A does it). The colour itself is untinted unless the asset-level tint applies.
   - **The colour space of tint colours** (`*_colorspace` = 1 or 2 in the scans) may mean linear vs sRGB. If tinted
     materials look too dark or too saturated, convert linear → sRGB before multiplying.
   - **A switch** "Apply Revit tint", on by default, so Gavin can compare in the walkthrough. Optional; ask.
5. **Inverted diffuse channel.** `unifiedbitmap_Invert` (Boolean on the bitmap asset) is **read but ignored** in
   build A. When it's true, Revit shows the image inverted (1 − rgb).
   - **Recommended:** a per-material flag in the table, applied in the shader **before** the tint and fade. Don't bake
     it into the JPEG: images are deduplicated by path, and the same image may be used inverted by one material and
     not by another.
   - **The table is full:** all four texels are used. Add a **fifth texel** (invert flag, plus room for later: RGB
     amount, brightness, tint mode) and update `MaterialTextures.TEXELS`, `UploadTable` and `MATERIALS_GLSL`
     together. Keep the table's documented layout in the class remarks and the GLSL comment in step.
   - **Format:** new optional `invert` (bool) field on `SceneMaterial`. Older readers ignore it.
   - **Also check:** whether Revit honours `unifiedbitmap_Invert` on the colour slot at all, or only on bump /
     cutout slots. If it's only those, the flag is just recorded.
6. **Diagnostics for 4 and 5:** extend the Material scan, before removing it, to list every material whose asset or
   bitmap has a tint on or invert on, with the values. Ask Gavin for a small test model with:
   - a tinted Generic plain colour;
   - a tinted Generic with an image;
   - a tinted Advanced material;
   - an inverted image.

   Compare each against Revit Realistic view screenshots.

---

## 4. Build B scope

### 4.1 Shared matching logic (BimGo.Core, testable)

New `Core/Scene/TextureSearch.cs`: pure file-name logic, no System.Drawing and no Revit. Revit and the app both use it,
and the Core tests cover it.

- **Input:** a set of missing raw paths (as `SceneMaterial.TextureSource`) and a folder.
- **Folder index:** enumerate the folder recursively **once** (image extensions only: `.jpg .jpeg .png .tif .tiff
  .bmp .gif`). Cap the depth (8) and the file count (50 000), and make it cancellable with `OperationProgress`.
- **Stages,** run in order. Each stage only sees what earlier stages left unmatched:
  1. **Exact** file name (case-insensitive). For each `|` alternative of the raw path, take its file name.
  2. **Same name, another extension** (`brick.jpg` ↔ `brick.png` / `.tif`).
  3. **Loose**: normalised stems (lower case; strip separators `_ - . space`; strip a trailing `_color` / `_diffuse` /
     `_albedo` / `_col`). Proposed **only when exactly one candidate** matches. Loose hits are **proposals the user
     accepts**, never applied silently.
- **Ambiguity:** when a stage finds several candidates (the same name in two folders), report them all. Pick none
  automatically; the user chooses.
- **Output:** per raw path, a stage tag, the candidates, and the chosen file (null when unresolved).
- **Tests:** exact, case, extension swap, loose-only-when-unique, ambiguity, depth / count caps, cancellation.

### 4.2 Remembered choices

- **Per model** overrides in `%AppData%\BimGo\texture-overrides\<host model key>.json`. The key is the same
  `LinkResolver.HostKey` used for linked-model choices. The file maps (document key: `host` or the link's model key,
  material UniqueId, with the material name as a fallback) to one of:
  - `{ "image": "<absolute path>" }`: a user pick or an accepted scan hit;
  - `{ "proxy": "<keyword>" }`: a CC0 proxy;
  - `{ "colourOnly": true }`: deliberately plain.
- **Global search folders:** `LaunchSettings.TextureSearchFolders` (list, sanitised, at most 20). They become a new
  `TextureLocator` stage **after** the additional render appearance paths, using the 4.1 exact-name index (built once
  per extraction, cached by folder and modified time).
- `SceneExtractor.ReadTexture` checks the override **before** the locator. Record where each texture came from in a new
  optional field `textureOrigin`: `asset / search / override / proxy`.
  - **Don't add values to the `TextureState` enum.** An older reader would fail on an unknown enum string and drop all
    materials.

### 4.3 Revit: "Review textures…" window (before extraction)

- A button on the MATERIALS & TEXTURES card in `OptionsWindow` (enabled when the extraction is ticked). It opens a WPF
  window in the same style (`Card`, `SectionHeader`, `PrimaryButton`…).
- **The scan:**
  - Covers the materials that **will be extracted**: the ticked categories or the active view, plus the ticked links.
  - Reuse the gather step of `SceneExtractor` (factor out the "find elements" part) and `Element.GetMaterialIds(false)`
    (+ paint, `true`). Stage 0 took ~8 s on Snowdon including the image headers; show progress and allow cancel.
  - Read each material with the same code as the extraction: factor `ReadAppearance` / `ReadTexture` so that "resolve
    only" (no embedding) is possible.
- **One row per material:**
  - columns: thumbnail (64 px, decoded lazily), material name, model (host / link), schema, status, source path;
  - status values: Found (stage) / Found by search / Missing / Procedural / Plain colour / Proxy / Override;
  - filter chips: **Missing**, Proxy, All.
- **Actions** (on the selected rows, multi-select):
  - **Browse image…**
  - **Use proxy ▸** (keyword list, with the suggested keyword first)
  - **Plain colour**
  - **Clear override**
- **Deep scan:** "Scan a folder…" runs 4.1 on the Missing rows, **one stage at a time**.
  - After each stage it shows a results list, and Gavin ticks what to accept. Exact hits come pre-ticked; loose hits
    don't.
  - Then "Next stage" or "Done".
  - "Remember this folder for all models" adds it to `TextureSearchFolders`.
- **Saving:** writes the per-model override file and returns to Options. Nothing changes in the Revit model, ever.
- The temporary **Material scan** ribbon button can become an "Export report…" button in this window (reuse
  `MaterialScan`), then come off the ribbon.

### 4.4 App: Textures panel (after extraction; no Revit needed)

- **The panel:**
  - A pause-menu button `TEXTURES (n missing)`, shown when the scene has materials.
  - Opens a card listing materials whose state is Missing, Proxy, or Plain with a proxy suggestion.
  - Uses the immediate-mode widgets in `GameSession.Menu.cs`: `Button`, `Checkbox`, `Segmented` and list rows like the
    links card.
- **Actions:**
  - **Find in folder…** needs a folder picker. `FileDialogs` has none yet: add one with WinForms
    `FolderBrowserDialog`, which is in-box and needs no package.
  - Runs 4.1, with the same staged accept flow (exact pre-ticked, loose ticked by hand).
  - **Pick image…** (`FileDialogs.ShowOpen`), **Use proxy**, **Plain colour**.
- **Applying a fix:**
  - Decode the image, resize it to `MaterialData.TextureMaxSize` and encode it as JPEG (System.Drawing, the same code as
    the Revit `EmbedImage`; consider moving it to a small shared helper in the app).
  - Add it to the textures under `textures/<hash>.jpg`, update the material (`Texture`, `TextureState = Embedded`,
    `textureOrigin`), then `MaterialTextures.Release()` + `Initialise()`.
- **Persistence:**
  - **File mode:** mark the document dirty, so Ctrl+S writes the images into the `.bimgo`. That's the "secondary UI"
    route that works with no Revit. `IsDirty` (`GameSession.Document.cs`) currently checks the journal, comments and
    bookmarks: add a materials-changed flag.
  - **Live session:** apply in the view, **and** write the same per-model override file (4.2, same machine and same
    `%AppData%`), so the next F5 / Go picks the fix up in Revit too.
- **Mutability:** `SceneData.Materials` / `MaterialData` are init-only, and `Textures` is an `IReadOnlyDictionary`.
  - **Recommended:** a `MaterialData.With(…)` that returns a new instance (copied table, shared streams, merged
    textures), and let `GameSession` hold the current one. The writer would take it through the `BimGoDocument`.
  - Alternatively, make `SceneData.Materials` settable. Choose and note it.

### 4.5 CC0 proxy pack

- **Where they're applied:** app-side at display time. The `.bimgo` only stores the **keyword** (`proxy` field on the
  material); the image ships with the app. This keeps files light and never embeds anything that isn't the user's.
  - **Trade-off:** a file opened in an app without the pack shows plain colour. That's acceptable; note it.
- **Pack:**
  - Contents: 15–20 tileable colour maps from **ambientCG** or **Poly Haven** (both CC0), 1K JPG colour map only,
    resized to 512². Candidates:
    - brick (common / face)
    - blockwork
    - concrete (smooth / board-formed)
    - render / plaster
    - timber floor
    - timber panel
    - plywood
    - carpet
    - vinyl
    - ceramic tile 300 / 600
    - stone / marble
    - metal (brushed / galvanised)
    - gravel
    - grass
    - asphalt
    - fabric
  - **Claude can't download these (the sandbox blocks those sites).** Gavin downloads them. List the exact assets in
    the notes once chosen.
  - Location: `BimGo.App/Resources/Proxies/` (copied beside the exe), plus `proxies.json` per image with `keyword`,
    `aliases[]` (name fragments: `brick`; `timber|wood|oak|ply`; `render|plaster|paint`…), real-world `sizeU` / `sizeV`
    (m) and the source URL / licence.
  - `THIRD-PARTY-NOTICES.txt` entry, even though CC0. **Never bundle Autodesk textures.**
- **Suggestions:** at extraction (Revit) or in the panel, suggest a keyword by material name, then schema (Masonry →
  brick, Hardwood → timber…). Apply it automatically **only** to `Missing` / `Unreadable` materials, and only when the
  setting "Proxy textures for missing images" is on. Plain-colour materials (paint) are left alone unless the user
  assigns a proxy.
- **Renderer:** load the pack images used by the scene into the same size-bucketed arrays (they're just more layers).
  The material's scale comes from the proxy's `sizeU` / `sizeV` when it had none of its own.

---

## 5. Likely files touched

| Area | Files |
|---|---|
| Core | `Scene/TextureSearch.cs` (new), `Scene/MaterialData.cs` (`renderColour`, `textureOrigin`, `proxy`, `invert`, `With(…)`), `Scene/LaunchSettings.cs` (`TextureSearchFolders`, `ProxyMissingTextures`), tests (`TextureSearchTests`, material additions) |
| Revit | `Extraction/TextureLocator.cs` (search-folder stage), `Extraction/SceneExtractor.Materials.cs` (overrides, shading-colour fallback, asset-level tint, invert, factor read-only resolve), `Extraction/MaterialScan.cs` (tint / invert report), `Extraction/SceneExtractor.cs` (factor the gather step), `Forms/TextureReviewWindow.xaml(.cs)` (new), `Forms/OptionsWindow.*` (button), overrides store (new, e.g. `Extraction/TextureOverrides.cs`), `Application.cs` (drop the Material scan button) |
| App | `Game/GameSession.Textures.cs` (new: panel + apply), `Game/GameSession.Menu.cs` (button), `Game/GameSession.Document.cs` (dirty flag, save), `Platform/FileDialogs.cs` (folder picker), `Rendering/MaterialTextures.cs` (proxy layers, re-init, fifth table texel), `Rendering/Shaders.cs` (`MATERIALS_GLSL`: invert), `Resources/Proxies/*` + `proxies.json`, `BimGo.App.csproj` (copy the pack), `THIRD-PARTY-NOTICES.txt` |

---

## 6. Questions for Gavin (ask, don't assume)

1. **Missing-texture fallback:** shading colour (recommended, matches "falls back to shaded appearance") or render
   colour?
2. **Proxies:** applied automatically to missing textures with a keyword match (on by default?), or only when he picks
   them?
3. **Loose matching:** are the normalisation rules in 4.1 right for his naming (`BG_Carpet_Plain1.jpg`, `_Bump`,
   `_Cutout` suffixes)? Should bump / cutout files ever be offered? (Recommend: never; they're filtered by suffix.)
4. **The proxy pack list** (4.5): which materials matter most to him; does he want to source them himself or send a
   zip?
5. **In live sessions:** should a fix made in the app also write the per-model override file (recommended), so Revit
   uses it on the next F5?
6. **Pattern origin:** world-anchored (current) or per-face like Revit?
7. **Tint:** can he send a small test model with tinted materials (asset-level and bitmap-level) and an inverted
   image, plus Realistic view screenshots? Does he want an "Apply Revit tint" switch for comparison?

---

## 7. Test plan

- **Core tests:** `TextureSearchTests` (all stages and caps); materials round-trip with the new optional fields; an
  older build A file still loads; a build B file still loads in build A (new fields ignored).
- **BG01** with the render appearance path **removed** from Revit Options:
  - the review window lists the `BG_…` textures as Missing;
  - a deep scan of `D:\RV Revit\BG\Support Files\Materials\Maps` finds them at stage 1, and the next Go shows them;
  - the override file and the remembered folder survive a Revit restart.
- **Loose stage:** rename one image (`BG_Carpet_Plain1.jpg` → `bg-carpet-plain-1.JPEG`). It's proposed at stage 3,
  unticked, and accepted by hand.
- **App, file mode:** open a `.bimgo` with missing textures, use Find in folder, then Ctrl+S. Reopen: the textures are
  embedded. The `.bimgo` opens in build A too.
- **App, live session:** a fix in the panel shows immediately; after F5 it comes back from Revit (override file).
- **Proxies:** a model with no library installed (or the library folder renamed) shows proxies on brick, concrete and
  timber at the right scale; Whitecard and Material modes are unaffected.
- **Tint and invert:** each material in Gavin's test model (3.6) matches Realistic view in hue and lightness, within
  reason: a tinted plain colour, a tinted image, an asset tint plus a bitmap tint, an inverted image, and an image shared
  between an inverted and a non-inverted material (both right).
- **Performance:** a recursive scan of a large folder (10 000+ files) stays responsive and cancellable.

---

## 8. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations**.
- **BimGo.Revit stays package-free.** Any package needs Gavin's yes (none expected: WPF, WinForms and System.Drawing are
  in-box).
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`, show a toast or dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`. **Nothing in build
  B changes the Revit model.**
- Format and protocol stay backward compatible (`formatVersion` 1, protocol 1; additive optional fields only; never
  new enum values in existing fields).
- Run the Core tests after any Core change.
- Keep `README.md` and `ai/261009i_Materials/` notes current (next file: `4_build notes build B.md`); zip the repo minus
  `bin/`, `obj/`, `.vs/`, `artifacts/`.
- Claude can't compile here (no .NET SDK or Revit API, and the sandbox can't download them). GLSL can be checked in
  headless WebGL2 (Playwright + the pre-installed Chromium; extract the shader strings from `Shaders.cs`). The C# is
  reviewed by eye. Say so in the notes.
