using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services;

/// <summary>One headline from the OpenSO website news feed.</summary>
public record NewsItem(string Slug, string Title, string Date, string? Summary, string? ImageUrl);

/// <summary>
/// Pulls the OpenSO website's news feed (openso.org/news/feed.json — the same feed the site renders) for
/// the launcher's news panel, and opens full posts (openso.org/post.html?p=&lt;slug&gt;) in the system
/// browser. Network failures are swallowed so an offline launcher still installs/plays.
/// </summary>
public sealed class NewsService
{
    private readonly LauncherConfig _config;
    private readonly HttpClient _http;

    public NewsService(LauncherConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
    }

    private string Site => _config.WebsiteUrl.TrimEnd('/');

    public async Task<IReadOnlyList<NewsItem>> GetLatestAsync(int max = 4, CancellationToken ct = default)
    {
        var items = new List<NewsItem>();
        try
        {
            var json = await _http.GetStringAsync($"{Site}/news/feed.json", ct);
            using var doc = JsonDocument.Parse(json);
            // feed.json is { "posts": [ {slug,title,date,author,summary,tags,image} ] }; tolerate a bare array too.
            var root = doc.RootElement;
            JsonElement posts = root.ValueKind == JsonValueKind.Array ? root
                : (root.TryGetProperty("posts", out var p) ? p : default);
            if (posts.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in posts.EnumerateArray())
                {
                    if (items.Count >= max) break;
                    var slug = Str(e, "slug");
                    var title = Str(e, "title");
                    if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title)) continue;
                    var img = Str(e, "image");
                    if (!string.IsNullOrEmpty(img) && img!.StartsWith('/')) img = Site + img; // root-relative -> absolute
                    items.Add(new NewsItem(slug!, title!, Str(e, "date") ?? "", Str(e, "summary"), img));
                }
            }
        }
        catch { /* offline / feed unavailable -> empty; launcher still works */ }
        return items;
    }

    public void OpenPost(string slug) => OpenUrl($"{Site}/post.html?p={Uri.EscapeDataString(slug)}");
    public void OpenWebsite() => OpenUrl(Site);

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Open a URL in the system default browser (cross-platform).</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch { /* best effort */ }
    }
}
