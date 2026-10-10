# BimGo — Handoff Brief v6 (for a new chat)

**Purpose of the next chat:** build and test v6 (sun, shadows, time of day), tune its look and performance from Gavin's feedback, then move to **linked models**.

**Read first:**
1. This brief.
2. `README.md` (controls, `.bimgo` format, live protocol, known limitations, changelog).
3. `ai/261009a_V6/1_build notes v6.md` (decisions, files touched, "To verify", test checklist) and the project doc `claude/BimGo_v6_Sun_Spec.md`.
4. **Ask Gavin for a fresh zip of his working copy before editing** (he builds in Visual Studio; his copy is the source of truth).

---

## 1. Where BimGo is now

- Solution `src/BimGo.sln`: **BimGo.Core** (net8.0), **BimGo.App** (net8.0-windows, `BimGo.exe`), **BimGo.Revit** (R25/R26 net8, R27 net10).
- v5.1 (redo, bookmarks, coordinate readout, v5 push / phases) built and works (coordinate readout UI/UX may be revisited later).
- **v6 (not compiled):** O toggles shadows (off by default); Shift+O / bottom-right sun icon opens the sun panel (time slider + play, month / day boxes, DST, quality, sunlight / sky / shadow / glass intensities). Cascaded shadow maps with a glass transmittance layer; solar maths in Core (Gavin's `SunPosition` + NOAA); Revit captures SiteLocation and the view's sun-study start; state saved with the model; bookmarks keep sun time; quality per machine (also in the Options dialog).

## 2. Agreed order (Gavin, 2026-10-05)

| Round | Scope |
|---|---|
| v6 | Sun + shadows + time of day (done, to build / tune) |
| **Next** | **Linked models** (each link a sub-model: own transform and id namespace; format + picking changes) |
| Then | Revisit guns (more guns / gun-bar rethink undecided) |
| Later | Coordinate readout UI/UX revisit, incremental refresh, flattened save, named pipes, tests (NuGet → ask), push follow-ups, signing / installer |

## 3. Likely v6 tuning topics

- Bias (polygon offset 1.5 / 3, normal offset 1.5 texels, compare bias 0.0002) against acne / peter-panning.
- Cascade split λ (0.8), distances per preset, cascade blending if seams show.
- Lighting balance (`SunLighting.Create`: direct 0.62, ambient 0.12–0.50, twilight colours) vs the classic look.
- Performance on big models while walking (far cascades refresh every 2nd–4th frame; could add caster LOD or skip small elements in far cascades).
- `SunAndShadowSettings.StartDateAndTime` time zone handling.

## 4. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; no LINQ / allocations in per-frame paths (`TextBuffer`, cached strings).
- No NuGet packages without asking; ask before reorganising folders.
- No exceptions to the user: log via `Utilities.Log_Utils.Write`, show a toast/dialog.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Qualify clashing names. Format and protocol stay backward compatible (additive fields; `formatVersion` 1, protocol 1).
- Revit API: `ElementOnPhaseStatus` has no `NotApplicable`.
- Keep `README.md` and an `ai/<date>_V<n>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`.
- Font atlas: Latin-1 plus `EXTRA` in `UiFont.cs`.
- Keys in use: WASD/arrows, Space, Shift, Ctrl, V, 1–8, wheel, Q/E (gizmo), N, T, E, R, G, Z/X/C (gizmo), H, X, B, L, O, [ ], Tab, F1, F5, F11, PgUp/PgDn, Ctrl+S/Z/Y/1–9. Free letters: F, I, J, K, M, P, U, Y (unmodified).
- Texture units: 0 = UI atlas, 1 = shadow depth array, 2 = glass transmittance array.
