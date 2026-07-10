using System;
using System.IO;
using System.IO.Compression;

namespace OpenSO.Launcher.Services.Extraction;

/// <summary>
/// Shared containment validation for untrusted archive entries (zip + cab). The launcher extracts
/// archives fetched over the network (game client, launcher self-update, TSO assets, remesh pack), so
/// a malicious or corrupt archive must never be able to write outside its destination directory.
///
/// The old per-extractor guard was a string-prefix check — <c>fullPath.StartsWith(fullDest)</c> — which
/// (a) never validated directory entries and (b) accepted a SIBLING whose name merely starts with the
/// destination's (dest "install" accepted "install-evil/…"). This helper replaces that with a
/// canonicalize-then-relative-path check that cannot be prefix-bypassed, and rejects the traversal
/// shapes (rooted paths, "..", empty components, backslash tricks) up front.
///
/// See BUILD_AND_TEST.md → "Archive-extraction security policy" for the full policy statement.
/// </summary>
internal static class ArchivePathGuard
{
    /// <summary>
    /// Resolves <paramref name="entryName"/> against <paramref name="destRoot"/> and returns the safe,
    /// canonical absolute path to write it to — or throws <see cref="IOException"/> if the entry would
    /// escape the destination. Validates both file and directory entries; callers MUST validate every
    /// entry BEFORE writing anything (reject the whole archive on the first bad entry).
    /// </summary>
    public static string ResolveContainedPath(string destRoot, string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
            throw new IOException("Blocked archive entry with an empty path.");

        // A zip/cab path separator is '/'. Reject literal backslashes so a "a\\..\\b" entry can't be
        // reinterpreted as traversal on Windows, and a backslash filename can't be smuggled elsewhere.
        if (entryName.IndexOf('\\') >= 0)
            throw new IOException($"Blocked archive entry containing a backslash: {entryName}");

        // Reject absolute / rooted entry paths outright ("/etc/passwd", "C:\\…", "\\\\server\\share").
        if (Path.IsPathRooted(entryName))
            throw new IOException($"Blocked absolute (rooted) archive entry path: {entryName}");

        // Component checks: no "." / ".." traversal, no empty interior components ("a//b"). Tolerate a
        // single trailing '/' (directory entries) but nothing else empty.
        var normalized = entryName.EndsWith('/') ? entryName[..^1] : entryName;
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0)
                throw new IOException($"Blocked archive entry with an empty path component: {entryName}");
            if (part == "." || part == "..")
                throw new IOException($"Blocked archive entry with a relative/traversal component: {entryName}");
        }

        var destFull = Path.GetFullPath(destRoot);
        var target = Path.GetFullPath(Path.Combine(destFull, entryName));

        // Authoritative containment guard: a RELATIVE path from the destination to the resolved target
        // that escapes (starts with "..") or is rooted means the entry left the destination. Unlike the
        // old string-prefix test, a sibling ("install-evil") yields "../install-evil/…" and is rejected.
        var rel = Path.GetRelativePath(destFull, target);
        if (Path.IsPathRooted(rel)
            || rel.Equals("..", StringComparison.Ordinal)
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"Blocked archive entry escaping the destination directory: {entryName}");

        return target;
    }

    /// <summary>
    /// Rejects any zip entry that is not a regular file or directory. A zip records the unix file type in the
    /// high 16 bits of <see cref="ZipArchiveEntry.ExternalAttributes"/>; a symlink (S_IFLNK) — or a
    /// device/fifo/socket — must never be created from an untrusted archive, because a symlink could redirect
    /// a later entry's write outside the destination (a link-based escape that a path check alone can't
    /// catch). Mode-less entries (0, e.g. Windows-authored zips) are treated as regular files. Shared by
    /// <c>ZipExtractor</c> and the delta engine so both enforce the identical policy (see BUILD_AND_TEST.md
    /// → "Archive-extraction security policy").
    /// </summary>
    public static void RejectIfLinkOrSpecial(ZipArchiveEntry entry)
    {
        int mode = (int)(entry.ExternalAttributes >> 16);
        int fmt = mode & 0xF000; // S_IFMT
        if (fmt == 0 || fmt == 0x8000 || fmt == 0x4000) return; // none / regular file / directory
        if (fmt == 0xA000)
            throw new IOException($"Blocked symlink zip entry: {entry.FullName}");
        throw new IOException($"Blocked special-file zip entry (mode 0x{fmt:X4}): {entry.FullName}");
    }
}
