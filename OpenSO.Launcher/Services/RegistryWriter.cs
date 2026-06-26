using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Writes the Windows registry install entries the game/launcher read back — port of registry.js
/// createGameEntry/createMaxisEntry (which shelled out to reg.exe in winreg.js). We use the managed
/// Registry API. On non-Windows this is a no-op; callers use the local-config marker fallback there.
///
/// Note: writing under HKLM requires elevation. The launcher should fall back to the per-user marker
/// file (InstallOrchestrator.RegisterInstall) when registry access is denied, exactly like upstream
/// (registry.js hasRegistryAccess()).
/// </summary>
public sealed class RegistryWriter
{
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Records an OpenSO/FreeSO client install (registry.js createGameEntry).</summary>
    public bool WriteFsoInstall(string installDir) =>
        WriteInstallDir(@"SOFTWARE\Rhys Simpson\FreeSO", installDir);

    /// <summary>Records a TSO/Maxis install (registry.js createMaxisEntry).</summary>
    public bool WriteTsoInstall(string installDir) =>
        WriteInstallDir(@"SOFTWARE\Maxis\The Sims Online", installDir);

    /// <summary>Dispatch used by the orchestrator: map a component code to its registry entry.</summary>
    public bool Write(string code, string installDir) => code switch
    {
        "FSO" => WriteFsoInstall(installDir),
        "TSO" => WriteTsoInstall(installDir),
        _ => false
    };

    private bool WriteInstallDir(string subKey, string installDir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return WriteWindows(subKey, installDir);
    }

    [SupportedOSPlatform("windows")]
    private static bool WriteWindows(string subKey, string installDir)
    {
        // Normalize to a directory path (the games store InstallDir as the folder).
        installDir = Path.GetFullPath(installDir);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = hklm.CreateSubKey(subKey, writable: true);
            key.SetValue("InstallDir", installDir, RegistryValueKind.String);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // No HKLM write access (not elevated). Caller falls back to the per-user marker file.
            return false;
        }
        catch
        {
            return false;
        }
    }
}
