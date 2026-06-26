using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Port of lib/installers/sdl.js — installs SDL2 (needed by the client on macOS/Linux).
/// Platform-branched like upstream:
///   - macOS: download the SDL2 .dmg, mount it (hdiutil), copy SDL2.framework into
///            /Library/Frameworks, then unmount — all elevated.
///   - Linux: install via the system package manager (apt `libsdl2-2.0-0`, pacman `sdl2`), elevated.
///   - Windows: not required (no-op).
/// </summary>
public sealed class SdlInstaller : IComponentInstaller
{
    public string Code => "SDL";

    private readonly LauncherConfig _config;
    private readonly ElevationService _elevation;

    public SdlInstaller(LauncherConfig config, ElevationService? elevation = null)
    {
        _config = config;
        _elevation = elevation ?? new ElevationService();
    }

    public async Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            progress.Report(new ProgressReport("sdl", 1.0, "SDL2 not required on Windows."));
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var dmg = Path.Combine(Path.GetTempPath(), $"openso-sdl-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.dmg");
            var url = _config.ResourceCentral.TryGetValue("SDL", out var u) ? u
                : throw new InvalidOperationException("No SDL download URL configured.");

            progress.Report(new ProgressReport("sdl", 0, "Downloading SDL2…"));
            await new DownloadService(url, dmg).RunAsync(Scale(progress, "sdl", 0.0, 0.85), ct);

            progress.Report(new ProgressReport("sdl", 0.9, "Installing SDL2 (you may be asked for your password)…"));
            var escaped = dmg.Replace(" ", "\\ ");
            // Mount → remove any existing framework → copy in → unmount (matches sdl.js).
            var cmd =
                $"hdiutil attach {escaped} && " +
                "rm -rf /Library/Frameworks/SDL2.framework && " +
                "cp -R /Volumes/SDL2/SDL2.framework /Library/Frameworks && " +
                "hdiutil unmount /Volumes/SDL2";
            var res = await _elevation.RunAsync(cmd, "OpenSO needs to install SDL2", ct);
            try { File.Delete(dmg); } catch { }

            if (!res.Success) throw new IOException("SDL2 install failed: " + res.StdErr);
            progress.Report(new ProgressReport("sdl", 1.0, "SDL2 installed."));
            return;
        }

        // Linux: system package manager.
        progress.Report(new ProgressReport("sdl", 0.2, "Installing SDL2 via your package manager (needs permission)…"));
        var pkgCmd = IsArchLike()
            ? "pacman -Syu --noconfirm sdl2"
            : "apt-get update && apt-get install -y libsdl2-2.0-0";
        var r = await _elevation.RunAsync(pkgCmd, "OpenSO needs to install SDL2", ct);
        if (!r.Success) throw new IOException("SDL2 install failed: " + r.StdErr);
        progress.Report(new ProgressReport("sdl", 1.0, "SDL2 installed."));
    }

    private static bool IsArchLike()
    {
        try
        {
            if (!File.Exists("/etc/os-release")) return false;
            var txt = File.ReadAllText("/etc/os-release").ToLowerInvariant();
            return txt.Contains("id=arch") || txt.Contains("id_like=arch");
        }
        catch { return false; }
    }

    private static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage, double lo, double hi) =>
        new Progress<ProgressReport>(r => outer.Report(new ProgressReport(stage, lo + (hi - lo) * r.Fraction, r.Detail)));
}
