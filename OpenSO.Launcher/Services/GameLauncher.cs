using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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
            // macOS/Linux: the client is a self-contained NATIVE build (apphost named "OpenSO", no
            // extension) — run it directly. The old Mono path (freeso.command / `mono OpenSO.exe`) is
            // gone: there's no Mono and no .exe anymore. The bare apphost sits at the install root even
            // on macOS (where OpenSO.app also exists); running it works for both arches and finds the
            // sibling Content/ folder.
            var exe = Path.Combine(installDir, "OpenSO");
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
