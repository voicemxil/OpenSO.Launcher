using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services;
using OpenSO.Launcher.Services.Extraction;
using OpenSO.Launcher.Services.Installers;
using OpenSO.Launcher.Services.Updates;

namespace OpenSO.Launcher.Tests;

/// <summary>
/// Headless test runner for the launcher's portable logic. Run with `dotnet run` to exercise the
/// ported services without the GUI. Returns non-zero on the first failure (CI-friendly).
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.WriteLine("OpenSO Launcher — logic tests\n");

        await Test("ZipExtractor round-trips a nested zip", TestZip);
        await Test("CabExtractor extracts an MSZIP cab (if a sample is provided)", TestCab);
        await Test("DownloadService rejects a wrong MD5", TestMd5Mismatch);
        await Test("DownloadService accepts a correct MD5", TestMd5Match);
        await Test("InstallStateService probes without throwing", TestInstallState);
        Test("Dependency graph resolves FSO deps for the current OS", TestDependencyGraph);
        Test("LauncherConfig points at OpenSO endpoints", TestConfig);
        Test("GameLauncher fails clearly on a missing install", TestLaunchMissing);
        Test("RegistryWriter is safe to call on any OS", TestRegistryWriter);
        Test("FsoInstaller.VerifyClientInstall rejects a truncated client", TestVerifyRejectsTruncated);
        Test("FsoInstaller.VerifyClientInstall accepts a complete client", TestVerifyAcceptsComplete);
        Test("FsoInstaller.VerifyClientInstall accepts the macOS code-only OpenSO.app layout", TestVerifyAcceptsMacBundle);
        Test("FsoInstaller.SwapIntoPlace replaces install + preserves a backup", TestSwapIntoPlace);
        Test("FsoInstaller.CarryOverUserData keeps user files/config, drops code + stray patches", TestCarryOverUserData);
        Test("FsoInstaller.PickFullClientAsset picks the full zip, not the delta/manifest", TestPickFullClientAsset);
        Test("UpdatePath.FindPath walks the incremental chain like the in-client updater", TestFindPathIncrementalChain);
        Test("UpdatePath.FindPath falls back to a full-zip start for unknown versions", TestFindPathFullZipFallback);

        // FIX A — exact-RID asset selection (no cross-platform fallback).
        Test("Client asset selection requires an EXACT RID (no cross-platform fallback)", TestExactRidClientAsset);
        Test("Launcher asset selection requires an EXACT RID (no cross-platform fallback)", TestExactRidLauncherAsset);
        await Test("Client resolve errors clearly for an unsupported platform (never a wrong-RID payload)", TestClientResolveUnsupportedPlatformError);
        await Test("Client resolve picks this platform's exact FULL zip when present", TestClientResolveExactRid);
        await Test("Launcher self-update refuses a different platform's build with a clear error", TestLauncherApplyUnsupportedPlatformError);

        // FIX B — ZIP extraction hardening (adversarial fixtures).
        await Test("ZipExtractor rejects ../ path traversal (no escape)", TestZipRejectsTraversal);
        await Test("ZipExtractor rejects absolute/rooted entry paths", TestZipRejectsAbsolute);
        await Test("ZipExtractor rejects sibling-prefix escape (install vs install-evil)", TestZipRejectsSiblingPrefix);
        await Test("ZipExtractor rejects directory-entry traversal", TestZipRejectsDirTraversal);
        await Test("ZipExtractor rejects backslash path tricks", TestZipRejectsBackslash);
        await Test("ZipExtractor rejects symlink entries", TestZipRejectsSymlink);
        await Test("ZipExtractor rejects the WHOLE archive on the first bad entry (no partial extraction)", TestZipNoPartialExtraction);

        // FIX C — canonical per-RID client manifest (openso-manifest.json) is the FIRST update source.
        Test("Manifest resolves each of the four RIDs to ONLY its own full url + sha256", TestManifestResolvesAllRids);
        Test("Manifest missing this RID fails safely (no cross-platform fallback)", TestManifestMissingRid);
        Test("Manifest with an unknown schemaVersion / malformed body fails hard (no downgrade)", TestManifestBadSchemaOrShape);
        await Test("Per-RID manifest is the FIRST source (wins over the GitHub release feed)", TestManifestIsFirstSource);
        await Test("Manifest unavailable falls back to the GitHub exact-RID asset", TestManifestUnavailableFallsBackToGitHub);
        await Test("Client download rejects a wrong SHA-256 and discards the file BEFORE extraction", TestClientSha256MismatchDiscardsBeforeExtract);
        await Test("Client download accepts a correct SHA-256", TestClientSha256MatchAccepted);

        // PHASE 2 — headless transactional delta engine (the in-launcher replacement for update.exe).
        Test("DeltaUpdateEngine derives per-tag manifest + removal-manifest URLs", TestDeltaUrlDerivation);
        await Test("DeltaUpdateEngine.SelectDeltaChain walks manifest back-links (single + multi-hop)", TestSelectDeltaChain);
        await Test("DeltaUpdateEngine.SelectDeltaChain returns no chain on a gap (missing intermediate) → full", TestDeltaChainGap);
        Test("DeltaUpdateEngine applies a single delta (adds/mods/removals; version marker last)", TestDeltaSingleHopApply);
        Test("DeltaUpdateEngine applies a 2-hop chain A→B→C (both hops' changes; marker at C)", TestDeltaTwoHopChain);
        Test("DeltaUpdateEngine mid-chain failure rolls back to a consistent B, then full completes to C", TestDeltaMidChainFailureThenFull);
        Test("DeltaUpdateEngine removal failure fails the transaction and rolls back byte-identical", TestDeltaRemovalFailureRollsBack);
        Test("DeltaUpdateEngine mid-apply failure rolls back to the pre-update state byte-identical", TestDeltaMidApplyFailureRollsBack);
        Test("DeltaUpdateEngine rejects an adversarial (traversal) delta with NO mutation", TestDeltaRejectsAdversarial);
        Test("DeltaUpdateEngine never overwrites or removes user-owned files (config/NLog/saves)", TestDeltaPreservesUserData);
        await Test("DeltaUpdateEngine SHA-256 mismatch on a hop is refused before mutation → full fallback", TestDeltaSha256MismatchFallsBack);
        await Test("Full-package upgrade A→B via fixture manifest preserves user data (Phase-3 upgrade test)", TestFullPackageUpgradePreservesUserData);

        if (Environment.GetEnvironmentVariable("OPENSO_LIVE_INSTALL_REPRO") == "1")
            await LiveInstallRepro();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---- tests ----

    private static async Task TestZip()
    {
        var tmp = NewTmp();
        var srcDir = Path.Combine(tmp, "src");
        Directory.CreateDirectory(Path.Combine(srcDir, "sub"));
        File.WriteAllText(Path.Combine(srcDir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(srcDir, "sub", "nested.txt"), "nested");
        var zip = Path.Combine(tmp, "a.zip");
        ZipFile.CreateFromDirectory(srcDir, zip);

        var outDir = Path.Combine(tmp, "out");
        await ZipExtractor.ExtractAsync(zip, outDir);

        Assert(File.ReadAllText(Path.Combine(outDir, "root.txt")) == "root", "root.txt content");
        Assert(File.ReadAllText(Path.Combine(outDir, "sub", "nested.txt")) == "nested", "nested.txt content");
    }

    private static async Task TestCab()
    {
        // Provide a real MSZIP cab at OPENSO_TEST_CAB to run this for real; otherwise it's skipped.
        var cab = Environment.GetEnvironmentVariable("OPENSO_TEST_CAB");
        if (string.IsNullOrEmpty(cab) || !File.Exists(cab))
        {
            Console.WriteLine("    (skipped — set OPENSO_TEST_CAB to a sample .cab to run)");
            return;
        }
        var outDir = Path.Combine(NewTmp(), "cabout");
        int files = 0;
        var prog = new Progress<ProgressReport>(_ => files++);
        await CabExtractor.ExtractAsync(cab, outDir, prog);
        Assert(Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Length > 0, "cab produced files");
    }

    private static async Task TestMd5Mismatch()
    {
        // Serve a tiny local file over a file:// URL isn't supported by HttpClient, so use a data
        // round-trip: write a file, compute its real md5, then assert a WRONG expected md5 fails.
        // We exercise the verification logic directly via a local HTTP server.
        var (url, stop) = await LocalServer.ServeBytes(System.Text.Encoding.ASCII.GetBytes("openso payload"));
        try
        {
            var dest = Path.Combine(NewTmp(), "dl.bin");
            var dl = new DownloadService(url, dest, "deadbeefdeadbeefdeadbeefdeadbeef");
            bool threw = false;
            try { await dl.RunAsync(); } catch { threw = true; }
            Assert(threw, "wrong md5 causes failure");
            Assert(!File.Exists(dest), "corrupt download is deleted");
        }
        finally { stop(); }
    }

    private static async Task TestMd5Match()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("openso payload");
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(payload)).ToLowerInvariant();
        var (url, stop) = await LocalServer.ServeBytes(payload);
        try
        {
            var dest = Path.Combine(NewTmp(), "dl.bin");
            var dl = new DownloadService(url, dest, md5);
            await dl.RunAsync();
            Assert(File.Exists(dest), "correct md5 download succeeds");
        }
        finally { stop(); }
    }

    private static async Task TestInstallState()
    {
        var svc = new InstallStateService();
        var list = await svc.GetInstalledAsync();
        Assert(list.Count > 0, "returns component statuses");
        // Should never throw and every entry has a code.
        Assert(list.All(s => !string.IsNullOrEmpty(s.Code)), "all entries have codes");
    }

    private static void TestDependencyGraph()
    {
        var os = OperatingSystem.IsWindows() ? OSPlatformKind.Windows
               : OperatingSystem.IsMacOS() ? OSPlatformKind.MacOS : OSPlatformKind.Linux;
        var deps = Components.DependenciesFor(os)["FSO"];
        // The self-contained native client only needs the TSO game files. The legacy FreeSO-on-Mono deps
        // (Mono/SDL/OpenAL/MacExtras) are obsolete and were intentionally dropped from FSO on every platform
        // (see Components.DependenciesFor) — this test used to assert the old graph and was stale.
        Assert(deps.Contains("TSO"), "FSO depends on TSO");
        Assert(!deps.Contains("OpenAL") && !deps.Contains("Mono") && !deps.Contains("SDL"),
            "FSO no longer carries the obsolete Mono/SDL/OpenAL deps");
    }

    private static void TestConfig()
    {
        var c = new LauncherConfig();
        Assert(c.ApiBaseUrl.Contains("openso.org"), "API points at openso.org");
        // TSO assets come from the canonical FreeSO Internet Archive item (EA's old host is dead).
        Assert(c.ResourceCentral["TheSimsOnline"].Contains("archive.org"), "TSO assets point at the Internet Archive");
        Assert(!c.ResourceCentral["TheSimsOnline"].Contains("largedownloads.ea.com"), "TSO assets do NOT point at the dead EA host");
    }

    private static void TestLaunchMissing()
    {
        var launcher = new GameLauncher();
        bool threw = false;
        try { launcher.Launch(Path.Combine(NewTmp(), "does-not-exist")); }
        catch (DirectoryNotFoundException) { threw = true; }
        catch (FileNotFoundException) { threw = true; }
        Assert(threw, "launching a missing install throws a clear error");
    }

    private static void TestRegistryWriter()
    {
        var rw = new RegistryWriter();
        // On non-Windows this must be a no-op returning false; on Windows it may fail w/o elevation.
        // Either way it must not throw.
        var result = rw.Write("FSO", NewTmp());
        if (!OperatingSystem.IsWindows())
            Assert(result == false, "registry write is a no-op off Windows");
    }

    // FsoInstaller hardening: the atomic-update guards that keep a bad update from gutting the install
    // (the incident where the runtime files got deleted, leaving an unlaunchable "install .NET" client).

    private static void TestVerifyRejectsTruncated()
    {
        var dir = Path.Combine(NewTmp(), "client");
        Directory.CreateDirectory(dir);
        // A gutted install: apphost + managed dll present, but the bundled runtime host is gone and the
        // file count is far below a real self-contained client — exactly the corruption we must reject.
        File.WriteAllText(Path.Combine(dir, "OpenSO.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "OpenSO.dll"), "x");
        bool threw = false;
        try { FsoInstaller.VerifyClientInstall(dir); } catch (IOException) { threw = true; }
        Assert(threw, "VerifyClientInstall must reject a truncated client (missing runtime / too few files)");
    }

    private static void TestVerifyAcceptsComplete()
    {
        var dir = Path.Combine(NewTmp(), "client");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "OpenSO.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "OpenSO.dll"), "x");
        File.WriteAllText(Path.Combine(dir, "hostfxr.dll"), "x");   // runtime host present
        for (int i = 0; i < 90; i++) File.WriteAllText(Path.Combine(dir, $"pad{i}.dll"), "x"); // over the min count
        FsoInstaller.VerifyClientInstall(dir); // must NOT throw
    }

    private static void TestVerifyAcceptsMacBundle()
    {
        // The macOS client is a CODE-ONLY OpenSO.app (apphost + runtime + DLLs inside
        // Contents/MacOS); the install root only has Content/ + version.txt. This is the layout that
        // broke Mac installs when verification demanded root-level code files.
        var dir = Path.Combine(NewTmp(), "client");
        var macos = Path.Combine(dir, "OpenSO.app", "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        File.WriteAllText(Path.Combine(macos, "OpenSO"), "x");
        File.WriteAllText(Path.Combine(macos, "OpenSO.dll"), "x");
        File.WriteAllText(Path.Combine(macos, "libhostfxr.dylib"), "x");
        var content = Path.Combine(dir, "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(dir, "version.txt"), "v0.1.23");
        for (int i = 0; i < 90; i++) File.WriteAllText(Path.Combine(content, $"pad{i}.dat"), "x");
        FsoInstaller.VerifyClientInstall(dir); // must NOT throw
    }

    private static void TestSwapIntoPlace()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        var staging = Path.Combine(tmp, ".install.staging");
        var backup = Path.Combine(tmp, ".install.backup");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "old.txt"), "old");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "new.txt"), "new");

        FsoInstaller.SwapIntoPlace(staging, install, backup);

        Assert(File.Exists(Path.Combine(install, "new.txt")), "live install now holds the staged (new) files");
        Assert(!File.Exists(Path.Combine(install, "old.txt")), "old files were swapped out");
        Assert(Directory.Exists(backup) && File.Exists(Path.Combine(backup, "old.txt")), "previous install preserved in backup");
        Assert(!Directory.Exists(staging), "staging dir moved into place");
    }

    private static void TestCarryOverUserData()
    {
        var tmp = NewTmp();
        var old = Path.Combine(tmp, "backup");
        var neu = Path.Combine(tmp, "install");

        // Old install: a mac flat-layout leftover + user data + patcher state + stray patch file.
        Directory.CreateDirectory(Path.Combine(old, "Content", "MeshReplace"));
        Directory.CreateDirectory(Path.Combine(old, "Content", "Patch", "translations"));
        Directory.CreateDirectory(Path.Combine(old, "PatchFiles"));
        Directory.CreateDirectory(Path.Combine(old, "OpenSO.app", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(old, "Content", "config.ini"), "user config");
        File.WriteAllText(Path.Combine(old, "Content", "MeshReplace", "sofa.fsom"), "mesh");
        File.WriteAllText(Path.Combine(old, "Content", "Patch", "stray.ffar"), "stray");
        File.WriteAllText(Path.Combine(old, "Content", "Patch", "translations", "keep.po"), "tr");
        File.WriteAllText(Path.Combine(old, "PatchFiles", "path0.zip"), "stale");
        File.WriteAllText(Path.Combine(old, "OpenSO.dll"), "legacy flat runtime");
        File.WriteAllText(Path.Combine(old, "OpenSO.app", "Contents", "MacOS", "OpenSO.dll"), "old code");
        File.WriteAllText(Path.Combine(old, "version.txt"), "v0.1.22");

        // New install: fresh code-only bundle + default config + new version.
        Directory.CreateDirectory(Path.Combine(neu, "Content"));
        Directory.CreateDirectory(Path.Combine(neu, "OpenSO.app", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(neu, "Content", "config.ini"), "default config");
        File.WriteAllText(Path.Combine(neu, "OpenSO.app", "Contents", "MacOS", "OpenSO.dll"), "new code");
        File.WriteAllText(Path.Combine(neu, "version.txt"), "v0.1.23");

        FsoInstaller.CarryOverUserData(old, neu);

        Assert(File.ReadAllText(Path.Combine(neu, "Content", "config.ini")) == "user config",
            "user's Content/config.ini wins over the shipped default (patcher IgnoreFiles)");
        Assert(File.Exists(Path.Combine(neu, "Content", "MeshReplace", "sofa.fsom")),
            "remesh pack survives the update");
        Assert(File.Exists(Path.Combine(neu, "Content", "Patch", "translations", "keep.po")),
            "Content/Patch subfolders are kept");
        Assert(!File.Exists(Path.Combine(neu, "Content", "Patch", "stray.ffar")),
            "stray top-level Content/Patch files are dropped (clean-patch behavior)");
        Assert(!File.Exists(Path.Combine(neu, "PatchFiles", "path0.zip")),
            "stale patcher state is not carried over");
        Assert(!File.Exists(Path.Combine(neu, "OpenSO.dll")),
            "legacy flat-layout code is not resurrected next to the code-only bundle");
        Assert(File.ReadAllText(Path.Combine(neu, "OpenSO.app", "Contents", "MacOS", "OpenSO.dll")) == "new code",
            "the new bundle stays pristine");
        Assert(File.ReadAllText(Path.Combine(neu, "version.txt")) == "v0.1.23",
            "files the new release ships always win");
    }

    private static List<ApiUpdate> FakeUpdateFeed() => new()
    {
        new ApiUpdate { update_id = 35, version_name = "v0.1.23", last_update_id = 34,
            full_zip = "u23-full", incremental_zip = "u23-inc", manifest_url = "u23-man" },
        new ApiUpdate { update_id = 34, version_name = "v0.1.22", last_update_id = 33,
            full_zip = "u22-full", incremental_zip = "u22-inc", manifest_url = "u22-man" },
        new ApiUpdate { update_id = 33, version_name = "v0.1.21", last_update_id = null,
            full_zip = "u21-full", incremental_zip = null, manifest_url = null },
    };

    private static void TestFindPathIncrementalChain()
    {
        // Installed v0.1.21, server wants v0.1.23: apply the v0.1.22 then v0.1.23 deltas, no full zip.
        var path = UpdatePath.FindPath(FakeUpdateFeed(), "v0.1.21", "v0.1.23");
        Assert(path != null && !path.FullZipStart, "chain found without a full-zip start");
        Assert(path!.Path.Count == 2 && path.Path[0].version_name == "v0.1.22" && path.Path[1].version_name == "v0.1.23",
            "deltas are ordered oldest -> newest");
    }

    private static void TestFindPathFullZipFallback()
    {
        // Installed version isn't on the feed: start from the newest full zip on the route.
        var path = UpdatePath.FindPath(FakeUpdateFeed(), "v0.0.9", "v0.1.23");
        Assert(path != null && path.FullZipStart, "unknown installed version starts from a full zip");
        Assert(path!.Path.Count == 1 && path.Path[0].version_name == "v0.1.23", "full zip of the target only");
        Assert(UpdatePath.FindPath(FakeUpdateFeed(), "v0.1.22", "v9.9.9") == null, "unknown target -> no path");
    }

    private static void TestPickFullClientAsset()
    {
        // A real v0.1.23 release lists these three "client win-x64" assets. The picker must choose the FULL
        // zip — NOT the incremental delta (a partial fileset -> broken/looping install) or the manifest.
        var assets = new (string?, string?)[]
        {
            ("OpenSO-client-win-x64.incremental.zip", "u-incremental"),
            ("OpenSO-client-win-x64.manifest.json",   "u-manifest"),
            ("OpenSO-client-win-x64.zip",             "u-full"),
            ("OpenSO-client-linux-x64.zip",           "u-linux"),
            ("OpenSO-client-osx-arm64.zip",           "u-osx"),
        };
        Assert(FsoInstaller.PickFullClientAsset(assets, "win-x64") == "u-full",
            "must pick the full win-x64 client zip, not the incremental/manifest");
        // Order independence: delta listed after the full zip must still not win.
        var reordered = new (string?, string?)[]
        {
            ("OpenSO-client-win-x64.zip",             "u-full"),
            ("OpenSO-client-win-x64.incremental.zip", "u-incremental"),
        };
        Assert(FsoInstaller.PickFullClientAsset(reordered, "win-x64") == "u-full", "full zip wins regardless of order");
    }

    // Diagnostic: run the REAL client update the launcher performs (resolve latest -> download ->
    // stage -> verify -> swap) against a throwaway install dir seeded with an old version.txt, and print
    // exactly what happens. Gated on OPENSO_LIVE_INSTALL_REPRO=1 (does a full ~340MB download).
    private static async Task LiveInstallRepro()
    {
        Console.WriteLine("\n--- LIVE INSTALL REPRO ---");
        var root = Path.Combine(NewTmp(), "OpenSO");
        var fso = Path.Combine(root, "FSO");
        Directory.CreateDirectory(fso);
        File.WriteAllText(Path.Combine(fso, "version.txt"), "v0.1.22\n"); // simulate the current install
        File.WriteAllText(Path.Combine(fso, "stale.txt"), "old");

        var cfg = new LauncherConfig();
        Console.WriteLine($"ClientManifestUrl = {cfg.ClientManifestUrl}");
        Console.WriteLine($"ClientReleaseFeed = {cfg.ClientReleaseFeed}");
        var installer = new FsoInstaller(cfg);
        var prog = new Progress<ProgressReport>(r => { /* quiet */ });
        try
        {
            await installer.InstallAsync(fso, prog);
            var vt = Path.Combine(fso, "version.txt");
            Console.WriteLine("RESULT: install returned OK");
            Console.WriteLine("  version.txt now = " + (File.Exists(vt) ? File.ReadAllText(vt).Trim() : "<MISSING>"));
            Console.WriteLine("  file count now  = " + Directory.EnumerateFiles(fso, "*", SearchOption.AllDirectories).Count());
            Console.WriteLine("  stale.txt gone? = " + (!File.Exists(Path.Combine(fso, "stale.txt"))));
        }
        catch (Exception ex)
        {
            Console.WriteLine("RESULT: install THREW -> " + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException != null) Console.WriteLine("  inner: " + ex.InnerException.Message);
        }
    }

    // ---- FIX A: exact-RID asset selection ----

    private static void TestExactRidClientAsset()
    {
        // A real release lists the full zip + delta + manifest for each shipped platform. Each RID must
        // select ONLY its own full zip; url values are set to the RID string so we can assert the match.
        var assets = new (string?, string?)[]
        {
            ("OpenSO-client-win-x64.zip",              "win-x64"),
            ("OpenSO-client-linux-x64.zip",            "linux-x64"),
            ("OpenSO-client-osx-x64.zip",              "osx-x64"),
            ("OpenSO-client-osx-arm64.zip",            "osx-arm64"),
            ("OpenSO-client-win-x64.incremental.zip",  "delta"),      // must never win
            ("OpenSO-client-win-x64.manifest.json",    "manifest"),   // must never win
        };
        foreach (var rid in new[] { "win-x64", "linux-x64", "osx-x64", "osx-arm64" })
            Assert(FsoInstaller.PickFullClientAsset(assets, rid) == rid,
                $"{rid} selects exactly its own full client zip");
        // A RID absent from the release (linux-arm64) must NOT borrow the Windows / macOS / x64 payloads.
        Assert(FsoInstaller.PickFullClientAsset(assets, "linux-arm64") == null,
            "unsupported RID linux-arm64 selects nothing — no cross-platform fallback");
    }

    private static void TestExactRidLauncherAsset()
    {
        var assets = new (string?, string?)[]
        {
            ("OpenSO-Launcher-win-x64.zip",   "win-x64"),
            ("OpenSO-Launcher-linux-x64.zip", "linux-x64"),
            ("OpenSO-Launcher-osx-x64.zip",   "osx-x64"),
            ("OpenSO-Launcher-osx-arm64.zip", "osx-arm64"),
        };
        foreach (var rid in new[] { "win-x64", "linux-x64", "osx-x64", "osx-arm64" })
            Assert(SelfUpdateService.PickLauncherAsset(assets, rid) == rid,
                $"{rid} self-update selects exactly its own launcher zip");
        Assert(SelfUpdateService.PickLauncherAsset(assets, "linux-arm64") == null,
            "unsupported RID linux-arm64 self-update selects nothing — no cross-platform fallback");
    }

    private static async Task TestClientResolveUnsupportedPlatformError()
    {
        // Manifest unreachable → GitHub fallback; the release ships a build for NO real RID → clear error.
        var closedUrl = await ClosedUrlAsync();
        var releaseJson = "{\"tag_name\":\"v9.9.9\",\"assets\":[" +
            "{\"name\":\"OpenSO-client-sunos-sparc.zip\",\"browser_download_url\":\"http://example.invalid/x\"}]}";
        var (feedUrl, stop) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(releaseJson));
        try
        {
            var cfg = new LauncherConfig { ClientManifestUrl = closedUrl, ClientReleaseFeed = feedUrl };
            var installer = new FsoInstaller(cfg);
            bool threw = false; string msg = "";
            try { await installer.ResolveClientZipUrlAsync(default); }
            catch (PlatformNotSupportedException ex) { threw = true; msg = ex.Message; }
            Assert(threw, "an unsupported platform throws PlatformNotSupportedException (never a silent wrong-RID download)");
            Assert(msg.Contains(FsoInstaller.CurrentRid()), "the error names this platform's RID");
        }
        finally { stop(); }
    }

    private static async Task TestClientResolveExactRid()
    {
        var rid = FsoInstaller.CurrentRid();
        var closedUrl = await ClosedUrlAsync();
        var releaseJson = "{\"tag_name\":\"v9.9.9\",\"assets\":[" +
            $"{{\"name\":\"OpenSO-client-{rid}.incremental.zip\",\"browser_download_url\":\"http://example.invalid/inc\"}}," +
            $"{{\"name\":\"OpenSO-client-{rid}.manifest.json\",\"browser_download_url\":\"http://example.invalid/man\"}}," +
            $"{{\"name\":\"OpenSO-client-{rid}.zip\",\"browser_download_url\":\"http://example.invalid/full\"}}]}}";
        var (feedUrl, stop) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(releaseJson));
        try
        {
            var cfg = new LauncherConfig { ClientManifestUrl = closedUrl, ClientReleaseFeed = feedUrl };
            var installer = new FsoInstaller(cfg);
            var url = await installer.ResolveClientZipUrlAsync(default);
            Assert(url == "http://example.invalid/full", "resolves the exact-RID FULL client zip (not the delta/manifest)");
        }
        finally { stop(); }
    }

    private static async Task TestLauncherApplyUnsupportedPlatformError()
    {
        // The feed advertises a newer launcher but ships no build matching THIS machine's RID.
        var feedJson = "{\"tag_name\":\"v99.99.99\",\"assets\":[" +
            "{\"name\":\"OpenSO-Launcher-nonexistent-rid.zip\",\"browser_download_url\":\"http://example.invalid/z\"}]}";
        var (feedUrl, stop) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(feedJson));
        try
        {
            var cfg = new LauncherConfig { LauncherUpdateFeed = feedUrl };
            var svc = new SelfUpdateService(cfg);
            bool threw = false; string msg = "";
            try { await svc.ApplyLauncherUpdateAsync(new Progress<ProgressReport>()); }
            catch (PlatformNotSupportedException ex) { threw = true; msg = ex.Message; }
            Assert(threw, "self-update refuses to swap in a different platform's build");
            Assert(msg.Contains(SelfUpdateService.CurrentRid()), "the error names this platform's RID");
        }
        finally { stop(); }
    }

    // ---- FIX B: ZIP extraction hardening (adversarial fixtures) ----

    private static async Task TestZipRejectsTraversal()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        var zip = MakeZipRaw(tmp, "trav.zip", ("../evil.txt", "pwn", null));
        await AssertExtractRejected(zip, dest, "a ../ traversal entry");
        Assert(!File.Exists(Path.Combine(tmp, "evil.txt")), "no file written outside dest via ../");
    }

    private static async Task TestZipRejectsAbsolute()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        // An absolute (rooted) entry path. Point it inside tmp so a guard failure is harmless-but-detectable.
        var abs = Path.Combine(tmp, "abs-evil.txt");
        Assert(Path.IsPathRooted(abs), "fixture entry is genuinely rooted");
        var zip = MakeZipRaw(tmp, "abs.zip", (abs, "pwn", null));
        await AssertExtractRejected(zip, dest, "an absolute/rooted entry path");
        Assert(!File.Exists(abs), "no file written at the absolute entry path");
    }

    private static async Task TestZipRejectsSiblingPrefix()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        // The exact bypass a string-prefix check allowed: a sibling whose name starts with the dest's.
        var zip = MakeZipRaw(tmp, "sib.zip", ("../install-evil/pwned.txt", "pwn", null));
        await AssertExtractRejected(zip, dest, "a sibling-prefix escape (install-evil)");
        Assert(!Directory.Exists(Path.Combine(tmp, "install-evil")), "no sibling directory created");
        Assert(!File.Exists(Path.Combine(tmp, "install-evil", "pwned.txt")), "no sibling escape file");
    }

    private static async Task TestZipRejectsDirTraversal()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        var zip = MakeZipRaw(tmp, "dirtrav.zip", ("../evil-dir/", "", null)); // directory entry with ../
        await AssertExtractRejected(zip, dest, "a directory-entry traversal");
        Assert(!Directory.Exists(Path.Combine(tmp, "evil-dir")), "no directory created outside dest");
    }

    private static async Task TestZipRejectsBackslash()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        var zip = MakeZipRaw(tmp, "bs.zip", ("..\\evil.txt", "pwn", null));
        await AssertExtractRejected(zip, dest, "a backslash path trick");
        Assert(!File.Exists(Path.Combine(tmp, "evil.txt")), "no escape via ..\\ on any OS");
    }

    private static async Task TestZipRejectsSymlink()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        // Unix mode S_IFLNK | 0777 in the high 16 bits of ExternalAttributes marks a symlink entry.
        var zip = MakeZipRaw(tmp, "link.zip", ("thelink", "/etc/passwd", unchecked((int)0xA1FF0000u)));
        await AssertExtractRejected(zip, dest, "a symlink entry");
        Assert(!File.Exists(Path.Combine(dest, "thelink")), "symlink entry is never materialized");
    }

    private static async Task TestZipNoPartialExtraction()
    {
        var tmp = NewTmp();
        var dest = Path.Combine(tmp, "install");
        // A valid entry FIRST, a malicious entry LAST: the whole archive must be rejected up front so the
        // earlier (valid) entry is NOT written — no partial extraction of an untrusted archive.
        var zip = MakeZipRaw(tmp, "partial.zip",
            ("good.txt", "ok", null),
            ("../evil.txt", "pwn", null));
        await AssertExtractRejected(zip, dest, "an archive whose later entry is malicious");
        Assert(!File.Exists(Path.Combine(dest, "good.txt")), "the earlier valid entry was NOT written");
        Assert(!File.Exists(Path.Combine(tmp, "evil.txt")), "no escape file written");
        Assert(!Directory.Exists(dest), "destination is not even created for a rejected archive");
    }

    // ---- FIX B helpers ----

    /// <summary>Builds a zip with the given raw entry names (adversarial names preserved verbatim).</summary>
    private static string MakeZipRaw(string dir, string zipName, params (string name, string content, int? extAttr)[] entries)
    {
        var path = Path.Combine(dir, zipName);
        using var fs = File.Create(path);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content, extAttr) in entries)
        {
            var e = za.CreateEntry(name);
            if (extAttr.HasValue) e.ExternalAttributes = extAttr.Value;
            using var s = e.Open();
            if (!string.IsNullOrEmpty(content))
            {
                var b = System.Text.Encoding.ASCII.GetBytes(content);
                s.Write(b, 0, b.Length);
            }
        }
        return path;
    }

    private static async Task AssertExtractRejected(string zip, string dest, string label)
    {
        bool threw = false;
        try { await ZipExtractor.ExtractAsync(zip, dest); }
        catch (IOException) { threw = true; }
        Assert(threw, $"extraction rejects {label}");
    }

    /// <summary>A localhost URL that is guaranteed to refuse connections (server started then stopped).</summary>
    private static async Task<string> ClosedUrlAsync()
    {
        var (url, stop) = await LocalServer.ServeBytes(Array.Empty<byte>());
        stop();
        return url;
    }

    // ---- FIX C: canonical per-RID client manifest (openso-manifest.json) ----

    /// <summary>A schemaVersion-1 manifest carrying all four supported RIDs (win-x64 also has a delta).
    /// Each RID's full url + sha256 is distinct so a test can prove exact-RID selection.</summary>
    private static string FourRidManifest() =>
        "{\"schemaVersion\":1,\"version\":\"v0.2.1\",\"clients\":{" +
        "\"win-x64\":{\"full\":{\"url\":\"u-win\",\"sha256\":\"" + new string('a', 64) + "\"}," +
            "\"deltas\":[{\"from\":\"v0.2.0\",\"url\":\"u-win-delta\",\"sha256\":\"" + new string('9', 64) + "\"}]}," +
        "\"linux-x64\":{\"full\":{\"url\":\"u-linux\",\"sha256\":\"" + new string('b', 64) + "\"}}," +
        "\"osx-x64\":{\"full\":{\"url\":\"u-osx\",\"sha256\":\"" + new string('c', 64) + "\"}}," +
        "\"osx-arm64\":{\"full\":{\"url\":\"u-arm\",\"sha256\":\"" + new string('d', 64) + "\"}}}}";

    private static void TestManifestResolvesAllRids()
    {
        var json = FourRidManifest();
        var expect = new (string rid, string url, char shaChar)[]
        {
            ("win-x64", "u-win", 'a'), ("linux-x64", "u-linux", 'b'),
            ("osx-x64", "u-osx", 'c'), ("osx-arm64", "u-arm", 'd'),
        };
        foreach (var (rid, url, shaChar) in expect)
        {
            var pkg = FsoInstaller.SelectFromManifest(json, rid);
            Assert(pkg.Url == url, $"{rid} selects ONLY its own full url");
            Assert(pkg.Sha256 == new string(shaChar, 64), $"{rid} carries its own sha256 for verification");
        }
    }

    private static void TestManifestMissingRid()
    {
        // A RID the release didn't build (linux-arm64) is a clear error — never another platform's payload.
        bool threw = false; string msg = "";
        try { FsoInstaller.SelectFromManifest(FourRidManifest(), "linux-arm64"); }
        catch (PlatformNotSupportedException ex) { threw = true; msg = ex.Message; }
        Assert(threw, "a missing RID throws PlatformNotSupportedException (no cross-platform fallback)");
        Assert(msg.Contains("linux-arm64"), "the error names the unsupported RID");
    }

    private static void TestManifestBadSchemaOrShape()
    {
        // Present-but-unknown schema fails hard (and yields NO package — can't downgrade to a wrong source).
        var badSchema = FourRidManifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":2");
        bool threw = false;
        try { FsoInstaller.SelectFromManifest(badSchema, "win-x64"); }
        catch (InvalidOperationException) { threw = true; }
        Assert(threw, "an unknown schemaVersion fails hard");

        // Malformed JSON also fails hard rather than returning something.
        bool threw2 = false;
        try { FsoInstaller.SelectFromManifest("{ not valid json ", "win-x64"); }
        catch (InvalidOperationException) { threw2 = true; }
        Assert(threw2, "a malformed manifest body fails hard");

        // A RID present but missing its full package (url/sha256) fails hard too.
        var noFull = "{\"schemaVersion\":1,\"clients\":{\"win-x64\":{\"deltas\":[]}}}";
        bool threw3 = false;
        try { FsoInstaller.SelectFromManifest(noFull, "win-x64"); }
        catch (InvalidOperationException) { threw3 = true; }
        Assert(threw3, "a RID missing its full package fails hard");
    }

    private static async Task TestManifestIsFirstSource()
    {
        var rid = FsoInstaller.CurrentRid();
        var sha = new string('e', 64);
        var manifest = "{\"schemaVersion\":1,\"version\":\"v9.9.9\",\"clients\":{\"" + rid +
            "\":{\"full\":{\"url\":\"http://example.invalid/manifest-full\",\"sha256\":\"" + sha + "\"}}}}";
        var (manUrl, stopMan) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(manifest));
        // A GitHub feed is ALSO reachable and would resolve a DIFFERENT url — the manifest must win.
        var releaseJson = "{\"tag_name\":\"v9.9.9\",\"assets\":[" +
            $"{{\"name\":\"OpenSO-client-{rid}.zip\",\"browser_download_url\":\"http://example.invalid/github-full\"}}]}}";
        var (feedUrl, stopFeed) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(releaseJson));
        try
        {
            var cfg = new LauncherConfig { ClientManifestUrl = manUrl, ClientReleaseFeed = feedUrl };
            var installer = new FsoInstaller(cfg);
            var pkg = await installer.ResolveClientPackageAsync(default);
            Assert(pkg != null, "resolves a package");
            Assert(pkg!.Value.Url == "http://example.invalid/manifest-full",
                "the per-RID manifest is the FIRST source (wins over the GitHub feed)");
            Assert(pkg.Value.Sha256 == sha, "the manifest's sha256 is carried for pre-extraction verification");
        }
        finally { stopMan(); stopFeed(); }
    }

    private static async Task TestManifestUnavailableFallsBackToGitHub()
    {
        var rid = FsoInstaller.CurrentRid();
        var closedManifest = await ClosedUrlAsync(); // manifest unreachable (network failure)
        var releaseJson = "{\"tag_name\":\"v9.9.9\",\"assets\":[" +
            $"{{\"name\":\"OpenSO-client-{rid}.zip\",\"browser_download_url\":\"http://example.invalid/github-full\"}}]}}";
        var (feedUrl, stop) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(releaseJson));
        try
        {
            var cfg = new LauncherConfig { ClientManifestUrl = closedManifest, ClientReleaseFeed = feedUrl };
            var installer = new FsoInstaller(cfg);
            var pkg = await installer.ResolveClientPackageAsync(default);
            Assert(pkg != null && pkg.Value.Url == "http://example.invalid/github-full",
                "an unavailable manifest falls back to the GitHub exact-RID asset");
            Assert(pkg!.Value.Sha256 == null, "the GitHub fallback carries no manifest hash");
        }
        finally { stop(); }
    }

    private static async Task TestClientSha256MismatchDiscardsBeforeExtract()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("openso client payload");
        var (url, stop) = await LocalServer.ServeBytes(payload);
        try
        {
            var dest = Path.Combine(NewTmp(), "client.zip");
            // Wrong sha256 → the download fails AND deletes the file, so extraction can never see it.
            var bad = new DownloadService(url, dest, expectedSha256: new string('0', 64));
            bool threw = false;
            try { await bad.RunAsync(); } catch (ChecksumMismatchException) { threw = true; }
            Assert(threw, "a wrong SHA-256 fails the download (before any extraction)");
            Assert(!File.Exists(dest), "the mismatched package is discarded");
        }
        finally { stop(); }
    }

    private static async Task TestClientSha256MatchAccepted()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes("openso client payload");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var (url, stop) = await LocalServer.ServeBytes(payload);
        try
        {
            var dest = Path.Combine(NewTmp(), "client.zip");
            await new DownloadService(url, dest, expectedSha256: sha).RunAsync();
            Assert(File.Exists(dest), "a correct SHA-256 download succeeds");
        }
        finally { stop(); }
    }

    // ---- PHASE 2: headless transactional delta engine ----

    private static void TestDeltaUrlDerivation()
    {
        var latest = "https://github.com/voicemxil/OpenSO/releases/latest/download/openso-manifest.json";
        Assert(DeltaUpdateEngine.PerTagManifestUrl(latest, "v0.2.0") ==
               "https://github.com/voicemxil/OpenSO/releases/download/v0.2.0/openso-manifest.json",
            "per-tag URL is derived from the /releases/latest/download/ asset URL");

        var perTag = "https://github.com/voicemxil/OpenSO/releases/download/v0.2.0/openso-manifest.json";
        Assert(DeltaUpdateEngine.PerTagManifestUrl(perTag, "v0.1.0") ==
               "https://github.com/voicemxil/OpenSO/releases/download/v0.1.0/openso-manifest.json",
            "an already-per-tag URL swaps the tag segment");

        Assert(DeltaUpdateEngine.PerTagManifestUrl("https://mirror.example.org/manifest.json", "v0.2.0") == null,
            "a non-GitHub mirror URL we can't rewrite yields null (multi-hop unavailable → full)");

        Assert(DeltaUpdateEngine.RemovalManifestUrlFor(
                   "https://x/OpenSO-client-win-x64.incremental.zip") ==
               "https://x/OpenSO-client-win-x64.manifest.json",
            "the removal manifest url is the incremental zip url with .incremental.zip → .manifest.json");
    }

    /// <summary>Builds a schemaVersion-1 per-release manifest with a single win-x64 delta back-link.</summary>
    private static string DeltaManifest(string version, string? from, string? url, string? sha = null) =>
        "{\"schemaVersion\":1,\"version\":\"" + version + "\",\"clients\":{\"win-x64\":{" +
        "\"full\":{\"url\":\"full-" + version + "\",\"sha256\":\"" + new string('f', 64) + "\"}" +
        (from == null ? "" :
            ",\"deltas\":[{\"from\":\"" + from + "\",\"url\":\"" + url + "\",\"sha256\":\"" + (sha ?? new string('a', 64)) + "\"}]") +
        "}}}";

    private static async Task TestSelectDeltaChain()
    {
        // Release graph: v0.1.0 → v0.2.0 → v0.3.0, each manifest carrying the delta FROM the previous release.
        var manifests = new Dictionary<string, string>
        {
            ["v0.3.0"] = DeltaManifest("v0.3.0", "v0.2.0", "url-BC", new string('c', 64)),
            ["v0.2.0"] = DeltaManifest("v0.2.0", "v0.1.0", "url-AB", new string('b', 64)),
            ["v0.1.0"] = DeltaManifest("v0.1.0", "v0.0.0", "url-00"),
        };
        Task<string?> Fetch(string tag) => Task.FromResult(manifests.TryGetValue(tag, out var m) ? m : null);

        // Single hop: installed is the target's immediate predecessor.
        var one = await DeltaUpdateEngine.SelectDeltaChainAsync("v0.2.0", "v0.3.0", "win-x64", Fetch);
        Assert(one != null && one.Count == 1, "single-hop chain has one hop");
        Assert(one![0].Url == "url-BC" && one[0].To == "v0.3.0" && one[0].From == "v0.2.0", "the one hop is v0.2.0 → v0.3.0");

        // Multi-hop: two releases behind → ordered oldest → newest.
        var two = await DeltaUpdateEngine.SelectDeltaChainAsync("v0.1.0", "v0.3.0", "win-x64", Fetch);
        Assert(two != null && two.Count == 2, "two-hop chain has two hops");
        Assert(two![0].Url == "url-AB" && two[0].To == "v0.2.0", "hop 0 is the OLDEST (v0.1.0 → v0.2.0)");
        Assert(two[1].Url == "url-BC" && two[1].To == "v0.3.0", "hop 1 is the NEWEST (v0.2.0 → v0.3.0)");

        // Already at target → no chain.
        Assert(await DeltaUpdateEngine.SelectDeltaChainAsync("v0.3.0", "v0.3.0", "win-x64", Fetch) == null,
            "installed == target yields no chain");

        // A RID with no deltas in the manifest → no chain (→ full).
        Assert(await DeltaUpdateEngine.SelectDeltaChainAsync("v0.2.0", "v0.3.0", "linux-x64", Fetch) == null,
            "a RID absent from the manifest's deltas has no delta chain");
    }

    private static async Task TestDeltaChainGap()
    {
        // v0.3.0's manifest links back to v0.2.0, but v0.2.0's manifest is MISSING — the walk can't reach the
        // installed v0.1.0, so it gives up (→ full fallback) rather than applying a partial chain.
        var manifests = new Dictionary<string, string>
        {
            ["v0.3.0"] = DeltaManifest("v0.3.0", "v0.2.0", "url-BC"),
            // no "v0.2.0" entry — the intermediate delta is unreachable
        };
        Task<string?> Fetch(string tag) => Task.FromResult(manifests.TryGetValue(tag, out var m) ? m : null);

        var chain = await DeltaUpdateEngine.SelectDeltaChainAsync("v0.1.0", "v0.3.0", "win-x64", Fetch);
        Assert(chain == null, "a missing intermediate manifest breaks the walk → no chain (caller uses full)");
    }

    private static void TestDeltaSingleHopApply()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "Content"));
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "goneA.dat"), "x");
        File.WriteAllText(Path.Combine(install, "Content", "config.ini"), "user config"); // user-owned
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // Delta A → B: add addB.dat + a nested new file, modify shared.dat, TRY to overwrite the user's
        // config.ini (must be skipped), advance version.txt. Removal manifest drops goneA.dat.
        var zip = MakeZipRaw(tmp, "hopAB.zip",
            ("addB.dat", "B", null),
            ("Content/sub/deep.dat", "D", null),
            ("shared.dat", "B", null),
            ("Content/config.ini", "SHOULD-NOT-WIN", null),
            ("version.txt", "vB", null));
        var man = MakeRemovalManifest(tmp, "hopAB.json", "vB", "goneA.dat");

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyDelta(install, zip, man, "vB");

        Assert(r.Success, "the delta applies successfully");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "B", "modification applied");
        Assert(File.ReadAllText(Path.Combine(install, "addB.dat")) == "B", "addition applied");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "sub", "deep.dat")) == "D", "nested addition applied");
        Assert(!File.Exists(Path.Combine(install, "goneA.dat")), "removal applied");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config",
            "the user's config.ini is NEVER overwritten by a delta");
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vB", "version marker advanced to the target");
        Assert(NoBackupLeftover(tmp, "install"), "the backup store is cleaned up on success");
    }

    private static void TestDeltaTwoHopChain()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "Content", "saves"));
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "goneA.dat"), "x");
        File.WriteAllText(Path.Combine(install, "Content", "config.ini"), "user config");
        File.WriteAllText(Path.Combine(install, "Content", "saves", "s.dat"), "save");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // Hop 1 (A→B): add addB.dat, modify shared.dat, remove goneA.dat.
        var hop1 = MakeZipRaw(tmp, "hop1.zip", ("addB.dat", "B", null), ("shared.dat", "B", null), ("version.txt", "vB", null));
        var man1 = MakeRemovalManifest(tmp, "hop1.json", "vB", "goneA.dat");
        // Hop 2 (B→C): add addC.dat, modify shared.dat again, remove addB.dat (added by hop 1).
        var hop2 = MakeZipRaw(tmp, "hop2.zip", ("addC.dat", "C", null), ("shared.dat", "C", null), ("version.txt", "vC", null));
        var man2 = MakeRemovalManifest(tmp, "hop2.json", "vC", "addB.dat");

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyChain(install, new[]
        {
            new DeltaUpdateEngine.StagedHop("vB", hop1, man1),
            new DeltaUpdateEngine.StagedHop("vC", hop2, man2),
        });

        Assert(r.Success, "the whole 2-hop chain applies");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "C", "the latest hop's modification wins");
        Assert(File.ReadAllText(Path.Combine(install, "addC.dat")) == "C", "hop 2's addition present");
        Assert(!File.Exists(Path.Combine(install, "addB.dat")), "hop 2's removal of hop 1's addition applied");
        Assert(!File.Exists(Path.Combine(install, "goneA.dat")), "hop 1's removal applied");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config", "user config preserved across the chain");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "saves", "s.dat")) == "save", "user save preserved across the chain");
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vC", "version marker ends at C");
        Assert(NoBackupLeftover(tmp, "install"), "no backup store left after a successful chain");
    }

    private static void TestDeltaMidChainFailureThenFull()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "Content", "saves"));
        Directory.CreateDirectory(Path.Combine(install, "busy")); // a DIRECTORY that hop 2 will collide with
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "goneA.dat"), "x");
        File.WriteAllText(Path.Combine(install, "Content", "config.ini"), "user config");
        File.WriteAllText(Path.Combine(install, "Content", "saves", "s.dat"), "save");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        var hop1 = MakeZipRaw(tmp, "mc1.zip", ("addB.dat", "B", null), ("shared.dat", "B", null), ("version.txt", "vB", null));
        var man1 = MakeRemovalManifest(tmp, "mc1.json", "vB", "goneA.dat");
        // Hop 2 applies aaa.txt, then tries to write a FILE at "busy" which is an existing directory → fails.
        var hop2 = MakeZipRaw(tmp, "mc2.zip", ("aaa.txt", "ok", null), ("busy", "x", null), ("version.txt", "vC", null));

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyChain(install, new[]
        {
            new DeltaUpdateEngine.StagedHop("vB", hop1, man1),
            new DeltaUpdateEngine.StagedHop("vC", hop2, null),
        });

        Assert(!r.Success, "the chain fails when a hop can't be applied");
        // Consistent at B: hop 1 committed, hop 2 fully rolled back.
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vB", "the marker sits at the last COMPLETED hop (B), not C");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "B", "hop 1's change is intact");
        Assert(File.Exists(Path.Combine(install, "addB.dat")), "hop 1's addition is intact");
        Assert(!File.Exists(Path.Combine(install, "goneA.dat")), "hop 1's removal is intact");
        Assert(!File.Exists(Path.Combine(install, "aaa.txt")), "hop 2's partial addition was rolled back");
        Assert(Directory.Exists(Path.Combine(install, "busy")), "the pre-existing directory is untouched");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config", "user config intact");
        Assert(NoBackupLeftover(tmp, "install"), "no backup store left after a rolled-back hop");

        // Fallback to the target's FULL package completes the update to C — using the exact mechanism the
        // launcher's full path runs (atomic swap + user-data carry-over).
        var fullC = Path.Combine(tmp, "fullC");
        Directory.CreateDirectory(Path.Combine(fullC, "Content"));
        File.WriteAllText(Path.Combine(fullC, "shared.dat"), "C");
        File.WriteAllText(Path.Combine(fullC, "newC.dat"), "C");
        File.WriteAllText(Path.Combine(fullC, "Content", "config.ini"), "default config");
        File.WriteAllText(Path.Combine(fullC, "version.txt"), "vC");
        var backup = Path.Combine(tmp, ".install.fullbackup");
        FsoInstaller.SwapIntoPlace(fullC, install, backup);
        FsoInstaller.CarryOverUserData(backup, install);

        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vC", "the full fallback completes the update to C");
        Assert(File.ReadAllText(Path.Combine(install, "newC.dat")) == "C", "the full package's files are in place");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config", "user config still preserved through the full fallback");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "saves", "s.dat")) == "save", "user save still preserved through the full fallback");
    }

    private static void TestDeltaRemovalFailureRollsBack()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "Removed"));
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "Content-config"), "x"); // (dummy padding)
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");
        var victim = Path.Combine(install, "Removed", "old.dat");
        File.WriteAllText(victim, "D");
        MakeUndeletable(victim); // force the removal's File.Delete to throw (cross-platform)

        var zip = MakeZipRaw(tmp, "rf.zip", ("shared.dat", "B", null), ("version.txt", "vB", null));
        var man = MakeRemovalManifest(tmp, "rf.json", "vB", "Removed/old.dat");

        try
        {
            var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyDelta(install, zip, man, "vB");
            Assert(!r.Success, "a failed removal fails the whole transaction");
            Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "A", "the earlier modification was rolled back");
            Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vA", "the version marker is unchanged");
            Assert(File.Exists(victim) && File.ReadAllText(victim) == "D", "the file that couldn't be removed is intact");
            Assert(NoBackupLeftover(tmp, "install"), "the backup store is cleaned up after rollback");
        }
        finally { MakeDeletable(victim); }
    }

    private static void TestDeltaMidApplyFailureRollsBack()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "busy")); // existing dir the delta collides with
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // addX (pure add, applies) → shared.dat (modify, applies) → busy (write a FILE over a DIR, fails).
        var zip = MakeZipRaw(tmp, "ma.zip",
            ("addX.dat", "X", null),
            ("shared.dat", "B", null),
            ("busy", "x", null),
            ("version.txt", "vB", null));

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyDelta(install, zip, null, "vB");

        Assert(!r.Success, "a mid-apply I/O failure fails the transaction");
        Assert(!File.Exists(Path.Combine(install, "addX.dat")), "the pure-addition was deleted on rollback (byte-identical)");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "A", "the modification was restored on rollback");
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vA", "the version marker is unchanged");
        Assert(Directory.Exists(Path.Combine(install, "busy")), "the pre-existing directory is untouched");
        Assert(NoBackupLeftover(tmp, "install"), "the backup store is cleaned up after rollback");
    }

    private static void TestDeltaRejectsAdversarial()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // A traversal entry — the shared ArchivePathGuard must reject the whole archive BEFORE any mutation.
        var zip = MakeZipRaw(tmp, "evil.zip", ("good.dat", "ok", null), ("../evil.txt", "pwn", null));

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyDelta(install, zip, null, "vB");

        Assert(!r.Success, "an adversarial delta is refused");
        Assert(!File.Exists(Path.Combine(tmp, "evil.txt")), "nothing was written outside the install");
        Assert(!File.Exists(Path.Combine(install, "good.dat")), "not even the earlier valid entry was written (no partial apply)");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "A", "the install is untouched");
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vA", "the version marker is unchanged");
        Assert(NoBackupLeftover(tmp, "install"), "no backup store is even created for a rejected archive");
    }

    private static void TestDeltaPreservesUserData()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(Path.Combine(install, "Content", "saves"));
        File.WriteAllText(Path.Combine(install, "Content", "config.ini"), "user config");
        File.WriteAllText(Path.Combine(install, "NLog.config"), "user nlog");
        File.WriteAllText(Path.Combine(install, "Content", "saves", "s.dat"), "save");
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "goneG.dat"), "game"); // a GAME file the delta removes
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // The delta tries to overwrite BOTH user files and modify a game file; the removal manifest lists the
        // user files AND a game file. The engine must skip both user overwrites and both user removals, but
        // still apply the game modification and the game removal.
        var zip = MakeZipRaw(tmp, "ud.zip",
            ("Content/config.ini", "DEFAULT", null),
            ("NLog.config", "DEFAULT", null),
            ("shared.dat", "B", null),
            ("version.txt", "vB", null));
        var man = MakeRemovalManifest(tmp, "ud.json", "vB", "Content/config.ini", "NLog.config", "goneG.dat");

        var r = new DeltaUpdateEngine(new LauncherConfig()).ApplyDelta(install, zip, man, "vB");

        Assert(r.Success, "the delta applies");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config", "user config is neither overwritten nor removed");
        Assert(File.ReadAllText(Path.Combine(install, "NLog.config")) == "user nlog", "user NLog.config is neither overwritten nor removed");
        Assert(File.ReadAllText(Path.Combine(install, "Content", "saves", "s.dat")) == "save", "user save (not in the delta) is untouched");
        Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "B", "the game modification is applied");
        Assert(!File.Exists(Path.Combine(install, "goneG.dat")), "the game-file removal is applied");
        Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vB", "the version marker advanced");
    }

    private static async Task TestDeltaSha256MismatchFallsBack()
    {
        var tmp = NewTmp();
        var install = Path.Combine(tmp, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "shared.dat"), "A");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // The incremental zip served here is valid, but the manifest advertises a WRONG sha256 for it, so the
        // download's hash check discards it and the engine reports "no delta applied" (→ the caller uses full).
        var deltaZipBytes = File.ReadAllBytes(MakeZipRaw(tmp, "served.zip", ("shared.dat", "B", null), ("version.txt", "vB", null)));
        var (zipUrl, stopZip) = await LocalServer.ServeBytes(deltaZipBytes);
        var manifestJson = "{\"schemaVersion\":1,\"version\":\"vB\",\"clients\":{\"win-x64\":{\"deltas\":[" +
            "{\"from\":\"vA\",\"url\":\"" + zipUrl + "\",\"sha256\":\"" + new string('0', 64) + "\"}]}}}";
        var (manUrl, stopMan) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(manifestJson));
        try
        {
            // ClientManifestUrl must carry the /releases/latest/download/ shape so per-tag derivation resolves
            // back to the (path-agnostic) local manifest server.
            var cfg = new LauncherConfig { ClientManifestUrl = manUrl.Replace("file.bin", "releases/latest/download/openso-manifest.json") };
            var engine = new DeltaUpdateEngine(cfg);
            var applied = await engine.TryDeltaUpdateAsync(install, "vA", "vB", "win-x64", new Progress<ProgressReport>());

            Assert(!applied, "a hash mismatch on the delta is refused and reported as not-applied (→ full fallback)");
            Assert(File.ReadAllText(Path.Combine(install, "shared.dat")) == "A", "the install was never mutated");
            Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vA", "the version marker is unchanged");
        }
        finally { stopZip(); stopMan(); }
    }

    private static async Task TestFullPackageUpgradePreservesUserData()
    {
        var rid = FsoInstaller.CurrentRid();
        var tmp = NewTmp();

        // Old install at version A carrying user data (config + a save the new release doesn't ship).
        var install = Path.Combine(tmp, "OpenSO", "FSO");
        Directory.CreateDirectory(Path.Combine(install, "Content", "saves"));
        File.WriteAllText(Path.Combine(install, "Content", "config.ini"), "user config");
        File.WriteAllText(Path.Combine(install, "Content", "saves", "s.dat"), "user save");
        File.WriteAllText(Path.Combine(install, "version.txt"), "vA");

        // Build a fixture FULL client zip for version B (meets VerifyClientInstall's structural minimum).
        var stage = Path.Combine(tmp, "clientB");
        Directory.CreateDirectory(Path.Combine(stage, "Content"));
        File.WriteAllText(Path.Combine(stage, "OpenSO.exe"), "x");
        File.WriteAllText(Path.Combine(stage, "OpenSO.dll"), "x");
        File.WriteAllText(Path.Combine(stage, "hostfxr.dll"), "x");
        File.WriteAllText(Path.Combine(stage, "Content", "config.ini"), "default config"); // shipped default
        File.WriteAllText(Path.Combine(stage, "newB.dat"), "B");                            // a new game file
        File.WriteAllText(Path.Combine(stage, "version.txt"), "vB");
        for (int i = 0; i < 90; i++) File.WriteAllText(Path.Combine(stage, $"pad{i}.dll"), "x");
        var zipPath = Path.Combine(tmp, "clientB.zip");
        ZipFile.CreateFromDirectory(stage, zipPath);
        var zipBytes = File.ReadAllBytes(zipPath);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();

        var (zipUrl, stopZip) = await LocalServer.ServeBytes(zipBytes);
        var manifest = "{\"schemaVersion\":1,\"version\":\"vB\",\"clients\":{\"" + rid +
            "\":{\"full\":{\"url\":\"" + zipUrl + "\",\"sha256\":\"" + sha + "\"}}}}";
        var (manUrl, stopMan) = await LocalServer.ServeBytes(System.Text.Encoding.UTF8.GetBytes(manifest));
        try
        {
            var cfg = new LauncherConfig { ClientManifestUrl = manUrl };
            await new FsoInstaller(cfg).InstallAsync(install, new Progress<ProgressReport>());

            Assert(File.ReadAllText(Path.Combine(install, "version.txt")) == "vB", "the full package upgraded the install to B");
            Assert(File.ReadAllText(Path.Combine(install, "newB.dat")) == "B", "the new release's game files are installed");
            Assert(File.ReadAllText(Path.Combine(install, "Content", "config.ini")) == "user config", "the user's config.ini is preserved over the shipped default");
            Assert(File.ReadAllText(Path.Combine(install, "Content", "saves", "s.dat")) == "user save", "the user's save is preserved across the full upgrade");
        }
        finally { stopZip(); stopMan(); }
    }

    // ---- PHASE 2 helpers ----

    /// <summary>Writes a removal manifest in the FSOUpdateManifest shape ({ Version, Diffs:[{ DiffType, Path }] })
    /// the delta engine reads — every listed path is a Remove (DiffType 2).</summary>
    private static string MakeRemovalManifest(string dir, string name, string version, params string[] removedPaths)
    {
        var diffs = string.Join(",", removedPaths.Select(p => "{\"DiffType\":2,\"Path\":\"" + p.Replace("\\", "\\\\") + "\"}"));
        var json = "{\"Version\":\"" + version + "\",\"Diffs\":[" + diffs + "]}";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>True when no ".&lt;name&gt;.updatebackup-*" store is left beside the install dir.</summary>
    private static bool NoBackupLeftover(string parent, string name) =>
        !Directory.EnumerateDirectories(parent, $".{name}.updatebackup-*").Any();

    private static void MakeUndeletable(string filePath)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(Path.GetDirectoryName(filePath)!, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static void MakeDeletable(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                File.SetAttributes(filePath, FileAttributes.Normal);
            else
                File.SetUnixFileMode(Path.GetDirectoryName(filePath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort */ }
    }

    // ---- harness ----

    private static async Task Test(string name, Func<Task> body)
    {
        try { await body(); Console.WriteLine($"  PASS  {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
    }

    private static void Test(string name, Action body)
    {
        try { body(); Console.WriteLine($"  PASS  {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
    }

    private static void Assert(bool cond, string what)
    {
        if (!cond) throw new Exception("assertion failed: " + what);
    }

    private static string NewTmp()
    {
        var p = Path.Combine(Path.GetTempPath(), "openso-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(p);
        return p;
    }
}

/// <summary>Minimal in-process HTTP server that serves a fixed byte payload, for download tests.</summary>
internal static class LocalServer
{
    public static Task<(string Url, Action Stop)> ServeBytes(byte[] payload)
    {
        var listener = new System.Net.HttpListener();
        int port = 8080 + Random.Shared.Next(1000, 9000);
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var cts = new System.Threading.CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload);
                ctx.Response.OutputStream.Close();
            }
        });

        return Task.FromResult((prefix + "file.bin", (Action)(() => { cts.Cancel(); listener.Stop(); })));
    }
}
