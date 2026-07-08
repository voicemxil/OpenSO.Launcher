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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the main window must end the app — don't rely on the OnLastWindowClose
            // default, which would keep the process alive if any secondary window ever exists.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Composition root: build the service graph here (not inside the view-model) and inject it.
            var vm = new MainViewModel(AppServices.CreateDefault());
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Stop the VM's polling loops (clock tick, server-status poll) when the app shuts
            // down, so no background work outlives the window.
            desktop.Exit += (_, _) => vm.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
