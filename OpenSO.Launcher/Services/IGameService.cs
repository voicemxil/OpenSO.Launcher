using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

/// <summary>Progress report for any long-running launcher operation.</summary>
public record ProgressReport(string Stage, double Fraction, string? Detail = null);

/// <summary>
/// The cross-platform launcher "engine". The implementation ports the reusable logic from the
/// existing C# code (FSOInstaller/TSOManifest, SetupFetcher, FSO.Patcher) OUT of their WinForms
/// shells into plain async services that run on Windows, macOS and Linux.
/// </summary>
public interface IGameService
{
    /// <summary>True if a usable OpenSO client install is already present.</summary>
    bool IsClientInstalled();

    /// <summary>True if the original TSO game assets (user-supplied, from EA) are present.</summary>
    bool AreAssetsInstalled();

    /// <summary>Returns the locally installed client version, or null if not installed.</summary>
    string? GetInstalledClientVersion();

    /// <summary>Checks the manifest/release feed for a newer client. Returns the new version or null.</summary>
    Task<string?> CheckForClientUpdateAsync(CancellationToken ct = default);

    /// <summary>Downloads the original TSO assets from EA's public location (SetupFetcher logic).</summary>
    Task FetchGameAssetsAsync(IProgress<ProgressReport> progress, CancellationToken ct = default);

    /// <summary>Downloads + verifies + extracts/patches the OpenSO client (GameDownloader/Patcher logic).</summary>
    Task InstallOrUpdateClientAsync(IProgress<ProgressReport> progress, CancellationToken ct = default);

    /// <summary>Launches the installed game client and exits/min the launcher (Patcher.StartFreeSO logic).</summary>
    Task LaunchGameAsync(CancellationToken ct = default);
}

/// <summary>Self-update for the launcher itself, checked against the launcher's GitHub releases.</summary>
public interface ISelfUpdateService
{
    Task<string?> CheckForLauncherUpdateAsync(CancellationToken ct = default);
    Task ApplyLauncherUpdateAsync(IProgress<ProgressReport> progress, CancellationToken ct = default);
}
