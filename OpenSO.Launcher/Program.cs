using Avalonia;

namespace OpenSO.Launcher;

internal static class Program
{
    [System.STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // No .WithInterFont(): use the OS system font (SF Pro on macOS, Segoe UI on Windows).
            .LogToTrace();
}
