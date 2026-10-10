# BimGo — Build notes: Gizmo / Clone move and rotate modes

**Status:** written, not compiled. C# only (no shader or format changes).

## Asked for (Gavin, 2026-10-09)

- LMB locks on in **move** mode (and cloning starts in move). WASD as before; **E / Q = Z up / down**; Z / X remain snap increment controls.
- A second, ergonomic key cycles to **rotate** mode: rotation on the XY plane only (Revit families want to stay level). Z / X become the angle increment controls.
- RMB commits.

## Choices to confirm

- **R** switches modes (next to WASD; free while the gizmo holds the keys: the Scan gun's R only acts when Scan is selected).
- Rotate keys are **A / D** (CCW / CW), read from "w and d rotate" (confirmed by Gavin, 2026-10-09); W / S do nothing in rotate mode.
- **E = up, Q = down** (the usual fly-camera mapping; confirmed by Gavin, 2026-10-09).
- C / V (old angle-increment keys) removed.

## Files

| File | Change |
|---|---|
| `Game/Guns/GizmoController.cs` | `GizmoMode` (Move / Rotate), `Mode`, `ToggleMode`; Begin resets to Move; Update split by mode, E / Q lift, snapped Z steps and clamp; drawing per mode + vertical arrows; Δ readout with Z |
| `Game/Guns/GizmoGun.cs` | `GizmoPanel.HandleKeys`: R toggles, Z / X step the current mode's increment; panel shows MOVE / ROTATE and per-mode hints |
| `Game/Guns/CloneGun.cs` | doc only (Begin already resets to move) |
| `Game/UiTheme.cs` | `AXIS_Z` (blue) |
| `README.md` | controls table, gun table, changelog |

## Notes

- Vertical moves need no protocol change: `EditRequest.Translation` and journal `Offset` were already 3D, and `RevitEditor` applies them with `MoveElement` / `CopyElement`. Revit may refuse or reinterpret Z for some hosted / level-based families: the existing refusal path reverts the walkthrough with Revit's message. Worth a test on a level-hosted chair and a face-hosted fixture.
- Push staleness check compares location points with the pivot (unchanged; the pivot recorded is the start pivot).
