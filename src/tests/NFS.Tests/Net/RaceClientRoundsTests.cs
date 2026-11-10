using NFS.Core;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Stage 5a.4 foundation: one transport connection carries consecutive single races. After the result of a round a
/// welcome with another race id and epoch starts a clean round (same name/fingerprint/connection), while delayed packets of the
/// old round and duplicated welcomes are ignored.</summary>
public class RaceClientRoundsTests
{
    const int StartStep = 256;

    /// <summary>Records what the client sends after the wrapper is installed, then forwards it to the real connection.</summary>
    sealed class CapturingTransport(INetClientTransport inner) : INetClientTransport
    {
        public List<byte[]> Sent { get; } = [];
        public void Send(NetChannel channel, byte[] data) { Sent.Add(data); inner.Send(channel, data); }
        public void Disconnect() => inner.Disconnect();
        public long RoundTripMicros => inner.RoundTripMicros;
    }

    static NetInputPacket DecodeInput(byte[] data)
    {
        var r = new WireReader(data);
        var h = NetHeader.Read(ref r);
        Assert.Equal(NetMessageType.Input, h.Type);
        return NetInputPacket.Decode(h, ref r);
    }

    static NetReady DecodeReady(byte[] data)
    {
        var r = new WireReader(data);
        var h = NetHeader.Read(ref r);
        Assert.Equal(NetMessageType.Ready, h.Type);
        return NetReady.Decode(h, ref r);
    }

