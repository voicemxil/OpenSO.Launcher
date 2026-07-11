using System;
using System.Linq;

namespace OpenSO.Launcher;

/// <summary>
/// Parses the launcher's CLI arguments. Currently a single recognized flag: <see cref="UpdateGameFlag"/>
/// (<c>--update-game</c>) — the game client's Windows handoff on a version mismatch for a
/// Launcher-managed install (see BUILD_AND_TEST.md → "Game → launcher handoff"): the client starts the
/// launcher with this flag and exits, and the launcher runs the update-then-launch flow automatically.
///
/// Args flow in via Avalonia's own desktop lifetime (<c>IClassicDesktopStyleApplicationLifetime.Args</c>,
/// populated from the <c>args</c> passed to <c>StartWithClassicDesktopLifetime</c> in Program.cs) — this
/// parser doesn't need to agree with Avalonia's own arg handling on anything except recognizing our flag.
/// Any other/unknown argument is ignored (forward-compatible).
/// </summary>
internal static class LauncherArgs
{
    public const string UpdateGameFlag = "--update-game";

    /// <summary>True iff <paramref name="args"/> contains <see cref="UpdateGameFlag"/> (case-insensitive).
    /// Null/empty args, and any unrecognized arguments, are treated as "no flag" rather than throwing.</summary>
    public static bool HasUpdateGame(string[]? args) =>
        args != null && args.Any(a => string.Equals(a, UpdateGameFlag, StringComparison.OrdinalIgnoreCase));
}
