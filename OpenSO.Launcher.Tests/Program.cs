using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services;
using OpenSO.Launcher.Services.Extraction;

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
        Assert(deps.Contains("TSO"), "FSO depends on TSO");
        if (os == OSPlatformKind.Windows)
            Assert(deps.Contains("OpenAL"), "Windows FSO depends on OpenAL");
        else
            Assert(deps.Contains("Mono") && deps.Contains("SDL"), "Unix FSO depends on Mono+SDL");
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
