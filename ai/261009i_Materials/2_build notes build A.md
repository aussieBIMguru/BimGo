# BimGo: Materials round, build notes, build A (Realistic mode core)

**Experimental round.** Roll back to `BimGo_1.0_Handoff_Materials.zip` if it doesn't work out. Decisions and scan
findings are in `1_build notes stage 0.md`.

**Not compiled.** Claude has no .NET SDK or Revit API here, so the C# was reviewed by eye. The GLSL was extracted from
`Shaders.cs`, then compiled, linked and run in headless WebGL2 (ANGLE / SwiftShader):

- the scene, AO-geometry, ground and glass-shadow programs all link;
- a test wall with a 2 × 2 material table and a two-layer array draws the image upright (top half of the image at the
  top of each 1 m repeat), at the right scale, and the untextured material in its render colour.

---

## 1. What was built

| Area | Files | What |
|---|---|---|
| Core | `Scene/MaterialData.cs` (new) | `MaterialData` (table, per-vertex `ushort` index, per-vertex metric UV, images by entry name, size cap), `SceneMaterial` (+ `Clean()`), `TextureState`. |
| Core | `Scene/SceneData.cs`, `Scene/LaunchSettings.cs` | `SceneData.Materials`; settings `ExtractTextures` (off), `TextureMaxSize` (512), `Reflections` (on), `ColourMode.Realistic`; sanitised. |
| Core | `Format/BimGoFormat.cs`, `FileModels.cs`, `BimGoWriter.cs`, `BimGoReader.cs` | `materials.json`, `material.bin` (`BMAT`), `textures/`. Written only when the streams match the vertex count. Read without ever failing the load: bad JSON / header / count drops the materials; indices past the table become NONE; non-finite UVs become 0; a missing image clears the reference (state Missing). |
| Tests | `MaterialTests.cs` (new), `TestData.cs` | Round-trip, shared image stored once, absent = not written, wrong count not written, damaged parts, mismatched streams, `Clean()`, settings. |
| Revit | `Extraction/SceneExtractor.Materials.cs` (new) | Material table, appearance reading per schema, texture find / resize / JPEG / session cache, surface coordinates. |
| Revit | `SceneExtractor.cs`, `SceneExtractor.Lighting.cs` | Hooks: settings, locator discovery (logged), `MaterialLook.Material`, `AddMesh(…, mapping, material)`, reset / over-limit / commit of the streams, `SourceModel.MaterialIndex`, `SceneData.Materials`. |
| Revit | `Forms/OptionsWindow.xaml(.cs)` | MATERIALS & TEXTURES card (tick, size, where textures will be found), Realistic radio (ticks the extraction; unticking falls back to Material). |
| App | `Rendering/MaterialTextures.cs` (new) | Size-bucketed RGBA8 arrays (256/512/1024/2048, mipmapped, repeat, anisotropic ≤ 8), RGBA32F material table (4 texels per material), GPU memory estimate, out-of-memory fallback. |
| App | `Native/Gl.cs` | `TexSubImage3D`, `GenerateMipmap`, float `TexParameter`, constants; entry points added to the required list. |
| App | `Rendering/Shaders.cs` | `SCENE_VS` attributes 4 (material) and 5 (UV); `MATERIALS_GLSL` (table lookup, rotation / offset / scale, V flip, `textureGrad` with gradients taken before branching, sky colour); `SCENE_FS` Realistic base colour + Fresnel sky reflection on glass. |
| App | `Rendering/SceneRenderer.cs` | Streams on both VAOs (static + moved / cloned), samplers on units 6–10 set once, `SceneDrawParams.Realistic / Reflections`, sky colours from the sun panel. |
| App | `Game/GameSession*.cs` | Colour mode Whitecard / Material / Realistic, "Sky reflections on glass" checkbox, status panel label, settings saved, toasts when a snapshot has no materials or textures failed. |

## 2. Design notes

- **Texture units:** 6 = material table, 7–10 = buckets 256 / 512 / 1024 / 2048. Units 1–5 stay as before (sun
  shadows, transmit, AO, glow, light shadows). The sampler uniforms are set even without materials: every sampler in a
  program needs a unit of its own type.
