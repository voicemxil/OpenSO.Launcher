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

    // Canonical community source for the FreeSO Remesh Package (forum thread #6152, last updated
    // 2023-07-27, ~196 MB, ~2600 .fsom meshes + textures). FreeSO's own host (beta.freeso.org, which
    // served the launcher's old "LauncherResourceCentral/3DModels") is decommissioned/NXDOMAIN, so this
    // simfileshare mirror is the live source. Used only as a last-ditch fallback — operators should
    // re-host the zip on their own infra (ResourceCentral["3DModels"] or a release asset) for reliability.
    private const string CommunityRemeshMirror = "https://simfileshare.net/download/4048366/";

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
        var (zipUrl, zipSha256) = await ResolveRemeshZipUrlAsync(ct);
        if (zipUrl == null)
            throw new InvalidOperationException("No 3D mesh pack is available from the OpenSO server yet.");
        RemoteUrl.RequireHttps(zipUrl, "the 3D mesh pack");

        var work = TempFiles.NewDir("remesh");
        var tempZip = Path.Combine(work, "remesh.zip");
        var unzipDir = Path.Combine(work, "x");
        try
        {
            var dl = new DownloadService(zipUrl, tempZip, expectedSha256: zipSha256);
            await dl.RunAsync(Scale(progress, "rms", 0.00, 0.80, "Downloading 3D meshes… "), ct);

            await ZipExtractor.ExtractAsync(tempZip, unzipDir,
                Scale(progress, "rms", 0.80, 0.92, "Unpacking… "), preservePermissions: false, ct);

            // Normalize the layout. The community zip wraps the meshes as
            // "FreeSO Remesh Package/MeshReplace/*.fsom"; a CI artifact may ship them flat. Either way,
            // copy the MeshReplace contents into the client's Content/MeshReplace (where RCMeshProvider
            // looks) — extracting the raw zip here would nest them under a wrapper folder and the game
            // would find nothing.
            var src = FindMeshSource(unzipDir)
                ?? throw new InvalidOperationException("The downloaded package contained no .fsom meshes.");

            progress.Report(new ProgressReport("rms", 0.93, "Installing 3D meshes…"));
            Directory.CreateDirectory(meshReplace);
            CopyTree(src, meshReplace);

            // Mark it installed and record the source, so a future version check can compare/refresh.
            try { File.WriteAllText(Path.Combine(meshReplace, ".openso-remesh"), $"RMS\n{zipUrl}\n{DateTimeOffset.UtcNow:o}\n"); } catch { }

            progress.Report(new ProgressReport("rms", 1.0, "3D mesh pack installed."));
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>
    /// Find the directory whose contents map onto Content/MeshReplace. Prefer a folder literally named
    /// "MeshReplace" (the community package); otherwise the folder that directly holds the .fsom files
    /// (a flat artifact). Returns null if the archive holds no meshes at all.
    /// </summary>
    private static string? FindMeshSource(string root)
    {
        var meshReplace = Directory.EnumerateDirectories(root, "MeshReplace", SearchOption.AllDirectories).FirstOrDefault();
        if (meshReplace != null) return meshReplace;
        var anyFsom = Directory.EnumerateFiles(root, "*.fsom", SearchOption.AllDirectories).FirstOrDefault();
        return anyFsom != null ? Path.GetDirectoryName(anyFsom) : null;
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: true);
    }

    /// <summary>
    /// Resolve the remesh zip URL: the OpenSO ResourceCentral "3DModels" entry first (either a direct
    /// zip/redirect or a small JSON manifest with a {full_zip|url|remesh} field), then a GitHub
    /// client-release asset whose name mentions "remesh"/"mesh"/"3d" as a fallback.
    /// </summary>
    private async Task<(string? url, string? sha256)> ResolveRemeshZipUrlAsync(CancellationToken ct)
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
                            {
                                string? sha = root.TryGetProperty("sha256", out var s) && s.ValueKind == JsonValueKind.String
                                    ? s.GetString() : null;
                                return (v.GetString(), sha);
                            }
                    }
                    else
                    {
                        // Direct file (zip / octet-stream) or a redirect we can just hand to DownloadService.
                        return (central, null);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Remesh ResourceCentral lookup failed; falling back to the GitHub release feed", ex); }
        }

        // GitHub client-release asset fallback (the asset's `digest` field is "sha256:<hex>" when present).
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
                    {
                        string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
                            ? d.GetString() : null;
                        return (url.GetString(), digest);
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn("GitHub remesh asset lookup failed; using the community mirror", ex); }

        // Last resort: the community mirror, so the feature works before operators re-host the package.
        // No published hash exists for it, so this path stays unverified (flagged in LAUNCHER_ROADMAP.md).
        return (CommunityRemeshMirror, null);
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
