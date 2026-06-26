using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Extraction;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Port of lib/installers/rms.js — installs the Remesh ("3D mesh") package: a set of high-quality
/// .fsom replacement meshes the client uses in 3D mode. Upstream downloaded a zip and unpacked it
/// into the FreeSO client's Content/MeshReplace folder; the renderer (RCMeshProvider) probes
/// &lt;Content&gt;/MeshReplace/ for &lt;obj&gt;_&lt;dgrp&gt;.fsom replacements, so that's exactly where these land.
///
/// RMS augments the client rather than installing into a folder of its own under the install root.
/// It depends on FSO, so the FSO install dir is resolved from state and the orchestrator-supplied
/// <c>installPath</c> (which would be &lt;root&gt;/RMS) is intentionally ignored.
/// </summary>
public sealed class RmsInstaller : IComponentInstaller
{
    public string Code => "RMS";

    private readonly LauncherConfig _config;
    private readonly InstallStateService _installState;
    private static readonly HttpClient Http = new();

    public RmsInstaller(LauncherConfig config, InstallStateService installState)
    {
        _config = config;
        _installState = installState;
    }

    public async Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        // The remeshes augment the client, so they go into the FSO client's Content/MeshReplace folder.
        var fso = await _installState.GetInstallStatusAsync("FSO");
        if (fso.Path == null)
            throw new InvalidOperationException("Install the OpenSO client before the 3D mesh pack.");
        var meshReplace = Path.Combine(fso.Path, "Content", "MeshReplace");

        progress.Report(new ProgressReport("rms", 0, "Locating the 3D mesh pack…"));
        var zipUrl = await ResolveRemeshZipUrlAsync(ct)
            ?? throw new InvalidOperationException("No 3D mesh pack is available from the OpenSO server yet.");

        var tempZip = Path.Combine(Path.GetTempPath(), $"openso-remesh-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.zip");
        try
        {
            var dl = new DownloadService(zipUrl, tempZip);
            await dl.RunAsync(Scale(progress, "rms", 0.00, 0.85, "Downloading 3D meshes… "), ct);

            // The package is a flat set of .fsom files; drop them straight into MeshReplace/.
            Directory.CreateDirectory(meshReplace);
            await ZipExtractor.ExtractAsync(tempZip, meshReplace,
                Scale(progress, "rms", 0.85, 0.99, "Installing 3D meshes… "), preservePermissions: false, ct);

            // Mark it installed and record the source, so a future version check can compare/refresh.
            try { File.WriteAllText(Path.Combine(meshReplace, ".openso-remesh"), $"RMS\n{zipUrl}\n{DateTimeOffset.UtcNow:o}\n"); } catch { }

            progress.Report(new ProgressReport("rms", 1.0, "3D mesh pack installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// Resolve the remesh zip URL: the OpenSO ResourceCentral "3DModels" entry first (either a direct
    /// zip/redirect or a small JSON manifest with a {full_zip|url|remesh} field), then a GitHub
    /// client-release asset whose name mentions "remesh"/"mesh"/"3d" as a fallback.
    /// </summary>
    private async Task<string?> ResolveRemeshZipUrlAsync(CancellationToken ct)
    {
        if (_config.ResourceCentral.TryGetValue("3DModels", out var central) && !string.IsNullOrWhiteSpace(central))
        {
            try
            {
                using var resp = await GetAsync(central, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (ctype.Contains("json"))
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                        var root = doc.RootElement;
                        foreach (var key in new[] { "full_zip", "url", "remesh" })
                            if (root.ValueKind == JsonValueKind.Object &&
                                root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                                return v.GetString();
                    }
                    else
                    {
                        // Direct file (zip / octet-stream) or a redirect we can just hand to DownloadService.
                        return central;
                    }
                }
            }
            catch { /* fall through to GitHub */ }
        }

        // GitHub client-release asset fallback.
        try
        {
            using var ghResp = await GetAsync(_config.ClientReleaseFeed, ct);
            ghResp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await ghResp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null) continue;
                    bool looksRemesh = name.Contains("remesh", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("mesh", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("3d", StringComparison.OrdinalIgnoreCase);
                    if (looksRemesh && asset.TryGetProperty("browser_download_url", out var url))
                        return url.GetString();
                }
            }
        }
        catch { /* no URL */ }

        return null;
    }

    private static Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
        return Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage,
        double lo, double hi, string? prefix = null) =>
        new Progress<ProgressReport>(r =>
            outer.Report(new ProgressReport(stage, lo + (hi - lo) * r.Fraction,
                prefix != null ? prefix + (r.Detail ?? "") : r.Detail)));
}
