# BimGo — Handoff Brief: Revit materials, textures and UVs (experimental round)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**), built on his own PC in Visual Studio. This brief starts the next chat.

**Purpose of the next chat:** an **experimental** round to bring Revit material appearance into the walkthrough: the material identity, texture images, real-world scale and alignment. This was flagged as the riskiest item on the rendering list. It goes ahead with eyes open, staged so each stage stands on its own and can be backed out.

**Read first:**
1. This brief.
2. `README.md` §5 (engine, AO, artificial lights), §6 (format), §8 (limitations), §9 (latest changelog entries).
3. `ai/261009f_Rendering_AO/`, `ai/261009g_Rendering_Lights/`, `ai/261009h_Gizmo_Modes/` build notes.
4. **Ask Gavin for a fresh zip of his working copy before editing.** He builds in Visual Studio, and his copy is the source of truth. The last zip from Claude was `BimGo_1.0_GizmoModes.zip`.

---

## 1. Where BimGo is now (2026-10-09)

| Round | State |
|---|---|
| 1.0.0 + dependencies (Silk.NET GL facade, Core tests, MIT) | Built, tests pass |
| **Installer** (`ai/261009e_Installer/`) | **Not started**: still pending, deliberately parked behind the rendering rounds |
| **Rendering 1: SSAO** | Built, confirmed working |
| **Rendering 2: artificial lights + glow** | Built, confirmed working |
| **Gizmo / Clone move ↔ rotate modes** | Built, confirmed working |

### What the renderer does today

- **Vertex:** `SceneVertex` is 28 B: position, normal and **RGBA8 colour baked from `Material.Color`**. A second optional per-vertex stream, **emissive RGBA8**, is attribute 3; it is only uploaded when the model has glowing surfaces and is expanded from `lighting.json` runs.
- **Batches:** batched by visibility group (category × model) and multi-drawn per chunk. **No per-material state**, so nothing in the draw path knows what material a triangle is.
- **ScreenEffects** (`Rendering/ScreenEffects.cs`) runs a half-resolution geometry pre-pass:
  - MRT target 0: view normal + view depth (RGBA32F), the AO input.
  - MRT target 1: glow (RGBA16F), the bloom source.
  - The AO result is on texture unit 3; the bloom passes use unit 4.
- **Artificial lights:**
  - `GameSession.Lights` picks the nearest 32 lights in view each frame.
  - Each light has a cached omnidirectional shadow map (`LightShadows`: 32 × 6 × 256 px, a 16-bit depth array on texture unit 5).
  - The light shaders live in `Shaders.LIGHTS_GLSL`.
  - Sun shadow maps use units 1–2. **Free texture units from 6 up.**
- **Extraction:** `SceneExtractor` walks `get_Geometry`, triangulates faces and caches a `MaterialLook` per material: colour, self-illumination and keyword flag (`SceneExtractor.Lighting.cs`). It **already opens each material's appearance asset**, using `AppearanceAssetElement.GetRenderingAsset()` and `Asset.FindByName`. That's the foothold for this round.
- **Format:** a `.bimgo` file is a ZIP. Optional entries are additive and ignored by older readers; `lighting.json` was added this way, so `formatVersion` is still 1. `geometry.bin` has its own layout version: `BGEO` header, vertex size 28.

### Lighting-round tuning knobs (for reference)

| Knob | Where |
|---|---|
| `LIGHT_SCALE` (100 lx ≈ full albedo), radius √lm × 0.16 clamped 2.5–9 m, daylight factors | `GameSession.Lights.cs` |
| `LIGHT_BOUNCE` 0.06, shadow normal offset / bias | `Shaders.LIGHTS_GLSL` |
| AO radius, intensity, max depth | `ScreenEffects` constants |

---

## 2. Concerns, stated up front

1. **Revit tessellation has no UVs.** `Face.Triangulate()` gives positions and normals only. UVs must come from one of three sources:
   - (a) the face's own parameterisation (`Face.Project` / `Face.Evaluate`);
   - (b) Revit's render pipeline (`CustomExporter` + `IExportContext`, whose `PolymeshTopology.GetUVs()` returns the same UVs Revit renders with);
   - (c) no UVs at all: world-space projection (triplanar).
2. **The texture images may not exist on the machine.**
   - Appearance assets reference bitmaps by path. Library materials point into the **Autodesk Material Library** (e.g. `C:\Program Files (x86)\Common Files\Autodesk Shared\Materials\Textures\…`), often as relative paths (`1\Mats\…`).
   - The library is an optional install, and render appearance paths can be added in Revit Options.
   - Standalone `.bimgo` users certainly won't have it, so images must be **embedded** in the file to travel.
