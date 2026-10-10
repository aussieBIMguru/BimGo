# BimGo — Handoff Brief: next round (explore the idea list + Gavin's additions)

**Context:** BimGo is Gavin's personal project (publisher **Aussie BIM Guru**, MIT), built on his own PC in Visual
Studio. This brief starts a new chat. The last zip from Claude was **`BimGo_Comments_SunHours_Fix1.zip`**: Gavin
confirmed it builds and works (the sun hours study especially).

**Read first:**
1. This brief.
2. `README.md`: *For AI assistants*, §3 (controls), §5 (how it works), §8 (limitations), §9 (history).
3. `ai/261009m_Comments_SunHours/` notes 0–1 (the last round) and, if a candidate touches them, the matching round's
   notes (§9 lists every folder).
4. **Ask Gavin for a fresh zip of his working copy before editing** (it is the source of truth), and for **the ideas he
   added since this brief**. Then agree this round's picks before building.

Note on dates: Claude's earlier rounds drifted ahead of the real date; folders are `yymmdd` plus a letter for same-day
rounds. Use the real date for the new round's folder (e.g. `261010a_…`).

---

## 1. Where things stand (all built and confirmed by Gavin)

| Area | State |
|---|---|
| Core | Live sessions, `.bimgo` files, journal + push, phases, linked models, bookmarks, coordinates, sun / shadows, AO, lights, Realistic materials, reflection probes (leak fixes: as good as it gets without a per-frame cost), quality profiles, hide UI |
| Last two rounds | Family library + Place gun (9) incl. work-plane families; drop / lift to surface (F); ground plane saved per model; comments as issues (status, priority, assignee, replies, saved view + thumbnail); direct sun hours study (J, Ladybug 0–7 h, CSV + legend screenshot); Find room (Ctrl+F) |
| Known open points | Rooms bounded at wall centres lose sun-hours wall cells (click walls instead); BCF deferred; keys grew one round at a time (review planned before v1) |

## 2. Candidate ideas (for Gavin to strike, reorder or promote)

Size: **S** (an evening), **M** (a round), **L** (several rounds / format change). ★ = Claude's suggestion for a good
next pick (value for the effort, builds on what exists).

**Follow-ups to what was just built**
- ★ **BCF 2.1 export (+ import)** of comments: `.bcfzip` with markup, viewpoint (camera from the saved view),
  snapshot (the thumbnail, or a full-size capture), element GUIDs (UniqueId; IFC GUIDs would need extraction), status /
  priority / assignee mapped to BCF fields. For ACC / Revizto / BIMcollab. *(M)*
- ★ **Sun hours, more:** save a study with the model; several days or a date range (average or minimum); compare two
  studies (difference colours); a "% of time in sun" mode; pass / fail colouring against a target (e.g. ≥ 2 h, ADG
  style); study the outside of the façade or the site. *(S–M each)*
- **Comments, more:** filter "assigned to me"; sort by priority / date; a full-size snapshot on demand; markup
  (arrows / circles drawn on the snapshot). *(S–M)*
- **Family library, more:** load `.rfa` from a folder (changes the model: needs care); hosted families on walls /
  ceilings (pick the host face); type parameters on the card. *(M)*

**Controls and quality of life**
- ★ **Key-binding review** (planned before v1), possibly rebindable keys in settings and a printable key sheet.
  Free letters today: M, P (F, J and Ctrl+F were taken this round). *(M)*
- **Minimap upgrades:** click to teleport, room numbers / names on the plan. *(S)*
- **Undo in live sessions** through Revit's own undo (today: undo in Revit, then F5). *(M, needs care)*

**Views and presentation**
- ★ **Section box / clipping plane** in the walkthrough (cut the model to see inside). *(M)*
- **Orbit / overview camera** (third-person or drone above the model, then drop back to walk). *(M)*
- **Photo mode:** supersampled high-res screenshots and **360° panoramas** (equirectangular) for phones and clients. *(M)*
- **Recorded fly-through:** camera keys (like bookmarks), smooth playback, frame export (video needs a package or
  ffmpeg: Gavin's yes). *(M)*
- **Sun study series:** shadow screenshots across a day or dates from one viewpoint (builds on F12 and the sun panel). *(S)*

**BIM data in the walkthrough**
- **Colour by parameter:** tint elements by a parameter value (fire rating, phase, workset, status) with a legend. *(M)*
- **Change highlight after F5:** colour what changed in Revit since the last snapshot. *(M)*
- **Room info:** room parameters / finishes in the room banner or the Scan panel. *(S)*
- **Phase / design option switching** in the walkthrough. *(L, extraction support)*

**Design checks (Australian context)**
- **Accessibility helpers:** wheelchair turning circles, door circulation spaces (AS 1428.1), stair headroom. *(M)*
- **Clash with the player:** highlight what you walk into below a set head height. *(S)*

**Rendering and performance**
- **Instancing** of repeated family geometry (smaller files, faster furniture-heavy models). *(L, format change)*
- **Occlusion culling / LOD** for very large models. *(L)*
- **Screen-space reflections** as an option beside probes (sharper, per-frame cost). *(M)*
- **IES light profiles** for fixtures. *(M)*
- **Render regression checks:** reference screenshots of a test model. *(M)*

**Distribution**
- **Installers** (Inno Setup, per user; handoff in `ai/261009e_Installer`, never built). *(M)*
- **Update check** in the app. *(S)*
- **Web viewer for `.bimgo`** (WebGL, read-only). *(L)* · **VR (OpenXR)**. *(L)*

**Gavin's additions since this brief:** *(ask)*

## 3. How to run the round
1. Get the zip and Gavin's added ideas; agree 1–3 picks (Claude may suggest the ★ items).
2. For each pick, ask the few questions whose answers change the design (recommendation first, as before), then build.
3. Deliver a zip plus build notes in the new `ai/` folder; update README (§3, §5, §6, §8, §9) and the project docs.

## 4. Conventions (unchanged)
- Readable, robust code, XML doc headers, explicit types where clearer; **no per-frame allocations** (cache labels;
  no lambdas or string formatting in per-frame UI).
- **BimGo.Revit stays package-free;** no new packages in the app without Gavin's yes.
- No exceptions reach the user: log via `Utilities.Log_Utils.Write`; toasts, with `important: true` for errors.
- Revit API only in `Commands/`, `Extraction/` (incl. `FamilyPlacer`), `Bridge/RevitEditor*.cs`,
  `Live/LiveDispatcher.cs`. Nothing changes the model except the user's edits and the family library's rolled-back
  transaction. Go is `TransactionMode.Manual` (needed for that); Export / Live stay ReadOnly.
- Format and protocol stay backward compatible (`formatVersion` 1, protocol 1; additive optional fields only).
- `GameSession` is split over many partial files: **check for duplicate member names across all of them** before
  adding constants or helpers (last round's only build error was a second `MONTHS`).
- Run the Core tests after Core changes (MSTest 4: `Assert.ThrowsExactly`).
- Claude can't compile here (no .NET SDK or NuGet). GLSL is checked in headless WebGL2 (Playwright + Chromium;
  extract the strings from `Shaders.cs`, `#version 330 core` → `300 es`). Say so in the notes.
- Zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
