# BimGo: Materials round, build notes, stage 0 (diagnostic)

**Status: experimental round.** If materials can't be made to work, this whole round is rolled back to the
`BimGo_1.0_Handoff_Materials.zip` working copy and the next chat starts from the original brief as if it never happened.

Claude could not compile here (no .NET SDK or Revit API). The C# was reviewed by eye. The lines most likely to need a
touch-up are the `Visual.Asset` API calls in `MaterialScan.cs`: `Asset.Get(int)`, `AssetPropertyList.GetValue()`,
`AssetPropertyEnum.Value` and `AssetPropertyDistance.Value`. Less common property types are read by reflection on
purpose, so they compile whatever the Revit year.

---

## 1. Decisions agreed with Gavin (2026-10-17)

| Topic | Decision |
|---|---|
| Opt-in | Texture extraction is **optional**, chosen at Go (Options window), **off by default**: a light model stays light. Off = identical output to today. |
| Texture size | User picks the max size (256 / 512 / 1024 / 2048 px longest side; default 512). Aspect is kept; rectangular maps use their own real-world X / Y scale. |
| UV target | Match Revit UVs wherever possible: **Stage 2** face-parameterisation UVs (scale X/Y, offset, rotation), box mapping as the fallback. `CustomExporter` only if Stage 2 visibly misses. |
| Colour modes | Add **Realistic** (render colour + textures) beside Whitecard and Material (shading colour, as today). |
| Pre-extract reconcile | "Review textures…" sub-window from the Options window. It covers the materials of what's about to be loaded: status per material (Found / Found by search / Missing / Proxy / Colour only), browse to a file, assign a proxy. |
| Post-extract reconcile | **In the app** (pause-menu Textures panel). Point at a folder; the app loads the images and writes them back into the `.bimgo`. No Revit needed. |
| Deep scan | User nominates a folder. It **reconciles missing textures only**. Loose matching is used where it clearly suits; otherwise only explicit hits count. Anything that doesn't reconcile **falls back to the shaded appearance** (colour). |
| Proxies | User-picked image per material **plus** a shipped **CC0 keyword pack** (brick, timber, concrete, carpet, tile, stone, render, metal, gravel, grass…). Needs a THIRD-PARTY-NOTICES entry. Never Autodesk textures in the app. |
| Autodesk Material Library | **Never assumed installed.** Probe for it (standard folders, registry), and recognise Autodesk-supplied textures from path syntax (`n\Mats\…`, `Autodesk Shared\Materials`, `Materials\Textures`). |
| Remembering | Manual picks and proxies remembered **per Revit model**; search folders remembered **globally**. |
| Live F5 | Textures cached by material + path across refreshes; only new or changed materials are re-read. |
| Extraction cost | A slower export with textures on is acceptable (measure and report). |
| Out of scope | Bump / normal maps, alpha cutouts (leaves, grilles). |
| Reflections | **Sky reflection on glass, switchable**: Fresnel-weighted sky / sun reflection on transparent and reflective materials. The nearby cube-probe is a possible later stretch; screen-space reflections skipped. |
| Order | **Stage 0 diagnostic first.** Gavin runs it on real models and sends the report back before the extraction is written. |

## 2. What stage 0 adds

| File | What |
|---|---|
| `BimGo.Revit/Extraction/TextureLocator.cs` | Texture discovery and path resolution (reused by the real extraction). Records every discovery step in `Notes`. |
| `BimGo.Revit/Extraction/MaterialScan.cs` | The diagnostic: scans host + loaded links, counts material use (`GetMaterialIds`, geometry + paint), dumps used materials' appearance assets in full, resolves bitmaps, reads image headers (System.Drawing, no decode), writes the report. |
| `BimGo.Revit/Commands/Cmds_BimGo.MaterialScan.cs` | `Cmd_MaterialScan`: progress window with Cancel, then a dialog to open the report or its folder. |
| `Application.cs` | "Material scan" button on the Walkthrough panel (temporary). |
| `Resources/Icons16|32/BimGo_MaterialScan*.png`, `Tooltips.resx` / `.Designer.cs` | Icon and tooltip. |

Report: `%LocalAppData%\BimGo\Logs\MaterialScans\MaterialScan_<model>_<yyMMdd_HHmmss>.txt`, with these sections:
texture locations on this machine → summary (counts, schemas, slots, found-by-stage, origin, formats, embed / GPU
estimates at 256–2048 px) → missing bitmaps → compact texture lines with placement (real-world scale, offset, angle,
repeat) → property names by schema → per-material dump (used materials in full; unused as headers).

## 3. What we want to learn from Gavin's reports

1. The schema names actually present (Generic vs Prism / Advanced / Physical) and the property names for the diffuse
   colour, diffuse bitmap, and transparency / reflectivity (for the glass reflection).
2. Whether Advanced materials keep the bitmap as a connected asset on e.g. `opaque_albedo`, and where their real-world
   scale lives.
