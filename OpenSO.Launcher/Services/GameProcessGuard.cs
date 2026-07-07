using System;
using System.Diagnostics;
using System.IO;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Detects whether the OpenSO client is currently running, so the launcher can refuse to install/update/
/// patch over a live game. Overwriting a running install locks the exe + loaded runtime DLLs (Windows) or
/// leaves a half-swapped tree — the exact way an "update while playing" corrupts the client.
/// </summary>
public static class GameProcessGuard
{
    // The client's apphost base name on every platform (Windows OpenSO.exe, unix/macOS "OpenSO"). No
    // extension — Process.ProcessName is the file name without it. Note the launcher runs as
    // "OpenSO.Launcher", which is NOT an exact match, so it never trips this on itself.
    private const string ClientProcessName = "OpenSO";

    /// <summary>
    /// True if an OpenSO client process appears to be running out of <paramref name="installDir"/>. When a
    /// process's image path can't be read (access denied / cross-arch), it's counted as a match — refusing
    /// an update on an unreadable "OpenSO" is far safer than corrupting a live install. A null/empty
    /// installDir matches any OpenSO process.
    /// </summary>
    public static bool IsGameRunning(string? installDir)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(ClientProcessName); }
        catch { return false; } // can't enumerate — don't hard-block the user

        try
        {
            foreach (var p in procs)
            {
                if (string.IsNullOrEmpty(installDir)) return true;
                var path = TryGetImagePath(p);
                if (path == null) return true;          // unverifiable — assume it's ours (safe: refuse)
                if (IsWithin(path, installDir!)) return true;
            }
            return false;
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
    }

    private static string? TryGetImagePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    private static bool IsWithin(string exePath, string installDir)
    {
        try
        {
            var root = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(exePath);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return full.StartsWith(root, cmp);
        }
        catch { return false; }
    }
}
