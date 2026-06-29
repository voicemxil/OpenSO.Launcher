using System;

namespace OpenSO.Launcher.Models;

/// <summary>
/// Mirrors FSO's <c>TSOTime.FromUTC</c>: maps a real UTC time to the in-game time of day. The city's game
/// clock is global and derived purely from UTC (a game day = 2 real hours; odd real hours are offset by an
/// in-game hour), so the launcher can compute and tick it locally — anchored to the server's reported UTC —
/// instead of showing the host's wall-clock time.
/// </summary>
public static class GameClock
{
    /// <summary>In-game (Hours 0-23, Minutes 0-59) for the given real UTC time.</summary>
    public static (int Hours, int Minutes) InGameTime(DateTime utc)
    {
        int cycle = (utc.Hour % 2 == 1) ? 3600 : 0;
        cycle += utc.Minute * 60 + utc.Second;
        return (cycle / 300, (cycle % 300) / 5);
    }

    /// <summary>12-hour formatted in-game time, e.g. "2:23 PM".</summary>
    public static string Format(DateTime utc)
    {
        var (h, m) = InGameTime(utc);
        int h12 = h % 12 == 0 ? 12 : h % 12;
        return $"{h12}:{m:00} {(h < 12 ? "AM" : "PM")}";
    }
}