3. How bitmap paths are written: absolute, `1\Mats\…` relative, multiple `|` alternatives.
4. Found / missing ratio with and without the Material Library, and which stage found them.
5. Real-world scale units (`AssetPropertyDistance` units) and whether `texture_WAngle` / offsets are commonly set.
6. Image formats (any `.tif` / `.exr` / `.tga` that System.Drawing can't decode) and typical sizes, to set the default cap.
7. How long the usage count takes on a large model (the real extraction won't need it, but it's a cost signal).

## 4. Next stage (after the reports)

Stage 1+2 together: the material table, embedded textures (`textures/<hash>.jpg`), `materials.json`, and per-vertex
material index and UV streams (additive entries, `formatVersion` stays 1). On the app side: the texture array, the
Realistic mode, the sky reflection on glass and the Textures panel. Then the Options window's "Extract textures",
size cap and "Review textures…" with deep scan, and the CC0 proxy pack.

---

## 5. Findings from Gavin's scans (2026-10-07, Revit 2025)

Models: **Snowdon Towers** (Autodesk sample, 7 docs, 753 materials / 199 used), **BG01** (4 docs with links, 284 / 101),
**BG05** (no links, custom textures, 40 / 27).

**Machine**
- The Material Library is installed at `C:\Program Files\Common Files\Autodesk Shared\Materials\Textures` (64-bit Common
  Files, not x86). The registry confirms it at `HKLM\SOFTWARE\Autodesk\ADSKTextureLibraryNew\{1,2,3}\Textures` →
  `LibraryPaths`, and that is the reliable probe. `ADSKAssetLibrary\n` points at `.adsklib` files (ignore).
- Revit.ini has **no** additional render appearance path entries.

**Paths**
- Library textures are stored as `1/Mats/x.jpg|2/Mats/x.jpg|3/Mats/x.jpg` (forward slashes, three alternatives) or
  `3\mats\…` (lower case). Resolving them against the library root found **114 of 115** in Snowdon.
- Gavin's own textures are stored as **bare file names** (`BG_Carpet_Plain1.jpg`), with no folder. Without an extra path
  or a deep scan they are always missing (BG01: 34 of 35 missing; BG05: 8 of 14). This is exactly the deep-scan case.
- An empty path (`""`) is common on Metal / PlasticVinyl pattern and perforation slots. That means "no texture", not a
  missing file. **The scan counted these as missing; the real extraction must skip them.**

**Schemas and the colour / diffuse slot to use** (everything else, i.e. bump, roughness, pattern, perforation, cutout,
`surface_albedo`, is ignored):

| Schema | Colour property | Diffuse texture = asset connected to |
|---|---|---|
| GenericSchema and legacy generic presets (`Generic-001`, `ACADGen-…`, `InvGen-…`) | `generic_diffuse` (Double4 **or Double3** in legacy) | `generic_diffuse`, blended by `generic_diffuse_image_fade` (1 = all texture) |
| PrismOpaqueSchema (Advanced, most common in Snowdon) | `opaque_albedo` | `opaque_albedo` |
| PrismMetalSchema | `metal_f0` | `metal_f0` |
| PrismLayeredSchema | `layered_diffuse` | `layered_diffuse` |
| PrismTransparentSchema | `transparent_color` | n/a (glass) |
| HardwoodSchema | `hardwood_tint_color` (if `hardwood_tint_enabled`) | `hardwood_color` |
| Ceramic / Metal / PlasticVinyl / WallPaint / MetallicPaint / Concrete / Masonry | `ceramic_color`, `metal_color`, `plasticvinyl_color`, `wallpaint_color`, `metallicpaint_base_color`… | none in practice |
| GlazingSchema | `glazing_transmittance_map` (colour), `glazing_reflectance` | n/a (glass) |
| Legacy presets with **0 properties** (`Paint-052`, `Wood-147`, `Metal-025`…) | none readable | none; fall back to the shading colour |

- In Prism, **`surface_albedo` is the specular / reflection map** (`…_refl.jpg`), **not** the diffuse. Never use it as
  the colour.
- The bitmap is a `UnifiedBitmapSchema` asset: path `unifiedbitmap_Bitmap`; `common_Tint_toggle` / `common_Tint_color`
  (tint on in 18 slots); `unifiedbitmap_Invert`, `unifiedbitmap_RGBAmount`. A connected `noise_*` asset (procedural) means
  the colour is used, not a texture.
- Diffuse colours may come with a `*_colorspace` integer (1 or 2). Revisit if colours look off: it may mean linear vs
  sRGB.
- Placement: `texture_RealWorldScaleX/Y` and `texture_RealWorldOffsetX/Y` are **Distance** properties in mixed units
  (inches, feet, centimetres seen). Convert with `UnitUtils.Convert(value, prop.GetUnitTypeId(), UnitTypeId.Meters)`.
  `texture_WAngle` is in degrees (0, sometimes 90). `texture_UOffset/VOffset/UScale/VScale` are usually 0 / 1.
  `texture_URepeat/VRepeat` are true. Rectangular scale (X ≠ Y) is common and matches rectangular images (256×512 at
  18″ × 36″).

**Glass**
- `generic_transparency` + `generic_reflectivity_at_0deg/90deg`; `transparent_color` + `transparent_ior` (Prism);
  `glazing_reflectance`; MirrorSchema. These are enough for a switchable Fresnel sky reflection.

**Sizes**
- Library diffuse images are small: 66 of 77 are 256 × 256; the largest is 1024 × 381. Embedded size is tiny (Snowdon ≈
  1.5 MB total).
- **GPU memory is the real constraint** if every layer is padded to the cap (Snowdon at 1024² ≈ 600 MB). Design
  decision: **bucket textures by size** into up to three arrays (256², 512², 1024²; each image goes in the smallest
  bucket ≥ min(its longest side, the user's cap)) rather than one array at the cap. Snowdon then needs ≈ 20–30 MB.
