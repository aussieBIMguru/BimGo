# BimGo — Handoff Brief: allowing dependencies where they simplify things (for a new chat)

**Context:** BimGo is Gavin's personal project, developed on his own PC in his own time. Until now it had a strict **no NuGet packages** rule: OpenGL, WGL, Win32, Raw Input, waveOut, the font atlas, the UI, physics and the file association are all hand-written. This round relaxes that rule in a controlled way.

**Purpose of the next chat:** carry out the agreed dependency changes, one step at a time, each verified against the UX checklist (§6). No new features.

**Read first:**
1. This brief.
2. `README.md` (§1 overview, §4 project structure, §5 how it works, §8 known limitations).
3. `ai/261009e_Installer/0_BimGo Installer_Handoff.md`: the installer round, which overlaps with step 2 here (bundle and packaging).
4. **Ask Gavin for a fresh zip of his working copy before editing.**

---

## 1. The policy (agreed with Gavin, 2026-10-09)

> **Own the things that differentiate BimGo. Rent the things that merely make Windows / OpenGL behave.**

**Keep owning (the engine, i.e. what makes BimGo BimGo):**
- renderer architecture, batching, frustum culling, shadows (cascades + glass), MSAA / render targets, transparency, minimap;
- camera, picking, physics / collision (incl. the stair edge riding);
- scene representation, the gun / tool system, the UI appearance (immediate-mode HUD, menus, sun panel, progress screens);
- the `.bimgo` format, the edit journal, the Revit bridge and live-session protocol.

**Stop owning (commodity plumbing):**
- **OpenGL function bindings**, i.e. `Native/Gl.cs` (~500 lines of unmanaged function pointers). It gives no product advantage, and every new GL call has to be hand-added correctly (the v6 `SRC_COLOR` miss is a small example). A mature binding library makes AI-assisted renderer work safer: "add GL feature X through our renderer", not "invent more unmanaged signatures".
- Possibly **WGL context creation** (`Native/Wgl.cs`, ~90 lines), only if it comes cleanly with the bindings package without touching the window.

**Leave alone until it actually causes pain:**
- the window / input / platform stack: `Platform/GameWindow.cs`, `Native/Win32.cs`, `Platform/InputState.cs` (~1,000 lines).
- It is ugly to own, but it carries a lot of today's UX: per-monitor DPI v2, raw mouse look, F11 borderless, file drop, focus / capture, the single-instance inbox and bring-to-front, and the Revit foreground hand-off. The regression surface is too large for the gain.

**Also unchanged:**
- **Revit API references:** keep the current HintPaths to `C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll` / `RevitAPIUI.dll` (Gavin's choice; no reference packages).
- **Audio** (waveOut synth), **font atlas** (System.Drawing), **physics**: keep custom.
- **UI toolkits** (Dear ImGui, Avalonia, WPF-UI…): rejected, they would change the look.
- **The Revit add-in stays dependency-free at run time.** It loads inside Revit next to every other add-in, so any assembly it ships can clash. Packages go in the app (its own process), in build / dev tooling, or nowhere.

**Rules for any package:** permissive licence (MIT / Apache / BSD / zlib), mature and maintained, version pinned, Gavin's explicit yes, and a line in the README dependency table (package, version, licence, where it's used, why).

## 2. Agreed order

| Step | What | Kind | Notes |
|---|---|---|---|
| 1 | **Tests** for BimGo.Core (xUnit or MSTest) | dev-only | ships nothing |
| 2 | **Installer + Revit add-in bundle** | build / packaging | see the installer brief and §3 |
| 3 | **OpenGL bindings package** under the existing `Gl` facade | app runtime | `Silk.NET.OpenGL` first choice, OpenTK as alternative; see §4 |
| 4 | Windowing / input | none | **leave alone**; revisit only if DPI / driver / input bugs appear |

## 3. Step 1 and 2 details

**Tests (step 1).**
- New `tests/BimGo.Core.Tests` (ask before adding the folder) targeting net8.0, referencing BimGo.Core only.
- Cover:
  - `.bimgo` write → read round-trips (links, visibility, bookmarks with home and thumbnail, sun, journal);
  - older-format files still read;
  - journal replay rules;
  - `SolarPosition` against known values (the Sydney / London / Adelaide checks from v6);
  - `LaunchSettings` sanitising and per-model link choices;
  - `OperationProgress` stage maths and cancellation;
  - `LiveProtocol` envelope validation.
