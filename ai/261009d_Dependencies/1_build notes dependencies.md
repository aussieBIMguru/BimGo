# BimGo dependencies round: build notes (steps 1 and 3; installer deferred)

Follows `0_BimGo Dependencies_Handoff.md` (this folder). **Status: built, all tests pass and the UX checks are fine (Gavin, 2026-10-09). MIT licence confirmed after review.** Originally written in a sandbox with no .NET SDK or NuGet access. The Silk.NET overloads used by the facade were checked line by line against the Silk.NET **v2.23.0** source (the `GL.gen.cs` signatures, `GL.GetApi`, `LamdaNativeContext`), and the expected sun values against an independent algorithm; Gavin then built and tested it.

## Decisions (Gavin, this round)

| Topic | Decision |
|---|---|
| Scope | Step 1 (Core tests) + step 3 (Silk.NET GL bindings) now. **Hold point** after both, then a handoff. Step 2 (installer + Revit bundle) next round. |
| Licence | **MIT now** (© Aussie BIM Guru), replacing the Unlicense. |
| Test folder | `tests/BimGo.Core.Tests` at the repo root, in a `tests` solution folder. |
| GL constants | Keep the public `uint` constants; cast to Silk's `GLEnum` inside the wrappers (zero call-site changes). |
| Versions | `Silk.NET.OpenGL` **2.23.0** (latest 2.x, Jan 2026); `MSTest.Sdk` **4.4.1** (latest stable, Sep 2026). |

## App: `Native/Gl.cs`

- Public surface unchanged (verified by diffing every `public static` signature and every `public const` against 1.0).
- The ~75 `delegate* unmanaged` fields and the `Load` table are gone. `Load()` now checks every entry point in `RequiredEntryPoints` through `GetProc` (same "OpenGL function 'x' is not available. Update the graphics driver." message at startup), then creates `SilkGL.GetApi(LoadProc)`.
- `LoadProc` = `GetProc(name, required: false)`: the same `wglGetProcAddress` → opengl32 export fallback as before. Silk.NET resolves each entry point lazily, once, and caches it in its generated vtable. A call is a vtable field read + function-pointer call: no delegates, closures or allocations per call.
- Signature differences absorbed in the wrappers: sizes / counts are `uint` in Silk (`Viewport`, `Scissor`, `TexImage2D/3D`, `ReadPixels`, `DrawElements`, `DrawArrays`, `DrawElementsInstanced`, `RenderbufferStorageMultisample`, `VertexAttribPointer` stride), `BufferData` / `BufferSubData` size is `nuint`, `MultiDrawElements` counts are `uint*`, `GetError` / `CheckFramebufferStatus` return `GLEnum` (cast back to `uint`), `DepthMask` / `ColorMask` / `VertexAttribPointer` take `bool`. Overloads were chosen so every argument is an exact match (`GLEnum`, `int` internal format, `void*`, raw pointers), so no group-enum overload can be picked by accident.
- `GetProc` stays public: `Wgl` still uses it for `wglCreateContextAttribsARB` / `wglSwapIntervalEXT`. `Native/Wgl.cs` untouched; `Silk.NET.WGL` / `Windowing` / `Input` not used.
- Adding a GL call from now on: one wrapper in `Gl.cs` forwarding to `_gl`, plus its GL name in `RequiredEntryPoints` (or check it with `GetProc(name, false)` if optional).

## App: project

- `BimGo.App.csproj`: `PackageReference Silk.NET.OpenGL 2.23.0` (pinned). Transitive: Silk.NET.Core, Silk.NET.Maths, Microsoft.DotNet.PlatformAbstractions, Microsoft.Extensions.DependencyModel 9.x and small System.* packages (all MIT). `LICENSE` → `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` copied beside `BimGo.exe`.
- BimGo.Revit does not reference BimGo.App, so nothing new reaches Revit's process.

## Core

- `Scene/LaunchSettings.cs` **bug fix found by the tests**: `SetLinksFor` capped the per-model link choices at 200 with `LinkedModels.Remove(LinkedModels.Keys.First())`. `Dictionary` refills freed slots, so after the first eviction `Keys.First()` was the entry just added: once full, every new model's choice was thrown away immediately. Now the dictionary is rebuilt in recency order (re-setting a model makes it the newest) and the oldest are dropped. No other Core change.

## Tests: `tests/BimGo.Core.Tests` (MSTest.Sdk 4.4.1, net8.0, Core only)

