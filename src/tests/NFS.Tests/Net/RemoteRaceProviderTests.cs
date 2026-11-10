using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

public sealed class RemoteRaceProviderTests
{
    static RaceServerOptions ShortRace() => new() { MaxRaceSteps = 300, LingerMicros = 3_000_000 };

    [GameFact]
    public void BothProviders_SubmitSeatZeroInputsAndAckTheirOwnTokensAndActions()
    {
        using var h = new NetHarness(814, new RaceServerOptions { MaxRaceSteps = 1_000, LingerMicros = 3_000_000 });
        var c0 = h.Join("first", LinkProfile.Rtt(10));
        var c1 = h.Join("second", LinkProfile.Rtt(10));
        var p0 = h.UseProvider(c0);
        var p1 = h.UseProvider(c1);
        Assert.True(h.RunUntil(() => c0.Latest?.RaceStarted == true && c1.Latest?.RaceStarted == true, 10_000_000));
        Assert.Equal([c0.Welcome!.CarId], p0.Description.LocalSeats);
        Assert.Equal([c1.Welcome!.CarId], p1.Description.LocalSeats);
        var before = h.Host.View.Latest;

        long[] t0 = [
            p0.SubmitInput(0, new PlayerInput(20, 248, 0, PlayerInput.BtnGearUp)),
            p0.SubmitInput(0, new PlayerInput(40, 240, 0, 0)),
            p0.SubmitInput(0, new PlayerInput(60, 232, 0, 0)),
            p0.SubmitInput(0, new PlayerInput(80, 224, 0, 0)),
            p0.SubmitInput(0, new PlayerInput(100, 216, 0, 0)),
        ];
        long[] t1 = [
            p1.SubmitInput(0, new PlayerInput(-20, 248, 0, PlayerInput.BtnReset)),
            p1.SubmitInput(0, new PlayerInput(-40, 240, 0, 0)),
        ];
        Assert.NotEqual(t0[^1], t1[^1]);
        // One poll fills several newly due host targets and assigns the queued submit tokens in order.
        long catchUpPoll = h.Net.Now + 4 * NetProtocol.StepMicros;
        p0.Poll(catchUpPoll);
        p1.Poll(catchUpPoll);
        Assert.True(h.RunUntil(() => p0.ReadLatestState().Frame.AppliedInput[0] >= t0[^1]
            && p1.ReadLatestState().Frame.AppliedInput[0] >= t1[^1]
            && c0.PendingActions == 0 && c1.PendingActions == 0
            && h.Host.Runner.ManualResets + h.Host.Runner.ManualResets2 > 0, 8_000_000));

        var applied = new ReplayInputSource(h.Host.Recorder.Build(h.Host.Runner.Tick));
        Assert.Equal(1, Enumerable.Range(0, h.Host.Steps).Count(i => (applied.At(i, c0.Welcome.Seat).Buttons & PlayerInput.BtnGearUp) != 0));
        Assert.Equal(1, Enumerable.Range(0, h.Host.Steps).Count(i => (applied.At(i, c1.Welcome.Seat).Buttons & PlayerInput.BtnReset) != 0));
        Assert.Equal(before.Cars[c0.Welcome.CarId].PoseEpoch, h.Host.View.Latest.Cars[c0.Welcome.CarId].PoseEpoch);
        Assert.NotEqual(before.Cars[c1.Welcome.CarId].PoseEpoch, h.Host.View.Latest.Cars[c1.Welcome.CarId].PoseEpoch);
        Assert.Equal(t0[^1], p0.ReadLatestState().Frame.AppliedInput[0]);
        Assert.Equal(t1[^1], p1.ReadLatestState().Frame.AppliedInput[0]);
    }

    [GameFact]
    public void ActionQueueSaturation_IsReportedAndDoesNotPartiallyQueueInputOrLoseRetry()
    {
        using var h = new NetHarness(815, new RaceServerOptions { MaxRaceSteps = 1_000 });
        var client = h.Join("first", LinkProfile.Rtt(10));
        h.Join("second", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest?.RaceStarted == true, 10_000_000));
        for (int i = 0; i < NetProtocol.MaxActionsPerPacket; i++)
            Assert.True(client.QueueAction(NetActionKind.GearDown));

