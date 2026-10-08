# BimGo UX cleanup — build B (Revit): tabbed Options window, model folders

**Baseline:** build A zip (Gavin: no local changes). Decisions: handoff §9.
**Not compiled here** (no .NET SDK reachable). The changed files were checked by reading the diff and for
balanced braces only; the XAML parses as XML, and every control name and handler from before is still there. Please run the Core tests (new
`ModelFolderTests`, 9 tests, plus build A's `QualityProfileTests`).

## 1. Options window (`Forms/OptionsWindow.xaml(.cs)`)
- `TabControl` with seven tabs: **Load** (what to load, phases) · **Categories** (cards + heavy) · **Geometry**
  (triangle limit, over the limit, step height, helper geometry) · **Materials** (colour mode, moved from Geometry,
  then the extraction card) · **Links** · **Parameters** · **Player** (profile, player & display, sharing).
- The start-position banner (top) and the footer (estimate, Cancel, Launch) stay outside the tabs.
- `LaunchSettings.LastOptionsTab` (0–6) is restored on open and saved on Launch (not on Cancel: the caller only
  saves on Launch).
- Validation messages first switch to the tab holding the field (`ShowTabOf`, logical tree walk to the `TabItem`).
- Window 760 × 720 (min 660 × 520), was 740 × 900.
- Every existing `x:Name` and handler is kept; new: `Tabs`, `ComboProfile`, `TextProfile`, `CheckSidecarsBesideModel`,
  `TextSidecars`, handlers `ComboProfile_SelectionChanged`, `DisplayControl_Changed` (Whitecard / Material radios),
  `DisplayCombo_Changed` (AA, shadow quality, lights).

### Quality profile (Player tab)
- `_display` = a clone of the settings. The visible governed controls (colour, AA, shadow quality, lights) are read
  into it; `QualityProfiles.Detect` picks the combo item (Custom / Basic / Medium / Realistic) and a description.
- Picking a profile: `QualityProfiles.Apply(_display, …)`, pushes the values into the controls (guarded by
  `_updating`), ticks **Extract materials and textures** for Realistic. Picking "Custom" just re-shows the match.
- On Launch, the walkthrough-only values a profile sets (AO, bloom, reflections on / threshold / probes / probe
  resolution) are copied from `_display`; they are unchanged unless a profile was picked in this window.

### Sharing option (Player tab)
- `LaunchSettings.SidecarsBesideModel` (off): "Also write them beside the model when its folder is writable".

## 2. Model folders
- Core `Format/ModelFolders.cs`: `%LocalAppData%\BimGo\Models\<safe title ≤ 60>_<8 hex of SHA-256(key)>\` with
  `comments.json`, `bookmarks.json`, `sun.json`, `visibility.json`, `texture-overrides.json`, `model.json`
  (`ModelFolderInfo`: title, path, key, sharing, beside-model comments path, updated).
- Key (Revit `Extraction/ModelFolderResolver.cs`): `cloud:<project GUID>/<model GUID>` → `central:<central path>`
  (workshared) → `local:<path>` → `unsaved:<title>`. `NormaliseKey` lower-cases and back-slashes path kinds.
- `SceneExtractor.ResolveCommentsPath()` → `ModelFolderResolver.PrepareCommentsPath(doc, settings)`: creates the
  folder, writes model.json, **copies** each missing sidecar from the first candidate that has it (beside the model,
  then the old `%LocalAppData%\BimGo\Comments\<title>.bimgo-comments.json`; comments also from `.rvtgo.json`),
  originals untouched. If the folder can't be created, the old Comments path is used.
- Everything downstream keys off the comments path as before: `SidecarJson.BesideComments` now maps
  `…\comments.json` to the fixed names (`bookmarks.json`, `sun.json`, `visibility.json`); older snapshots that
  still carry a beside-model path keep working unchanged.
- **Sharing on:** `ModelFolders.MirrorAfterWrite` (called by `SidecarJson.Write` and `CommentFiles.Write`) copies
  each written sidecar beside the model (reads model.json; does nothing when sharing is off or the target folder is
  missing; failures logged only). At each Go, `PullNewer` replaces folder files with newer ones beside the model.
  Last writer wins per file: no merge of two people's comments.
- Cloud models have no beside-model path, so sharing does nothing for them.
- App: `CommentStore.FileName` says "BimGo's folder for this model" for the comment-saved toast.

## 3. Texture overrides
- `TextureOverrideSet.LoadFromModelFolder(modelFolder, legacyHostKey, legacyFolder = null)`: file
  `texture-overrides.json` in the model folder; on first use copies `%AppData%\BimGo\texture-overrides\<key>.json`
  in (left as a backup). New `FilePath` (not serialised); `Save()` writes there. No folder → old per-key file.
- Callers: `SceneExtractor.Materials` (extraction), `TextureReview.ModelFolder` → `TextureReviewWindow`,
  `LinkChoices.ModelFolder` (set in `Cmds_BimGo`) → Options window summary, app `WriteOverrides` (folder from
  `Scene.CommentsPath`, live sessions).
- Effect: overrides are now per model folder, so two projects made from one template no longer share them; two
  local copies of one model no longer share them either (each copies the old file in once).

## 4. Not changed
- `.bimgo` files (their data lives inside); Export still copies comments and bookmarks from the (now folder) path.
- `settings.json` stays in `%AppData%\BimGo` (Roaming).
- Linked-model choices (`LaunchSettings.LinkedModels`) are still keyed by `ProjectInformation.UniqueId`.

## Test checklist (Gavin)
1. Options: tabs, last tab remembered after Launch; bad triangle limit → jumps to Geometry.
2. Player: pick each profile → colour / AA / shadows / lights change, Realistic ticks extraction; change one → Custom.
   Launch with Realistic → pause menu shows Realistic.
3. Go on a model that had sidecars beside it → comments / bookmarks / sun / hidden come back; the folder appears
   under `%LocalAppData%\BimGo\Models\` with model.json; old files still beside the model.
4. Read-only or cloud model → comments save (no more "could not be saved").
5. Sharing ticked on a network model → files appear / update beside it; edit there, Go again → taken in.
6. Review textures → choices saved to the folder's texture-overrides.json; existing choices carried over.

## Files
- Core: new `Format/ModelFolders.cs`; `Format/SidecarJson.cs`, `Format/CommentFiles.cs`, `Scene/TextureOverrides.cs`,
  `Scene/LaunchSettings.cs` (`SidecarsBesideModel`, `LastOptionsTab`). Tests: new `ModelFolderTests.cs`.
- Revit: new `Extraction/ModelFolderResolver.cs`; `Extraction/SceneExtractor.cs`, `.Materials.cs`, `.Review.cs`,
  `Commands/Cmds_BimGo.cs`, `Forms/OptionsWindow.xaml(.cs)`, `Forms/TextureReviewWindow.xaml.cs`.
- App: `Game/CommentStore.cs`, `Game/GameSession.Textures.cs`.
