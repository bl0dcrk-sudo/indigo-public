using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Sleep choice of the server loops: 1 ms cadence while a round runs, a longer bounded wait otherwise. The decision
/// is pure (no wall clock); the session timers themselves are driven by the injected clock and covered by the lifecycle,
/// reconnect and shutdown tests.</summary>
public class ServerIdlePolicyTests
{
    [Fact]
    public void ABusyLoopKeepsTheOneMillisecondCadenceWhateverTheDeadline()
    {
        Assert.Equal(1, ServerIdlePolicy.WaitMillis(true, 0));
        Assert.Equal(1, ServerIdlePolicy.WaitMillis(true, 0, 50_000_000));
    }

    [Fact]
    public void AnIdleLoopWithoutDeadlineWaitsTheIdleBound()
    {
        Assert.Equal(ServerIdlePolicy.IdleMillis, ServerIdlePolicy.WaitMillis(false, 123));
        Assert.Equal(ServerIdlePolicy.IdleMillis, ServerIdlePolicy.WaitMillis(false, 123, long.MaxValue));
        Assert.InRange(ServerIdlePolicy.IdleMillis, 5, 20);
    }

    [Theory]
    [InlineData(0L, 100_000_000L, 10)]  // far deadline: capped
    [InlineData(0L, 10_000L, 10)]       // exactly the bound
    [InlineData(0L, 4_000L, 4)]         // near deadline: shortened
    [InlineData(0L, 3_001L, 4)]         // rounded up, never early
    [InlineData(0L, 1L, 1)]             // below 1 ms: the minimum
    [InlineData(500L, 500L, 1)]         // due now
    [InlineData(900L, 100L, 1)]         // overdue
    public void AnIdleLoopNeverSleepsPastTheNextDeadline(long now, long deadline, int expected) =>
        Assert.Equal(expected, ServerIdlePolicy.WaitMillis(false, now, deadline));

    [Fact]
    public void NeverReturnsLessThanOneNorMoreThanTheBound()
    {
        for (long d = -5_000; d < 30_000; d += 137)
            Assert.InRange(ServerIdlePolicy.WaitMillis(false, 0, d), ServerIdlePolicy.BusyMillis, ServerIdlePolicy.IdleMillis);
    }

    [GameFact]
    public void ASessionServerIsIdleInTheLobbyAndFastDuringTheCountdownAndRace()
    {
        var options = new SessionServerOptions { CountdownMicros = 2_000_000, MaxRaceSteps = 30, RaceWaitTimeoutMicros = 120_000_000 };
        using var h = new SessionHarness(401, options);
        Assert.False(h.Server.NeedsFastLoop); // no game at all
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.List != null, 1_000_000));
        alice.Create("game", 2, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        Assert.False(h.Server.NeedsFastLoop); // a lobby only
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InCountdown, 1_000_000));
        Assert.True(h.Server.NeedsFastLoop);
        Assert.True(h.RunUntil(() => alice.InRace, 10_000_000));
        Assert.True(h.Server.NeedsFastLoop);
        Assert.True(h.RunUntil(() => alice.Results != null && !h.Server.NeedsFastLoop, 60_000_000), "the loop must relax after the round");
    }

    [GameFact]
    public void AStoppingSessionServerKeepsTheFastCadenceToDrainItsOutput()
    {
        using var h = new SessionHarness(402);
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.List != null, 1_000_000));
        Assert.False(h.Server.NeedsFastLoop);
        h.Server.Shutdown();
        Assert.True(h.Server.NeedsFastLoop);
    }
}
