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
        Test("GameUpdateService.BuildDownloads mirrors the in-client PatchFiles layout", TestBuildDownloads);

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

    private static void TestBuildDownloads()
    {
        // Delta chain: each step is its incremental zip + manifest, named path{i}.zip/.json — the
        // exact layout FSO.Patcher's Program.UpdatePath() picks up from PatchFiles/.
        var chain = UpdatePath.FindPath(FakeUpdateFeed(), "v0.1.21", "v0.1.23")!;
        var files = GameUpdateService.BuildDownloads(chain)!;
        Assert(files.Count == 4, "two deltas + two manifests");
        Assert(files[0] == ("u22-inc", "path0.zip") && files[1] == ("u22-man", "path0.json"), "step 0 = v0.1.22 delta");
        Assert(files[2] == ("u23-inc", "path1.zip") && files[3] == ("u23-man", "path1.json"), "step 1 = v0.1.23 delta");

        // Full-zip start: step 0 downloads the full zip instead.
        var full = UpdatePath.FindPath(FakeUpdateFeed(), "v0.0.9", "v0.1.23")!;
        var fullFiles = GameUpdateService.BuildDownloads(full)!;
        Assert(fullFiles[0] == ("u23-full", "path0.zip"), "full-zip start downloads the full zip first");
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
