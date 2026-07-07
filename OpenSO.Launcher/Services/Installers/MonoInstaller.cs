using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Port of lib/installers/mono.js — installs the Mono runtime (needed to run the client on
/// macOS/Linux). Platform-branched exactly like upstream:
///   - macOS: download the Mono .pkg and install it with `installer -pkg ... -target /` (elevated).
///   - Linux: install via the system package manager (apt `mono-complete`, or pacman `mono`), elevated.
///   - Windows: not required (no-op).
/// </summary>
public sealed class MonoInstaller : IComponentInstaller
{
    public string Code => "Mono";

    private readonly LauncherConfig _config;
    private readonly ElevationService _elevation;

    public MonoInstaller(LauncherConfig config, ElevationService? elevation = null)
    {
        _config = config;
        _elevation = elevation ?? new ElevationService();
    }

    public async Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            progress.Report(new ProgressReport("mono", 1.0, "Mono not required on Windows."));
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Download the .pkg, then install it system-wide with elevation.
            var work = TempFiles.NewDir("mono");
            var pkg = Path.Combine(work, "mono.pkg");
            var url = _config.ResourceCentral.TryGetValue("Mono", out var u) ? u
                : throw new InvalidOperationException("No Mono download URL configured.");

            progress.Report(new ProgressReport("mono", 0, "Downloading Mono runtime…"));
            await new DownloadService(url, pkg).RunAsync(Scale(progress, "mono", 0.0, 0.85), ct);

            progress.Report(new ProgressReport("mono", 0.9, "Installing Mono (you may be asked for your password)…"));
            var res = await _elevation.RunAsync($"installer -pkg {ElevationService.ShQuote(pkg)} -target /",
                "OpenSO needs to install the Mono runtime", ct);
            try { Directory.Delete(work, true); } catch { }

            if (!res.Success) throw new IOException("Mono install failed: " + res.StdErr);
            progress.Report(new ProgressReport("mono", 1.0, "Mono installed."));
            return;
        }

        // Linux: system package manager.
        progress.Report(new ProgressReport("mono", 0.2, "Installing Mono via your package manager (needs permission)…"));
        var cmd = IsArchLike()
            ? "pacman -Syu --noconfirm mono"
            : "apt-get update && apt-get install -y mono-complete";
        var r = await _elevation.RunAsync(cmd, "OpenSO needs to install the Mono runtime", ct);
        if (!r.Success) throw new IOException("Mono install failed: " + r.StdErr);
        progress.Report(new ProgressReport("mono", 1.0, "Mono installed."));
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
