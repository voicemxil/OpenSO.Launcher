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
        // Progress is byte-based, not file-count: a single large file (the ~100 MB bundled runtime)
        // otherwise jumps 1%->100% while small files fly by. Track uncompressed bytes written vs the
        // archive total, reporting within big files too so the bar moves smoothly.
        long totalBytes = 0;
        foreach (var e in archive.Entries) totalBytes += e.Length;
        long writtenBytes = 0;

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
                var buffer = new byte[262144]; // 256 KB — fewer syscalls than the old 80 KB on fast disks
                int n;
                long sinceReport = 0;
                while ((n = await inStream.ReadAsync(buffer, ct)) > 0)
                {
                    await outStream.WriteAsync(buffer.AsMemory(0, n), ct);
                    writtenBytes += n;
                    sinceReport += n;
                    if (sinceReport >= 4 * 1024 * 1024) // report intra-file every ~4 MB
                    {
                        sinceReport = 0;
                        progress?.Report(new ProgressReport("extract", Fraction(writtenBytes, totalBytes, done, total), entry.Name));
                    }
                }
            }

            if (preservePermissions && !OperatingSystem.IsWindows())
            {
                // External attributes high 16 bits hold the unix mode (matches unzip.js cpperm).
                int mode = (int)(entry.ExternalAttributes >> 16);
                if (mode != 0) TrySetUnixMode(destination, mode);
            }

            progress?.Report(new ProgressReport("extract", Fraction(writtenBytes, totalBytes, done, total), entry.Name));
        }

        progress?.Report(new ProgressReport("extract", 1.0, "done"));
    }

    // Prefer the byte fraction; fall back to the file-count fraction for a zip of empty files (totalBytes 0).
    private static double Fraction(long writtenBytes, long totalBytes, int done, int total) =>
        totalBytes > 0 ? Math.Clamp((double)writtenBytes / totalBytes, 0, 1)
                       : (total > 0 ? (double)done / total : 1.0);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void TrySetUnixMode(string path, int mode)
    {
        try { File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF)); }
        catch { /* best effort; non-fatal */ }
    }
}