| File | Covers |
|---|---|
| `BimGoRoundTripTests` | manifest (writer, kind, counts, created time kept), geometry bytes, elements (ranges, phases, links, move reasons), model (levels, rooms, links at double precision, site incl. shared transform and location, phases, spawn), parameters, journal, bookmarks with home + thumbnail + sun time, sun, visibility, comments (blank dropped), optional entries only when non-empty, atomic replace, cancelled save leaves the old file, cancelled read, uncompressed snapshot |
| `BimGoReaderCompatibilityTests` | hand-made early-layout file (no links / phases / bookmarks / sun / visibility / location / journal / comments) reads with defaults, default move reason, unknown category → generic, Sydney fallback; newer format and newer geometry layout refused with "newer BimGo", foreign format, missing geometry / model, index out of range, bad element ranges clamped, missing / garbage files |
| `EditJournalTests` | numbering, renumber on load, undo / redo identity, new edit clears redo, clone keys reserved by undone clones, `MarkApplied`, pending order, `JournalPush.BuildRequest` (pending entries + known clones) |
| `SolarPositionTests` | Sydney 21 Dec 12:00, London 21 Jun 12:00, Adelaide 20 Mar 15:00 and 15 Jan 15:00 DST against the Astronomical Almanac algorithm (tolerances 0.2–0.4° altitude, 0.5–0.6° azimuth); DST = one hour earlier; clamping; `Direction`, `ToModel`, `LocationOf`; `SunTime.StartFor`; `SunSettings.Clean` |
| `LaunchSettingsTests` | clamps, MSAA snapping, null / enum repair, extra parameters, helper keywords, link choices (per model, empty removes, bounded, recency) and `NearestStep` |
| `OperationProgressTests` | stage mapping, clamping, count steps, reversed / wide ranges, cancellation and `CanCancel` |
| `LiveProtocolTests` | `FolderChannel` pair in a temp folder: payload round-trip, reply-to, send order, wrong session (deleted), newer protocol, 4 MB cap (deleted), unreadable / untyped, duplicate ids; `Envelope.Read` failures; `journal.apply` payload; `JournalResultPayload.Count`; session id validation; `SessionInfo.IsAlive` |
| `SidecarTests` | sidecar naming, comments round-trip + RvtGo migration, bookmarks (non-finite dropped, default names), sun, visibility, missing / damaged |

- Logging goes to `BimGo.Tests.log` (`[AssemblyInitialize]`), so tests never reset `BimGo.App.log`. Files only in `%TEMP%\BimGo.Tests\<guid>`; `LoadOrDefault` / `Save` and real session folders are never touched. `[assembly: DoNotParallelize]`.
- Not covered: journal replay onto the scene (in BimGo.App `GameSession`, not Core).

## Repo

- `LICENSE`: MIT. `THIRD-PARTY-NOTICES.txt` (new). `src/BimGo.sln`: `tests` folder + BimGo.Core.Tests (Debug / Release under every R25–R27 configuration).
- README: intro line, *For AI assistants* item 3 (policy) + 9 (tests), §2 restore / tests steps, §4 tree, §8 to-verify items, §9 changelog, new §10 dependency table.

## Hold point: what to check before the installer round

1. **Restore and build** every configuration (`Debug R25/R26/R27`, `Release …`). Expected compile risks, if any: a Silk.NET overload I matched wrongly in `Gl.cs`, or an MSTest 4 analyzer warning.
2. **Run all tests** (Test Explorer). Send me any failures with the message.
3. **Output folder:** note which dlls appear beside `BimGo.exe` (Silk.NET.*, Microsoft.Extensions.DependencyModel, maybe `System.Text.Json.dll` 9.x; see README §8).
4. **UX parity (handoff §6), rendering first:** scene, shadows at Low / Medium / High (depth array, transmittance array, `glTexImage3D`, `glFramebufferTextureLayer`), glass tint, MSAA on / off (blit), minimap (scissor), UI text (atlas), highlights, transparency, F12 screenshot and bookmark thumbnails (`glReadPixels`), overlay lines, progress screens, help panel.
5. **Startup errors:** a 3.3-only driver (or a VM) still starts; a missing function still shows the readable message.
6. **Frame times** unchanged (FPS counter on the same view / model before and after).
7. Window, input, audio, Revit and files are not touched by this round, but a quick smoke run of Go / Export / open / save / push is worthwhile.

## Next (installer round, handoff §3 step 2 and §5)

Inno Setup per-user setup.exe, `BimGo.bundle` (`PackageContents.xml`, `Contents\2025|2026|2027`), self-contained win-x64 publish (trimming off), `--register --quiet` / `--unregister --quiet`, legacy `Addins\<year>` cleanup, Revit / BimGo running check, Debug-only developer copy targets, publisher "Aussie BIM Guru" (csproj `Company`, `.addin` VendorDescription, Inno `AppPublisher`), notices page.
