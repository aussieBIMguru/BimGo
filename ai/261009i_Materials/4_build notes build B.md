# BimGo: Materials round, build notes, build B (reconciliation, deep scan, proxies, tint and invert)

**Still the experimental round.** If materials don't work out, roll back to `BimGo_1.0_Handoff_Materials.zip`. Build A
compiled and worked for its goals (Gavin, 2026-10-09). Build B starts from that zip, unedited.

**Not compiled.** There's no .NET SDK or Revit API here, so the C# was reviewed by eye. The GLSL was extracted from
`Shaders.cs`, then compiled, linked and run in headless WebGL2 (ANGLE / SwiftShader):

- the scene, ground, AO-geometry and shadow programs all link;
- a six-material table test gives the expected pixels in all three tint modes:

| Material | Result |
|---|---|
| Plain colour | The colour |
| Image | Upright |
| Inverted image | 1 − rgb |
| Proxy | Grey 0.5 tinted to the material colour (0.8, 0.4, 0.2), whatever the tint mode |
| Appearance tint | Multiplied in *Multiply*, ignored in *Off* |
| Image tint | Applied to the image only |

---

## 1. Decisions (Gavin, 2026-10-09)

| Topic | Decision |
|---|---|
| Missing-texture fallback | **Shading colour** (the appearance's render colour is kept in `renderColour`). |
| Proxies | **Automatic for missing / unreadable images**, by name then schema, with the setting on by default. |
| Tint | No test model yet. Built to best reading, with a switch to compare: **Revit tint Off / Multiply / Keep lightness** (Textures panel). |
| Bump / cutout / reflection maps | Never offered by the deep scan (filtered by suffix). |
| Live fixes | Also written to the per-model override file, so the next F5 brings them back from Revit. |
| Pattern origin | Stays world-anchored. |
| Proxy pack | Gavin sourced 21 ambientCG CC0 maps; Claude processed them (see §4). |
| Proxy colour (added) | Proxies take the material's own colour by default ("Proxies take the material's colour"), so a white vinyl isn't charcoal. |
| Reflection probes (25 / 50 / 75 % mirrored, threshold clamp) | **Parked** for later (performance). |

## 2. What was built

| Area | Files | What |
|---|---|---|
| Core | `Scene/MaterialData.cs` | New optional fields: `uniqueId`, `renderColour`, `assetTint`, `invert` (written only when true), `textureOrigin` (string constants in `TextureOrigins`, never an enum), `proxy`. `MaterialData.With(table, addedImages)` shares the vertex streams, merges the images and prunes unreferenced ones. |
| Core | `Scene/TextureSearch.cs` (new) | `TextureFolderIndex`: a recursive walk (depth 8, 50 000 images, cancellable, unreadable folders skipped), indexed by name, stem and loose stem, plus a per-process cache by folder last-write time. `TextureSearch.RunStage` / `RunAll`: exact → extension → loose; loose only when unique; ambiguity reported, never picked; auxiliary maps filtered. |
| Core | `Scene/ProxyCatalog.cs` (new) | 21 keywords with label, aliases (word-start match), schema fallbacks and real-world size. Order matters (specific before general). A "never" list (glass, mirror, acoustic, ceiling tile, lamp…). |
| Core | `Scene/TextureOverrides.cs` (new) | `TextureOverrideSet`: `%AppData%\BimGo\texture-overrides\<host key>.json`, document key → material key (UniqueId, else `name:…`) → `{image \| proxy \| colourOnly}`. Exactly one meaning (image > proxy > plain). The file is deleted when empty, a damaged file is ignored, and the save is atomic. |
| Core | `Scene/LaunchSettings.cs` | `RevitTint` (`TintMode`), `ProxyMissingTextures`, `ProxyMaterialColour`, `TextureSearchFolders` (≤ 20, the latest kept), `AddTextureSearchFolder`, `Clone()`. |
| Core | `Format/BimGoDocument.cs`, `BimGoWriter.cs` | `BimGoDocument.Materials` (null = the scene's); the writer saves `document.Materials ?? scene.Materials`. |
| Tests | `TextureSearchTests.cs` (new: `TextureSearchTests`, `ProxyCatalogTests`, `TextureOverrideTests`), `MaterialTests.cs` | Stages, case, extension, loose-only-when-unique, auxiliary filter, ambiguity, caps, cancellation; suggestions (21 cases); override round-trip, clear, damaged file, document keys; build B fields round-trip; a build A table loads; unknown fields are ignored; `With`; document override written; settings and `Clone`. |
| Revit | `Extraction/SceneExtractor.Materials.cs` | `PrepareTextures` (locator + search folders + overrides). `ReadAppearance` now records the UniqueId and the asset-level tint, and calls `Finish` (override → auto proxy → shading-colour fallback). `ReadTexture` reads `unifiedbitmap_Invert` and tries the override image before the locator. Resolve-only mode (`_resolveOnly`, `_lookups`) for the review. The log line gains search hits, overrides, proxies and fallbacks. |
| Revit | `Extraction/TextureLocator.cs` | New last stage: exact file name in the remembered search folders (a unique hit only; ambiguity noted). `TextureFound.SearchFolder` / `Override`. |
| Revit | `Extraction/SceneExtractor.cs` | `PrepareSources` and `Gather` factored out of `Run` (shared with the review); `resolveOnly` constructor flag. |
| Revit | `Extraction/SceneExtractor.Review.cs` (new) | `SceneExtractor.Review(uiDoc, trialSettings, progress)` → `TextureReview` (rows: material, document key, model, found path and stage, suggested proxy, uses). Materials come from `Element.GetMaterialIds(false)` + paint (`true`) over the same element set as the extraction. |
| Revit | `Extraction/MaterialScan.cs` | TINT AND INVERT section: appearance and bitmap tints that are on (with colour and colour space), and inverted bitmaps. |
| Revit | `Forms/TextureReviewWindow.xaml(.cs)` (new) | Status filter chips, 64 px lazy thumbnails, multi-select actions, the staged deep scan panel, "Remember this folder", Export report…, Save choices. |
| Revit | `Forms/OptionsWindow.*`, `Commands/Cmds_BimGo.cs` | "Proxy textures for missing images" tick, **Review textures…** button (runs on the window's unsaved choices via `Clone()`), a count of this model's choices; `TextureReviewServices` passed from the command. |
| Revit | `Application.cs`, `Commands/Cmds_BimGo.MaterialScan.cs` (deleted), icons, tooltip | The temporary Material scan ribbon button is removed; the report lives on as Export report… in the review window. |
| App | `Rendering/MaterialTextures.cs` | 5 texels per material; proxy layers (key `proxy:<keyword>`, capped at 512); proxy table rows at the pack's size, fade 1, tint = material colour ÷ the proxy's average colour (when the option is on); `Initialise` always releases first (re-init). |
| App | `Rendering/Shaders.cs` | `uTintMode`, `applyTint` (multiply / keep lightness), t4 (asset tint + flags 1 = invert, 2 = proxy). Order: image → invert → image tint → fade → asset tint. |
| App | `Rendering/SceneRenderer.cs` | `SceneDrawParams.Tint`, `AutoProxy`, `ProxyMaterialColour`, `ReloadMaterials(MaterialData)`. |
| App | `Rendering/ProxyPack.cs` (new) | `ProxyPack` (reads `Resources\Proxies\proxies.json`, falls back to `<keyword>.jpg`, `EffectiveProxy`); `TextureEncoder` (the same resize / JPEG 85 / entry naming as Revit's `EmbedImage`). |
| App | `Game/GameSession.Textures.cs` (new) | The panel, staged scan, apply / undo, live override writes, the dirty revision, a per-material display cache (no per-frame string building). |
| App | `Game/GameSession*.cs`, `Platform/FileDialogs.cs` | Menu button `TEXTURES (n MISSING)`, Esc handling, settings load / save, draw params; `FileDialogs.ShowFolder`. |
| App | `Resources/Proxies/*`, `BimGo.App.csproj`, `THIRD-PARTY-NOTICES.txt` | 21 JPGs + `proxies.json`, copied beside the exe; CC0 notice with asset IDs. |

## 3. Design notes

- **Where proxies are decided.**
  - **Revit:** at extraction, only for `Missing` / `Unreadable` with no override, when the setting is on. Recorded as `proxy` + `textureOrigin = proxy`.
  - **App:** honours the stored keyword. It suggests one itself only for files with no recorded `textureOrigin` (build A files), so a build B decision of "no proxy" (setting off, no match, or Plain colour chosen) is never second-guessed.
- **Proxy scale:** the pack's real-world size, not the material's own placement (which was set for a different image). This changes the brief, which said "only when the material has none of its own". Material placement on a stand-in image put brick courses at the wrong height. The material's angle is kept.
- **Proxy colour:** tint = material colour ÷ the proxy image's average colour, sampled at upload. It always multiplies (flag 2), whatever the tint mode. "Proxies take the material's colour" turns it off to show the pack's own colours.
- **Fallback colour:** applies to Missing / Unreadable / Procedural, and to a dropped texture (proxy or plain override). The asset tint is cleared on fallback, so the shading colour isn't tinted twice.
- **Tint.** An open question until a test model arrives:
  - **Multiply** (default) matches build A for bitmap tints.
  - **Keep lightness** is a "Color" blend: the tint's hue and saturation at the colour's luminance. A grey tint is treated as a brightness change. It's there for comparison and can be removed once the right blend is known.
  - **Off** ignores the bitmap, Hardwood and appearance tints, and also `RGBAmount`, which is folded into the image tint.
- **Invert** is a per-material flag in the table (t4.w bit 1), so an image shared by an inverted and a normal material draws right in both.
- **Overrides in Revit:** checked before the locator. An override image on a material without a bitmap gets 1 m repeats (there's no placement to use).
- **Search folders** come after the locator's own stages and use exact names only. Their indexes are cached while each folder's last-write time is unchanged. That only covers the top folder, so a file added deep inside needs Revit restarted, or the folder touched.
- **Review pass:** reads the materials exactly as the extraction would, including overrides and search folders, but decodes nothing. Found images show as "Found (stage)". Unreadable images only show up at extraction.
- **App mutability:** `MaterialData.With` plus `GameSession` holding the current set; `BimGoDocument.Materials` carries it to the writer. The scene snapshot stays immutable.
- **Images added in the app** use the same entry naming as Revit (`SHA1(lower path | cap)`), so they dedupe with extracted ones.
- **Live session:** the override file is written immediately (it's a per-machine preference); the in-app view updates at once.

## 4. Proxy pack (Gavin's ambientCG picks; sizes estimated by Claude from the images)

| Keyword | ambientCG | Size (m) | Note |
|---|---|---|---|
| brick | Bricks059 | 0.90 × 1.38 | ≈ 16 courses per repeat (86 mm) |
| blockwork | Bricks066 | 1.20 × 1.80 | 3 blocks × 9 courses (400 × 200) |
| concrete | Concrete034 | 2.40 × 1.20 | 1024 × 512 source |
| concrete-board | Concrete044A | 2.40 | |
| render | Plaster001 | 2.00 | |
| timber-floor | WoodFloor040 | 1.20 | ≈ 9 boards per repeat |
| timber-panel | Wood003 | 1.20 | |
| plywood | Chipboard005 | 1.20 | |
| carpet | Carpet016 | 2.00 | |
| vinyl | Plastic012A | 1.00 | Charcoal (taken to the material colour by default) |
| tile-300 | Tiles107 | 3.60 | 12 × 12 tiles |
| tile-600 | Tiles133B | 4.80 | 8 × 8 tiles |
| marble | Marble004 | 1.50 | |
| marble-brushed | Onyx005 | 1.50 | "Marble, figured": by hand, or names with figured / feature marble / onyx |
| stone | PavingStones128 | 2.00 | |
| metal-brushed | Metal009 | 1.00 | |
| metal-galvanised | Metal052C | 2.00 | |
| gravel | Gravel043 | 1.50 | |
| grass | Grass001 | 2.00 | |
| asphalt | Asphalt031 | 2.50 | |
| fabric | Fabric030 | 0.30 | |

Resized to 512 px (JPEG 85, ≈ 1 MB in total). Sizes are in `proxies.json` and can be tuned without a rebuild (copy to the
output, or edit it beside the exe). The installer round must include `Resources\Proxies\`.

## 4b. Tint test model results (Gavin, 2026-10-07, Revit 2025)

Seven walls, A → G (scan report `MaterialScan_Project1_261007_183355.txt`, Revit Realistic and BimGo screenshots). The
average colour of each wall was measured and normalised by wall B (grey 0.5) to remove the lighting difference:

| Wall | Setup | Revit | BimGo (build B as sent) | Finding |
|---|---|---|---|---|
| A | Grey 0.5, appearance tint red | R = B's grey | R = B's grey | **Tint is a multiply** |
| B | Grey 0.5, no tint | Control | Control | |
| C | Brick image, appearance tint red | Red channel of the brick | Same | Multiply ✓ |
| D | Brick image, no tint | **Brown** | **Grey-green** | ❓ Open: not explained by the material data (C, D and G are identical except for invert). Waiting on the exported `.bimgo` + JPEG |
| E | Generic "Pine" image, appearance tint red | Red (clipped) | Red | Multiply ✓ (a Generic asset; Prism tint not tested) |
| F | Blue colour, 50 % image fade, tint red | 0.80 × B | 0.58 × B | **Revit fades in linear light**: linear mixing predicts Revit's 137 exactly |
| G | Brick image inverted | Near-white cyan | Mid blue | **Revit inverts in linear light** (1 − linear) |

Changes made:

- `MATERIALS_GLSL` linearises the colour and image (gamma 2.2), then inverts, applies the image tint, fades and applies the appearance tint in linear light, and converts back to display values. Proxies are coloured before linearising.
- The tint switch is On / Off. `TintMode.KeepLightness` is retired and reads as Multiply.
- The tint is applied as given (colour space 2 assumed sRGB; untested beyond pure red).

The WebGL2 check was re-run: plain colours are unchanged, and a wall-F-like case gives 186 (linear) instead of 128.

## 5. Test checklist for Gavin

1. Build R25 and the app; run the Core tests. The new classes are `TextureSearchTests`, `ProxyCatalogTests` and `TextureOverrideTests`, plus `MaterialTests`.
2. **BG01, with the render appearance path removed from Revit Options:**
   - Options → Review textures…: the `BG_…` images are listed as Missing.
   - Scan a folder… → `D:\RV Revit\BG\Support Files\Materials\Maps`: stage 1 finds them, pre-ticked. Accept, done, tick "Remember", then Save.
   - Go: they show. The log has "found in search folders" / "overridden".
   - Restart Revit: they're still found (override file + remembered folder).
3. **Loose stage:** rename `BG_Carpet_Plain1.jpg` → `bg-carpet-plain-1.JPEG`. It's offered at stage 3, unticked; tick it by hand.
4. **Snowdon with the library folder renamed:** brick, concrete and timber get proxies at a believable scale in their shading colours. Whitecard and Material modes are unchanged. Toggle "Proxies take the material's colour" in the Textures panel.
5. **Fallback:** a material whose image is missing now draws its shading colour, not white.
6. **App, file mode:** open a `.bimgo` with missing images → Esc → TEXTURES → FIND IN FOLDER…, accept, then Ctrl+S. Reopen: the images are embedded. Also open that file in build A (it should load).
7. **App, live:** IMAGE… / PROXY on a missing material shows at once; F5 brings it back from Revit.
8. **Tint and invert:** with any tinted materials, compare Revit's Realistic view with Revit tint Multiply / Keep lightness / Off. Export report… (review window) lists TINT AND INVERT. Send the report, screenshots, and which mode matches.
9. **Performance:** a scan of a large folder (10 000+ files) stays responsive and cancellable. Check the review pass time on Snowdon.

## 6. To verify (compile risks)

- **WPF:** `Microsoft.Win32.OpenFolderDialog` (.NET 8+, both target frameworks); binding to the window's nested row classes (made `internal` for that reason); the `Chip` RadioButton template.
- **Revit API:** `new ElementId(long)`, and `Element.GetMaterialIds(true)` on all categories in the review (exceptions are caught per element).
- **WinForms:** `FolderBrowserDialog.InitialDirectory` / `UseDescriptionForTitle` (.NET Core 3+).
- **Layout:** the Textures panel on small screens, and the 4-column proxy grid's height.
- **Removed button:** `Tooltips.resx` / `.Designer.cs` lost the Material scan entry, and the icons were deleted. If the ribbon helper looks icons up by command name, nothing references them any more.
