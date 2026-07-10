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
  already covers the platform-specific *publish* step separately).
- **`.github/workflows/release.yml`** — gates the release on the same build+test: a `test` job (identical
  build+test steps) must succeed before the per-RID `build`/publish job runs, which in turn gates the
  `release` job that publishes the GitHub Release (`needs: test` → `needs: build`). A release can never
  be cut from a red suite. The `test` job's stdout is captured to `test-results.log`, echoed into the
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
that needs no installed .NET runtime:

```bash
cd OpenSO.Launcher

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

## What works today (tested)

- Resilient downloads (retry/resume/progress, MD5 + SHA-256 verification) — `DownloadService`
- Zip extraction with nested paths + hardened traversal/symlink guard (see policy above) — `ZipExtractor`
- **Cross-platform CAB + MSZIP extraction** (no native deps, no EULA) — `CabExtractor`
- Install-state detection (Windows registry + path fallbacks) — `InstallStateService`
- Dependency-resolved install orchestration — `InstallOrchestrator`
- FSO client install (per-RID manifest → SHA-256-verify → extract → register) — `FsoInstaller` (see "Client update source precedence")
- Headless transactional delta updates (multi-hop chain, per-hop SHA-256 + backup/apply/removals/rollback, version marker last, user-data preservation, automatic full fallback) — `DeltaUpdateEngine` (see "Deltas"); the in-launcher replacement for `update.exe`
- TSO assets install (download → unzip → find Data1.cab → CAB-extract → register) — `TsoInstaller`

## Still to port (next slices)

- Mono + SDL installers (runtime deps for macOS/Linux)
- RMS (remesh), Simitone installers
- Launcher self-update (`SelfUpdateService`)
- Game launch (`FSO.Patcher.StartFreeSO` port) + elevation (`ElevationService`)
- Windows registry *write* for game entries (currently a marker file fallback)
