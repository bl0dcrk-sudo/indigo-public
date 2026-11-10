using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Dedicated server, consecutive single races (stage after 5a.4): after the result the operator `next` prepares a fresh
/// race on the SAME transport connections (fresh race id/epoch, new welcome/baseline/roster), clients become ready again and the
/// operator still has to issue `start`. Packets of the finished round can never touch the new one.</summary>
public class ServerNextRoundTests
{
    const int StartStep = 256;

    static RaceServerOptions Manual(int raceSteps) => new()
    {
        ManualStart = true,
        MaxRaceSteps = raceSteps,
        WaitTimeoutMicros = 60_000_000,
        LingerMicros = 0, // an interactive server keeps the result until `next` or `quit`
        BotCount = 2,
    };

    static ServerRaceConfig Race => new(Track: 1, Laps: 2, Opponents: 2);

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static void AssertRaceOver(RaceServer server) =>
        Assert.True(server.Phase is NetRacePhase.Finished or NetRacePhase.Aborted, $"phase {server.Phase}");

    static string Key(RaceResults r) => string.Join("|", r.Racers.Select(x =>
        $"{x.Place},{x.CarIndex},{x.Model},{x.Human},{x.Estimated},{x.RaceTicks},{x.RaceState},{x.TopSpeedFx}"));

