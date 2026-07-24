# Building, running, and testing the OpenSO Launcher

Native Avalonia (C#/.NET 9) launcher — no Electron, no Chromium.

## Prerequisites

- **.NET 9 SDK** — https://dotnet.microsoft.com/download
  Check: `dotnet --version` (should be 9.x)

## Run the launcher (dev)

```bash
cd OpenSO.Launcher
dotnet run
```

A native window opens, detects what's installed, and offers Install/Play.

## Run the headless logic tests

These exercise the ported services (zip, cab, install-state, dependency graph, config) with no GUI:

```bash
cd OpenSO.Launcher.Tests
dotnet run
```

Exit code is 0 if all pass, non-zero otherwise (CI-friendly).

To also test real CAB extraction, point the runner at a sample MSZIP `.cab`:

```bash
OPENSO_TEST_CAB=/path/to/Data1.cab dotnet run
```

## Continuous integration

- **`.github/workflows/ci.yml`** — runs on every pull request and every push to `main`. Installs the
  .NET SDK (`actions/setup-dotnet@v4`, pinned to `9.0.x` — matches the `TargetFramework` in both
  `OpenSO.Launcher.csproj` and `OpenSO.Launcher.Tests.csproj`), builds
  `OpenSO.Launcher/OpenSO.Launcher.csproj` in Release, then runs the exact commands from "Run the
  headless logic tests" above (`cd OpenSO.Launcher.Tests && dotnet run`). The job fails if the build
  errors (warnings are informative only — no `TreatWarningsAsErrors`) or if any test fails (the harness
  exits non-zero on the first accumulated failure). A single `ubuntu-latest` runner is used — the suite
  is OS-agnostic by inspection: `RegistryWriter`/`FsoInstaller.CurrentRid` branch on
  `OperatingSystem.Is*()` internally and are asserted either way, the ZIP symlink test sets the Unix mode
  bits directly on the zip entry (no real symlink is created), and the CAB test self-skips unless
  `OPENSO_TEST_CAB` is set. No matrix is needed for correctness (the release workflow's per-RID matrix
  already covers the platform-specific *publish* step separately). After the tests, ci.yml also produces
  the **trimmed** linux-x64 publish and runs its `--smoke` self-check (see "Trimmed-binary smoke gate"),
  so a trimming regression fails the job too.
- **`.github/workflows/release.yml`** — gates the release on the same build+test: a `test` job (identical
  build+test steps) must succeed before the per-RID `build`/publish job runs, which in turn gates the
  `release` job that publishes the GitHub Release (`needs: test` → `needs: build`). A release can never
  be cut from a red suite. Each per-RID publish additionally runs the trimmed binary's `--smoke`
  self-check (skipping the cross-published osx-x64) so a release can never be cut from a broken trim. The `test` job's stdout is captured to `test-results.log`, echoed into the
  job's step summary, and uploaded as the `release-test-log` artifact (90-day retention) so a release's
  verification is auditable after the fact.

### Reproduce CI locally

```bash
dotnet build OpenSO.Launcher/OpenSO.Launcher.csproj -c Release
cd OpenSO.Launcher.Tests && dotnet run
```

If your machine only has a newer SDK installed (e.g. .NET 10, no matching .NET 9 SDK), roll forward:

```bash
DOTNET_ROLL_FORWARD=Major dotnet build OpenSO.Launcher/OpenSO.Launcher.csproj -c Release
cd OpenSO.Launcher.Tests && DOTNET_ROLL_FORWARD=Major dotnet run
```

CI itself installs the real 9.0.x SDK via `actions/setup-dotnet@v4`, so `DOTNET_ROLL_FORWARD` is a
local-only workaround and is not set in either workflow.

## Publish native, self-contained builds (what you ship)

Replaces the upstream `npm buildwin / builddarwin / builddeb`. Each produces a self-contained app
that needs no installed .NET runtime. **Multi-file** (not single-file) — the release workflow copies the
whole publish dir into the macOS `.app` bundle and the self-update swap replaces the dir wholesale; see
"Published size & trimming" below for why single-file is a deferred follow-up.

```bash
cd OpenSO.Launcher

# Windows x64
dotnet publish -c Release -r win-x64   --self-contained -o ../dist/win-x64

# macOS (Intel + Apple Silicon)
dotnet publish -c Release -r osx-x64   --self-contained -o ../dist/osx-x64
dotnet publish -c Release -r osx-arm64 --self-contained -o ../dist/osx-arm64

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained -o ../dist/linux-x64
```

All the size knobs (invariant globalization, IL-only/no-ReadyToRun, no shipped PDBs, trimming) live in
`OpenSO.Launcher.csproj` and apply automatically — no extra `-p:` flags. Per-OS installers (Inno Setup
`.exe`, `.dmg`, `.deb`) wrap these outputs, mirroring upstream.

## Published size & trimming

The self-contained publish is aggressively size-optimized. Baseline vs. optimized (self-contained, per RID):

| RID       | Baseline | Optimized | What dominates the "before" |
|-----------|---------:|----------:|-----------------------------|
| osx-arm64 |   109 MB |     47 MB | untrimmed framework (CoreLib 15.5 MB, Xml 8.8 MB, …) |
| linux-x64 |  ~109 MB |     45 MB | same |
| win-x64   |  ~146 MB |     46 MB | the above **+ ~100 MB of native PDBs** (libSkiaSharp.pdb ≈ 80 MB, libHarfBuzzSharp.pdb ≈ 20 MB) |

The knobs, in the csproj:

- **`InvariantGlobalization=true`.** The launcher does no culture-specific parsing/formatting — server
  timestamps are ISO-8601 (culture-invariant), on-screen times use fixed formats (`HH:mm:ss`, `h:mm tt`),
  and every case-fold is `*Invariant`. Invariant mode changes the culture, **not** the time zone, so the
  status/clock times stay local. Drops `icudt.dat` (~24 MB on win/linux; macOS uses the system ICU) and
  lets the trimmer prune the globalization stack.
- **IL only, never ReadyToRun** (`PublishReadyToRun=false`), and **no shipped PDBs** — `DebugType=none`
  for the app's managed symbols, plus a `StripPublishSymbols` target that removes **native** `.pdb`s the
  runtime packs copy in (the ~100 MB Windows win above). `release.yml` uploads no separate symbol
  artifact, so shipped symbols are pure weight.
- **`PublishTrimmed=true`, `TrimMode=partial`.** Trims the framework and trim-annotated libs (Avalonia 12
  is trim-safe; this app uses compiled bindings everywhere — CI validates them) while copying
  non-annotated libs (e.g. AsyncImageLoader) whole. Shrinks e.g. CoreLib 15.5→2.65 MB, System.Text.Json
  2.0→0.27 MB, and removes System.Private.Xml, System.Data.Common, DataContractSerialization, VisualBasic.

### Trim safety (the reflection audit)

Trimming can silently drop members reached only by reflection. The audited surfaces and their fixes:

- **JSON.** The only reflection-based serialization was `LauncherSettings` (settings file) and
  `ServerStatus` (status endpoint). Both now route through the **source-generated** `LauncherJsonContext`
  (`Models/LauncherJsonContext.cs`) — compile-time metadata, zero reflection, trim-safe. Every other JSON
  path is `JsonDocument` (a DOM reader — reflection-free already): the client manifest, GitHub release
  feeds, delta manifests, launcher self-update feed.
- **XAML bindings.** `MainWindow.axaml` had two `x:CompileBindings="False"` item-template islands
  (busiest-lots and news) that fell back to **reflection bindings** — which break under trimming if the
  bound model's members are stripped. Both were converted to **compiled bindings** (`x:DataType` on the
  `DataTemplate`; the news command uses the `$parent[ItemsControl].((vm:MainViewModel)DataContext)` form).
  Avalonia validates compiled bindings at build time, so a mistake fails the build, not the runtime.
