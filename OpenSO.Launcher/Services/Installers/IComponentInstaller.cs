using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services.Installers;

/// <summary>
/// Contract for one component installer (port of the lib/installers/*.js classes).
/// Each installer runs an ordered set of steps: download → setup dir → extract → register →
/// platform extras. Implementations report progress and throw on failure (caller decides retry).
/// </summary>
public interface IComponentInstaller
{
    /// <summary>Component code, e.g. "FSO", "TSO".</summary>
    string Code { get; }

    /// <summary>Run the full install into <paramref name="installPath"/>.</summary>
    Task InstallAsync(string installPath, IProgress<ProgressReport> progress, CancellationToken ct = default);
}
