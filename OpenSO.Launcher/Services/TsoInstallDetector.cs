using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OpenSO.Launcher.Models;

namespace OpenSO.Launcher.Services;

/// <summary>Where a detected TSO candidate came from (for UI provenance + copy/repair decisions).</summary>
public enum TsoProvenance
{
    /// <summary>The launcher-managed location (a sibling of the OpenSO client — the layout the game's
    /// relative "../The Sims Online/TSOClient/" locator finds first).</summary>
    Managed,
    /// <summary>Recorded in the Windows registry (Maxis InstallDir — what WindowsLocator reads).</summary>
    Registry,
    /// <summary>A well-known legacy retail install path (e.g. Program Files\Maxis\The Sims Online\TSOClient).</summary>
    LegacyPath
}

/// <summary>One detected candidate: a path, where it came from, and its completeness validation.</summary>
public sealed record TsoCandidate(string Path, TsoProvenance Provenance, TsoValidation Validation);

/// <summary>
/// Detects existing The Sims Online game-asset installs and orders them by precedence, mirroring how the
/// game client itself resolves the TSO path at runtime (OpenSO tso.client GameLocator):
///   1. the RELATIVE sibling (launcher-managed) location — the locators' first check,
///   2. the Windows REGISTRY (<c>HKLM\SOFTWARE\Maxis\The Sims Online\InstallDir</c>, read under BOTH the
///      32-bit/WOW6432Node view — the one <c>WindowsLocator</c> actually opens
///      (<c>RegistryView.Registry32</c>) — and the native view, so a value written under either is seen),
///   3. the well-known legacy retail PATHS (the locator's hardcoded Program Files fallback).
/// Registry beats the hardcoded legacy paths because an older-version install can live anywhere the
/// registry points. Each candidate is validated (<see cref="TsoAssetValidator"/>) so callers can tell a
/// complete install from a stale/incomplete pointer.
///
/// Off Windows there is no registry and no retail installer; the game only finds TSO via the sibling
/// path, so detection is the launcher-managed location alone (plus <see cref="LauncherConfig.InstallPath"/>
/// when the user has overridden the install root — the only user-configured path mechanism that exists).
/// The registry-gathering is a thin OS wrapper over the pure, testable <see cref="BuildCandidates"/>.
/// </summary>
public sealed class TsoInstallDetector
{
    /// <summary>Registry subkey the game and the legacy retail installer both use for TSO.</summary>
    public const string MaxisSubKey = @"SOFTWARE\Maxis\The Sims Online";
    private const string InstallDirValue = "InstallDir";

    private readonly LauncherConfig _config;

    public TsoInstallDetector(LauncherConfig? config = null) => _config = config ?? new LauncherConfig();

    /// <summary>The launcher-managed TSO directory: <c>&lt;installRoot&gt;/The Sims Online</c>.</summary>
    public string ManagedTsoDir =>
        Path.Combine(_config.ResolvedInstallRoot(), Components.InstallDirName("TSO"));

    /// <summary>Gathers real candidate sources for THIS machine, then orders + validates them.</summary>
    public IReadOnlyList<TsoCandidate> Detect() =>
        BuildCandidates(ManagedTsoDir, ReadRegistryPaths(), LegacyPaths());

    /// <summary>
    /// Pure ordering + validation used by <see cref="Detect"/> and by tests (no real registry needed):
    /// managed first, then registry paths, then legacy paths; duplicates collapse to the highest-precedence
    /// provenance; every surviving candidate is validated. Blank/duplicate paths are dropped.
    /// </summary>
    internal static IReadOnlyList<TsoCandidate> BuildCandidates(
        string? managedPath, IEnumerable<string> registryPaths, IEnumerable<string> legacyPaths)
    {
        var ordered = new List<(string Path, TsoProvenance Prov)>();
        if (!string.IsNullOrWhiteSpace(managedPath)) ordered.Add((managedPath, TsoProvenance.Managed));
        foreach (var p in registryPaths ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(p)) ordered.Add((p, TsoProvenance.Registry));
        foreach (var p in legacyPaths ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(p)) ordered.Add((p, TsoProvenance.LegacyPath));

        var seen = new HashSet<string>(PathComparer);
        var result = new List<TsoCandidate>();
        foreach (var (path, prov) in ordered)
        {
            if (!seen.Add(Normalize(path))) continue; // first (highest-precedence) provenance wins
            result.Add(new TsoCandidate(path, prov, TsoAssetValidator.Validate(path)));
        }
        return result;
    }

    /// <summary>
    /// The install the launcher should trust: the highest-precedence COMPLETE candidate, else the
    /// highest-precedence INCOMPLETE one (so the UI can surface "incomplete → repair"), else null when
    /// nothing usable was detected.
    /// </summary>
    public static TsoCandidate? SelectBest(IReadOnlyList<TsoCandidate> candidates)
    {
        var complete = candidates.FirstOrDefault(c => c.Validation.State == TsoInstallState.Complete);
        if (complete != null) return complete;
        return candidates.FirstOrDefault(c => c.Validation.State == TsoInstallState.Incomplete);
    }

    /// <summary>
    /// A COMPLETE install worth COPYING into the managed location instead of re-downloading 1.27 GB — i.e.
    /// a complete candidate that is NOT already the managed dir. Returns null when the only complete install
    /// is the managed one (nothing external to reuse) or nothing complete exists.
    /// </summary>
    public static TsoCandidate? SelectCopySource(IEnumerable<TsoCandidate> candidates, string managedTsoDir)
    {
        var managed = Normalize(managedTsoDir);
        foreach (var c in candidates)
        {
            if (c.Validation.State != TsoInstallState.Complete) continue;
            if (c.Provenance == TsoProvenance.Managed) continue;
            if (PathComparer.Equals(Normalize(c.Path), managed)) continue;
            return c;
        }
        return null;
    }

    // ── OS-specific gathering ──────────────────────────────────────────────────────────────────────

    private IEnumerable<string> ReadRegistryPaths()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        return ReadMaxisInstallDirs();
    }

    /// <summary>Well-known legacy retail install paths (Windows only). Both Program Files roots are
    /// checked because the 32-bit retail client could land in either, and the game's own hardcoded
    /// fallback is <c>C:\Program Files\Maxis\The Sims Online\TSOClient</c>.</summary>
    private static IEnumerable<string> LegacyPaths()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        yield return @"C:\Program Files\Maxis\The Sims Online\TSOClient";
        yield return @"C:\Program Files (x86)\Maxis\The Sims Online\TSOClient";
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadMaxisInstallDirs()
    {
        var paths = new List<string>();
        // Read BOTH views: WindowsLocator opens HKLM with RegistryView.Registry32 (WOW6432Node) — the
        // authoritative game read — while a native/64-bit writer would land in the default view. Read both
        // so a launcher- or retail-written value is found regardless of which view holds it.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(MaxisSubKey);
                if (key?.GetValue(InstallDirValue) is string dir && !string.IsNullOrWhiteSpace(dir))
                    paths.Add(dir);
            }
            catch { /* absent / access denied — nothing to add for this view */ }
        }
        return paths;
    }

    // ── path helpers ───────────────────────────────────────────────────────────────────────────────

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar)));
        }
        catch { return path; }
    }
}