- **CommunityToolkit.Mvvm** is source-generated (trim-safe). **AsyncImageLoader** is copied whole (not
  trim-annotated), so its internal reflection is preserved.

Result: **zero project-code trim warnings.** The only remaining `IL2xxx` warnings come from
`Avalonia.DesignerSupport.Remote.*` — Avalonia's design-time XAML-previewer entry point, which the shipped
app never executes (its entry point is `Program.Main`, not `RemoteDesignerEntryPoint.Main`). Those are
library warnings and are justified/expected.

### Trimmed-binary smoke gate (`--smoke`)

The logic tests (`OpenSO.Launcher.Tests`) compile the sources **fresh**, so they can never see the
trimmer's output. To catch trim breakage the shipped binary carries a headless self-check:

```bash
./OpenSO.Launcher --smoke     # ...\OpenSO.Launcher.exe --smoke on Windows
```

It never starts the Avalonia UI loop; it exercises the trim-sensitive paths and exits 0 iff all pass
(non-zero otherwise). Coverage: source-gen `ServerStatus` deserialize (case-insensitive, nested arrays) +
`LauncherSettings` round-trip; per-RID manifest select/version-parse (incl. malformed → hard-fail); GitHub
release-feed parse + exact-RID asset picking (client + launcher); RID detection; version comparison;
invariant timestamp formatting; and `ArchivePathGuard` on a real in-memory zip (safe extracts; traversal
rejected, nothing written). Implemented in `SmokeTest.cs`, dispatched from `Program.Main` before any
Avalonia bootstrap.

