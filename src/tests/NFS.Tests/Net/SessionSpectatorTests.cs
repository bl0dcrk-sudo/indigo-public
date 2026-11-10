using NFS.Core;
using NFS.Game.Race;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Spectators of a knockout round (protocol v13): an eliminated human stays in the game and receives the same welcome,
/// baseline, snapshot, event, durable, start and result stream as the racers, but has no seat, sends no controls and never changes
/// the simulation, the result, the "everybody is home" rule or the straggler countdown.</summary>
public class SessionSpectatorTests
{
    static readonly byte[] Fp = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    static NetWelcome Welcome(int seat = 0, bool spectator = false) => new(7, 9, seat, new[] { 0, 1 }, 2, 0, new[] { 0, 1 },
        new[] { 100, 100 }, true, 2, NetProtocol.StepMicros, NetProtocol.MaxFutureSteps, NetProtocol.HoldSteps, NetProtocol.ActionExpirySteps,
        NetProtocol.InputRedundancy, NetProtocol.MaxDatagram, new RaceSetup().Block, Fp, Fp, Spectator: spectator);

    [Fact]
    public void TheSpectatorFlagOfTheWelcomeSurvivesTheWireAndDefaultsToARacer()
    {
        Assert.Equal(19, NetProtocol.Version);
        Assert.False(NetWelcome.Decode(Welcome().Encode()).Spectator);
        var back = NetWelcome.Decode(Welcome(seat: 1, spectator: true).Encode());
        Assert.True(back.Spectator);
        Assert.Equal(1, back.Seat);
        Assert.Equal(1, back.CarId); // the car the spectator's view starts on
        Assert.False(NetWelcome.Decode((Welcome() with { Resume = true }).Encode()).Spectator);
        Assert.True(NetWelcome.Decode((Welcome(spectator: true) with { Resume = true }).Encode()).Resume);
    }

    [Fact]
    public void ASpectatorWelcomeStillNeedsAValidSeatToStartOn()
    {
        Assert.Throws<NetProtocolException>(() => NetWelcome.Decode(Welcome(seat: 2, spectator: true).Encode()));
    }

    /// <summary>Plays the first round of a two-human knockout game and returns the harness with the eliminated human and the one still in.</summary>
    static (SessionHarness H, SessionTestClient Spectator, SessionTestClient Racer) AfterRoundOne(int seed, SessionServerOptions options,
        Action? roundOneFixture = null)
    {
        var h = SessionKnockoutTests.Knockout(seed, options, "alice", "bob");
        var alice = h.Clients[0];
        SessionKnockoutTests.PlayRound(h, alice, roundOneFixture);
        var series = alice.Lobby!.Series!;
        var out1 = series.Entries.Single(e => e.Slot >= 0 && !e.Active);
        var spectator = h.Clients.Single(c => c.Name == out1.Name);
        return (h, spectator, h.Clients.Single(c => c != spectator));
    }

    static string Digest(NetResults r) => $"{r.Reason}@{r.Step}/{r.SimulationTick64}/dnf{r.DnfSeats}:" +
        string.Join(",", r.Results.Racers.Select(x => $"{x.Place}.{x.CarIndex}.{x.RaceTicks}.{x.RaceState}"));

