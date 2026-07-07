using System;
using System.IO;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Allocates working directories under an app-owned temp root with unpredictable (random) names. The old
/// timestamp-based names (openso-client-&lt;unix&gt;.zip) were guessable, so on a shared-temp system (e.g.
/// Linux /tmp) a local attacker could pre-create or swap the file between download and use. Random names
/// plus a 0700 dir (best-effort, POSIX) close that race.
/// </summary>
public static class TempFiles
{
    private static string Root => Path.Combine(Path.GetTempPath(), "OpenSO.Launcher");

    /// <summary>Creates and returns a fresh, uniquely-named working directory for <paramref name="label"/>.</summary>
    public static string NewDir(string label)
    {
        var dir = Path.Combine(Root, $"{label}-{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only rwx so another local user can't read/replace files mid-install. Windows temp
            // is already per-user; nothing to tighten there.
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
        }
        return dir;
    }

    /// <summary>A uniquely-named file (in its own fresh dir) with the given name/extension.</summary>
    public static string NewFile(string label, string fileName) => Path.Combine(NewDir(label), fileName);
}
