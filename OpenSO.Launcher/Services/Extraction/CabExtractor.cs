using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services.Extraction;

/// <summary>
/// Pure-managed Microsoft Cabinet (.cab) extractor, ported from the upstream FreeSO launcher's
/// lib/cabinet.js. Handles the real multi-part, MSZIP-compressed TSO set (Data1.cab…Data1111.cab),
/// including the key structural details:
///
///  • MULTIPLE FOLDERS. Large game files each live in their own CAB "folder"; a folder's uncompressed
///    offsets are folder-relative (they reset to 0 for a new file). A file's iFolder selects its
///    folder (special values: 0xFFFD/0xFFFF = first folder / continued-from-previous-cab, 0xFFFE =
///    last folder / continues-into-next-cab). Each folder has its OWN 32 KB MSZIP window.
///  • FOLDERS SPAN CABS. A cabinet's first folder may continue the previous cabinet's last folder
///    (CFHEADER prev-cabinet flag): its data blocks are appended to that folder rather than starting
///    a new one. Within a folder, a data block with uncompressed size 0 is a SPLIT block whose
///    compressed bytes continue (with no "CK" header) as the first block of the next cabinet.
///  • MSZIP WINDOW. Each block is "CK" + DEFLATE, but blocks in a folder share a sliding window
///    (block N back-references block N-1). DeflateStream has no preset-dictionary API, so we prime
///    each block with the previous 32 KB (as "stored" DEFLATE blocks) and trim them off.
///
/// MEMORY: blocks reference their cab by PATH, not by bytes — cab bytes are loaded lazily one cab at
/// a time, and extraction runs folder-by-folder holding only the current folder's decompressed data.
/// (An earlier version pinned every cab's bytes AND cached every decompressed folder for the whole
/// run — over 4 GB resident for the TSO set.) Peak memory is one cab + one folder; with purge, each
/// cab file is deleted from disk the moment its last folder finishes, so the cabs and the (larger)
/// extracted game still never fully coexist on disk. (LZX/Quantum unimplemented.)
/// </summary>
public static class CabExtractor
{
    private const ushort FlagPrevCabinet = 0x0001;
    private const ushort FlagNextCabinet = 0x0002;
    private const ushort FlagReservePresent = 0x0004;

    public static Task ExtractAsync(string firstCab, string to,
        IProgress<ProgressReport>? progress = null, bool purge = false, CancellationToken ct = default)
    {
        if (!File.Exists(firstCab)) throw new FileNotFoundException($"cab to extract {firstCab} does not exist");
        return Task.Run(() => Run(firstCab, to, progress, purge, ct), ct);
    }

    private sealed class Block { public string CabPath = ""; public int Offset; public ushort CBytes; public ushort UcBytes; }
    private sealed class Folder { public List<Block> Blocks = new(); }
    private sealed class FileRec { public uint USize; public uint UOff; public int GlobalFolder; public string Name = ""; }
    private sealed class CabMeta { public bool Prev; public string? Next; public List<List<Block>> Folders = new(); public List<(uint USize, uint UOff, ushort IFolder, string Name)> Files = new(); }

