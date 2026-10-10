# BCF + sun hours round 2: build B notes (daylight)

**Baseline:** build A + Fix 1 (Gavin: BCF works in viewers, saved studies good). **Not compiled here.** Brackets
checked, `GameSession` partials scanned for duplicate members (none). Please build and run the Core tests (new
`DaylightTests`: 9 tests; `SunStudyTests` updated).

## Decisions (Gavin, 2026-10-10)
- Daylight lives as **modes in the J panel**: Sun hours / Daylight % / Lux (shared surfaces, grid, CSV, screenshot,
  save / load).
- Daylight modes test the room's floor at a **0.7 m work plane** (editable).
- Lux: **CIE clear sky + sun**, sun switchable. Pass / fail: **lux for a share of time** (300 lux, 50 %).
- **No regional terminology:** the ADG presets are gone. One **PASS / FAIL** toggle, then the mode's value (2 h,
  2 %, 300 lux for 50 %). Older saved studies with a preset read as pass / fail on.

## Core
- `Scene/Daylight.cs`: `StudyMode`; 192 sky patches (`PatchOf`, `PatchCentre`, `PatchSolidAngle`,
  `HorizontalIlluminance`); CIE overcast (scaled to a unit horizontal illuminance) and CIE clear sky (scaled to the
  IES clear-sky diffuse horizontal illuminance); IES direct normal; Hammersley cosine-weighted rays; BRE split-flux
  IRC and floor bounce; reflectance from colour; legend colours for % and lux.
- `SunHoursSettings`: `Mode`, `WorkPlane`, `Rays`, `DirectSun`, `StandardReflectance`, `FactorTarget`, `LuxTarget`,
  `LuxShare`, `PassFail` (from `Target`, now Off / On; 1 and 2 read as On), `HorizontalOffset`. Removed
  `ColourPassFail`, `ApplyTarget`, `MatchesTarget`.
- `SunStudyDocument.Shares` (illuminance, optional).

## App
- `SunHoursStudy`: `RayOutcome`, `RayClassifier`, `RoomLight`, `DaylightInputs`; `StartDaylight`, `StepDaylight`
  (rays binned into patches once per cell), `StepLuxCell` (per sample: sky patches, ground, outside, sky IRC, sun ray),
  `FinishLux` (sun bounce per room, averages and shares); `Passes(i, settings)`, `PassShare(settings)`; `SetResults`
  takes shares. The grid uses the work plane in daylight modes.
- `GameSession.Daylight.cs`: `RunDaylightStudy`, `ClassifyDaylightRay` (opaque hit in the room → internal, else
  external; nothing → sky / ground; see-through hits before → × 0.7), `ComputeRoomLight` (area from the boundary,
  glazing from see-through triangles, reflectances from colours or standard).
- `GameSession.SunHours.cs`: mode selector, per-mode rows, PASS / FAIL toggle + steppers, per-mode summary, legend
  (0–7 h, 0–5 %, 0–2000 lux), overlay colours, CSV (settings, room figures, value columns), save / load.

## Known limits
See README §8 (Daylight). Results are labelled early design indicators.

## Fix 1
- Gavin: `LiveProtocolTests.SendAndReceive_RoundTripsEnvelopeAndPayload` failed (one message file left after the
  scan). Not from this round: a timing race where Windows (anti-virus / indexer) briefly holds a just-created file so
  the channel's delete fails; the next scan deletes it (the id check already prevents double handling).
  `FolderChannel.TryDelete` now retries up to 4 × 15 ms on IO / access errors, and the test scans for up to 2 s for the
  inbox to empty instead of checking at once.
