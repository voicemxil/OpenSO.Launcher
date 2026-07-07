# OpenSO Launcher — Improvement Roadmap

A prioritized backlog of security, correctness, efficiency, and architecture work for
`OpenSO.Launcher`. Compiled from a three-pass review (security / bugs / efficiency) on
2026-07-07. Each item lists the file:line anchor, the concrete failure it prevents, and a
suggested fix so a future agent can pick it up cold.

For product/feature work (homepage, packaging, UX polish) see [LAUNCHER_TODO.md](LAUNCHER_TODO.md).
This file is the *engineering health* backlog.

Legend — Effort: **S** ≈ <1h, **M** ≈ a few hours, **L** ≈ a day+.
Status: `[ ]` open · `[~]` in progress · `[x]` done.

---

## P0 — Do before the next public release

These are exploitable or cause silent data loss / dead UI.

- [x] **Verify the self-update download** — DONE 2026-07-07: `SelfUpdateService` now reads the GitHub
  release asset's `digest` field (`sha256:<hex>`) and passes it to `DownloadService`, which gained
  SHA-256 verification (deletes + throws `ChecksumMismatchException` on mismatch). Note: verification
  only applies when GitHub supplies a digest (all assets uploaded since 2025 have one) — a follow-up
  could *require* a digest and fail closed.

- [x] **Verify the game-client download** — DONE 2026-07-07: `FsoInstaller` and `RmsInstaller` carry the
  picked asset's `digest` through to `DownloadService` (see `PickFullClientAsset` digest overload).
  The OpenSO-API path honors an optional `full_zip_sha256` / `sha256` field — **server-side follow-up:
  publish those fields in the API feed**, and the community remesh mirror remains unverified (no
  published hash exists).

- [x] **Fix `async void` background loops that die silently** — DONE 2026-07-07: `StartClock` /
  `StartStatusPolling` now catch per-iteration failures and keep looping; only shutdown cancellation exits.

- [x] **Make settings writes atomic** — DONE 2026-07-07: `LauncherSettings.Save` serializes, then
  write-temp + atomic rename under a lock.

- [x] **Stop shell-concatenating elevated commands** — DONE 2026-07-07: added `ElevationService.ShQuote`
  (POSIX single-quote escaping, unit-tested) and used it for the .dmg/.pkg paths in `SdlInstaller` /
  `MonoInstaller`. The Windows `cmd.exe /c` elevation path is currently unused (elevation is only
  invoked on macOS/Linux) — harden it with proper cmd quoting before any Windows caller is added.

## P1 — Fix before public beta

- [x] **Await initial state before it can be misread** — DONE 2026-07-07: startup is now
  `InitializeAsync` = refresh → PatchFiles sweep → start status polling; `RefreshAsync` never throws
  and reports "Unknown — couldn't check the install" instead of guessing.

- [x] **Case-insensitive zip/cab-slip check on Windows** — DONE 2026-07-07: both extractors compare with
  `OrdinalIgnoreCase` on Windows, append a trailing separator (fixes the same-prefix-sibling bypass,
  e.g. `C:\inst` vs `C:\inst-evil`), and directory entries are now guarded too. Unit-tested.

- [x] **Share/static-ify HttpClient instances** — DONE 2026-07-07: `StatusService`, `NewsService`,
  `SelfUpdateService` now use a static shared client like `DownloadService`.

- [x] **Robust game-launch success detection** — DONE 2026-07-07: a clean early exit (code 0) is treated
  as a hand-off to a detached child, not a crash; only a non-zero early exit reports failure.

- [x] **Clean up stale PatchFiles on failure and startup** — DONE 2026-07-07: failure cleanup retries
  with backoff; `GameUpdateService.SweepStalePatchFiles` runs at launcher startup.

- [x] **Add a disk-space pre-check to FsoInstaller** — DONE 2026-07-07: 1.5 GB pre-flight on both the
  install volume and temp, mirroring `TsoInstaller.EnsureFreeSpace`.

- [x] **Guard install/refresh with a mutex** — DONE 2026-07-07: a `SemaphoreSlim` install gate serializes
  the orchestrator runs (FSO/RMS/game-update) and the `GetInstalledAsync` probe inside `RefreshAsync`.

## P2 — Reliability & observability (do together, high leverage)

