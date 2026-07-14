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
    // Static: per-instance HttpClients are never disposed and leak socket handles (see StatusService).
    private static readonly HttpClient Http = CreateClient();

    public NewsService(LauncherConfig config) => _config = config;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
        return c;
    }

    private string Site => _config.WebsiteUrl.TrimEnd('/');

    private static string CachePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenSO Launcher", "news-cache.json");

    public async Task<IReadOnlyList<NewsItem>> GetLatestAsync(int max = 4, CancellationToken ct = default)
    {
        var items = new List<NewsItem>();
        try
        {
            var json = await Http.GetStringAsync($"{Site}/news/feed.json", ct);
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
            if (items.Count > 0) SaveCache(items);
            return items;
        }
        catch (Exception ex)
        {
            // Offline / transient failure: show the last good feed instead of blanking the panel.
            Log.Warn("News feed fetch failed; using the cached feed", ex);
            return LoadCache(max);
        }
    }

    private static void SaveCache(List<NewsItem> items)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
            // Source-generated (trim-safe) metadata — see LauncherJsonContext.
            System.IO.File.WriteAllText(CachePath, JsonSerializer.Serialize(items, LauncherJsonContext.Default.ListNewsItem));
        }
        catch { /* non-fatal */ }
    }

    private static List<NewsItem> LoadCache(int max)
    {
        try
        {
            if (System.IO.File.Exists(CachePath))
            {
                var cached = JsonSerializer.Deserialize(System.IO.File.ReadAllText(CachePath), LauncherJsonContext.Default.ListNewsItem);
                if (cached != null) return cached.Count > max ? cached.GetRange(0, max) : cached;
            }
        }
        catch { /* corrupt cache -> empty */ }
        return new List<NewsItem>();
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
