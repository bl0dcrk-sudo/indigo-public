using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Knockout series and the straggler/disconnect rules of a round, on the simulated network (fake clock): the server runs
/// the whole series in one game, counts every result once, eliminates the last place, keeps eliminated humans as spectators and
/// treats a disconnected human as DNF and last. (The race stream of eliminated humans is covered by SessionSpectatorTests.) Rounds are ended either by the step limit (the humans stand still) or by a
/// finish fixture that pushes one human car across its lap boundaries (like the natural-finish test of the race server).</summary>
public class SessionKnockoutTests
{
    const long Grace = 60_000_000;

    internal static SessionServerOptions Options(int maxSteps = 40, long grace = Grace, long straggler = 0) => new()
    {
        CountdownMicros = 0,
        GraceMicros = grace,
        MaxRaceSteps = maxSteps,
        StragglerLimitMicros = straggler,
    };

    internal static string Tail(SessionHarness h) => string.Join("\n", h.Log.TakeLast(25));

    internal static SessionHarness Game(int seed, SessionServerOptions options, NetGameMode mode, params string[] names)
    {
        var h = new SessionHarness(seed, options);
        h.Net.DisconnectTimeoutMicros = 200_000;
        var first = h.Join(names[0]);
        Assert.True(h.RunUntil(() => first.List != null, 1_000_000));
        first.Send(new NetGameCreate("game", 4, mode).Encode());
        Assert.True(h.RunUntil(() => first.InLobby, 1_000_000));
        for (int i = 1; i < names.Length; i++)
        {
            var client = h.Join(names[i]);
            Assert.True(h.RunUntil(() => client.List != null, 1_000_000));
            client.JoinGame(first.GameId);
            Assert.True(h.RunUntil(() => client.InLobby && first.Lobby?.Members.Count == i + 1, 1_000_000));
        }
        return h;
    }

    internal static SessionHarness Knockout(int seed, SessionServerOptions options, params string[] names) => Game(seed, options, NetGameMode.Knockout, names);

    /// <summary>Plays one round started by the operator until the game is back in the lobby with one more completed round.</summary>
    internal static void PlayRound(SessionHarness h, SessionTestClient op, Action? fixture = null)
    {
        int rounds = op.Lobby!.Series?.CompletedRounds ?? 0;
        op.RequestStart();
        PlayRoundToTheEnd(h, op, fixture, rounds);
    }

    /// <summary>Runs a round the operator has already started until the game is back in the lobby with one more completed round.</summary>
    internal static void PlayRoundToTheEnd(SessionHarness h, SessionTestClient op, Action? fixture = null, int? completedBefore = null)
    {
        int rounds = completedBefore ?? op.Lobby!.Series?.CompletedRounds ?? 0;
        var game = h.Server.Games.Single();
        int results = op.AllResults.Count;
        Assert.True(h.RunUntil(() =>
        {
            fixture?.Invoke();
            return game.Phase == NetGamePhase.Lobby && op.InLobby && op.Lobby!.Series is { } s && s.CompletedRounds != rounds;
        }, 120_000_000), $"{op.Name}: results {op.AllResults.Count}/{results}, phase {op.Lobby?.Phase}, completed {op.Lobby?.Series?.CompletedRounds}/{rounds}\n{Tail(h)}");
    }

    internal static NetKnockoutEntry Entry(SessionTestClient c, string name) => c.Lobby!.Series!.Entries.Single(e => e.Name == name);

    /// <summary>Pushes the human car of <paramref name="seat"/> over its two lap boundaries once the round has run long enough.</summary>
    internal static Action Finisher(SessionHarness h, int seat)
    {
        bool lap1 = false, lap2 = false;
        return () =>
        {
            var host = h.Server.Games.Single().Round?.Host;
            if (host == null) return;
            var car = host.Runner.HumanSims[seat].Obj;
            if (!lap1 && host.Steps >= 300) { car.Lap = 1; lap1 = true; }
            if (!lap2 && host.Steps >= 340) { car.Lap = 2; lap2 = true; }
        };
    }

