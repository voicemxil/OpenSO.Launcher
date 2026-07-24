<p align="center">
  <img src="OpenSO.Launcher/Assets/openso-glyph.svg" alt="OpenSO icon" width="128" height="128">
</p>

# <div align="center">OpenSO Launcher</div>

<p align="center">
  The official cross-platform installer, updater, and launcher for <a href="https://github.com/voicemxil/OpenSO">OpenSO</a>.
</p>

OpenSO Launcher is the easiest way to install, update, repair, and play OpenSO: a modern, self-hostable reimplementation of *The Sims Online*. It is a native .NET desktop application built with Avalonia—no embedded browser or Electron runtime.

## Download

Download the installer for your platform from [Releases](https://github.com/voicemxil/OpenSO.Launcher/releases).

Supported platforms:

- Windows x64
- macOS on Apple Silicon and Intel
- Linux x64 — download the **`.AppImage`** (a single, double-clickable file with its own name and icon), or the `.zip` if you prefer a plain folder (run `./OpenSO.Launcher` inside it)

Release builds are self-contained, so players do not need to install the .NET runtime separately.

## What it does

- Installs, launches, updates, and repairs OpenSO.
- Keeps the launcher itself up to date.
- Selects the correct package for the current platform and verifies official update manifests and package hashes when available.
- Uses incremental updates on Windows when available, with a safe full-package fallback on every supported platform.
- Preserves player-owned data during updates, including settings and saved content.
- Shows live server status, game version, activity, and news.
- Hands an out-of-date Windows game install back to the Launcher to update and restart it.

## Original game data

OpenSO requires the original *The Sims Online* game data to run. This repository contains no EA or Maxis game assets and is not affiliated with Electronic Arts or Maxis.

## Building and testing

Development requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/voicemxil/OpenSO.Launcher.git
cd OpenSO.Launcher

# Run the launcher in development
dotnet run --project OpenSO.Launcher/OpenSO.Launcher.csproj

# Build and run the headless test suite
dotnet build OpenSO.Launcher/OpenSO.Launcher.csproj -c Release
dotnet run --project OpenSO.Launcher.Tests/OpenSO.Launcher.Tests.csproj
```

For release publishing, update behavior, security guarantees, and the trimmed-build smoke test, see [BUILD_AND_TEST.md](BUILD_AND_TEST.md).

## Contributing

Bug reports, testing, and pull requests are welcome. Please run the headless test suite before opening a pull request.

## License

OpenSO Launcher is released under the [Mozilla Public License, version 2.0](LICENSE.md). It is a companion project to [OpenSO](https://github.com/voicemxil/OpenSO).
