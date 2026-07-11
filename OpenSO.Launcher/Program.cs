using Avalonia;

namespace OpenSO.Launcher;

internal static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        // args flows through to App.OnFrameworkInitializationCompleted as
        // IClassicDesktopStyleApplicationLifetime.Args, where LauncherArgs.HasUpdateGame checks for
        // --update-game (the game client's version-mismatch handoff — see BUILD_AND_TEST.md → "Game →
        // launcher handoff"). No parsing needed here; StartWithClassicDesktopLifetime wires it up.
        var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Backstop: closing the window must always end the launcher process. By the time
        // StartWithClassicDesktopLifetime returns, the Avalonia lifetime has shut down and App has
        // cancelled the view-model's background loops — but a launcher must never linger as a
        // window-less background process, so exit explicitly rather than trusting that no stray
        // foreground thread (ours or a library's) keeps the CLR alive. Child processes we started
        // detached (the game, the self-update swap script) are separate OS processes and are NOT
        // affected by this. (Game updates are applied in-process by DeltaUpdateEngine / FsoInstaller —
        // the launcher no longer spawns the legacy update.exe patcher.)
        System.Environment.Exit(exitCode);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // No .WithInterFont(): use the OS system font (SF Pro on macOS, Segoe UI on Windows).
            .LogToTrace();
}
