using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>The session client against the real <see cref="SessionServer"/> in memory (simulated network, fake clock): every
/// operation, the server's refusals as the client reports them, resume by token and by name, and reconnect after a cut link.</summary>
public class SessionClientServerTests
{
    /// <summary>A <see cref="SessionClient"/> wired to the harness network; records race messages and answers a race welcome with
    /// a ready message like a race client would.</summary>
    sealed class Rig
    {
        readonly SessionHarness _h;
        public SessionClient Client { get; }
        public List<SimNetwork.SimClient> Links { get; } = [];
        public List<SessionError> Errors { get; } = [];
        public List<bool> Reconnects { get; } = [];
        public List<NetWelcome> Welcomes { get; } = [];
        public List<NetMessageType> RaceTypes { get; } = [];
        /// <summary>While set, new connection attempts go nowhere (a dead network): nothing is delivered.</summary>
        public bool Offline { get; set; }
        public SimNetwork.SimClient Link => Links[^1];

        public Rig(SessionHarness h, string name, SessionClientOptions? options = null, byte[]? fingerprint = null, bool autoReady = true)
        {
            _h = h;
            Client = new SessionClient(fingerprint ?? SessionHarness.Fingerprint, name, Open, () => h.Net.Now, options,
                s => h.Log.Add($"{h.Net.Now / 1000} {s}"));
            Client.Error += Errors.Add;
            Client.Reconnected += Reconnects.Add;
            Client.RaceMessage += (channel, data) =>
            {
                var type = (NetMessageType)data[0];
                RaceTypes.Add(type);
                if (type != NetMessageType.Welcome) return;
                var welcome = NetWelcome.Decode(data);
                Welcomes.Add(welcome);
                if (autoReady) Client.SendRace(NetChannel.ReliableOrdered, new NetReady(welcome.RaceId, welcome.Epoch, welcome.SetupHash).Encode());
            };
        }

        INetClientTransport Open(INetClientHandler handler, byte[] hello)
        {
            var link = _h.Net.AddClient(handler, new LinkProfile(), new LinkProfile());
            Links.Add(link);
            if (!Offline) link.Connect(hello);
            return link;
        }
    }

    static readonly SessionServerOptions Defaults = new() { CountdownMicros = 500_000, MaxRaceSteps = 200_000, GraceMicros = 30_000_000 };

    static SessionHarness Server(int seed, SessionServerOptions? options = null)
    {
        var h = new SessionHarness(seed, options ?? Defaults);
        h.Net.DisconnectTimeoutMicros = 200_000;
        return h;
    }

    static bool Until(SessionHarness h, Func<bool> done, long micros = 2_000_000, params Rig[] rigs) =>
        h.RunUntil(() =>
        {
            foreach (var rig in rigs) rig.Client.Update(h.Net.Now);
            return done();
        }, micros);

    static void Wait(SessionHarness h, Func<bool> done, string what, long micros = 2_000_000, params Rig[] rigs) =>
        Assert.True(Until(h, done, micros, rigs), $"timed out: {what}\n{string.Join("\n", h.Log.TakeLast(25))}");

