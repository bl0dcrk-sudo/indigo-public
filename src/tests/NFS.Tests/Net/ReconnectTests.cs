using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>
/// Stage 3 (reconnect): a resume token issued with the seat, the grace window that reserves a seat lost in Countdown/Race,
/// the resume welcome with a fresh epoch and fresh baseline, rejection of stale controls, the client-side resume in Started,
/// and the automatic retry within the grace window. Everything runs on the simulated network (no sockets, no windows).
/// </summary>
public class ReconnectTests
{
    const long Grace = 30_000_000;

    static byte[] HelloWithToken(string name, byte[] token) =>
        new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, name,
            NetProtocol.NoCarType, BuildInfo.AppVersion, token).Encode();

    /// <summary>Two humans, countdown 0, a long race; returns once both are in the race with welcomes.</summary>
    static (SessionHarness H, SessionTestClient Alice, SessionTestClient Bob) RaceSetup(int seed, SessionServerOptions? options = null)
    {
        var h = new SessionHarness(seed, options ?? new SessionServerOptions
        {
            CountdownMicros = 0,
            MaxRaceSteps = 200_000,
            GraceMicros = Grace,
        });
        h.Net.DisconnectTimeoutMicros = 200_000;
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.List != null, 1_000_000));
        alice.Create("game", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => bob.List != null, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && alice.Lobby?.Members.Count == 2, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InRace && bob.InRace && bob.Welcome != null, 10_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        return (h, alice, bob);
    }

    static int MemberIndex(SessionTestClient client, string name) =>
        client.Lobby!.Members.ToList().FindIndex(m => m.Name == name);

    [GameFact]
    public void LobbyStateCarriesAStablePerSeatResumeToken()
    {
        using var h = new SessionHarness(101);
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null, 1_000_000));
        alice.Create("game", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && alice.Lobby?.Members.Count == 2, 1_000_000));

        var aliceToken = alice.Lobby!.ResumeToken;
        var bobToken = bob.Lobby!.ResumeToken;
        Assert.NotNull(aliceToken);
        Assert.NotNull(bobToken);
        Assert.Equal(NetProtocol.ResumeTokenLength, aliceToken!.Length);
        Assert.Equal(NetProtocol.ResumeTokenLength, bobToken!.Length);
        Assert.False(aliceToken.SequenceEqual(bobToken));

        bob.SetReady(true);
        Assert.True(h.RunUntil(() => bob.Ready, 1_000_000));
        Assert.Equal(aliceToken, alice.Lobby!.ResumeToken);
        Assert.Equal(bobToken, bob.Lobby!.ResumeToken);
    }

    [GameFact]
    public void RaceDisconnectReservesTheSeatAndTheTokenResumesItWithANewEpoch()
    {
        var (h, alice, bob) = RaceSetup(102);
        using (h)
        {
            var token = bob.Lobby!.ResumeToken!;
            var oldWelcome = bob.Welcome!;
            var oldEpoch = oldWelcome.Epoch;
            int bobSlot = bob.Lobby.Members.Single(m => m.Name == "bob").Slot;

            bob.Link.Cut();
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") >= 0 && !alice.Lobby!.Members[MemberIndex(alice, "bob")].Connected,
                2_000_000), string.Join("\n", h.Log.TakeLast(20)));
            Assert.True(alice.InRace);
            Assert.Equal(2, alice.Lobby!.Members.Count);
            var game = h.Server.Games.Single();
            Assert.Equal(2, game.MemberCount);
            Assert.Equal(2, game.Round!.MemberCount);
            Assert.Empty(alice.PlayerLefts);

            var resumed = h.Join("bob", hello: HelloWithToken("bob", token));
            Assert.True(h.RunUntil(() => resumed.Welcome != null && resumed.Connected, 2_000_000),
                string.Join("\n", h.Log.TakeLast(20)));
            Assert.True(resumed.Welcome!.Resume);
            Assert.Equal(oldWelcome.RaceId, resumed.Welcome.RaceId);
            Assert.Equal(oldWelcome.Seat, resumed.Welcome.Seat);
            Assert.NotEqual(oldEpoch, resumed.Welcome.Epoch);
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") >= 0 && alice.Lobby!.Members[MemberIndex(alice, "bob")].Connected,
                1_000_000));

            Assert.Equal(1, h.Server.Stats.Resumed);
            Assert.Equal(2, game.MemberCount);
            Assert.Equal(bobSlot, game.Members.Single(m => m.Name == "bob").Slot);
            Assert.Equal(2, game.Round!.MemberCount);
            Assert.Single(resumed.Welcomes);
            Assert.True(resumed.Welcomes[0].Resume);

            // Controls carrying the old epoch are rejected; controls of the new epoch are accepted.
            long wrongBefore = game.Round.Stats.WrongSession;
            long inputsBefore = game.Round.Stats.InputPackets;
            long target = game.Round.Host.Steps + 1;
            resumed.Link.Send(NetChannel.Unreliable, new NetInputPacket(oldWelcome.RaceId, oldEpoch, 0,
                [new NetInputSample(1, target, 10, 200, 0, 0)], []).Encode());
            h.Run(100_000);
            Assert.True(game.Round.Stats.WrongSession > wrongBefore);
            Assert.Equal(2, game.Round.MemberCount);

            resumed.Link.Send(NetChannel.Unreliable, new NetInputPacket(oldWelcome.RaceId, resumed.Welcome.Epoch, 0,
                [new NetInputSample(2, target + 1, 12, 180, 0, 0)], []).Encode());
            h.Run(100_000);
            Assert.True(game.Round.Stats.InputPackets > inputsBefore);
        }
    }

    [GameFact]
    public void GraceExpiryFreesTheSeatMarksDnfAndInvalidatesTheToken()
    {
        var (h, alice, bob) = RaceSetup(103, new SessionServerOptions
        {
            CountdownMicros = 0,
            MaxRaceSteps = 200_000,
            GraceMicros = 300_000,
        });
        using (h)
        {
            var token = bob.Lobby!.ResumeToken!;
            bob.Link.Cut();
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") >= 0 && !alice.Lobby!.Members[MemberIndex(alice, "bob")].Connected,
                1_000_000));
            var game = h.Server.Games.Single();
            Assert.Equal(2, game.MemberCount);

            int leftBefore = alice.PlayerLefts.Count;
            Assert.True(h.RunUntil(() => game.MemberCount == 1, 1_000_000), string.Join("\n", h.Log.TakeLast(20)));
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") < 0, 1_000_000));
            Assert.True(alice.PlayerLefts.Count > leftBefore);
            Assert.Equal(1, game.Round!.MemberCount);

            var late = h.Join("bob", hello: HelloWithToken("bob", token));
            Assert.True(h.RunUntil(() => late.Disconnected, 1_000_000));
            Assert.Equal(NetRejectReason.ResumeTokenInvalid, late.Reject?.Reason);
            Assert.Equal(1, h.Server.Stats.ResumeRefused);
        }
    }

    [GameFact]
    public void LobbyDisconnectIsAnOrdinaryLeaveAndTheTokenCannotResume()
    {
        using var h = new SessionHarness(104);
        var alice = h.Join("alice");
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => alice.List != null && bob.List != null, 1_000_000));
        alice.Create("game", 4, 1, 1);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        bob.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && alice.Lobby?.Members.Count == 2, 1_000_000));
        var token = bob.Lobby!.ResumeToken!;
        var game = h.Server.Games.Single();

        bob.Disconnect();
        Assert.True(h.RunUntil(() => game.MemberCount == 1 && alice.Lobby!.Members.Count == 1, 1_000_000));

        var resume = h.Join("bob", hello: HelloWithToken("bob", token));
        Assert.True(h.RunUntil(() => resume.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, resume.Reject?.Reason);
        Assert.Equal(1, game.MemberCount);

        // The name and the seat are free for a normal join: no reservation was kept.
        var again = h.Join("bob");
        Assert.True(h.RunUntil(() => again.List != null, 1_000_000));
        again.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => again.InLobby && game.MemberCount == 2, 1_000_000));
        Assert.True(again.Lobby!.Members.Single(m => m.Name == "bob").Connected);
    }

    [GameFact]
    public void ReservedNameIsNotGivenToAClientWithAnotherSeatsTokenDuringTheGraceWindow()
    {
        var (h, alice, bob) = RaceSetup(105);
        using (h)
        {
            var token = bob.Lobby!.ResumeToken!;
            bob.Link.Cut();
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") >= 0 && !alice.Lobby!.Members[MemberIndex(alice, "bob")].Connected,
                1_000_000));

            // A token of another seat does not unlock the name (without a token the name alone resumes: SessionLeaveResumeTests).
            var wrongSeat = h.Join("bob", hello: HelloWithToken("bob", alice.Lobby!.ResumeToken!));
            Assert.True(h.RunUntil(() => wrongSeat.Disconnected, 1_000_000));
            Assert.Equal(NetRejectReason.ResumeTokenInvalid, wrongSeat.Reject?.Reason);

            var resumed = h.Join("bob", hello: HelloWithToken("bob", token));
            Assert.True(h.RunUntil(() => resumed.Welcome?.Resume == true, 1_000_000), string.Join("\n", h.Log.TakeLast(20)));
            Assert.Null(resumed.Reject);
            Assert.Equal(1, h.Server.Stats.Resumed);
        }
    }

    // ---------------- client side ----------------

    sealed class SinkTransport : INetClientTransport
    {
        public List<byte[]> Sent { get; } = [];
        public void Send(NetChannel channel, byte[] data) => Sent.Add(data);
        public void Disconnect() { }
        public long RoundTripMicros => 0;
    }

    sealed class DeadTransport : INetClientTransport
    {
        public void Send(NetChannel channel, byte[] data) { }
        public void Disconnect() { }
        public long RoundTripMicros => 0;
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
    public void ResumeWelcomeInStartedResetsTheRoundAndKeepsSendingControls()
    {
        using var h = new NetHarness(105, new RaceServerOptions { MaxRaceSteps = 200_000, LingerMicros = 60_000_000 });
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10));
        h.Join("bob", LinkProfile.Rtt(10));
        h.Controls = NetHarness.Weave;
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && a.Latest != null, 10_000_000),
            string.Join("\n", h.Log.TakeLast(20)));

        var old = a.Welcome!;
        long durable = a.LatestDurableSequence;
        var sink = new SinkTransport();
        a.Transport = sink;
        uint newEpoch = old.Epoch == uint.MaxValue ? 1 : old.Epoch + 1;
        var resume = old with { Epoch = newEpoch, Resume = true };

        a.OnReceive(NetChannel.ReliableOrdered, resume.Encode());
        Assert.Equal(RaceClientState.Started, a.State);
        Assert.Equal(newEpoch, a.Welcome!.Epoch);
        Assert.True(a.Welcome.Resume);
        Assert.Equal(1, a.Stats.Resumes);
        Assert.Null(a.Baseline);
        Assert.Null(a.Latest);
        Assert.Null(a.LatestInfo);
        Assert.Null(a.Results);
        Assert.Empty(a.PlayersLeft);
        Assert.Empty(a.DrainFrames());
        var ready = DecodeReady(Assert.Single(sink.Sent));
        Assert.Equal((old.RaceId, newEpoch), (ready.RaceId, ready.Epoch));

        // Duplicated, stale and non-resume welcomes never reset the live round again.
        long wrong = a.Stats.WrongSession;
        a.OnReceive(NetChannel.ReliableOrdered, old.Encode());
        a.OnReceive(NetChannel.ReliableOrdered, (resume with { Resume = false }).Encode());
        a.OnReceive(NetChannel.ReliableOrdered, resume.Encode());
        Assert.Equal(wrong + 3, a.Stats.WrongSession);
        Assert.Equal(1, a.Stats.Resumes);
        Assert.Single(sink.Sent);

        // Full durable state (the server extends the old stream), then the fresh baseline and a step.
        ushort knockedId = 0x2222;
        a.OnReceive(NetChannel.ReliableOrdered, new NetDurable(old.RaceId, newEpoch, durable + 1, 0,
            [new KeyValuePair<ushort, KnockedObject>(knockedId, new KnockedObject(1, 2, 3))], null).Encode());
        Assert.Equal(durable + 1, a.LatestDurableSequence);
        Assert.True(a.DurableKnocked.ContainsKey(knockedId));

        var source = h.Frames[0];
        var baselineInfo = new NetSnapshotInfo(0, RaceViewFrameKind.Initial, source.SimulationTick64, source.Steps,
            NetRacePhase.Racing, true, false, durable + 1, source.NumLaps, source.RacerCount, source.Ranking, 1);
        foreach (var part in SnapshotCodec.EncodeParts(old.RaceId, newEpoch, baselineInfo, default,
                     SnapshotCodec.EncodeCars(source, _ => true)))
            a.OnReceive(NetChannel.ReliableOrdered, part);
        Assert.NotNull(a.Baseline);
        Assert.Same(a.Baseline, a.Latest);
        Assert.Equal(0, a.Baseline!.SnapshotSequence);

        var step = source with { SnapshotSequence = 1, Kind = RaceViewFrameKind.Step, Steps = 1, SimulationTick64 = 2, RaceStarted = true };
        var stepInfo = new NetSnapshotInfo(1, RaceViewFrameKind.Step, 2, 1, NetRacePhase.Racing, true, false, durable + 1,
            step.NumLaps, step.RacerCount, step.Ranking, 1);
        foreach (var part in SnapshotCodec.EncodeParts(old.RaceId, newEpoch, stepInfo, default,
                     SnapshotCodec.EncodeCars(step, _ => false)))
            a.OnReceive(NetChannel.Unreliable, part);
        Assert.Equal(1, a.Latest!.SnapshotSequence);
        Assert.Equal(old.RaceId, a.Latest.RaceId);
        Assert.True(a.Latest.Knocked.ContainsKey(knockedId));

        // Controls continue with the new epoch; a result of the old epoch is never applied twice.
        a.Controls = new PlayerInput(12, 200, 0, 0);
        int sentBefore = sink.Sent.Count;
        a.Update(h.Net.Now);
        Assert.True(sink.Sent.Count > sentBefore);
        var input = DecodeInput(sink.Sent[^1]);
        Assert.Equal((old.RaceId, newEpoch), (input.RaceId, input.Epoch));
        Assert.NotEmpty(input.Samples);
        Assert.Equal(12, input.Samples[^1].Steer);
        Assert.True(a.LastSentSequence > 0);

        a.OnReceive(NetChannel.ReliableOrdered,
            new NetResults(old.RaceId, old.Epoch, NetFinishReason.Finished, 1, 2, 0, new RaceResults([])).Encode());
        Assert.Equal(1, a.Stats.WrongSession - wrong - 3);
        Assert.Null(a.Results);
        Assert.Equal(RaceClientState.Started, a.State);
    }

    [GameFact]
    public void ClientAutomaticallyReconnectsInsideTheGraceWindow()
    {
        var (h, alice, bob) = RaceSetup(106);
        using (h)
        {
            var token = bob.Lobby!.ResumeToken!;
            long oldEpoch = bob.Welcome!.Epoch;
            // The old client already drove with high sequence/action numbers; the new client starts again from 1.
            var round = h.Server.Games.Single().Round!;
            Assert.True(h.RunUntil(() => round.Phase == NetRacePhase.Racing, 10_000_000), $"phase {round.Phase}");
            long target = round.Host.Steps + 2;
            bob.Link.Send(NetChannel.Unreliable, new NetInputPacket(bob.Welcome.RaceId, bob.Welcome.Epoch, 0,
                [new NetInputSample(1000, target, 0, 255, 0, 0)], [new NetAction(1000, NetActionKind.GearUp, target)]).Encode());
            h.Run(300_000);
            bob.Link.Cut();

            // The controller starts before the server has noticed the cut, so its first attempt is refused with NameTaken and
            // the retry (after the transport timeout) resumes the reserved seat.
            var controller = new ReconnectController(SessionHarness.Fingerprint, "bob", token, Grace,
                c =>
                {
                    var link = h.Net.AddClient(c, new LinkProfile(), new LinkProfile());
                    link.Connect(c.Hello());
                    return link;
                }, () => h.Net.Now, retryIntervalMicros: 50_000);
            var client = controller.Start();

            long end = h.Net.Now + 3_000_000;
            while (h.Net.Now < end && !(controller.Resumed && client.State == RaceClientState.Started))
            {
                long t = h.Net.Now + 1000;
                h.Net.AdvanceTo(t);
                h.Server.Update(t);
                client = controller.Poll(t);
                client.Update(t);
            }
            Assert.True(controller.Resumed, string.Join("\n", h.Log.TakeLast(30)));
            Assert.True(controller.Attempts >= 2);
            Assert.Equal(RaceClientState.Started, client.State);
            Assert.True(client.Welcome!.Resume);
            Assert.Equal(bob.Welcome.RaceId, client.Welcome.RaceId);
            Assert.NotEqual(oldEpoch, client.Welcome.Epoch);
            Assert.Same(client, controller.Client);
            Assert.True(h.RunUntil(() => MemberIndex(alice, "bob") >= 0 &&
                alice.Lobby!.Members[MemberIndex(alice, "bob")].Connected, 1_000_000));

            long sequenceBefore = client.LastSentSequence;
            end = h.Net.Now + 2_000_000;
            while (h.Net.Now < end)
            {
                long t = h.Net.Now + 1000;
                h.Net.AdvanceTo(t);
                h.Server.Update(t);
                client = controller.Poll(t);
                client.Update(t);
            }
            Assert.True(client.LastSentSequence > sequenceBefore);
            // The server applies the new client's inputs (its ACK follows the new numbering instead of staying at 1000).
            Assert.InRange(client.LatestAck.AppliedInput, 1, client.LastSentSequence);
            Assert.Null(client.Results);
            Assert.Empty(client.PlayersLeft);
            Assert.Equal(1, h.Server.Stats.Resumed);
        }
    }

    [Fact]
    public void ClientGivesUpWhenTheGraceWindowExpires()
    {
        long now = 0;
        var controller = new ReconnectController(new byte[32], "bob", new byte[NetProtocol.ResumeTokenLength], 200_000,
            c =>
            {
                var transport = new DeadTransport();
                c.OnDisconnected(null);   // the link dies at once; no server answer
                return transport;
            }, () => now, retryIntervalMicros: 50_000);
        var client = controller.Start();
        for (now = 0; now <= 400_000; now += 1000)
        {
            client = controller.Poll(now);
            client.Update(now);
        }
        Assert.True(controller.Expired);
        Assert.False(controller.Stopped);
        Assert.True(controller.Attempts >= 2);
        Assert.Equal(RaceClientState.Rejected, client.State);
    }
    [Fact]
    public void GraceWindowRunsFromTheFirstLossEvenWhenEachAttemptFailsLater()
    {
        // Every attempt sits in Connecting for 150 ms before the transport times out; with a 50 ms retry interval a window
        // restarted by each pending attempt would never end.
        long now = 0;
        var pending = new List<(RaceClient Client, long FailAt)>();
        var controller = new ReconnectController(new byte[32], "bob", new byte[NetProtocol.ResumeTokenLength], 400_000,
            c =>
            {
                pending.Add((c, now + 150_000));
                return new DeadTransport();
            }, () => now, retryIntervalMicros: 50_000);
        var client = controller.Start();
        long expiredAt = -1;
        for (now = 0; now <= 2_000_000 && expiredAt < 0; now += 1000)
        {
            foreach (var p in pending.Where(p => p.FailAt <= now).ToList())
            {
                p.Client.OnDisconnected(null);
                pending.Remove(p);
            }
            client = controller.Poll(now);
            client.Update(now);
            if (controller.Expired) expiredAt = now;
        }
        Assert.True(controller.Expired);
        // first loss at 150 ms, window 400 ms
        Assert.InRange(expiredAt, 550_000, 560_000);
        Assert.InRange(controller.Attempts, 2, 4);
    }

    [Fact]
    public void LocalLeaveStopsTheControllerWithoutAnotherAttempt()
    {
        long now = 0;
        var controller = new ReconnectController(new byte[32], "bob", new byte[NetProtocol.ResumeTokenLength], 1_000_000,
            _ => new DeadTransport(), () => now, retryIntervalMicros: 50_000);
        var client = controller.Start();
        client.Leave();
        for (now = 0; now <= 300_000; now += 1000) client = controller.Poll(now);
        Assert.True(controller.Stopped);
        Assert.False(controller.Expired);
        Assert.Equal(1, controller.Attempts);
    }

    [Fact]
    public void FinalServerAnswerStopsTheController()
    {
        long now = 0;
        var controller = new ReconnectController(new byte[32], "bob", new byte[NetProtocol.ResumeTokenLength], 1_000_000,
            _ => new DeadTransport(), () => now, retryIntervalMicros: 50_000);
        var client = controller.Start();
        client.OnDisconnected(new NetReject(NetRejectReason.SlowClient).Encode());
        for (now = 0; now <= 300_000; now += 1000) client = controller.Poll(now);
        Assert.True(controller.Stopped);
        Assert.Equal(1, controller.Attempts);
    }
}
