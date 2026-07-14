using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Desktop notifications. Windows: Shell_NotifyIcon balloons — shown by the OS as native toast
/// notifications on Windows 10/11; the notification icon is added only for the lifetime of a balloon
/// (then removed), so it doesn't sit next to the launcher's regular tray icon. macOS: Notification
/// Center via osascript's "display notification" (no signing entitlements or permission plumbing
/// needed). Mirrors the upstream launcher's Launcher.DesktopNotifications feature. No-ops on Linux.
/// </summary>
internal static class TrayNotifier
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x1, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint IconId = 0x534F; // "SO" — fixed id so re-adds always target the same icon

    private static readonly object Lock = new();
    private static IntPtr _hwnd;
    private static IntPtr _hIcon;
    private static bool _iconVisible;
    private static int _generation; // invalidates a pending removal when a newer balloon replaces it

    /// <summary>Call once from the main window when its native handle exists. Windows only.</summary>
    public static void Initialize(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        // The launcher exe embeds the app icon (ApplicationIcon in the csproj) — pull it from there;
        // fall back to the stock application icon if extraction fails (e.g. running via `dotnet`).
        try
        {
            ExtractIconEx(Environment.ProcessPath ?? "", 0, out var large, out var small, 1);
            _hIcon = small != IntPtr.Zero ? small : large;
        }
        catch { /* fall through to stock icon */ }
        if (_hIcon == IntPtr.Zero)
            _hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);
    }

    /// <summary>Shows a desktop notification. Silently does nothing if unsupported or uninitialized.</summary>
    public static void Show(string title, string message)
    {
        if (OperatingSystem.IsMacOS()) { ShowMac(title, message); return; }
        if (!OperatingSystem.IsWindows() || _hwnd == IntPtr.Zero) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = IconId,
                uFlags = NIF_INFO | NIF_ICON | NIF_TIP,
                hIcon = _hIcon,
                szTip = "OpenSO Launcher",
                szInfoTitle = title.Length > 63 ? title[..63] : title,
                szInfo = message.Length > 255 ? message[..255] : message,
                dwInfoFlags = NIIF_INFO | NIIF_RESPECT_QUIET_TIME,
            };
            int gen;
            lock (Lock)
            {
                if (!Shell_NotifyIcon(_iconVisible ? NIM_MODIFY : NIM_ADD, ref data)) return;
                _iconVisible = true;
                gen = ++_generation;
            }
            // Remove the icon once the balloon has had time to display, unless a newer balloon took over.
            _ = Task.Delay(TimeSpan.FromSeconds(12)).ContinueWith(_ => RemoveIcon(gen));
        }
        catch (Exception ex) { Log.Warn("Desktop notification failed", ex); }
    }

    /// <summary>macOS Notification Center. osascript takes the script as an argv element (no shell),
    /// so only AppleScript string escaping is needed — backslashes and double quotes.</summary>
    private static void ShowMac(string title, string message)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", $"display notification \"{Esc(message)}\" with title \"{Esc(title)}\"" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log.Warn("Desktop notification failed (osascript)", ex); }
    }

    private static void RemoveIcon(int generation)
    {
        lock (Lock)
        {
            if (!_iconVisible || generation != _generation) return;
            var data = new NOTIFYICONDATA { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = IconId };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconVisible = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
}
