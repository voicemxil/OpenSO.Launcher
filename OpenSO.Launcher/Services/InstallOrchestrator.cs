using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenSO.Launcher.Models;
using OpenSO.Launcher.Services.Installers;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Port of the install orchestration in fsolauncher.js (runFullInstall / getMissingDependencies /
/// handleStandardInstall): given a component, resolve its missing dependencies from the graph,
/// then install everything in dependency order. This slice wires up the FSO installer; the other
/// component installers (TSO, Mono, SDL, RMS…) plug in as they're ported.
/// </summary>
public sealed class InstallOrchestrator
{
    private readonly LauncherConfig _config;
    private readonly InstallStateService _installState;

    public InstallOrchestrator(LauncherConfig config, InstallStateService installState)
    {
        _config = config;
        _installState = installState;
    }

    private static OSPlatformKind CurrentOs =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatformKind.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatformKind.MacOS : OSPlatformKind.Linux;

    /// <summary>
    /// Returns the dependency codes for <paramref name="code"/> that are not yet installed
    /// (port of getMissingDependencies — filters the graph against current install state).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetMissingDependenciesAsync(string code)
    {
        var graph = Components.DependenciesFor(CurrentOs);
        if (!graph.TryGetValue(code, out var deps)) return Array.Empty<string>();

        var installed = (await _installState.GetInstalledAsync())
            .Where(s => s.IsInstalled).Select(s => s.Code).ToHashSet();

        return deps.Where(d => !installed.Contains(d)).ToList();
    }

    /// <summary>
    /// Installs <paramref name="code"/> and any missing dependencies, in order.
    /// Currently only the FSO installer is wired (this slice). Missing deps that don't yet have a
    /// ported installer are reported via <paramref name="onUnsupported"/> so the UI can surface them.
    /// </summary>
    public async Task InstallAsync(string code, string installRoot,
        IProgress<ProgressReport> progress, Action<string>? onUnsupported = null,
        CancellationToken ct = default)
    {
        var missing = await GetMissingDependenciesAsync(code);
        var order = new List<string>();
        order.AddRange(missing);
        order.Add(code);

        foreach (var component in order)
        {
            var installer = CreateInstaller(component);
            if (installer == null)
            {
                onUnsupported?.Invoke(component); // e.g. TSO/Mono/SDL not ported yet
                continue;
            }

            var dir = Path.Combine(installRoot, Components.InstallDirName(component));
            progress.Report(new ProgressReport(component, 0, $"Installing {Components.Names.GetValueOrDefault(component, component)}…"));
            await installer.InstallAsync(dir, progress, ct);
        }
    }

    /// <summary>Factory for ported installers. Returns null for components not yet ported.</summary>
    private IComponentInstaller? CreateInstaller(string code) => code switch
    {
        "FSO" => new FsoInstaller(_config, RegisterInstall),
        "TSO" => new TsoInstaller(_config, RegisterInstall),
        "RMS" => new RmsInstaller(_config, _installState),
        "Mono" => new MonoInstaller(_config),
        "SDL" => new SdlInstaller(_config),
        _ => null
    };

    private readonly RegistryWriter _registry = new();

    /// <summary>Writes the install location so InstallStateService can find it next launch.
    /// Port of registry.js createGameEntry/createMaxisEntry: write the Windows registry entry when
    /// possible (requires elevation), and ALWAYS write the per-user marker as a fallback the
    /// path-probe can detect even without registry access.</summary>
    private void RegisterInstall(string code, string dir)
    {
        // Registry entry (Windows + elevated). Non-fatal if it fails — the marker covers detection.
        // Registry entry (Windows + elevated). Non-fatal if it fails — the marker covers detection.
        try { _registry.Write(code, dir); } catch { /* fall back to marker */ }

        // Per-user marker (always).
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".openso-install"), $"{code}\n{DateTimeOffset.UtcNow:o}\n");
        }
        catch { /* non-fatal */ }
    }
}
