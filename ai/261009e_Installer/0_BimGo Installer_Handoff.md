# BimGo — Handoff Brief: installer + Revit add-in bundle (for a new chat)

**Context:** BimGo is Gavin's personal project, developed on his own PC in his own time (not a work project). Publisher is **Aussie BIM Guru**; no employer branding anywhere.

**Purpose of the next chat:** build the installer for **BimGo 1.0.0**: one per-user `BimGo-Setup-1.0.0.exe` (Inno Setup) that installs the standalone app and, optionally, BimGo for Revit 2025 / 2026 / 2027 as an Autodesk `.bundle`. **No new features.** Only the installer, the publish steps that feed it, and the small code / project changes the install layout forces (vendor fields, Debug-only dev copies, registration ownership).

This brief replaces the first installer brief: most of its open questions were settled in the dependencies round (`ai/261009d_Dependencies/0_BimGo Dependencies_Handoff.md` §5).

**Read first:**
1. This brief.
2. `README.md`: §1 overview, §2 getting started, §4 project structure, §8 known limitations, §9 changelog, §10 dependencies.
3. `ai/261009d_Dependencies/1_build notes dependencies.md` (the round just finished).
4. **Ask Gavin for a fresh zip of his working copy before editing.** He builds in Visual Studio; his copy is the source of truth.

---

## 1. Where BimGo is now

