using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Extraction;

namespace OpenSO.Launcher.Services.Updates;

/// <summary>
/// Headless, transactional delta-update engine — the in-launcher replacement for the legacy Windows
/// <c>update.exe</c> patcher (FSO.Patcher.ReversiblePatcher). It applies the manifest's incremental patch
/// package(s) to an existing install with full transactional semantics, reproducing the patcher's
/// advance-per-patch behaviour without ever invoking <c>update.exe</c>.
///
/// <para><b>Delta package format</b> (confirmed from FSO.DeltaGen + gen-manifest.py): each release's
/// per-RID manifest (<c>openso-manifest.json</c>) may carry, for <c>win-x64</c> only, a single
/// <c>deltas: [{ from, url, sha256 }]</c> entry whose <c>from</c> is the immediately-previous release and
/// whose <c>url</c> is <c>OpenSO-client-win-x64.incremental.zip</c> (the SHA-256 is of that zip). The
/// incremental zip contains ONLY the Add + Modify files at their install-relative paths — no removals,
/// and never the patcher's own <c>update.*</c> files. Removals live in a SEPARATE, hash-less sibling asset
/// <c>OpenSO-client-win-x64.manifest.json</c> (shape <c>{ Version, Diffs: [{ DiffType, Path }] }</c>,
/// <c>DiffType == 2</c> = Remove), reachable by swapping <c>.incremental.zip</c> → <c>.manifest.json</c>
/// on the delta url.</para>
///
/// <para><b>Multi-hop</b>: the legacy system supports patch CHAINS (installed → … → target). Because each
/// manifest's single delta is a back-link (<c>from</c> = the previous release), a chain is discovered by
/// walking per-release manifests backwards from the target tag until <c>from</c> == the installed version
/// (see <see cref="SelectDeltaChainAsync"/>). Every hop carries its own SHA-256, so every hop is
/// hash-verified before any mutation — a property the userapi/update feed (no per-asset hash) can't give,
/// which is why chain discovery is manifest-first rather than feed-driven. Each hop is applied by its own
/// transaction and the version marker advances per hop, so a mid-chain failure leaves a CONSISTENT install
/// at the last completed hop; the caller then falls back to the target's full package.</para>
///
/// The design is platform-neutral; delta USAGE is gated to Windows (win-x64) at the call site per the
/// update plan. All extraction routes through the shared <see cref="ArchivePathGuard"/> policy.
/// </summary>
public sealed class DeltaUpdateEngine
{
    private readonly LauncherConfig _config;
    private static readonly HttpClient Http = new();

    public DeltaUpdateEngine(LauncherConfig config) => _config = config;

    /// <summary>
    /// User-owned files the update must NEVER overwrite or remove — the same set the full-install path
    /// keeps (<c>FsoInstaller.CarryOverUserData</c>'s keepUserCopy) and the legacy patcher's IgnoreFiles.
    /// Everything else a delta touches is a game file the release ships. Files the delta does NOT list
    /// (saves, the remesh pack, the mesh cache) are inherently untouched — a delta only carries changed
    /// game files and its removal manifest only lists game files that left the release.
    /// </summary>
    internal static readonly HashSet<string> ProtectedUserFiles =
        new(StringComparer.OrdinalIgnoreCase) { "Content/config.ini", "NLog.config" };

    public readonly record struct DeltaResult(bool Success, string? Error);

    /// <summary>One hop of a patch chain: apply the delta at <see cref="Url"/> (hash <see cref="Sha256"/>)
    /// to advance the install from <see cref="From"/> to <see cref="To"/>.</summary>
    internal readonly record struct DeltaHop(string From, string To, string Url, string Sha256);

    /// <summary>A hop whose delta zip (and optional removal manifest) has been downloaded + verified to
    /// local paths, ready for <see cref="ApplyChain"/>.</summary>
    public readonly record struct StagedHop(string ToVersion, string IncrementalZipPath, string? RemovalManifestPath);

