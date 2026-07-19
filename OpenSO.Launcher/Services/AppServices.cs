using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services;

/// <summary>
/// The launcher's composition root: constructs the service graph in one place and hands it to the
/// view-model, instead of the view-model <c>new</c>-ing its own collaborators. Lightweight by design — a
/// full DI container would be overkill for a single-window app, and adding one cuts against the project's
/// minimal-dependency ethos. Tests can build one of these with substituted services.
/// </summary>
public sealed class AppServices
{
    public required LauncherConfig Config { get; init; }
    public required InstallStateService InstallState { get; init; }
    public required InstallOrchestrator Orchestrator { get; init; }
    public required TsoInstallDetector TsoDetector { get; init; }
    public required NewsService News { get; init; }
    public required SelfUpdateService SelfUpdate { get; init; }
    public required StatusService Status { get; init; }
    public required GameLauncher Launcher { get; init; }

    /// <summary>Builds the default production service graph from a fresh config.</summary>
    public static AppServices CreateDefault()
    {
        var config = new LauncherConfig();
        var installState = new InstallStateService(config);
        return new AppServices
        {
            Config = config,
            InstallState = installState,
            Orchestrator = new InstallOrchestrator(config, installState),
            TsoDetector = new TsoInstallDetector(config),
            News = new NewsService(config),
            SelfUpdate = new SelfUpdateService(config),
            Status = new StatusService(config),
            Launcher = new GameLauncher(),
        };
    }
}
