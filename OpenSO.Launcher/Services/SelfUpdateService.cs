using System;
using System.Collections.Generic;
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
    // Static: per-instance HttpClients are never disposed and leak socket handles (see StatusService).
    private static readonly HttpClient Http = CreateClient();

    public SelfUpdateService(LauncherConfig config) => _config = config;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSO.Launcher");
        return c;
    }

    /// <summary>This launcher's version, from the assembly (e.g. "0.1.0").</summary>
    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task<string?> CheckForLauncherUpdateAsync(CancellationToken ct = default)
    {
        var (tag, _, _) = await ResolveLatestAsync(ct);
        return tag != null && IsNewer(tag, CurrentVersion()) ? tag : null;
    }

    public async Task ApplyLauncherUpdateAsync(IProgress<ProgressReport> progress, CancellationToken ct = default)
    {
        // AppImage install: the whole launcher IS the single file at $APPIMAGE. There is no dir to swap —
        // download the new .AppImage, verify it, atomically replace $APPIMAGE, and relaunch it. The
        // zip-swap path below is for the tarball install (and existing pre-AppImage installs on any OS).
        var appImagePath = AppImagePath();
        if (appImagePath != null)
        {
            await ApplyAppImageUpdateAsync(appImagePath, progress, ct);
            return;
        }

        var (tag, assetUrl, assetSha256) = await ResolveLatestAsync(ct);
        if (tag == null)
            throw new InvalidOperationException("No launcher update is available right now (the update feed is unreachable).");
        if (assetUrl == null)
            // A release exists but ships no build for THIS platform — never swap in a different RID's payload.
            throw new PlatformNotSupportedException(
                $"This launcher release does not include a build for your platform ({CurrentRid()}) yet.");
        RemoteUrl.RequireHttps(assetUrl, "the launcher update");

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var staging = TempFiles.NewDir("launcher-update");
        var tmpZip = Path.Combine(staging, "launcher.zip");
        var newDir = Path.Combine(staging, "new");

        progress.Report(new ProgressReport("Updating launcher", 0.0, "Downloading " + tag + "…"));
        // assetSha256 is GitHub's release-asset digest; verifying it stops a MITM'd or corrupted
        // download from ever being swapped in over the running launcher.
        await new DownloadService(assetUrl, tmpZip, expectedSha256: assetSha256).RunAsync(progress, ct);
        progress.Report(new ProgressReport("Updating launcher", 0.85, "Extracting…"));
        await ZipExtractor.ExtractAsync(tmpZip, newDir, new Progress<ProgressReport>(),
            preservePermissions: !OperatingSystem.IsWindows(), ct);

        progress.Report(new ProgressReport("Updating launcher", 0.97, "Restarting…"));
        SpawnSwapAndRelaunch(newDir, staging, appDir);
        // The caller must shut the launcher down now so the swap script can replace the no-longer-running files.
    }

    // ── AppImage self-update ─────────────────────────────────────────────────────────────────────
    // When the launcher runs as an AppImage, Environment.ProcessPath points into the transient
    // /tmp/.mount_* squashfs (read-only, gone at exit); the real, updatable file is the .AppImage the
    // AppImage runtime records in the APPIMAGE env var. Self-update here is a single-file replace, not a
    // dir swap: download the new .AppImage, verify it, atomically rename it over $APPIMAGE, relaunch.

    internal const string AppImageEnvVar = "APPIMAGE";

    /// <summary>The absolute path of the running .AppImage (from the <c>APPIMAGE</c> env var the AppImage
    /// runtime sets), or null when the launcher is not running as an AppImage. <paramref name="getEnv"/> is
    /// injectable so the mode decision is a pure, testable function.</summary>
    internal static string? AppImagePath(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var v = getEnv(AppImageEnvVar);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>The download target for a new AppImage: a hidden sibling in the SAME directory as
    /// <paramref name="appImagePath"/>, so replacing the original is an atomic same-filesystem rename.</summary>
    internal static string AppImageSiblingTemp(string appImagePath, int pid)
    {
        var full = Path.GetFullPath(appImagePath);
        var dir = Path.GetDirectoryName(full) ?? ".";
        return Path.Combine(dir, "." + Path.GetFileName(full) + ".new-" + pid);
    }

    /// <summary>Atomically replaces <paramref name="targetAppImage"/> with <paramref name="newFile"/> and
    /// makes it executable. The running AppImage keeps its now-unlinked inode mounted, so overwriting the
    /// very file it launched from is safe (the rename only swaps the directory entry). Pure enough to test
    /// with plain files on any OS (the chmod is a no-op off Unix).</summary>
    internal static void ReplaceAppImageFile(string newFile, string targetAppImage)
    {
        if (!OperatingSystem.IsWindows())
        {
            // rwxr-xr-x — an AppImage must be executable to launch.
            try { File.SetUnixFileMode(newFile, (UnixFileMode)0x1ED); } catch { /* best effort */ }
        }
        File.Move(newFile, targetAppImage, overwrite: true);
    }

    private async Task ApplyAppImageUpdateAsync(string appImagePath, IProgress<ProgressReport> progress, CancellationToken ct)
    {
        var (tag, assetUrl, assetSha256) = await ResolveLatestAsync(ct, wantAppImage: true);
        if (tag == null)
            throw new InvalidOperationException("No launcher update is available right now (the update feed is unreachable).");
        if (assetUrl == null)
            throw new PlatformNotSupportedException(
                $"This launcher release does not include an AppImage build for your platform ({CurrentRid()}) yet.");
        RemoteUrl.RequireHttps(assetUrl, "the launcher update");

        var tmp = AppImageSiblingTemp(appImagePath, Environment.ProcessId);
        progress.Report(new ProgressReport("Updating launcher", 0.0, "Downloading " + tag + "…"));
        // Verify the GitHub asset digest when the feed carries one; DownloadService deletes-and-throws on a
        // mismatch so a tampered AppImage never reaches disk-as-final. When the feed has no digest, the
        // download still can't be empty (DownloadService throws on a zero-byte response) — a size sanity below.
        try
        {
            await new DownloadService(assetUrl, tmp, expectedSha256: assetSha256).RunAsync(progress, ct);
            if (new FileInfo(tmp).Length <= 0)
                throw new IOException("The downloaded AppImage was empty.");

            progress.Report(new ProgressReport("Updating launcher", 0.97, "Restarting…"));
            ReplaceAppImageFile(tmp, appImagePath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        SpawnRelaunchAppImage(appImagePath, Environment.ProcessId);
        // The caller shuts the launcher down; the tiny relaunch script waits for it to exit, then starts the
        // (now replaced) $APPIMAGE. No file-in-use conflict — the new AppImage mounts its own fresh inode.
    }

    private static void SpawnRelaunchAppImage(string appImagePath, int pid)
    {
        var sh = Path.Combine(Path.GetTempPath(), "openso-launcher-appimage-relaunch-" + pid + ".sh");
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine($"while kill -0 {pid} 2>/dev/null; do sleep 1; done");
        sb.AppendLine($"\"{appImagePath}\" &");
        sb.AppendLine("rm -- \"$0\"");
        File.WriteAllText(sh, sb.ToString());
        Process.Start(new ProcessStartInfo("/bin/sh", $"\"{sh}\"") { UseShellExecute = false });
    }

    /// <summary>Fetch releases/latest; return (tagName-without-leading-v, assetUrlForThisRid, assetSha256) — any may be null.
    /// The sha256 comes from the GitHub asset's <c>digest</c> field ("sha256:&lt;hex&gt;") when present.</summary>
    private async Task<(string? tag, string? assetUrl, string? assetSha256)> ResolveLatestAsync(CancellationToken ct, bool wantAppImage = false)
    {
        try
        {
            var json = await Http.GetStringAsync(_config.LauncherUpdateFeed, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            string? assetUrl = null, assetSha256 = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var list = new List<(string? name, string? url, string? digest)>();
                foreach (var a in assets.EnumerateArray())
                    list.Add((a.TryGetProperty("name", out var n) ? n.GetString() : null,
                              a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                              a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
                (assetUrl, assetSha256) = PickLauncherAsset(list, CurrentRid(), wantAppImage ? AppImageSuffix : ZipSuffix);
            }
            return (tag?.TrimStart('v', 'V'), assetUrl, assetSha256);
        }
        catch { return (null, null, null); }
    }

    // The self-update asset extensions. The tar/zip install swaps in a ".zip"; an AppImage install
    // replaces itself with the single ".AppImage" file (see the AppImage self-update path above).
    private const string ZipSuffix = ".zip";
    internal const string AppImageSuffix = ".AppImage";

    /// <summary>
    /// Picks the launcher zip for this EXACT <paramref name="rid"/> from a release's assets. Exact-RID
    /// ONLY — there is deliberately NO generic/first-zip fallback: a running launcher must never overwrite
    /// itself with a different platform's build (the old "fall back to the first .zip asset" logic would
    /// have handed a linux-arm64 launcher the Windows or x64 zip). Returns null when this platform has no
    /// build, which the caller surfaces as a clear "not supported yet" error.
    /// </summary>
    internal static string? PickLauncherAsset(IEnumerable<(string? name, string? url)> assets, string rid)
        => PickLauncherAsset(System.Linq.Enumerable.Select(assets, a => (a.name, a.url, (string?)null)), rid).url;

    internal static (string? url, string? sha256) PickLauncherAsset(IEnumerable<(string? name, string? url, string? digest)> assets, string rid)
        => PickLauncherAsset(assets, rid, ZipSuffix);

    /// <summary>As the two-arg overload, but for an explicit asset extension — <c>".zip"</c> for the
    /// swap-and-relaunch install, <c>".AppImage"</c> for an AppImage install. Exact-RID only (no
    /// cross-platform fallback), same as the zip path.</summary>
    internal static (string? url, string? sha256) PickLauncherAsset(IEnumerable<(string? name, string? url, string? digest)> assets, string rid, string suffix)
    {
        foreach (var (name, url, digest) in assets)
        {
            if (name == null || url == null) continue;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains(rid, StringComparison.OrdinalIgnoreCase)) return (url, digest); // exact platform match only
        }
        return (null, null);
    }

    /// <summary>This machine's release RID — matches launcher asset suffixes (win-x64, osx-arm64, …).</summary>
    internal static string CurrentRid()
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
            var bat = Path.Combine(Path.GetTempPath(), "openso-launcher-swap-" + pid + ".bat");
            File.WriteAllText(bat, BuildWindowsSwapScript(pid));
            Process.Start(new ProcessStartInfo("cmd.exe", BuildWindowsSwapArgs(bat, sourceDir, appDir, stagingRoot))
            { UseShellExecute = false, CreateNoWindow = true });
        }
        else
        {
            var exe = Path.Combine(appDir, "OpenSO.Launcher");
            // (sh reads UTF-8 natively, so Unicode paths may be embedded directly here.)
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

    /// <summary>
    /// The Windows swap script. Its content must stay PURE ASCII: cmd.exe decodes batch files in the
    /// legacy OEM codepage (CP437/CP737/…), never UTF-8, so a literal Unicode profile path (a Greek
    /// C:\Users\Δημήτρης\…) turns to mojibake and the swap dies with "cannot find the path specified".
    /// All paths therefore arrive as ARGUMENTS — %1 the staged new files, %2 the install dir, %3 the
    /// staging root — because process command lines are UTF-16 the whole way (CreateProcessW → cmd →
    /// xcopy), immune to the codepage. (Args also dodge cmd expanding a literal % in a path.)
    /// </summary>
    internal static string BuildWindowsSwapScript(int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL && (timeout /t 1 /nobreak >NUL & goto wait)");
        sb.AppendLine("xcopy /E /Y /I \"%~1\\*\" \"%~2\\\" >NUL");
        sb.AppendLine("start \"\" \"%~2\\OpenSO.Launcher.exe\"");
        sb.AppendLine("rmdir /S /Q \"%~3\"");
        // A plain `del "%~f0"` line makes cmd re-read the (now deleted) file for the next
        // command and strand a window on "The batch file cannot be found." The (goto) idiom
        // parses the whole line first, ends batch processing, then deletes — no re-read.
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    /// <summary>cmd.exe arguments running the swap script with the three paths as batch arguments.
    /// The script runs DIRECTLY under /S /C — not via `start`: starting a .bat re-enters cmd as
    /// `cmd /K "bat" "args…"`, and /C(/K)'s quote rule mangles any command line holding more than one
    /// quoted token (it strips the FIRST and LAST quote, gluing the bat path to the arguments), so the
    /// script never ran. /S pins the rule deterministically: wrap the whole line in one extra quote
    /// pair, cmd strips exactly that pair, and every inner quoted token survives. Direct execution
    /// under the launcher's CreateNoWindow console also means NO visible cmd window, ever — the old
    /// `start "" /min` flashed one (and surfaced mojibake errors to users). Trailing separators are
    /// trimmed so no path ends in `\"` (an escaped quote to the child's parser, gluing arguments).</summary>
    internal static string BuildWindowsSwapArgs(string bat, string sourceDir, string appDir, string stagingRoot)
    {
        static string Arg(string p) => "\"" + p.TrimEnd('\\', '/') + "\"";
        return $"/S /C \" {Arg(bat)} {Arg(sourceDir)} {Arg(appDir)} {Arg(stagingRoot)} \"";
    }
}
