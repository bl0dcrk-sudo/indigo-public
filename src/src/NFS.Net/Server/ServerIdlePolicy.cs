namespace NFS.Net.Server;

/// <summary>
/// How long a server loop sleeps between two iterations. During a round (countdown, race, a start being prepared, a stop being
/// drained) the loop keeps the 1 ms cadence of the lockstep steps; with no round in progress nothing is time-critical
/// (lobby, grace and idle timers have second-scale limits), so it waits longer, never beyond the next known deadline.
/// Pure logic (no clock, no sockets), so it is testable without wall time.
/// </summary>
public static class ServerIdlePolicy
{
    /// <summary>Sleep of a loop that has a round to drive (the previous fixed value).</summary>
    public const int BusyMillis = 1;
    /// <summary>Upper bound of the sleep without a round: also the worst-case delay before a received packet is dispatched,
    /// because the transport delivers events only from <c>Poll</c> on the loop thread.</summary>
    public const int IdleMillis = 10;

    /// <summary>Milliseconds to sleep. <paramref name="busy"/>: a round needs the fast cadence. <paramref name="nextDeadlineMicros"/>:
    /// the earliest time (same clock as <paramref name="nowMicros"/>) the loop itself must act at, or null/long.MaxValue for none.</summary>
    public static int WaitMillis(bool busy, long nowMicros, long? nextDeadlineMicros = null)
    {
        if (busy) return BusyMillis;
        if (nextDeadlineMicros is not { } deadline || deadline == long.MaxValue) return IdleMillis;
        long remaining = deadline - nowMicros;
        if (remaining <= 0) return BusyMillis;
        long millis = (remaining + 999) / 1000; // round up: never wake before the deadline
        return (int)Math.Clamp(millis, BusyMillis, IdleMillis);
    }
}
