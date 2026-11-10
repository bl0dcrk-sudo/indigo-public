using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Lobby of one game: roster order and operator, readiness, options and car selection, the start countdown (frozen
/// options, blocked joins, car changes allowed), the composed race and the return to the lobby.</summary>
public class GameLobbyTests
{
    static SessionHarness Lobby(int seed, SessionServerOptions? options = null, int maxPlayers = 3, int track = 1, int laps = 2,
        params string[] names)
    {
        var h = new SessionHarness(seed, options);
        Assert.True(names.Length > 0);
        var first = h.Join(names[0]);
        Assert.True(h.RunUntil(() => first.List != null, 1_000_000));
        first.Create("game", maxPlayers, track, laps);
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

    [GameFact]
    public void RosterIsOrderedByJoin_FirstMemberIsTheOperator()
    {
        using var h = Lobby(30, names: ["alice", "bob", "carol"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var carol = h.Clients[2];

        Assert.Equal(["alice", "bob", "carol"], alice.Lobby!.Members.Select(m => m.Name));
        Assert.Equal([0, 1, 2], alice.Lobby.Members.Select(m => m.Slot));
        Assert.True(alice.IsOperator);
        Assert.False(bob.IsOperator);
        Assert.False(carol.IsOperator);
        Assert.Single(alice.Lobby.Members, m => m.Operator);
        Assert.Equal(NetGamePhase.Lobby, alice.Lobby.Phase);
        Assert.Equal(1, alice.Lobby.Track);
        Assert.Equal(2, alice.Lobby.Laps);
        Assert.Equal(3, alice.Lobby.MaxPlayers);
        Assert.Equal(NetGameMode.SingleRace, alice.Lobby.Mode);
        Assert.Equal(0, alice.Lobby.CountdownMillis);
    }

    [GameFact]
    public void ReadinessIsPerConnection_AndOnlyTheOperatorChangesOptions()
    {
        using var h = Lobby(31, names: ["alice", "bob", "carol"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var carol = h.Clients[2];

        bob.SetReady(true);
        Assert.True(h.RunUntil(() => carol.Lobby!.Members.First(m => m.Name == "bob").Ready, 1_000_000));
        Assert.True(bob.Ready);
        Assert.False(alice.Ready);
        Assert.False(carol.Ready);

        long refused = h.Server.Stats.RefusedLobby;
        bob.SetOptions(5, 3);
        Assert.True(h.RunUntil(() => h.Server.Stats.RefusedLobby > refused, 1_000_000));
        Assert.Equal(1, carol.Lobby!.Track);
        Assert.Equal(2, carol.Lobby.Laps);

        alice.SetOptions(5, 3);
        Assert.True(h.RunUntil(() => carol.Lobby!.Track == 5 && carol.Lobby.Laps == 3, 1_000_000));

        bob.SetCar(7);
        Assert.True(h.RunUntil(() => carol.Lobby!.Members.First(m => m.Name == "bob").CarType == 7, 1_000_000));
        Assert.Equal(NetProtocol.NoCarType, carol.Lobby.Members.First(m => m.Name == "alice").CarType);
    }

    [GameFact]
    public void OperatorCancelsTheCountdown_EveryoneIsBackInTheLobbyWithReadyFlagsKept_OthersAreRefused()
    {
        using var h = Lobby(33, maxPlayers: 4, names: ["alice", "bob"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];

        long refused = h.Server.Stats.RefusedLobby;
        alice.CancelStart(); // nothing to cancel in the lobby
        Assert.True(h.RunUntil(() => h.Server.Stats.RefusedLobby > refused, 1_000_000));

        bob.SetReady(true);
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.First(m => m.Name == "bob").Ready, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => alice.InCountdown && bob.InCountdown, 1_000_000));

        refused = h.Server.Stats.RefusedLobby;
        bob.CancelStart(); // only the operator
        Assert.True(h.RunUntil(() => h.Server.Stats.RefusedLobby > refused, 1_000_000));
        Assert.True(bob.InCountdown);

        alice.CancelStart();
        Assert.True(h.RunUntil(() => alice.InLobby && bob.InLobby, 1_000_000));
        Assert.Equal(NetGamePhase.Lobby, h.Server.Games.Single().Phase);
        Assert.Equal(0, bob.Lobby!.CountdownMillis);
        Assert.True(bob.Lobby.Members.First(m => m.Name == "bob").Ready);
        h.Run(6_000_000); // the aborted countdown never starts a race
        Assert.Null(alice.Welcome);
        Assert.Null(bob.Welcome);

        alice.RequestStart(); // a new start runs the full countdown again
        Assert.True(h.RunUntil(() => alice.InCountdown, 1_000_000));
        Assert.InRange(alice.Lobby!.CountdownMillis, 4_000, 5_000);
    }

    [GameFact]
    public void StartNeedsTheOperatorOnly_FreezesOptionsAndBlocksJoins_DuringTheFiveSecondCountdown()
    {
        using var h = Lobby(32, maxPlayers: 4, names: ["alice", "bob"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var dave = h.Join("dave");
        Assert.True(h.RunUntil(() => dave.List != null, 1_000_000));

        long refused = h.Server.Stats.RefusedLobby;
        bob.RequestStart();
        h.Run(100_000);
        Assert.True(h.Server.Stats.RefusedLobby > refused);
        Assert.True(alice.InLobby);
        Assert.Null(bob.Welcome);

        alice.RequestStart(); // bob is not ready: the operator may still start
        Assert.True(h.RunUntil(() => alice.InCountdown && bob.InCountdown, 1_000_000));
        Assert.InRange(alice.Lobby!.CountdownMillis, 1, 5_000);
        Assert.False(alice.Ready);
        Assert.False(bob.Ready);

        long joins = h.Server.Stats.RefusedJoins;
        dave.JoinGame(alice.GameId);
        h.Run(100_000);
        Assert.True(h.Server.Stats.RefusedJoins > joins);
        Assert.Null(dave.Lobby);
        Assert.Equal(2, alice.Lobby.Members.Count);

        long refusedOptions = h.Server.Stats.RefusedLobby;
        alice.SetOptions(6, 4);
        h.Run(100_000);
        Assert.True(h.Server.Stats.RefusedLobby > refusedOptions);
        Assert.Equal(1, bob.Lobby!.Track);
        Assert.Equal(2, bob.Lobby.Laps);

        bob.SetCar(3); // car selection stays open until the end of the countdown
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.First(m => m.Name == "bob").CarType == 3, 1_000_000));

        Assert.True(h.RunUntil(() => bob.Welcome != null && alice.Welcome != null, 8_000_000));
        Assert.Equal(alice.Welcome!.RaceId, bob.Welcome!.RaceId);
        Assert.NotEqual(alice.GameId, alice.Welcome.RaceId);
        Assert.NotEqual(0u, alice.Welcome.Epoch);
    }

    [GameFact]
    public void RaceWithHumansAndBots_ComposesRound_AndReturnsToTheLobbyWithStatePreserved()
    {
        using var h = Lobby(33, new SessionServerOptions { MaxRaceSteps = 30 }, maxPlayers: 4, track: 1, laps: 2,
            names: ["alice", "bob"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var dave = h.Join("dave");
        Assert.True(h.RunUntil(() => dave.List != null, 1_000_000));

        alice.SetOptions(3, 2);
        Assert.True(h.RunUntil(() => bob.Lobby!.Track == 3, 1_000_000));
        alice.SetCar(2);
        bob.SetCar(4);
        Assert.True(h.RunUntil(() => bob.Lobby!.Members.All(m => m.CarType >= 0), 1_000_000));
        alice.RequestStart();

        Assert.True(h.RunUntil(() => alice.Welcome != null && bob.Welcome != null, 8_000_000));
        int raceId = alice.Welcome!.RaceId;
        Assert.Equal(raceId, bob.Welcome!.RaceId);
        Assert.Equal(NetGamePhase.Race, h.Server.Games.Single().Phase);

        // A join during the race is refused; the same client joins as soon as the game is back in its lobby.
        dave.JoinGame(alice.GameId);
        h.Run(200_000);
        Assert.Null(dave.Lobby);

        Assert.True(h.RunUntil(() => alice.Results != null && bob.Results != null, 10_000_000));
        Assert.Equal(raceId, alice.Results!.RaceId);
        Assert.Equal(raceId, bob.Results!.RaceId);
        Assert.Equal(NetFinishReason.TimeLimit, alice.Results.Reason);
        Assert.Equal(8, alice.Results.Results.Racers.Count);
        Assert.Equal(2, alice.Results.Results.Racers.Count(x => x.Human));

        Assert.True(h.RunUntil(() => alice.InLobby && bob.InLobby, 5_000_000));
        Assert.Equal(NetGamePhase.Lobby, alice.Lobby!.Phase);
        Assert.Equal(3, alice.Lobby.Track);
        Assert.Equal(2, alice.Lobby.Laps);
        Assert.Equal(4, alice.Lobby.MaxPlayers);
        Assert.Equal(["alice", "bob"], alice.Lobby.Members.Select(m => m.Name));
        Assert.True(alice.IsOperator);
        Assert.False(bob.IsOperator);
        Assert.False(alice.Ready);
        Assert.False(bob.Ready);
        Assert.Equal(2, alice.Lobby.Members.First(m => m.Name == "alice").CarType);
        Assert.Equal(4, alice.Lobby.Members.First(m => m.Name == "bob").CarType);
        Assert.Equal(0, alice.Lobby.CountdownMillis);

        dave.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => dave.InLobby && alice.Lobby?.Members.Count == 3, 1_000_000),
            $"dave connected={dave.Connected} reject={dave.Reject?.Reason} game={alice.GameId:X8} phase={h.Server.Games.Single().Phase} " +
            $"members={h.Server.Games.Single().MemberCount} refused={h.Server.Stats.RefusedJoins} log={string.Join(" | ", h.Log)}");
        Assert.Equal(["alice", "bob", "dave"], dave.Lobby!.Members.Select(m => m.Name));
        Assert.True(dave.Lobby.Members[0].Operator);
    }

    [GameFact]
    public void OperatorIsTransferredToTheEarliestRemainingMember_AndTheLastLeaveRemovesTheGame()
    {
        using var h = Lobby(34, names: ["alice", "bob", "carol"]);
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var carol = h.Clients[2];

        alice.Disconnect();
        Assert.True(h.RunUntil(() => carol.Lobby?.Members.Count == 2 && carol.Lobby.Members[0].Name == "bob", 1_000_000));
        Assert.Equal("bob", carol.Lobby!.Members.Single(m => m.Operator).Name);
        Assert.True(bob.IsOperator);
        Assert.False(carol.IsOperator);

        bob.LeaveGame();
        Assert.True(h.RunUntil(() => carol.Lobby?.Members.Count == 1 && carol.Lobby.Members[0].Name == "carol", 1_000_000));
        Assert.True(carol.IsOperator);

        carol.LeaveGame();
        Assert.True(h.RunUntil(() => h.Server.GameCount == 0, 1_000_000));
        Assert.Equal(1, h.Server.Stats.GamesClosed);
    }
}
