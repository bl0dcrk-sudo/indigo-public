using System.Net;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using static NFS.Desktop.Tests.MenuPlayers;

namespace NFS.Desktop.Tests;

/// <summary>Task 5.3 end to end without windows: <see cref="NetworkMenuHost"/>s (what <c>Game1</c> drives) over real UDP on
/// localhost against a real <see cref="SessionServer"/>, operated only through menu keys. The client never runs a server of its
/// own: the server is an external process.</summary>
public sealed class RemoteDesktopSessionUdpTests
{
    static UdpSessionServer Server(string dir, int raceSteps = 256 + 200, long graceMicros = 30_000_000) =>
        new(dir, new SessionServerOptions
        {
            CountdownMicros = 500_000, MaxRaceSteps = raceSteps, RaceLingerMicros = 2_000_000, GraceMicros = graceMicros,
        });

    /// <summary>Alice connects, creates a two-player game without bots from the options screen; Bob connects and joins it
    /// from the games list; both mark ready.</summary>
    static async Task<(MenuPlayer Alice, MenuPlayer Bob)> LobbyOfTwo(string dir, UdpSessionServer server, int aliceCar = 3, int bobCar = 9)
    {
        var alice = new MenuPlayer(dir, "Alice", server.Port, aliceCar);
        var bob = new MenuPlayer(dir, "Bob", server.Port, bobCar);
        try
        {
            Assert.Equal(NetScreen.Connect, alice.Screen);
            alice.Connect();
            Assert.Equal(NetScreen.Connecting, alice.Screen);
            Assert.True(await Until(() => alice.Screen == NetScreen.Games, 10_000, alice), Why(alice));

            // Create: the options screen is separate; two players, no bots, one lap.
            alice.MoveToGames(GamesRow.Create);
            alice.Press(MenuKey.Enter);
            Assert.Equal(NetScreen.CreateOptions, alice.Screen);
            alice.MoveToCreate(CreateRow.MaxPlayers);
            for (int i = 0; i < 6; i++) alice.Press(MenuKey.Left);
            Assert.Equal(2, alice.Menu.MaxPlayers);
            alice.MoveToCreate(CreateRow.Create);
            alice.Press(MenuKey.Enter);
            Assert.True(await Until(() => alice.Screen == NetScreen.Lobby, 10_000, alice), Why(alice));
            Assert.True(alice.Menu.IsOperator);
            // no bots and one lap: the operator sets both in the lobby (the server created the game with auto bots and two laps)
            alice.MoveToLobby(LobbyRow.Bots);
            alice.Press(MenuKey.Right);
            Assert.True(await Until(() => alice.Menu.Lobby is { Bots: 0 }, 10_000, alice), Why(alice));   // the next change builds on the server's answer
            alice.MoveToLobby(LobbyRow.Laps);
            alice.Press(MenuKey.Left);
            Assert.True(await Until(() => alice.Menu.Lobby is { Bots: 0, Laps: 1 }, 10_000, alice), Why(alice));

            // Join: Bob sees the game on the list and picks it.
            bob.Connect();
            Assert.True(await Until(() => bob.Screen == NetScreen.Games && bob.Menu.Games.Count == 1, 10_000, alice, bob), Why(alice, bob));
            bob.MoveToGames(GamesRow.Game, alice.Menu.CreateName);
            bob.Press(MenuKey.Enter);   // Enter on the selected game joins it
            Assert.True(await Until(() => bob.Screen == NetScreen.Lobby && alice.Menu.Lobby!.Members.Count == 2, 10_000, alice, bob), Why(alice, bob));
            Assert.False(bob.Menu.IsOperator);
            Assert.DoesNotContain(LobbyRow.Start, bob.Menu.LobbyRows);

            foreach (var p in new[] { alice, bob })
            {
                p.MoveToLobby(LobbyRow.Ready);
                p.Press(MenuKey.Enter);
            }
            // the menu's car of each player reached the server on entering the lobby
            Assert.True(await Until(() => alice.Menu.Lobby!.Members.All(m => m.Ready)
                && alice.Menu.Lobby.Members.Single(m => m.Name == "Alice").CarType == aliceCar
                && alice.Menu.Lobby.Members.Single(m => m.Name == "Bob").CarType == bobCar, 10_000, alice, bob), Why(alice, bob));
            return (alice, bob);
        }
        catch { alice.Dispose(); bob.Dispose(); throw; }
    }

    [GameDataFact]
    public async Task TwoMenuClientsConnectCreateJoinReadyStartRaceSummaryAndASecondRoundOnOneConnectionEach()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = Server(dir);
        var (alice, bob) = await LobbyOfTwo(dir, server);
        using var _a = alice;
        using var _b = bob;

