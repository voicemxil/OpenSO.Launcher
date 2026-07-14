using System;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Formatting for small, glanceable status captions on the SERVER STATUS card. Kept separate from
/// <see cref="StatusService"/> (which owns the actual network fetch) so this pure, testable formatting
/// logic carries no HTTP/JSON dependencies into the headless test project.
/// </summary>
public static class StatusDisplay
{
    /// <summary>
    /// Formats when the SERVER STATUS card's stats were last SUCCESSFULLY refreshed, for display next to
    /// the Refresh button. Pure and testable: given the last successful load's LOCAL time (or null when
    /// nothing has loaded successfully yet this session — <c>MainViewModel.LoadStatusAsync</c> only
    /// advances this on a successful load, never on a failed/offline one, so the displayed time always
    /// honestly reflects the age of the data currently on screen), returns "Updated HH:mm:ss" or a
    /// not-yet-loaded placeholder.
    ///
    /// Deliberately an ABSOLUTE local time, not a relative "n min ago": the latter would silently go stale
    /// without a dedicated re-render timer, and the ViewModel doesn't have one purely for this — the
    /// existing ~10s steady status poll already makes the absolute clock digits visibly tick, which reads
    /// as "live" without inventing a second periodic UI-only timer.
    /// </summary>
    public static string FormatLastUpdated(DateTime? lastSuccessLocal) =>
        lastSuccessLocal is { } t ? $"Updated {t:HH:mm:ss}" : "Updated —";
}
