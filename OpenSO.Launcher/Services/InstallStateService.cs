using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OpenSO.Launcher.Services;

public record InstallStatus(string Code, bool IsInstalled, string? Path);

public sealed class InstallStateService
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static string AppData
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsMac) return Path.Combine(home, "Library", "Application Support", "OpenSO Launcher");
            if (!IsWindows) return Path.Combine(home, ".openso");
            return ".";
        }
    }

    private static readonly Dictionary<string, (string Key, string Value)> WinRegistry = new()
    {
        ["FSO"]      = (@"SOFTWARE\Rhys Simpson\FreeSO", "InstallDir"),
        ["TSO"]      = (@"SOFTWARE\Maxis\The Sims Online", "InstallDir"),
        ["Simitone"] = (@"SOFTWARE\Rhys Simpson\Simitone", "InstallDir"),
        ["OpenAL"]   = (@"SOFTWARE\OpenAL", ""),
    };

    private readonly string? _installRoot;

    public InstallStateService(Models.LauncherConfig? config = null)
        => _installRoot = config?.ResolvedInstallRoot();

    private IEnumerable<string> Fallbacks(string code)
    {
        // The launcher installs each component to <installRoot>/<CODE> and drops a .openso-install
        // marker there — check that first so a fresh install is detected immediately.
        if (_installRoot != null)
            yield return Path.Combine(_installRoot, code);

        switch (code)
        {
            case "FSO":
                yield return Path.Combine(AppData, "FreeSO");
                yield return Path.Combine(AppData, "FSO");
                if (IsWindows) yield return @"C:\Program Files\FreeSO";
                break;
            case "TSO":
                yield return Path.Combine(AppData, "The Sims Online");
                yield return Path.Combine(AppData, "TSO");
                if (IsWindows) yield return @"C:\Program Files\Maxis\The Sims Online";
                break;
            case "Simitone":
                yield return Path.Combine(AppData, "Simitone");
                break;
        }
    }

    public async Task<IReadOnlyList<InstallStatus>> GetInstalledAsync()
    {
        var codes = new List<string> { "TSO", "FSO", "OpenAL", "RMS", "Simitone", "MacExtras", "Mono", "SDL" };
        var results = new List<InstallStatus>();
        foreach (var code in codes)
        {
            if (IsWindows && (code == "Mono" || code == "SDL")) continue;
            results.Add(await GetInstallStatusAsync(code));
        }
        return results;
    }

    public Task<InstallStatus> GetInstallStatusAsync(string code)
    {
        string? path = null;

        if (OperatingSystem.IsWindows() && WinRegistry.TryGetValue(code, out var reg))
            path = ReadRegistry(reg.Key, reg.Value);

        if (path != null && !PathExists(path)) path = null;

        if (path == null)
        {
            foreach (var fb in Fallbacks(code))
            {
                // An install-root path (<root>/<code>) counts only if the install actually completed,
                // marked with a .openso-install file — avoids false positives on a partial dir.
                bool isInstallRoot = _installRoot != null &&
                    string.Equals(Path.GetFullPath(fb), Path.GetFullPath(Path.Combine(_installRoot, code)), StringComparison.Ordinal);
                bool ok = isInstallRoot ? File.Exists(Path.Combine(fb, ".openso-install")) : PathExists(fb);
                if (ok) { path = fb; break; }
            }
        }

        path = NormalizeLocalPath(path);
        return Task.FromResult(new InstallStatus(code, path != null, path));
    }

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return path.Replace('\\', '/')
                   .Replace("/OpenSO.exe", "")
                   .Replace("/TSOClient/TSOClient.exe", "")
                   .Replace("/Simitone.Windows.exe", "");
    }

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadRegistry(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = hklm.OpenSubKey(subKey);
            if (key == null) return null;
            if (string.IsNullOrEmpty(valueName)) return subKey;
            return key.GetValue(valueName) as string;
        }
        catch { return null; }
    }
}
