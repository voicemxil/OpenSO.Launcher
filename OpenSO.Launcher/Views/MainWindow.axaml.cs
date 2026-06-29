using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

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

        // Only macOS gets the integrated frame: extend the client area so the traffic lights float
        // over the frosted sidebar instead of sitting in a separate strip above the UI, and push the
        // logo down to clear them. On Windows/Linux we keep the real native title bar (draggable,
        // normal min/max/close, no stray fullscreen toggle) — Avalonia 12's extended chrome there
        // isn't draggable and adds a fullscreen button we don't want.
        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
            LogoImage.Margin = new Thickness(22, 52, 0, 8);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // If the compositor granted no transparency (e.g. Mica unavailable on this Windows build),
        // a Transparent window background would leak the desktop through the translucent sidebar.
        // Fall back to an opaque themed background so the sidebar reads as a clean solid panel.
        if (ActualTransparencyLevel == WindowTransparencyLevel.None &&
            this.TryFindResource("Bg", ActualThemeVariant, out var bg) && bg is IBrush brush)
        {
            Background = brush;
        }
    }
}
