using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Launcher-side port of the game's own update flow (tso.client UpdateController): fetch the server's
/// update feed, compute the delta chain from the installed version to the target (UpdatePath.FindPath),
/// stage the zips into &lt;install&gt;/PatchFiles exactly like AcceptUpdate does, then run the bundled
/// in-game patcher (update.exe). The update is therefore applied by the exact same code path — with
/// the exact same on-disk result, including preserved user files and Content/config.ini — as an
/// update triggered from inside the client. The patcher relaunches the game itself when it finishes.
///
/// This path needs the patcher, which only ships in the Windows client (and the published deltas are
/// win-x64); when it isn't available (macOS/Linux, feed down, unknown installed version) the caller
/// falls back to the full-zip reinstall in FsoInstaller.
/// </summary>
public sealed class GameUpdateService
{
    private readonly LauncherConfig _config;
    private static readonly HttpClient Http = new();

    public GameUpdateService(LauncherConfig config) => _config = config;

    /// <summary>
    /// Attempts the in-client-identical patch update. Returns true when the patcher was run (the
    /// patcher owns the outcome from there — it retries, reverts, and restarts the game itself);
    /// false when this path isn't available and the caller should fall back to a full reinstall.
    /// </summary>
    public async Task<bool> TryPatchUpdateAsync(string installDir, string? installedVersion,
        string targetVersion, IReadOnlyList<string> gameArgs, IProgress<ProgressReport> progress,
        CancellationToken ct = default)
    {
        // The patcher only ships in the Windows client build (see release.yml "Bundle the in-game
        // patcher"); macOS/Linux use the launcher's full-zip path.
        if (!OperatingSystem.IsWindows()) return false;
        var patcherExe = Path.Combine(installDir, "update.exe");
        if (!File.Exists(patcherExe)) return false;
        // Defense in depth: never stage/run the patcher against a live game (the UI guards this too).
        // The patcher would overwrite locked exe/DLLs and leave a half-applied, corrupt install.
        if (GameProcessGuard.IsGameRunning(installDir))
            throw new InvalidOperationException("OpenSO is still running — close the game before updating.");
        // Without a known installed version there's no chain start — an old pre-version.txt install.
        if (string.IsNullOrWhiteSpace(installedVersion)) return false;

        progress.Report(new ProgressReport("update", 0, "Checking for an incremental update…"));
        var updates = await GetUpdateListAsync(ct);
        if (updates == null || updates.Count == 0) return false;

        var path = UpdatePath.FindPath(updates, installedVersion.Trim(), targetVersion.Trim());
        if (path == null || path.Path.Count == 0) return false;

        var downloads = BuildDownloads(path);
        if (downloads == null) return false; // a chain step is missing its zip URL — feed is unusable

        // Stage into <install>/PatchFiles — the same layout UpdateController.AcceptUpdate creates.
        var patchDir = Path.Combine(installDir, "PatchFiles");
        try
        {
            TryDeleteDir(patchDir); // never mix with stale patch state
            Directory.CreateDirectory(patchDir);

            // clean.txt tells the patcher this chain starts from a full zip (it then clears stray
            // files directly in Content/Patch) — identical to AcceptUpdate.
            if (path.FullZipStart)
                File.WriteAllText(Path.Combine(patchDir, "clean.txt"), "CLEAN");

            for (int i = 0; i < downloads.Count; i++)
            {
                var (url, name) = downloads[i];
                RemoteUrl.RequireHttps(url, $"update file {name}");
                double lo = 0.85 * i / downloads.Count, hi = 0.85 * (i + 1) / downloads.Count;
                progress.Report(new ProgressReport("update", lo, $"Downloading {name}…"));
                var dl = new DownloadService(url, Path.Combine(patchDir, name));
                await dl.RunAsync(ProgressScaler.Scale(progress, "update", lo, hi, $"Downloading {name}… "), ct);
            }
        }
        catch (Exception ex)
        {
            // A half-staged PatchFiles dir would be picked up (and applied!) by the next patcher
            // run, in-client or otherwise — remove it, then let the caller fall back / report.
            // Retried: a transiently-locked file here (AV scan, lagging handle) would otherwise
            // leave the stale dir behind for the next patcher run to corrupt the install with.
            Log.Warn("Staging the incremental update failed; cleaning PatchFiles and falling back", ex);
            TryDeleteDirWithRetry(patchDir);
            throw;
        }

        // Run the patcher from the install dir with the game's launch args — exactly what the
        // client's RestartGamePatch does — and wait for it to finish (it restarts the game itself).
        progress.Report(new ProgressReport("update", 0.85, "Running the game patcher…"));
        var psi = new ProcessStartInfo
        {
            FileName = patcherExe,
            WorkingDirectory = installDir,
            UseShellExecute = false,
        };
        foreach (var a in gameArgs) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the game patcher (update.exe).");
        await proc.WaitForExitAsync(ct);
        progress.Report(new ProgressReport("update", 1.0, "Patcher finished."));
        return true;
    }

    /// <summary>Fetches the update feed the in-client updater uses. Null when unreachable/invalid.</summary>
    public async Task<List<ApiUpdate>?> GetUpdateListAsync(CancellationToken ct = default)
    {
        try
        {
            var url = _config.ApiBaseUrl.TrimEnd('/') + "/userapi/update";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<List<ApiUpdate>>(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) { Log.Warn("Update feed unavailable; the game-update path will fall back to a full reinstall", ex); return null; }
    }

    /// <summary>
    /// The download set for a chain — mirrors UpdateController.BuildFiles: step i lands at
    /// PatchFiles/path{i}.zip (the full zip for step 0 of a full-zip start, the incremental
    /// otherwise) plus its manifest at path{i}.json. Null if any step is missing its zip URL.
    /// </summary>
    internal static List<(string Url, string Name)>? BuildDownloads(UpdatePath path)
    {
        var result = new List<(string, string)>();
        for (int i = 0; i < path.Path.Count; i++)
        {
            var item = path.Path[i];
            var zip = (i == 0 && path.FullZipStart) ? item.full_zip : item.incremental_zip;
            if (zip == null) return null;
            result.Add((zip, $"path{i}.zip"));
            if (item.manifest_url != null)
                result.Add((item.manifest_url, $"path{i}.json"));
        }
        return result;
    }

    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private static void TryDeleteDirWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { if (!Directory.Exists(path)) return; Directory.Delete(path, true); return; }
            catch { Thread.Sleep(500 * (attempt + 1)); }
        }
        TryDeleteDir(path); // last best-effort attempt
    }

    /// <summary>
    /// Startup sweep: removes a leftover &lt;install&gt;/PatchFiles dir. One only exists when a previous
    /// patch run failed AND its cleanup couldn't delete it (locked files) — the patcher would happily
    /// apply that stale/partial chain on its next run and corrupt the install. Safe to call any time
    /// the patcher isn't running; TryPatchUpdateAsync re-stages a fresh dir for every update anyway.
    /// </summary>
    public static void SweepStalePatchFiles(string installDir)
    {
        try
        {
            var patchDir = Path.Combine(installDir, "PatchFiles");
            if (Directory.Exists(patchDir)) TryDeleteDirWithRetry(patchDir);
        }
        catch { /* best effort */ }
    }
}
