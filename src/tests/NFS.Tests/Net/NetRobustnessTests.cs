using NFS.Core;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Stage 5a.3 failure handling: one-shot actions under duplication/loss, a slow client, disconnects (DNF policy), handshake
/// mismatches, foreign/malformed/late/future traffic and the waiting limit. The server never waits for a client.</summary>
public class NetRobustnessTests
{
    const int StartStep = 256;

    static RaceServerOptions Bounded(int raceSteps, int backlog = NetProtocol.MaxReliableBacklog) =>
        new() { MaxRaceSteps = raceSteps, MaxReliableBacklog = backlog, LingerMicros = 3_000_000, WaitTimeoutMicros = 30_000_000 };

    static bool BothDone(NetHarness h) => h.Clients.All(c => c.Client.State is RaceClientState.Finished or RaceClientState.Closed);

    [GameFact]
    public void ServerClock_LongStallHasBoundedCatchUp()
    {
        using var h = new NetHarness(21, Bounded(0));
        var a = h.Join("alice", LinkProfile.Rtt(0));
        var b = h.Join("bob", LinkProfile.Rtt(0));
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 2_000_000));
        int before = h.Host.Steps;
        h.Net.AdvanceTo(h.Net.Now + 10_000_000);
        h.Server.Update(h.Net.Now);
        Assert.InRange(h.Host.Steps - before, 1, NetProtocol.MaxCatchUpSteps);
        Assert.True(h.Server.Stats.ClockSlipSteps > 0);
        int after = h.Host.Steps;
        h.Server.Update(h.Net.Now);
        Assert.Equal(after, h.Host.Steps);
    }

    [GameFact]
    public void GearAndReset_AreAppliedExactlyOnceDespiteDuplicatesLossAndResends()
    {
        using var h = new NetHarness(11, Bounded(StartStep + 200));
        var link = LinkProfile.Rtt(100, 20, loss: 0.05, duplicate: 0.3);
        var a = h.Join("alice", link);
        h.Join("bob", link);
        bool gear = false, reset = false;
        h.Controls = (i, f) =>
        {
            if (i == 0 && f is { RaceStarted: true })
            {
                if (!gear) gear = a.QueueAction(NetActionKind.GearUp);
                if (f.Steps > StartStep + 80 && !reset) reset = a.QueueAction(NetActionKind.Reset);
            }
            return NetHarness.Weave(i, f);
        };
        Assert.True(h.RunUntil(() => BothDone(h), 40_000_000));
        Assert.True(gear && reset);
        int seat = a.Welcome!.Seat;
        var applied = new ReplayInputSource(h.Host.Recorder.Build(h.Host.Runner.Tick));
        var buttons = Enumerable.Range(0, h.Host.Steps).Select(i => applied.At(i, seat).Buttons).ToArray();
        Assert.Equal(1, buttons.Count(b => (b & PlayerInput.BtnGearUp) != 0));
        Assert.Equal(1, buttons.Count(b => (b & PlayerInput.BtnReset) != 0));
        Assert.Equal(0, buttons.Count(b => (b & PlayerInput.BtnGearDown) != 0));
        Assert.Equal(2, a.LatestAck.LastAction);
        Assert.Equal(0, a.PendingActions);
        // the other seat never pressed anything
        Assert.All(Enumerable.Range(0, h.Host.Steps), i => Assert.Equal(0, applied.At(i, 1 - seat).Buttons & ~NetInputSample.HeldMask));
    }

    [GameFact]
    public void SlowClient_DegradesAloneWithoutStallingTheHostOrTheOtherClient()
    {
        using var h = new NetHarness(12, Bounded(StartStep + 200));
        var fast = h.Join("fast", LinkProfile.Rtt(40));
        var slow = h.Join("slow", LinkProfile.Rtt(40), new LinkProfile(20, BandwidthBytesPerSecond: 6000));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => BothDone(h), 60_000_000), string.Join("\n", h.Log.TakeLast(15)));
        Assert.Equal(StartStep + 200, h.Host.Steps);
        Assert.Equal(0, h.Server.Stats.ClockSlipSteps);                 // the host kept the 32 Hz schedule
        Assert.True(h.Received[0].Count > (StartStep + 200) * 0.95, "the fast client was not slowed down");
        int slowFrames = h.Received[1].Count;
        Assert.InRange(slowFrames, 1, (StartStep + 200) / 2);            // degraded: most snapshots dropped in its queue
        foreach (var f in h.Received[1].Where(f => f.SnapshotSequence > 0)) NetHarness.AssertSameFrame(h.Frames[f.SnapshotSequence], f);
        Assert.NotNull(slow.Results);                                     // reliable result still arrives
        Assert.Equal(0, slow.Results!.DnfSeats);
        Assert.True(h.Server.Stats.MaxReliableBacklog <= NetProtocol.MaxReliableBacklog);
    }

    [GameFact]
    public void ReliableBacklogAboveTheBound_DropsTheClientAsDnfAndTheRaceGoesOn()
    {
        using var h = new NetHarness(20, Bounded(StartStep + 100, backlog: 2));
        var fast = h.Join("fast", LinkProfile.Rtt(0));
        var slow = h.Join("slow", LinkProfile.Rtt(0));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => fast.State == RaceClientState.Started && slow.State == RaceClientState.Started, 2_000_000));
        h.Clients[1].Link.SetProfiles(new LinkProfile(0), new LinkProfile(3000));     // several reliable countdown beeps queue up; fast delivery still fits
        Assert.True(h.RunUntil(() => fast.State == RaceClientState.Finished, 30_000_000), string.Join("\n", h.Log.TakeLast(15)));
        Assert.Equal(StartStep + 100, h.Host.Steps);
        Assert.Equal(0, h.Server.Stats.ClockSlipSteps);
        Assert.True(h.Server.Stats.SlowClientDrops >= 1);
        Assert.Equal(1 << slow.Welcome!.Seat, fast.Results!.DnfSeats);
        Assert.Contains(fast.PlayersLeft, p => p.Seat == slow.Welcome.Seat && p.Reason == NetRejectReason.SlowClient);
        Assert.True(h.RunUntil(() => slow.State == RaceClientState.Closed, 4_000_000));
        Assert.Equal(NetRejectReason.SlowClient, slow.RejectReason!.Reason);
    }

    [GameFact]
    public void CutConnection_AfterStartMakesTheSeatDnfAndTheRaceGoesOn()
    {
        using var h = new NetHarness(13, Bounded(StartStep + 400));
        var a = h.Join("alice", LinkProfile.Rtt(60, 10));
        var b = h.Join("bob", LinkProfile.Rtt(60, 10));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.Latest?.Steps > StartStep + 20, 30_000_000));
        h.Clients[1].Link.Cut();
        long cutStep = h.Host.Steps;
        Assert.True(h.RunUntil(() => a.PlayersLeft.Count > 0, 10_000_000));
        long detected = h.Host.Steps - cutStep;
        Assert.InRange(detected, 150, 175);                              // transport timeout (5 s) while the host kept stepping
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished, 30_000_000));
        Assert.Equal(StartStep + 400, h.Host.Steps);
        Assert.Equal(1 << b.Welcome!.Seat, a.Results!.DnfSeats);
        var bCar = h.Host.View.Latest.Car(b.Welcome.CarId);
        Assert.Equal(1, bCar.RaceState);                                 // aborted, never finished by itself
        Assert.Equal(RaceClientState.Closed, b.State);
    }

    [GameFact]
    public void AllHumansLeaving_EndsTheRaceAtOnceWithAResult()
    {
        using var h = new NetHarness(14, Bounded(0));
        var a = h.Join("alice", LinkProfile.Rtt(20));
        var b = h.Join("bob", LinkProfile.Rtt(20));
        Assert.True(h.RunUntil(() => a.Latest?.Steps > 40, 20_000_000));
        a.Leave();
        Assert.True(h.RunUntil(() => b.PlayersLeft.Count == 1, 2_000_000));
        b.Leave();
        Assert.True(h.RunUntil(() => h.Server.FinishReason != null, 2_000_000));
        Assert.Equal(NetFinishReason.AllHumansLeft, h.Server.FinishReason);
        Assert.NotNull(h.Host.Results);
        Assert.True(h.RunUntil(() => h.Server.Phase == NetRacePhase.Closed, 5_000_000));
    }

    static NetReject? Handshake(NetHarness h, byte[] hello)
    {
        var raw = new RecordingHandler();
        h.Net.AddClient(raw, LinkProfile.Rtt(10), LinkProfile.Rtt(10)).Connect(hello);
        h.RunUntil(() => raw.Connected || raw.Disconnected, 1_000_000);
        return raw.Disconnected ? NetReject.TryDecode(raw.DisconnectData ?? []) : null;
    }

    /// <summary>Hello in the v7 layout (no application version field): magic, versions, fingerprint, name, car.</summary>
    internal static byte[] LegacyHello(ushort version)
    {
        var w = new WireWriter(64);
        w.U32(NetProtocol.Magic);
        w.U16(version);
        w.U16(NetProtocol.PhysicsVersion);
        foreach (var b in NetHarness.Fingerprint) w.U8(b);
        w.U8(1);
        w.U8((byte)'x');
        return w.ToArray();
    }

    [GameFact]
    public void Handshake_RejectsOtherVersionsDataMalformedHellosAFullRaceAndLateJoins()
    {
        using var h = new NetHarness(15, Bounded(StartStep + 40));
        var fp = NetHarness.Fingerprint;
        Assert.Equal(NetRejectReason.ProtocolMismatch, Handshake(h, new NetHello(NetProtocol.Version + 1, NetProtocol.PhysicsVersion, fp, "x").Encode())!.Reason);
        Assert.Equal(NetRejectReason.PhysicsMismatch, Handshake(h, new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion + 1, fp, "x").Encode())!.Reason);
        var staleBuild = Handshake(h, new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, fp, "x", NetProtocol.NoCarType,
            new Version(0, 0, 0, 1)).Encode())!;
        Assert.Equal(NetRejectReason.AppVersionMismatch, staleBuild.Reason);
        Assert.Equal(BuildInfo.AppVersion, staleBuild.ServerAppVersion);
        var otherData = (byte[])fp.Clone();
        otherData[5] ^= 0x10;
        Assert.Equal(NetRejectReason.DataMismatch, Handshake(h, new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, otherData, "x").Encode())!.Reason);
        Assert.Equal(NetRejectReason.Malformed, Handshake(h, [1, 2, 3, 4, 5])!.Reason);
        Assert.Equal(NetRejectReason.ProtocolMismatch, Handshake(h, LegacyHello(7))!.Reason);
        var a = h.Join("alice", LinkProfile.Rtt(10));
        var b = h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 2_000_000));
        Assert.Equal(NetRejectReason.RaceInProgress, Handshake(h, new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, fp, "late").Encode())!.Reason);
        Assert.Equal(7, h.Server.Stats.Rejected);
        Assert.True(h.RunUntil(() => BothDone(h), 30_000_000));
        Assert.NotNull(a.Results);
    }

    [GameFact]
    public void LeavingBeforeTheStart_FreesTheSeatForANewConnectionWithANewEpoch()
    {
        using var h = new NetHarness(16, Bounded(StartStep + 20));
        var a = h.Join("alice", LinkProfile.Rtt(10));
        var raw = new RecordingHandler();                                // takes seat 1 but never reports ready
        var rawLink = h.Net.AddClient(raw, LinkProfile.Rtt(10), LinkProfile.Rtt(10));
        rawLink.Connect(a.Hello());
        Assert.True(h.RunUntil(() => raw.Received.Count > 0 && a.State == RaceClientState.Ready, 1_000_000));
        var rawWelcome = NetWelcome.Decode(raw.Received[0]);
        Assert.Equal(NetRejectReason.RaceFull, Handshake(h, a.Hello())!.Reason);
        h.Run(500_000);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);              // nobody starts without every seat ready
        rawLink.Disconnect();
        h.Run(100_000);
        Assert.Equal(NetRacePhase.WaitingForPlayers, h.Server.Phase);
        var c = h.Join("carol", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => c.State == RaceClientState.Started, 2_000_000));
        Assert.Equal(rawWelcome.Seat, c.Welcome!.Seat);
        Assert.NotEqual(rawWelcome.Epoch, c.Welcome.Epoch);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && c.State == RaceClientState.Finished, 30_000_000));
        Assert.Equal(0, a.Results!.DnfSeats);
    }

    [GameFact]
    public void ForeignStaleMalformedAndFloodedTraffic_IsCountedAndNeverAppliedNorAdvancesTheClock()
    {
        using var h = new NetHarness(17, Bounded(StartStep + 60) with { MaxBadMessages = 1000 });
        var a = h.Join("alice", LinkProfile.Rtt(20));
        var b = h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.Latest?.Steps > StartStep + 10, 20_000_000));
        var link = h.Clients[0].Link;
        var w = a.Welcome!;
        long next = h.Host.Steps + 1;
        NetInputPacket Inputs(int raceId, uint epoch, long seq, long target) =>
            new(raceId, epoch, 0, [new NetInputSample(seq, target, 127, 255, 255, 0)], [new NetAction(999, NetActionKind.Reset, target)]);
        link.Send(NetChannel.Unreliable, Inputs(w.RaceId, w.Epoch ^ 1, 1_000_000, next + 5).Encode());          // other connection epoch
        link.Send(NetChannel.Unreliable, Inputs(w.RaceId + 1, w.Epoch, 1_000_001, next + 5).Encode());          // other race
        link.Send(NetChannel.Unreliable, Inputs(w.RaceId, b.Welcome!.Epoch, 1_000_002, next + 5).Encode());     // the other seat's epoch
        var rng = new Random(5);
        for (int i = 0; i < 50; i++)
        {
            var junk = new byte[rng.Next(0, 120)];
            rng.NextBytes(junk);
            if (junk.Length > 0) junk[0] = (byte)rng.Next(0, 12);
            link.Send(NetChannel.Unreliable, junk);
        }
        h.Run(200_000);
        Assert.True(h.Server.Stats.WrongSession >= 3);
        Assert.True(h.Server.Stats.Malformed + h.Server.Stats.WrongSession >= 50);
        // future targets and late targets are refused; the client cannot pull the race forward
        long steps = h.Host.Steps;
        long seq = a.LastSentSequence + 1;
        link.Send(NetChannel.Unreliable, Inputs(w.RaceId, w.Epoch, seq, steps + 10_000).Encode());
        link.Send(NetChannel.Unreliable, Inputs(w.RaceId, w.Epoch, seq + 1, Math.Max(1, steps - NetProtocol.ActionExpirySteps - 1)).Encode());
        // a flood above the rate limit
        var flood = (Inputs(w.RaceId, w.Epoch, seq + 2, steps + 3) with { Actions = [] }).Encode();
        for (int i = 0; i < NetProtocol.MaxInputPacketsPerSecond * 2; i++) link.Send(NetChannel.Unreliable, flood);
        h.Run(300_000);
        Assert.True(a.LatestAck.FutureInputs >= 1);
        Assert.True(a.LatestAck.LateInputs >= 1);
        Assert.True(h.Server.Stats.RateLimited > 0);
        Assert.InRange(h.Host.Steps - steps, 9, 11);                     // 300 ms of server time = ~9.6 steps, nothing more
        // the injected reset (action 999 at full lock) was never applied: no reset button reached the host
        Assert.True(h.RunUntil(() => BothDone(h), 20_000_000));
        var applied = new ReplayInputSource(h.Host.Recorder.Build(h.Host.Runner.Tick));
        Assert.All(Enumerable.Range(0, h.Host.Steps), i => Assert.Equal(0, applied.At(i, w.Seat).Buttons & PlayerInput.BtnReset));
        Assert.Equal(0, h.Host.Runner.ManualResets + h.Host.Runner.ManualResets2);
    }

    [GameFact]
    public void Client_DropsMessagesOfAnotherRaceOrEpoch()
    {
        using var h = new NetHarness(18, Bounded(StartStep + 20));
        var a = h.Join("alice", LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => a.Latest?.Steps > 5, 20_000_000));
        var latest = a.Latest!;
        var w = a.Welcome!;
        var info = new NetSnapshotInfo(latest.SnapshotSequence + 50, NFS.Game.Session.RaceViewFrameKind.Step, 1, 1, NetRacePhase.Racing, true, false, 0, 2, 8, [], 3);
        foreach (var (race, epoch) in new[] { (w.RaceId + 1, w.Epoch), (w.RaceId, w.Epoch + 1) })
            foreach (var part in SnapshotCodec.EncodeParts(race, epoch, info, default, SnapshotCodec.EncodeCars(latest, _ => false)))
                a.OnReceive(NetChannel.Unreliable, part);
        a.OnReceive(NetChannel.ReliableOrdered, new NetResults(w.RaceId + 1, w.Epoch, NetFinishReason.Finished, 1, 1, 0, new RaceResults([])).Encode());
        Assert.True(a.Stats.WrongSession >= 3);
        Assert.Same(latest, a.Latest);
        Assert.Null(a.Results);
    }

    [GameFact]
    public void Server_ClosesWhenTheSeatsAreNotReadyInTime()
    {
        using var h = new NetHarness(19, new RaceServerOptions { WaitTimeoutMicros = 2_000_000 });
        var a = h.Join("alice", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Closed, 4_000_000));
        Assert.Equal(NetRacePhase.Closed, h.Server.Phase);
        Assert.Equal(NetRejectReason.RaceClosed, a.RejectReason!.Reason);
        Assert.Null(h.Server.FinishReason);
        Assert.Equal(0, h.Host.Steps);
    }
}
