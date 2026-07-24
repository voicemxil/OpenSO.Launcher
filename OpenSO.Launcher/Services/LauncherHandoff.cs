using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Writes/refreshes the game→launcher handoff marker consumed by the FSO client: a UTF-8, single-line
/// file named <see cref="MarkerFileName"/> dropped into the GAME install root, holding the absolute path
/// of THIS launcher's own executable. The client treats an install as "Launcher-managed" iff this file
/// exists AND the path it names still exists (see BUILD_AND_TEST.md → "Game → launcher handoff").
///
/// Called from three places (single shared helper, not three copies) so both fresh AND pre-existing
/// installs end up marked:
/// <list type="bullet">
/// <item>after a successful full install/update — <see cref="Installers.FsoInstaller.InstallAsync"/>.</item>
/// <item>after a successful delta update — <see cref="Updates.DeltaUpdateEngine.TryDeltaUpdateAsync"/>.</item>
/// <item>on every game launch — <see cref="GameLauncher.Launch"/> — so an install made by an OLDER
/// launcher (that never wrote the marker) gets one the first time the user presses PLAY, without
/// waiting for the next update.</item>
/// </list>
///
/// Writing the marker is always BEST-EFFORT: a permissions error, an unwritable/missing install dir, or
/// an undeterminable launcher path must never fail an install, update, or launch — every failure here is
/// swallowed.
/// </summary>
internal static class LauncherHandoff
{
    public const string MarkerFileName = "openso-launcher.path";

    // Encoding.UTF8 emits a byte-order-mark preamble (EF BB BF), which would land as a literal U+FEFF
    // character at the start of the file. The game's reader trims the line with plain string.Trim(), and
    // .NET's char.IsWhiteSpace does NOT treat U+FEFF as whitespace — a BOM would survive the trim, get
    // prepended to the path, and make File.Exists/Directory.Exists fail on an otherwise-correct path. Use
    // UTF-8 WITHOUT a BOM so the file contains nothing but the path's own bytes.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>This launcher's own executable path. <see cref="Environment.ProcessPath"/> resolves
    /// correctly for the published self-contained/single-file apphost; the current process's main-module
    /// path is a fallback for hosts where <c>ProcessPath</c> is unavailable.
    ///
    /// Under an AppImage, <c>ProcessPath</c> points into the transient <c>/tmp/.mount_*</c> squashfs, which
    /// vanishes when the launcher exits — a marker to it would be a dead path by the time the game reads it.
    /// The real, persistent file is the .AppImage the runtime records in the <c>APPIMAGE</c> env var, so that
    /// wins when present. The game <c>Process.Start</c>s the marker path directly; an .AppImage is an ELF with
    /// the exec bit, so it starts fine.</summary>
    internal static string? CurrentLauncherPath()
    {
        try
        {
            var proc = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            return ResolveMarkerPath(Environment.GetEnvironmentVariable("APPIMAGE"), proc);
        }
        catch { return null; }
    }

    /// <summary>Pure marker-path decision: prefer the AppImage path (<c>APPIMAGE</c>) when set, else the
    /// process/apphost path. Injectable inputs keep it unit-testable without a real AppImage mount.</summary>
    internal static string? ResolveMarkerPath(string? appImageEnv, string? processPath)
        => !string.IsNullOrWhiteSpace(appImageEnv) ? appImageEnv
         : (!string.IsNullOrWhiteSpace(processPath) ? processPath : null);

    /// <summary>
    /// Writes/refreshes <see cref="MarkerFileName"/> in <paramref name="gameInstallDir"/> with this
    /// launcher's executable path. Never throws: a failure — permissions, the install dir missing/
    /// uncreatable, a path colliding with an existing non-directory entry, an undeterminable launcher
    /// path, or anything else — is silently ignored so the caller's install/update/launch always proceeds.
    /// </summary>
    public static void WriteMarker(string? gameInstallDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameInstallDir)) return;
            var launcherPath = CurrentLauncherPath();
            if (string.IsNullOrWhiteSpace(launcherPath)) return;

            Directory.CreateDirectory(gameInstallDir);
            File.WriteAllText(Path.Combine(gameInstallDir, MarkerFileName), launcherPath, Utf8NoBom);
        }
        catch { /* best-effort — never break an install/update/launch over the handoff marker */ }
    }
}
