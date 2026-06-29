using System.Collections.Generic;

namespace OpenSO.Launcher.Models;

/// <summary>
/// The installable components and their dependency graph, ported directly from the upstream
/// launcher's constants.js (`components`, `dependencies`, `needInternet`).
/// Keeping this faithful is what makes "install FSO" correctly pull TSO + the right runtime deps.
/// </summary>
public static class Components
{
    /// <summary>Component code → human-readable name (constants.js `components`).</summary>
    public static readonly Dictionary<string, string> Names = new()
    {
        ["TSO"]       = "The Sims Online",
        ["FSO"]       = "OpenSO Client",      // was "FreeSO" upstream
        ["OpenAL"]    = "OpenAL",
        ["NET"]       = ".NET Framework",
        ["RMS"]       = "Remesh Package",
        ["Simitone"]  = "Simitone",
        ["Mono"]      = "Mono Runtime",
        ["MacExtras"] = "OpenSO MacExtras",
        ["SDL"]       = "SDL2",
    };

    /// <summary>
    /// Folder name a component installs into under the install root. TSO must land in "The Sims Online"
    /// (with a TSOClient subfolder) because the game's locator looks for "../The Sims Online/TSOClient/"
    /// relative to the client's working directory FIRST — so a sibling layout makes it findable with no
    /// registry/admin. Everything else just uses its code.
    /// </summary>
    public static string InstallDirName(string code) => code == "TSO" ? "The Sims Online" : code;

    /// <summary>
    /// Dependency graph (constants.js `dependencies`). The OpenSO client is a self-contained native
    /// .NET build (CI: `dotnet publish -r &lt;rid&gt; --self-contained`), so it bundles its own runtime and
    /// native libs (SDL2/OpenAL via MonoGame's runtime packages). The legacy FreeSO-on-Mono deps
    /// (Mono, SDL, OpenAL, MacExtras) are obsolete — the only thing the client still needs is the TSO
    /// game assets. (Those endpoints were never hosted, so requiring them 404'd and broke install.)
    /// </summary>
    public static Dictionary<string, string[]> DependenciesFor(OSPlatformKind os)
    {
        bool unixLike = os is OSPlatformKind.MacOS or OSPlatformKind.Linux;
        return new Dictionary<string, string[]>
        {
            ["FSO"]       = new[] { "TSO" },
            ["RMS"]       = new[] { "FSO" },
            ["Simitone"]  = unixLike ? new[] { "Mono", "SDL" } : System.Array.Empty<string>(),
        };
    }

    /// <summary>Components that require an internet connection to install (constants.js `needInternet`).</summary>
    public static readonly HashSet<string> NeedInternet = new()
    {
        "TSO", "FSO", "RMS", "Simitone", "Mono", "MacExtras", "SDL"
    };
}

public enum OSPlatformKind { Windows, MacOS, Linux }
