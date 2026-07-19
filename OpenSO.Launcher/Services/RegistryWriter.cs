using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenSO.Launcher.Services;

/// <summary>Which HKLM view a registry entry targets. TSO/Maxis entries are written to BOTH so the game's
/// 32-bit/WOW6432Node read (<c>RegistryView.Registry32</c>, see WindowsLocator) and any native reader see
/// the same value.</summary>
public enum RegistryScope
{
    /// <summary>32-bit / WOW6432Node view — the one the game's TSO locator opens.</summary>
    Wow6432,
    /// <summary>The default (native) view.</summary>
    Native
}

/// <summary>A single planned registry write under HKLM (pure data — see <see cref="RegistryWriter"/>).</summary>
public readonly record struct RegistryEntry(RegistryScope Scope, string SubKey, string ValueName, string Value);

/// <summary>
/// Writes the Windows registry install entries the game/launcher read back — port of registry.js
/// createGameEntry/createMaxisEntry (which shelled out to reg.exe in winreg.js). We use the managed
/// Registry API. On non-Windows this is a no-op; callers use the local-config marker fallback there.
///
/// The set of values written is exposed as a pure <see cref="RegistryEntry"/> PLAN (<see cref="PlanTsoInstall"/>
/// / <see cref="PlanFsoInstall"/>) so the decision — which subkey, which value, which views — is testable
/// without touching a real registry. <see cref="WriteTsoInstall"/> doubles as the reinstall RESET: it
/// (over)writes <c>SOFTWARE\Maxis\The Sims Online\InstallDir</c> in BOTH views to the new (managed) path,
/// so a stale pointer left by an old/incomplete install (e.g. the Program Files one) can no longer win the
/// game's registry lookup.
///
/// Note: writing under HKLM requires elevation. The launcher should fall back to the per-user marker
/// file (InstallOrchestrator.RegisterInstall) when registry access is denied, exactly like upstream
/// (registry.js hasRegistryAccess()).
/// </summary>
public sealed class RegistryWriter
{
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The Maxis/TSO subkey the game's WindowsLocator reads and the retail installer wrote.</summary>
    public const string TsoSubKey = @"SOFTWARE\Maxis\The Sims Online";
    /// <summary>The OpenSO/FreeSO client subkey.</summary>
    public const string FsoSubKey = @"SOFTWARE\Rhys Simpson\FreeSO";
    private const string InstallDirValue = "InstallDir";

    /// <summary>The exact registry values a TSO (re)install writes/resets — <c>InstallDir</c> under both the
    /// WOW6432Node and native views, pointing at the (managed) install dir. Pure/testable.</summary>
    public static IReadOnlyList<RegistryEntry> PlanTsoInstall(string installDir) => Plan(TsoSubKey, installDir);

    /// <summary>The exact registry values an OpenSO client install writes — <c>InstallDir</c> under both views.</summary>
    public static IReadOnlyList<RegistryEntry> PlanFsoInstall(string installDir) => Plan(FsoSubKey, installDir);

    private static IReadOnlyList<RegistryEntry> Plan(string subKey, string installDir)
    {
        // Normalize to a directory path (the games store InstallDir as the folder).
        var dir = SafeFullPath(installDir);
        return new[]
        {
            // The game opens HKLM with RegistryView.Registry32 (WOW6432Node) — write it FIRST/always.
            new RegistryEntry(RegistryScope.Wow6432, subKey, InstallDirValue, dir),
            new RegistryEntry(RegistryScope.Native,  subKey, InstallDirValue, dir),
        };
    }

    /// <summary>Records an OpenSO/FreeSO client install (registry.js createGameEntry).</summary>
    public bool WriteFsoInstall(string installDir) => WritePlan(PlanFsoInstall(installDir));

    /// <summary>Records/RESETS a TSO/Maxis install (registry.js createMaxisEntry). See class remarks.</summary>
    public bool WriteTsoInstall(string installDir) => WritePlan(PlanTsoInstall(installDir));

    /// <summary>Dispatch used by the orchestrator: map a component code to its registry entry.</summary>
    public bool Write(string code, string installDir) => code switch
    {
        "FSO" => WriteFsoInstall(installDir),
        "TSO" => WriteTsoInstall(installDir),
        _ => false
    };

    private bool WritePlan(IReadOnlyList<RegistryEntry> plan)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return WriteWindows(plan);
    }

    [SupportedOSPlatform("windows")]
    private static bool WriteWindows(IReadOnlyList<RegistryEntry> plan)
    {
        // Requires elevation; non-fatal if denied — the relative-path layout + marker still let the game
        // and the launcher find the install.
        bool wroteAny = false;
        foreach (var entry in plan)
        {
            var view = entry.Scope == RegistryScope.Wow6432 ? RegistryView.Registry32 : RegistryView.Default;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.CreateSubKey(entry.SubKey, writable: true);
                key.SetValue(entry.ValueName, entry.Value, RegistryValueKind.String);
                wroteAny = true;
            }
            catch (UnauthorizedAccessException) { return false; } // not elevated -> caller uses the marker fallback
            catch { /* try the other view */ }
        }
        return wroteAny;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