- **1.0.0 builds and runs on Revit 2025, 2026 and 2027** (Gavin, 2026-10-09).
- **Dependencies round built, tested and confirmed working** (Gavin, 2026-10-09):
  - `Native/Gl.cs` is a thin facade over **Silk.NET.OpenGL 2.23.0** (BimGo.App only; `Wgl.cs`, window and input unchanged).
  - `tests/BimGo.Core.Tests` (MSTest.Sdk 4.4.1, net8.0) all pass.
  - Licence is **MIT** (© Aussie BIM Guru), kept after review (Gavin, 2026-10-09: it was a recommendation, not a dependency requirement; MIT chosen for attribution and its clean standing under Australian law versus the Unlicense's public-domain dedication).
  - `LICENSE` → `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` are copied beside `BimGo.exe` on build.
- Solution `src/BimGo.sln`:
  - **BimGo.Core**: net8.0 library.
  - **BimGo.App**: net8.0-windows WinExe `BimGo.exe`, x64, WinForms in-box + Silk.NET.OpenGL (and its transitive MIT packages). Framework-dependent today.
  - **BimGo.Revit**: `BimGo.Revit.dll`, x64, WPF + WinForms, **no packages** (must stay that way: it loads inside Revit). Configurations `Debug|Release R25 / R26 / R27`: R25 and R26 target net8.0-windows, **R27 targets net10.0-windows**. Revit API by HintPath to `C:\Program Files\Autodesk\Revit <year>\`.
  - **tests/BimGo.Core.Tests**: dev-only.

## 2. How it deploys today (developer builds)

| Piece | Where it goes | Mechanism |
|---|---|---|
| App | `%LocalAppData%\Programs\BimGo\` (all output except .pdb) | `InstallBimGoApp` target in `BimGo.App.csproj`, after every build (Debug **and** Release) |
| Add-in | `%AppData%\Autodesk\Revit\Addins\<year>\BimGo\` + `…\<year>\BimGo.addin` | `CopyToRevitAddins` target in `BimGo.Revit.csproj`, after every build |
| `.addin` | `src/BimGo.Revit/BimGo.addin`: `Assembly` = `BimGo/BimGo.Revit.dll` (relative), AddInId `C1A459F6-4B86-42DF-8CA9-C9E2042F1ABA` (**keep forever**), `FullClassName` `BimGo.Application`, VendorId `Author Name`, VendorDescription `Author Description` (placeholders) | copied by the target |
| Finding the app | `App_Utils.FindExe()`: `%LocalAppData%\Programs\BimGo\BimGo.exe`, else `BimGo.exe` beside the add-in dll | Revit side |
| Association + shortcut | HKCU `Software\Classes\.bimgo` → `BimGo.Model`, `Applications\BimGo.exe`, Start-menu `BimGo.lnk` | `FileAssociation.EnsureRegistered()` on start from the install dir (skipped if the opt-out marker exists); `BimGo.exe --register` / `--unregister` [`--quiet`]. `--unregister` writes the opt-out marker `%LocalAppData%\BimGo\App\no-file-association`; `--register` deletes it |
| Single instance | named mutex + inbox `%LocalAppData%\BimGo\App\inbox\` | `AppInstance` |

**User data (created at run time, never installed):** `%AppData%\BimGo\settings.json`; `%LocalAppData%\BimGo\` (logs, `Sessions\`, app inbox, fallback comments); sidecars beside Revit models (`.bimgo-comments.json`, `-bookmarks.json`, `-sun.json`, `-visibility.json`).

**Placeholders to replace:** `<Company>Author</Company>` in all three product csproj files; `.addin` VendorId / VendorDescription; the `Program.Version` XML doc still says "3.00.00.01" (cosmetic).

## 3. Decisions (settled, Gavin 2026-10-05 / 2026-10-09)

| # | Topic | Decision |
|---|---|---|
| 1 | Packaging / updates | **Inno Setup** (standalone compiler on Gavin's PC, not NuGet). **No auto-update**: each release is a new `BimGo-Setup-x.y.z.exe`. Velopack not used. |
| 2 | Scope | **Per-user, no admin** (`PrivilegesRequired=lowest`). App in `%LocalAppData%\Programs\BimGo\` (unchanged, so `FindExe` doesn't change). Revit bundle in `%AppData%\Autodesk\ApplicationPlugins\BimGo.bundle\`. Association in HKCU. |
| 3 | Installers | **One setup.exe** (confirm with Gavin). App always installed; each Revit year a ticked component, offered only if that Revit is detected. |
| 4 | .NET | App published **self-contained win-x64**, trimming off (WinForms), so standalone users need nothing. Add-in runs on Revit's runtime (2025 / 2026: .NET 8, 2027: .NET 10). |
| 5 | Licence | **MIT**; Inno licence page shows `LICENSE`; `THIRD-PARTY-NOTICES.txt` installed beside the exe (already in the build output). Self-contained adds the .NET runtime (MIT) — already listed in the notices. |
| 6 | Publisher | **Aussie BIM Guru**: csproj `<Company>`, Inno `AppPublisher`, `.addin` VendorDescription, `PackageContents.xml` CompanyDetails. VendorId a short stable ID (suggest `AUSB`; confirm), never changed afterwards. |
| 7 | Signing | **None for 1.0.** Accept SmartScreen and Revit's unverified-publisher prompt ("Always Load"). Script the signtool step but leave it switched off. |
| 8 | Association / shortcut owner | **The app.** Installer runs `BimGo.exe --register --quiet` after install and `--unregister --quiet` on uninstall (`[UninstallRun]`, before files are removed). Inno creates **no** Start-menu icon of its own. `EnsureRegistered` stays as a quiet repair and must not duplicate the shortcut. |

## 4. Still to confirm with Gavin (ask, don't assume)

1. One setup.exe with optional Revit components (vs two downloads).
2. VendorId value (`AUSB`?).
3. Inno `AppId` GUID (new, fixed forever) and `PackageContents.xml` `ProductCode` / `UpgradeCode` GUIDs.
4. **Dev PC vs installer** (duplicate AddInId risk, §6): either never run the installer on the dev PC, or point the Debug `CopyToRevitAddins` target into the bundle's `Contents\<year>\` so only one copy exists.
5. Uninstall: keep user data (default) or offer to remove it.
6. New folders: `installer/` (Inno script, bundle template) and `build/` (publish script), plus `artifacts/` output (git-ignored, zip-excluded). Ask before creating them.

## 5. Plan

1. **Project changes (small):**
   - `<Company>Aussie BIM Guru</Company>` in Core, App and Revit csproj; VendorId / VendorDescription in `BimGo.addin`.
   - Make `InstallBimGoApp` and `CopyToRevitAddins` **Debug-only** (condition on `$(Configuration.StartsWith('Debug'))` / `'$(Configuration)' == 'Debug'`), or switch them off with a property for publish, so release builds never touch the build machine's installs.
   - Version stamped once (e.g. `Directory.Build.props` with `Version` 1.0.0, or `-p:Version=` from the script).
2. **Publish script** (`build/publish.ps1`):
   - `dotnet publish src/BimGo.App -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false` → `artifacts/app/`. Folder publish, not single-file (the icon is read from beside the exe for the window class). Compare size with framework-dependent (expect ~70 MB on disk, ~30 MB compressed).
   - `dotnet build src/BimGo.Revit -c "Release R25"` (and R26, R27; R27 needs the .NET 10 SDK) → `artifacts/revit/2025|2026|2027/` holding `BimGo.addin` + `BimGo\` (dll, BimGo.Core.dll, deps; no .pdb). Check no Silk.NET or other package dll appears there.
   - Run `dotnet test tests/BimGo.Core.Tests` first and stop on failure.
   - Optional `signtool` step, off by default.
   - `iscc installer/BimGo.iss` → `artifacts/BimGo-Setup-1.0.0.exe`.
3. **Autodesk bundle** (`installer/BimGo.bundle/PackageContents.xml` template):
   - `ApplicationPackage` (`AutodeskProduct="Revit"`, `ProductType="Application"`, Name, AppVersion, ProductCode, UpgradeCode), `CompanyDetails Name="Aussie BIM Guru"`.
   - One `Components` per year: `RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2025" SeriesMax="R2025"` (2026, 2027 likewise) with `ComponentEntry ModuleName="./Contents/2025/BimGo.addin"`.
   - Layout `Contents\<year>\BimGo.addin` + `Contents\<year>\BimGo\…` keeps the `.addin`'s relative `Assembly` path unchanged.
   - The installer writes only the ticked years' `Contents\<year>\` and a PackageContents.xml listing only those (or all, if Revit ignores entries whose folder is missing: verify).
4. **Inno script** (`installer/BimGo.iss`):
   - `PrivilegesRequired=lowest`, `DefaultDirName={localappdata}\Programs\BimGo`, `DisableDirPage=yes` (FindExe expects that path), `AppPublisher=Aussie BIM Guru`, `LicenseFile=..\LICENSE`, `SetupIconFile` = `src/BimGo.App/Resources/BimGo.ico`, `UninstallDisplayIcon={app}\BimGo.exe`, version 1.0.0, 64-bit only (`ArchitecturesAllowed=x64compatible`, `ArchitecturesInstallIn64BitMode=x64compatible`).
   - `[Components]`: `app` (fixed), `revit2025`, `revit2026`, `revit2027`, each `Check:` Revit-year detected (`HKLM\SOFTWARE\Autodesk\Revit\…` or `{commonpf}\Autodesk\Revit <year>\Revit.exe`).
   - `[Files]`: `artifacts/app/*` → `{app}`; bundle → `{userappdata}\Autodesk\ApplicationPlugins\BimGo.bundle\`.
   - `[Run]` `BimGo.exe --register --quiet` (runhidden); `[UninstallRun]` `--unregister --quiet`.
   - `[Code]`: before install, remove legacy `{userappdata}\Autodesk\Revit\Addins\<year>\BimGo.addin` + `BimGo\` and `RvtGo.addin` + `RvtGo\` for every year (duplicate AddInId otherwise); refuse to continue (with a retry message) while `Revit.exe` or `BimGo.exe` runs (`CloseApplications` or a process check: locked dlls).
   - `[UninstallDelete]`: the bundle folder. User data untouched (unless decision §4.5 says otherwise).
5. **README / notes:** §2 splits "Install" (users: run the setup) from "Develop" (VS flow); §8 / §9 updated; `ai/<date>_Installer/1_build notes installer.md`.

## 6. Things to watch

- **Duplicate AddInId:** an `Addins\<year>\BimGo.addin` plus the bundle makes Revit report a duplicate. The installer's legacy cleanup handles users; the dev PC needs decision §4.4.
- **Per-user ApplicationPlugins:** confirm each of Revit 2025 / 2026 / 2027 loads bundles from `%AppData%\Autodesk\ApplicationPlugins\` (not only `%ProgramData%`), and that each year loads only its own component.
- **`App_Utils.FindExe`** must still find `%LocalAppData%\Programs\BimGo\BimGo.exe`: don't let the user pick another folder.
- **`--unregister` leaves the opt-out marker** in `%LocalAppData%\BimGo\App\`. That's fine (a reinstall's `--register` deletes it), but an uninstall that keeps user data leaves that file behind.
- **Self-contained app + framework-dependent leftovers:** upgrading over a dev-build install leaves old framework-dependent files (e.g. `BimGo.runtimeconfig.json` differences). Consider `[InstallDelete]` of `{app}\*` before copying, since the folder holds nothing but the app.
- **Transitive packages** in the app output (Silk.NET.*, Microsoft.Extensions.DependencyModel, possibly System.Text.Json 9.x): all listed in `THIRD-PARTY-NOTICES.txt`; recheck that list against the publish folder.
- **Locked add-in dll** while Revit runs; **BimGo.exe** must be closed to replace it.

## 7. Test matrix

- Clean Windows user with no .NET: standalone install, double-click `.bimgo`, Start menu shortcut, uninstall leaves only user data.
- Each Revit year alone, and all three: ribbon tab appears once, Go launches the installed app, Export / Live / push work.
- Upgrade over the dev-build layout (legacy Addins copies removed, no duplicate AddInId) and over a previous setup (same AppId).
- Running Revit / BimGo during install and uninstall.
- Non-admin account.
- `--register` / `--unregister` results match what uninstall removes (HKCU keys, shortcut).
- UX parity checklist (dependencies handoff §6) on the installed, self-contained build.

## 8. Conventions (unchanged)

- Readable, robust code, XML doc headers, explicit types where clearer; no per-frame allocations.
- Dependency policy: README *For AI assistants* item 3 and §10. Installer tooling is outside NuGet; any package still needs Gavin's yes. **BimGo.Revit stays package-free.**
- Ask before adding folders (`installer/`, `build/`, `artifacts/`).
- No exceptions to the user: log via `Utilities.Log_Utils.Write`, show a dialog / toast.
- Revit API only in `Commands/`, `Extraction/`, `Bridge/RevitEditor*.cs`, `Live/LiveDispatcher.cs`.
- Format and protocol stay backward compatible (`formatVersion` 1, protocol 1).
- Run the Core tests after any Core change.
- Keep `README.md` and an `ai/<date>_<topic>/` notes file current; zip the repo minus `bin/`, `obj/`, `.vs/`, `artifacts/`.
