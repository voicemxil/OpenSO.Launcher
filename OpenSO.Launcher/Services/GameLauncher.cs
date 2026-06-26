using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Launches the installed OpenSO/FreeSO client — port of fsolauncher.js launchGame() and
/// FSO.Patcher.StartFreeSO(). Builds the same argument set the upstream launcher used and starts
/// the process per-OS (Windows: OpenSO.exe directly; macOS/Linux: the freeso .command shell
/// scripts the client ships, which invoke mono). The launcher process can then exit/minimize.
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
            // macOS/Linux: the client ships a launch script that sets up mono + the right paths.
            var script = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? Path.Combine(installDir, "freeso.command")
                : Path.Combine(installDir, "freeso-linux.command");

            if (File.Exists(script))
            {
                TryMakeExecutable(script);
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    WorkingDirectory = installDir,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(script);
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
            else
            {
                // Fallback (matches StartFreeSO): run the .exe through mono directly.
                var mono = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "/Library/Frameworks/Mono.framework/Commands/mono" : "/usr/bin/mono";
                psi = new ProcessStartInfo
                {
                    FileName = File.Exists(mono) ? mono : "mono",
                    WorkingDirectory = installDir,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("OpenSO.exe");
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the game process.");
        return proc;
    }

    /// <summary>Builds the launch arguments, mirroring fsolauncher.js launchGame().</summary>
    private static List<string> BuildArgs(Options o)
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
