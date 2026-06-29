using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSO.Launcher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Frosted-glass backdrop: vibrancy on macOS (NSVisualEffectView), Mica on Windows 11,
        // a plain blur elsewhere. The window background is Transparent so the blur shows through
        // the translucent sidebar; the content panel paints an opaque background over it.
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.Blur,
        };

        // Integrate the native window controls (macOS traffic lights / Windows caption buttons)
        // into our own frame instead of a separate OS title-bar strip above the UI.
        // (Avalonia 12 dropped ExtendClientAreaChromeHints; the system chrome is the default.)
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
    }
}