3. **File size.** Embedding textures can add tens to hundreds of MB. Downscaling to 512 px (1024 max) at extraction, dedupe by path and JPEG/PNG re-encode are needed.
4. **Licensing.** Embedding Autodesk library textures in a file the user keeps for their own model is ordinary use. Shipping them in BimGo itself is not allowed. **Never bundle Autodesk textures with the app.** Any built-in fallback textures must be CC0 (ambientCG, Poly Haven).
5. **Shaded vs realistic colour.** `Material.Color` (what we bake today) is Revit's *shading* colour. The appearance asset's diffuse (`generic_diffuse`) is the *render* colour, and they often differ. Choosing per mode, or offering both, is a decision for Gavin (§6).
6. **Draw-path impact.** Per-material textures must not break the batching that keeps big models fast. Use **one texture array plus a per-vertex layer index** (a single draw path; see §4), not texture binds per material.
7. **Interaction with existing systems.**
   - Whitecard mode must bypass textures.
   - Emissive and lights multiply by albedo, so textured albedo just works.
   - The AO and glow pre-pass doesn't need textures.
   - Alpha-cutout (leaves, grilles) is out of scope: textures are opaque.
8. **Effort on links.** Linked documents have their own materials and assets; the per-document caches (`SourceModel`) already handle this pattern.
9. **The `CustomExporter` route is a near-rewrite of extraction.** Phases, active-view-only, link transforms, helper-geometry skipping, proxies, triangle thresholds and element records are all built around `get_Geometry`, and the two tessellations can't be matched triangle-for-triangle. **Treat it as a last resort.**

---

## 3. Approaches (staged, each reversible)

### Stage 1: material table + embedded textures + triangle-normal box mapping (lowest risk)

- **Extraction:** per material (cached in `MaterialLook`), read the appearance asset:
  - The diffuse texture is the connected asset on `generic_diffuse` (`AssetProperty.GetSingleConnectedAsset()`, a UnifiedBitmap asset).
  - Its path is `unifiedbitmap_Bitmap`; it may hold several paths separated by `|`.
  - Real-world scale is `texture_RealWorldScaleX` / `…Y`; offset is `texture_RealWorldOffsetX` / `…Y`; rotation is `texture_WAngle`.
  - Also read the tint / `generic_diffuse_image_fade` and the render colour `generic_diffuse`.
  - Advanced / Physical (Prism) materials use other names (`surface_albedo`, `opaque_albedo`…). **Log every unknown schema and property set seen** (`asset.Name`, `AssetProperty.Name` dump) on Gavin's real models before hard-coding names.
- **Resolve the path:** absolute path; else relative to the Autodesk library roots; else Revit's additional render appearance paths (if readable); else not found (logged and counted).
- **Embed:** load, downscale to ≤ 512 or 1024, re-encode, store in the zip as `textures/<hash>.jpg|png`, deduped by path + size. System.Drawing is available on both sides (Revit WPF/WinForms; the app has WinForms) with no new package. **BimGo.Revit must stay package-free.**
- **Format (additive):**
  - `materials.json`: per material, name, shading colour, render colour, transparency, texture ref, scale X/Y, offset, rotation, glow fields.
  - `material.bin`: an optional per-vertex `ushort` material index stream (or runs, like the emissive runs).
  - Older readers ignore both. Keep `formatVersion` at 1 if at all possible.
- **App:**
  - One `GL_TEXTURE_2D_ARRAY` (all layers resized to a common size, e.g. 512²) with mipmaps. This needs a `glGenerateMipmap` wrapper in `Gl.cs` plus its required entry point, and anisotropic filtering via the extension if present.
  - Per material, a small uniform or texture-buffer table: layer, scale, rotation, offset, tint.
  - A per-vertex material index on attribute 4 (only when the model has textured materials).
- **Mapping:** pick the projection plane from the **triangle's own normal** (box mapping in world space, 3 planes) with real-world scale. This gives correct size and roughly correct orientation on axis-aligned BIM geometry and needs no UVs. It is a hard per-face choice rather than a triplanar blend; flat BIM faces don't need blending.
- **Result:** bricks, timber, carpet and tiles at the right scale. Alignment is only correct relative to world axes (coursing won't start at a wall's edge), and walls at 45° get a stretched projection.

### Stage 2: proper per-face UVs from the face parameterisation

- In `AddSolid`, for each triangulated face, compute each vertex's UV with `face.Project(point).UVPoint`, converted to metres.
  - For `PlanarFace` the UV is metric along the face's own axes, so alignment follows the face. Rotated walls work and coursing starts at the face origin.
  - For curved faces the parameter space isn't metric, so fall back to Stage 1 mapping or scale by derivatives (`face.ComputeDerivatives`).
- Store UVs as a third optional stream (`uv.bin`, 2 × half or float per vertex). Shader: use UVs when present, else box mapping.
- **Unknowns:** how Revit's own render UVs relate to the face UVs (offset and origin), and whether it matches what users see in Realistic view. Expect "close but not identical".
- Cost: one `Project` call per vertex at extraction (slower export; measure it).

### Stage 3 (only if Stages 1–2 disappoint): the `CustomExporter` route

