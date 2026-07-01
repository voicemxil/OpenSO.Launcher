using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Launches the installed OpenSO client. Builds the same argument set the upstream launcher used and
/// starts the native, self-contained apphost directly per-OS (Windows: OpenSO.exe; macOS/Linux: the
/// "OpenSO" apphost). The launcher process can then exit/minimize.
/// </summary>
public sealed class GameLauncher
{
    /// <summary>Subset of the upstream user settings that affect the launch command.</summary>
    public sealed class Options
    {
        /// <summary>"ogl" (OpenGL), "dx" (DirectX, Windows only), or "sw" (software).</summary>
        public string GraphicsMode { get; set; } = "ogl";
        public bool Enable3D { get; set; } = false;
        /// <summary>Refresh-rate hint passed as -hz. 0 = let the game decide.</summary>
        public int RefreshRate { get; set; } = 60;
        /// <summary>Game language code (0 = English), passed as -lang.</summary>
        public int LanguageCode { get; set; } = 0;
        public bool Windowed { get; set; } = true;
    }

    /// <summary>
    /// Starts the game from <paramref name="installDir"/> (the FSO install directory). Returns the
    /// started Process. Throws if the executable/launch script can't be found.
    /// </summary>
    public Process Launch(string installDir, Options? options = null)
    {
        options ??= new Options();
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            throw new DirectoryNotFoundException($"OpenSO install not found at: {installDir}");

        var args = BuildArgs(options);

        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var exe = Path.Combine(installDir, "OpenSO.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("OpenSO.exe not found.", exe);
            psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        else
        {
            // macOS/Linux: self-contained native build, no Mono / no .exe. On macOS the client ships as a
            // code-only OpenSO.app, so run the apphost inside it (which keeps the app's icon while it runs);
            // the game data lives in Content/ next to the .app. Fall back to a bare apphost (Linux, or an
            // older install). Working dir is the install root so Content/ resolves either way.
            var appExe = Path.Combine(installDir, "OpenSO.app", "Contents", "MacOS", "OpenSO");
            var exe = File.Exists(appExe) ? appExe : Path.Combine(installDir, "OpenSO");
            if (!File.Exists(exe))
                throw new FileNotFoundException("OpenSO executable not found in the install.", exe);
            TryMakeExecutable(exe);
            psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the game process.");
        return proc;
    }

    /// <summary>Builds the launch arguments, mirroring fsolauncher.js launchGame(). Also passed to the
    /// in-game patcher (update.exe), which hands them back to the game when it relaunches it.</summary>
    internal static List<string> BuildArgs(Options o)
    {
        var args = new List<string>();
        if (o.Windowed) args.Add("w");                  // windowed
        args.Add($"-lang{o.LanguageCode}");             // language

        // Software mode only supports OpenGL; macOS/Linux are always OpenGL.
        var gfx = o.GraphicsMode;
        if (gfx == "sw") gfx = "ogl";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) gfx = "ogl";
        args.Add($"-{gfx}");

        if (o.Enable3D && o.GraphicsMode != "sw") args.Add("-3d");
        if (o.RefreshRate > 0) args.Add($"-hz{o.RefreshRate}");
        return args;
    }

    /// <summary>Returns the process's exit code if it dies within <paramref name="within"/> (the launch
    /// failed), or null if it's still running by then (launched OK).</summary>
    public static async Task<int?> WaitForEarlyExitAsync(Process proc, TimeSpan within)
    {
        try
        {
            using var cts = new CancellationTokenSource(within);
            await proc.WaitForExitAsync(cts.Token);
            return SafeExitCode(proc);
        }
        catch (OperationCanceledException) { return null; } // still running -> launched OK
        catch { return null; }
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    /// <summary>macOS: native popup explaining Gatekeeper blocked the (non-notarized) app and how to allow
    /// it. Returns true if the user chose to clear the download-quarantine flag (the caller then retries).</summary>
    public async Task<bool> ShowMacBlockedHelpAsync(string installDir)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
        var script =
            "display dialog \"macOS blocked OpenSO from opening because it isn't notarized by Apple. " +
            "Click 'Allow & Retry' to clear the download flag and try again, or open Privacy & Security " +
            "and choose 'Open Anyway'.\" with title \"OpenSO\" with icon caution " +
            "buttons {\"Open Privacy Settings\", \"Allow & Retry\"} default button \"Allow & Retry\"";
        var choice = await RunCaptureAsync("osascript", new[] { "-e", script });
        if (choice.Contains("Allow & Retry"))
        {
            await RunAsync("xattr", new[] { "-dr", "com.apple.quarantine", installDir });
            return true;
        }
        if (choice.Contains("Open Privacy Settings"))
            await RunAsync("open", new[] { "x-apple.systempreferences:com.apple.preference.security?Privacy" });
        return false;
    }

    private static async Task<string> RunCaptureAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var outp = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return outp;
        }
        catch { return ""; }
    }

    private static async Task RunAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch { }
    }

    private static void TryMakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { /* best effort */ }
    }
}
