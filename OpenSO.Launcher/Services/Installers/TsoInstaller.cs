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
        // Pre-flight: this install needs the download + unpacked cabs + the extracted game on disk.
        // Check up front (both the install volume and temp) so we fail with a clear message instead of
        // running out of space partway through the cab extraction.
        DiskSpace.EnsureFreeSpace(installPath, MinFreeBytes, "install The Sims Online");
        DiskSpace.EnsureFreeSpace(Path.GetTempPath(), MinFreeBytes, "install The Sims Online");

        var source = _config.ResourceCentral.TryGetValue("TheSimsOnline", out var url)
            ? url : _config.TsoAssetsBaseUrl;

        var work = TempFiles.NewDir("tso");
        var zipPath = Path.Combine(work, "tso.zip");
        var unzipDir = Path.Combine(work, "client");

        try
        {
            // Step 1: download (resilient). The archive source may not send Content-Length, so the
            // progress fraction can read 0 until done — that's expected (matches upstream's note).
            progress.Report(new ProgressReport("tso", 0, "Downloading The Sims Online (~1.27 GB, from the Internet Archive)…"));
            var dl = new DownloadService(source, zipPath, _config.TsoAssetsMd5);
            await dl.RunAsync(ProgressScaler.Scale(progress, "tso", 0.00, 0.60), ct);

            // Step 2 + 3: ensure install dir, unzip distribution to temp.
            Directory.CreateDirectory(installPath);
            await ZipExtractor.ExtractAsync(zipPath, unzipDir,
                ProgressScaler.Scale(progress, "tso", 0.60, 0.72, "Unpacking installer… "), false, ct);

            // Free the 1.27 GB download now that the cabs are unpacked — it's no longer needed, and
            // keeping it would inflate the peak disk usage during the (large) cab extraction below.
            try { File.Delete(zipPath); } catch { }

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

            // Data1.cab ALREADY contains a TSOClient/ folder, so extracting straight into installPath (the
            // "The Sims Online" dir, per Components.InstallDirName) yields <install>/TSOClient/tuning.dat —
            // exactly what the game's locator wants: the relative "../The Sims Online/TSOClient/" check (run
            // from the client's working dir, its sibling) and the Maxis registry InstallDir + "\TSOClient\".
            // (Extracting into a TSOClient subfolder here would double it to TSOClient/TSOClient/.)
            await CabExtractor.ExtractAsync(firstCab, installPath,
                ProgressScaler.Scale(progress, "tso", 0.72, 0.97, "Extracting game files… "), purge: true, ct);

            // Step 5: register the Maxis/TSO install (InstallDir = the "The Sims Online" parent of TSOClient).
            progress.Report(new ProgressReport("tso", 0.98, "Registering install…"));
            _registerInstall?.Invoke(Code, installPath);

            progress.Report(new ProgressReport("tso", 1.0, "The Sims Online installed."));
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    // The install peaks at the download (~1.3 GB) coexisting with the unpacked cabs (~1.3 GB), then the
    // extracted game (~2 GB) after both are freed. Require a safe margin so we never stall mid-extraction.
    private const long MinFreeBytes = 4L * 1024 * 1024 * 1024;
}
