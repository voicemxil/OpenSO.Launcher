using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Extraction;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Port of lib/installers/fso.js — installs the OpenSO client.
/// Steps mirror the upstream:
///   1. Resolve the client zip URL (OpenSO API first, GitHub release assets as fallback)
///   2. Download it (resilient DownloadService)
///   3. Create the install directory
///   4. Extract the zip into it
///   5. Register the install (registry on Windows / local config elsewhere)
/// The client is self-contained native .NET, so there's no separate runtime/MacExtras step.
/// </summary>
public sealed class FsoInstaller : IComponentInstaller
{
    public string Code => "FSO";

    private readonly LauncherConfig _config;
    private readonly Action<string, string>? _registerInstall; // (code, installDir) -> write registry/local entry
    private static readonly HttpClient Http = new();

    public FsoInstaller(LauncherConfig config, Action<string, string>? registerInstall = null)
    {
        _config = config;
        _registerInstall = registerInstall;
    }

    public async Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        // Step 1: resolve the zip URL.
        progress.Report(new ProgressReport("client", 0, "Locating the latest client…"));
        var zipUrl = await ResolveClientZipUrlAsync(ct)
            ?? throw new InvalidOperationException("Could not obtain OpenSO client release information.");

        // Step 2: download.
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tempZip = Path.Combine(Path.GetTempPath(), $"openso-client-{stamp}.zip");
        var dl = new DownloadService(zipUrl, tempZip);
        await dl.RunAsync(Scale(progress, "client", 0.00, 0.70), ct); // downloads = first 70%

