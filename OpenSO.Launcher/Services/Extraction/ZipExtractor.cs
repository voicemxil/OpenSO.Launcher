using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services.Extraction;

/// <summary>
/// Port of the upstream launcher's lib/unzip.js (yauzl → System.IO.Compression).
/// Extracts a zip into a destination directory, creating subdirectories, reporting each entry,
/// and (optionally) preserving the unix file mode for executables — the cpperm behavior used by
/// the Mac/Linux extras in fso.js step6.
/// </summary>
public static class ZipExtractor
{
    public static async Task ExtractAsync(string from, string to,
        IProgress<ProgressReport>? progress = null, bool preservePermissions = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(from))
            throw new FileNotFoundException($"file to extract {from} does not exist");

        Directory.CreateDirectory(to);

        using var archive = ZipFile.OpenRead(from);
        int total = archive.Entries.Count;
        int done = 0;

        // Zip-slip guard root: trailing separator so a SIBLING with the same prefix ("C:\inst-evil"
        // vs "C:\inst") can't pass, and case-insensitive comparison on Windows (the filesystem is —
        // an Ordinal check would wave through a case-varied traversal).
        var root = Path.GetFullPath(to);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            // Directory entry (name ends with '/') — just ensure it exists (guarded like files:
            // a "../" directory entry must not create folders outside the destination).
            if (entry.FullName.EndsWith("/") || string.IsNullOrEmpty(entry.Name))
            {
                var dirDest = Path.GetFullPath(Path.Combine(to, entry.FullName));
                if (!dirDest.StartsWith(root, cmp) && dirDest + Path.DirectorySeparatorChar != root)
                    throw new IOException($"Blocked unsafe zip entry path: {entry.FullName}");
                Directory.CreateDirectory(dirDest);
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(to, entry.FullName));

            // Zip-slip guard: never write outside the destination directory.
            if (!destination.StartsWith(root, cmp))
                throw new IOException($"Blocked unsafe zip entry path: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var inStream = entry.Open())
            await using (var outStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await inStream.CopyToAsync(outStream, 81920, ct);
            }

            if (preservePermissions && !OperatingSystem.IsWindows())
            {
                // External attributes high 16 bits hold the unix mode (matches unzip.js cpperm).
                int mode = (int)(entry.ExternalAttributes >> 16);
                if (mode != 0) TrySetUnixMode(destination, mode);
            }

            progress?.Report(new ProgressReport("extract",
                total > 0 ? (double)done / total : 1.0, entry.Name));
        }

        progress?.Report(new ProgressReport("extract", 1.0, "done"));
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void TrySetUnixMode(string path, int mode)
    {
        try { File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF)); }
        catch { /* best effort; non-fatal */ }
    }
}
