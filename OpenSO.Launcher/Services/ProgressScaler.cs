using System;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Maps a child operation's 0..1 progress into a [lo,hi] band of an overall multi-step stage, optionally
/// prefixing the detail text. Every installer/updater used to carry an identical private copy of this; it
/// lives here once so a fix (or the indeterminate-flag forwarding below) applies everywhere.
/// </summary>
public static class ProgressScaler
{
    public static IProgress<ProgressReport> Scale(IProgress<ProgressReport> outer, string stage,
        double lo, double hi, string? prefix = null) =>
        new Progress<ProgressReport>(r =>
            outer.Report(new ProgressReport(
                stage,
                lo + (hi - lo) * r.Fraction,
                prefix != null ? prefix + (r.Detail ?? "") : r.Detail,
                r.IsIndeterminate)));
}
