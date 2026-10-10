# BimGo — Handoff Brief 1.0 (for a new chat)

**Purpose of the next chat:** build and test 1.0 (active view only, helper geometry filter, hide / isolate, visibility saved with the model, F12 screenshots, version 1.0.0), fix what the compiler / Revit finds, and prepare the release (installer / signing, release notes). Then revisit the guns.

**Read first:**
1. This brief.
2. `README.md` (controls, `.bimgo` format, live protocol, known limitations, changelog).
3. `ai/261009c_V8_1.0/1_build notes 1.0.md` (decisions, files touched, "To verify", test checklist). Linked models: `ai/261009b_V7/`.
4. **Ask Gavin for a fresh zip of his working copy before editing** (he builds in Visual Studio; his copy is the source of truth).

## 1. Where BimGo is now

- Solution `src/BimGo.sln`: **BimGo.Core** (net8.0), **BimGo.App** (net8.0-windows, `BimGo.exe`), **BimGo.Revit** (R25/R26 net8, R27 net10). Version **1.0.0**.
- v6 (sun / shadows) and v7 (linked models) built and work (Gavin, 2026-10-09; v7 needed only `Gl.SRC_COLOR`).
- **1.0 (not compiled):** Options → WHAT TO LOAD "Only elements visible in the active view" (off by default; host + ticked links); Options → GEOMETRY "Helper geometry" (Light Source subcategory + keyword subcategories left out, on by default); ground 100 mm below the lowest level; Scan I / Shift+I hide / isolate + pause menu SHOW ALL; category / link / element hiding saved (`visibility.json` / sidecar); F12 screenshot to Pictures\BimGo.

## 2. Order

| Round | Scope |
|---|---|
| 1.0 | Build / test the above; release prep (installer, signing, release notes) |
| Next | Revisit guns (more guns / gun-bar rethink; > 9 guns needs a key rethink) |
| Later | Coordinate readout UI/UX, incremental refresh, flattened save, named pipes, tests (NuGet → ask), push follow-ups, nested links, link levels in PgUp / PgDn |

## 3. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; no LINQ / allocations in per-frame paths (`TextBuffer`, cached strings).
- No NuGet packages without asking; ask before reorganising folders.
- No exceptions to the user: log via `Utilities.Log_Utils.Write`, show a toast/dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Qualify clashing names. Format and protocol stay backward compatible (additive fields; `formatVersion` 1, protocol 1).
- Element ids are per model (`ElementRecord.IsLinked`); visibility is per (link, category) group (`SceneBatches.GroupOf`) plus edit hides (`_hidden`) and walkthrough hides (`_userHidden`).
- Keep `README.md` and an `ai/<date>_V<n>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`.
- Keys in use: WASD/arrows, Space, Shift, Ctrl, V, 1–8, wheel, Q/E (gizmo), N, T, E, R, G, I, Z/X/C (gizmo), H, X, B, L, O, [ ], Tab, F1, F5, F11, F12, PgUp/PgDn, Ctrl+S/Z/Y/1–9. Free letters: F, J, K, M, P, U, Y (unmodified).
- Texture units: 0 = UI atlas, 1 = shadow depth array, 2 = glass transmittance array.