        // The operator starts: countdown on both screens, then the race (the creator takes the same path as the guest).
        alice.MoveToLobby(LobbyRow.Start);
        alice.Press(MenuKey.Enter);
        Assert.True(await Until(() => alice.Screen is NetScreen.Countdown or NetScreen.Race && bob.Screen is NetScreen.Countdown or NetScreen.Race,
            10_000, alice, bob), Why(alice, bob));
        Assert.True(await Until(() => alice.Host.Drive != null && bob.Host.Drive != null, 30_000, alice, bob), Why(alice, bob));
        Assert.Equal(NetScreen.Race, alice.Screen);
        var round1 = alice.Host.Session!.Client!;
        var drive1 = alice.Host.Drive;
        int raceId1 = round1.Welcome!.RaceId;
        Assert.Equal(raceId1, bob.Host.Session!.Client!.Welcome!.RaceId);
        Assert.NotEqual(round1.Welcome.CarId, bob.Host.Session.Client.Welcome!.CarId);

        // Results: both go to the summary; the race picture is dropped and the server is back in its lobby.
        Assert.True(await Until(() => alice.Screen == NetScreen.Summary && bob.Screen == NetScreen.Summary, 60_000, alice, bob), Why(alice, bob));
        foreach (var p in new[] { alice, bob })
        {
            Assert.Null(p.Host.Drive);
            Assert.True(p.Menu.HasSummary);
            Assert.NotNull(p.Menu.View!.Results);
            Assert.Equal(2, p.Menu.View.Results!.Racers.Count(r => r.Human));
        }
        Assert.True(await Until(() => alice.Menu.Lobby?.Phase == NetGamePhase.Lobby, 10_000, alice, bob), Why(alice, bob));

        // Alice returns to the lobby; Bob still looks at the summary. Alice starts round two: Bob's summary is closed
        // without a question, he sees the countdown and drives.
        alice.MoveToSummary(SummaryRow.ReturnToLobby);
        alice.Press(MenuKey.Enter);
        Assert.Equal(NetScreen.Lobby, alice.Screen);
        Assert.False(alice.Menu.HasSummary);
        alice.MoveToLobby(LobbyRow.Start);
        alice.Press(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmStart, alice.Screen);          // Bob is not ready again (readiness resets on the return to the lobby)
        alice.Press(MenuKey.Right, MenuKey.Enter);
        Assert.True(await Until(() => bob.Screen is NetScreen.Countdown or NetScreen.Race, 10_000, alice, bob), Why(alice, bob));
        Assert.True(await Until(() => alice.Host.Drive != null && bob.Host.Drive != null, 30_000, alice, bob), Why(alice, bob));
        Assert.NotSame(drive1, alice.Host.Drive);
        Assert.NotEqual(raceId1, alice.Host.Session!.Client!.Welcome!.RaceId);
        Assert.NotSame(round1, alice.Host.Session.Client);
        Assert.True(await Until(() => alice.Screen == NetScreen.Summary && bob.Screen == NetScreen.Summary, 60_000, alice, bob), Why(alice, bob));

        // One UDP connection and one session per player carried both rounds; nobody saw an error.
        foreach (var p in new[] { alice, bob })
        {
            Assert.Equal(1, p.Host.Session!.Session.Attempts);
            Assert.Equal(2, p.Host.Session.Session.Stats.RaceClients);
            Assert.Null(p.Host.Session.Error);
        }

