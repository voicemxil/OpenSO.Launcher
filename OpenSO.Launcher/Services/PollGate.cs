using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

/// <summary>
/// Coordinates a periodic background poll with an on-demand manual trigger of the SAME work (e.g. the
/// adaptive status poll vs. the SERVER STATUS card's Refresh button, or the 6-hour launcher self-update
/// poll vs. Refresh's own self-update check), so a manual trigger never runs redundantly alongside the
/// automatic poll and the poll's next tick is deferred a full interval after a manual one instead of
/// firing right on top of it. Two independent, composable primitives — a caller uses whichever it needs:
/// <list type="bullet">
/// <item><b>Wait / Nudge.</b> The poll loop calls <see cref="WaitAsync"/> between ticks; a manual trigger
/// calls <see cref="Nudge"/> once it finishes so the loop wakes and re-schedules from "now" instead of
/// firing its already-elapsing interval right after the manual one. Extra nudges that land before anyone
/// is waiting are coalesced (not queued) — only one early wake is ever remembered.</item>
/// <item><b>TryEnter / Release.</b> An exclusive "in-flight" claim so the automatic poll and a manual
/// trigger never hit the network for the same work concurrently. <see cref="TryEnter"/> returns false
/// immediately (never blocks) when another caller already holds it — the loser should skip its own call
/// outright, since the winner's result covers it, rather than wait for the winner to finish.</item>
/// </list>
/// </summary>
public sealed class PollGate
{
    private readonly SemaphoreSlim _nudge = new(0, 1);
    private readonly SemaphoreSlim _inFlight = new(1, 1);

    /// <summary>Waits up to <paramref name="delay"/>, or returns early if <see cref="Nudge"/> is called
    /// first. Propagates <see cref="OperationCanceledException"/> when <paramref name="ct"/> is
    /// cancelled.</summary>
    public Task WaitAsync(TimeSpan delay, CancellationToken ct = default) => _nudge.WaitAsync(delay, ct);

    /// <summary>Wakes a pending <see cref="WaitAsync"/> early. Never throws: a no-op if a nudge is already
    /// pending or nobody is currently waiting.</summary>
    public void Nudge() { try { _nudge.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Attempts to claim exclusive ownership of the guarded work. Returns <c>true</c> once claimed
    /// — the caller MUST call <see cref="Release"/> (typically in a <c>finally</c>) when done. Returns
    /// <c>false</c> immediately (never blocks) if another call already owns it.</summary>
    public bool TryEnter() => _inFlight.Wait(0);

    /// <summary>Releases a claim taken by <see cref="TryEnter"/>.</summary>
    public void Release() => _inFlight.Release();
}
