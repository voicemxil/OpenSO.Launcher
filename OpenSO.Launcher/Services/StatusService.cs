using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Polls the server's public status endpoint (GET {api}/userapi/status — server time, game version,
/// players/lots online, per-shard status, busiest lots) for the launcher homepage. The endpoint is cached
/// server-side (~10s), so polling every ~30s is plenty. Network failures return null so an offline launcher
/// still installs/plays.
/// </summary>
public sealed class StatusService
{
    private readonly LauncherConfig _config;
    // Static like DownloadService's client: per-instance HttpClients are never disposed and leak
    // socket handles across launcher restarts/re-instantiations.
    private static readonly HttpClient Http = CreateClient();

    public StatusService(LauncherConfig config) => _config = config;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
        return c;
    }

    private string Api => _config.ApiBaseUrl.TrimEnd('/');

    public async Task<ServerStatus?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync($"{Api}/userapi/status", ct);
            // Source-generated (trim-safe) metadata; case-insensitive is baked into LauncherJsonContext.
            return JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ServerStatus);
        }
        catch { return null; } // offline / endpoint unavailable -> caller shows placeholders
    }

    /// <summary>Returns the shard's city thumbnail URL ({api}/userapi/city/{shardId}/city.png) when the
    /// server actually serves an image there, else null. The endpoint doesn't exist on older servers
    /// (the city route only knows per-lot locations), so the caller probes ONCE per shard and simply
    /// hides the thumbnail when this returns null — the feature lights up when the server gains it.</summary>
    public async Task<string?> GetCityThumbnailUrlAsync(int shardId, CancellationToken ct = default)
    {
        var url = $"{Api}/userapi/city/{shardId}/city.png";
        try
        {
            // GET with headers-only read: HEAD support is inconsistent, and this never downloads the body.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode
                   && resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true
                ? url : null;
        }
        catch { return null; }
    }
}
