using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Lifecycle rules of a game round (review of network-game-sessions 6.4): a member that is gone when a round starts
/// never blocks the others, a round that ends without a result returns the game to the lobby, a member that leaves the race
/// core only leaves the game (its connection and everybody else's stay), and a player is a member of one game only. All on
/// the simulated network with a fake clock.</summary>
public class SessionLifecycleTests
{
    const long Grace = 60_000_000;

    static SessionServerOptions Options(long countdown = 0, long grace = Grace, int maxSteps = 30, long raceWait = 120_000_000) => new()
    {
        CountdownMicros = countdown,
        GraceMicros = grace,
        MaxRaceSteps = maxSteps,
        RaceWaitTimeoutMicros = raceWait,
    };

    static SessionHarness Lobby(int seed, SessionServerOptions options, params string[] names)
    {
        var h = new SessionHarness(seed, options);
        h.Net.DisconnectTimeoutMicros = 200_000;
        var first = h.Join(names[0]);
        Assert.True(h.RunUntil(() => first.List != null, 1_000_000));
        first.Create("game", 4, 1, 1);
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

    static SessionHarness Race(int seed, SessionServerOptions options, params string[] names)
    {
        var h = Lobby(seed, options, names);
        h.Clients[0].RequestStart();
        Assert.True(h.RunUntil(() => h.Clients.All(c => c.InRace && c.Welcome != null), 10_000_000), string.Join("\n", h.Log.TakeLast(20)));
        return h;
    }

    static string Tail(SessionHarness h) => string.Join("\n", h.Log.TakeLast(25));

    static void LeaveGame(SessionHarness h, SessionTestClient client)
    {
        int lists = client.Lists.Count;
        client.LeaveGame();
        Assert.True(h.RunUntil(() => client.Lists.Count > lists, 1_000_000), Tail(h));
    }

    static int HumanCars(NetResults results) => results.Results.Racers.Count(r => r.Human);

    // ---------------- a member that is gone when the round starts ----------------

    [GameFact]
    public void AMemberWhoLeftTheCountdownAndDidNotReturnDoesNotBlockTheRaceOfTheOthers()
    {
        using var h = Lobby(301, Options(countdown: 5_000_000), "alice", "bob", "carol");
        var (alice, bob, carol) = (h.Clients[0], h.Clients[1], h.Clients[2]);
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => carol.InCountdown, 1_000_000));
        LeaveGame(h, bob);

        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Race, 10_000_000), Tail(h));
        Assert.Equal(2, game.Round!.MemberCount);
        Assert.Equal(2, game.MemberCount);
        Assert.True(h.RunUntil(() => alice.Results != null && carol.Results != null && game.Phase == NetGamePhase.Lobby, 60_000_000), Tail(h));
        Assert.Equal(2, HumanCars(alice.Results!));
        Assert.Equal(alice.Results!.RaceId, carol.Results!.RaceId);
        Assert.True(h.RunUntil(() => alice.InLobby && carol.InLobby, 1_000_000));
        Assert.Null(bob.Results);
        Assert.False(alice.Disconnected || bob.Disconnected || carol.Disconnected);
        Assert.Equal(3, h.Server.ConnectionCount);
        Assert.Equal(0, h.Server.Stats.Disconnects);
    }

    [GameFact]
    public void AMemberWhoseLinkDiedInTheCountdownDoesNotBlockTheRaceEither()
    {
        using var h = Lobby(302, Options(countdown: 5_000_000), "alice", "bob", "carol");
        var (alice, bob, carol) = (h.Clients[0], h.Clients[1], h.Clients[2]);
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => carol.InCountdown, 1_000_000));
        bob.Link.Cut();
        Assert.True(h.RunUntil(() => game.Members.Any(m => m.Name == "bob" && !m.Connected), 2_000_000));

        Assert.True(h.RunUntil(() => alice.Results != null && carol.Results != null && game.Phase == NetGamePhase.Lobby, 60_000_000), Tail(h));
        Assert.Equal(2, HumanCars(alice.Results!));
        Assert.Equal(2, game.MemberCount);
        Assert.True(h.RunUntil(() => alice.InLobby && carol.InLobby, 1_000_000));
        Assert.Equal(2, h.Server.ConnectionCount);
    }

    [GameFact]
    public void AReservationThatSurvivedTheReturnToTheLobbyDoesNotBlockTheNextStart()
    {
        using var h = Race(303, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var game = h.Server.Games.Single();
        LeaveGame(h, bob);
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby && alice.Results != null, 30_000_000), Tail(h));
        Assert.Contains(game.Members, m => m.Name == "bob" && !m.Connected);   // still reserved in the lobby

        int results = alice.AllResults.Count;
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.AllResults.Count > results && game.Phase == NetGamePhase.Lobby, 60_000_000), Tail(h));
        Assert.Equal(1, game.MemberCount);
        Assert.Equal(1, HumanCars(alice.AllResults[^1]));
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        Assert.False(alice.Disconnected);
    }

    [GameFact]
    public void AMemberWhoLostItsLinkWhileLoadingIsDroppedWhenItsWindowEndsAndTheRoundIsRecomposedForTheOthers()
    {
        using var h = Lobby(304, Options(grace: 1_000_000), "alice", "bob", "carol");
        var (alice, bob, carol) = (h.Clients[0], h.Clients[1], h.Clients[2]);
        var game = h.Server.Games.Single();
        bob.AutoReady = false;   // never finishes loading
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Clients.All(c => c.InRace && c.Welcome != null), 10_000_000), Tail(h));
        int firstRace = alice.Welcome!.RaceId;
        Assert.Equal(NetRacePhase.Loading, game.Round!.Phase);
        bob.Link.Cut();

        Assert.True(h.RunUntil(() => alice.AllResults.Count > 0 && carol.AllResults.Count > 0, 60_000_000), Tail(h));
        Assert.Equal(2, alice.Welcomes.Count);
        Assert.NotEqual(firstRace, alice.Welcomes[1].RaceId);
        Assert.Equal(alice.Welcomes[1].RaceId, alice.Results!.RaceId);
        Assert.Equal(2, HumanCars(alice.Results));
        Assert.Equal(2, game.MemberCount);
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby && carol.InLobby, 1_000_000));
        Assert.Equal(2, h.Server.ConnectionCount);
    }

    // ---------------- a round that ends without a result ----------------

    [GameFact]
    public void ARoundClosedWithoutAResultReturnsTheGameToTheLobbyAndKeepsTheConnections()
    {
        using var h = Lobby(305, Options(raceWait: 2_000_000), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var game = h.Server.Games.Single();
        alice.AutoReady = bob.AutoReady = false;   // nobody ever reports ready: the round's wait times out
        alice.RequestStart();
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Race && alice.InRace, 1_000_000), Tail(h));

        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby && bob.InLobby, 10_000_000), Tail(h));
        Assert.Null(game.Round);
        Assert.Null(alice.Results);
        Assert.Null(bob.Results);
        h.Run(500_000);
        Assert.False(alice.Disconnected || bob.Disconnected);
        Assert.Equal(2, h.Server.ConnectionCount);
        Assert.Equal(0, h.Server.Stats.Disconnects);
        Assert.Equal(2, game.MemberCount);

        // The game is usable again: the next start (with clients that do load) reaches a result.
        alice.AutoReady = bob.AutoReady = true;
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.Results != null && bob.Results != null && game.Phase == NetGamePhase.Lobby, 60_000_000), Tail(h));
    }

    // ---------------- the round core ends a member ----------------

    [GameFact]
    public void ALeaveSentToTheRaceCoreTakesThePlayerBackToTheGamesListWithItsConnection()
    {
        using var h = Race(306, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var game = h.Server.Games.Single();
        int lists = bob.Lists.Count;
        long wrong = h.Server.Stats.WrongSession;

        bob.Send(NetLeave.Encode(bob.Welcome!.RaceId, bob.Welcome.Epoch));
        Assert.True(h.RunUntil(() => bob.Lists.Count > lists, 1_000_000), Tail(h));
        h.Run(300_000);
        Assert.False(bob.Disconnected);
        Assert.Equal(2, h.Server.ConnectionCount);
        Assert.Equal(0, h.Server.Stats.Disconnects);
        Assert.Single(game.Members);
        Assert.Equal("alice", game.Members[0].Name);
        Assert.Single(bob.List!.Games);

        // The client's own game-level leave that follows a race-level one (as the desktop does) is harmless.
        bob.LeaveGame();
        h.Run(300_000);
        Assert.False(bob.Disconnected);
        Assert.Equal(wrong, h.Server.Stats.WrongSession);

        // It is on the list and may create another game; the others' race goes on to its result.
        bob.Create("second", 4, 1, 1);
        Assert.True(h.RunUntil(() => bob.InLobby && bob.Lobby!.GameName == "second", 1_000_000), Tail(h));
        Assert.True(h.RunUntil(() => alice.Results != null, 60_000_000), Tail(h));
        Assert.False(alice.Disconnected);
    }

    [GameFact]
    public void ASetupMismatchOfTheReadyOnlyRemovesTheMemberFromTheGame()
    {
        using var h = Lobby(307, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var game = h.Server.Games.Single();
        bob.AutoReady = false;
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InRace && bob.InRace && bob.Welcome != null, 10_000_000), Tail(h));
        int lists = bob.Lists.Count;

        bob.Send(new NetReady(bob.Welcome!.RaceId, bob.Welcome.Epoch, new byte[32]).Encode());
        Assert.True(h.RunUntil(() => bob.Lists.Count > lists, 1_000_000), Tail(h));
        h.Run(300_000);
        Assert.False(bob.Disconnected);
        Assert.Equal(2, h.Server.ConnectionCount);
        Assert.Equal(1, game.MemberCount);

        // The round of the remaining player is recomposed and ends with a result.
        Assert.True(h.RunUntil(() => alice.AllResults.Count > 0 && game.Phase == NetGamePhase.Lobby, 60_000_000), Tail(h));
        Assert.Equal(1, HumanCars(alice.Results!));
        Assert.False(alice.Disconnected);
    }

    [GameFact]
    public void AMisbehavingMemberStillLosesItsConnection()
    {
        using var h = Race(308, Options(), "alice", "bob");
        var bob = h.Clients[1];
        // More wrong-session messages than the tolerated bad ones: the connection is closed (Malformed).
        for (int i = 0; i < 80 && !bob.Disconnected; i++)
        {
            bob.Send(NetLeave.Encode(bob.Welcome!.RaceId, bob.Welcome.Epoch + 7));
            h.Run(2_000);
        }
        Assert.True(h.RunUntil(() => bob.Disconnected, 2_000_000), Tail(h));
        Assert.Equal(NetRejectReason.Malformed, bob.Reject?.Reason);
        Assert.False(h.Clients[0].Disconnected);
    }

    [GameFact]
    public void ControlsStillInFlightWhenTheRoundEndedAreIgnoredNotCountedAsForeign()
    {
        using var h = Race(311, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var game = h.Server.Games.Single();
        var welcome = bob.Welcome!;
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && bob.InLobby && bob.Results != null, 30_000_000), Tail(h));
        long wrong = h.Server.Stats.WrongSession;

        bob.Send(new NetInputPacket(welcome.RaceId, welcome.Epoch, 0, [new NetInputSample(1, 5, 0, 255, 0, 0)], []).Encode());
        h.Run(300_000);
        Assert.Equal(wrong, h.Server.Stats.WrongSession);
        Assert.False(bob.Disconnected);

        // Anything else that carries a race header after the round is a foreign message as before.
        bob.Send(NetLeave.Encode(welcome.RaceId, welcome.Epoch));
        h.Run(300_000);
        Assert.Equal(wrong + 1, h.Server.Stats.WrongSession);
    }

    // ---------------- one game per player ----------------

    [GameFact]
    public void CreatingAnotherGameEndsTheReservationInTheRunningOne()
    {
        using var h = Race(309, Options(maxSteps: 600), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var running = h.Server.Games.Single();
        Assert.True(h.RunUntil(() => running.Round!.Phase == NetRacePhase.Racing, 10_000_000));
        LeaveGame(h, bob);
        Assert.True(h.RunUntil(() => running.Members.Any(m => m.Name == "bob" && !m.Connected), 1_000_000));

        bob.Create("second", 4, 1, 1);
        Assert.True(h.RunUntil(() => bob.InLobby && bob.Lobby!.GameName == "second", 1_000_000), Tail(h));
        Assert.Single(running.Members);
        Assert.Equal(1, running.Round!.MemberCount);
        Assert.True(h.RunUntil(() => alice.PlayerLefts.Count > 0, 1_000_000), Tail(h));
        Assert.True(h.RunUntil(() => alice.Results != null, 60_000_000), Tail(h));
        Assert.Equal(2, HumanCars(alice.Results!));   // the car of the former member stays in the result as DNF
        Assert.Equal(0, h.Server.Stats.Resumed);
    }

    [GameFact]
    public void JoiningAnotherGameEndsTheReservationInTheRunningOne()
    {
        using var h = Race(310, Options(), "alice", "bob");
        var (alice, bob) = (h.Clients[0], h.Clients[1]);
        var running = h.Server.Games.Single();
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => carol.List != null, 1_000_000));
        carol.Create("other", 4, 1, 1);
        Assert.True(h.RunUntil(() => carol.InLobby, 1_000_000));
        var other = h.Server.Games.Single(g => g.Name == "other");
        LeaveGame(h, bob);
        Assert.True(h.RunUntil(() => running.Members.Any(m => m.Name == "bob" && !m.Connected), 1_000_000));

        bob.JoinGame(other.GameId);
        Assert.True(h.RunUntil(() => bob.InLobby && bob.GameId == other.GameId, 1_000_000), Tail(h));
        Assert.Single(running.Members);
        Assert.Equal(2, other.MemberCount);
        Assert.Equal(0, h.Server.Stats.Resumed);

        // A new connection of that name can no longer be resumed into the old game: bob is connected elsewhere.
        var again = h.Join("bob");
        Assert.True(h.RunUntil(() => again.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, again.Reject?.Reason);
        Assert.False(alice.Disconnected);
    }
}