        Assert.Throws<InvalidOperationException>(() => provider.SubmitInput(0,
            new PlayerInput(0, 248, 0, PlayerInput.BtnGearUp)));
        Assert.Equal(NetProtocol.MaxActionsPerPacket, client.PendingActions);
        Assert.Equal(0, client.PendingInputs);
        h.RunUntil(() => client.PendingActions == 0, 3_000_000);
        long token = provider.SubmitInput(0, new PlayerInput(0, 248, 0, PlayerInput.BtnGearUp));
        Assert.True(h.RunUntil(() => provider.ReadLatestState().Frame.AppliedInput[0] >= token
            && client.PendingActions == 0, 3_000_000));
    }

    [GameFact]
    public void DurableJoin_UsesStateAtExactSequenceAndDropsDelayedOlderState()
    {
        using var h = new NetHarness(816, new RaceServerOptions { MaxRaceSteps = 2_000 });
        var client = h.Join("first", LinkProfile.Rtt(10));
        h.Join("second", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest != null, 5_000_000));
        var basis = client.Latest!;
        var previousKnocked = basis.Knocked;
        var previousSkids = basis.Skids.Clone();
        long durableSequence = client.LatestDurableSequence;
        var changedSkids = previousSkids.Clone();
        changedSkids.Add(new Vec3(0x10000, 0, 0x20000), new Vec3(0x18000, 0, 0x30000), 1);
        var skidDelta = NetSkidDelta.Diff(previousSkids, changedSkids)!;
        ushort objectId = 60_000;
        var knocked = new KeyValuePair<ushort, KnockedObject>(objectId, new KnockedObject(123, 2, 17));
        long futureSnapshot = basis.SnapshotSequence + 3;
        var durable = new NetDurable(basis.RaceId, client.Welcome!.Epoch, durableSequence + 1,
            futureSnapshot, [knocked], skidDelta);

        void SendFrame(long snapshot, long durableSeq)
        {
            var info = client.LatestInfo! with { SnapshotSequence = snapshot, DurableSequence = durableSeq,
                Steps = basis.Steps + (int)(snapshot - basis.SnapshotSequence),
                SimulationTick64 = basis.SimulationTick64 + (int)(snapshot - basis.SnapshotSequence) * 2 };
            foreach (var part in SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome.Epoch, info, client.LatestAck,
                         SnapshotCodec.EncodeCars(basis, _ => false)))
                client.OnReceive(NetChannel.Unreliable, part);
            provider.Poll(h.Net.Now);
        }

        SendFrame(basis.SnapshotSequence + 1, durableSequence + 1); // wait for the durable channel
        Assert.Equal(basis.SnapshotSequence, provider.ReadLatestState().Frame.SnapshotSequence);
        SendFrame(basis.SnapshotSequence + 2, durableSequence);     // supersedes the waiting older snapshot
        var beforeChange = provider.ReadLatestState().Frame;
        var queuedBeforeChange = provider.DrainFrames();
        Assert.DoesNotContain(queuedBeforeChange, f => f.SnapshotSequence == basis.SnapshotSequence + 1);
        Assert.Contains(queuedBeforeChange, f => f.SnapshotSequence == basis.SnapshotSequence + 2);
        Assert.Equal(previousKnocked, beforeChange.Knocked);
        NetProtocolTests.AssertSameSkids(previousSkids, beforeChange.Skids);

        client.OnReceive(NetChannel.ReliableUnordered, durable.Encode());
        SendFrame(futureSnapshot, durableSequence + 1);
        var afterChange = provider.ReadLatestState().Frame;
        Assert.Equal(knocked.Value, afterChange.Knocked[objectId]);
        NetProtocolTests.AssertSameSkids(changedSkids, afterChange.Skids);

        long malformed = client.Stats.Malformed;
        SendFrame(futureSnapshot + 1, durableSequence);
        Assert.Equal(futureSnapshot, provider.ReadLatestState().Frame.SnapshotSequence);
        Assert.Equal(malformed, client.Stats.Malformed);
    }

    [GameFact]
    public void DurableLag_ContinuesPublishingSnapshotsWhileExactDurableStateArrivesBehind()
    {
        using var h = new NetHarness(818, new RaceServerOptions { MaxRaceSteps = 4_000 });
        var client = h.Join("first", LinkProfile.Rtt(10));
        h.Join("second", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest != null, 5_000_000));

        var basis = client.Latest!;
        long startSnapshot = basis.SnapshotSequence;
        long startDurable = client.LatestDurableSequence;
        var infoBasis = client.LatestInfo!;
        var skidState = client.DurableSkids.Clone();
        const int count = 96; // three snapshots of lag (~100 ms) over a multi-second stream

        void SendSnapshot(int index)
        {
            var info = infoBasis with { SnapshotSequence = startSnapshot + index,
                DurableSequence = startDurable + index, Steps = basis.Steps + index,
                SimulationTick64 = basis.SimulationTick64 + index * 2 };
            foreach (var part in SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome!.Epoch, info, client.LatestAck,
                         SnapshotCodec.EncodeCars(basis, _ => false)))
                client.OnReceive(NetChannel.Unreliable, part);
        }

        void SendDurable(int index)
        {
            var nextSkids = skidState.Clone();
            int x = index * 0x10000;
            nextSkids.Add(new Vec3(x, 0, 0x20000), new Vec3(x + 0x8000, 0, 0x30000), 1);
            var delta = NetSkidDelta.Diff(skidState, nextSkids)!;
            client.OnReceive(NetChannel.ReliableUnordered, new NetDurable(basis.RaceId, client.Welcome!.Epoch,
                startDurable + index, startSnapshot + index, [new KeyValuePair<ushort, KnockedObject>(
                    (ushort)(40_000 + index), new KnockedObject(index, 2, 1))], delta).Encode());
            skidState = nextSkids;
        }

        for (int i = 1; i <= count; i++)
        {
            h.Net.AdvanceTo(h.Net.Now + NetProtocol.StepMicros);
            SendSnapshot(i);
            if (i > 3) SendDurable(i - 3);
            provider.Poll(h.Net.Now);
            if (i >= 16)
                Assert.True(provider.ReadLatestState().Frame.SnapshotSequence >= startSnapshot + i - 4,
                    $"published stream stalled at input snapshot {i}");
        }

        var beforeTail = provider.ReadLatestState().Frame.SnapshotSequence;
        Assert.InRange(startSnapshot + count - beforeTail, 2, 4);
        for (int i = count - 2; i <= count; i++) SendDurable(i);
        provider.Poll(h.Net.Now);

        var latest = provider.ReadLatestState().Frame;
        Assert.Equal(startSnapshot + count, latest.SnapshotSequence);
        Assert.Equal(new KnockedObject(count, 2, 1), latest.Knocked[(ushort)(40_000 + count)]);
        NetProtocolTests.AssertSameSkids(skidState, latest.Skids);
    }

    [GameFact]
    public void SingleSubmit_IsAcknowledgedAfterManyAutomaticSamplesAndBoundedTokenHistory()
    {
        using var h = new NetHarness(819, new RaceServerOptions { MaxRaceSteps = 2_000 });
        var client = h.Join("first", LinkProfile.Rtt(10));
        h.Join("second", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest?.RaceStarted == true, 10_000_000));

        long token = provider.SubmitInput(0, new PlayerInput(72, 216, 0, 0));
        long pollStart = h.Net.Now;
        for (int i = 1; i <= 100; i++) provider.Poll(pollStart + i * NetProtocol.StepMicros);
        long wireAck = client.LastSentSequence;
        Assert.True(wireAck >= 90, "the submit should be followed by many automatically held samples");

        var basis = client.Latest!;
        var info = client.LatestInfo! with { SnapshotSequence = basis.SnapshotSequence + 1,
            Steps = basis.Steps + 1, SimulationTick64 = basis.SimulationTick64 + 2 };
        var ack = new NetSeatAck(wireAck, client.LatestAck.LastAction, client.LatestAck.LateInputs,
            client.LatestAck.FutureInputs, client.LatestAck.HeldSteps, client.LatestAck.NeutralSteps);
        foreach (var part in SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome!.Epoch, info, ack,
                     SnapshotCodec.EncodeCars(basis, _ => false)))
            client.OnReceive(NetChannel.Unreliable, part);
        provider.Poll(pollStart + 100 * NetProtocol.StepMicros);

        Assert.Equal(token, provider.ReadLatestState().Frame.AppliedInput[0]);
    }

    [GameFact]
    public void DelayedSnapshotAfterDurableHistoryPrunes_IsDroppedWithoutMalformedOrRegression()
    {
        using var h = new NetHarness(817, new RaceServerOptions { MaxRaceSteps = 2_000 });
        var client = h.Join("first", LinkProfile.Rtt(10));
        h.Join("second", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest != null, 5_000_000));
        var basis = client.Latest!;
        long startDurable = client.LatestDurableSequence;
        var skidState = client.DurableSkids.Clone();
        for (int i = 1; i <= 260; i++)
        {
            int x = i * 0x10000;
            var next = skidState.Clone();
            next.Add(new Vec3(x, 0, 0x20000), new Vec3(x + 0x8000, 0, 0x30000), 1);
            var delta = NetSkidDelta.Diff(skidState, next)!;
            var change = new NetDurable(basis.RaceId, client.Welcome!.Epoch, startDurable + i,
                basis.SnapshotSequence + i, [new KeyValuePair<ushort, KnockedObject>((ushort)(50_000 + i), new(i, 2, 1))], delta);
            client.OnReceive(NetChannel.ReliableUnordered, change.Encode());
            skidState = next;
        }

        long malformed = client.Stats.Malformed;
        var info = client.LatestInfo! with { SnapshotSequence = basis.SnapshotSequence + 1,
            DurableSequence = startDurable + 1, Steps = basis.Steps + 1, SimulationTick64 = basis.SimulationTick64 + 2 };
        foreach (var part in SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome!.Epoch, info, client.LatestAck,
                     SnapshotCodec.EncodeCars(basis, _ => false)))
            client.OnReceive(NetChannel.Unreliable, part);
        provider.Poll(h.Net.Now);
        Assert.Equal(basis.SnapshotSequence, provider.ReadLatestState().Frame.SnapshotSequence);
        Assert.Equal(malformed, client.Stats.Malformed);
        Assert.True(client.Stats.SnapshotsIncomplete > 0);
    }

    [GameFact]
    public void Provider_HandlesJitterLossAndReorderedSnapshots()
    {
        using var h = new NetHarness(811, ShortRace());
        var client = h.Join("alice", new LinkProfile(25, 20, 0.12, 0.03), new LinkProfile(25, 20, 0.12, 0.03));
        h.Join("bob", LinkProfile.Rtt(20));
        var provider = h.UseProvider(client);
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => client.Results != null, 30_000_000));
        provider.Poll(h.Net.Now);
        var received = h.Received[0].Where(f => f.SnapshotSequence > 0).ToArray();
        Assert.NotEmpty(received);
        Assert.True(received.Zip(received.Skip(1)).All(pair => pair.First.SnapshotSequence <= pair.Second.SnapshotSequence));
        Assert.True(h.Net.Lost > 0);
        Assert.True(client.Stats.StaleParts > 0, "jitter should deliver an older snapshot after a newer one");
        foreach (var frame in received.Where(f => f.Results == null))
            NetHarness.AssertSameFrame(h.Frames[frame.SnapshotSequence], frame);
        Assert.Contains(h.Taken[0], e => e is RaceFinishedEvent);

        var basis = client.Latest!;
        var info = client.LatestInfo! with { SnapshotSequence = basis.SnapshotSequence + 1,
            Steps = basis.Steps + 1, SimulationTick64 = basis.SimulationTick64 + 2 };
        var encoded = SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome!.Epoch, info, client.LatestAck,
            SnapshotCodec.EncodeCars(basis, _ => false), 256);
        Assert.True(encoded.Count > 1);
        client.OnReceive(NetChannel.Unreliable, encoded[0]);
        var nextInfo = info with { SnapshotSequence = info.SnapshotSequence + 1, Steps = info.Steps + 1,
            SimulationTick64 = info.SimulationTick64 + 2 };
        foreach (var part in SnapshotCodec.EncodeParts(basis.RaceId, client.Welcome.Epoch, nextInfo, client.LatestAck,
                     SnapshotCodec.EncodeCars(basis, _ => false), 256))
            client.OnReceive(NetChannel.Unreliable, part);
        Assert.True(client.Stats.SnapshotsIncomplete > 0);

        long durable = client.LatestDurableSequence;
        var later = new NetDurable(basis.RaceId, client.Welcome.Epoch, durable + 2, basis.SnapshotSequence, [], null);
        var earlier = later with { DurableSequence = durable + 1 };
        client.OnReceive(NetChannel.ReliableUnordered, later.Encode());
        Assert.Equal(durable, client.LatestDurableSequence);
        client.OnReceive(NetChannel.ReliableUnordered, earlier.Encode());
        Assert.Equal(durable + 2, client.LatestDurableSequence);
    }

    [GameFact]
    public void ResultAfterLastSnapshot_PublishesFinalFrameAndFinishEvent()
    {
        using var h = new NetHarness(812, new RaceServerOptions { MaxRaceSteps = 2_000, LingerMicros = 3_000_000 });
        var client = h.Join("alice", LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest != null && client.State == RaceClientState.Started, 5_000_000));
        Assert.True(h.RunUntil(() => client.Latest!.Steps > 270, 15_000_000));

        var before = provider.ReadLatestState().Frame;
        var endStep = before.Steps + 3;
        var result = new NetResults(client.Welcome!.RaceId, client.Welcome.Epoch, NetFinishReason.TimeLimit,
            endStep, before.SimulationTick64 + 6, 0, new RaceResults([]));
        client.OnReceive(NetChannel.ReliableOrdered, result.Encode());
        provider.Poll(h.Net.Now);

        var final = provider.ReadLatestState().Frame;
        Assert.Equal(before.Steps, final.Steps);
        Assert.Equal(before.SimulationTick64, final.SimulationTick64);
        Assert.Equal(RaceViewFrameKind.Resync, final.Kind);
        Assert.Equal(result.Results.Racers, final.Results!.Racers);
        var events = provider.DrainEvents();
        var finish = Assert.IsType<RaceFinishedEvent>(Assert.Single(events));
        Assert.Equal(result.Results.Racers, finish.Results.Racers);
        Assert.Contains(provider.DrainFrames(), f => f.Results?.Racers.SequenceEqual(result.Results.Racers) == true);

        var lateInfo = client.LatestInfo! with { SnapshotSequence = client.Latest!.SnapshotSequence + 1,
            Steps = client.Latest.Steps + 1, SimulationTick64 = client.Latest.SimulationTick64 + 2 };
        foreach (var part in SnapshotCodec.EncodeParts(before.RaceId, client.Welcome.Epoch, lateInfo, client.LatestAck,
                     SnapshotCodec.EncodeCars(client.Latest, _ => false)))
            client.OnReceive(NetChannel.Unreliable, part);
        provider.Poll(h.Net.Now);
        Assert.Same(final, provider.ReadLatestState().Frame);
        Assert.Empty(provider.DrainFrames());
        Assert.Empty(provider.DrainEvents());
    }

    [GameFact]
    public void Disconnect_IsReflectedInProviderState()
    {
        using var h = new NetHarness(813, ShortRace());
        var client = h.Join("alice", LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        var provider = h.UseProvider(client);
        Assert.True(h.RunUntil(() => client.Latest != null, 5_000_000));
        h.Clients[0].Link.Cut();
        h.Run(5_100_000);
        provider.Poll(h.Net.Now);
        Assert.False(provider.ReadLatestState().Connected);
    }

    /// <summary>Session architecture (openspec 4.3): the provider is the adapter of ONE round. Each round of a session gets its
    /// own provider over that round's <see cref="RaceClient"/>; the finished one keeps its results and ignores the next
    /// round, the new one starts from clean input tokens and acks.</summary>
    [GameFact]
    public void SessionRounds_EachRoundClientGetsItsOwnProviderWithItsOwnTokensAndResults()
    {
        using var h = new SessionHarness(817, new SessionServerOptions { CountdownMicros = 500_000, MaxRaceSteps = 256 + 150 });
        h.Net.DisconnectTimeoutMicros = 200_000;
        var peers = new[] { "alice", "bob" }.Select(name =>
        {
            var peer = new Peer(name);
            peer.Client = new SessionClient(SessionHarness.Fingerprint, name, (handler, hello) =>
            {
                var link = h.Net.AddClient(handler, LinkProfile.Rtt(10), LinkProfile.Rtt(10));
                link.Connect(hello);
                return link;
            }, () => h.Net.Now, new SessionClientOptions { PrepareRace = _ => true });
            peer.Client.Connect();
            return peer;
        }).ToArray();
        bool Run(Func<bool> done, long micros) => h.RunUntil(() =>
        {
            foreach (var p in peers) p.Drive(h.Net.Now);
            return done();
        }, micros);

        Assert.True(Run(() => peers.All(p => p.Client.Phase == SessionClientPhase.Browsing), 2_000_000));
        peers[0].Client.CreateGame("g", 2);
        Assert.True(Run(() => peers[0].Client.Phase == SessionClientPhase.InGame, 2_000_000));
        peers[1].Client.JoinGame(peers[0].Client.GameId);
        Assert.True(Run(() => peers[1].Client.Phase == SessionClientPhase.InGame, 2_000_000));
        Assert.True(peers[0].Client.StartRace());

        Assert.True(Run(() => peers.All(p => p.Provider?.ReadLatestState().Frame.RaceStarted == true), 10_000_000));
        var providers1 = peers.Select(p => p.Provider!).ToArray();
        var clients1 = peers.Select(p => p.Client.Race!).ToArray();
        Assert.All(providers1, p => Assert.Single(p.Description.LocalSeats));
        long token = providers1[0].SubmitInput(0, new PlayerInput(20, 248, 0, 0));
        Assert.Equal(1, token);
        Assert.True(Run(() => providers1[0].ReadLatestState().Frame.AppliedInput[0] >= token, 3_000_000));

        Assert.True(Run(() => peers.All(p => p.Client.Race?.Results != null && p.Client.Lobby!.Phase == NetGamePhase.Lobby), 60_000_000));
        Assert.All(providers1, p => Assert.NotNull(p.ReadLatestState().Frame.Results));
        var finalFrame = providers1[0].ReadLatestState().Frame;

        // Round 2 on the same connection: a new RaceClient, hence a new provider; the old one is dropped, not reset.
        Assert.True(peers[0].Client.StartRace());
        Assert.True(Run(() => peers.All(p => !ReferenceEquals(p.Client.Race, clients1[Array.IndexOf(peers, p)])
            && p.Provider?.ReadLatestState().Frame.RaceStarted == true), 20_000_000));
        for (int i = 0; i < peers.Length; i++)
        {
            Assert.NotSame(providers1[i], peers[i].Provider);
            Assert.NotEqual(providers1[i].Description.RaceId, peers[i].Provider!.Description.RaceId);
        }
        Assert.Same(finalFrame, providers1[0].ReadLatestState().Frame);   // the finished round's view is untouched
        Assert.Equal(1, peers[0].Provider!.SubmitInput(0, new PlayerInput(-20, 248, 0, 0)));   // tokens restart per provider
        Assert.True(Run(() => peers[0].Provider!.ReadLatestState().Frame.AppliedInput[0] >= 1, 3_000_000));
    }

    sealed class Peer(string name)
    {
        public string Name { get; } = name;
        public SessionClient Client { get; set; } = null!;
        public RemoteRaceProvider? Provider { get; private set; }
        RaceClient? _providerClient;

        /// <summary>Owner loop of a session client: update, one provider per round client, poll the provider.</summary>
        public void Drive(long now)
        {
            Client.Update(now);
            if (Client.Lobby is { Phase: NetGamePhase.Lobby } && !Client.IsReady && Client.Race?.Results == null) Client.SetReady(true);
            var race = Client.Race;
            if (race == null) return;
            if (!ReferenceEquals(race, _providerClient))
            {
                if (race.Baseline == null) { race.Update(now); return; }
                Provider?.DetachForNextRound();
                _providerClient = race;
                Provider = new RemoteRaceProvider(race);
            }
            Provider!.Poll(now);
        }
    }
}