    static Rig Connected(SessionHarness h, string name, SessionClientOptions? options = null)
    {
        var rig = new Rig(h, name, options);
        Assert.True(rig.Client.Connect());
        Assert.Equal(SessionClientPhase.Connecting, rig.Client.Phase);
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Browsing, "handshake", rigs: rig);
        return rig;
    }

    static Rig Creator(SessionHarness h, string name = "alice", string game = "game", int max = 4, int track = 1, int laps = 1,
        bool backward = false, int bots = NetProtocol.AutoBots, bool traffic = false)
    {
        var rig = Connected(h, name);
        Assert.True(rig.Client.CreateGame(game, max));
        Wait(h, () => rig.Client.Phase == SessionClientPhase.InGame, "create", rigs: rig);
        // The server gives a new game its defaults; the operator sets the options of the test in the lobby.
        Assert.True(rig.Client.SetOptions(track, laps, backward, bots, traffic));
        Wait(h, () => rig.Client.Lobby is { } l && (l.Track, l.Laps, l.Backward, l.Bots, l.Traffic) == (track, laps, backward, bots, traffic),
            "options", rigs: rig);
        return rig;
    }

    static Rig Joiner(SessionHarness h, string name, int gameId)
    {
        var rig = Connected(h, name);
        Assert.True(rig.Client.JoinGame(gameId));
        Wait(h, () => rig.Client.Phase == SessionClientPhase.InGame, "join", rigs: rig);
        return rig;
    }

    // ---------------- handshake ----------------

    [GameFact]
    public void HandshakeLeadsToTheGamesListAndAnIdleClientSeesGamesAppear()
    {
        using var h = Server(1);
        var bob = Connected(h, "bob");
        Assert.Empty(bob.Client.Games);
        Assert.True(bob.Client.Connected);
        Assert.Null(bob.Client.Lobby);
        Assert.Equal(1, bob.Client.Stats.GameLists);

        var changes = 0;
        bob.Client.GamesChanged += _ => changes++;
        var alice = Creator(h, "alice", "fast one", track: 2, laps: 3);
        Wait(h, () => bob.Client.Games.Count == 1, "broadcast list", rigs: bob);
        var info = bob.Client.Games.Single();
        Assert.Equal("fast one", info.Name);
        Assert.Equal(2, info.Track);
        Assert.Equal(3, info.Laps);
        Assert.Equal(NetGamePhase.Lobby, info.Phase);
        Assert.Equal(1, info.Players);
        Assert.True(changes >= 1);

        Assert.True(bob.Client.RequestGames());
        int lists = (int)bob.Client.Stats.GameLists;
        Wait(h, () => bob.Client.Stats.GameLists > lists, "requested list", rigs: bob);
        Assert.Equal(info.GameId, alice.Client.GameId);
    }

    [GameFact]
    public void ADuplicateNameIsRejectedWithNameTakenAndTheFirstClientIsUntouched()
    {
        using var h = Server(2);
        var alice = Connected(h, "alice");
        var second = new Rig(h, "ALICE");
        second.Client.Connect();
        Wait(h, () => second.Client.Phase == SessionClientPhase.Rejected, "reject", rigs: second);
        Assert.Equal(NetRejectReason.NameTaken, second.Client.LastReject!.Reason);
        Assert.Equal(NetRejectReason.NameTaken, second.Client.LastError!.Reason);
        Assert.False(second.Client.Connected);
        Assert.Single(second.Links);   // a final answer is not retried
        Assert.Equal(SessionClientPhase.Browsing, alice.Client.Phase);
    }

    [GameFact]
    public void ABogusResumeTokenIsRefusedAndForgotten()
    {
        using var h = Server(3);
        var rig = new Rig(h, "alice");
        rig.Client.ResumeToken = Enumerable.Repeat((byte)7, 16).ToArray();
        rig.Client.Connect();
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Rejected, "reject", rigs: rig);
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, rig.Client.LastReject!.Reason);
        Assert.Null(rig.Client.ResumeToken);
        // Without the token the same client connects normally.
        Assert.True(rig.Client.Connect());
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Browsing, "second try", rigs: rig);
    }

    [GameFact]
    public void ADifferentDataFingerprintIsRejectedWithDataMismatch()
    {
        using var h = Server(4);
        var other = SessionHarness.Fingerprint.ToArray();
        other[0] ^= 0xFF;
        var rig = new Rig(h, "alice", fingerprint: other);
        rig.Client.Connect();
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Rejected, "reject", rigs: rig);
        Assert.Equal(NetRejectReason.DataMismatch, rig.Client.LastReject!.Reason);
    }

    [GameFact]
    public void AServerAtItsConnectionLimitRejectsTheClient()
    {
        using var h = Server(5, new SessionServerOptions { MaxConnections = 1 });
        Connected(h, "alice");
        var rig = new Rig(h, "bob");
        rig.Client.Connect();
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Rejected, "reject", rigs: rig);
        Assert.Equal(NetRejectReason.RaceFull, rig.Client.LastReject!.Reason);
    }

    [GameFact]
    public void AServerShutdownClosesTheSessionWithoutReconnecting()
    {
        using var h = Server(6);
        var alice = Creator(h);
        h.Server.Shutdown();
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Closed, "close", rigs: alice);
        Assert.Equal(NetRejectReason.ServerShutdown, alice.Client.LastError!.Reason);
        h.Run(2_000_000);
        alice.Client.Update(h.Net.Now);
        Assert.Single(alice.Links);
    }

    // ---------------- create / join / lobby ----------------

    [GameFact]
    public void CreatingAGameMakesTheCreatorOperatorWithTheChosenOptionsAndAToken()
    {
        using var h = Server(10);
        var alice = Creator(h, "alice", "night run", max: 5, track: 3, laps: 4, backward: true, bots: 2, traffic: true);
        var lobby = alice.Client.Lobby!;
        Assert.Equal("night run", lobby.GameName);
        Assert.Equal(5, lobby.MaxPlayers);
        Assert.Equal(3, lobby.Track);
        Assert.Equal(4, lobby.Laps);
        Assert.True(lobby.Backward);
        Assert.Equal(2, lobby.Bots);
        Assert.True(lobby.Traffic);
        Assert.Equal(NetGamePhase.Lobby, lobby.Phase);
        Assert.True(alice.Client.IsOperator);
        Assert.False(alice.Client.IsReady);
        Assert.Equal(NetProtocol.ResumeTokenLength, alice.Client.ResumeToken!.Length);
        Assert.Null(alice.Client.LastError);
        Assert.Empty(alice.Errors);   // the list broadcast that races the answer is not mistaken for a refusal
        Assert.False(alice.Client.Pending);
        Assert.Equal(h.Server.Games.Single().GameId, alice.Client.GameId);
    }

    [GameFact]
    public void JoiningAddsTheClientAsAPlayerAndBothSeeTheSameRoster()
    {
        using var h = Server(11);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 2, "alice sees bob", rigs: alice);
        Assert.False(bob.Client.IsOperator);
        Assert.Equal(["alice", "bob"], bob.Client.Lobby!.Members.Select(m => m.Name));
        Assert.Equal(["alice", "bob"], alice.Client.Lobby!.Members.Select(m => m.Name));
        Assert.NotEqual(alice.Client.Epoch, bob.Client.Epoch);
        Assert.NotEqual(alice.Client.ResumeToken, bob.Client.ResumeToken);
        Assert.Equal(alice.Client.GameId, bob.Client.GameId);
    }

    [GameFact]
    public void ReadinessAndCarSelectionReachEveryoneAndOnlyChangeTheCallersSeat()
    {
        using var h = Server(12);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Assert.True(bob.Client.SetReady(true));
        Assert.True(bob.Client.SetCar(5));
        Wait(h, () => alice.Client.Lobby!.Members.FirstOrDefault(m => m.Name == "bob") is { Ready: true, CarType: 5 }, "bob's choices", rigs: alice);
        Assert.True(bob.Client.IsReady);
        Assert.Equal(5, bob.Client.Me!.CarType);
        Assert.False(alice.Client.Lobby!.Members.Single(m => m.Name == "alice").Ready);

        Assert.True(bob.Client.SetReady(false));
        Wait(h, () => !bob.Client.IsReady && alice.Client.Lobby!.Members.Single(m => m.Name == "bob").Ready == false, "unready", rigs: [alice, bob]);
    }

    [GameFact]
    public void TheOperatorChangesTheOptionsAndEveryoneSeesThem()
    {
        using var h = Server(13);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Assert.True(alice.Client.SetOptions(6, 5, backward: true));
        Wait(h, () => bob.Client.Lobby!.Track == 6, "options", rigs: bob);
        Assert.Equal(5, bob.Client.Lobby!.Laps);
        Assert.True(bob.Client.Lobby.Backward);
        Wait(h, () => alice.Client.Lobby!.Track == 6, "operator state", rigs: alice);
        Assert.Equal(6, h.Server.Games.Single().Track);
        // The list advertises them too.
        Assert.Equal(6, h.Server.Games.Single().ToInfo().Track);
    }

    [GameFact]
    public void ANonOperatorCannotChangeOptionsOrStartAndNothingReachesTheServer()
    {
        using var h = Server(14);
        var alice = Creator(h, track: 1, laps: 1);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        long messages = h.Server.Stats.Messages;
        Assert.False(bob.Client.SetOptions(6, 5));
        Assert.Equal(SessionErrorKind.NotOperator, bob.Client.LastError!.Kind);
        Assert.False(bob.Client.StartRace());
        Assert.Equal(SessionErrorKind.NotOperator, bob.Errors.Last().Kind);
        h.Run(300_000);
        Assert.Equal(messages, h.Server.Stats.Messages);
        Assert.Equal(1, h.Server.Games.Single().Track);
        Assert.Equal(NetGamePhase.Lobby, h.Server.Games.Single().Phase);
    }

    [GameFact]
    public void TheOperatorRoleMovesToTheNextPlayerWhenTheOperatorLeavesAndTheClientFollowsIt()
    {
        using var h = Server(15);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Assert.True(alice.Client.LeaveGame());
        Assert.Equal(SessionClientPhase.Browsing, alice.Client.Phase);
        Wait(h, () => bob.Client.IsOperator, "operator handover", rigs: bob);
        Assert.Equal(["bob"], bob.Client.Lobby!.Members.Select(m => m.Name));
        Assert.True(bob.Client.SetOptions(4, 2));
        Wait(h, () => bob.Client.Lobby!.Track == 4, "new operator options", rigs: bob);
    }

    [GameFact]
    public void LeavingTheLobbyFreesTheSeatReturnsToTheListAndTheLastLeaverRemovesTheGame()
    {
        using var h = Server(16);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        int gameId = alice.Client.GameId;
        Assert.True(bob.Client.LeaveGame());
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.Null(bob.Client.Lobby);
        Assert.Null(bob.Client.ResumeToken);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 1, "bob gone", rigs: alice);
        Wait(h, () => bob.Client.Games.Count == 1, "list for bob", rigs: bob);

        Assert.True(alice.Client.LeaveGame());
        Wait(h, () => h.Server.GameCount == 0, "game removed", rigs: alice);
        Wait(h, () => bob.Client.Games.Count == 0, "bob sees it gone", rigs: bob);
        // bob can rejoin nothing; creating again works and the name is free.
        Assert.True(bob.Client.JoinGame(gameId));
        Wait(h, () => bob.Client.LastError is { Kind: SessionErrorKind.JoinRefused }, "refused join", rigs: bob);
        Assert.Contains("no longer exists", bob.Client.LastError!.Message);
    }

    // ---------------- server refusals ----------------

    [GameFact]
    public void CreatingAGameWithAUsedNameIsRefusedAndReported()
    {
        using var h = Server(20);
        Creator(h, "alice", "taken");
        var bob = Connected(h, "bob");
        Assert.True(bob.Client.CreateGame("Taken", 4));
        Wait(h, () => bob.Client.LastError is { Kind: SessionErrorKind.CreateRefused }, "refusal", rigs: bob);
        Assert.Contains("already exists", bob.Client.LastError!.Message);
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.False(bob.Client.Pending);
        Assert.Single(h.Server.Games);
        Assert.Equal(1, h.Server.Stats.RefusedCreates);
        // Still usable afterwards.
        Assert.True(bob.Client.CreateGame("free", 4));
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame, "created after refusal", rigs: bob);
        Assert.Null(bob.Client.LastError);
    }

    [GameFact]
    public void CreatingBeyondTheGameLimitIsRefused()
    {
        using var h = Server(21, new SessionServerOptions { MaxGames = 1 });
        Creator(h, "alice", "first");
        var bob = Connected(h, "bob");
        Assert.True(bob.Client.CreateGame("second", 4));
        Wait(h, () => bob.Client.LastError is { Kind: SessionErrorKind.CreateRefused }, "refusal", rigs: bob);
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.Single(h.Server.Games);
    }

    [GameFact]
    public void InvalidCreateArgumentsNeverReachTheServer()
    {
        using var h = Server(22);
        var bob = Connected(h, "bob");
        long messages = h.Server.Stats.Messages;
        Assert.False(bob.Client.CreateGame("g", 9));
        Assert.Equal(SessionErrorKind.InvalidArgument, bob.Client.LastError!.Kind);
        h.Run(200_000);
        Assert.Equal(messages, h.Server.Stats.Messages);
        Assert.Equal(0, h.Server.Stats.RefusedCreates);
    }

    [GameFact]
    public void JoiningAFullGameIsRefusedWithTheReason()
    {
        using var h = Server(23);
        var alice = Creator(h, max: 1);
        var bob = Connected(h, "bob");
        Wait(h, () => bob.Client.Games.Count == 1, "list", rigs: bob);
        Assert.True(bob.Client.JoinGame(alice.Client.GameId));
        Wait(h, () => bob.Client.LastError is { Kind: SessionErrorKind.JoinRefused }, "refusal", rigs: bob);
        Assert.Contains("full", bob.Client.LastError!.Message);
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.Equal(1, h.Server.Games.Single().MemberCount);
    }

    [GameFact]
    public void JoiningAnUnknownGameIsRefused()
    {
        using var h = Server(24);
        var bob = Connected(h, "bob");
        Assert.True(bob.Client.JoinGame(4242));
        Wait(h, () => bob.Client.LastError is { Kind: SessionErrorKind.JoinRefused }, "refusal", rigs: bob);
        Assert.Contains("no longer exists", bob.Client.LastError!.Message);
        Assert.Equal(1, bob.Client.Stats.RefusedJoins);
    }

    [GameFact]
    public void ANewPlayerCannotJoinAGameInCountdownOrRace()
    {
        using var h = Server(25);
        var alice = Creator(h);
        var carol = Connected(h, "carol");
        Assert.True(alice.Client.StartRace());
        Wait(h, () => alice.Client.Lobby!.Phase != NetGamePhase.Lobby, "countdown", rigs: alice);
        Wait(h, () => carol.Client.Games.Count == 1 && carol.Client.Games[0].Phase != NetGamePhase.Lobby, "list shows the phase", rigs: carol);
        Assert.True(carol.Client.JoinGame(alice.Client.GameId));
        Wait(h, () => carol.Client.LastError is { Kind: SessionErrorKind.JoinRefused }, "refusal", rigs: carol);
        Assert.Matches("Countdown|Race", carol.Client.LastError!.Message);
        Assert.Equal(SessionClientPhase.Browsing, carol.Client.Phase);
    }

    // ---------------- start / race hook ----------------

    [GameFact]
    public void TheOperatorStartsTheRaceAndRaceMessagesReachTheHookWhileOptionsAreFrozen()
    {
        using var h = Server(30);
        var alice = Creator(h, track: 1, laps: 1);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 2, "roster", rigs: alice);
        Assert.True(alice.Client.StartRace());
        Wait(h, () => bob.Client.Lobby!.Phase == NetGamePhase.Countdown, "countdown", rigs: bob);
        Assert.True(bob.Client.Lobby!.CountdownMillis > 0);
        Assert.False(alice.Client.SetOptions(2, 2));
        Assert.Equal(SessionErrorKind.WrongPhase, alice.Client.LastError!.Kind);
        Assert.False(alice.Client.StartRace());
        Assert.True(bob.Client.SetCar(3));   // still allowed during the countdown

        Wait(h, () => alice.Client.Lobby!.Phase == NetGamePhase.Race && bob.Client.Lobby!.Phase == NetGamePhase.Race &&
                      alice.Welcomes.Count == 1 && bob.Welcomes.Count == 1, "race", 10_000_000, alice, bob);
        Assert.Contains(NetMessageType.Welcome, alice.RaceTypes);
        var welcome = bob.Welcomes.Single();
        Assert.False(welcome.Resume);
        Assert.NotEqual(welcome.Seat, alice.Welcomes.Single().Seat);
        Assert.Equal(welcome.RaceId, alice.Welcomes.Single().RaceId);
        Assert.False(bob.Client.SetReady(true));
        Assert.Equal(SessionErrorKind.WrongPhase, bob.Client.LastError!.Kind);
        Assert.Equal(0, alice.Client.Stats.Malformed);
        // The race client of task 4.2 answers on the same connection through this transport.
        long messages = h.Server.Stats.Messages;
        alice.Client.RaceTransport.Send(NetChannel.ReliableOrdered, new NetReady(welcome.RaceId, welcome.Epoch, welcome.SetupHash).Encode());
        h.Run(100_000);
        Assert.True(h.Server.Stats.Messages > messages);
    }

    // ---------------- resume / reconnect ----------------

    static Rig RaceOf(SessionHarness h, out Rig bob, SessionClientOptions? options = null, string aliceName = "alice")
    {
        var alice = new Rig(h, aliceName, options);
        alice.Client.Connect();
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Browsing, "alice connected", rigs: alice);
        alice.Client.CreateGame("game", 4);
        Wait(h, () => alice.Client.Phase == SessionClientPhase.InGame, "create", rigs: alice);
        bob = Joiner(h, "bob", alice.Client.GameId);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 2, "roster", rigs: alice);
        Assert.True(alice.Client.StartRace());
        var b = bob;
        Wait(h, () => alice.Welcomes.Count == 1 && b.Welcomes.Count == 1 && b.Client.Lobby!.Phase == NetGamePhase.Race, "race", 10_000_000, alice, bob);
        return alice;
    }

    [GameFact]
    public void ALostLinkInTheRaceReconnectsAutomaticallyAndResumesTheSameSeat()
    {
        using var h = Server(40);
        var alice = RaceOf(h, out var bob);
        var token = alice.Client.ResumeToken!.ToArray();
        int slot = alice.Client.Me!.Slot;
        long raceId = alice.Welcomes[0].RaceId;
        uint oldEpoch = alice.Client.Epoch;

        alice.Link.Cut();
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Reconnecting, "loss noticed", rigs: [alice, bob]);
        Assert.Equal(SessionErrorKind.ConnectionLost, alice.Errors.Last().Kind);
        Wait(h, () => alice.Reconnects.Count == 1 && alice.Welcomes.Count == 2 && alice.Client.Phase == SessionClientPhase.InGame,
            "resume", 5_000_000, alice, bob);

        Assert.Equal([true], alice.Reconnects);
        Assert.True(alice.Welcomes[1].Resume);
        Assert.Equal(raceId, alice.Welcomes[1].RaceId);
        Assert.NotEqual(alice.Welcomes[0].Epoch, alice.Welcomes[1].Epoch);
        Assert.Equal(slot, alice.Client.Me!.Slot);
        Assert.True(alice.Client.Me.Connected);
        Assert.Equal(NetGamePhase.Race, alice.Client.Lobby!.Phase);
        Assert.NotEqual(oldEpoch, alice.Client.Epoch);
        Assert.Equal(token, alice.Client.ResumeToken);
        Assert.Equal(1, h.Server.Stats.Resumed);
        Assert.Equal(1, h.Server.GameCount);
        Assert.Equal(2, h.Server.Games.Single().MemberCount);
        Assert.Equal(2, alice.Links.Count);
        Wait(h, () => bob.Client.Lobby!.Members.All(m => m.Connected), "bob sees alice back", rigs: bob);
    }

    [GameFact]
    public void AnOperatorWhoLostTheLinkInTheCountdownComesBackAsAPlayerAndTheRaceStillStarts()
    {
        using var h = Server(41, new SessionServerOptions { CountdownMicros = 3_000_000, MaxRaceSteps = 200_000, GraceMicros = 30_000_000 });
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 2, "roster", rigs: alice);
        alice.Client.StartRace();
        Wait(h, () => alice.Client.Lobby!.Phase == NetGamePhase.Countdown, "countdown", rigs: alice);
        alice.Link.Cut();
        Wait(h, () => bob.Client.IsOperator, "bob takes over", rigs: [alice, bob]);
        Wait(h, () => alice.Reconnects.Count == 1 && alice.Client.Phase == SessionClientPhase.InGame, "alice back", 5_000_000, alice, bob);
        Assert.True(alice.Client.Me!.Connected);
        Assert.False(alice.Client.IsOperator);
        Assert.True(bob.Client.IsOperator);
        Wait(h, () => alice.Client.Lobby!.Phase == NetGamePhase.Race, "race", 10_000_000, alice, bob);
    }

    [GameFact]
    public void ALostLinkInTheLobbyReconnectsToTheListWithTheSeatGone()
    {
        using var h = Server(42);
        var alice = Creator(h);
        var bob = Joiner(h, "bob", alice.Client.GameId);
        int gameId = alice.Client.GameId;
        bob.Link.Cut();
        Wait(h, () => bob.Reconnects.Count == 1 && bob.Client.Phase == SessionClientPhase.Browsing, "bob back on the list", 5_000_000, alice, bob);
        Assert.Equal([false], bob.Reconnects);
        Assert.Null(bob.Client.Lobby);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 1, "seat freed", rigs: alice);
        // The lobby seat is a fresh join, as the spec says.
        Assert.True(bob.Client.JoinGame(gameId));
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame, "rejoin", rigs: bob);
        Assert.False(bob.Client.IsReady);
        Wait(h, () => alice.Client.Lobby!.Members.Count == 2, "alice sees the new member", rigs: alice);
    }

    [GameFact]
    public void AClientThatLeftTheRaceRejoinsFromTheListAndResumes()
    {
        using var h = Server(43);
        var alice = RaceOf(h, out var bob);
        Assert.True(bob.Client.LeaveGame());
        Assert.Equal(SessionClientPhase.Browsing, bob.Client.Phase);
        Assert.NotNull(bob.Client.ResumeToken);
        Wait(h, () => bob.Client.Games.Count == 1 && bob.Client.Games[0].Phase == NetGamePhase.Race, "list", rigs: bob);
        Assert.True(bob.Client.JoinGame(bob.Client.Games[0].GameId));
        Wait(h, () => bob.Client.Phase == SessionClientPhase.InGame && bob.Welcomes.Count == 2, "resumed", 5_000_000, alice, bob);
        Assert.True(bob.Welcomes[1].Resume);
        Assert.Null(bob.Client.LastError);
        Assert.Single(bob.Links);
    }

    [GameFact]
    public void ARestartedClientResumesByNameAloneAndAWrongTokenIsRefused()
    {
        using var h = Server(44);
        var alice = RaceOf(h, out var bob, new SessionClientOptions { AutoReconnect = false });
        var token = alice.Client.ResumeToken!.ToArray();
        alice.Link.Cut();
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Closed, "client gave up", rigs: [alice, bob]);
        Assert.Equal(SessionErrorKind.ConnectionLost, alice.Client.LastError!.Kind);
        Wait(h, () => bob.Client.Lobby!.Members.Any(m => !m.Connected), "server noticed", rigs: bob);

        // Someone presenting another reservation's token for that name is refused and changes nothing.
        var impostor = new Rig(h, "alice");
        impostor.Client.ResumeToken = bob.Client.ResumeToken;
        impostor.Client.Connect();
        Wait(h, () => impostor.Client.Phase == SessionClientPhase.Rejected, "impostor rejected", rigs: [impostor, bob]);
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, impostor.Client.LastReject!.Reason);
        Assert.Contains(bob.Client.Lobby!.Members, m => !m.Connected);

        // A restarted client (no token) with the reserved name is resumed.
        var restarted = new Rig(h, "alice");
        Assert.Null(restarted.Client.ResumeToken);
        restarted.Client.Connect();
        Wait(h, () => restarted.Client.Phase == SessionClientPhase.InGame && restarted.Welcomes.Count == 1, "resume by name", 5_000_000, restarted, bob);
        Assert.True(restarted.Welcomes[0].Resume);
        Assert.Equal(token, restarted.Client.ResumeToken);
        Assert.True(restarted.Client.Me!.Connected);
    }

    [GameFact]
    public void TheReconnectWindowEndsTheSessionWhenTheServerCannotBeReached()
    {
        using var h = Server(46);
        var rig = new Rig(h, "carol", new SessionClientOptions { ReconnectWindowMicros = 1_500_000, RetryIntervalMicros = 200_000 });
        rig.Client.Connect();
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Browsing, "connected", rigs: rig);
        rig.Offline = true;
        rig.Link.Cut();
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Reconnecting, "loss", rigs: rig);
        // Attempts that go nowhere simply hang until the window is over.
        Wait(h, () => rig.Client.Phase == SessionClientPhase.Closed, "window over", 5_000_000, rig);
        Assert.Equal(SessionErrorKind.ReconnectExpired, rig.Client.LastError!.Kind);
        int attempts = rig.Links.Count;
        h.Run(2_000_000);
        rig.Client.Update(h.Net.Now);
        Assert.Equal(attempts, rig.Links.Count);
    }

    [GameFact]
    public void ADeliberateDisconnectReleasesTheNameAndTheClientDoesNotComeBack()
    {
        using var h = Server(47);
        var alice = Creator(h);
        alice.Client.Disconnect();
        Wait(h, () => h.Server.ConnectionCount == 0, "server notices", rigs: alice);
        Assert.Equal(SessionClientPhase.Closed, alice.Client.Phase);
        Wait(h, () => h.Server.GameCount == 0, "empty lobby removed", rigs: alice);
        h.Run(1_000_000);
        alice.Client.Update(h.Net.Now);
        Assert.Single(alice.Links);
        // The name is free again, and the same client object starts a fresh session.
        Assert.True(alice.Client.Connect());
        Wait(h, () => alice.Client.Phase == SessionClientPhase.Browsing, "second session", rigs: alice);
    }

    [GameFact]
    public void TwoGamesRunIndependentlyOnOneServerAndEachClientSeesOnlyItsOwn()
    {
        using var h = Server(48);
        var a = Creator(h, "alice", "one", track: 1);
        var b = Creator(h, "bob", "two", track: 4);
        Assert.NotEqual(a.Client.GameId, b.Client.GameId);
        Assert.True(a.Client.SetReady(true));
        Wait(h, () => a.Client.IsReady, "alice ready", rigs: a);
        Assert.False(b.Client.IsReady);
        Assert.Equal("two", b.Client.Lobby!.GameName);
        Assert.Equal(4, b.Client.Lobby.Track);
        var c = Connected(h, "carol");
        Wait(h, () => c.Client.Games.Count == 2, "both listed", rigs: c);
        Assert.Equal(["one", "two"], c.Client.Games.Select(g => g.Name).Order());
    }
}