        // Leaving from the summary asks first, then lands on the games list; backing out of it disconnects for good.
        bob.MoveToSummary(SummaryRow.Exit);
        bob.Press(MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmExit, bob.Screen);
        bob.Press(MenuKey.Right, MenuKey.Enter);
        Assert.True(await Until(() => bob.Screen == NetScreen.Games, 5_000, alice, bob), Why(alice, bob));
        bob.Press(MenuKey.Escape);
        Assert.True(bob.Host.ExitRequested);
        Assert.Null(bob.Host.Session);
    }

    [GameDataFact]
    public async Task LeavingTheRaceThroughPauseGoesToTheGamesListAndTheAbandonedRaceCanBeRejoined()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = Server(dir, raceSteps: 256 + 3000);
        var (alice, bob) = await LobbyOfTwo(dir, server);
        using var _a = alice;
        using var _b = bob;
        alice.MoveToLobby(LobbyRow.Start);
        alice.Press(MenuKey.Enter);
        Assert.True(await Until(() => alice.Host.Drive != null && bob.Host.Drive != null, 30_000, alice, bob), Why(alice, bob));
        int raceId = bob.Host.Session!.Client!.Welcome!.RaceId;

        // Pause -> "leave race" -> exit confirmation -> yes.
        bob.Pause();
        Assert.True(bob.Host.Drive!.RemotePaused);
        bob.Press(MenuKey.Down, MenuKey.Enter);
        Assert.Equal(NetScreen.ConfirmExit, bob.Screen);
        Assert.NotNull(bob.Host.Drive);   // the race picture stays under the question
        bob.Press(MenuKey.Enter);   // "no" is the default answer: back to the paused race
        Assert.Equal(NetScreen.Race, bob.Screen);
        bob.Press(MenuKey.Enter);   // pause menu: Leave is still selected
        Assert.Equal(NetScreen.ConfirmExit, bob.Screen);
        bob.Press(MenuKey.Right, MenuKey.Enter);
        Assert.True(await Until(() => bob.Screen == NetScreen.Games && bob.Host.Drive == null, 5_000, alice, bob), Why(alice, bob));

        // The game stays on the list as returnable; the player cannot be locked out of it.
        Assert.True(await Until(() => bob.Menu.Games.Any(g => g.Phase == NetGamePhase.Race), 10_000, alice, bob), Why(alice, bob));
        var game = bob.Menu.Games.Single(g => g.Phase == NetGamePhase.Race);
        Assert.Equal(JoinAvailability.Reserved, bob.Menu.CanJoin(game));
        bob.MoveToGames(GamesRow.Game, game.Name);
        bob.Press(MenuKey.Enter);
        Assert.True(await Until(() => bob.Screen == NetScreen.Race && bob.Host.Drive != null, 20_000, alice, bob), Why(alice, bob));
        Assert.Equal(raceId, bob.Host.Session!.Client!.Welcome!.RaceId);
        Assert.Equal(1, bob.Host.Session.Session.Attempts);   // the same connection
    }

    [GameDataFact]
    public async Task LinkLossShowsTheReconnectBannerAndTheResumedRaceGetsAFreshDrive()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = Server(dir, raceSteps: 256 + 3000);
        var (alice, bob) = await LobbyOfTwo(dir, server);
        using var _a = alice;
        using var _b = bob;
        alice.MoveToLobby(LobbyRow.Start);
        alice.Press(MenuKey.Enter);
        Assert.True(await Until(() => alice.Host.Drive != null && bob.Host.Drive != null, 30_000, alice, bob), Why(alice, bob));
        var driveBefore = bob.Host.Drive;
        var clientBefore = bob.Host.Session!.Client;

        bob.Host.Session.DropLink();
        Assert.True(await Until(() => bob.Menu.Reconnecting, 10_000, alice, bob), Why(alice, bob));
        Assert.Equal(NetScreen.Race, bob.Screen);   // the race screen stays; only the banner shows
        Assert.True(await Until(() => !bob.Menu.Reconnecting && bob.Host.Drive != null && !ReferenceEquals(bob.Host.Drive, driveBefore),
            30_000, alice, bob), Why(alice, bob));
        Assert.Equal(NetScreen.Race, bob.Screen);
        Assert.NotSame(clientBefore, bob.Host.Session.Client);   // a new round client (resume welcome, new epoch)
        Assert.Equal(clientBefore!.Welcome!.RaceId, bob.Host.Session.Client!.Welcome!.RaceId);
        Assert.Null(bob.Host.Session.Error);
    }

    [GameDataFact]
    public async Task ConnectFailuresReturnToTheConnectScreenWithTheReasonAndCancelIsClean()
    {
        string dir = GameFiles.FindGameDir(null);
        using var server = Server(dir);

        // A nick that is already connected is refused at the handshake: the Connect screen shows why.
        using var first = new MenuPlayer(dir, "Alice", server.Port);
        first.Connect();
        Assert.True(await Until(() => first.Screen == NetScreen.Games, 10_000, first), Why(first));
        using var intruder = new MenuPlayer(dir, "Alice", server.Port);
        intruder.Connect();
        Assert.True(await Until(() => intruder.Screen == NetScreen.Connect, 10_000, first, intruder), Why(intruder));
        Assert.True(intruder.Menu.MessageIsError);
        Assert.Contains("nick", intruder.Menu.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(intruder.Host.Session);

        // An address that does not resolve never reaches the network.
        using var lost = new MenuPlayer(dir, "Carol", server.Port,
            resolve: (_, _, _) => Task.FromException<IPEndPoint>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
        lost.Connect();
        Assert.True(await Until(() => lost.Screen == NetScreen.Connect && lost.Menu.Message != null, 5_000, lost), Why(lost));
        Assert.Contains("lookup", lost.Menu.Message, StringComparison.OrdinalIgnoreCase);

        // Cancelling a pending connection (Escape) frees the session; a later attempt works.
        using var cancel = new MenuPlayer(dir, "Dave", server.Port);
        cancel.Connect();
        Assert.Equal(NetScreen.Connecting, cancel.Screen);
        cancel.Press(MenuKey.Escape);
        Assert.Equal(NetScreen.Connect, cancel.Screen);
        for (int i = 0; i < 5; i++) cancel.Step();
        Assert.Null(cancel.Host.Session);
        // The server may still hold the cancelled attempt under that nick for a moment; trying again gets in.
        var retry = System.Diagnostics.Stopwatch.StartNew();
        cancel.Connect();
        Assert.True(await Until(() =>
        {
            if (cancel.Screen == NetScreen.Connect && retry.ElapsedMilliseconds > 300) { retry.Restart(); cancel.Connect(); }
            return cancel.Screen == NetScreen.Games;
        }, 20_000, cancel), Why(cancel));
    }
}