- **Buckets:** an image takes the smallest bucket ≥ min(its longest side, the cap), so Snowdon's 256 px library images
  cost 256² layers (≈ 25 MB total), not cap² layers. A bucket full to `GL_MAX_ARRAY_TEXTURE_LAYERS` spills to the next
  smaller one.
- **UV convention:** surface coordinates are metres, material-independent. Scale, offset and angle live in the table.
  So the in-app reconcile (build B) can swap an image or assign a proxy without re-extracting.
- **Mapping choices:**
  - Walls: U = Z × n (horizontal), V = n × U (up).
  - Faces with |n.z| ≥ 0.7: plan X / Y, so floors, ceilings and roofs up to ~45° are mapped in plan.
  - Coordinates are world-anchored (no per-face origin), so coplanar faces and wall joins continue the pattern.
    Revit starts each face's pattern at the face origin, so pattern start positions can differ from Realistic view;
    size, rotation and direction match.
  - Curved faces: `Face.Project` per vertex, scaled by the derivatives at the face middle. Whichever parameter runs more
    vertically becomes V.
- **Colour slot per schema:** see the stage 0 findings table. `surface_albedo` is never used as the colour. Unknown
  schemas fall back to the first `*_color` / `*_diffuse` colour. Legacy presets with 0 properties use the shading colour.
- **Reflectivity** is only for see-through materials: Generic transparency, Glazing, Prism Transparent, SolidGlass, or
  material transparency > 0. Sources: `glazing_reflectance`, `generic_reflectivity_at_0deg`, else from
  `transparent_ior`. Clamped 0.02–0.6. In the shader: Schlick Fresnel × 0.85 over the sky colour (zenith / horizon of
  the current time of day), which also raises the glass's alpha.
- **Transparency** still comes from the material (vertex alpha, opaque / transparent batches), in every mode.
- **Session cache:** images keyed by path + last write time + cap, up to 2000 entries, so F5 only re-reads changed or
  new images.

## 3. Test checklist for Gavin

1. Build R25 (and the app); run the Core tests (`MaterialTests` + everything else).
2. **Snowdon Towers:** Go with "Extract materials and textures" ticked, 512 px, colour Realistic. Check:
   - the log's `Materials:` line (expect ~100 textured, 1 missing) and `Textures:` line (library root found);
   - timber floors, concrete, metal panels at the right scale (brick ≈ 230 × 76 mm, oak boards along their length);
   - walls at 45° and curved walls;
   - glass shows a sky sheen that grows at grazing angles; the Sky reflections checkbox turns it off.
3. **BG01 / BG05** with the additional render appearance path set: the `BG_…` textures should now be found (log:
   "found via ExtraPath"). Carpet, ceiling tiles 600 × 600, plasterboard, blockwork 200 × 400 on rotated walls.
4. Untick extraction, Go again: Realistic shows material colours with a toast. The `.bimgo` from Export has no
   `materials.json` (same size as before).
5. F5 in a live session with textures on: the second extraction should be faster (cached images).
6. Moved / cloned elements keep their textures. Whitecard and the plan minimap are unaffected. Lights, glow, AO and
   shadows still look right on textured surfaces.
7. FPS with Realistic on vs Material on a big model; GPU memory at 1024 / 2048 caps (log: `Materials: … ≈ N MB`).
8. Open a new textured `.bimgo` in the previous build: it should load (the new entries are ignored).

## 4. Build B (next)

- **Review textures…** window before extraction: per material, status, thumbnail, browse to an image, assign a proxy.
  Deep scan of a nominated folder, missing textures only. Explicit hits first (exact name, then the same name with
  another extension); a loose name match only where it clearly suits. Anything that doesn't reconcile falls back to the
  shaded appearance.
  Picks remembered per model; search folders remembered globally.
- **In-app Textures panel** (pause menu): missing and proxy materials. Point at a folder, and the images are loaded,
  embedded and saved back into the `.bimgo` (the UVs are already metric, so no re-extraction).
- **CC0 keyword proxy pack** shipped in the app (THIRD-PARTY-NOTICES entry), for materials with no or a missing image.
- Remove the temporary Material scan button, or fold it into the review window as "Export report".
