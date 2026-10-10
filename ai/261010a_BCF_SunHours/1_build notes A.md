# BCF + sun hours round 2: build A notes

**Baseline:** `BimGo_Comments_SunHours_Final.zip`. **Not compiled here** (no .NET SDK, NuGet or Revit API in Claude's
environment). Changed files were reviewed by eye, checked for balanced brackets, and every `GameSession` partial was
scanned for duplicate member names (none; last round's `MONTHS` lesson). No shader changed. Please build and run the
Core tests (new `BcfTests`: 18 tests, `SunStudyTests`: 7 tests).

## 1. BCF (Core)
- **`Format/IfcGuid.cs`:** encode / decode / validate IFC's 22-character GUIDs (text order, first character 2 bits).
- **`Format/BcfModels.cs`:** `BcfTopic`, `BcfComment`, `BcfViewpoint`, `BcfComponent`, `BcfVector` (double),
  `BcfProject`, `BcfReadResult`.
- **`Format/BcfFile.cs`:** `Write` (plain BCF 2.1: `bcf.version`, `project.bcfp`, per topic `markup.bcf`,
  `viewpoint.bcfv`, `snapshot.jpg`; 2.1 element order; temp file then replace) and `Read` (2.0 / 2.1 / 3.0:
  namespace-agnostic; comments under Markup or Topic/Comments; viewpoints `Viewpoints` or `ViewPoint`; perspective or
  orthogonal cameras; selection components; snapshot PNG / JPEG; DTDs refused; 8 MB XML / 32 MB image caps; a damaged
  topic is skipped and counted).
- **`Format/BcfMapping.cs`:** `BcfCoordinates` (Shared / Project / Internal) and `BcfFrame` (internal metres ↔ BCF,
  points and directions, double precision; falls back to internal when the site lacks the wanted base);
  `BcfMapping`: status / priority both ways (generous on import), `TitleOf`, `ToTopic`, `ToViewpoint` (eye = feet +
  eye height; vertical FOV of a 16:9 picture from the horizontal FOV, clamped 45–60° for BCF 2.1), `TryToView`,
  `ToComment` (text = description, title in front when different; else the first comment; else the title),
  `Merge` (status / priority / assignee; new replies by GUID or same author + text + time; the description repeated as
  a comment is skipped), `DeterministicGuid` (MD5) for project ids and non-GUID comment ids.

## 2. Comment pictures and element references
- `CommentRecord`: `ElementUniqueId`, `Snapshot` (name `comments/<id>.jpg`), in-memory `SnapshotData` /
  `SnapshotDirty`. `CommentSnapshots` (in `CommentFiles.cs`): safe names, load / write beside a **model folder's**
  `comments.json` (`comments\` sub-folder; unused pictures removed; nothing written beside a Revit model).
- `.bimgo`: optional `comments/*.jpg` entries (writer / reader); `elements.json` optional `ifcGuid`
  (`ElementRecord.IfcGuid`). Format stays version 1.
- App: the comment capture now also encodes a ≤ 1280 px JPEG (`EncodeJpeg`, generalised from `EncodeThumbnail`) and
  stores both with `CommentStore.SetPictures` (one save). New comments record the element's UniqueId.
- Revit: `SceneExtractor.IfcGuidOf` (stored `IfcGUID` parameter if valid, else `ExportUtils.GetExportId` compressed).

## 3. BCF (App, `GameSession.Bcf.cs`)
- COMMENTS panel bottom row: EXPORT CSV… · **EXPORT BCF…** (shown) · **BCF (ALL)…** · **IMPORT BCF…** ·
  **BCF: SHARED / PROJECT / INTERNAL** (cycles; saved as `BcfCoordinates`) · CLOSE.
- Export: topic per comment; viewpoint from the saved view (older comments: `ApproachMarker`, factored out of
  `TeleportToComment`); component from the element (UniqueId, else ElementId; IFC GUID when present); snapshot =
  picture, else thumbnail. Project id = deterministic GUID of the model key.
- Import: merge by GUID or add: `PlaceImported` (view, walking / flying from the floor under the feet, marker from the
  centre ray / element / 2 m ahead, element, level), `ImportPictures` (thumbnail 192 × 108 + picture ≤ 1280 px,
  re-encoded as JPEG, PNG snapshots too). `CommentStore.ApplyImport` saves once.

## 4. Sun hours round 2
- **Wall-centre fix (`SunHoursStudy.SideOf`):** wall faces from a room count between 0.4 m inside and 3 cm outside the
  boundary (signed depth), tested on the side whose 0.45 m probe goes deeper into the room. Rooms bounded at finishes
  and at centres both get their wall cells; a partition's far face stays out (unless the wall is under 60 mm thick
  and the room is centre-bounded).
- **Targets (Core `SunHours`):** `SunTarget` Off / Adg2 / Adg3 / Custom, `TargetHours`, `ColourPassFail`;
  `ApplyTarget` (ADG = 21 June, 9:00–15:00, no DST, 2 / 3 h), `MatchesTarget` (an edited ADG day or time turns Custom),
  `Passes`, `PASS` / `FAIL` colours. Panel rows: Target (segmented) and, with a target, Pass at −/+ (custom only) and a
  PASS / FAIL ↔ HOURS colour toggle. Overlay and legend recolour without re-running; summary and CSV add the pass
  share / Pass column. The panel grew to 820 px max.
- **Saved studies (Core `SunStudyFiles`, `SunStudyDocument`, `SunStudyFace`; `ModelFolders.KeySourceFor`):** SAVE
  STUDY… (text box, suggested "room day times"; same name replaces) → `sun-studies\<name>.json` in the model folder
  (live: the session's; files: from the provenance). SAVED STUDIES… → list with LOAD / DELETE (twice) / BACK. LOAD
  rebuilds faces (element by UniqueId / ElementId, triangles in the saved plane, room by number|name|link|floor
  height), lays the grid and shows the saved hours when every cell matches within 2 cm; otherwise asks for a RUN.
- The text box takes the clicks while open (unpaused too), so panels behind it don't react.

## 5. To verify
See README §8 (BCF / sun hours round 2). The Revit APIs to check: `ExportUtils.GetExportId(Document, ElementId)`,
`BuiltInParameter.IFC_GUID`.

## 6. Hold point → build B (daylight)
After Gavin confirms build A: daylight factor (CIE overcast sky component by weighted rays through glazing, externally
reflected one bounce, internally reflected by the BRE split-flux formula per room; reflectances from material colours
with overrides) and a lux study (sun + clear sky, single day over a time range, pass / fail), reusing the study grid,
time slicing, legend, CSV and saved studies.

## 7. Fix 1 (Gavin's test of build A)
- Gavin: sun study save / load good; BCF export / import round-trips in BimGo, but common BCF viewers wouldn't open
  the `.bcfzip`.
- Export now writes **`.bcf`** (the BCF 2.x extension; `.bcfzip` was older tools' name, still read), **PNG
  snapshots named `snapshot.png`** (the name and format most viewers expect; the app converts the JPEG picture), and
  declares the `xsi` / `xsd` namespaces on each XML root like buildingSMART's sample files.
- COMMENTS panel buttons are now EXPORT BCF… (the comments shown; filters on All / all levels = every comment),
  IMPORT BCF…, BCF COORDS: SHARED / PROJECT / INTERNAL, CLOSE. The comment CSV export is removed (`ExportComments`,
  `CommentStore.ExportCsv`).
- If a viewer still refuses the file, send its error message (or the file): next suspects are its expectations of a
  `Header` / IFC file reference or of `Index` elements.
- Checked against buildingSMART's BCF 2.1 test cases (Gavin's link, `Test Cases/v2.1/Project/ExtensionSchema` and
  `Visualization/PerspectiveCamera`): the layout matched except **project.bcfp**, which had an empty
  `<ExtensionSchema/>`. Export now writes **`extensions.xsd`** (a redefine of markup.xsd listing TopicType Issue,
  TopicStatus Open / In Progress / Closed, Priority Low / Normal / High, plus any other value a topic carries) and
  references it, as the samples do. That is the most likely reason viewers refused the files.
