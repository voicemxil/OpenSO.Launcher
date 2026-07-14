using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenSO.Launcher.Services;
using OpenSO.Launcher.ViewModels;
using OpenSO.Launcher.Views;

namespace OpenSO.Launcher;

public partial class App : Application
{
    /// <summary>True once the user (tray Exit) or the self-updater asked for a real shutdown — the
    /// minimize-to-tray close handler must let the window actually close then.</summary>
    public static bool ExitRequested;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private void TrayOpen_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
        {
            win.Show();
            win.WindowState = WindowState.Normal;
            win.Activate();
        }
    }

    private void TrayExit_Click(object? sender, System.EventArgs e)
    {
        ExitRequested = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the main window must end the app — don't rely on the OnLastWindowClose
            // default, which would keep the process alive if any secondary window ever exists.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // --update-game: the game client's handoff on a version mismatch for a Launcher-managed
            // install (see BUILD_AND_TEST.md → "Game → launcher handoff"). desktop.Args is populated by
            // Avalonia from the args Program.cs passed to StartWithClassicDesktopLifetime.
            var updateGame = LauncherArgs.HasUpdateGame(desktop.Args);

            // Composition root: build the service graph here (not inside the view-model) and inject it.
            var vm = new MainViewModel(AppServices.CreateDefault(), updateGame);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Stop the VM's polling loops (clock tick, server-status poll) when the app shuts
            // down, so no background work outlives the window.
            desktop.Exit += (_, _) => vm.Shutdown();
        }

        // macOS: clicking the Dock icon while the window is hidden (minimize-to-tray) sends a
        // "reopen" activation — re-show the window like a native mac app would.
        if (ApplicationLifetime is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) TrayOpen_Click(null, System.EventArgs.Empty);
            };
        base.OnFrameworkInitializationCompleted();
    }
}
