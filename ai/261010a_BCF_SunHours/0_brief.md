# BCF + sun hours round 2: brief (agreed with Gavin, 2026-10-10)

**Baseline:** `BimGo_Comments_SunHours_Final.zip` (Gavin's working copy after `261009m`, confirmed working).

## Round plan
1. **This round:** BCF export / import (plain), sun hours additions, the wall-centre fix. **Build A**, then a hold point,
   then **build B**: daylight study.
2. Next round: section box / clipping planes.
3. Round after: photo mode (high-res screenshots, 360° panoramas).
4. Then hand over to a new chat for the key-binding review, final polish and the installer.
   (Minimap stays as it is: no room names, no click-to-teleport.)

## Decisions
**BCF**
- Plain BCF 2.1 out, tolerant in (2.0 / 2.1 / 3.0): recreate comments as cleanly as possible from any platform.
- Export the comments the panel shows (filters applied), plus an "all" option.
- Re-import merges by GUID: status, priority and assignee take the file's values, only new replies are added; unknown
  topics become new comments; nothing local is deleted.
- Elements: extract IFC GUIDs (optional field), write IFC GUID + Revit ElementId, match either on import. Older
  `.bimgo` files need a re-export for IFC GUIDs.
- Camera coordinates: shared by default, project / internal switchable (remembered).
- Snapshots: new comments keep a larger picture (~1280 px) for BCF; older comments use their thumbnail.

**Sun hours**
- Single-day studies only (no date ranges); pass / fail criteria: ADG preset (21 June, 9–3, ≥ 2 h; 3 h option outside
  Sydney) + custom target; green / red cells and the passing share in the legend.
- Saved studies in BimGo's model folder (named; load / delete), not inside the `.bimgo`.
- Wall faces within half a wall's thickness of the room boundary count (rooms bounded at wall centres).

**Daylight (build B)**
- Both modes: daylight factor % (CIE overcast sky, no date / time; 2 % pass line) and a lux study (sun + clear sky,
  single day over a time range, pass / fail e.g. ≥ 300 lux for a share of the samples), following the sun hours
  study's principle (single day, time-range sampling).
- Reflectances from Revit material colours, with overrides (ceiling 0.7, walls 0.5, floor 0.2 defaults).
- Labelled as an early design indicator, not compliance-grade (Radiance / ClimateStudio are).
