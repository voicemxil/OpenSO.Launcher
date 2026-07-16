using System;
using System.IO;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Pre-flight free-space check shared by the installers. Measures the filesystem that actually
/// contains the target path — NOT <c>Path.GetPathRoot</c>, which on Linux always collapses to "/".
/// On immutable distros (Bazzite/Silverblue: ostree + composefs) "/" is a read-only overlay whose
/// statvfs reports 0 bytes free while the real writable space lives on /var, so probing the root
/// falsely failed with "0 GB free on /". DriveInfo on Unix runs statvfs on the given directory
/// itself; on Windows the constructor normalizes any path back to its drive root, so behavior
/// there is unchanged.
/// </summary>
internal static class DiskSpace
{
    /// <summary>Throws <see cref="IOException"/> when the filesystem containing <paramref name="path"/>
    /// has less than <paramref name="minFreeBytes"/> available. <paramref name="action"/> completes the
    /// error sentence: "Not enough free disk space to {action}: …".</summary>
    internal static void EnsureFreeSpace(string path, long minFreeBytes, string action)
    {
        string? probe;
        long available;
        try
        {
            // The target dir may not exist yet (fresh install) and statvfs needs a real path — walk up
            // to the nearest existing ancestor, which lives on the same filesystem the install writes to.
            probe = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(probe)) return;

            var di = new DriveInfo(probe);
            if (!di.IsReady) return;
            available = di.AvailableFreeSpace;
        }
        catch { return; } // DriveInfo/statvfs unavailable for this path — skip the pre-flight rather than block.

        // Thrown OUTSIDE the try so an incidental IOException from DriveInfo can never be confused
        // with (or swallowed as) the deliberate not-enough-space failure.
        if (available < minFreeBytes)
            throw new IOException(
                $"Not enough free disk space to {action}: about {Format(minFreeBytes)} is needed, " +
                $"but only {Format(available)} is free on {probe}. Free up space and try again.");
    }

    private static string Format(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes >> 20} MB";
}
