using System;
using System.Collections.Generic;
using System.IO;
namespace OpenSO.Launcher.Models;
public class LauncherConfig
{
    public string ResolvedInstallRoot() =>
        InstallPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenSO");

    public string LauncherUpdateFeed { get; set; } = "https://api.github.com/repos/voicemxil/OpenSO.Launcher/releases/latest";
    public string ClientReleaseFeed { get; set; } = "https://api.github.com/repos/voicemxil/OpenSO/releases/latest";
    public string ClientManifestUrl { get; set; } = "https://api.openso.org/launcher/manifest.json";
    public string TsoAssetsBaseUrl { get; set; } = "https://archive.org/download/TheSimsOnline_201802/TSO.zip";
    public string? TsoAssetsMd5 { get; set; } = "0ad068398192d98fdc2fd423e94c3218";
    public string WebsiteUrl { get; set; } = "https://openso.org";
    public string ApiBaseUrl { get; set; } = "https://api.openso.org";
    public string GameServerHost { get; set; } = "play.openso.org";
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
