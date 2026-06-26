using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Extraction;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Port of lib/installers/tso.js — installs the original The Sims Online game assets.
/// These are EA's files, supplied by the user's own machine downloading them (the legal fair-use
/// path); OpenSO never redistributes them. Steps mirror upstream:
///   1. Download the TSO distribution archive (zip) from the configured source
///   2. Create the install directory
///   3. Unzip it to a temp folder
///   4. Find Data1.cab (in TSO_Installer_v1.1239.1.0/ or at the root) and CAB-extract it
///   5. Register the Maxis install entry
/// </summary>
public sealed class TsoInstaller : IComponentInstaller
{
    public string Code => "TSO";

    // Upstream constants.js temp.TSO firstCab/rootCab.
    private const string FirstCabPath = "TSO_Installer_v1.1239.1.0/Data1.cab";
    private const string RootCab = "Data1.cab";

    private readonly LauncherConfig _config;
    private readonly Action<string, string>? _registerInstall;

    public TsoInstaller(LauncherConfig config, Action<string, string>? registerInstall = null)
    {
        _config = config;
        _registerInstall = registerInstall;
    }

    public async Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        var source = _config.ResourceCentral.TryGetValue("TheSimsOnline", out var url)
            ? url : _config.TsoAssetsBaseUrl;

        var work = Path.Combine(Path.GetTempPath(), $"openso-tso-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "tso.zip");
        var unzipDir = Path.Combine(work, "client");

        try
        {
            // Step 1: download (resilient). The archive source may not send Content-Length, so the
            // progress fraction can read 0 until done — that's expected (matches upstream's note).
            progress.Report(new ProgressReport("tso", 0, "Downloading The Sims Online (~1.27 GB, from the Internet Archive)…"));
            var dl = new DownloadService(source, zipPath, _config.TsoAssetsMd5);
            await dl.RunAsync(Scale(progress, "tso", 0.00, 0.60), ct);

            // Step 2 + 3: ensure install dir, unzip distribution to temp.
            Directory.CreateDirectory(installPath);
            await ZipExtractor.ExtractAsync(zipPath, unzipDir,
                Scale(progress, "tso", 0.60, 0.72, "Unpacking installer… "), false, ct);

            // Step 4: locate Data1.cab. The canonical FreeSO TSO.zip puts Data1.cab…Data1111.cab at
            // the root; some other distributions nest them under TSO_Installer_v.../. Check root
            // first, then the nested path, then fall back to a recursive search.
            var firstCab = Path.Combine(unzipDir, RootCab);
            if (!File.Exists(firstCab))
                firstCab = Path.Combine(unzipDir, FirstCabPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(firstCab))
                firstCab = Directory.EnumerateFiles(unzipDir, "Data1.cab", SearchOption.AllDirectories).FirstOrDefault() ?? firstCab;
            if (!File.Exists(firstCab))
                throw new FileNotFoundException("Could not find Data1.cab in the TSO distribution.");

            await CabExtractor.ExtractAsync(firstCab, installPath,
                Scale(progress, "tso", 0.72, 0.97, "Extracting game files… "), purge: true, ct);

            // Step 5: register the Maxis/TSO install.
            progress.Report(new ProgressReport("tso", 0.98, "Registering install…"));
            _registerInstall?.Invoke(Code, installPath);

            progress.Report(new ProgressReport("tso", 1.0, "The Sims Online installed."));
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    private static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage,
        double lo, double hi, string? prefix = null) =>
        new Progress<ProgressReport>(r =>
            outer.Report(new ProgressReport(stage, lo + (hi - lo) * r.Fraction,
                prefix != null ? prefix + (r.Detail ?? "") : r.Detail)));
}
