# OpenSO Launcher — porting the FreeSO Launcher to native Avalonia (C#/.NET)

This is a **port**, not a rewrite. The upstream launcher (`ItsSim/fsolauncher`, Electron + Node)
carries years of platform-specific install logic that we keep; we only replace the **shell**
(Electron + Chromium + Pug UI) with **native Avalonia (C#/.NET)**.

Source studied: `ItsSim/fsolauncher` @ `app/` — `fsolauncher/` (main process logic, ~4,000 LOC)
and `fsolauncher-ui/` (renderer; this is the part Avalonia replaces).

---

## What's worth keeping (do NOT reinvent)

The launcher's value is in `app/fsolauncher/`, not the UI. Port this logic faithfully:

| Upstream (JS) | What it does | Avalonia/C# home |
|---|---|---|
| `fsolauncher.js` (1,238 LOC) | Main controller: install orchestration, task tracking, internet checks, update checks, dependency resolution | `Services/LauncherController.cs` + `MainViewModel` |
| `constants.js` | URLs, component list, **dependency graph**, per-OS paths, languages | `Models/LauncherConfig.cs` + `Models/Components.cs` |
| `lib/installers/*.js` (9 installers) | Per-component install steps (download → dir → extract → registry → extras) | `Services/Installers/*.cs` |
| `lib/download.js` | HTTP download with **retry (5×), resume, abort, progress** | `Services/DownloadService.cs` |
| `lib/unzip.js`, `lib/cabinet.js` | Zip (yauzl) + InstallShield **.cab** extraction | `Services/Extraction/*` |
| `lib/registry.js` | **Windows registry** detection + game-path entries, with cross-platform fallbacks | `Services/InstallStateService.cs` |
| `lib/ipc-bridge.js` | Renderer ↔ main messaging | Replaced by direct MVVM binding (no IPC needed) |
| `lib/modal.js`, `toast.js`, `locale.js` | Dialogs, toasts, i18n | Avalonia dialogs + `Services/LocaleService.cs` |

---

## The logic that MUST survive the port (the hard-won parts)

1. **Component dependency graph** (`constants.js` → `dependencies`):
   - `FSO` requires `TSO` + (`Mono`+`SDL` on macOS/Linux, `OpenAL` on Windows)
   - `RMS` requires `FSO`; `MacExtras` requires `FSO`; `Simitone` requires `Mono`+`SDL` on macOS/Linux
   - Installing FSO must resolve+install missing deps first (`getMissingDependencies`, `runFullInstall`).

2. **Install-state detection per platform** (`registry.js` → `getInstallStatus`):
   - **Windows:** read registry (`InstallDir`, game edition `255`, key existence).
   - **macOS/Linux:** check known paths under `~/Library/Application Support/FreeSO Launcher`
     (macOS) or `~/.fsolauncher` (Linux), with **fallback path checks** if the primary is missing.
   - Always re-verify the path still exists on disk before trusting "installed".

3. **Resilient downloads** (`download.js`): 5 retries, resume on partial, abort, live progress —
   players are on flaky connections pulling large files. Keep this behavior.

4. **Extraction**: both **zip** (FSO/RMS/etc.) and **InstallShield .cab** (the TSO assets) —
   `cabinet.js` handles the EA TSO cabinets specifically. Port both.

5. **Elevation**: upstream uses `sudo-prompt` for steps needing admin (registry writes, some
   install dirs). In .NET: request elevation on Windows (manifest/UAC), `sudo`/polkit on Linux,
   AppleScript auth on macOS — only when actually needed.

6. **The OpenSO repoint** (the whole reason for a fork): every upstream URL points at
   `beta.freeso.org` / `riperiperi/FreeSO`. In `LauncherConfig.cs` these become OpenSO endpoints
   (your API, your GitHub releases, openso.org). EA's TSO asset URL
   (`largedownloads.ea.com/pub/misc/tso/`) stays — that's the legal user-supplied-assets path.

---

## What we drop / change

- **Electron + Chromium**: gone. Avalonia renders natively → ~tens of MB instead of ~150+ MB,
  no embedded browser, far less RAM.
- **Pug / HTML / CSS UI** (`fsolauncher-ui/`): rebuilt as Avalonia AXAML views, styled in the
  OpenSO brand (navy + teal, Space Grotesk logo, Inter UI). The *screens* and *flows* are mirrored;
  the implementation is native.
- **IPC bridge**: unnecessary — MVVM binds the UI to services directly in-process.
- **Node deps** (`yauzl`, `follow-redirects`, `sudo-prompt`, `ini`, `howler`, `socket.io-client`):
  replaced by .NET equivalents (`System.IO.Compression`, `HttpClient`, OS elevation, `NAudio`/Avalonia
  audio, a SignalR/WebSocket client if live features are needed).
- **Sentry**: optional; swap for your preferred .NET crash reporting or drop initially.

---

## Reuse from the OpenSO C# repo too

The OpenSO engine repo already has C# logic we can lift instead of re-porting from JS:

- `FSOInstaller/TSOManifest.cs` — manifest parsing (plain class, portable as-is).
- `FSOInstaller/SetupFetcher.cs` — EA TSO asset fetch flow (logic, minus the WinForms shell).
- `FSO.Patcher/Patcher.cs` — `ExtractEntry`, `AttemptRename`, `Cleanup`, `StartFreeSO`
  (update-apply + game-launch logic; lift out of the `Form`).

So the port draws from **two** sources: the Electron launcher's orchestration/platform logic, and
the existing C# install/patch code. Where they overlap, prefer the C# (already in your language).

---

## Target project shape (Avalonia, MVVM)

```
OpenSO.Launcher/
  OpenSO.Launcher.csproj        net9.0, Avalonia 11, no -windows suffix (cross-platform)
  Program.cs / App.axaml        Avalonia bootstrap
  Models/
    LauncherConfig.cs           ← constants.js (URLs/paths) — OpenSO endpoints
    Components.cs               ← constants.js dependency graph + component list
  Services/
    IGameService.cs             install/update/launch contract
    LauncherController.cs       ← fsolauncher.js orchestration
    DownloadService.cs          ← download.js (retry/resume/progress)
    InstallStateService.cs      ← registry.js (detect installs per-OS)
    Extraction/ZipExtractor.cs  ← unzip.js
    Extraction/CabExtractor.cs  ← cabinet.js (TSO .cab)
    Installers/*.cs             ← lib/installers/*.js (one per component)
    ElevationService.cs         ← sudo-prompt equivalent per-OS
  ViewModels/                   MainViewModel, ProgressViewModel, SettingsViewModel
  Views/                        MainWindow.axaml (+ brand styling)
  Assets/                       openso.ico, logo, fonts
```

## Build (replaces npm buildwin/builddarwin/builddeb)

`dotnet publish -c Release -r <rid> --self-contained` for `win-x64`, `osx-x64`, `osx-arm64`,
`linux-x64`. Wire into the CI release workflow (strategy doc §8) so the launcher ships alongside
the client. Per-platform installers (Inno Setup on Windows, .dmg on macOS, .deb on Linux) mirror
the upstream `scripts/build-*.js` outputs.

## Parity checklist (don't ship without)

- [ ] Detects existing FSO/TSO/deps installs on all three OSes (registry + fallbacks)
- [ ] Full-install resolves the dependency graph in order
- [ ] Download retry/resume works on dropped connections
- [ ] Zip AND .cab extraction
- [ ] Elevation prompts only when needed, per-OS
- [ ] Self-update of the launcher
- [ ] Launches the game and tracks/min the launcher
- [ ] All endpoints point at OpenSO (config), EA TSO asset URL preserved
