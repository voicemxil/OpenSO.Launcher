# OpenSO Launcher — roadmap / TODO

Consolidated list of remaining launcher work. ✅ = done, 🔜 = next, ⏸ = deferred.

## Live data / homepage
- ✅ Live stats panel — players online, lots online, in-game clock, server-status pill, game version (polls `/userapi/status`).
- ✅ Busiest-lots list (name + player count); news moved to the bottom.
- 🔜 **Lot thumbnails in busiest-lots** — show each lot's rendered PNG via `{api}/userapi/city/{shardId}/{location}.png` (topLots already has shardId+location; no server change). Needs an async image loader in Avalonia + a placeholder for lots without a render.
- ⏸ Character portraits — NOT feasible: no server-side avatar portrait endpoint (sim faces are client-rendered).

## Updating
- ✅ Launcher self-update (release CI feed + swap-and-relaunch).
- ✅ In-game incremental delta updates (semver `prev`-detection bug fixed + backfill workflow).
- ✅ Game auto-update by server version — installed `version.txt` vs `/userapi/status` `gameVersion`; UPDATE GAME button on mismatch.
- ✅ **Launcher updates = in-client updates** — the launcher downloads the incremental delta chain from `/userapi/update` into `PatchFiles/` and runs the game's own patcher (`update.exe`), identical to an update triggered in-client (GameUpdateService). Falls back to the full-zip atomic reinstall when the patcher/chain isn't available (macOS/Linux — no published deltas or patcher — old installs, feed down); that path now preserves user data (saves/remesh/config) like the patcher would, and verification accepts the macOS code-only `OpenSO.app` layout (the v0.1.11-era Mac install/update breaker).

## Packaging / distribution
- ✅ Per-RID self-contained zips (win/linux/osx-x64/osx-arm64); Windows Inno installer; macOS `.app` + DMG; install to `%LOCALAPPDATA%`.
- 🔜 **Liquid Glass app icon** — commit `openso.icon` (Icon Composer, from the `openso-branding` repo) to `OpenSO.Launcher/Assets/`; CI compiles it via `actool`. (In progress in a separate session.)
- 🔜 **Cut a launcher release** to ship the homepage/stats/clock + install-location/sidebar work in downloadable builds.
- ⏸ macOS notarization — needs a paid Apple Developer ID; deferred. Instead: a "first launch on Mac (right-click → Open / Open Anyway)" note on the openso.org download page.
- ⏸ Linux `.deb` / AppImage — zips cover Linux for now.

## UX / polish
- ✅ Sidebar active-tab indicator fix; in-game clock instead of UTC.
- 🔜 Installer cards: show a checkmark for installed components instead of raw `True`/`False`.
- 🔜 Real GUI test pass (the launcher hasn't been driven by an agent — needs a human run-through of install → play → settings).
- 🔜 Discord link — `OpenDiscord` currently opens openso.org; point it at the real Discord invite once the app is registered.
- 🔜 Verify Settings actually apply on launch (graphics/3D/refresh) and the notifications panel behaves.