- [x] **Add real logging + crash reporting** — DONE 2026-07-07: hand-rolled zero-dependency
  [`Services/Log.cs`](OpenSO.Launcher/Services/Log.cs) (chose this over Serilog to keep the
  self-contained binary lean and match the codebase's no-deps ethos). Daily file under
  `<AppData>/OpenSO Launcher/logs/`, 7-day pruning, session header (version/OS/runtime).
  `Program.Main` installs `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`
  handlers and wraps the run in a fatal-logging try. The formerly-silent `catch {}` sites in the
  installers, `InstallStateService`, `GameUpdateService`, `InstallOrchestrator`, and the MainViewModel
  install/update/launch failures now log.

- [x] **Validate remote-feed URLs before use** — DONE 2026-07-07: [`RemoteUrl.RequireHttps`](OpenSO.Launcher/Services/RemoteUrl.cs)
  gates every feed-derived download (self-update asset, client zip, remesh zip, each game-update file)
  to absolute HTTPS before it reaches `DownloadService`. Unit-tested. (Config-sourced TSO/Mono/SDL URLs
  are intentionally not gated, so operators can self-host on an internal mirror.)

- [x] **Unpredictable temp filenames** — DONE 2026-07-07: [`TempFiles.NewDir`](OpenSO.Launcher/Services/TempFiles.cs)
  gives each install/update a random-named dir under an app-owned temp root (0700 on POSIX). All
  installers + self-update use it. Unit-tested.

- [x] **Graceful shutdown for background work** — DONE 2026-07-07: installs/updates now run under
  `_shutdownCts.Token` (they were `ct=default`, i.e. uncancellable); `MainViewModel.Shutdown` cancels
  and waits up to 3s for the in-flight op to unwind before `Program`'s `Environment.Exit`. Since FSO
  now stages+swaps and TSO/RMS clean their temp dirs, an abrupt kill no longer guts a live install.
  *Follow-up: tighter socket-level read timeouts in `DownloadService` (still relies on the 120s stall).*

- [x] **Robust version comparison** — DONE 2026-07-07: `MainViewModel.SameVersion` pads both versions to
  four components and compares via `System.Version` (so `1.2.3` == `1.2.3.0` — note a naive `Version`
  compare still fails this because unspecified revision is `-1`), with a string-equality fallback.

- [ ] **Atomic staging for TSO and RMS installs** — STILL OPEN. `FsoInstaller` uses stage→verify→swap, but
  `TsoInstaller` CAB-extracts directly into the live dir and `RmsInstaller` copies file-by-file. An
  interruption still corrupts the live install (graceful-shutdown cancellation reduces but doesn't
  eliminate the window — an OS kill / power loss mid-extract still bisects the install). Deferred as its
  own pass: it changes disk/perf characteristics (a second ~2 GB move for TSO) and needs real-install
  verification, not just unit tests. Fix: extract/copy into staging, verify, then atomic move. Effort: **M** each.

## P3 — Architecture (schedule when touching these areas)

- [ ] **Split the god-object ViewModel** — `MainViewModel.cs` (~443 lines, ~78 observable props, 3 loops)
  drives every section. Fix: `HomeViewModel` / `InstallerViewModel` / `DownloadsViewModel` /
  `SettingsViewModel` + a thin `ShellViewModel` for nav. Effort: **L**.

- [ ] **Introduce a DI container** — services are `new`'d inline in the VM ctor, blocking mock injection
  in tests. Wire a container (built-in `ServiceCollection`, or Splat) in `App.axaml.cs`. Effort: **M**.

- [ ] **De-duplicate shared helpers** — the `Scale()` progress-band helper is copy-pasted across
  `FsoInstaller`, `TsoInstaller`, `RmsInstaller`, `GameUpdateService`; GitHub `HttpRequestMessage` setup
  (User-Agent + token) is duplicated in `FsoInstaller`/`RmsInstaller`. Extract `ProgressScaler` and an
  HTTP-request helper. Effort: **S**.

## P4 — Efficiency & UX polish

- [ ] **Indeterminate progress when size is unknown** — `TsoInstaller.cs:57` (no Content-Length) shows 0%
  for a multi-GB download; users think it's hung. Add an `IsIndeterminate` flag on `ProgressReport`. **S**.
- [ ] **Byte-based extraction progress** — Zip/Cab extractors report by file count, so a single 500 MB
  file jumps 1%→100%. Emit progress every ~10 MB. **M**.
- [ ] **Visible retry feedback** — `DownloadService` retries 15× silently (up to ~75s). Surface
  "Retrying (N/15)…" and add a global give-up timeout to fail fast on a dead server. **S**.
- [ ] **News fetch retry / cache** — `NewsService` gives up after one transient failure for the whole
  session; add 1–2 retries or cache the last good feed. **S**.
- [ ] **Cache/parallelize install-state probe** — `InstallStateService` scans registry+FS on every launch;
  cache briefly or parallelize to cut "Checking…" time. **M**.
- [ ] **Larger download buffer** — bump `DownloadService` buffer 80 KB → 256 KB for fewer syscalls on fast
  links. **S**.

## P5 — Build / CI

- [ ] **Run the unit tests in CI** — `release.yml` builds and publishes but does not run
  `OpenSO.Launcher.Tests`. A regression (e.g. `SwapIntoPlace`) can ship. Add a test step before publish. **S**.
- [ ] **Enable ReadyToRun** — `<PublishReadyToRun>true</PublishReadyToRun>` for faster startup. **S**.
- [ ] **Evaluate trimming** — `<PublishTrimmed>` could roughly halve the self-contained size (~80–120 MB →
  ~40–60 MB); needs Avalonia reflection roots + per-platform testing. **M**.
- [ ] **Track publish size in CI** — report + flag >10% growth per release. **S**.
- [ ] **Cross-platform test run** — run the logic tests on each RID (esp. macOS code-only bundle). **M**.

## Missing test coverage (add alongside the fixes above)

- Concurrent `LauncherSettings.Save` (corruption) · atomic write behavior.
- Corrupt-settings-JSON recovery on `Load`.
- `FsoInstaller.SwapIntoPlace` failure/rollback paths.
- `GameUpdateService` patcher-failure + PatchFiles cleanup.
- Download stall-timeout / resume / Range-not-supported restart.
- Offline-mode integration (status/news/update endpoints down).
- Background-task cancellation on shutdown.

---

## Already solid (don't redo)

- Atomic stage→verify→swap install with user-data carry-over (`FsoInstaller`).
- Resilient downloads: 15 retries w/ backoff, Range-resume, MD5 verify *when a hash is supplied*,
  ~250ms progress cadence.
- Incremental game updates reuse the in-client delta chain with full-reinstall fallback.
- Cross-platform path/registry/permission handling; macOS code-only `OpenSO.app` layout.
- 20 headless unit tests (extraction, asset picking, version compare, swap, carry-over).
- Offline-first graceful degradation.
