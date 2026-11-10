using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Stage 5a.3 with the real race host over the deterministic simulated network: two humans, authoritative server,
/// common start, exact snapshots, input ACKs, one-shot actions, durable state, events, results, bad networks and bad peers.</summary>
public class NetRaceTests
{
    /// <summary>Green light at host step 256 (tick 0x200); a bounded race ends shortly after.</summary>
    const int StartStep = 256;

    static RaceServerOptions Bounded(int raceSteps = StartStep + 160, int backlog = NetProtocol.MaxReliableBacklog) =>
        new() { MaxRaceSteps = raceSteps, MaxReliableBacklog = backlog, LingerMicros = 3_000_000 };

    static string Key(RaceResults r) => string.Join("|", r.Racers.Select(x =>
        $"{x.Place},{x.CarIndex},{x.Model},{x.Human},{x.Estimated},{x.RaceTicks},{string.Join(".", x.LapTicks)},{x.BestLapTicks},{x.RaceState},{x.Driver},{x.TopSpeedFx}"));

    [GameFact]
    public void RealtimeControlChangeUsesLatestWinsAndIsAppliedByTheHost()
    {
        using var h = new NetHarness(921, new RaceServerOptions { MaxRaceSteps = 1200 });
        var a = h.Join("alice", LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        var provider = h.UseProvider(a, realtimeInputs: true);
        RaceViewFrame? serverFrameAtControlSend = null;
        a.InputSampleCreated += (sample, _) =>
        {
            if (sample.TargetStep == 0 && sample.Steer == 100) serverFrameAtControlSend ??= h.Host.View.Latest;
        };
        Assert.True(h.RunUntil(() => a.Latest?.RaceStarted == true, 10_000_000));

        var visibleBeforeInput = provider.ReadLatestState().Frame;
        provider.UpdateRealtimeControls(new PlayerInput(0, 0, 0, 0));
        Assert.True(h.RunUntil(() => h.Samples[0].Count > 0, 300_000));
        provider.UpdateRealtimeControls(new PlayerInput(100, 255, 0, 0));
        Assert.Same(visibleBeforeInput, provider.ReadLatestState().Frame); // controls never predict/mutate a client car pose
        Assert.True(h.RunUntil(() => h.Samples[0].Any(s => s.TargetStep == 0 && s.Steer == 100), 100_000));
        var replacement = h.Samples[0].Last(s => s.TargetStep == 0 && s.Steer == 100);
        Assert.True(h.RunUntil(() => a.LatestAck.AppliedInput >= replacement.Sequence, 1_000_000));
        Assert.NotNull(serverFrameAtControlSend);
        Assert.True(h.Host.Steps > serverFrameAtControlSend!.Steps); // only the authoritative host steps can advance this pose
        h.Run(1_000_000); // let the applied throttle produce visible authoritative displacement
        Assert.NotEqual(visibleBeforeInput.Car(a.Welcome!.CarId).Pos,
            provider.ReadLatestState().Frame.Car(a.Welcome!.CarId).Pos);
        var applied = new ReplayInputSource(h.Host.Recorder.Build(h.Host.Runner.Tick))
            .At(h.Host.Steps - 1, a.Welcome!.Seat);
        Assert.Equal(100, applied.Steer);
        Assert.Equal(248, applied.Gas); // PlayerDriver applies the original 8-step gas quantisation.
        Assert.NotEqual(serverFrameAtControlSend.Car(a.Welcome.CarId).Pos, h.Host.View.Latest.Car(a.Welcome.CarId).Pos);

        long packetsBeforeHeartbeat = a.Stats.InputPackets;
        h.Run(100_000);
        long heartbeatPackets = a.Stats.InputPackets - packetsBeforeHeartbeat;
        Assert.InRange(heartbeatPackets, 7, 9); // 80 Hz maximum heartbeat, enough to recover lost UDP controls promptly.
    }

    [GameFact]
    public void NaturalHostFinish_ReachesBothClientsOnceAndFreezesHost()
    {
        using var h = new NetHarness(31, new RaceServerOptions { LingerMicros = 3_000_000 });
        var a = h.Join("alice", LinkProfile.Rtt(100, 10, loss: 0.05));
        var b = h.Join("bob", LinkProfile.Rtt(100, 10, loss: 0.05));
        bool lap1 = false, lap2 = false;
        h.Controls = (i, frame) =>
        {
            // Server fixture places both humans across their lap boundary; finishing is still decided by RaceProgress.
            if (!lap1 && h.Host.Steps >= 300)
            {
                h.Host.Runner.Car.Lap = h.Host.Runner.Player2!.Obj.Lap = 1;
                lap1 = true;
            }
            if (!lap2 && h.Host.Steps >= 340)
            {
                h.Host.Runner.Car.Lap = h.Host.Runner.Player2!.Obj.Lap = 2;
                lap2 = true;
            }
            return NetHarness.Weave(i, frame);
        };
        Assert.True(h.RunUntil(() => a.Results != null && b.Results != null, 60_000_000));
        Assert.Equal(NetFinishReason.Finished, h.Server.FinishReason);
        Assert.Equal(Key(h.Host.Results!), Key(a.Results!.Results));
        Assert.Equal(Key(h.Host.Results!), Key(b.Results!.Results));
        Assert.Single(h.Server.PublishedEvents!.OfType<RaceFinishedEvent>());
        var first = a.Results;
        int steps = h.Host.Steps;
        h.Run(500_000);
        Assert.Equal(steps, h.Host.Steps);
        Assert.Same(first, a.Results);
    }

    [GameTheory]
    [InlineData(0, 0, 0.0, 0.0)]
    [InlineData(50, 5, 0.0, 0.0)]
    [InlineData(100, 10, 0.01, 0.02)]
    [InlineData(200, 20, 0.05, 0.05)]
    public void TwoHumans_CommonStartExactStateAckedInputsEventsAndResults(int rttMs, int jitterMs, double loss, double duplicate)
    {
        using var h = new NetHarness(rttMs + 1, Bounded());
        var link = LinkProfile.Rtt(rttMs, jitterMs, loss, duplicate);
        var a = h.Join("alice", link);
        var b = h.Join("bob", link);
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished, 40_000_000), string.Join("\n", h.Log.TakeLast(20)));
        // The final step's one-shot events travel reliably-unordered and can arrive after the ordered result that ends the
        // race; drain the link so the event accounting below sees every published event.
        h.Run(20_000);

