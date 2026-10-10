# BimGo — Handoff Brief v7 (for a new chat)

**Purpose of the next chat:** build and test v7 (linked models), fix what the compiler / Revit finds, then move on to **revisiting the guns** (more guns / gun-bar rethink, undecided).

**Read first:**
1. This brief.
2. `README.md` (controls, `.bimgo` format, live protocol, known limitations, changelog).
3. `ai/261009b_V7/1_build notes v7.md` (decisions, files touched, "To verify", test checklist).
4. **Ask Gavin for a fresh zip of his working copy before editing** (he builds in Visual Studio; his copy is the source of truth).

---

## 1. Where BimGo is now

- Solution `src/BimGo.sln`: **BimGo.Core** (net8.0), **BimGo.App** (net8.0-windows, `BimGo.exe`), **BimGo.Revit** (R25/R26 net8, R27 net10).
- v6 (sun, shadows, time of day) built and works well (Gavin, 2026-10-05).
- **v7 (not compiled):** linked models. Options → LINKED MODELS lists link instances (grouped by file, none ticked by default, choice remembered per host model). Ticked loaded instances are extracted with the host's categories through their total transform, in the link phase named like the host's; read-only in the app (Scan / Measure / Comment / Teleport / Portal; Demolish / Gizmo / Clone refuse); per-link toggles in the pause menu; Scan → R selects inside the link (`Selection.SetReferences`). Format additive: `model.links[]`, `elements[].link`, `rooms[].link`.

## 2. Agreed order (Gavin, 2026-10-05)

| Round | Scope |
|---|---|
| v6 | Sun + shadows + time of day (built, works) |
| v7 | Linked models (done, to build / test) |
| **Next** | **Revisit guns** (more guns / gun-bar rethink undecided; > 9 guns needs a key rethink) |
| Later | Coordinate readout UI/UX revisit, incremental refresh, flattened save, named pipes, tests (NuGet → ask), push follow-ups, signing / installer, nested links |

## 3. Likely v7 follow-ups

- Revit API names to confirm (see build notes): link reference selection and `UIView.ZoomAndCenterRectangle`.
- Performance with large links (extraction time is logged per link); maybe a per-link triangle limit or category subset later.
- Nested links (currently not extracted), link levels in PgUp / PgDn, link visibility saved with the file.

## 4. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; no LINQ / allocations in per-frame paths (`TextBuffer`, cached strings).
- No NuGet packages without asking; ask before reorganising folders.
- No exceptions to the user: log via `Utilities.Log_Utils.Write`, show a toast/dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Qualify clashing names. Format and protocol stay backward compatible (additive fields; `formatVersion` 1, protocol 1).
- Revit API: `ElementOnPhaseStatus` has no `NotApplicable`.
- Element ids are per model: look elements up by id only for host elements (`ElementRecord.IsLinked`), and never send a linked element's id to Revit as a host id.
- Visibility is per (link, category) group: `SceneBatches.GroupOf(record)` indexes the session's `_groupVisible`.
- Keep `README.md` and an `ai/<date>_V<n>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`.
- Font atlas: Latin-1 plus `EXTRA` in `UiFont.cs`.
- Keys in use: WASD/arrows, Space, Shift, Ctrl, V, 1–8, wheel, Q/E (gizmo), N, T, E, R, G, Z/X/C (gizmo), H, X, B, L, O, [ ], Tab, F1, F5, F11, PgUp/PgDn, Ctrl+S/Z/Y/1–9. Free letters: F, I, J, K, M, P, U, Y (unmodified).
- Texture units: 0 = UI atlas, 1 = shadow depth array, 2 = glass transmittance array.
