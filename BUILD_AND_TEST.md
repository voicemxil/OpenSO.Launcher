# Building, running, and testing the OpenSO Launcher

Native Avalonia (C#/.NET 9) launcher — no Electron, no Chromium.

## Prerequisites

- **.NET 9 SDK** — https://dotnet.microsoft.com/download
  Check: `dotnet --version` (should be 9.x)

## Run the launcher (dev)

```bash
cd launcher/OpenSO.Launcher
dotnet run
```

A native window opens, detects what's installed, and offers Install/Play.

## Run the headless logic tests

These exercise the ported services (zip, cab, install-state, dependency graph, config) with no GUI:

```bash
cd launcher/OpenSO.Launcher.Tests
dotnet run
```

Exit code is 0 if all pass, non-zero otherwise (CI-friendly).

To also test real CAB extraction, point the runner at a sample MSZIP `.cab`:

```bash
OPENSO_TEST_CAB=/path/to/Data1.cab dotnet run
```

## Publish native, self-contained builds (what you ship)

Replaces the upstream `npm buildwin / builddarwin / builddeb`. Each produces a self-contained app
that needs no installed .NET runtime:

```bash
cd launcher/OpenSO.Launcher

# Windows x64
dotnet publish -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o ../dist/win-x64

# macOS (Intel + Apple Silicon)
dotnet publish -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true -o ../dist/osx-x64
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o ../dist/osx-arm64

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o ../dist/linux-x64
```

Wire these into the CI release workflow (strategy doc §8) so the launcher ships alongside the client.
Per-OS installers (Inno Setup `.exe`, `.dmg`, `.deb`) wrap these outputs, mirroring upstream.

## What works today (tested)

- Resilient downloads (retry/resume/progress/MD5) — `DownloadService`
- Zip extraction with nested paths + zip-slip guard — `ZipExtractor`
- **Cross-platform CAB + MSZIP extraction** (no native deps, no EULA) — `CabExtractor`
- Install-state detection (Windows registry + path fallbacks) — `InstallStateService`
- Dependency-resolved install orchestration — `InstallOrchestrator`
- FSO client install (download → extract → register → mac extras) — `FsoInstaller`
- TSO assets install (download → unzip → find Data1.cab → CAB-extract → register) — `TsoInstaller`

## Still to port (next slices)

- Mono + SDL installers (runtime deps for macOS/Linux)
- RMS (remesh), Simitone installers
- Launcher self-update (`SelfUpdateService`)
- Game launch (`FSO.Patcher.StartFreeSO` port) + elevation (`ElevationService`)
- Windows registry *write* for game entries (currently a marker file fallback)