    private static void Run(string firstCab, string to, IProgress<ProgressReport>? progress, bool purge, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        var dir = Path.GetDirectoryName(Path.GetFullPath(firstCab))! + Path.DirectorySeparatorChar;

        // 1. Walk the chain, building the GLOBAL folder list (merging continued folders) and the file
        //    list with each file mapped to its global folder index.
        var folders = new List<Folder>();
        var files = new List<FileRec>();
        var seen = new HashSet<string>(); // (name|globalFolder) dedup across continuation cabs
        var processed = new List<string>();
        int prevLastFolder = -1;

        string? cur = firstCab;
        int cabCount = 0;
        while (cur != null && File.Exists(cur))
        {
            ct.ThrowIfCancellationRequested();
            var meta = Parse(cur);
            processed.Add(cur);
            cabCount++;

            // Map this cab's local folder indices to global ones (merge folder 0 if continued).
            var localToGlobal = new int[meta.Folders.Count];
            for (int fi = 0; fi < meta.Folders.Count; fi++)
            {
                if (fi == 0 && meta.Prev && prevLastFolder >= 0)
                {
                    folders[prevLastFolder].Blocks.AddRange(meta.Folders[fi]);
                    localToGlobal[fi] = prevLastFolder;
                }
                else
                {
                    folders.Add(new Folder { Blocks = new List<Block>(meta.Folders[fi]) });
                    localToGlobal[fi] = folders.Count - 1;
                }
            }
            if (meta.Folders.Count > 0) prevLastFolder = localToGlobal[meta.Folders.Count - 1];

            foreach (var (usize, uoff, ifolder, name) in meta.Files)
            {
                int local = ifolder switch
                {
                    0xFFFD or 0xFFFF => 0,
                    0xFFFE => meta.Folders.Count - 1,
                    _ => ifolder
                };
                if (local < 0 || local >= meta.Folders.Count) local = Math.Clamp(local, 0, meta.Folders.Count - 1);
                int gf = localToGlobal[local];
                var key = name + "|" + gf;
                if (seen.Add(key)) files.Add(new FileRec { USize = usize, UOff = uoff, GlobalFolder = gf, Name = name });
            }

            progress?.Report(new ProgressReport("cab", 0.5, $"Reading game archives… ({cabCount})"));
            cur = meta.Next != null ? Path.Combine(dir, meta.Next) : null;
        }

        // 2. Decompress folder-by-folder (files are grouped by their global folder, order within a
        //    folder preserved) so only ONE folder's uncompressed data is alive at a time. Cab bytes are
        //    loaded lazily below with a single-cab cache; with purge, refcounts delete each cab file
        //    the moment its last folder finishes — the cabs and the (larger) extracted game still never
        //    fully coexist on disk, without holding either in memory.
        string? cachedCabPath = null; byte[]? cachedCabBytes = null;
        byte[] LoadCab(string path)
        {
            if (path != cachedCabPath) { cachedCabBytes = File.ReadAllBytes(path); cachedCabPath = path; }
            return cachedCabBytes!;
        }

        // How many folders still need each cab (a folder can span cabs, a cab can hold many folders).
        var cabRefs = new Dictionary<string, int>();
        foreach (var folder in folders)
            foreach (var p in folder.Blocks.Select(b => b.CabPath).Distinct())
                cabRefs[p] = cabRefs.GetValueOrDefault(p) + 1;

        int total = files.Count, done = 0;
        foreach (var group in files.GroupBy(f => f.GlobalFolder).OrderBy(g => g.Key))
        {
            ct.ThrowIfCancellationRequested();
            var data = DecompressFolder(folders[group.Key].Blocks, LoadCab);
            foreach (var f in group)
            {
                ct.ThrowIfCancellationRequested();
                long start = Math.Min(f.UOff, data.Length);
                long len = Math.Min(f.USize, data.Length - start);

                // Canonicalize + relative-path containment (shared with the zip extractor): rejects
                // traversal, rooted paths, and sibling-prefix escapes — the old StartsWith check accepted
                // a sibling whose name began with the destination's.
                var dest = ArchivePathGuard.ResolveContainedPath(to, f.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var fs = File.Create(dest)) fs.Write(data, (int)start, (int)len);

                done++;
                progress?.Report(new ProgressReport("cab", total > 0 ? (double)done / total : 1.0, f.Name));
            }

            // This folder is done — release any cab no other folder still needs.
            if (purge)
                foreach (var p in folders[group.Key].Blocks.Select(b => b.CabPath).Distinct())
                    if (--cabRefs[p] == 0)
                    {
                        if (p == cachedCabPath) { cachedCabPath = null; cachedCabBytes = null; }
                        try { File.Delete(p); } catch { }
                    }
        }

        // Backstop: folders with no files never decrement their refcounts — sweep whatever remains.
        if (purge) foreach (var p in processed) try { if (File.Exists(p)) File.Delete(p); } catch { }

        progress?.Report(new ProgressReport("cab", 1.0, "done"));
    }

    private static byte[] DecompressFolder(List<Block> blocks, Func<string, byte[]> loadCab)
    {
        using var outMs = new MemoryStream();
        byte[] window = Array.Empty<byte>();
        byte[] pending = Array.Empty<byte>();
        foreach (var blk in blocks)
        {
            var raw = new ReadOnlySpan<byte>(loadCab(blk.CabPath), blk.Offset, blk.CBytes);
            if (blk.UcBytes == 0) { pending = raw.Slice(2).ToArray(); continue; } // split → carry to next cab

            byte[] payload;
            if (pending.Length > 0)
            {
                payload = new byte[pending.Length + raw.Length];
                Buffer.BlockCopy(pending, 0, payload, 0, pending.Length);
                raw.CopyTo(payload.AsSpan(pending.Length));
                pending = Array.Empty<byte>();
            }
            else
            {
                if (raw.Length < 2 || raw[0] != (byte)'C' || raw[1] != (byte)'K')
                    throw new InvalidDataException("Bad MSZIP block signature.");
                payload = raw.Slice(2).ToArray();
            }

            var outB = InflatePrimed(payload, window);
            outMs.Write(outB);
            window = TailWindow(window, outB);
        }
        return outMs.ToArray();
    }

