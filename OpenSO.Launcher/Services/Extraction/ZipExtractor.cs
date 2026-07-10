using System;
using System.Collections.Generic;
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
///
/// Archives come from the network (client, self-update, TSO, remesh) and are therefore untrusted:
/// EVERY entry — files and directories — is validated against the destination BEFORE any disk
/// mutation (two passes), and the whole archive is rejected on the first unsafe entry so a
/// half-extraction of a malicious zip is never left on disk. See ArchivePathGuard / BUILD_AND_TEST.md.
/// </summary>
public static class ZipExtractor
{
    public static async Task ExtractAsync(string from, string to,
        IProgress<ProgressReport>? progress = null, bool preservePermissions = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(from))
            throw new FileNotFoundException($"file to extract {from} does not exist");

        using var archive = ZipFile.OpenRead(from);
        var destRoot = Path.GetFullPath(to);

        // PASS 1 — validate EVERY entry against the destination before touching the disk. Any unsafe
        // entry (traversal, rooted path, sibling escape, symlink/special file) rejects the WHOLE archive
        // with nothing written: no partial extraction of an untrusted zip.
        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDir)>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            ArchivePathGuard.RejectIfLinkOrSpecial(entry);
            bool isDir = entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);
            var target = ArchivePathGuard.ResolveContainedPath(destRoot, entry.FullName);
            plan.Add((entry, target, isDir));
        }

        // PASS 2 — the archive is proven safe; materialize it.
        Directory.CreateDirectory(to);
        int total = plan.Count, done = 0;
        foreach (var (entry, destination, isDir) in plan)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            if (isDir)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

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