    [GameFact]
    public void NextRound_RunsOnTheSameConnectionsAndRejectsTrafficOfTheFinishedRound()
    {
        var race = Race;
        using var h = new NetHarness(8811, Manual(StartStep + 60), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var a = h.Join("alice", LinkProfile.Rtt(20));
        var b = h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        // ---- first round: lobby, operator start, bounded finish
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string why), why);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished, 40_000_000), Tail(h));
        AssertRaceOver(h.Server);
        var oldWelcome = a.Welcome!;
        int oldRaceId = h.Server.RaceId;
        var oldPresentation = new RemoteRaceProvider(a);
        Assert.Equal(oldRaceId, oldPresentation.Description.RaceId);
        var oldResults = a.Results!;
        int oldSteps = h.Host.Steps;
        var oldLatest = a.Latest!;
        var oldFrame = h.Frames.OrderBy(kv => kv.Key).Last().Value;
        int connA = h.Clients[0].Link.Connection, connB = h.Clients[1].Link.Connection;
        Assert.Equal(oldRaceId, oldResults.RaceId);

        // The interactive server keeps the result and the connections (LingerMicros=0 must not close it).
        h.Run(2_000_000);
        AssertRaceOver(h.Server);
        Assert.False(a.Disconnected);
        Assert.False(b.Disconnected);

        // ---- operator `next`: fresh host, fresh ids, seats reset to not-ready, no automatic start
        Assert.True(h.Server.TryOperatorNext(out string error), error);
        int newRaceId = h.Server.RaceId;
        Assert.NotEqual(oldRaceId, newRaceId);
        Assert.NotSame(h.Host, h.Server.Host);
        Assert.Equal(0, h.Server.Host.Steps);
        Assert.Equal(oldSteps, h.Host.Steps);              // the finished host is frozen and no longer stepped
        Assert.Same(oldResults, a.Results);                // kept until the new welcome arrives (asserted below)
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase); // both connections are still there
        Assert.False(h.Server.TryOperatorNext(out _));      // only after the next result
        Assert.False(h.Server.TryOperatorStart(out string notReady));
        Assert.Contains("waiting for players", notReady);   // every seat must report ready again

        var roundFrames = new Dictionary<long, RaceViewFrame> { [0] = h.Server.Host.View.Latest };
        h.Server.Host.View.Published += (f, _) => roundFrames[f.SnapshotSequence] = f;

        // ---- the same connections receive the new welcome + baseline, report ready, and still wait for `start`
        Assert.True(h.RunUntil(() => a.Welcome!.RaceId == newRaceId && b.Welcome!.RaceId == newRaceId &&
                                     a.State == RaceClientState.Ready && b.State == RaceClientState.Ready, 5_000_000), Tail(h));
        Assert.Equal(connA, h.Clients[0].Link.Connection);
        Assert.Equal(connB, h.Clients[1].Link.Connection);
        Assert.NotEqual(oldWelcome.Epoch, a.Welcome!.Epoch);
        Assert.Equal(oldRaceId, oldPresentation.Description.RaceId); // presentation must not silently become the new round
        oldPresentation.DetachForNextRound();
        var newPresentation = new RemoteRaceProvider(a);
        Assert.Equal(newRaceId, newPresentation.Description.RaceId);
        newPresentation.DetachForNextRound();
        Assert.Null(a.Results);
        Assert.NotNull(a.Baseline);
        Assert.Equal(newRaceId, a.Baseline!.RaceId);
        Assert.Equal(0, a.Baseline.SnapshotSequence);

        // Roster of the new round reaches the clients; readiness alone never starts a round.
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart && a.PlayerNames.Count == 2 && a.ReadySeats.Count == 2, 5_000_000), Tail(h));
        h.Run(1_000_000);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.Equal(0, h.Server.Host.Steps);
        Assert.Equal(newRaceId, h.Server.RaceId);

        // ---- delayed packets of the finished round are rejected by the session gate, never applied
        long wrong = a.Stats.WrongSession;
        var staleInfo = new NetSnapshotInfo(oldLatest.SnapshotSequence + 1000, RaceViewFrameKind.Step,
            oldLatest.SimulationTick64 + 1000, oldSteps + 1000, NetRacePhase.Racing, true, false, 0,
            oldLatest.NumLaps, oldLatest.RacerCount, oldLatest.Ranking, 3);
        var staleParts = SnapshotCodec.EncodeParts(oldRaceId, oldWelcome.Epoch, staleInfo, default,
            SnapshotCodec.EncodeCars(oldFrame, _ => false));
        foreach (var part in staleParts) h.Net.ServerTransport.Send(connA, NetChannel.Unreliable, part);
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, new NetStart(oldRaceId, oldWelcome.Epoch, 1, NetProtocol.StartLeadMicros).Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, new NetDurable(oldRaceId, oldWelcome.Epoch, 1, oldLatest.SnapshotSequence,
            [new KeyValuePair<ushort, KnockedObject>(0x3210, new KnockedObject(1, 2, 3))], null).Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, oldResults.Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableUnordered, new NetEvents(oldRaceId, oldWelcome.Epoch,
            [new CountdownBeepEvent(oldRaceId, 1, 1, 1, 0)]).Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, new NetPlayerLeft(oldRaceId, oldWelcome.Epoch, 1, NetRejectReason.Left, 1).Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, new NetRoster(oldRaceId, oldWelcome.Epoch,
            new Dictionary<int, string> { [0] = "stale" }, [0]).Encode());
        h.Net.ServerTransport.Send(connA, NetChannel.ReliableOrdered, oldWelcome.Encode());
        h.Run(500_000); // deliver the delayed packets to the client over the same simulated link
        Assert.Equal(wrong + staleParts.Count + 7, a.Stats.WrongSession);
        Assert.Same(a.Baseline, a.Latest);              // no stale snapshot applied
        Assert.Null(a.Results);
        Assert.Equal(RaceClientState.Ready, a.State);
        Assert.Equal(newRaceId, a.Welcome!.RaceId);
        Assert.Empty(a.DurableKnocked);
        Assert.Equal(new[] { "alice", "bob" }, a.PlayerNames.Values.Order());
        Assert.DoesNotContain("stale", a.PlayerNames.Values);

        // A delayed old-round input packet never reaches the new host.
        long serverWrong = h.Server.Stats.WrongSession, inputs = h.Server.Stats.InputPackets;
        var staleInput = new NetInputPacket(oldRaceId, oldWelcome.Epoch, 0,
            [new NetInputSample(999, 0, 0, 255, 0, 0)], []).Encode();
        h.Clients[0].Link.Send(NetChannel.Unreliable, staleInput);
        h.Run(200_000); // the delayed uplink packet reaches the server with the old session
        Assert.Equal(serverWrong + 1, h.Server.Stats.WrongSession);
        Assert.Equal(inputs, h.Server.Stats.InputPackets);
        Assert.Equal(0, h.Server.Host.Steps);

        // ---- operator `start`: the second race runs with the same connections and fresh ids
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished, 40_000_000), Tail(h));
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.Equal(StartStep + 60, h.Server.Host.Steps);
        Assert.Equal(oldSteps, h.Host.Steps);
        Assert.Equal(newRaceId, a.Results!.RaceId);
        Assert.NotSame(oldResults, a.Results);
        Assert.NotSame(a.Results, b.Results);
        Assert.Equal(Key(h.Server.Host.Results!), Key(a.Results.Results));
        Assert.Equal(Key(h.Server.Host.Results!), Key(b.Results!.Results));
        Assert.False(a.Disconnected);
        Assert.False(b.Disconnected);
        Assert.Empty(a.PlayersLeft);
        Assert.Equal(connA, h.Clients[0].Link.Connection);
        Assert.Equal(newRaceId, h.Server.RaceId);

        // every second-round snapshot reproduces the second host exactly; no frame of another round leaked in
        var received = h.Received[0].Where(f => f.RaceId == newRaceId).ToList();
        Assert.NotEmpty(received);
        foreach (var f in received) NetHarness.AssertSameFrame(roundFrames[f.SnapshotSequence], f);
        Assert.All(h.Received[0], f => Assert.True(f.RaceId == oldRaceId || f.RaceId == newRaceId));
        Assert.All(h.Received[1], f => Assert.True(f.RaceId == oldRaceId || f.RaceId == newRaceId));
    }

    [GameFact]
    public void NextRound_WithoutManualStart_StillWaitsForTheOperator()
    {
        // A one-race server (no console) auto-starts and closes after the result; with a host factory but without ManualStart
        // the first round still auto-starts, while the prepared second round always waits for `start`.
        var race = Race;
        using var h = new NetHarness(8813, new RaceServerOptions
        {
            MaxRaceSteps = StartStep + 30, WaitTimeoutMicros = 60_000_000, LingerMicros = 3_000_000,
        }, race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var a = h.Join("alice", LinkProfile.Rtt(20));
        h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished, 40_000_000), Tail(h));
        AssertRaceOver(h.Server);
        Assert.True(h.Server.TryOperatorNext(out string error), error);
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        h.Run(2_000_000);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase); // readiness alone never starts the new round
        Assert.Equal(0, h.Server.Host.Steps);
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.Equal(NetRacePhase.Countdown, h.Server.Phase);
    }

    [GameFact]
    public void FailedNext_LeavesTheFinishedRoundIntactAndCanBeRetried()
    {
        var race = Race;
        int calls = 0;
        RaceHost Factory(int id, int humans)
        {
            if (Interlocked.Increment(ref calls) == 2) throw new InvalidOperationException("simulated data error");
            return race.CreateHost(GameData.Dir, id, humans);
        }

        using var h = new NetHarness(8812, Manual(StartStep + 20), race, Factory);
        var a = h.Join("alice", LinkProfile.Rtt(20));
        h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string why), why);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished, 40_000_000), Tail(h));
        var oldHost = h.Server.Host;
        int oldRaceId = h.Server.RaceId;
        var oldResults = a.Results!;

        Assert.False(h.Server.TryOperatorNext(out string error));
        Assert.Contains("cannot create the next race", error);
        Assert.Same(oldHost, h.Server.Host);
        Assert.Equal(oldRaceId, h.Server.RaceId);
        AssertRaceOver(h.Server);
        h.Run(1_000_000); // the interactive result still does not close
        AssertRaceOver(h.Server);
        Assert.False(a.Disconnected);
        Assert.Same(oldResults, a.Results);

        Assert.True(h.Server.TryOperatorNext(out error), error);
        Assert.NotEqual(oldRaceId, h.Server.RaceId);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.True(h.RunUntil(() => a.Welcome!.RaceId == h.Server.RaceId && a.State == RaceClientState.Ready, 5_000_000), Tail(h));
        Assert.Null(a.Results);
    }

    [GameFact]
    public void Next_WithFactoryHostOnAUsedRaceId_IsRejectedAndCanBeRetried()
    {
        // A factory that ignores the requested id and hands back a host on the finished round's id would desynchronise
        // every client (their session gate rejects the round), so the server must refuse it and keep the result.
        var race = Race;
        int calls = 0;
        NetHarness? harness = null;
        RaceHost Factory(int id, int humans)
        {
            calls++;
            return calls == 2 ? race.CreateHost(GameData.Dir, harness!.Server.RaceId, humans) : race.CreateHost(GameData.Dir, id, humans);
        }

        using var h = new NetHarness(8814, Manual(StartStep + 20), race, Factory);
        harness = h;
        var a = h.Join("alice", LinkProfile.Rtt(20));
        h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string why), why);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished, 40_000_000), Tail(h));

        var oldHost = h.Server.Host;
        int oldRaceId = h.Server.RaceId;
        var oldResults = a.Results!;
        var oldWelcome = a.Welcome!;

        Assert.False(h.Server.TryOperatorNext(out string error));
        Assert.Contains("does not match the requested", error);
        Assert.Contains(oldRaceId.ToString("X8"), error);
        Assert.Same(oldHost, h.Server.Host);
        Assert.Equal(oldRaceId, h.Server.RaceId);
        AssertRaceOver(h.Server);
        Assert.Same(oldResults, a.Results);
        Assert.Same(oldWelcome, a.Welcome);
        Assert.False(a.Disconnected);
        h.Run(1_000_000); // the finished round and its connections survive the refused command
        AssertRaceOver(h.Server);
        Assert.False(a.Disconnected);

        // The retry with a correct host succeeds on a fresh id.
        Assert.True(h.Server.TryOperatorNext(out error), error);
        Assert.Equal(3, calls);
        Assert.NotEqual(oldRaceId, h.Server.RaceId);
        Assert.NotSame(oldHost, h.Server.Host);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.True(h.RunUntil(() => a.Welcome!.RaceId == h.Server.RaceId && a.State == RaceClientState.Ready, 5_000_000), Tail(h));
        Assert.Null(a.Results);
    }
}
