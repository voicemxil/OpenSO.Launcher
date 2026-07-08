using System;
using System.Net.Http;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Builds the GET requests the launcher makes to release feeds/APIs, in one place. Several services had an
/// identical copy of "new request + User-Agent + optional GitHub token"; centralizing it means the
/// rate-limit token (and any future header) is applied consistently.
/// </summary>
public static class HttpRequests
{
    /// <summary>
    /// A GET carrying the launcher User-Agent, plus — for api.github.com URLs — the
    /// <c>GITHUB_RATELIMIT_TOKEN</c> env var as a bearer so CI/dev machines aren't anonymously rate-limited.
    /// </summary>
    public static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
        if (url.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_RATELIMIT_TOKEN");
            if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        }
        return req;
    }
}