    private static byte[] InflatePrimed(byte[] deflate, byte[] window)
    {
        if (window.Length == 0) return Inflate(deflate);
        int dl = Math.Min(32768, window.Length), ds = window.Length - dl;
        var primed = StoredBlocks(window, ds, dl);
        var comb = new byte[primed.Length + deflate.Length];
        Buffer.BlockCopy(primed, 0, comb, 0, primed.Length);
        Buffer.BlockCopy(deflate, 0, comb, primed.Length, deflate.Length);
        var all = Inflate(comb);
        var outB = new byte[all.Length - dl];
        Buffer.BlockCopy(all, dl, outB, 0, outB.Length);
        return outB;
    }

    private static byte[] TailWindow(byte[] prev, byte[] add)
    {
        const int W = 32768;
        if (prev.Length == 0 && add.Length >= W) { var t = new byte[W]; Buffer.BlockCopy(add, add.Length - W, t, 0, W); return t; }
        int keep = Math.Min(W, prev.Length + add.Length);
        var win = new byte[keep];
        int fromPrev = Math.Max(0, keep - add.Length);
        if (fromPrev > 0) Buffer.BlockCopy(prev, prev.Length - fromPrev, win, 0, fromPrev);
        Buffer.BlockCopy(add, Math.Max(0, add.Length - (keep - fromPrev)), win, fromPrev, keep - fromPrev);
        return win;
    }

    private static byte[] Inflate(byte[] deflate)
    {
        using var inMs = new MemoryStream(deflate);
        using var ds = new DeflateStream(inMs, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        ds.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static byte[] StoredBlocks(byte[] d, int start, int len)
    {
        using var ms = new MemoryStream(); int p = start, e = start + len;
        while (p < e)
        {
            int c = Math.Min(65535, e - p);
            ms.WriteByte(0x00);
            ms.WriteByte((byte)(c & 0xFF)); ms.WriteByte((byte)((c >> 8) & 0xFF));
            ms.WriteByte((byte)(~c & 0xFF)); ms.WriteByte((byte)((~c >> 8) & 0xFF));
            ms.Write(d, p, c); p += c;
        }
        return ms.ToArray();
    }

    /// <summary>Parses one cabinet's header/folder/file tables. The cab's bytes are read transiently —
    /// blocks reference the FILE PATH (+ offset), so nothing from <paramref name="file"/> stays resident.</summary>
    private static CabMeta Parse(string file)
    {
        var d = File.ReadAllBytes(file);
        var meta = new CabMeta();
        uint coffFiles = BitConverter.ToUInt32(d, 16);
        ushort cFolders = BitConverter.ToUInt16(d, 26);
        ushort cFiles = BitConverter.ToUInt16(d, 28);
        ushort flags = BitConverter.ToUInt16(d, 30);
        meta.Prev = (flags & FlagPrevCabinet) != 0;
        int read = 36; byte cbCFFolder = 0;
        if ((flags & FlagReservePresent) != 0)
        {
            ushort cbH = BitConverter.ToUInt16(d, read); read += 2;
            cbCFFolder = d[read]; read += 2; read += cbH;
        }
        if ((flags & FlagPrevCabinet) != 0) { read = SkipCStr(d, read); read = SkipCStr(d, read); }
        if ((flags & FlagNextCabinet) != 0) { (meta.Next, read) = ReadCStr(d, read); read = SkipCStr(d, read); }

        for (int i = 0; i < cFolders; i++)
        {
            uint cfo = BitConverter.ToUInt32(d, read); read += 4;
            ushort cbl = BitConverter.ToUInt16(d, read); read += 2; read += 2; read += cbCFFolder;
            var blocks = new List<Block>();
            int p = (int)cfo;
            for (int j = 0; j < cbl; j++)
            {
                p += 4;
                ushort cB = BitConverter.ToUInt16(d, p); p += 2;
                ushort ucB = BitConverter.ToUInt16(d, p); p += 2;
                blocks.Add(new Block { CabPath = file, Offset = p, CBytes = cB, UcBytes = ucB });
                p += cB;
            }
            meta.Folders.Add(blocks);
        }

        int fp = (int)coffFiles;
        for (int i = 0; i < cFiles; i++)
        {
            uint usize = BitConverter.ToUInt32(d, fp);
            uint uoff = BitConverter.ToUInt32(d, fp + 4);
            ushort ifolder = BitConverter.ToUInt16(d, fp + 8);
            fp += 16;
            (string name, fp) = ReadCStr(d, fp);
            meta.Files.Add((usize, uoff, ifolder, name.Replace('\\', '/')));
        }
        return meta;
    }

    private static int SkipCStr(byte[] d, int p) { while (d[p] != 0) p++; return p + 1; }
    private static (string, int) ReadCStr(byte[] d, int p)
    { int s = p; while (d[p] != 0) p++; return (System.Text.Encoding.ASCII.GetString(d, s, p - s), p + 1); }
}