    [GameFact]
    public void AKnockoutGameIsListedAndTheSeriesStartsWithNineEntrantsOnTheFirstTrack()
    {
        using var h = Knockout(401, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        Assert.Equal(NetGameMode.Knockout, alice.Lobby!.Mode);
        Assert.Null(alice.Lobby.Series); // nothing played yet
        alice.RequestList();
        Assert.True(h.RunUntil(() => alice.List != null && alice.List.Games.Count == 1, 1_000_000));
        Assert.Equal(NetGameMode.Knockout, alice.List!.Games.Single().Mode);

        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InRace && bob.InRace && alice.Welcome != null && bob.Welcome != null, 10_000_000), Tail(h));
        var series = alice.Lobby!.Series!;
        Assert.Equal(NetProtocol.KnockoutEntrants, series.Entries.Count);
        Assert.Equal(0, series.CompletedRounds);
        Assert.All(series.Entries, e => Assert.True(e.Active));
        Assert.Equal(["alice", "bob"], series.Entries.Take(2).Select(e => e.Name));
        Assert.Equal([0, 1], series.Entries.Take(2).Select(e => e.Slot));
        Assert.All(series.Entries.Skip(2), e => Assert.Equal(-1, e.Slot));
        Assert.Equal(NetProtocol.KnockoutEntrants, alice.Welcome!.CarCount);
        // Everybody gets the same state: the clients only show it.
        Assert.Equal(series.Entries, bob.Lobby!.Series!.Entries);
    }

    [GameFact]
    public void EachRoundEliminatesExactlyOneEntrantAndMovesToTheNextTrackInTheSameGame()
    {
        using var h = Knockout(402, Options(), "alice", "bob");
        var alice = h.Clients[0];
        int gameId = alice.GameId;
        for (int round = 1; round <= 3; round++)
        {
            PlayRound(h, alice);
            var series = alice.Lobby!.Series!;
            Assert.Equal(round, series.CompletedRounds);
            Assert.Equal(9 - round, series.Entries.Count(e => e.Active));
            Assert.Equal(Enumerable.Range(0, round), series.Entries.Where(e => e.EliminatedRound >= 0).Select(e => e.EliminatedRound).Order());
            Assert.Equal(gameId, alice.GameId);
            Assert.Single(h.Server.Games);
            if (series.Finished) break; // both idle humans may be out
            Assert.Equal(round, alice.Lobby.Track); // the track follows the stage
        }
    }

    [GameFact]
    public void AnEliminatedHumanStaysInTheGameAsASpectatorAndTheOthersRaceOn()
    {
        using var h = Knockout(403, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        PlayRound(h, alice);
        var series = alice.Lobby!.Series!;
        var out1 = series.Entries.Where(e => e.Slot >= 0 && !e.Active).ToArray();
        // Standing humans are slow: one of them is the last of the round. If the series is already over there is nothing to watch.
        Assert.Single(out1);
        var spectator = h.Clients.Single(c => c.Name == out1.Single().Name);
        var racer = h.Clients.Single(c => c != spectator);
        Assert.True(spectator.Connected && !spectator.Disconnected);
        Assert.Equal(2, alice.Lobby.Members.Count);
        PlayRound(h, alice); // the operator may be the spectator: it still starts the rounds
        // The spectator is streamed the second round like the racer (a spectator welcome, no seat), see SessionSpectatorTests.
        Assert.Equal(2, spectator.Welcomes.Count);
        Assert.True(spectator.Welcomes[1].Spectator);
        Assert.False(spectator.Welcomes[0].Spectator);
        Assert.Equal(2, racer.Welcomes.Count);
        Assert.False(racer.Welcomes[1].Spectator);
        Assert.True(h.Clients.All(c => c.Connected));
        Assert.Equal(2, alice.Lobby.Series!.CompletedRounds);
        Assert.Equal(NetProtocol.KnockoutEntrants - 1, racer.Welcome!.CarCount);
    }

    [GameFact]
    public void NobodyJoinsAndNothingIsChangedWhileTheSeriesRuns()
    {
        using var h = Knockout(404, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        // Before the first round the laps (not the track) can still be chosen.
        alice.SetOptions(5, 3);
        Assert.True(h.RunUntil(() => alice.Lobby!.Laps == 3, 1_000_000));
        Assert.Equal(0, alice.Lobby!.Track); // the track of a series follows its stage
        PlayRound(h, alice);
        Assert.False(alice.Lobby!.Series!.Finished);
        alice.SetOptions(2, 6);
        h.Run(500_000);
        Assert.Equal(3, alice.Lobby.Laps);
        Assert.Equal(1, alice.Lobby.Track);

        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => carol.List != null, 1_000_000));
        carol.JoinGame(alice.GameId);
        h.Run(1_000_000);
        Assert.Null(carol.Lobby);
        Assert.Equal(2, h.Server.Games.Single().MemberCount);
        Assert.False(bob.Disconnected);
    }

    [GameFact]
    public void TheResultOfARoundIsCountedOnceEvenIfTheClientsKeepAcknowledging()
    {
        using var h = Knockout(405, Options(), "alice", "bob");
        var alice = h.Clients[0];
        PlayRound(h, alice);
        var series = alice.Lobby!.Series!;
        var game = h.Server.Games.Single();
        for (int i = 0; i < 3; i++) { alice.ReturnToLobby(); h.Clients[1].ReturnToLobby(); }
        h.Run(1_000_000);
        Assert.Equal(1, alice.Lobby.Series!.CompletedRounds);
        Assert.Equal(series.Entries, alice.Lobby.Series.Entries);
        Assert.Equal(series.Entries.Count(e => e.Active), game.Series!.Entrants.Count);
    }

    [GameFact]
    public void AHumanWhoseConnectionIsLostForGoodIsDnfLastAndEliminated()
    {
        using var h = Knockout(406, Options(maxSteps: 200, grace: 500_000), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InRace && bob.InRace && h.Server.Games.Single().Round?.Host.Steps > 5, 10_000_000), Tail(h));
        bob.Disconnect();
        Assert.True(h.RunUntil(() => alice.Results != null && alice.InLobby && alice.Lobby!.Series!.CompletedRounds == 1, 60_000_000), Tail(h));
        var entry = Entry(alice, "bob");
        Assert.False(entry.Active);
        Assert.Equal(0, entry.EliminatedRound);
        Assert.Equal(NetProtocol.KnockoutEntrants, entry.Place); // last of the round
        Assert.Equal(1, alice.Lobby!.Series!.Entries.Count(e => e.EliminatedRound == 0)); // only the last place left, bob was it
        // The series goes on without him.
        PlayRound(h, alice);
        Assert.Equal(2, alice.Lobby.Series!.CompletedRounds);
    }

    [GameFact]
    public void AnAwayHumanDoesNotKeepTheRoundWaitingOnceTheOthersFinish()
    {
        using var h = Knockout(407, Options(maxSteps: 0), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var finish = Finisher(h, 0);
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InRace && bob.InRace && h.Server.Games.Single().Round?.Host.Steps > 5, 10_000_000), Tail(h));
        bob.Disconnect();
        Assert.True(h.RunUntil(() =>
        {
            finish();
            return alice.Results != null && alice.InLobby;
        }, 120_000_000), $"results {alice.Results != null} lobby {alice.Lobby?.Phase}\n{Tail(h)}");
        Assert.Equal(NetFinishReason.RemainingHumansFinished, alice.Results!.Reason);
        // The grace window (60 s) is still open: nobody waited for it, the seat is only reserved.
        Assert.Contains(h.Server.Games.Single().Members, m => m.Name == "bob" && !m.Connected);
        var bobEntry = Entry(alice, "bob");
        Assert.False(bobEntry.Active);
        Assert.Equal(NetProtocol.KnockoutEntrants, bobEntry.Place);
        Assert.Equal(1, Entry(alice, "alice").Place);
        Assert.Equal(NetProtocol.KnockoutEntrants, alice.Results.Results.Racers.Single(r => r.CarIndex == 1).Place); // bob drove car 1
    }

    [GameFact]
    public void StragglersGetALimitAfterTheFirstFinisherAndAreRankedBehindTheFinishers()
    {
        using var h = Knockout(408, Options(maxSteps: 0, straggler: 2_000_000), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var finish = Finisher(h, 0);
        alice.RequestStart();
        Assert.True(h.RunUntil(() =>
        {
            finish();
            return alice.Results != null && alice.InLobby;
        }, 120_000_000), $"results {alice.Results != null} lobby {alice.Lobby?.Phase}\n{Tail(h)}");
        Assert.Equal(NetFinishReason.StragglerLimit, alice.Results!.Reason);
        var rows = alice.Results.Results.Racers;
        Assert.Equal(Enumerable.Range(1, NetProtocol.KnockoutEntrants), rows.Select(r => r.Place).Order());
        var finished = rows.Where(r => r.RaceState == 2).ToArray();
        Assert.NotEmpty(finished);
        Assert.Equal(Enumerable.Range(1, finished.Length), finished.Select(r => r.Place).Order());   // finishers first ...
        Assert.All(rows.Where(r => r.RaceState != 2), r => Assert.True(r.Place > finished.Length));   // ... then the DNF
        Assert.Equal(1, Entry(alice, "alice").Place);
        // The countdown was visible to the clients while it ran and is gone with the round.
        Assert.Contains(bob.States, s => s.Phase == NetGamePhase.Race && s.CutoffMillis is > 0 and <= 2000);
        Assert.Equal(0, bob.Lobby!.CutoffMillis);
        // The straggler (bob, standing still) is the last of the round and out of the series; he is still connected.
        Assert.True(bob.Connected);
        Assert.False(Entry(alice, "bob").Active);
    }

    [GameFact]
    public void TheStragglerLimitAppliesToASingleRaceToo()
    {
        using var h = Game(409, Options(maxSteps: 0, straggler: 2_000_000), NetGameMode.SingleRace, "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var finish = Finisher(h, 0);
        alice.RequestStart();
        Assert.True(h.RunUntil(() =>
        {
            finish();
            return alice.Results != null && bob.Results != null && alice.InLobby;
        }, 120_000_000), Tail(h));
        Assert.Equal(NetFinishReason.StragglerLimit, alice.Results!.Reason);
        Assert.Null(alice.Lobby!.Series);
        var rows = alice.Results.Results.Racers;
        Assert.Equal(1, rows.Single(r => r.CarIndex == 0).Place);
        Assert.Equal(2, rows.Single(r => r.CarIndex == 0).RaceState);
        Assert.True(rows.Single(r => r.CarIndex == 1).Place > 1);
        Assert.Equal(bob.Results!.Results.Racers.Select(r => r.Place), alice.Results.Results.Racers.Select(r => r.Place));
    }

    [GameFact]
    public void AFinishedSeriesIsFollowedByANewOneOnTheNextStart()
    {
        using var h = Knockout(410, Options(), "alice", "bob");
        var alice = h.Clients[0];
        // Standing humans end up last: the series is over as soon as both are out (at the latest after the eighth round).
        for (int i = 0; i < 8 && alice.Lobby!.Series is not { Finished: true }; i++) PlayRound(h, alice);
        Assert.True(alice.Lobby!.Series!.Finished);
        int welcomes = alice.Welcomes.Count;
        PlayRound(h, alice);
        var next = alice.Lobby!.Series!;
        Assert.Equal(1, next.CompletedRounds); // a brand new series: round 1 is done
        Assert.Equal(8, next.Entries.Count(e => e.Active));
        Assert.Equal(welcomes + 1, alice.Welcomes.Count);
        Assert.Equal(NetProtocol.KnockoutEntrants, alice.Welcome!.CarCount); // nine entrants again
        Assert.False(next.Finished);
    }
}