        // seats: one race, two connections, distinct seats and cars chosen by the server
        Assert.Equal((h.Server.RaceId, h.Server.RaceId), (a.Welcome!.RaceId, b.Welcome!.RaceId));
        Assert.NotEqual(a.Welcome.Epoch, b.Welcome.Epoch);
        Assert.Equal([0, 1], new[] { a.Welcome.Seat, b.Welcome.Seat }.Order());
        Assert.Equal(h.Host.View.Description.LocalSeats.Order(), new[] { a.Welcome.CarId, b.Welcome.CarId }.Order());
        Assert.Equal([a.Welcome.CarId], a.Description!.LocalSeats);

        // the server clock alone ran the race: every step on time, no catch-up slips, bounded finish with one result
        Assert.Equal(StartStep + 160, h.Host.Steps);
        Assert.Equal(0, h.Server.Stats.ClockSlipSteps);
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.Equal(Key(h.Host.Results!), Key(a.Results!.Results));
        Assert.Equal(Key(h.Host.Results!), Key(b.Results!.Results));

        foreach (var (client, i) in new[] { (a, 0), (b, 1) })
        {
            // every completed snapshot reproduces the host frame of the same sequence exactly
            var frames = h.Received[i].Where(f => f.SnapshotSequence > 0).ToList();
            Assert.True(frames.Count > (StartStep + 160) * (1 - 4 * loss) * 0.95, $"client {i}: {frames.Count} frames");
            foreach (var f in frames) NetHarness.AssertSameFrame(h.Frames[f.SnapshotSequence], f);
            Assert.True(frames.Zip(frames.Skip(1)).All(p => p.First.SnapshotSequence < p.Second.SnapshotSequence), "frames in order");

            // inputs: the host applied exactly the sample of each target step (recorded inputs = sent samples), ACK follows
            int seat = client.Welcome!.Seat;
            var applied = new ReplayInputSource(h.Host.Recorder.Build(h.Host.Runner.Tick));
            var due = h.Samples[i].Where(s => s.TargetStep <= h.Host.Steps).ToList();
            int matched = due.Count(s =>
            {
                var rec = applied.At((int)s.TargetStep - 1, seat);
                return rec.Steer == s.Steer && rec.Gas == s.Gas && rec.Brake == s.Brake && (rec.Buttons & NetInputSample.HeldMask) == s.Held;
            });
            Assert.True(matched >= due.Count * 0.97, $"client {i}: {matched}/{due.Count} samples applied at their step");
            Assert.True(client.LatestAck.AppliedInput >= due.Max(s => s.Sequence) - 2, $"ack {client.LatestAck.AppliedInput}");
            Assert.True(client.LatestAck.NeutralSteps <= 2, $"neutral {client.LatestAck.NeutralSteps}");

            // one-shot events: each delivered at most once; delivered + expired = everything the host published (no result event)
            var expected = h.Server.PublishedEvents!.Where(e => e is not RaceFinishedEvent).Select(e => e.EventId).ToHashSet();
            var taken = h.Taken[i].Concat(client.Events!.TakeThrough(long.MaxValue)).Select(e => e.EventId).ToList();
            Assert.Equal(taken.Count, taken.Distinct().Count());
            Assert.True(taken.All(expected.Contains));
            Assert.Equal(expected.Count, taken.Count + client.Events.Expired);
            Assert.True(expected.Count >= 4, "countdown beeps");

            // durable state: tyre marks and knocked objects mirrored exactly
            NetProtocolTests.AssertSameSkids(h.Host.View.Latest.Skids, client.DurableSkids);
            Assert.Equal(h.Host.View.Latest.Knocked.OrderBy(k => k.Key), client.DurableKnocked.OrderBy(k => k.Key));
        }
        Assert.True(h.Host.View.Latest.Skids.Added > 0, "the scripted slides left tyre marks");
        Assert.True(h.Server.Stats.MaxSnapshotPart <= NetProtocol.MaxDatagram);
        Assert.True(h.Server.Stats.MaxDurableMessage <= NetProtocol.MaxDatagram);
    }

    [GameTheory]
    [InlineData(2)]
    [InlineData(0)]
    public void Snapshots_FitTheDatagramBudgetWithFullGridAndTraffic(int opponents)
    {
        var race = new ServerRaceConfig(Track: 1, Laps: 2, Opponents: opponents, Traffic: true);
        using var host = race.CreateHost(GameData.Dir, 77);
        int maxPart = 0, maxParts = 0;
        long bytes = 0;
        int budget = LiteNetLib.NetConstants.InitialMtu - LiteNetLib.NetConstants.ChanneledHeaderSize;
        Assert.True(NetProtocol.MaxDatagram <= budget, $"budget {NetProtocol.MaxDatagram} vs unfragmented sequenced payload {budget}");
        var info0 = new NetSnapshotInfo(0, RaceViewFrameKind.Step, 0, 0, NetRacePhase.Racing, true, false, 0, 2, 8, [], 3);
        for (int i = 0; i < StartStep + 320; i++)
        {
            host.Step(new PlayerInput((sbyte)(i % 50), 255, 0, 0), new PlayerInput((sbyte)-(i % 40), 255, 0, 0));
            var f = host.View.Latest;
            var parts = SnapshotCodec.EncodeParts(1, 1, info0 with { Ranking = f.Ranking, SnapshotSequence = f.SnapshotSequence, Steps = f.Steps },
                new NetSeatAck(long.MaxValue / 4, 1000, 1000, 1000, 1000, 1000), SnapshotCodec.EncodeCars(f, _ => true));
            maxParts = Math.Max(maxParts, parts.Count);
            foreach (var p in parts)
            {
                maxPart = Math.Max(maxPart, p.Length);
                bytes += p.Length;
                var r = new WireReader(p);
                SnapshotCodec.Decode(NetHeader.Read(ref r), ref r); // every part decodes on its own
            }
        }
        if (opponents == 2) Assert.True(host.View.Latest.Cars.Count >= 8, "full grid");
        else Assert.Contains(host.View.Latest.Cars, c => (c.State.Flags & CarFlags.Traffic) != 0);
        Assert.True(maxPart <= NetProtocol.MaxDatagram, $"part {maxPart} B");
        Assert.True(maxParts <= 6, $"{maxParts} parts per snapshot with {host.View.Latest.Cars.Count} cars");
        Assert.True(bytes / (StartStep + 320) * NetProtocol.StepHz < 160_000, $"{bytes / (StartStep + 320) * NetProtocol.StepHz} B/s");
    }
}
