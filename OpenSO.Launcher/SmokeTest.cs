using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services;
using OpenSO.Launcher.Services.Extraction;
using OpenSO.Launcher.Services.Installers;
using OpenSO.Launcher.Services.Updates;

namespace OpenSO.Launcher;

/// <summary>
/// Headless self-check for the PUBLISHED (and trimmed) launcher binary. Run as
/// <c>OpenSO.Launcher --smoke</c>: it never starts the Avalonia UI loop; it exercises the
/// trim-sensitive, reflection-adjacent code paths that the compile-linked
/// <c>OpenSO.Launcher.Tests</c> suite cannot catch (those tests compile the same sources fresh, so
/// they never see the trimmer's output). It prints one line per check and exits 0 iff every check
/// passed, non-zero otherwise — CI runs this against the trimmed publish and fails the build on a
/// non-zero exit. See BUILD_AND_TEST.md → "Trimmed-binary smoke gate".
///
/// What it covers (the paths most likely to break under PublishTrimmed / InvariantGlobalization):
///   • System.Text.Json <b>source-generated</b> deserialize of a status payload (camelCase → the
///     case-insensitive LauncherJsonContext) and serialize+deserialize round-trip of LauncherSettings —
///     the only reflection-based serialization in the app, and the single highest-risk trim surface.
///   • JsonDocument (DOM) parse + strict selection of the per-RID client manifest, and the version-only
///     parse; JsonDocument parse of a GitHub release feed + exact-RID asset picking (client + launcher).
///   • RID detection, version comparison, timestamp formatting (all invariant-globalization sensitive).
///   • ArchivePathGuard containment on a real in-memory zip: a safe archive extracts; a traversal
///     archive is rejected whole with nothing written.
/// </summary>
internal static class SmokeTest
{
    public const string Flag = "--smoke";