    // ------------------------------------------------------------------------------------------------
    // Orchestration (network) — used by the launcher's update flow. Windows-gated at the call site.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Discovers the delta chain installed → target from the per-release manifests, stages (downloads +
    /// SHA-256-verifies) every hop, then applies them in order. Returns <c>true</c> when the FULL chain
    /// applied and the install is at <paramref name="targetVersion"/>; <c>false</c> on any condition that
    /// means the caller should fall back to the full package (no chain, a gap/missing delta, a hash
    /// mismatch, a mid-chain apply failure, or the chain being impractically long). A mid-chain apply
    /// failure leaves the install CONSISTENT at the last completed hop (never half-applied), so the full
    /// fallback simply completes it.
    /// </summary>
    public async Task<bool> TryDeltaUpdateAsync(string installDir, string? installedVersion,
        string targetVersion, string rid, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installedVersion)) return false; // no chain start (pre-version.txt install)
        try
        {
            progress.Report(new ProgressReport("delta", 0, "Checking for an incremental update…"));
            var chain = await SelectDeltaChainAsync(installedVersion!, targetVersion, rid,
                tag => FetchManifestByTagAsync(tag, ct), ct: ct);
            if (chain == null || chain.Count == 0) return false; // → full

            var staged = new List<StagedHop>();
            var temps = new List<string>();
            try
            {
                // Stage EVERY hop (download + hash-verify) before any mutation. A download/hash failure here
                // aborts with the install untouched; DownloadService deletes a mismatched file and throws.
                for (int i = 0; i < chain.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var hop = chain[i];
                    double lo = 0.55 * i / chain.Count, hi = 0.55 * (i + 1) / chain.Count;
                    var zip = TempPath($"openso-delta-{i}", ".zip");
                    temps.Add(zip);
                    await new DownloadService(hop.Url, zip, expectedSha256: hop.Sha256)
                        .RunAsync(Scale(progress, "delta", lo, hi, $"Downloading update {i + 1}/{chain.Count}… "), ct);

                    // The removal manifest is a hash-less sibling asset; its absence just means "no removals"
                    // for this hop (matching the legacy patcher). Best-effort — never fail the hop over it.
                    string? man = null;
                    try
                    {
                        var mp = TempPath($"openso-delta-{i}", ".json");
                        await new DownloadService(RemovalManifestUrlFor(hop.Url), mp).RunAsync(null, ct);
                        man = mp; temps.Add(mp);
                    }
                    catch { man = null; }

                    staged.Add(new StagedHop(hop.To, zip, man));
                }

                progress.Report(new ProgressReport("delta", 0.55, "Applying incremental update(s)…"));
                var r = ApplyChain(installDir, staged, Scale(progress, "delta", 0.55, 1.0), ct);
                return r.Success;
            }
            finally { foreach (var t in temps) TryDelete(t); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; } // ANY delta failure (network, hash, apply) → caller falls back to the full package
    }

    /// <summary>Fetches the per-release <c>openso-manifest.json</c> for a specific tag. Null when the URL
    /// can't be derived (non-GitHub mirror) or the fetch fails/returns non-success.</summary>
    private async Task<string?> FetchManifestByTagAsync(string tag, CancellationToken ct)
    {
        var url = PerTagManifestUrl(_config.ClientManifestUrl, tag);
        if (url == null) return null;
        try
        {
            using var resp = await GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------------------------------------
    // Chain discovery (manifest-first, back-link walk). Testable via an injected manifest fetcher.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the ordered (oldest → newest) chain of delta hops that advances <paramref name="installedVersion"/>
    /// to <paramref name="targetVersion"/> for <paramref name="rid"/>, by fetching the target's manifest and
    /// walking its <c>deltas[].from</c> back-links release-by-release until a hop's <c>from</c> equals the
    /// installed version. Returns null (→ full fallback) if any intermediate manifest is missing, has no
    /// delta for this RID, describes the wrong version, or the walk exceeds <paramref name="maxHops"/>.
    /// </summary>
    internal static async Task<List<DeltaHop>?> SelectDeltaChainAsync(string installedVersion, string targetVersion,
        string rid, Func<string, Task<string?>> fetchManifestByTag, int maxHops = 12, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(targetVersion)) return null;
        if (VersionEquals(installedVersion, targetVersion)) return null; // already at target — nothing to apply

        var hops = new List<DeltaHop>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tag = targetVersion.Trim();

        for (int i = 0; i < maxHops; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(NormalizeVersion(tag))) return null; // cycle guard

            var json = await fetchManifestByTag(tag);
            if (json == null) return null;                       // missing intermediate manifest → gap → full

            var parsed = ParseManifestDelta(json, rid);
            if (parsed == null) return null;                     // malformed → full
            var (version, hop) = parsed.Value;
            if (!VersionEquals(version, tag)) return null;       // manifest doesn't describe the tag we asked for → full
            if (hop == null) return null;                        // no delta for this RID at this hop → gap → full

            hops.Add(hop.Value);
            if (VersionEquals(hop.Value.From, installedVersion))
            {
                hops.Reverse();                                  // installed → … → target
                return hops;
            }
            tag = hop.Value.From;                                // walk back one release
        }
        return null; // chain too long to be practical → full is the safe answer
    }

    /// <summary>Reads a per-release manifest's <c>version</c> and this RID's single delta (if any). Returns
    /// null on malformed JSON / non-schema-1 shape (caller treats as "no chain" → full).</summary>
    private static (string Version, DeltaHop? Hop)? ParseManifestDelta(string json, string rid)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
                && sv.TryGetInt32(out var schema) && schema != 1) return null; // unknown schema → full
            if (!root.TryGetProperty("version", out var vEl) || vEl.ValueKind != JsonValueKind.String) return null;
            var version = vEl.GetString();
            if (string.IsNullOrWhiteSpace(version)) return null;

            DeltaHop? hop = null;
            if (root.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Object
                && clients.TryGetProperty(rid, out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("deltas", out var deltas) && deltas.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in deltas.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    if (d.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
                        && d.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                        && d.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String)
                    {
                        var from = f.GetString(); var url = u.GetString(); var sha = h.GetString();
                        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(sha))
                        {
                            hop = new DeltaHop(from!, version!, url!, sha!.Trim().ToLowerInvariant());
                            break; // one delta per manifest today — take the first well-formed one
                        }
                    }
                }
            }
            return (version!, hop);
        }
        catch { return null; }
    }

    /// <summary>Derives the per-tag manifest URL from <see cref="LauncherConfig.ClientManifestUrl"/> using the
    /// documented GitHub asset layout (<c>/releases/latest/download/…</c> ↔ <c>/releases/download/&lt;tag&gt;/…</c>).
    /// Null for a mirror URL we can't rewrite — multi-hop delta is then unavailable and the caller uses full.</summary>
    internal static string? PerTagManifestUrl(string clientManifestUrl, string tag)
    {
        if (string.IsNullOrWhiteSpace(clientManifestUrl) || string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();

        const string latest = "/releases/latest/download/";
        var i = clientManifestUrl.IndexOf(latest, StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
            return clientManifestUrl[..i] + "/releases/download/" + tag + "/" + clientManifestUrl[(i + latest.Length)..];

        const string dl = "/releases/download/";
        var j = clientManifestUrl.IndexOf(dl, StringComparison.OrdinalIgnoreCase);
        if (j >= 0)
        {
            var after = j + dl.Length;
            var slash = clientManifestUrl.IndexOf('/', after);
            if (slash > after) return clientManifestUrl[..after] + tag + clientManifestUrl[slash..];
        }
        return null;
    }

    /// <summary>The removal manifest sits next to the incremental zip: <c>…incremental.zip</c> → <c>…manifest.json</c>.</summary>
    internal static string RemovalManifestUrlFor(string incrementalUrl) =>
        incrementalUrl.Replace(".incremental.zip", ".manifest.json", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------------------------------------
    // Chain application (no network) — advance-per-hop, each hop its own transaction.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies staged hops in order, each via its own <see cref="ApplyDelta"/> transaction. The version
    /// marker advances only as each hop fully succeeds, so a failed hop is rolled back and the install is
    /// left CONSISTENT at the previous hop's version (never half-applied). Stops and reports on the first
    /// failing hop; the caller then falls back to the target's full package.
    /// </summary>
    public DeltaResult ApplyChain(string installDir, IReadOnlyList<StagedHop> hops,
        IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        for (int i = 0; i < hops.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var hop = hops[i];
            progress?.Report(new ProgressReport("delta",
                hops.Count > 0 ? (double)i / hops.Count : 0, $"Applying update {i + 1}/{hops.Count} → {hop.ToVersion}…"));

            var r = ApplyDelta(installDir, hop.IncrementalZipPath, hop.RemovalManifestPath, hop.ToVersion, progress, ct);
            if (!r.Success)
                return new DeltaResult(false,
                    $"Incremental update {i + 1}/{hops.Count} (→ {hop.ToVersion}) failed: {r.Error} " +
                    "The install was rolled back to the last completed version.");
        }
        progress?.Report(new ProgressReport("delta", 1.0, "Incremental update(s) applied."));
        return new DeltaResult(true, null);
    }

    // ------------------------------------------------------------------------------------------------
    // Single-hop transactional core (no network) — the heart of the engine.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Transactionally applies ONE delta package to <paramref name="installDir"/>. Lifecycle:
    /// <list type="number">
    /// <item><b>validate</b> — open the incremental zip and validate EVERY entry with <see cref="ArchivePathGuard"/>
    ///   (containment + symlink/special rejection); parse + sanitise the removal manifest. No mutation yet —
    ///   an adversarial/invalid archive is refused here with nothing written.</item>
    /// <item><b>backup + apply</b> — for each Add/Modify entry (skipping protected user files and deferring
    ///   the version marker), back up any file it overwrites, then extract it. New files record a delete-undo.</item>
    /// <item><b>remove</b> — for each sanitised removal path (never a protected user file), back it up then
    ///   delete it. A removal that FAILS fails the whole transaction.</item>
    /// <item><b>finalize</b> — write <c>version.txt</c> LAST (the commit point), so the marker advances only
    ///   on full success.</item>
    /// </list>
    /// On ANY failure every step is undone in reverse (backups restored, added files deleted, removed files
    /// restored), leaving the install byte-identical to its pre-update state, and a clear error is returned.
    /// The caller (the delta chain / full fallback) is expected to have already SHA-256-verified
    /// <paramref name="incrementalZipPath"/> against the manifest before calling this.
    /// </summary>
    public DeltaResult ApplyDelta(string installDir, string incrementalZipPath, string? removalManifestPath,
        string? targetVersion, IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        installDir = Path.GetFullPath(installDir);

        ZipArchive archive;
        try { archive = ZipFile.OpenRead(incrementalZipPath); }
        catch (Exception ex) { return new DeltaResult(false, $"the delta archive could not be opened ({ex.Message})."); }

        using (archive)
        {
            // ---- PASS 1: validate the WHOLE archive (no mutation). Reject on the first bad entry. ----
            var plan = new List<(ZipArchiveEntry Entry, string Target, string Rel)>();
            ZipArchiveEntry? versionEntry = null;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    ArchivePathGuard.RejectIfLinkOrSpecial(entry);
                    var target = ArchivePathGuard.ResolveContainedPath(installDir, entry.FullName);
                    bool isDir = entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);
                    if (isDir) continue; // directories are materialised on demand when writing files
                    var rel = NormalizeRel(entry.FullName);
                    if (IsVersionFile(rel)) { versionEntry = entry; continue; } // defer the marker to finalize
                    plan.Add((entry, target, rel));
                }
                catch (Exception ex)
                {
                    return new DeltaResult(false, $"the delta archive was rejected for safety ({entry.FullName}: {ex.Message}).");
                }
            }

            // ---- Parse + sanitise removals (no mutation). Unsafe or protected paths are dropped, not acted on. ----
            var removals = new List<string>();
            if (removalManifestPath != null && File.Exists(removalManifestPath))
            {
                foreach (var raw in ReadRemovedPaths(removalManifestPath))
                {
                    var rel = SafeRelativePath(raw);
                    if (rel == null) continue;              // unsafe removal path — never act on it
                    if (IsProtectedUserFile(rel)) continue; // never remove a user-owned file
                    removals.Add(rel);
                }
            }

            // ---- Backup store + undo stack for full rollback. ----
            var trimmed = installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            var parent = Directory.GetParent(trimmed)?.FullName ?? Path.GetTempPath();
            var backupDir = Path.Combine(parent, $".{name}.updatebackup-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Guid.NewGuid():N}");
            var undo = new List<Action>();
            var createdDirs = new List<string>();

            try
            {
                Directory.CreateDirectory(backupDir);

                // ---- APPLY additions / modifications ----
                int total = plan.Count, done = 0;
                foreach (var (entry, target, rel) in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsProtectedUserFile(rel)) continue; // never overwrite the user's config (patcher IgnoreFiles)

                    EnsureDir(Path.GetDirectoryName(target)!, createdDirs);
                    if (File.Exists(target))
                    {
                        var bak = BackupPathFor(backupDir, rel);
                        EnsureDir(Path.GetDirectoryName(bak)!, null);
                        File.Copy(target, bak, overwrite: true);
                        var t = target;
                        undo.Add(() => { try { File.Copy(bak, t, overwrite: true); } catch { } });
                    }
                    else
                    {
                        var t = target;
                        undo.Add(() => { try { if (File.Exists(t)) File.Delete(t); } catch { } });
                    }

                    using (var inS = entry.Open())
                    using (var outS = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                        inS.CopyTo(outS, 81920);

                    done++;
                    progress?.Report(new ProgressReport("delta", total > 0 ? 0.85 * done / total : 0.85, entry.Name));
                }

                // ---- REMOVALS (a failed removal fails the whole transaction) ----
                foreach (var rel in removals)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.Combine(installDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(target)) continue; // already gone — nothing to do
                    var bak = BackupPathFor(backupDir, rel);
                    EnsureDir(Path.GetDirectoryName(bak)!, null);
                    File.Copy(target, bak, overwrite: true);
                    File.Delete(target);
                    var t = target;
                    undo.Add(() => { try { File.Copy(bak, t, overwrite: true); } catch { } });
                }

                // ---- FINALIZE: version marker LAST (the commit point) ----
                WriteVersionMarker(installDir, versionEntry, targetVersion, backupDir, undo);

                progress?.Report(new ProgressReport("delta", 1.0, "Delta applied."));
                return new DeltaResult(true, null);
            }
            catch (Exception ex)
            {
                Rollback(undo, createdDirs);
                if (ex is OperationCanceledException) throw;
                return new DeltaResult(false, ex.Message);
            }
            finally { TryDeleteDir(backupDir); }
        }
    }

    /// <summary>Writes <c>version.txt</c> as the last mutation: the zip's version.txt bytes when the delta
    /// ships one (authoritative, CI-stamped), else the target version string. Backs up / records undo so a
    /// later-stage failure restores the previous marker (and rollback leaves the pre-update version).</summary>
    private static void WriteVersionMarker(string installDir, ZipArchiveEntry? versionEntry,
        string? targetVersion, string backupDir, List<Action> undo)
    {
        var vt = Path.Combine(installDir, "version.txt");
        if (File.Exists(vt))
        {
            var bak = BackupPathFor(backupDir, "version.txt");
            EnsureDir(Path.GetDirectoryName(bak)!, null);
            File.Copy(vt, bak, overwrite: true);
            undo.Add(() => { try { File.Copy(bak, vt, overwrite: true); } catch { } });
        }
        else
        {
            undo.Add(() => { try { if (File.Exists(vt)) File.Delete(vt); } catch { } });
        }

        byte[] bytes;
        if (versionEntry != null)
        {
            using var vs = versionEntry.Open();
            using var ms = new MemoryStream();
            vs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes((targetVersion ?? "").Trim());
        }
        File.WriteAllBytes(vt, bytes);
    }

    /// <summary>Undoes every recorded mutation in reverse order, then removes any now-empty directories the
    /// apply created (deepest first) — restoring the install byte-identical to its pre-update state.</summary>
    private static void Rollback(List<Action> undo, List<string> createdDirs)
    {
        for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
        foreach (var d in createdDirs.OrderByDescending(x => x.Length))
        {
            try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d, false); }
            catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Removal-manifest reading (port of FSO.Patcher.UpdateManifest.GetRemovedPaths + SafeRelativePath).
    // ------------------------------------------------------------------------------------------------

    /// <summary>Returns the install-relative paths this hop's manifest says were removed (Diffs with
    /// <c>DiffType == 2</c>). A missing/unparsable manifest just means "nothing to remove".</summary>
    internal static List<string> ReadRemovedPaths(string manifestPath)
    {
        const int RemoveDiffType = 2; // FileDiffType: Add=0, Modify=1, Remove=2, Unchanged=3 (numeric on the wire)
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("Diffs", out var diffs) || diffs.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var d in diffs.EnumerateArray())
                if (d.TryGetProperty("DiffType", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == RemoveDiffType
                    && d.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String)
                    result.Add(p.GetString()!);
        }
        catch { /* corrupt/partial manifest → skip removals rather than aborting the hop */ }
        return result;
    }

    /// <summary>Rejects anything not safely relative to the install root: absolute/rooted paths and ".."
    /// segments. The manifest is CI-authored but arrives as a downloaded file, so it is untrusted.</summary>
    internal static string? SafeRelativePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Replace('\\', '/');
        while (p.StartsWith("./")) p = p[2..]; // DeltaGen prefixes root-level files with "./"
        if (p.Length == 0 || Path.IsPathRooted(p)) return null;
        foreach (var seg in p.Split('/'))
            if (seg.Length == 0 || seg == "..") return null;
        return p;
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    internal static bool IsProtectedUserFile(string rel) => ProtectedUserFiles.Contains(NormalizeRel(rel));

    private static bool IsVersionFile(string rel) => rel.Equals("version.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Install-relative, forward-slash, with any leading "./" stripped.</summary>
    private static string NormalizeRel(string entryName)
    {
        var r = entryName.Replace('\\', '/');
        while (r.StartsWith("./")) r = r[2..];
        return r;
    }

    private static string BackupPathFor(string backupDir, string rel) =>
        Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates <paramref name="dir"/> and its missing ancestors, recording each newly-created
    /// directory into <paramref name="created"/> (when non-null) so rollback can remove them if empty.</summary>
    private static void EnsureDir(string dir, List<string>? created)
    {
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;
        EnsureDir(Path.GetDirectoryName(dir)!, created);
        Directory.CreateDirectory(dir);
        created?.Add(dir);
    }

    internal static string NormalizeVersion(string? v) => (v ?? "").Trim().TrimStart('v', 'V').ToLowerInvariant();

    internal static bool VersionEquals(string? a, string? b) =>
        NormalizeVersion(a).Equals(NormalizeVersion(b), StringComparison.Ordinal);

    private static string TempPath(string prefix, string ext) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}{ext}");

    private static Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
        if (url.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_RATELIMIT_TOKEN");
            if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        }
        return Http.SendAsync(req, ct);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDir(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    /// <summary>Maps a child operation's 0..1 progress into a [lo,hi] band of the overall stage.</summary>
    private static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage,
        double lo, double hi, string? prefix = null) =>
        new Progress<ProgressReport>(r =>
            outer.Report(new ProgressReport(stage, lo + (hi - lo) * r.Fraction,
                prefix != null ? prefix + (r.Detail ?? "") : r.Detail)));
}