- Runs from Visual Studio Test Explorer.

**Installer + bundle (step 2).** Follow the installer brief, plus:
- **Autodesk add-in bundle:** `BimGo.bundle\PackageContents.xml` + `Contents\2025\`, `\2026\`, `\2027\` (each with `BimGo.addin` + dlls), installed to `%AppData%\Autodesk\ApplicationPlugins\` (per-user) or `%ProgramData%\…` (per-machine).
  - One folder serves every Revit year; uninstall means deleting one folder.
  - It's Autodesk's own format, not a dependency.
  - Verify that each year loads only its component, and that `App_Utils.FindExe` still finds `BimGo.exe`.
- **Packaging tool:** **Inno Setup** (decided, see §5). Per-user, one setup.exe, with the Revit years as optional components.
- **App publish:** self-contained win-x64 (runtime packs come with the SDK) so standalone users need no .NET install. Check size vs a framework-dependent build with a runtime check.
- Make the developer copy targets (`InstallBimGoApp`, `CopyToRevitAddins`) Debug-only (or off for release), so release builds don't overwrite the build machine's installs.

## 4. Step 3: OpenGL bindings behind the `Gl` facade

**Goal:** the renderer and UI code keep calling `Gl.Xxx(...)` exactly as now. Only the inside of `Native/Gl.cs` changes.

1. Add `Silk.NET.OpenGL` (pin the version; it pulls in `Silk.NET.Core`) to **BimGo.App only**.
2. After `Wgl` makes the context current, create the API once:
   ```csharp
   _gl = GL.GetApi(name => Wgl.GetProcAddress(name)); // existing wglGetProcAddress + opengl32 export fallback
   ```
3. Re-implement each `Gl` wrapper as a one-line forward to `_gl` (same names, same signatures, same constants), keeping:
   - the `required` / optional entry-point behaviour (GL 4.1 → 3.3 fallback);
   - the error messages ("Update the graphics driver");
   - `unsafe` overloads used by uploads (`BufferSubData`, `TexImage2D/3D`, `ReadPixels`, `MultiDrawElements`).
4. Delete the hand-written function-pointer fields and the `GetProc` table once every wrapper forwards.
5. Optionally replace the hand-maintained constants with `Silk.NET.OpenGL` enums inside the facade (keep the public `uint` constants until call sites are migrated, if ever).
6. **WGL:** keep `Native/Wgl.cs` (context creation is tied to our window and pixel format). Only consider `Silk.NET.WGL` if it replaces it cleanly with no window change.
7. **Verify:**
   - every renderer path works: scene, shadows (depth array, transmittance array, `glTexImage3D`, `glFramebufferTextureLayer`), MSAA blit, minimap scissor, UI atlas, thumbnails / screenshots (`glReadPixels`), overlay lines;
   - the 3.3 fallback on an older driver;
   - startup errors still show a readable message;
   - frame times are unchanged (forwarding must not allocate: no delegates or closures per call).

**Exit criteria:** `Gl.cs` is a thin facade over Silk.NET, there are no hand-written GL function pointers, renderer code is untouched, and §6 passes.

## 5. Decisions (settled with Gavin, 2026-10-05)

| # | Topic | Decision | Consequences |
|---|---|---|---|
| 1 | Packaging / updates | **Inno Setup, no auto-update.** Velopack is not used. | No NuGet for packaging. Each update is a new `BimGo-Setup-x.y.z.exe`. Velopack can be revisited later if release cadence ever justifies it. |
| 2 | Install scope | **Per-user, no admin.** | App in `%LocalAppData%\Programs\BimGo\` (unchanged, so `FindExe` doesn't change). Revit bundle in `%AppData%\Autodesk\ApplicationPlugins\BimGo.bundle\`. Association in HKCU. Inno: `PrivilegesRequired=lowest`. |
| 3 | Test framework | **MSTest** via `MSTest.Sdk` (pinned). | Dev-only `tests/BimGo.Core.Tests` (net8.0, refs BimGo.Core only). Runs in VS Test Explorer. |
| 4 | GL bindings | **`Silk.NET.OpenGL` 2.x, pinned.** | BimGo.App only. Keep `Native/Wgl.cs`. Do not pull in `Silk.NET.Windowing`/`Input`. |
| 5 | Licence | **MIT** (Claude's recommendation for a fun personal project; Gavin to confirm). Alternative if he'd rather keep the source private: freeware, all rights reserved, with a short EULA. | `LICENSE` in the repo root, shown on Inno's licence page. Add `THIRD-PARTY-NOTICES.txt` (Silk.NET / Silk.NET.Core MIT, .NET runtime MIT since the app is self-contained) and install it beside the exe. |
| 6 | Publisher | **Aussie BIM Guru.** | csproj `<Company>`, Inno `AppPublisher`, `.addin` VendorDescription "Aussie BIM Guru". VendorId: a short stable ID, e.g. `AUSB`, kept the same forever. Keep the AddInId. |
| 7 | Code signing | **None for 1.0.** | Accept the SmartScreen warning and Revit's unverified-publisher prompt ("Always Load"). Script the signtool step, but switch it off. |
| 8 | Association / shortcut owner | **The installer runs `BimGo.exe --register --quiet`** after install and `--unregister --quiet` on uninstall. | The app owns the keys and shortcut, and dev and installed builds share one code path. `EnsureRegistered` stays as a quiet repair and must not create a duplicate shortcut. |
| 9 | One or two installers | **One setup.exe** (assumed from the installer brief; confirm). The app is always installed. Each Revit year (2025/2026/2027) is a ticked component, offered only if that Revit is detected. | Standalone users untick (or never see) the Revit components. |

**Things to watch that come out of these decisions:**
- **Duplicate AddInId.** If a `BimGo.addin` exists in `Addins\<year>\` *and* the bundle is installed, Revit reports a duplicate AddInId. The installer must remove `Addins\<year>\BimGo.addin` + `BimGo\` (and legacy `RvtGo.addin` + `RvtGo\`) for every year.
  - On Gavin's dev PC, either don't run the installer, or point the Debug `CopyToRevitAddins` target into the bundle's `Contents\<year>\` folder so there is only one copy. Decide which when step 2 starts.
- **Locked add-in dll.** Inno should detect a running `Revit.exe` (and `BimGo.exe`) and ask the user to close it. `CloseApplications` / `[Code]` check.
- **PackageContents.xml:** one `ComponentEntry` per year, with `SeriesMin`/`SeriesMax` = `R2025`/`R2025` etc. R27 is the net10 build.
- **Self-contained publish:** check that trimming is off (WinForms). Expect roughly 70 MB on disk and roughly 30 MB compressed in setup.exe.

## 6. UX parity checklist (run after every runtime change)

- **Window:** per-monitor DPI (move between monitors of different scale), F11 borderless, minimise / restore, ">>" icon in title bar and taskbar, file drop, double-click `.bimgo` while running (inbox, bring to front), Go from Revit while running (switch prompt), Revit hand-off foreground.
- **Input:** mouse capture / release (click to look, Esc, sun panel free cursor), raw mouse smoothness, key repeat ([ ], Z X C V), text entry (comments, bookmark names, date boxes).
- **Rendering:** shadows at every quality, glass tint, MSAA toggle, minimap, highlights, transparency order, F12 screenshots and bookmark thumbnails, progress screens, help panel sizing.
- **Audio:** every effect plays, no stutter.
- **Revit:** Options dialog, progress window and Cancel, Go / Export / Live, refresh, push, select in Revit (including linked), on 2025, 2026 and 2027.
- **Files:** open / save / save-as / push for older `.bimgo` files and new ones; sidecars.

## 7. Conventions

- Readable, robust code, XML doc headers, explicit types where clearer; no per-frame allocations.
- **The dependency policy in §1 replaces the old "No dependencies" rule.** Update README "For AI assistants" item 3 to match and add the dependency table. Every package still needs Gavin's explicit yes. Ask before adding folders (`tests/`, `build/`, `installer/`, `artifacts/`).
- No exceptions to the user: log via `Utilities.Log_Utils.Write`, show a dialog / toast.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Format and protocol stay backward compatible (`formatVersion` 1, protocol 1).
- Keep `README.md` and an `ai/<date>_<topic>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.