    [GameFact]
    public void AnEliminatedHumanReceivesTheWholeRaceStreamAndTheIdenticalResult()
    {
        var (h, spectator, racer) = AfterRoundOne(501, SessionKnockoutTests.Options());
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => spectator.Start != null && racer.Start != null && spectator.Welcomes.Count == 2 && game.Round != null, 10_000_000),
            SessionKnockoutTests.Tail(h));
        // One human car in the round, two connections in it: the spectator has no seat of its own.
        Assert.Equal(1, game.Round!.Host.Seats);
        Assert.Equal(2, game.Round.MemberCount);
        var sw = spectator.Welcomes[1];
        var rw = racer.Welcomes[1];
        Assert.True(sw.Spectator);
        Assert.False(rw.Spectator);
        Assert.Equal(rw.RaceId, sw.RaceId);
        Assert.NotEqual(rw.Epoch, sw.Epoch);
        Assert.Equal(rw.SeatCarIds, sw.SeatCarIds);
        Assert.Equal(rw.SetupHash, sw.SetupHash);
        Assert.InRange(sw.Seat, 0, sw.SeatCarIds.Count - 1);

        SessionKnockoutTests.PlayRoundToTheEnd(h, alice);
        Assert.InRange(spectator.SnapshotParts, 30, int.MaxValue);
        Assert.Equal(racer.SnapshotParts, spectator.SnapshotParts);
        Assert.Equal(racer.DurableMessages, spectator.DurableMessages);
        Assert.Equal(racer.EventMessages, spectator.EventMessages);
        Assert.NotNull(spectator.Results);
        Assert.Equal(Digest(racer.Results!), Digest(spectator.Results!));
        Assert.Equal(sw.RaceId, spectator.Results!.RaceId);
        Assert.Equal(0, h.Server.Stats.Malformed);
        Assert.Equal(0, h.Server.Stats.WrongSession);
        Assert.Equal(0, spectator.Malformed);
        Assert.True(spectator.Connected && racer.Connected);
    }

    [GameFact]
    public void ControlsFromASpectatorAreIgnoredAndNeverCounted()
    {
        var (h, spectator, racer) = AfterRoundOne(502, SessionKnockoutTests.Options());
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => spectator.Start != null && game.Round?.Host.Steps > 3, 10_000_000), SessionKnockoutTests.Tail(h));
        var w = spectator.Welcomes[1];
        for (int i = 0; i < 5; i++)
        {
            spectator.Link.Send(NetChannel.Unreliable, new NetInputPacket(w.RaceId, w.Epoch, 0,
                [new NetInputSample(i + 1, game.Round!.Host.Steps + 4 + i, 0, 255, 0, 0)], []).Encode());
            h.Run(40_000);
        }
        Assert.Equal(0, game.Round!.Stats.InputPackets);
        SessionKnockoutTests.PlayRoundToTheEnd(h, alice);
        Assert.Equal(0, h.Server.Stats.Malformed);
        Assert.Equal(0, h.Server.Stats.WrongSession);
        Assert.Equal(Digest(racer.Results!), Digest(spectator.Results!));
    }

    [GameFact]
    public void ASpectatorIsNeitherWaitedForNorCountedAsAStragglerOrAFinisher()
    {
        // Round one: alice finishes, bob stands and is out after the straggler limit. Round two: alice drives alone and finishes while the
        // spectator never reports ready; the round starts without it and ends with the natural result (the finish delay is shorter than
        // the straggler limit), nobody waits for the spectator.
        var options = SessionKnockoutTests.Options(maxSteps: 0, straggler: 8_000_000);
        var h = SessionKnockoutTests.Knockout(503, options, "alice", "bob");
        using var _ = h;
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        SessionKnockoutTests.PlayRound(h, alice, SessionKnockoutTests.Finisher(h, 0));
        Assert.False(SessionKnockoutTests.Entry(alice, "bob").Active);
        Assert.True(SessionKnockoutTests.Entry(alice, "alice").Active);
        bob.AutoReady = false;
        alice.RequestStart();
        var finish = SessionKnockoutTests.Finisher(h, 0);
        Assert.True(h.RunUntil(() =>
        {
            finish();
            return alice.Results != null && alice.AllResults.Count == 2 && alice.InLobby;
        }, 120_000_000), SessionKnockoutTests.Tail(h));
        Assert.Equal(NetFinishReason.Finished, alice.AllResults[1].Reason);
        Assert.Equal(2, bob.Welcomes.Count);
        Assert.True(bob.Welcomes[1].Spectator);
        Assert.Equal(2, bob.AllResults.Count);
        Assert.Equal(Digest(alice.AllResults[1]), Digest(bob.AllResults[1]));
        Assert.InRange(bob.SnapshotParts, 1, int.MaxValue);
    }

    [GameFact]
    public void ASpectatorThatLeavesBeforeTheCountdownDoesNotRecomposeTheRound()
    {
        var (h, spectator, racer) = AfterRoundOne(504, SessionKnockoutTests.Options());
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        racer.AutoReady = false;
        spectator.AutoReady = false;
        alice.RequestStart();
        Assert.True(h.RunUntil(() => spectator.Welcomes.Count == 2 && racer.Welcomes.Count == 2, 10_000_000), SessionKnockoutTests.Tail(h));
        int raceId = racer.Welcome!.RaceId;
        spectator.Disconnect();
        h.Run(500_000);
        Assert.Equal(2, racer.Welcomes.Count); // no new welcome: the round was not recomposed for the remaining racer
        Assert.Equal(raceId, game.Round!.RaceId);
        Assert.True(game.Round.Phase < NetRacePhase.Countdown);
        racer.Send(new NetReady(raceId, racer.Welcome.Epoch, racer.Welcome.SetupHash).Encode());
        Assert.True(h.RunUntil(() => racer.Results != null && racer.AllResults.Count == 2 && racer.InLobby, 60_000_000), SessionKnockoutTests.Tail(h));
        Assert.Equal(NetFinishReason.TimeLimit, racer.Results!.Reason);
        Assert.Equal(raceId, racer.Results.RaceId);
    }

    [GameFact]
    public void ASpectatorLostMidRaceChangesNothingAndMayRejoinTheStream()
    {
        var (h, spectator, racer) = AfterRoundOne(505, SessionKnockoutTests.Options(maxSteps: 200));
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => spectator.Start != null && game.Round?.Host.Steps > 20, 20_000_000), SessionKnockoutTests.Tail(h));
        spectator.LeaveGame();
        Assert.True(h.RunUntil(() => spectator.List != null && game.Members.Any(m => m.Name == spectator.Name && !m.Connected), 2_000_000));
        Assert.Equal(NetGamePhase.Race, game.Phase); // the round goes on without it
        spectator.JoinGame(game.GameId);
        Assert.True(h.RunUntil(() => spectator.Welcomes.Count == 3 && spectator.Lobby?.Phase == NetGamePhase.Race, 3_000_000), SessionKnockoutTests.Tail(h));
        Assert.True(spectator.Welcome!.Resume);
        Assert.True(spectator.Welcome.Spectator);
        Assert.Equal(spectator.Welcomes[1].RaceId, spectator.Welcome.RaceId);
        Assert.NotEqual(spectator.Welcomes[1].Epoch, spectator.Welcome.Epoch);
        Assert.True(h.RunUntil(() => spectator.Results != null && spectator.AllResults.Count == 2 && racer.AllResults.Count == 2 && racer.InLobby, 60_000_000),
            SessionKnockoutTests.Tail(h));
        Assert.InRange(spectator.SnapshotParts, 1, int.MaxValue);
        Assert.Equal(Digest(racer.Results!), Digest(spectator.Results!));
        Assert.Equal(0, h.Server.Stats.Malformed);
        Assert.Equal(0, h.Server.Stats.WrongSession);
    }

    [GameFact]
    public void AnEliminatedPlayerInGraceAtTheRoundStartStaysAWatcherAndResumesAsSpectator()
    {
        var (h, spectator, racer) = AfterRoundOne(507, SessionKnockoutTests.Options(maxSteps: 200) with { CountdownMicros = 3_000_000 });
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        int welcomesBefore = spectator.Welcomes.Count;
        alice.RequestStart();
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Countdown, 2_000_000), SessionKnockoutTests.Tail(h));
        spectator.LeaveGame();   // lost inside the countdown: the seat is reserved for the grace window
        Assert.True(h.RunUntil(() => game.Members.Any(m => m.Name == spectator.Name && !m.Connected), 2_000_000), SessionKnockoutTests.Tail(h));
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Race && game.Round != null, 20_000_000), SessionKnockoutTests.Tail(h));
        // The round start does not drop the eliminated player: it is still a member (away), not a racer.
        Assert.Contains(game.Members, m => m.Name == spectator.Name && !m.Connected);
        int raceId = game.Round!.RaceId;
        spectator.JoinGame(game.GameId);
        Assert.True(h.RunUntil(() => spectator.Welcomes.Count == welcomesBefore + 1 && spectator.Welcome!.Resume, 5_000_000), SessionKnockoutTests.Tail(h));
        Assert.True(spectator.Welcome!.Spectator);
        Assert.Equal(raceId, spectator.Welcome.RaceId);
        Assert.True(h.RunUntil(() => spectator.AllResults.Count == 2 && racer.AllResults.Count == 2 && racer.InLobby, 60_000_000), SessionKnockoutTests.Tail(h));
        Assert.Equal(Digest(racer.Results!), Digest(spectator.Results!));
        Assert.Equal(0, h.Server.Stats.Malformed);
    }

    [GameFact]
    public void AnEliminatedPlayerWhoLeavesTheLobbyBetweenRoundsKeepsTheSeatAndComesBackAsAWatcher()
    {
        var (h, spectator, racer) = AfterRoundOne(509, SessionKnockoutTests.Options(maxSteps: 200));
        using var _ = h;
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        Assert.Equal(NetGamePhase.Lobby, game.Phase);
        spectator.LeaveGame();   // between the rounds, in the lobby
        Assert.True(h.RunUntil(() => spectator.List != null && game.Members.Any(m => m.Name == spectator.Name && !m.Connected), 2_000_000), SessionKnockoutTests.Tail(h));
        spectator.JoinGame(game.GameId);   // the series is running, yet the eliminated member resumes its reserved seat
        Assert.True(h.RunUntil(() => game.Members.Any(m => m.Name == spectator.Name && m.Connected) && spectator.Lobby != null, 3_000_000), SessionKnockoutTests.Tail(h));
        int welcomes = spectator.Welcomes.Count;
        h.Clients.Single(c => c.Name == game.Operator!.Name).RequestStart(); // the operator role is not restored to a returning member
        Assert.True(h.RunUntil(() => spectator.Welcomes.Count == welcomes + 1 && racer.Welcomes.Count >= 2, 10_000_000), SessionKnockoutTests.Tail(h));
        Assert.True(spectator.Welcome!.Spectator);
        Assert.False(racer.Welcome!.Spectator);
    }

    [GameFact]
    public void AnOutsiderCannotTakeTheSeatOfAnEliminatedPlayerWhoLeftTheLobby()
    {
        var (h, spectator, _) = AfterRoundOne(510, SessionKnockoutTests.Options(maxSteps: 200));
        using var _ = h;
        var game = h.Server.Games.Single();
        spectator.LeaveGame();
        Assert.True(h.RunUntil(() => spectator.List != null && game.Members.Any(m => m.Name == spectator.Name && !m.Connected), 2_000_000), SessionKnockoutTests.Tail(h));
        Assert.Equal(2, game.MemberCount); // still reserved, the series is not opened to newcomers
    }

    [GameFact]
    public void ASingleRaceLobbyStillFreesTheSeatAtOnce()
    {
        using var h = SessionKnockoutTests.Game(511, SessionKnockoutTests.Options(), NetGameMode.SingleRace, "alice", "bob");
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        bob.LeaveGame();
        Assert.True(h.RunUntil(() => game.MemberCount == 1, 2_000_000), SessionKnockoutTests.Tail(h));
    }

    [GameFact]
    public void ASingleRaceHasNoSpectators()
    {
        using var h = SessionKnockoutTests.Game(506, SessionKnockoutTests.Options(), NetGameMode.SingleRace, "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.Results != null && bob.Results != null && alice.InLobby, 60_000_000), SessionKnockoutTests.Tail(h));
        Assert.All(h.Clients, c => Assert.All(c.Welcomes, w => Assert.False(w.Spectator)));
    }
}
