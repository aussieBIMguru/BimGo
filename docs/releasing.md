# Releasing BimGo (maintainers)

← [Docs home](README.md)

How a version goes from the `dev` branch to a download on GitHub. It assumes the usual flow: you work on `dev` and
merge to `main` with a pull request. A **release** is three things on top of that: a version number, a **tag** (a
named pointer to one commit on `main`) and a **GitHub Release** (a page for that tag with the setup attached).

## Once: tools

- Visual Studio with the .NET 8 SDK, plus the .NET 10 SDK (BimGo for Revit 2027), and Revit 2025 / 2026 / 2027
  installed (the build references their API dlls).
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (free; the default install folder is found automatically).
- Optional: the [GitHub CLI](https://cli.github.com/) (`gh`), if you'd rather publish from the command line.

## Version numbers

`major.minor.patch`, for example `1.0.0`:
- **patch** (1.0.**1**): fixes only.
- **minor** (1.**1**.0): new features; files and live sessions stay compatible.
- **major** (**2**.0.0): something people must act on (a file format break, a dropped Revit year).

The number lives in **one place**: `src/Directory.Build.props` → `<BimGoVersion>`. The app, the add-in, the bundle and
the installer all read it.

## Each release

1. **On `dev`: bump the version.** Edit `src/Directory.Build.props`, and add a row to `HISTORY.md`. Commit:
   ```
   git checkout dev
   git commit -am "Version 1.1.0"
   git push
   ```
2. **Merge to `main`** with your usual pull request (dev → main), and wait for it to merge.
3. **Build from `main`** so the setup matches exactly what is tagged:
   ```
   git checkout main
   git pull
   powershell -ExecutionPolicy Bypass -File build\publish.ps1
   ```
   It runs the tests, publishes the app, builds BimGo for Revit 2025–2027 and writes
   `artifacts\BimGo-Setup-1.1.0.exe` (plus a `.sha256.txt` checksum). `artifacts\` is never committed.
4. **Test the setup** on a PC (or a Windows user) without your dev builds: install, open a `.bimgo`, Go from Revit,
   uninstall. The test list is in `DEVELOPING.md` (Installer).
5. **Tag the release** (a tag starting with `v`, on the commit you built):
   ```
   git tag -a v1.1.0 -m "BimGo 1.1.0"
   git push origin v1.1.0
   ```
6. **Publish the GitHub Release**: on GitHub open **Releases → Draft a new release**, choose the tag `v1.1.0`, title it
   "BimGo 1.1.0", write a few lines of what changed (from `HISTORY.md`), attach `BimGo-Setup-1.1.0.exe` and the
   `.sha256.txt`, and click **Publish release**. Or from the command line:
   ```
   gh release create v1.1.0 artifacts\BimGo-Setup-1.1.0.exe artifacts\BimGo-Setup-1.1.0.sha256.txt --title "BimGo 1.1.0" --notes-file notes.md
   ```
   Tick **Set as a pre-release** for a beta (for example tag `v1.1.0-beta.1`).
7. **Back to work:** `git checkout dev` and `git merge main` (so `dev` has the release commit), then carry on.

## Good to know

- **Pre-releases** let testers try a build without it showing as the latest: use them for the beta before 1.0.
- **A tag is permanent in practice.** If a release is broken, don't move the tag: fix it on `dev`, release a new
  patch (1.0.2).
- **Upgrades:** running a newer setup over an older one replaces the app and the add-in and keeps everyone's settings
  and per-model data (the installer's AppId never changes).
- **The installer isn't code-signed** for 1.0, so Windows SmartScreen warns on download. A code-signing certificate
  removes that later; `build\publish.ps1 -Sign -CertThumbprint <thumbprint>` signs the app, the add-in and the setup.
- **Docs and links:** the welcome card's ONLINE GUIDE opens `docs/` on GitHub's `main` branch, so merge the docs with
  the release.