        // ATOMIC UPDATE. Never extract directly into the live install dir: an interrupted extract (network
        // drop, I/O error, cancellation, crash) would leave it half-gutted with the self-contained .NET
        // runtime files deleted and no way back — an unlaunchable client ("install .NET Desktop Runtime").
        // Instead: extract into a sibling STAGING dir, verify it's a complete client, then swap it into
        // place (moving the old install aside as a BACKUP first, restoring it if the swap fails). Staging
        // and backup are siblings of installPath so the moves are same-volume atomic renames.
        var name = Path.GetFileName(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parent = Directory.GetParent(installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName
                     ?? Path.GetTempPath();
        var staging = Path.Combine(parent, $".{name}.staging-{stamp}");
        var backup = Path.Combine(parent, $".{name}.backup-{stamp}");

        try
        {
            // Step 3 + 4: extract into STAGING. Preserve unix permissions so the native macOS/Linux apphost
            // (and OpenSO.app's executable) keep their +x bit — otherwise the game can't launch.
            TryDeleteDir(staging);
            await ZipExtractor.ExtractAsync(tempZip, staging,
                Scale(progress, "client", 0.70, 0.90, "Extracting client files… "), preservePermissions: true, ct);

            // Step 4b: verify the staged client is COMPLETE before we touch the live install. This is the
            // guard against shipping/keeping a truncated extract (the exact failure that gutted the runtime).
            progress.Report(new ProgressReport("client", 0.91, "Verifying client files…"));
            VerifyClientInstall(staging);

            // Step 4c: atomic swap (old -> backup, staging -> live; restore backup on failure).
            progress.Report(new ProgressReport("client", 0.94, "Finalizing update…"));
            bool hadPrevious = Directory.Exists(installPath);
            SwapIntoPlace(staging, installPath, backup);

            // Step 4d: the in-game patcher (update.exe) EXTRACTS a release over the existing install,
            // so anything the release doesn't ship survives an update — saves, the remesh pack, the
            // mesh cache — and the user's Content/config.ini is never overwritten. The swap above
            // replaced the whole folder, so restore those from the old install (now the backup) to
            // land on the same end state the patcher would have produced.
            if (hadPrevious)
            {
                progress.Report(new ProgressReport("client", 0.95, "Restoring user data…"));
                CarryOverUserData(backup, installPath);
            }

            // Step 5: register the install.
            progress.Report(new ProgressReport("client", 0.97, "Registering install…"));
            _registerInstall?.Invoke(Code, installPath);

            progress.Report(new ProgressReport("client", 1.0, "Installation finished."));
        }
        catch
        {
            // Live install is either untouched (failure before swap) or already restored (failure during
            // swap). Only the staging dir may remain — drop it. The backup is cleaned up in finally.
            TryDeleteDir(staging);
            throw;
        }
        finally
        {
            TryDelete(tempZip);   // matches fso.js end() -> dl.cleanup()
            TryDeleteDir(backup); // on success this is the old install; on a restored failure it's already gone
        }
    }

    /// <summary>
    /// Throws if <paramref name="dir"/> doesn't look like a complete self-contained OpenSO client — a
    /// truncated extract is exactly what deleted the bundled .NET runtime and left an unlaunchable install.
    /// Checks a sane minimum file count plus the presence of the managed entry, the bundled runtime host,
    /// and the apphost. Two valid layouts: the flat win/linux one (code at the install root), and the
    /// macOS CODE-ONLY bundle (apphost + runtime + managed DLLs inside OpenSO.app/Contents/MacOS, with
    /// only Content/ and version.txt at the root — see the client's CreateMacAppBundle target).
    /// </summary>
    internal static void VerifyClientInstall(string dir)
    {
        int fileCount = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        const int minFiles = 80; // a complete self-contained .NET client is ~200 files; far fewer = truncated
        if (fileCount < minFiles)
            throw new IOException($"Client extract looks incomplete ({fileCount} files, expected >= {minFiles}); refusing to install a broken client.");

        var macCode = Path.Combine("OpenSO.app", "Contents", "MacOS");
        bool Has(string n) => File.Exists(Path.Combine(dir, n)) || File.Exists(Path.Combine(dir, macCode, n));
        if (!Has("OpenSO.dll"))
            throw new IOException("Client extract is missing OpenSO.dll (managed entry) — incomplete download.");
        if (!(Has("hostfxr.dll") || Has("libhostfxr.so") || Has("libhostfxr.dylib")
              || Has("coreclr.dll") || Has("libcoreclr.so") || Has("libcoreclr.dylib")))
            throw new IOException("Client extract is missing the bundled .NET runtime (hostfxr/coreclr) — incomplete download.");
        if (!(Has("OpenSO.exe") || Has("OpenSO")))
            throw new IOException("Client extract is missing the OpenSO apphost — incomplete download.");
    }

    /// <summary>
    /// Atomically replaces the install at <paramref name="installPath"/> with the verified <paramref name="staging"/>
    /// dir: moves any existing install aside to <paramref name="backup"/> first, then moves staging into place.
    /// If the final move fails, the old install is restored from backup. Same-volume Directory.Move is an
    /// atomic rename, so the live path is never in a half-written state.
    /// </summary>
    internal static void SwapIntoPlace(string staging, string installPath, string backup)
    {
        bool hadOld = Directory.Exists(installPath);
        if (hadOld) Directory.Move(installPath, backup);
        try
        {
            Directory.Move(staging, installPath);
        }
        catch
        {
            // Restore the previous install so a swap failure never leaves the user with nothing.
            if (hadOld && !Directory.Exists(installPath) && Directory.Exists(backup))
                Directory.Move(backup, installPath);
            throw;
        }
    }

    /// <summary>
    /// Restores user-owned files from the pre-update install (<paramref name="oldInstall"/>) into the
    /// freshly-swapped <paramref name="newInstall"/>, mirroring the in-game patcher's semantics: files
    /// the new release doesn't ship are kept (saves, remesh .fsom meshes, mesh cache), the user's
    /// Content/config.ini and NLog.config are kept over the shipped defaults (the patcher's
    /// IgnoreFiles), and stray files directly in Content/Patch are dropped like a clean full-zip patch
    /// does (subfolders, e.g. translations, are kept). Code and patcher state are never carried over.
    /// Best-effort per file — a single unrestorable file must not fail the install.
    /// </summary>
    internal static void CarryOverUserData(string oldInstall, string newInstall)
    {
        if (!Directory.Exists(oldInstall)) return;
        // With the code-only macOS bundle, anything code-like at the OLD root is a legacy flat-layout
        // leftover (bare runtime next to the .app) — resurrecting it would just shadow the bundle.
        bool newIsMacBundle = Directory.Exists(Path.Combine(newInstall, "OpenSO.app"));

        foreach (var src in Directory.EnumerateFiles(oldInstall, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(oldInstall, src).Replace('\\', '/');
            string top = rel.Split('/')[0];

            if (top == "OpenSO.app") continue;                        // code-only bundle stays pristine
            if (top is "PatchFiles" or "updateBackup") continue;      // stale patcher state
            if (top.StartsWith('.')) continue;                        // markers / staging leftovers (.openso-install is rewritten below)
            if (newIsMacBundle && !rel.Contains('/') && IsRootCodeFile(rel)) continue;
            if (rel.StartsWith("Content/Patch/", StringComparison.OrdinalIgnoreCase) &&
                rel.Count(c => c == '/') == 2) continue;              // clean-patch behavior: drop stray top-level patch files

            try
            {
                // The patcher never overwrites these (IgnoreFiles) — keep the USER's copy. Everything
                // else only fills gaps: files the new release ships always win.
                bool keepUserCopy = rel.Equals("Content/config.ini", StringComparison.OrdinalIgnoreCase)
                                 || rel.Equals("NLog.config", StringComparison.OrdinalIgnoreCase);
                var dst = Path.Combine(newInstall, rel);
                if (!keepUserCopy && File.Exists(dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
            catch { /* best effort */ }
        }
    }

    private static bool IsRootCodeFile(string name)
    {
        if (name is "OpenSO" or "createdump") return true; // extensionless unix apphost/diagnostics
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".dll" or ".dylib" or ".so" or ".exe" or ".pdb" or ".json" or ".xml";
    }

    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    /// <summary>
    /// Port of fso.js getZipUrl(): try the OpenSO API release feed first (expects an array with a
    /// `full_zip`), then fall back to the GitHub releases API, picking the asset whose name contains
    /// "client". Repointed at OpenSO endpoints via LauncherConfig.
    /// </summary>
    private async Task<string?> ResolveClientZipUrlAsync(CancellationToken ct)
    {
        // 1) OpenSO API (array of releases, [0].full_zip)
        try
        {
            using var apiResp = await GetAsync(_config.ClientManifestUrl, ct);
            if (apiResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                JsonElement first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                    ? root[0] : root;
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("full_zip", out var fz) && fz.ValueKind == JsonValueKind.String)
                    return fz.GetString();
            }
        }
        catch { /* fall through to GitHub */ }

        // 2) GitHub releases fallback. See PickFullClientAsset for the selection rules (per-platform +
        //    must be the FULL client zip, not the incremental delta / manifest).
        try
        {
            using var ghResp = await GetAsync(_config.ClientReleaseFeed, ct);
            ghResp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await ghResp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(string? name, string? url)>();
                foreach (var asset in assets.EnumerateArray())
                    list.Add((asset.TryGetProperty("name", out var n) ? n.GetString() : null,
                              asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null));
                var picked = PickFullClientAsset(list, CurrentRid());
                if (picked != null) return picked;
            }
        }
        catch { /* no URL */ }

        return null;
    }

    /// <summary>
    /// Picks the FULL client zip for <paramref name="rid"/> from a release's assets. A release publishes
    /// several assets whose names contain "client" AND the RID: the full client zip
    /// (OpenSO-client-win-x64.zip), the incremental delta zip (…-win-x64.incremental.zip), and its manifest
    /// (…-win-x64.manifest.json). The delta is only the CHANGED files and the manifest isn't even a zip —
    /// installing either yields a broken, verification-failing client (and an "update again" loop). So match
    /// ONLY a real full ".zip" that is NOT the incremental, preferring the exact-RID asset then any full
    /// client zip. (Before incremental deltas existed there was one matching asset, so the old "first name
    /// contains client+rid" logic happened to work.)
    /// </summary>
    internal static string? PickFullClientAsset(IEnumerable<(string? name, string? url)> assets, string rid)
    {
        string? generic = null;
        foreach (var (name, url) in assets)
        {
            if (name == null || url == null) continue;
            if (!name.Contains("client", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;       // excludes .manifest.json
            if (name.Contains("incremental", StringComparison.OrdinalIgnoreCase)) continue; // excludes the delta zip
            if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)) return url;         // exact platform match
            generic ??= url;                                                                // any full client zip
        }
        return generic;
    }

    /// <summary>
    /// This machine's OpenSO release RID — matches the release asset suffixes
    /// (win-x64, linux-x64, osx-x64, osx-arm64).
    /// </summary>
    private static string CurrentRid()
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    private static Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
        if (url.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_RATELIMIT_TOKEN");
            if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        }
        return Http.SendAsync(req, ct);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>Maps a child operation's 0..1 progress into a [lo,hi] band of the overall stage.</summary>
    private static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage,
        double lo, double hi, string? prefix = null) =>
        new Progress<ProgressReport>(r =>
            outer.Report(new ProgressReport(stage, lo + (hi - lo) * r.Fraction,
                prefix != null ? prefix + (r.Detail ?? "") : r.Detail)));
}
