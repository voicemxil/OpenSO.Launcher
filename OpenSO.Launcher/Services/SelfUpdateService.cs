using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Extraction;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Self-update for the launcher itself: checks the voicemxil/OpenSO.Launcher releases for a newer version,
/// downloads this platform's asset, stages it, and spawns a small swap-and-relaunch script — a running exe
/// can't overwrite its own files in place, so the script waits for this process to exit, copies the new
/// files over, and relaunches (the same trick the in-game patcher uses).
/// </summary>
public sealed class SelfUpdateService : ISelfUpdateService
{
    private readonly LauncherConfig _config;
    private readonly HttpClient _http;

    public SelfUpdateService(LauncherConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
    }

    /// <summary>This launcher's version, from the assembly (e.g. "0.1.0").</summary>
    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task<string?> CheckForLauncherUpdateAsync(CancellationToken ct = default)
    {
        var (tag, _) = await ResolveLatestAsync(ct);
        return tag != null && IsNewer(tag, CurrentVersion()) ? tag : null;
    }

    public async Task ApplyLauncherUpdateAsync(IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        var (tag, assetUrl) = await ResolveLatestAsync(ct);
        if (tag == null || assetUrl == null)
            throw new InvalidOperationException("No launcher update asset is available for this platform.");

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var staging = Path.Combine(Path.GetTempPath(), "openso-launcher-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var tmpZip = Path.Combine(staging, "launcher.zip");
        var newDir = Path.Combine(staging, "new");

        progress.Report(new ProgressReport("Updating launcher", 0.0, "Downloading " + tag + "…"));
        await new DownloadService(assetUrl, tmpZip).RunAsync(progress, ct);
        progress.Report(new ProgressReport("Updating launcher", 0.85, "Extracting…"));
        await ZipExtractor.ExtractAsync(tmpZip, newDir, new Progress<ProgressReport>(),
            preservePermissions: !OperatingSystem.IsWindows(), ct);

        progress.Report(new ProgressReport("Updating launcher", 0.97, "Restarting…"));
        SpawnSwapAndRelaunch(newDir, staging, appDir);
        // The caller must shut the launcher down now so the swap script can replace the no-longer-running files.
    }

    /// <summary>Fetch releases/latest; return (tagName-without-leading-v, assetUrlForThisRid) — either may be null.</summary>
    private async Task<(string? tag, string? assetUrl)> ResolveLatestAsync(CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(_config.LauncherUpdateFeed, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var rid = CurrentRid();
                string? generic = null;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!a.TryGetProperty("browser_download_url", out var u)) continue;
                    var url = u.GetString();
                    if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)) { assetUrl = url; break; } // exact platform
                    generic ??= url; // fall back to the first .zip asset
                }
                assetUrl ??= generic;
            }
            return (tag?.TrimStart('v', 'V'), assetUrl);
        }
        catch { return (null, null); }
    }

    /// <summary>This machine's release RID — matches launcher asset suffixes (win-x64, osx-arm64, …).</summary>
    private static string CurrentRid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return "win-" + arch;
        if (OperatingSystem.IsMacOS()) return "osx-" + arch;
        return "linux-" + arch;
    }

    /// <summary>True if remote &gt; local (System.Version compare; falls back to "string differs").</summary>
    private static bool IsNewer(string remote, string local)
    {
        if (Version.TryParse(Pad(remote), out var rv) && Version.TryParse(Pad(local), out var lv))
            return rv > lv;
        return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);

        static string Pad(string s) => s.Split('.').Length >= 2 ? s : s + ".0";
    }

    private static void SpawnSwapAndRelaunch(string sourceDir, string stagingRoot, string appDir)
    {
        var pid = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            var exe = Path.Combine(appDir, "OpenSO.Launcher.exe");
            var bat = Path.Combine(Path.GetTempPath(), "openso-launcher-swap-" + pid + ".bat");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL && (timeout /t 1 /nobreak >NUL & goto wait)");
            sb.AppendLine($"xcopy /E /Y /I \"{sourceDir}\\*\" \"{appDir}\\\" >NUL");
            sb.AppendLine($"start \"\" \"{exe}\"");
            sb.AppendLine($"rmdir /S /Q \"{stagingRoot}\"");
            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(bat, sb.ToString());
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" /min \"{bat}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        else
        {
            var exe = Path.Combine(appDir, "OpenSO.Launcher");
            var sh = Path.Combine(Path.GetTempPath(), "openso-launcher-swap-" + pid + ".sh");
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine($"while kill -0 {pid} 2>/dev/null; do sleep 1; done");
            sb.AppendLine($"cp -R \"{sourceDir}/.\" \"{appDir}/\"");
            sb.AppendLine($"chmod +x \"{exe}\" 2>/dev/null");
            sb.AppendLine($"rm -rf \"{stagingRoot}\"");
            sb.AppendLine($"\"{exe}\" &");
            sb.AppendLine("rm -- \"$0\"");
            File.WriteAllText(sh, sb.ToString());
            Process.Start(new ProcessStartInfo("/bin/sh", $"\"{sh}\"") { UseShellExecute = false });
        }
    }
}
