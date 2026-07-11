using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenSO.Launcher.ViewModels;
using OpenSO.Launcher.Views;

namespace OpenSO.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            var vm = new MainViewModel(updateGame);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Stop the VM's polling loops (clock tick, server-status poll) when the app shuts
            // down, so no background work outlives the window.
            desktop.Exit += (_, _) => vm.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
