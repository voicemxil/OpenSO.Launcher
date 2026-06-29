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
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public StatusService(LauncherConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
    }

    private string Api => _config.ApiBaseUrl.TrimEnd('/');

    public async Task<ServerStatus?> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync($"{Api}/userapi/status", ct);
            return JsonSerializer.Deserialize<ServerStatus>(json, Opts);
        }
        catch { return null; } // offline / endpoint unavailable -> caller shows placeholders
    }
}