**CI gates on it.** `ci.yml` publishes the trimmed linux-x64 binary and runs `--smoke` on every PR/push;
`release.yml` runs `--smoke` on each freshly-published binary (skipping osx-x64, which is cross-published
on the arm64 mac runner and can't execute there). A non-zero exit fails the job and blocks the release.

**What `--smoke` does NOT cover:** the Avalonia UI itself (XAML load, Fluent theme, control rendering,
AsyncImageLoader) — it runs headless. Those are covered by compiled-binding build validation + running the
app on a real desktop session; verify the window renders after any Avalonia/trim change.

### Single-file publish — deferred follow-up

`release.yml` publishes multi-file (`PublishSingleFile=false`). Reasons to keep it that way for now:

- **macOS must stay multi-file:** the workflow does `cp -R publish/. "OpenSO Launcher.app/Contents/MacOS/"`
  then `codesign --deep` + DMG — a single-file apphost that self-extracts natives to a temp dir would
  fight that layout and signing.
- **Self-update is already layout-agnostic** (verified by reading `SelfUpdateService.SpawnSwapAndRelaunch`:
  it copies the *whole* extracted dir over the app dir — `xcopy /E` on Windows, `cp -R` on Unix — so file
  count doesn't matter), so single-file for **win/linux only** (via per-RID conditional publish args) is
  *possible* and would add `EnableCompressionInSingleFile` savings.
- **But** SkiaSharp/HarfBuzz natives under single-file need `IncludeNativeLibrariesForSelfExtract=true` and
  runtime verification on real Windows/Linux — which can't be done from a macOS dev box — so it's left as a
  recommended follow-up rather than shipped unverified.

## Linux packaging (zip + AppImage)

The Linux release ships **two** assets, both for `linux-x64`:

- **`OpenSO.Launcher-linux-x64.zip`** — the same self-contained multi-file publish as every platform, name
  and layout **unchanged**. This is what existing installs' self-update swaps in (`SelfUpdateService`
  relaunches `$INSTALL/OpenSO.Launcher`), so it must keep its exact name and the `OpenSO.Launcher` apphost.
  A short **`README.txt`** is added to the zip (packaging step) that names the binary (`./OpenSO.Launcher`),
  gives the `chmod +x` fallback, and notes that game data/saves/settings live under `~/.local/share/OpenSO`
  (or `$XDG_DATA_HOME`) and `~/.config/OpenSO Launcher`, not in the unzipped folder.
- **`OpenSO-Launcher-linux-x64.AppImage`** — a single double-clickable file with its own `Name=OpenSO Launcher`
  and icon (the format most Linux users recognize). Built in `release.yml` (linux-x64 leg only): assemble an
  AppDir (`AppRun` → `usr/bin/OpenSO.Launcher`, an `openso-launcher.desktop` with `Categories=Game;`, and a
  256px PNG rendered from `Assets/openso-glyph.svg`), then `appimagetool ... --appimage-extract-and-run
  --no-appstream` (extract-and-run because CI has no FUSE; `--no-appstream` because we ship no metainfo). The
  zip continues to be published unchanged alongside it.

### Exec bit (both the release zip and the self-update swap)

The Linux apphost must be executable. Two independent paths preserve that, and both are verified:

- **Release zip.** `zip -r` stores unix modes (host = unix) and `unzip` restores them, so `OpenSO.Launcher`
  comes out `0755`. (GUI extractors sometimes drop the bit — hence the README's `chmod +x` line.)
- **Self-update swap.** `ZipExtractor` extracts with `preservePermissions: true` on Unix and restores each
  entry's mode from the high 16 bits of `ExternalAttributes` (the same field `zip -r` writes) — so the
  swapped-in binary keeps `+x`. A lost bit here would strand an un-runnable launcher; the regression test
  **"ZipExtractor preserves the unix exec bit on extraction"** (Unix-only, self-skips on Windows) locks it in.

### AppImage self-update

Inside an AppImage, `Environment.ProcessPath` points into the transient `/tmp/.mount_*` squashfs; the real,
updatable file is `$APPIMAGE`. `SelfUpdateService` detects this (`AppImagePath` — reads the `APPIMAGE` env)
and takes a **single-file replace** path instead of the dir swap: it resolves the update from the **same**
release feed but picks the `.AppImage` asset for this RID (`PickLauncherAsset(..., AppImageSuffix)`), downloads
to a hidden same-dir sibling (`AppImageSiblingTemp` → atomic same-filesystem rename), verifies it (the feed's
`sha256` digest when present, else a non-empty sanity check — `DownloadService` throws on an empty response),
atomically renames it over `$APPIMAGE` and `chmod +x` (`ReplaceAppImageFile`), then relaunches `$APPIMAGE` and
exits. Overwriting the running AppImage is safe: the rename only swaps the directory entry, and the running
process keeps its now-unlinked inode mounted until it exits. The mode decision, asset choice, target-path
derivation, and the replace itself are pure/injectable seams and are headlessly tested.

**Handoff marker under AppImage.** `LauncherHandoff` writes `$APPIMAGE` when set (via `ResolveMarkerPath`),
not the dead mount path, so the game's `Process.Start` of the marker starts the real, persistent AppImage
(an ELF with the exec bit). Tested via env injection.

**Install root is unaffected.** The game install root (`LauncherConfig.DefaultInstallRoot` → `$XDG_DATA_HOME`
/`~/.local/share/OpenSO`, legacy `~/OpenSO` kept if present) and launcher settings (`SpecialFolder.Application‑
Data` → `~/.config/OpenSO Launcher`) are XDG-based and never derived from the executable's directory, so the
read-only AppImage mount is a non-issue — no migration needed.

## Archive-extraction security policy

Every archive the launcher unpacks (game client, launcher self-update, TSO assets, remesh pack) comes
from the network and is treated as **untrusted**. All extraction routes through the shared
`ArchivePathGuard.ResolveContainedPath(dest, entryName)` (used by both `ZipExtractor` and
`CabExtractor`), which enforces:

- **Canonicalize + relative-path containment.** The entry is resolved with `Path.GetFullPath`, then
  `Path.GetRelativePath(dest, target)` must be non-rooted and must not start with `..`. This replaces
  the old `fullPath.StartsWith(fullDest)` string-prefix check, which accepted a sibling whose name began
  with the destination's (dest `install` accepted `install-evil/…`).
- **Reject up front:** rooted/absolute entry paths, any `..` (or `.`) component, empty path components
  (`a//b`), and backslashes (so `a\..\b` can't be reinterpreted as traversal on Windows).
- **Every entry is validated — files AND directories.** `ZipExtractor` validates the whole archive in a
  first pass and only writes on a second pass, so an unsafe entry rejects the **entire** archive with
  **nothing written** (no partial extraction of a malicious archive).
- **Reject symlink / special-file entries.** A zip entry whose unix mode (high 16 bits of
  `ExternalAttributes`) is `S_IFLNK` (or a device/fifo/socket) is refused — a symlink could redirect a
  later entry's write outside the destination.

The headless delta-update path (`DeltaUpdateEngine`) extracts its incremental zips through this **same**
`ArchivePathGuard` policy (validate every entry — containment + symlink/special rejection — before any
mutation; reject the whole archive on the first bad entry). The launcher no longer invokes the legacy
`update.exe` patcher for any archive extraction (see "Deltas" below).

## Client update source precedence

The game-client full package (install and full-reinstall update, `FsoInstaller.ResolveClientPackageAsync`)
is resolved from these sources, in order. Everything downloaded from the network is untrusted transport
input.

1. **Canonical per-RID manifest — `openso-manifest.json` (FIRST / primary).** Fetched from
   `LauncherConfig.ClientManifestUrl`, which defaults to the stable release asset
   `https://github.com/voicemxil/OpenSO/releases/latest/download/openso-manifest.json`. It is parsed as
   the `schemaVersion: 1` per-RID schema defined in `OpenSO/Documentation/update-manifest.md`:
   `{ schemaVersion, version, clients: { "<rid>": { full: { url, sha256 }, deltas? } } }`. Selection is
   **exact-RID only** (`FsoInstaller.CurrentRid()` → `win-x64` / `linux-x64` / `osx-x64` / `osx-arm64`),
   and returns that RID's hash-verified `full` package. The URL is configurable so it can point at a
   mirror (e.g. an `api.openso.org` endpoint), but a mirror **must** serve the same schemaVersion-1
   per-RID schema — the old single-`full_zip`, no-RID-check API manifest is no longer honoured.

2. **GitHub release-asset enumeration (CONTROLLED FALLBACK).** Used **only when the manifest is
   unavailable** — a network failure or a release that predates the manifest (HTTP non-success). It
   enumerates the release feed (`LauncherConfig.ClientReleaseFeed`) and picks the exact-RID full client
   zip (`PickFullClientAsset`, the wave-1 hardening: never a cross-platform payload). The release feed
   publishes no per-asset hash, so this path carries **no SHA-256**.

**A manifest that is reachable but wrong** (malformed JSON, unknown `schemaVersion`, or missing this RID)
is a **hard fail — never a silent downgrade to the GitHub path**, so a corrupt or hostile manifest can't
route the user around hash verification. A missing RID surfaces as `PlatformNotSupportedException`; a bad
schema/shape as `InvalidOperationException`. Both are shown to the user (MainViewModel → "Install
failed: …") and never substitute another platform's build.

### SHA-256 verification precedes extraction

When the package came from the manifest it carries a `sha256`. `FsoInstaller` passes it to
`DownloadService(expectedSha256:)`, which hashes the completed download and, on mismatch, **deletes the
file and throws `ChecksumMismatchException`** — so a tampered or corrupt package is discarded and can
**never** reach the (already hardened) `ZipExtractor`. Verification therefore always happens *before*
extraction. (The GitHub fallback has no hash and is verified only by the existing structural
`VerifyClientInstall` check on the staged extract.)

### Deltas (incremental update path)

The manifest may carry **Windows-only** `deltas` (`{ from, url, sha256 }`, one back-link per release: `from`
is the immediately-previous release, `url` is `OpenSO-client-win-x64.incremental.zip`, `sha256` is of that
zip). These are consumed by the launcher's own headless, transactional **`DeltaUpdateEngine`**
(`Services/Updates/DeltaUpdateEngine.cs`) — the in-process replacement for the legacy `update.exe` patcher.
`FsoInstaller.SelectFromManifest` still reads only `clients.<rid>.full` (install / full-reinstall); the
delta path is separate and lives entirely in the engine.

**When it's used.** On a game update (`MainViewModel.UpdateGameAsync`), on **Windows / `win-x64` only** (the
only platform deltas are published for), the engine is tried before the full package. Every other platform,
and every delta failure, uses the full package below.

**Delta package format** (confirmed from `FSO.DeltaGen` + `gen-manifest.py`): the incremental zip contains
**only the Add + Modify files** at their install-relative paths (never the patcher's own `update.*`). File
**removals** are NOT in the zip — they live in a separate, hash-less sibling asset
`OpenSO-client-win-x64.manifest.json` (`{ Version, Diffs: [{ DiffType, Path }] }`, `DiffType == 2` = Remove),
reachable by swapping `.incremental.zip` → `.manifest.json` on the delta url.

**Multi-hop chains.** When the install is more than one release behind, the engine applies a *chain* of
deltas (installed → … → target), reproducing the legacy `update.exe` advance-per-patch behaviour. The chain
is discovered **manifest-first**: each release's manifest carries a single delta whose `from` back-links to
the previous release, so the engine fetches the target's per-release manifest
(`…/releases/download/<tag>/openso-manifest.json`) and walks the `from` links backwards, tag by tag, until a
hop's `from` equals the installed version. This is preferred over the `userapi/update` feed precisely because
each manifest delta carries a **SHA-256** (the feed's `ApiUpdate` entries do not), so every hop can be
hash-verified before mutation. The walk is bounded; a missing intermediate manifest/delta, a too-long chain,
or a mirror URL that can't be rewritten simply yields "no chain" → full fallback.

**Transaction lifecycle (per hop).** `stage → validate → backup → apply → remove → finalize`, each hop its
own transaction: the incremental zip is downloaded and **SHA-256-verified against the manifest before any
mutation** (a mismatch discards the file and never touches the install); every archive entry is validated
through the shared `ArchivePathGuard` policy (containment + symlink/special rejection) so an adversarial zip
is refused whole with nothing written; each overwritten or removed file is backed up first; a **removal that
fails fails the whole hop**; and the **version marker (`version.txt`) is written last** as the commit point.
On **any** failure the hop is rolled back to byte-identical pre-state. Because the marker advances only per
completed hop, a mid-chain failure leaves the install **consistent at the last completed hop's version**,
never half-applied.

**Fallback to full.** Any delta outcome other than a fully-applied chain (non-Windows, no/partial chain,
hash mismatch, apply/removal failure, an install predating `version.txt`) returns `false` and the update
falls back automatically to the target's **full package** — which is always safe, hash-verified, and
preserves user data (`FsoInstaller` atomic swap + `CarryOverUserData`). The notification surfaces which path
ran ("via incremental delta" vs the full reinstall).

**User-owned files.** A delta never overwrites or removes the user's files. `Content/config.ini` and
`NLog.config` (the same keep-user-copy set as the full path's `CarryOverUserData`, matching the legacy
patcher's `IgnoreFiles`) are skipped for both overwrite and removal even if a delta/removal-manifest lists
them. Files a delta does not mention (saves, the remesh pack, the mesh cache) are inherently untouched — a
delta only carries changed *game* files and its removal manifest only lists *game* files. Every removal path
is additionally validated relative-safe (no `..`/rooted paths) before it can reach a `File.Delete`.

**`update.exe` deprecation.** The launcher **never invokes `update.exe`.** The former delegation
(`GameUpdateService` → staging `PatchFiles/` → running `update.exe`, driven by the `userapi/update` feed) has
been removed; `DeltaUpdateEngine` is now the delta path. `update.exe` itself remains, in-game, as the
temporary Windows legacy/recovery tool (unchanged in the OpenSO repo).

## Game → launcher handoff

On Windows, a **Launcher-managed** install can hand control back to the launcher when the game client itself
detects a version mismatch (its login handshake against the server): the client starts the launcher with
`--update-game` and exits. The contract is fixed and agreed with the game side:

**Marker file — `openso-launcher.path`.** Written/refreshed by `LauncherHandoff.WriteMarker`
(`Services/LauncherHandoff.cs`) into the **GAME install root** (the FSO directory): a UTF-8-**without-BOM**,
single-line file holding the absolute path of *this launcher's own executable* (`Environment.ProcessPath`,
falling back to `Process.GetCurrentProcess().MainModule?.FileName` — the actual apphost file, never a macOS
`.app` bundle directory). The **no-BOM** requirement is load-bearing, not cosmetic: `Encoding.UTF8` emits a
byte-order-mark preamble by default, which would land as a literal leading `U+FEFF` character in the file;
the game's reader only does a plain `string.Trim()`, and .NET's `char.IsWhiteSpace` does **not** treat
`U+FEFF` as whitespace, so a BOM would survive the trim and break `File.Exists`/`Directory.Exists` on an
otherwise-correct path (caught during manual verification of this feature — see `LauncherHandoff`'s
`Utf8NoBom` encoding and the raw-byte assertion in its test). The game treats an install as Launcher-managed
**iff** this file exists and the path it names still exists; it reads the marker, trims the single line, and
starts that path directly with `Process.Start(path, "--update-game")`. Writing the marker is always
**best-effort** — a permissions error or an unwritable/missing install dir is swallowed and never fails the
caller. One shared helper is called from three places so both fresh and pre-existing installs end up marked:

1. After a successful full install/update — `FsoInstaller.InstallAsync`.
2. After a successful delta update — `DeltaUpdateEngine.TryDeltaUpdateAsync`.
3. On **every** game launch — `GameLauncher.Launch` — so an install made by an *older* launcher (before this
   marker existed) gets one the first time the user presses PLAY, without waiting for the next update.

**`--update-game` CLI flag.** Parsed by `LauncherArgs.HasUpdateGame` (unrecognized args are ignored) from the
args Avalonia's desktop lifetime captures (`Program.cs` → `StartWithClassicDesktopLifetime(args)` →
`IClassicDesktopStyleApplicationLifetime.Args`, read in `App.OnFrameworkInitializationCompleted`). When set,
`App` constructs `MainViewModel(updateGame: true)`, which fires `MainViewModel.RunUpdateGameHandoffAsync()`
once startup is under way (fire-and-forget, same pattern as the launcher's other startup tasks) — guarded by
a re-entrancy flag so the flag can only ever trigger the flow once per process. That method: refreshes the
install state, takes one deterministic read of the server's required version, updates **only if needed**
(`DeltaUpdateEngine.NeedsUpdate` — the same delta-chain-or-full pipeline PLAY/UPDATE GAME drive manually, with
the normal progress UI), and **auto-launches the game on success**. When the install is already current, it
skips straight to launch — **no unnecessary reinstall**. On failure it surfaces the normal error in the UI,
**stays open**, and does **not** retry or loop.

## TSO detection, completeness validation & reinstall/repair

The launcher detects existing **The Sims Online** game assets the same way the game client resolves them at
runtime (OpenSO `tso.client/Utils/GameLocator/*Locator.cs`), then validates completeness before trusting a
directory, and exposes reinstall/repair actions for both artifacts.

**Detection candidate order (`Services/TsoInstallDetector.cs`).** Highest precedence first — mirroring the
game's own locator order (relative-sibling → registry → hardcoded fallback):

1. **Managed** — the launcher-managed `<installRoot>/The Sims Online` (a sibling of the OpenSO client, which
   the game's relative `../The Sims Online/TSOClient/` check finds first).
2. **Registry** *(Windows)* — `HKLM\SOFTWARE\Maxis\The Sims Online\InstallDir`, read under **both** the
   32-bit/WOW6432Node view (`RegistryView.Registry32` — the view `WindowsLocator` actually opens) and the
   native view, so a value written by either the launcher or the legacy retail installer is seen.
3. **Legacy path** *(Windows)* — the well-known retail dirs `C:\Program Files\Maxis\The Sims Online\TSOClient`
   and `C:\Program Files (x86)\...\TSOClient` (the game's own hardcoded fallback).

Registry beats the hardcoded paths (an old install lives where the registry points). Duplicate paths collapse
to the highest-precedence provenance. Off Windows there is no registry/retail installer, so detection is the
managed location alone (plus `LauncherConfig.InstallPath` when the install root is overridden — the only
user-configured path mechanism that exists). Each candidate is validated; `SelectBest` returns the
highest-precedence **complete** candidate, else the highest-precedence **incomplete** one (so the UI can
offer a repair), else nothing.

**Completeness validation (`Services/TsoAssetValidator.cs`).** A cheap structural check derived from what the
game actually loads, not invented: `tuning.dat` (the authoritative file every locator tests, and the first
thing `Content.Init` loads) plus the content directories the client scans under the TSOClient base path —
`uigraphics/`, `objectdata/`, `packingslips/`, `sounddata/`. States: **Complete** (all present),
**Incomplete** (some but not all — a truncated extract), **Absent** (empty/unrelated dir). A candidate may
point at the `The Sims Online` **parent** (registry `InstallDir` / managed folder — game appends `\TSOClient\`)
or directly at the `TSOClient` dir (legacy path form); both are resolved.

**Reinstall/repair actions (Installer tab).** Each artifact card has an action:

- **Reinstall OpenSO client** (`ReinstallClientCommand`) — reuses `FsoInstaller`'s atomic staging → verify →
  swap with **`CarryOverUserData`**, so saves, `Content/config.ini`, `NLog.config` and the remesh pack survive.
- **Reinstall / Repair TSO** (`ReinstallTsoCommand`) — the button reads **Repair** when detection is Incomplete.
  If a **complete** install is detected elsewhere (`SelectCopySource` — e.g. a legacy retail copy), it is
  **copied** into the managed location (`TsoInstaller.CopyFromExistingAsync`, validated complete before it's
  trusted) — no 1.27 GB re-download; otherwise the assets are downloaded fresh from the Internet Archive.
  An **Incomplete** state is surfaced visibly (the card shows what's missing) and offers the repair.

**Legacy-install handling — copy, not in-place.** A detected complete legacy install is offered as a *source*
(copied into the managed location), not pointed at in place. This fits the existing managed-sibling
architecture: the game finds TSO by the relative sibling path first (which only works when TSO is a sibling of
the client), off-Windows has no registry to point elsewhere at all, and the registry reset below is meant to
make the *managed* path canonical — an external in-place install would contradict all three.

**Registry reset on (re)install (`Services/RegistryWriter.cs`).** When TSO is installed/repaired into the
managed location, `WriteTsoInstall` (over)writes `SOFTWARE\Maxis\The Sims Online\InstallDir` in **both** views
to the new managed path, so a stale pointer from an old/incomplete install (e.g. the Program Files one) can no
longer win the game's registry lookup. The exact value set is a pure, testable **plan** (`PlanTsoInstall` /
`PlanFsoInstall`) so tests assert it without a real registry. Off Windows this is a no-op (the marker +
sibling layout cover detection), keeping `RegistryWriter`'s existing `OperatingSystem`-branching pattern.

All of this decision logic lives in testable seams (`TsoAssetValidator.Validate`,
`TsoInstallDetector.BuildCandidates`/`SelectBest`/`SelectCopySource`, `RegistryWriter.Plan*`,
`TsoInstaller.CopyFromExistingAsync`) covered by the headless suite with fixture TSO trees
(complete/incomplete/empty), candidate precedence (incl. registry-vs-legacy), the registry value set, and
user-data preservation on reinstall.

## Server status polling & manual refresh

`MainViewModel` runs two independent background polls (adaptive server status, 3s/10s; launcher self-update,
every 6h) plus an on-demand Refresh button on the SERVER STATUS card. Refresh doesn't just nudge those loops
to run sooner — it **explicitly** re-checks both kinds of update: it reloads server status (which recomputes
the game-update state from live data) and, only when the status endpoint didn't answer, falls back to a
lightweight version-only fetch of the client manifest (`FsoInstaller.FetchManifestVersionAsync`) so the "a
game update is required" banner stays truthful instead of silently going quiet while the status API is
unreachable (`DeltaUpdateEngine.ShouldFallBackToManifest` is the testable decision of when that fallback is
worth attempting); it also runs the launcher self-update check via the exact code path the 6h poll uses, so
either one shows the same banner. Both network calls run concurrently so the common path isn't slowed down.
`PollGate` (Wait/Nudge + TryEnter/Release) is the shared, testable primitive both polls use to avoid firing
redundantly right behind a manual Refresh; `IsRefreshing` always clears in a `finally` even when everything
is offline (every check swallows its own errors — no exceptions, no error spam). The card also shows when the
stats on screen were last **successfully** loaded (`LastUpdatedText`, formatted by
`StatusDisplay.FormatLastUpdated` — an absolute local time, not a relative "ago" string, so it needs no extra
UI timer to stay honest); a failed/offline load never advances it, so a stale timestamp next to "Offline" is
an accurate combination, not a bug.

## What works today (tested)

- Resilient downloads (retry/resume/progress, MD5 + SHA-256 verification) — `DownloadService`
- Zip extraction with nested paths + hardened traversal/symlink guard (see policy above) — `ZipExtractor`
- **Cross-platform CAB + MSZIP extraction** (no native deps, no EULA) — `CabExtractor`
- Install-state detection (Windows registry + path fallbacks) — `InstallStateService`
- Dependency-resolved install orchestration — `InstallOrchestrator`
- FSO client install (per-RID manifest → SHA-256-verify → extract → register) — `FsoInstaller` (see "Client update source precedence")
- Headless transactional delta updates (multi-hop chain, per-hop SHA-256 + backup/apply/removals/rollback, version marker last, user-data preservation, automatic full fallback) — `DeltaUpdateEngine` (see "Deltas"); the in-launcher replacement for `update.exe`
- TSO assets install (download → unzip → find Data1.cab → CAB-extract → register) — `TsoInstaller`
- Game → launcher handoff (see "Game → launcher handoff" above): marker write/refresh + best-effort failure
  swallowing (`LauncherHandoff`), `--update-game` arg recognition (`LauncherArgs`), and the update-needed
  decision (`DeltaUpdateEngine.NeedsUpdate`) are all headlessly tested. `MainViewModel.RunUpdateGameHandoffAsync`
  itself (the auto-run-on-startup wiring) is **not** headlessly tested — `MainViewModel`/`App` need the
  Avalonia UI loop and aren't linked into `OpenSO.Launcher.Tests`; it's exercised by running the launcher
  with `--update-game` against a real install.
- Manual-refresh hardening (see "Server status polling & manual refresh" above): the manifest-fallback
  decision (`DeltaUpdateEngine.ShouldFallBackToManifest`), the manifest version-only parse/fetch
  (`FsoInstaller.ParseManifestVersion` / `FetchManifestVersionAsync`), the last-updated caption formatter
  (`StatusDisplay.FormatLastUpdated`), and the poll/reentrancy primitive (`PollGate`) are all headlessly
  tested. `MainViewModel.RefreshStatusAsync`/`RecheckGameUpdateAsync` themselves are **not** — same
  Avalonia-UI-loop reason as `RunUpdateGameHandoffAsync` above.

## Still to port (next slices)

- Mono + SDL installers (runtime deps for macOS/Linux)
- RMS (remesh), Simitone installers
- Launcher self-update (`SelfUpdateService`)
- Game launch (`FSO.Patcher.StartFreeSO` port) + elevation (`ElevationService`)
- Windows registry *write* for game entries (currently a marker file fallback)
