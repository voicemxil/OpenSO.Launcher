using System;
using System.Collections.Generic;
using System.IO;
namespace OpenSO.Launcher.Models;
public class LauncherConfig
{
    public string ResolvedInstallRoot() => InstallPath ?? DefaultInstallRoot();

    // Per-user, no-elevation install root. Avoids the bare home folder (clutter) and Program Files (needs
    // admin for every write/update). Windows -> %LOCALAPPDATA%\OpenSO, macOS -> ~/Library/Application
    // Support/OpenSO, Linux -> $XDG_DATA_HOME (or ~/.local/share)/OpenSO. The game files are large, so
    // LocalAppData (not roaming) is correct. The game's locator finds TSO via the sibling path + registry
    // regardless of root, so this is safe to move.
    private static string DefaultInstallRoot()
    {
        // Keep using a legacy ~/OpenSO install if one already exists, so we don't strand it / force a
        // re-download. Fresh installs go to the per-user location below.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var legacy = Path.Combine(home, "OpenSO");
        if (Directory.Exists(legacy)) return legacy;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSO");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "OpenSO");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(home, ".local", "share");
        return Path.Combine(xdg, "OpenSO");
    }

    public string LauncherUpdateFeed { get; set; } = "https://api.github.com/repos/voicemxil/OpenSO.Launcher/releases/latest";
    public string ClientReleaseFeed { get; set; } = "https://api.github.com/repos/voicemxil/OpenSO/releases/latest";

    // The canonical per-RID client distribution manifest (openso-manifest.json). It is the FIRST source
    // FsoInstaller consults to resolve the game-client full package: a schemaVersion-1 index mapping the
    // EXACT RID (win-x64 / linux-x64 / osx-x64 / osx-arm64) to a hash-verified full zip. Defaults to the
    // release asset that always tracks the latest release. This may be repointed at a mirror (e.g. an
    // api.openso.org endpoint), but a mirror MUST serve the same schemaVersion-1 per-RID schema — the old
    // single-`full_zip`, no-RID-check API manifest is no longer honoured. See
    // OpenSO/Documentation/update-manifest.md and BUILD_AND_TEST.md → "Client update source precedence".
    public string ClientManifestUrl { get; set; } = "https://github.com/voicemxil/OpenSO/releases/latest/download/openso-manifest.json";
    public string TsoAssetsBaseUrl { get; set; } = "https://archive.org/download/TheSimsOnline_201802/TSO.zip";
    public string? TsoAssetsMd5 { get; set; } = "0ad068398192d98fdc2fd423e94c3218";
    public string WebsiteUrl { get; set; } = "https://openso.org";
    public string ApiBaseUrl { get; set; } = "https://api.openso.org";
    public string GameServerHost { get; set; } = "play.openso.org";
    /// <summary>FALLBACK city map for the SERVER STATUS card thumbnail, used only while the server's
    /// /userapi/status hasn't advertised the active shard's map (shards[].map). 0013 is what the live
    /// Genesis shard actually runs — an ORIGINAL TSO map, so its thumbnail comes from the TSO install,
    /// not the client's Content/Cities (see CityMaps for the &gt;= 100 split). Genesis was once
    /// intended to run the client-bundled 0101 recreation of the same map; it does not.</summary>
    public string CityMapId { get; set; } = "0013";
    public string? InstallPath { get; set; }
    public Dictionary<string, string> ResourceCentral { get; set; } = new()
    {
        ["TheSimsOnline"] = "https://archive.org/download/TheSimsOnline_201802/TSO.zip",
        ["FreeSO"]        = "https://api.openso.org/launcher/client",
        ["3DModels"]      = "https://api.openso.org/launcher/remesh",
        ["Mono"]          = "https://api.openso.org/launcher/mono",
        ["MacExtras"]     = "https://api.openso.org/launcher/macextras",
        ["SDL"]           = "https://api.openso.org/launcher/sdl",
    };
}