- `IExportContext` gives polymeshes with **render UVs** (`PolymeshTopology.GetUVs()`) and `OnMaterial(MaterialNode)` with the appearance. This is what Enscape, Twinmotion and similar exporters use.
- **Option to evaluate:** run it *only* for textured materials' elements and replace their geometry wholesale (not a triangle match). That still means two geometry paths and divergence risk.
- Requires a 3D view; link handling is via `OnLinkBegin`.

### Fallback at any stage: keyword textures

CC0 tileable textures selected by material-name keyword (brick, timber, concrete, carpet, tile, render…) with the same mapping. They would ship in the app, CC0 only, and cover models with no appearance bitmaps or no Material Library.

---

## 4. Suggested shader shape (Stage 1–2)

```glsl
// scene FS, after base colour is known
if (uTextures == 1 && uWhitecard == 0 && vMaterial >= 0) {
    MaterialInfo m = materials[vMaterial];          // layer, scale, rot, offset, tint (texture buffer or UBO)
    vec2 uv = hasUv ? vUv : boxProject(vWorld, faceNormal, m);   // metres → repeats
    vec3 tex = texture(uMaterialTextures, vec3(uv, m.layer)).rgb; // sRGB → linear if we go linear later
    base.rgb = mix(base.rgb, tex * m.tint, m.fade);
}
```

- **Material table:** GL 3.3 has no SSBOs. Use a `samplerBuffer` (texture buffer, GL 3.1) or a UBO (≥ 16 KB is guaranteed, so about 1000 materials × 16 B). Choose by measured material counts.
- **Box mapping** must use the *geometric* normal (flat per triangle: `dFdx`/`dFdy` cross, or the vertex normal for planar faces) to avoid swimming.
- **Mip selection:** world-space UVs work with normal derivatives. Add anisotropic filtering for floors at grazing angles.

---

## 5. Likely files touched

| Area | Files |
|---|---|
| Core | `Scene/MaterialData.cs` (new: table, per-vertex index, texture refs), `SceneData.Materials`, `Format/*` (new optional entries + `textures/` folder), tests (round-trip, absent, damaged, dedupe) |
| Revit | `Extraction/SceneExtractor.Materials.cs` (new partial: asset reading, path resolution, image load / downscale / encode), hooks in `AddMesh` (material index per vertex, as `_tmpEmissive` does), the `MaterialLook` cache, maybe an Options toggle "Extract textures" + max size |
| App | `Rendering/MaterialTextures.cs` (new: array upload, mipmaps, table), `Gl.cs` (`GenerateMipmap`, texture buffer / UBO wrappers, anisotropy constant), `Shaders.cs` (`SCENE_VS` attributes 4 and 5, FS sampling), `SceneRenderer` (VBO streams on both VAOs, like `UploadEmissive`), a pause-menu toggle (Textures on/off) and a colour mode (Shaded / Realistic?) |

---

## 6. Questions for Gavin (ask, don't assume)

1. **Fidelity target:** "looks like Revit Realistic view" or "plausibly textured"? This decides whether Stage 2 or 3 is needed.
2. **Colour source:** keep `Material.Color` (shaded) for untextured surfaces, switch to the render colour, or offer a third colour mode (Whitecard / Material / Realistic)?
3. **Material Library:** which machines have it, and which Revit years' library paths? Can he send a log of asset property names from a real model (Stage 1 starts with a dump)?
4. **File size tolerance** for embedded textures (texture size cap 512 vs 1024; JPEG quality)?
5. **Extraction cost:** acceptable slowdown for Stage 2's per-vertex `Face.Project`?
6. **Live sessions:** extract textures on every F5 refresh, or cache by material and path across snapshots?
7. **Bump / normal maps and reflectivity:** confirm out of scope (recommend: yes, out).
8. **Keyword CC0 fallback** pack: wanted? It would ship in the app and needs a THIRD-PARTY-NOTICES entry even though CC0.

---

## 7. Test plan

- A model with library materials: brick, timber floor, carpet, tile, concrete. Check scale (one brick ≈ 230 × 76 mm), orientation on rotated walls, curved walls, sloped roofs, stairs.
- A model using custom textures from a user folder: path resolution, and a missing file is logged and falls back to colour.
- Linked model materials.
- File size before and after; extraction time before and after; FPS with textures on vs off.
- Older `.bimgo` files open unchanged; a new file opens in the previous build (optional entries ignored).
- Whitecard bypasses textures; emissive, lights and AO still correct on textured surfaces.

---

## 8. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations**.
- Dependency policy: README *For AI assistants* item 3 and §10. **BimGo.Revit stays package-free.** Any package needs Gavin's yes.
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`, show a toast or dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Format and protocol stay backward compatible (`formatVersion` 1, protocol 1; additive optional entries).
- Run the Core tests after any Core change.
- Keep `README.md` and an `ai/<date>_<topic>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
- Claude can't compile here (no .NET SDK or Revit API). GLSL can be compiled and run in headless WebGL2 to check it; the C# is reviewed by eye. Say so in the notes.