    [GameFact]
    public void FinishedRound_NewWelcomeClearsRoundStateAndOldTrafficIsRejected()
    {
        using var h = new NetHarness(71, new RaceServerOptions { MaxRaceSteps = StartStep + 120, LingerMicros = 60_000_000 });
        int prepared = 0;
        NetWelcome? preparedWelcome = null;
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), prepare: w => { prepared++; preparedWelcome = w; return true; });
        h.Join("bob", LinkProfile.Rtt(10));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished, 40_000_000), string.Join("\n", h.Log.TakeLast(20)));
        Assert.Equal(1, prepared);
        Assert.NotNull(a.Results);
        Assert.Equal(2, a.PlayerNames.Count);                 // roster of the finished round is present and must be discarded
        Assert.Equal(2, a.ReadySeats.Count);

        var oldWelcome = a.Welcome!;
        var oldLatest = a.Latest!;
        var oldResults = a.Results!;

        // durable state of the old round, applied while it is still the current round
        ushort knockedId = 0x3210;
        a.OnReceive(NetChannel.ReliableOrdered, new NetDurable(oldWelcome.RaceId, oldWelcome.Epoch,
            a.LatestDurableSequence + 1, oldLatest.SnapshotSequence,
            [new KeyValuePair<ushort, KnockedObject>(knockedId, new KnockedObject(1, 2, 3))], null).Encode());
        Assert.True(a.DurableKnocked.ContainsKey(knockedId));

        var wire = new CapturingTransport(h.Clients[0].Link);
        a.Transport = wire;

        int newRaceId = oldWelcome.RaceId + 1;
        uint newEpoch = oldWelcome.Epoch < uint.MaxValue ? oldWelcome.Epoch + 1 : oldWelcome.Epoch - 1;
        var newWelcome = oldWelcome with { RaceId = newRaceId, Epoch = newEpoch };

        // duplicated / partially reused welcomes never reset the finished round
        long wrong = a.Stats.WrongSession;
        a.OnReceive(NetChannel.ReliableOrdered, oldWelcome.Encode());
        a.OnReceive(NetChannel.ReliableOrdered, (oldWelcome with { RaceId = newRaceId }).Encode());
        a.OnReceive(NetChannel.ReliableOrdered, (oldWelcome with { Epoch = newEpoch }).Encode());
        Assert.Equal(wrong + 3, a.Stats.WrongSession);
        Assert.Same(oldWelcome, a.Welcome);
        Assert.Equal(RaceClientState.Finished, a.State);
        Assert.Equal(1, prepared);
        Assert.Empty(wire.Sent);

        // the new welcome is accepted: clean round, same connection, prepare and ready again
        a.OnReceive(NetChannel.ReliableOrdered, newWelcome.Encode());
        Assert.Equal(RaceClientState.Ready, a.State);
        Assert.Equal(newRaceId, a.Welcome!.RaceId);
        Assert.Equal(newEpoch, a.Welcome.Epoch);
        Assert.Equal(2, prepared);
        Assert.Equal(newRaceId, preparedWelcome!.RaceId);

        Assert.Null(a.Results);
        Assert.Null(a.Baseline);
        Assert.Null(a.Latest);
        Assert.Null(a.LatestInfo);
        Assert.Empty(a.PlayersLeft);
        Assert.Empty(a.PlayerNames);
        Assert.Empty(a.ReadySeats);
        Assert.Empty(a.DurableKnocked);
        Assert.Equal(0, a.DurableSkids.Added);
        Assert.Equal(0, a.PendingActions);
        Assert.Equal(0, a.PendingInputs);
        Assert.Empty(a.DrainFrames());

        var ready = DecodeReady(Assert.Single(wire.Sent));
        Assert.Equal(newRaceId, ready.RaceId);
        Assert.Equal(newEpoch, ready.Epoch);
        Assert.Equal(oldWelcome.SetupHash, ready.SetupHash);

        // identity and fingerprint survive the round change
        var hello = NetHello.Decode(a.Hello());
        Assert.Equal("alice", hello.Name);
        Assert.Equal(NetHarness.Fingerprint, hello.DataFingerprint);

        // a duplicated new welcome after the round began is ignored (no second ready)
        wrong = a.Stats.WrongSession;
        a.OnReceive(NetChannel.ReliableOrdered, newWelcome.Encode());
        Assert.Equal(wrong + 1, a.Stats.WrongSession);
        Assert.Equal(2, prepared);
        Assert.Single(wire.Sent);
        Assert.Equal(RaceClientState.Ready, a.State);

        // baseline of the new race (the old one is gone, so only a fresh sequence 0 publishes)
        var source = h.Frames[0];
        var newBaseline = source with { RaceId = newRaceId };
        var baselineInfo = new NetSnapshotInfo(0, RaceViewFrameKind.Initial, newBaseline.SimulationTick64, newBaseline.Steps,
            NetRacePhase.WaitingForPlayers, false, false, 0, newBaseline.NumLaps, newBaseline.RacerCount, newBaseline.Ranking, 1);
        var baselineParts = SnapshotCodec.EncodeParts(newRaceId, newEpoch, baselineInfo, default,
            SnapshotCodec.EncodeCars(newBaseline, _ => true));
        Assert.NotEmpty(baselineParts);
        foreach (var part in baselineParts) a.OnReceive(NetChannel.ReliableOrdered, part);
        Assert.NotNull(a.Baseline);
        Assert.Same(a.Baseline, a.Latest);
        Assert.Equal(newRaceId, a.Baseline!.RaceId);
        Assert.Equal(0, a.Baseline.SnapshotSequence);
        Assert.Single(a.DrainFrames());

        // delayed packets of the finished round are rejected by the session header, never applied
        wrong = a.Stats.WrongSession;
        var before = a.Latest;
        var oldInfo = new NetSnapshotInfo(900, RaceViewFrameKind.Step, 900, 900, NetRacePhase.Racing, true, false, 0, 2, 8, [], 3);
        var oldParts = SnapshotCodec.EncodeParts(oldWelcome.RaceId, oldWelcome.Epoch, oldInfo, default,
            SnapshotCodec.EncodeCars(oldLatest, _ => false));
        foreach (var part in oldParts) a.OnReceive(NetChannel.Unreliable, part);
        a.OnReceive(NetChannel.ReliableOrdered, new NetDurable(oldWelcome.RaceId, oldWelcome.Epoch,
            a.LatestDurableSequence + 1, oldLatest.SnapshotSequence,
            [new KeyValuePair<ushort, KnockedObject>(0x4444, new KnockedObject(4, 5, 6))], null).Encode());
        a.OnReceive(NetChannel.ReliableOrdered, new NetResults(oldWelcome.RaceId, oldWelcome.Epoch, NetFinishReason.Finished,
            oldResults.Step, oldResults.SimulationTick64, oldResults.DnfSeats, oldResults.Results).Encode());
        a.OnReceive(NetChannel.ReliableUnordered, new NetEvents(oldWelcome.RaceId, oldWelcome.Epoch,
            [new CountdownBeepEvent(oldWelcome.RaceId, 1, 1, 1, 0)]).Encode());
        a.OnReceive(NetChannel.ReliableOrdered, new NetPlayerLeft(oldWelcome.RaceId, oldWelcome.Epoch, 1, NetRejectReason.Left, 1).Encode());
        a.OnReceive(NetChannel.ReliableOrdered, new NetRoster(oldWelcome.RaceId, oldWelcome.Epoch,
            new Dictionary<int, string> { [0] = "stale" }, [0]).Encode());

        Assert.Equal(wrong + oldParts.Count + 5, a.Stats.WrongSession);
        Assert.Same(before, a.Latest);
        Assert.Null(a.Results);
        Assert.Empty(a.PlayersLeft);
        Assert.Empty(a.PlayerNames);
        Assert.Empty(a.ReadySeats);
        Assert.Empty(a.DurableKnocked);
        Assert.Equal(0, a.Events!.Pending);

        // drive the new race: start, input samples and snapshots carry the new race id/epoch
        a.OnReceive(NetChannel.ReliableOrdered, new NetStart(newRaceId, newEpoch, 1, NetProtocol.StartLeadMicros).Encode());
        Assert.Equal(RaceClientState.Started, a.State);
        a.Controls = new PlayerInput(12, 200, 0, 0);
        int sent = wire.Sent.Count;
        a.Update(h.Net.Now);
        Assert.True(wire.Sent.Count > sent);
        var input = DecodeInput(wire.Sent[^1]);
        Assert.Equal(newRaceId, input.RaceId);
        Assert.Equal(newEpoch, input.Epoch);
        Assert.NotEmpty(input.Samples);
        Assert.Equal(12, input.Samples[^1].Steer);
        Assert.Equal(200, input.Samples[^1].Gas);
        Assert.True(a.LastSentSequence > 0);

        var stepFrame = newBaseline with { SnapshotSequence = 1, Kind = RaceViewFrameKind.Step, Steps = 1, SimulationTick64 = 2, RaceStarted = true };
        var stepInfo = new NetSnapshotInfo(1, RaceViewFrameKind.Step, 2, 1, NetRacePhase.Racing, true, false, 0,
            stepFrame.NumLaps, stepFrame.RacerCount, stepFrame.Ranking, 1);
        foreach (var part in SnapshotCodec.EncodeParts(newRaceId, newEpoch, stepInfo, default,
                     SnapshotCodec.EncodeCars(stepFrame, _ => false)))
            a.OnReceive(NetChannel.Unreliable, part);
        Assert.Equal(1, a.Latest!.SnapshotSequence);
        Assert.Equal(newRaceId, a.Latest.RaceId);

        a.OnReceive(NetChannel.ReliableUnordered, new NetEvents(newRaceId, newEpoch,
            [new CountdownBeepEvent(newRaceId, 1, 1, 2, 0)]).Encode());
        Assert.Equal(1, a.Events!.Pending);
        a.OnReceive(NetChannel.ReliableOrdered, new NetRoster(newRaceId, newEpoch,
            new Dictionary<int, string> { [0] = "alice", [1] = "bob" }, [0, 1]).Encode());
        Assert.Equal("alice", a.PlayerNames[0]);
        Assert.Equal(2, a.ReadySeats.Count);

        // the next result closes the second round and yet another welcome is still accepted (the gate is reusable)
        a.OnReceive(NetChannel.ReliableOrdered, new NetResults(newRaceId, newEpoch, NetFinishReason.TimeLimit, 1, 2, 0,
            oldResults.Results).Encode());
        Assert.Equal(RaceClientState.Finished, a.State);
        Assert.NotNull(a.Results);
        wrong = a.Stats.WrongSession;
        a.OnReceive(NetChannel.ReliableOrdered, oldWelcome.Encode());
        Assert.Equal(wrong + 1, a.Stats.WrongSession);
        Assert.Equal(newRaceId, a.Welcome!.RaceId);
        Assert.Equal(RaceClientState.Finished, a.State);
        var thirdWelcome = newWelcome with { RaceId = newRaceId + 1, Epoch = newEpoch + 1 };
        a.OnReceive(NetChannel.ReliableOrdered, thirdWelcome.Encode());
        Assert.Equal(RaceClientState.Ready, a.State);
        Assert.Equal(newRaceId + 1, a.Welcome!.RaceId);
        Assert.Equal(3, prepared);
        Assert.Null(a.Results);
    }

    [GameFact]
    public void PreStartReconfigurationWelcomeIsAccepted_StaleAndLateOnesAreIgnored()
    {
        using var h = new NetHarness(72, new RaceServerOptions { LingerMicros = 60_000_000 });
        int prepared = 0;
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), prepare: _ => { prepared++; return true; });
        var b = h.Join("bob", LinkProfile.Rtt(10));
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Ready, 2_000_000), string.Join("\n", h.Log.TakeLast(20)));
        var welcome = a.Welcome!;
        var wire = new CapturingTransport(h.Clients[0].Link);
        a.Transport = wire;

        // The server composed the race for the connected humans: a welcome with a fresh id AND epoch replaces the lobby round.
        var reconfigured = welcome with { RaceId = welcome.RaceId + 1, Epoch = welcome.Epoch + 1 };
        a.OnReceive(NetChannel.ReliableOrdered, reconfigured.Encode());
        Assert.Equal(RaceClientState.Ready, a.State);
        Assert.Equal(reconfigured.RaceId, a.Welcome!.RaceId);
        Assert.Equal(reconfigured.Epoch, a.Welcome.Epoch);
        Assert.Equal(2, prepared);
        var ready = DecodeReady(Assert.Single(wire.Sent));
        Assert.Equal(reconfigured.RaceId, ready.RaceId);
        Assert.Equal(reconfigured.Epoch, ready.Epoch);
        Assert.Equal(reconfigured.SetupHash, ready.SetupHash);
        Assert.Null(a.Latest);   // the composed round starts clean; its baseline arrives next
        Assert.Null(a.Baseline);
        Assert.Equal(reconfigured.RaceId, a.Description!.RaceId);

        // Stale, duplicated and partially reused welcomes leave the composed round intact and never resend Ready.
        long wrong = a.Stats.WrongSession;
        a.OnReceive(NetChannel.ReliableOrdered, welcome.Encode());                                       // previous race
        a.OnReceive(NetChannel.ReliableOrdered, reconfigured.Encode());                                  // duplicate
        a.OnReceive(NetChannel.ReliableOrdered, (reconfigured with { RaceId = welcome.RaceId + 5 }).Encode()); // reused epoch
        a.OnReceive(NetChannel.ReliableOrdered, (reconfigured with { Epoch = welcome.Epoch + 9 }).Encode());   // reused race id
        Assert.Equal(wrong + 4, a.Stats.WrongSession);
        Assert.Equal((reconfigured.RaceId, reconfigured.Epoch), (a.Welcome!.RaceId, a.Welcome.Epoch));
        Assert.Equal(RaceClientState.Ready, a.State);
        Assert.Single(wire.Sent);
        Assert.False(a.Disconnected);
        Assert.Equal(2, prepared);

        // Once the countdown started the round can no longer be reconfigured.
        a.OnReceive(NetChannel.ReliableOrdered, new NetStart(reconfigured.RaceId, reconfigured.Epoch, 1, NetProtocol.StartLeadMicros).Encode());
        Assert.Equal(RaceClientState.Started, a.State);
        a.OnReceive(NetChannel.ReliableOrdered, (reconfigured with { RaceId = reconfigured.RaceId + 1, Epoch = reconfigured.Epoch + 1 }).Encode());
        Assert.Equal(wrong + 5, a.Stats.WrongSession);
        Assert.Equal(reconfigured.RaceId, a.Welcome!.RaceId);
        Assert.Equal(RaceClientState.Started, a.State);
        Assert.Equal(2, prepared);

        // A welcome whose setup block does not match its hash still ends the client (hash gate unchanged); bob is still in
        // his first (lobby) round, so the fresh id/epoch passes the session gate.
        Assert.True(h.RunUntil(() => b.State == RaceClientState.Ready, 2_000_000), string.Join("\n", h.Log.TakeLast(20)));
        var bobWelcome = b.Welcome!;
        var invalidHash = bobWelcome with { RaceId = bobWelcome.RaceId + 2, Epoch = bobWelcome.Epoch + 2,
            SetupHash = new byte[bobWelcome.SetupHash.Length] };
        b.OnReceive(NetChannel.ReliableOrdered, invalidHash.Encode());
        Assert.Equal(RaceClientState.Closed, b.State);
        Assert.Equal(2, prepared);
    }
}
