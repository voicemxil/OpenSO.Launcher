using Avalonia;
using OpenSO.Launcher.Services;

namespace OpenSO.Launcher;

internal static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        Log.Init();
        // Last-resort breadcrumbs: without these, a fatal on a background thread or an unobserved task
        // exception vanishes with the process and there's nothing to debug from a user's report.
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as System.Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // --smoke: headless self-check of the trim-sensitive paths against THIS (published/trimmed)
        // binary — never starts the Avalonia UI loop. CI runs it after publish and fails on a non-zero
        // exit (see SmokeTest / BUILD_AND_TEST.md → "Trimmed-binary smoke gate"). Handle it before any
        // Avalonia bootstrap so the check needs no window server.
        if (SmokeTest.IsRequested(args))
            System.Environment.Exit(SmokeTest.Run());

        // args flows through to App.OnFrameworkInitializationCompleted as
        // IClassicDesktopStyleApplicationLifetime.Args, where LauncherArgs.HasUpdateGame checks for
        // --update-game (the game client's version-mismatch handoff — see BUILD_AND_TEST.md → "Game →
        // launcher handoff"). No parsing needed here; StartWithClassicDesktopLifetime wires it up.
        int exitCode;
        try
        {
            exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (System.Exception ex)
        {
            Log.Error("Fatal: the launcher crashed during startup/run", ex);
            throw;
        }

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
