using System;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Guards URLs that come from remote feeds (GitHub release JSON, the OpenSO API) before they're handed to
/// the downloader. A tampered/MITM'd feed could otherwise point a download at an attacker's host or
/// downgrade it to plain HTTP; combined with the SHA-256 checks this keeps the update path on HTTPS only.
/// </summary>
public static class RemoteUrl
{
    public static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps
            // Plain HTTP is allowed ONLY for loopback: local test fixtures serve from 127.0.0.1, and
            // loopback traffic never crosses a network a MITM could sit on.
            || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    /// <summary>Returns <paramref name="url"/> if it's an absolute HTTPS (or loopback) URL; throws otherwise.</summary>
    public static string RequireHttps(string? url, string what)
    {
        if (!IsHttps(url))
            throw new InvalidOperationException($"Refusing to download {what} from a non-HTTPS URL: {url ?? "(null)"}");
        return url!;
    }
}
