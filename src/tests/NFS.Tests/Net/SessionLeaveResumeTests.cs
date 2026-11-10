using NFS.Core;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Leaving a running game on purpose and coming back: a leave in Countdown/Race reserves the seat like a lost
/// connection (the connection stays and gets the list), the same connection rejoins from the list, a new connection returns by
/// name or token, the operator is always a connected member, and the reservation outlives the return to the lobby until the
/// grace window ends.</summary>
public class SessionLeaveResumeTests
{
    const long Grace = 30_000_000;

    static byte[] HelloWithToken(string name, byte[] token) =>
        new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, name,
            NetProtocol.NoCarType, BuildInfo.AppVersion, token).Encode();

    /// <summary>A game "game" (4 places) of the named players; the first one creates it and is the operator.</summary>
    static SessionHarness Game(int seed, SessionServerOptions options, params string[] names)
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

    static SessionServerOptions RaceOptions(long grace = Grace, int maxSteps = 200_000) => new()
    {
        CountdownMicros = 0,
        MaxRaceSteps = maxSteps,
        GraceMicros = grace,
    };

    static SessionHarness Race(int seed, SessionServerOptions options, params string[] names)
    {
        var h = Game(seed, options, names);
        h.Clients[0].RequestStart();
        Assert.True(h.RunUntil(() => h.Clients.All(c => c.InRace && c.Welcome != null), 10_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        return h;
    }

    static NetLobbyMember? Seat(SessionTestClient viewer, string name) => viewer.Lobby?.Members.FirstOrDefault(m => m.Name == name);

    static bool Reserved(SessionTestClient viewer, string name) => Seat(viewer, name) is { Connected: false };

    /// <summary>The client leaves the game and waits for the list the server sends to a connection without a game.</summary>
    static void Leave(SessionHarness h, SessionTestClient client)
    {
        int lists = client.Lists.Count;
        client.LeaveGame();
        Assert.True(h.RunUntil(() => client.Lists.Count > lists, 1_000_000), string.Join("\n", h.Log.TakeLast(20)));
    }

    /// <summary>The connection joins the game from the list and waits for the fresh lobby state of the new epoch.</summary>
    static void Rejoin(SessionHarness h, SessionTestClient client, int gameId)
    {
        int states = client.States.Count;
        client.JoinGame(gameId);
        Assert.True(h.RunUntil(() => client.States.Count > states && Seat(client, client.Name)?.Connected == true, 2_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
    }

    [GameFact]
    public void LeavingTheRaceReservesTheSeatAndTheConnectionGetsTheListAndKeepsItsName()
    {
        using var h = Race(201, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        int connections = h.Server.ConnectionCount;

        Leave(h, bob);
        Assert.True(h.RunUntil(() => Reserved(alice, "bob"), 1_000_000));
        Assert.True(alice.InRace);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(2, game.Round!.MemberCount);
        Assert.Empty(alice.PlayerLefts);
        Assert.False(bob.Disconnected);
        Assert.Equal(connections, h.Server.ConnectionCount);
        var info = Assert.Single(bob.List!.Games);
        Assert.Equal(game.GameId, info.GameId);
        Assert.Equal(NetGamePhase.Race, info.Phase);

        // The connection still owns the name: another client cannot take it.
        var other = h.Join("bob");
        Assert.True(h.RunUntil(() => other.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, other.Reject?.Reason);
    }

    [GameFact]
    public void RejoiningFromTheListInTheRaceResumesTheSameSeat()
    {
        using var h = Race(202, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        var oldWelcome = bob.Welcome!;
        var member = game.Members.Single(m => m.Name == "bob");
        int slot = member.Slot;
        var token = member.ResumeToken;

        Leave(h, bob);
        Assert.True(h.RunUntil(() => Reserved(alice, "bob"), 1_000_000));
        bob.JoinGame(game.GameId);
        Assert.True(h.RunUntil(() => bob.Welcomes.Count == 2 && bob.Lobby?.Phase == NetGamePhase.Race && Seat(bob, "bob")?.Connected == true,
            2_000_000), string.Join("\n", h.Log.TakeLast(20)));

        Assert.True(bob.Welcome!.Resume);
        Assert.Equal(oldWelcome.RaceId, bob.Welcome.RaceId);
        Assert.Equal(oldWelcome.Seat, bob.Welcome.Seat);
        Assert.NotEqual(oldWelcome.Epoch, bob.Welcome.Epoch);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(2, game.Round!.MemberCount);
        Assert.Equal(slot, game.Members.Single(m => m.Name == "bob").Slot);
        Assert.Equal(token, bob.Lobby!.ResumeToken);
        Assert.Equal(1, h.Server.Stats.Resumed);
        Assert.True(h.RunUntil(() => Seat(alice, "bob")?.Connected == true, 1_000_000));
    }

    [GameFact]
    public void JoiningARunningGameWithoutAReservationIsRefused()
    {
        using var h = Race(203, RaceOptions(), "alice", "bob");
        var game = h.Server.Games.Single();
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => carol.List != null, 1_000_000));
        long refused = h.Server.Stats.RefusedJoins;

        carol.JoinGame(game.GameId);
        Assert.True(h.RunUntil(() => h.Server.Stats.RefusedJoins > refused, 1_000_000));
        h.Run(100_000);
        Assert.Null(carol.Lobby);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(2, game.Round!.MemberCount);
    }

    [GameFact]
    public void OperatorLeavingInTheCountdownPassesTheRoleOnAndTheReturnerIsAnOrdinaryPlayer()
    {
        using var h = Game(204, new SessionServerOptions { CountdownMicros = 5_000_000, GraceMicros = Grace, MaxRaceSteps = 200_000 },
            "alice", "bob", "carol");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var carol = h.Clients[2];
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => bob.InCountdown && game.Phase == NetGamePhase.Countdown, 1_000_000));

        Leave(h, alice);
        Assert.True(h.RunUntil(() => Reserved(bob, "alice") && bob.IsOperator, 1_000_000));
        Assert.Equal(NetGamePhase.Countdown, game.Phase);
        Assert.Equal("bob", game.Operator?.Name);
        Assert.False(carol.IsOperator);
        Assert.Single(bob.Lobby!.Members, m => m.Operator);
        Assert.Equal(3, game.MemberCount);

        Rejoin(h, alice, game.GameId);
        Assert.Equal(NetGamePhase.Countdown, alice.Lobby!.Phase);
        Assert.False(alice.IsOperator);
        Assert.Equal("bob", game.Operator?.Name);
        Assert.Equal(["alice", "bob", "carol"], alice.Lobby.Members.Select(m => m.Name));
        Assert.Single(alice.Lobby.Members, m => m.Operator);

        // The countdown was not interrupted by the leave and the return.
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Race, 10_000_000));
        Assert.Equal(3, game.MemberCount);
    }

    [GameFact]
    public void WhenNobodyIsConnectedTheFirstMemberToReturnBecomesTheOperator()
    {
        using var h = Game(205, new SessionServerOptions { CountdownMicros = 60_000_000, GraceMicros = Grace }, "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        alice.RequestStart();
        Assert.True(h.RunUntil(() => bob.InCountdown, 1_000_000));

        alice.Disconnect();
        Assert.True(h.RunUntil(() => Reserved(bob, "alice") && bob.IsOperator, 1_000_000));
        bob.Disconnect();
        Assert.True(h.RunUntil(() => game.Members.All(m => !m.Connected), 1_000_000));
        Assert.Null(game.Operator);
        Assert.Equal(1, h.Server.GameCount);

        var bob2 = h.Join("bob");
        Assert.True(h.RunUntil(() => bob2.InCountdown && bob2.IsOperator, 1_000_000), string.Join("\n", h.Log.TakeLast(20)));
        Assert.Equal("bob", game.Operator?.Name);

        var alice2 = h.Join("alice");
        Assert.True(h.RunUntil(() => alice2.InCountdown && Seat(alice2, "alice")?.Connected == true, 1_000_000));
        Assert.False(alice2.IsOperator);
        Assert.Equal("bob", game.Operator?.Name);
        Assert.Single(alice2.Lobby!.Members, m => m.Operator);
    }

    [GameFact]
    public void TheReservationSurvivesTheReturnToTheLobbyAndIsRemovedWhenTheWindowRunsOut()
    {
        const long grace = 60_000_000;
        using var h = Race(206, RaceOptions(grace, maxSteps: 30), "alice", "bob");
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        long left = h.Net.Now;
        Leave(h, h.Clients[1]);
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby && alice.Results != null, 20_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        Assert.True(h.Net.Now - left < grace, "the race must end inside the window for this test");

        var seat = Seat(alice, "bob");
        Assert.NotNull(seat);
        Assert.False(seat!.Connected);
        Assert.False(seat.Ready);
        Assert.Equal(2, game.MemberCount);
        Assert.True(alice.IsOperator);

        Assert.True(h.RunUntil(() => game.MemberCount == 1, grace), string.Join("\n", h.Log.TakeLast(20)));
        Assert.True(h.Net.Now - left >= grace);
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.Count == 1, 1_000_000));
        Assert.True(alice.IsOperator);
        Assert.Equal(1, h.Server.GameCount);
    }

    [GameFact]
    public void ReturningFromTheListInsideTheWindowResumesTheLobbySeatAfterTheRace()
    {
        using var h = Race(207, RaceOptions(60_000_000, maxSteps: 30), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        int slot = game.Members.Single(m => m.Name == "bob").Slot;
        Leave(h, bob);
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby && alice.Results != null, 20_000_000));

        Rejoin(h, bob, game.GameId);
        Assert.True(bob.InLobby);
        Assert.False(bob.Ready);
        Assert.False(bob.IsOperator);
        Assert.True(alice.IsOperator);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(slot, Seat(bob, "bob")!.Slot);
        Assert.Equal(1, h.Server.Stats.Resumed);
    }

    [GameFact]
    public void TheWindowEndingInTheRaceMarksTheCarDnfAndTheLobbyHasNoSeatForIt()
    {
        using var h = Race(208, RaceOptions(grace: 400_000, maxSteps: 600), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        Assert.True(h.RunUntil(() => game.Round!.Phase == NetRacePhase.Racing, 10_000_000));
        Leave(h, bob);
        Assert.True(h.RunUntil(() => game.MemberCount == 1, 2_000_000), string.Join("\n", h.Log.TakeLast(20)));
        Assert.True(h.RunUntil(() => alice.PlayerLefts.Count > 0, 1_000_000), string.Join("\n", h.Log.TakeLast(25)));
        Assert.True(h.RunUntil(() => alice.Results != null && alice.InLobby, 20_000_000));
        Assert.Equal(8, alice.Results!.Results.Racers.Count);
        Assert.Equal(2, alice.Results.Results.Racers.Count(r => r.Human));   // the departed car stays in the result (DNF), the lobby no longer lists it
        Assert.Equal(["alice"], alice.Lobby!.Members.Select(m => m.Name));

        // The reservation is gone: a join of the lobby from the list is a plain new join.
        Rejoin(h, bob, game.GameId);
        Assert.True(bob.InLobby);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(0, h.Server.Stats.Resumed);
    }

    [GameFact]
    public void LeavingTheLobbyStillFreesTheSeatAtOnce()
    {
        using var h = Game(209, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var game = h.Server.Games.Single();
        Leave(h, h.Clients[1]);
        Assert.True(h.RunUntil(() => game.MemberCount == 1 && alice.Lobby!.Members.Count == 1, 1_000_000));
        Assert.True(alice.IsOperator);
    }

    [GameFact]
    public void WithoutAGraceWindowLeavingTheRaceIsAnOrdinaryLeave()
    {
        using var h = Race(210, RaceOptions(grace: 0), "alice", "bob");
        var game = h.Server.Games.Single();
        Leave(h, h.Clients[1]);
        Assert.True(h.RunUntil(() => game.MemberCount == 1, 1_000_000));
        Assert.Equal(1, game.Round!.MemberCount);
    }

    // ---------------- resume by name ----------------

    [GameFact]
    public void HelloWithoutATokenResumesTheReservedMemberByName()
    {
        using var h = Race(211, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        var token = bob.Lobby!.ResumeToken!;
        var oldWelcome = bob.Welcome!;
        bob.Link.Cut();
        Assert.True(h.RunUntil(() => Reserved(alice, "bob"), 2_000_000));

        // A restarted client has no token; another letter case of the same name is the same player.
        var restarted = h.Join("BOB");
        Assert.True(h.RunUntil(() => restarted.Welcome?.Resume == true && restarted.Lobby != null, 2_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        Assert.Null(restarted.Reject);
        Assert.Equal(oldWelcome.Seat, restarted.Welcome!.Seat);
        Assert.Equal(oldWelcome.RaceId, restarted.Welcome.RaceId);
        Assert.Equal(token, restarted.Lobby!.ResumeToken);
        Assert.Equal(2, game.MemberCount);
        Assert.Equal(2, game.Round!.MemberCount);
        Assert.Equal(1, h.Server.Stats.Resumed);
        Assert.True(h.RunUntil(() => Seat(alice, "bob")?.Connected == true, 1_000_000));
    }

    [GameFact]
    public void ATokenOfAnotherReservationIsRefusedAndTheGameStaysAsItWas()
    {
        using var h = Race(212, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        var aliceToken = alice.Lobby!.ResumeToken!;
        bob.Link.Cut();
        Assert.True(h.RunUntil(() => Reserved(alice, "bob"), 2_000_000));
        int revision = game.Revision;
        int roundMembers = game.Round!.MemberCount;

        var wrong = h.Join("bob", hello: HelloWithToken("bob", aliceToken));
        Assert.True(h.RunUntil(() => wrong.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, wrong.Reject?.Reason);
        var garbage = h.Join("bob", hello: HelloWithToken("bob", new byte[NetProtocol.ResumeTokenLength]));
        Assert.True(h.RunUntil(() => garbage.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, garbage.Reject?.Reason);

        Assert.Equal(revision, game.Revision);
        Assert.Equal(roundMembers, game.Round.MemberCount);
        Assert.Equal(2, game.MemberCount);
        Assert.False(game.Members.Single(m => m.Name == "bob").Connected);
        Assert.Equal(0, h.Server.Stats.Resumed);

        // The reservation is untouched: its own name still resumes it.
        var right = h.Join("bob");
        Assert.True(h.RunUntil(() => right.Welcome?.Resume == true, 2_000_000));
    }

    [GameFact]
    public void ANameUsedByAConnectedPlayerIsADuplicateEvenWithAToken()
    {
        using var h = Race(213, RaceOptions(), "alice", "bob");
        var alice = h.Clients[0];
        var bobToken = h.Clients[1].Lobby!.ResumeToken!;

        var plain = h.Join("alice");
        Assert.True(h.RunUntil(() => plain.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, plain.Reject?.Reason);
        var withToken = h.Join("alice", hello: HelloWithToken("alice", alice.Lobby!.ResumeToken!));
        Assert.True(h.RunUntil(() => withToken.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, withToken.Reject?.Reason);
        var foreignToken = h.Join("alice", hello: HelloWithToken("alice", bobToken));
        Assert.True(h.RunUntil(() => foreignToken.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.NameTaken, foreignToken.Reject?.Reason);
        Assert.True(alice.InRace);
        Assert.Equal(0, h.Server.Stats.Resumed);
    }

    [GameFact]
    public void AnExpiredReservationFreesTheNameAndInvalidatesTheToken()
    {
        using var h = Race(214, RaceOptions(grace: 300_000), "alice", "bob");
        var alice = h.Clients[0];
        var bob = h.Clients[1];
        var game = h.Server.Games.Single();
        var token = bob.Lobby!.ResumeToken!;
        bob.Link.Cut();
        Assert.True(h.RunUntil(() => Reserved(alice, "bob"), 2_000_000));
        Assert.True(h.RunUntil(() => game.MemberCount == 1, 2_000_000));

        var withToken = h.Join("bob", hello: HelloWithToken("bob", token));
        Assert.True(h.RunUntil(() => withToken.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, withToken.Reject?.Reason);

        // Without a token the name is an ordinary new connection that is not put back into the race.
        var fresh = h.Join("bob");
        Assert.True(h.RunUntil(() => fresh.List != null, 1_000_000));
        Assert.Null(fresh.Reject);
        Assert.False(fresh.Disconnected);
        Assert.Null(fresh.Welcome);
        fresh.JoinGame(game.GameId);
        h.Run(200_000);
        Assert.Null(fresh.Lobby);
        Assert.Equal(1, game.MemberCount);
    }
}
