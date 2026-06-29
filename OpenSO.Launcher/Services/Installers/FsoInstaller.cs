using System;
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
        var tempZip = Path.Combine(Path.GetTempPath(), $"openso-client-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.zip");
        var dl = new DownloadService(zipUrl, tempZip);
        await dl.RunAsync(Scale(progress, "client", 0.00, 0.70), ct); // downloads = first 70%

        try
        {
            // Step 3 + 4: ensure dir, extract.
            Directory.CreateDirectory(installPath);
            await ZipExtractor.ExtractAsync(tempZip, installPath,
                Scale(progress, "client", 0.70, 0.92, "Extracting client files… "), preservePermissions: false, ct);

            // Step 5: register the install.
            progress.Report(new ProgressReport("client", 0.93, "Registering install…"));
            _registerInstall?.Invoke(Code, installPath);

            progress.Report(new ProgressReport("client", 1.0, "Installation finished."));
        }
        finally
        {
            TryDelete(tempZip); // matches fso.js end() -> dl.cleanup()
        }
    }

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

        // 2) GitHub releases fallback. A dev-N release publishes one client asset PER platform
        //    (OpenSO-client-win-x64.zip, -linux-x64, -osx-x64, -osx-arm64), so "contains client" alone
        //    isn't enough — it would grab whichever OS's zip is listed first. Prefer the asset whose name
        //    also contains THIS machine's RID; only fall back to the first "client" asset if none match.
        try
        {
            using var ghResp = await GetAsync(_config.ClientReleaseFeed, ct);
            ghResp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await ghResp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var rid = CurrentRid();
                string? genericClient = null;
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null || !name.Contains("client", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!asset.TryGetProperty("browser_download_url", out var url)) continue;
                    var u = url.GetString();
                    if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)) return u; // exact platform match
                    genericClient ??= u; // remember the first client asset as a last resort
                }
                if (genericClient != null) return genericClient;
            }
        }
        catch { /* no URL */ }

        return null;
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