    public static bool IsRequested(string[]? args)
    {
        if (args == null) return false;
        foreach (var a in args)
            if (string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        Console.WriteLine("OpenSO Launcher — trimmed-binary smoke self-check\n");

        CheckStatusSourceGenDeserialize();
        CheckLauncherSettingsRoundTrip();
        CheckManifestSelectAndVersion();
        CheckReleaseFeedAssetPicking();
        CheckRidDetection();
        CheckVersionComparison();
        CheckTimestampFormatting();
        CheckArchiveGuardOnRealZip();

        Console.WriteLine($"\n{_passed} passed, {_failed} failed.");
        if (_failed == 0) { Console.WriteLine("SMOKE OK"); return 0; }
        Console.WriteLine("SMOKE FAILED");
        return 1;
    }

    // ── individual checks ────────────────────────────────────────────────────────────────────────

    /// <summary>THE critical trim surface: source-generated ServerStatus deserialize (camelCase in →
    /// PascalCase DTO via the case-insensitive context). If trimming stripped the DTO members or the
    /// generated metadata, this silently loses values.</summary>
    private static void CheckStatusSourceGenDeserialize()
    {
        const string json = """
        {
          "serverTime": "2026-07-13T12:34:56Z",
          "gameVersion": "1.2.3",
          "playersOnline": 42,
          "lotsOnline": 7,
          "shards": [ { "id": 1, "name": "Blazing Falls", "status": "Up", "map": "0102", "playersOnline": 42, "lotsOnline": 7 } ],
          "topLots": [ { "shardId": 1, "name": "The Hangout", "location": 12345, "players": 9 } ]
        }
        """;
        try
        {
            var s = JsonSerializer.Deserialize(json, LauncherJsonContext.Default.ServerStatus);
            bool ok = s != null
                      && s.GameVersion == "1.2.3"
                      && s.PlayersOnline == 42
                      && s.LotsOnline == 7
                      && s.Shards is { Length: 1 } && s.Shards[0].Name == "Blazing Falls"
                      && s.Shards[0].Map == "0102" // drives the SERVER STATUS card's city thumbnail
                      && s.TopLots is { Length: 1 } && s.TopLots[0].Players == 9
                      && s.TopLots[0].Location == 12345u;
            Report("ServerStatus source-gen deserialize (case-insensitive, nested arrays)", ok,
                ok ? "" : "deserialized values did not match the payload");
        }
        catch (Exception ex) { Report("ServerStatus source-gen deserialize", false, ex.Message); }
    }

    /// <summary>Source-generated serialize + deserialize round-trip of the persisted settings DTO.</summary>
    private static void CheckLauncherSettingsRoundTrip()
    {
        try
        {
            var original = new LauncherSettings
            {
                GraphicsMode = "DirectX", Enable3D = true, LiveNotifications = false, ClosingBehavior = "Minimize"
            };
            var text = JsonSerializer.Serialize(original, LauncherJsonContext.Default.LauncherSettings);
            var back = JsonSerializer.Deserialize(text, LauncherJsonContext.Default.LauncherSettings);
            bool ok = back != null
                      && back.GraphicsMode == "DirectX" && back.Enable3D
                      && !back.LiveNotifications && back.ClosingBehavior == "Minimize"
                      && text.Contains('\n'); // WriteIndented from the context options
            Report("LauncherSettings source-gen serialize+deserialize round-trip", ok,
                ok ? "" : "round-trip lost or changed a field");
        }
        catch (Exception ex) { Report("LauncherSettings source-gen round-trip", false, ex.Message); }
    }

    /// <summary>Per-RID client manifest: strict select for this platform, tolerant version-only parse,
    /// and a hard-fail on a malformed manifest (never a silent downgrade).</summary>
    private static void CheckManifestSelectAndVersion()
    {
        string manifest = """
        {
          "schemaVersion": 1,
          "version": "1.2.3",
          "clients": {
            "win-x64":   { "full": { "url": "https://example.test/OpenSO-client-win-x64.zip",   "sha256": "AA11" } },
            "linux-x64": { "full": { "url": "https://example.test/OpenSO-client-linux-x64.zip", "sha256": "BB22" } },
            "osx-x64":   { "full": { "url": "https://example.test/OpenSO-client-osx-x64.zip",   "sha256": "CC33" } },
            "osx-arm64": { "full": { "url": "https://example.test/OpenSO-client-osx-arm64.zip", "sha256": "DD44" } }
          }
        }
        """;
        try
        {
            var rid = FsoInstaller.CurrentRid();
            var pkg = FsoInstaller.SelectFromManifest(manifest, rid);
            bool selOk = pkg.Url.Contains(rid) && !string.IsNullOrWhiteSpace(pkg.Sha256)
                         && pkg.Sha256 == pkg.Sha256.ToLowerInvariant();
            Report($"Manifest SelectFromManifest resolves this RID ({rid}) to its full url+sha256", selOk,
                selOk ? "" : $"got url='{pkg.Url}' sha='{pkg.Sha256}'");

            var ver = FsoInstaller.ParseManifestVersion(manifest);
            Report("Manifest ParseManifestVersion reads top-level version", ver == "1.2.3",
                ver == "1.2.3" ? "" : $"got '{ver}'");

            bool hardFailed = false;
            try { FsoInstaller.SelectFromManifest("{ not valid json", rid); }
            catch (InvalidOperationException) { hardFailed = true; }
            Report("Manifest malformed body hard-fails (no silent downgrade)", hardFailed,
                hardFailed ? "" : "malformed manifest did not throw InvalidOperationException");
        }
        catch (Exception ex) { Report("Manifest select/version", false, ex.Message); }
    }

    /// <summary>GitHub release feed: JsonDocument (DOM) parse + exact-RID asset picking for both the
    /// client full zip and the launcher self-update zip.</summary>
    private static void CheckReleaseFeedAssetPicking()
    {
        const string feed = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "OpenSO.Launcher-win-x64.zip",              "browser_download_url": "https://example.test/L-win-x64.zip" },
            { "name": "OpenSO.Launcher-linux-x64.zip",            "browser_download_url": "https://example.test/L-linux-x64.zip" },
            { "name": "OpenSO.Launcher-osx-x64.zip",              "browser_download_url": "https://example.test/L-osx-x64.zip" },
            { "name": "OpenSO.Launcher-osx-arm64.zip",            "browser_download_url": "https://example.test/L-osx-arm64.zip" },
            { "name": "OpenSO-client-win-x64.zip",                "browser_download_url": "https://example.test/C-win-x64.zip" },
            { "name": "OpenSO-client-win-x64.incremental.zip",    "browser_download_url": "https://example.test/C-win-x64-delta.zip" },
            { "name": "OpenSO-client-linux-x64.zip",              "browser_download_url": "https://example.test/C-linux-x64.zip" },
            { "name": "OpenSO-client-osx-x64.zip",                "browser_download_url": "https://example.test/C-osx-x64.zip" },
            { "name": "OpenSO-client-osx-arm64.zip",              "browser_download_url": "https://example.test/C-osx-arm64.zip" }
          ]
        }
        """;
        try
        {
            var assets = new List<(string? name, string? url)>();
            using (var doc = JsonDocument.Parse(feed))
            {
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    assets.Add((a.GetProperty("name").GetString(), a.GetProperty("browser_download_url").GetString()));
            }

            var launcherRid = SelfUpdateService.CurrentRid();
            var launcherUrl = SelfUpdateService.PickLauncherAsset(assets, launcherRid);
            bool lOk = launcherUrl != null && launcherUrl.Contains(launcherRid);
            Report($"Release feed → launcher asset pick (exact RID {launcherRid})", lOk,
                lOk ? "" : $"got '{launcherUrl}'");

            var clientRid = FsoInstaller.CurrentRid();
            var clientUrl = FsoInstaller.PickFullClientAsset(assets, clientRid);
            bool cOk = clientUrl != null && clientUrl.Contains(clientRid)
                       && !clientUrl.Contains("incremental") && !clientUrl.Contains("delta");
            Report($"Release feed → client full-zip pick (exact RID {clientRid}, not the delta)", cOk,
                cOk ? "" : $"got '{clientUrl}'");
        }
        catch (Exception ex) { Report("Release feed asset picking", false, ex.Message); }
    }

    private static void CheckRidDetection()
    {
        try
        {
            var fso = FsoInstaller.CurrentRid();
            var self = SelfUpdateService.CurrentRid();
            var known = new HashSet<string> { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x86", "linux-x86" };
            bool ok = known.Contains(fso) && self.Contains('-');
            Report($"RID detection (FsoInstaller='{fso}', SelfUpdate='{self}')", ok,
                ok ? "" : "RID not in the expected shape");
        }
        catch (Exception ex) { Report("RID detection", false, ex.Message); }
    }

    private static void CheckVersionComparison()
    {
        try
        {
            bool ok = DeltaUpdateEngine.NeedsUpdate("1.2.2", "1.2.3")          // behind → needs update
                      && !DeltaUpdateEngine.NeedsUpdate("1.2.3", "1.2.3")      // current → no update
                      && DeltaUpdateEngine.NeedsUpdate(null, "1.2.3")          // unstamped install → needs update
                      && !DeltaUpdateEngine.NeedsUpdate("1.2.3", "")           // unknown required → no guess
                      && DeltaUpdateEngine.VersionEquals("v1.2.3", "1.2.3")    // leading-v tolerant
                      && DeltaUpdateEngine.NormalizeVersion("V2.0.0") == "2.0.0";
            Report("Version comparison (NeedsUpdate / VersionEquals / NormalizeVersion)", ok,
                ok ? "" : "a version comparison returned the wrong result");
        }
        catch (Exception ex) { Report("Version comparison", false, ex.Message); }
    }

    private static void CheckTimestampFormatting()
    {
        try
        {
            var formatted = StatusDisplay.FormatLastUpdated(new DateTime(2026, 7, 13, 9, 5, 3));
            var empty = StatusDisplay.FormatLastUpdated(null);
            var clock = new DateTime(2026, 7, 13, 15, 45, 0).ToString("h:mm tt"); // MainViewModel clock format, invariant-sensitive
            bool ok = formatted == "Updated 09:05:03" && empty == "Updated —"
                      && (clock == "3:45 PM" || clock == "3:45 pm");
            Report($"Timestamp formatting (invariant): '{formatted}', clock '{clock}'", ok,
                ok ? "" : "a formatted timestamp did not match the expected invariant output");
        }
        catch (Exception ex) { Report("Timestamp formatting", false, ex.Message); }
    }

    /// <summary>Real end-to-end zip through ArchivePathGuard: a safe archive extracts; a traversal
    /// archive is rejected whole with nothing written to disk.</summary>
    private static void CheckArchiveGuardOnRealZip()
    {
        var work = Path.Combine(Path.GetTempPath(), "openso-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);

            // 1) SAFE zip → extracts.
            var safeZip = Path.Combine(work, "safe.zip");
            using (var fs = new FileStream(safeZip, FileMode.Create))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(z.CreateEntry("sub/dir/hello.txt").Open());
                w.Write("hi");
            }
            var safeOut = Path.Combine(work, "safe-out");
            ZipExtractor.ExtractAsync(safeZip, safeOut).GetAwaiter().GetResult();
            bool safeOk = File.Exists(Path.Combine(safeOut, "sub", "dir", "hello.txt"));
            Report("ArchivePathGuard: safe in-memory zip extracts", safeOk,
                safeOk ? "" : "expected extracted file was not present");

            // 2) TRAVERSAL zip → rejected, nothing written.
            var evilZip = Path.Combine(work, "evil.zip");
            using (var fs = new FileStream(evilZip, FileMode.Create))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // ZipArchive normalizes '\' but not '../', so craft the entry name directly.
                using var w = new StreamWriter(z.CreateEntry("../escape.txt").Open());
                w.Write("pwned");
            }
            var evilOut = Path.Combine(work, "evil-out");
            bool rejected = false;
            try { ZipExtractor.ExtractAsync(evilZip, evilOut).GetAwaiter().GetResult(); }
            catch (IOException) { rejected = true; }
            bool nothingWritten = !File.Exists(Path.Combine(work, "escape.txt"));
            Report("ArchivePathGuard: traversal zip rejected, nothing written", rejected && nothingWritten,
                (rejected ? "" : "traversal entry was not rejected; ") + (nothingWritten ? "" : "a file escaped the destination"));

            // 3) Direct guard calls.
            bool directOk;
            try
            {
                ArchivePathGuard.ResolveContainedPath(evilOut, "a/b/c.txt"); // safe
                bool threw = false;
                try { ArchivePathGuard.ResolveContainedPath(evilOut, "../x"); } catch (IOException) { threw = true; }
                directOk = threw;
            }
            catch { directOk = false; }
            Report("ArchivePathGuard: ResolveContainedPath accepts safe, rejects '..'", directOk,
                directOk ? "" : "direct containment check behaved unexpectedly");
        }
        catch (Exception ex) { Report("ArchivePathGuard on real zip", false, ex.Message); }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    // ── reporting ────────────────────────────────────────────────────────────────────────────────

    private static void Report(string name, bool ok, string detail)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}{(string.IsNullOrEmpty(detail) ? "" : "  — " + detail)}"); }
    }
}
