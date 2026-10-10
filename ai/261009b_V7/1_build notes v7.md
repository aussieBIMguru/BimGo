# BimGo v7 build notes: linked models

Follows `ai/261009a_V6/0_BimGo v6_Handoff.md`. Gavin reported v6 (sun, shadows, time of day) built and working well (2026-10-05) and supplied his working copy (`BimGo_v6.zip`), which this round starts from. Written without a .NET SDK or the Revit API: **not compiled yet**.

## Decisions (Gavin, 2026-10-05)

| Topic | Decision |
|---|---|
| Default | **No links** extracted unless the user ticks them at Go / Export (Options dialog) |
| Remember | The choice is **remembered per host model** (first launch: none) and reused by F5 / Send a fresh snapshot |
| Granularity | **Per link instance**, grouped by link file (a file tick box sets all its instances) |
| Editing | **Read-only**: Scan, Measure, Comment, Teleport, Portal work; Demolish / Gizmo / Clone refuse; nothing from links enters the journal or push |
| Filter | **Same categories as the host**; the link's phase with the same name as the host's new phase (else its last); existing phase likewise. Link rooms feed the readout where the host has no room |

## Core

- `Scene/LinkInfo.cs` (new): one extracted link instance (index, instance name, title, instance id / UniqueId, model key / path, total transform as double origin + bases, phases used, element / room counts; `Label` for the HUD). Also the `model.links[]` DTO.
- `Scene/SceneData.cs`: `ElementRecord.Link` (0 = host, n = `Links[n-1]`) + `IsLinked`; `RoomInfo.Link`; `SceneData.Links`, `LinkOf(record)`.
- `Format/FileModels.cs`, `BimGoReader.cs`, `BimGoWriter.cs`: `model.links`, `elements[].link`, `rooms[].link` (nullable, omitted for the host), `counts.links`. The reader renumbers links 1..n and maps out-of-range numbers to the host.
- `Scene/LaunchSettings.cs`: `LinkedModels` (host model key → ticked instance UniqueIds), `LinksFor`, `SetLinksFor` (bounded to 200 models), sanitised.
- `Sources/FileEditSource.cs`: linked elements are left out of the hosted-element map.
- `Live/LiveProtocol.cs`: `SelectPayload.Linked` (`LinkedElementRef[]`, additive). `LiveSessionSource.ShowLinkedElement` (+ `ILiveLink`).

## Revit

- `Extraction/LinkResolver.cs` (new): `HostKey(doc)` (`ProjectInformation.UniqueId`, else `title:`), `Candidates(doc)` (top-level `RevitLinkInstance`s: id, UniqueId, name, `RevitLinkType` name as file, loaded doc, total transform), `Selected(doc, settings)` (ticked + loaded; logs ticked ones that are unloaded or gone).
- `Extraction/SceneExtractor.cs`: a private `SourceModel` (doc, transform, link number, phases, `LinkInfo`, read-only reason, per-document material / category / level caches) replaces the single-document caches. `Run` gathers host then link elements per enabled category (each guarded), computes the origin over all of them (link bbox centres through the transform), collects host rooms then each link's (transformed, tagged), extracts host elements first, then each link's. Linked elements: geometry walked with the link transform (proxies too), `HostId` 0, `MoveBlockReason` "In linked model “title” (read-only)", phase role against the link's phases, `Link` = n. Logged per link.
- `Forms/OptionsWindow`: new LINKED MODELS card (rows per instance grouped by file, three-state file box when a file has several instances, unloaded rows disabled with a tooltip, "This model has no Revit links."); the footer estimate adds "+ n linked models"; Launch saves `SetLinksFor(hostKey, ticked)`. New `LinkChoice` / `LinkChoices` types; `CommandSteps.ShowOptions` fills them.
- `Live/LiveDispatcher.cs`: `select.elements` with `linked[]` → `SelectLinked`: `new Reference(element).CreateLinkReference(instance)`, `Selection.SetReferences`, zoom the active `UIView` to the element's box through the link transform; falls back to selecting the link instance.
- `General/GlobalUsings.cs`: `LinkInfo` alias (in case the Revit API has a clashing name).

## App

- `Rendering/SceneBatches.cs`: batches per (link, category) "visibility group" (`GroupOf(record)` = link × categories + category, `GroupCount(scene)`); elements are bucketed once per pass instead of scanned per category. `RenderBatch.Group`.
- `Rendering/SceneRenderer.cs`, `Physics/DynamicSet.cs`: take the group visibility array instead of the category array.
- `Game/GameSession.cs`: `_linkVisible` + `_groupVisible` (filled in place by `UpdateGroupVisibility`, called from `RefreshMasks`); id / UniqueId / host maps hold host elements only; the load toast counts links.
- `Game/GameSession.Menu.cs`: `BuildLinkCard` under the category cards (toggle per link, element count, "+n more" when the screen is short).
- `Game/GameSession.Edits.cs`: room readout prefers host rooms; group visibility in `SetStaticHidden`.
- Guns: Hammer refuses linked elements (toast + panel line, no hover highlight); Scan panel **Model** row and plain "Existing" phase text for links; Comment gun records no element id for linked elements; Show in Revit (R) sends `ShowLinkedElement`.

## To verify when building

- Revit API: `RevitLinkInstance.GetLinkDocument()`, `GetTotalTransform()`, `RevitLinkType.Name`; `Reference(Element)` + `Reference.CreateLinkReference(RevitLinkInstance)`; `Selection.SetReferences(IList<Reference>)` (2023+); `UIDocument.GetOpenUIViews()` / `UIView.ZoomAndCenterRectangle`; `XYZ * double`.
- C#: the tuple element names in `ComputeOrigin(IEnumerable<(Element Element, Transform Transform)>)` vs the `Select(e => (e, w.Source.Transform))` call; the `select?.Linked is { Length: > 0 } linked` pattern; WPF lambda `(_, _) =>` handlers in the Options dialog.
- Link transform: Revit link instances only rotate in plan, so room heights use the transform origin's Z. A link moved vertically or rotated should line up exactly with Revit's 3D view.

## Test checklist

- Options: no links → "This model has no Revit links."; links listed by file, none ticked on first launch; unloaded link greyed with tooltip; a file with two instances shows a file box (indeterminate when one is ticked); estimate shows "+ n linked models"; Launch / Export then reopen Options → the same ticks; another model → its own (none).
- Extraction log: "Link n “…”: x elements, y rooms, phases A → B"; total "… n links".
- Walkthrough: linked geometry lines up with the host (rotated / shared-coordinate links too); material colours from the link's materials; Scan shows Model "Link · title", the link's level and element id; Demolish refuses with "In linked model “…” (read-only): edit it in its own model."; Gizmo / Clone show the same reason; Measure, Comment, Teleport, Portal and collision / stairs work on link geometry.
- Pause menu: LINKED MODELS toggles hide a link (render, minimap, picking, collision, shadows); the category toggles still work per category across host and links.
- R on a linked element (live): Revit selects that element inside the link and zooms to it; with an older add-in the link instance is selected.
- Room readout: host room names win; a host without rooms shows the architectural link's rooms.
- Save / reopen a .bimgo with links: links, toggles and Scan rows come back; an older BimGo opens the file (links appear as ordinary, non-movable elements).
- Undo / redo / push with links present: only host edits are recorded / pushed; hosted-insert cascades never touch linked elements even when ids coincide.
- F5 in a live session re-extracts the same links; untick all → next Go has none.
