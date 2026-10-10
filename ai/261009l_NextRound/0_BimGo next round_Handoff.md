# BimGo — Handoff Brief: next round (probe leaks, family library and placement, drop to floor)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**), built on his own PC in Visual Studio.
This brief starts a new chat. The last zip from Claude was **`BimGo_UX_BuildB.zip`** (UX cleanup round, builds A + B).

**Read first:**
1. This brief.
2. `README.md` §3 (controls), §5 (how it works), §9 history (UX cleanup, reflection probes).
3. `ai/261009k_UX_Cleanup/` notes 0–2 (decisions §9 in note 0) and `ai/261009j_ReflectionProbes/` notes 1–3.
4. **Ask Gavin for a fresh zip of his working copy before editing**, with any compile fixes to UX builds A / B. His copy
   is the source of truth. (UX A was confirmed good; B not yet tested when this was written.)

---

## 1. Where things stand

| Item | State |
|---|---|
| UX build A (app) | Confirmed by Gavin: hide-UI (U), important toasts, quality profiles, tabbed pause menu (Display · Reflections · Debug), sun panel tidy. |
| UX build B (Revit) | Delivered, **not yet tested**: 7-tab Options window with profile and sharing option; sidecars and texture overrides in `%LocalAppData%\BimGo\Models\<title>_<hash>\`. |
| Reflection probes | Working (build B / B.1). Known issue: **probes leak between rooms**. |
| Keys | Gavin wants a broad key-binding review before v1 (many keys arrived one round at a time). |

## 2. This round's items (Gavin's list)

### 2.1 Probe leaks between rooms, without performance cost
Gavin sees a neighbouring room's probe inside a room. Analysis from the UX handoff §7, with the constraint now
explicit: **no per-frame cost**. Every fix below happens either once when the lookup grid is built, or as a constant
offset in the shader.

| # | Likely cause | Fix | Cost |
|---|---|---|---|
| 1 | `ReflectionProbes.BuildGrid` blends the neighbour's probe into every cell within ~1 m of a room boundary, so each room shows its neighbour up to 1 m from **every wall**, not only at doorways. | Blend only where the boundary is open: door / opening elements (already in the scene by category) or a few rays through the BVH at grid build. Elsewhere, no blend (or 0.25 m). | Grid build only |
| 2 | Walls, glass and floors lie **on** the boundary, so the cell they sample may belong to the next room (0.5 m cells straddle walls). | Look up the cell at `world + normal × 0.3` (pushed into the room the surface faces). | One shader MAD |
| 3 | Height bands: a cell takes any room whose band overlaps by ±0.3 m, so a slab's cell can take the room below's probe. | Use the room whose BottomZ is nearest **below** the cell centre; halve the band height. | Grid build only |
| 4 | Coarse cells on big models (cell size grows above 4 M cells). | Log the final cell size; sparse grid if needed. | Memory only |

**Start with** pause menu → Debug → **Probes** colours on Gavin's test model to see which cause dominates, then fix
in that order (2 and 3 are small; 1 is the main one).

### 2.2 Family library: browse, search, filter and place new loadable families
A placement tool like the Clone gun, but picking from a **graphical library** of the loadable families in the model.

**What exists to build on**
- Clone gun (`Game/Guns/CloneGun.cs`) + `GizmoController`: makes a dynamic instance and goes straight into move mode;
  RMB commits, Esc discards. Journal op `clone` (`Core/Edits/EditJournal.cs`); Revit side `RevitEditor.cs` uses
  `ElementTransformUtils.CopyElement` then rotate / move. Push-to-Revit replays the journal for files.
- Thumbnails: bookmarks already draw thumbnails in the immediate-mode UI (`GameSession.Thumbnails.cs`).

**Questions to settle with Gavin (recommendation first)**
1. **Which families are offered:** the loadable family *types* already loaded in the model (FamilySymbol), filtered
   to the FFE / services categories Clone already allows? Or also load `.rfa` from a folder library?
   *(Recommended: loaded types first; an `.rfa` folder is a later step, since loading families changes the model.)*
2. **Geometry for types with no instance in the snapshot:** extract each offered type's geometry at Go by placing a
   temporary instance in a transaction that is **rolled back** (nothing stays in the model), or only offer types that
   already have an instance in the snapshot? *(Recommended: rolled-back temporary instances, in a background-ish
   pass with a progress bar and a cap, cached per type in the model folder; opt-in tick "Include family library".)*
3. **Previews:** Revit's own `ElementType.GetPreviewImage(size)` (fast, matches Revit's browser) or renders of the
   extracted geometry in the app? *(Recommended: Revit previews at extraction, stored as small PNGs in the snapshot.)*
4. **Hosting:** non-hosted and level-based families only at first; face-based / wall-hosted later?
   *(Recommended: yes, non-hosted / level-based first; hosted types listed but greyed with the reason.)*
5. **UI:** a panel (pause menu button **LIBRARY**, or a key) with a search box, category and family filters, a grid of
   previews; pick → the new instance appears in front of the player in the gizmo's move mode, as Clone.
6. **Files (.bimgo):** include the library in exports (bigger files) so offline placement works, or live sessions only?
   *(Recommended: live only at first; an export tick later.)*
7. **Journal / protocol:** new op `place` (type UniqueId, level, transform) and a matching edit request;
   `formatVersion` stays 1 (additive), older apps ignore it with a note.

**Likely files:** Revit `Extraction/` (type list, previews, rolled-back geometry pass), `Bridge/RevitEditor*.cs`
(`NewFamilyInstance`), Core `Edits/EditJournal.cs` + `EditMessages.cs` + `Format/` (library entries), App a new
`Guns/PlaceGun.cs` or a library panel feeding the Clone flow, `GameSession.Menu.cs`.

### 2.3 Drop to floor: hotkey to reset an element onto the surface below
When an FFE element has been moved (Gizmo / Clone, and the new Place), one key drops it so its bounding box bottom
rests on the first surface below.

- **Proposal:** while Gizmo / Clone holds an element, **F** (free today in every gun and globally) casts down from
  the bounding box (centre and four bottom corners, highest hit wins, so it sits on a bench rather than through it),
  against the static BVH and other dynamics **excluding the element itself**, and sets the vertical offset so
  `bboxMinZ = hit`. It stays in the gizmo (still uncommitted); RMB commits as usual, so Revit / the journal get an
  ordinary `transform`.
- Max drop distance (e.g. 10 m); nothing below → toast "Nothing below to drop onto".
- Questions: also a version for an element *not* held (aim + F with the Gizmo gun)? Snap up when the element is
  intersecting the floor (lift to the surface)? *(Recommended: yes to both: "drop or lift to the surface below the
  bbox centre".)*
- Files: `Guns/GizmoController.cs` / `GizmoPanel` keys, `Physics/Bvh.cs` + `DynamicSet.Raycast`, README §3.

## 3. Candidate ideas for later rounds (for Gavin to review)

Grouped; rough size S / M / L. Gavin to strike, reorder or promote.

**Controls and quality of life**
- **Key-binding review** before v1 (already planned), possibly with rebindable keys in settings. *(M)*
- **Find element:** search by element ID, Mark or name and teleport in front of it. *(S)*
- **Minimap:** click to teleport, room names / numbers on the plan. *(S)*
- **Undo in live sessions** through Revit's own undo (today: undo in Revit, then F5). *(M, needs care)*

**Views and presentation**
- **Section box / clipping plane** in the walkthrough (cut the model to see inside). *(M)*
- **Orbit / overview camera** (third-person or "drone" mode above the model, then drop back to walk). *(M)*
- **Photo mode:** supersampled high-res screenshots, and **360° panoramas** (equirectangular PNG) for phones and
  client reviews. *(M)*
- **Recorded fly-through:** place camera keys (like bookmarks), play them back smoothly, export frames. *(M; video
  encoding would need a package or an external ffmpeg)*
- **Sun study export:** a series of shadow screenshots across a day / dates from one viewpoint. *(S, builds on F12)*

**BIM data in the walkthrough**
- **Colour by parameter:** tint elements by a chosen parameter value (fire rating, phase, workset, status). *(M)*
- **Phase / design option switching** in the walkthrough. *(L, needs extraction support)*
- **Change highlight after F5:** colour elements changed in Revit since the last snapshot. *(M)*
- **Room info:** finishes and room parameters in the room banner / Scan panel. *(S)*

**Review and collaboration**
- **Comment upgrades:** a screenshot attached to each comment, status (open / closed), author, and **BCF export**
  (BIM Collaboration Format) so comments go into ACC / Revizto / BIMcollab. *(M–L, high value for reviews)*
- **Multi-user sessions:** two people in the same model with simple avatars. *(L)*

**Design checks (Australian context)**
- **Accessibility helpers:** wheelchair turning-circle and door-clearance checks (AS 1428.1), headroom on stairs. *(M)*
- **Clash with the player:** highlight things you walk into below a set head height. *(S)*

**Rendering and performance**
- **Instancing of repeated family geometry** (one mesh per type, many transforms): smaller files and snapshots, faster
  draws on furniture-heavy models. *(L, format change)*
- **Occlusion culling / LOD** for very large models. *(L)*
- **Screen-space reflections** as an option next to probes (sharper, but a per-frame cost). *(M)*
- **IES light profiles** for artificial lights. *(M)*
- **Render regression checks:** reference screenshots of a test model to catch shader regressions. *(M)*

**Distribution**
- **Update check** in the app (compare with a published version file). *(S)*
- **Web viewer for .bimgo** (WebGL, read-only) so clients need no install. *(L)*
- **VR (OpenXR)** walkthroughs. *(L)*

## 4. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations**.
- **BimGo.Revit stays package-free;** no new packages in the app without Gavin's yes.
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`, show a toast or dialog; errors use
  `Toast(…, important: true)` so they show while the UI is hidden.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`. Nothing changes the
  Revit model except the user's own edits (and, if agreed in 2.2, rolled-back temporary transactions).
- Format and protocol stay backward compatible (`formatVersion` 1; additive optional fields). Settings migrate quietly.
- Run the Core tests after any Core change (MSTest 4: `Assert.ThrowsExactly`, not `ThrowsException`).
- Keep `README.md` and the round's `ai/` notes current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
- Claude can't compile here (no .NET SDK reachable). GLSL is checked in headless WebGL2 (Playwright + the
  pre-installed Chromium: `extract.py` pulls the shader strings out of `Shaders.cs`, `#version 330 core` → `300 es`;
  give every sampler its own unit in the test). Say so in the notes.
