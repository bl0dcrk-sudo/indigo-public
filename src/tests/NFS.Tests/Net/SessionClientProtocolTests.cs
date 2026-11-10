using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Tests.Net;

/// <summary>The session client against a scripted transport (no server): what it sends for every operation, which operations
/// it refuses locally, how it reads the server's messages and how it treats foreign, stale and malformed ones.</summary>
public class SessionClientProtocolTests
{
    static readonly byte[] Fingerprint = new byte[32];

    sealed class Rig
    {
        public readonly List<INetClientHandler> Handlers = [];
        public readonly List<byte[]> Hellos = [];
        public readonly List<(NetChannel Channel, byte[] Data)> Sent = [];
        public readonly List<SessionError> Errors = [];
        public readonly List<SessionClientPhase> Phases = [];
        public readonly List<(NetChannel Channel, byte[] Data)> Race = [];
        public long Now;
        public SessionClient Client = null!;
        public INetClientHandler Handler => Handlers[^1];

        public Rig(string name = "alice", SessionClientOptions? options = null)
        {
            Client = new SessionClient(Fingerprint, name, (handler, hello) =>
            {
                Handlers.Add(handler);
                Hellos.Add(hello);
                return new Transport(this);
            }, () => Now, options);
            Client.Error += Errors.Add;
            Client.PhaseChanged += Phases.Add;
            Client.RaceMessage += (c, d) => Race.Add((c, d.ToArray()));
        }

        public void Receive(byte[] data, NetChannel channel = NetChannel.ReliableOrdered) => Handler.OnReceive(channel, data);

        public void Browse(params NetGameInfo[] games)
        {
            Client.Connect();
            Handler.OnConnected();
            Receive(new NetGameList(games).Encode());
        }

        public int SentCount => Sent.Count;

        /// <summary>A refusal as the server sends it: the plain list, then the reply to the list request that follows the request.</summary>
        public void Refuse(params NetGameInfo[] games)
        {
            Receive(new NetGameList(games).Encode());
            Receive(new NetGameList(games).Encode());
        }

        sealed class Transport(Rig rig) : INetClientTransport
        {
            public void Send(NetChannel channel, byte[] data) => rig.Sent.Add((channel, data));
            public void Disconnect() { }
            public long RoundTripMicros => 0;
        }
    }

    static NetGameInfo Info(int id, string name, NetGamePhase phase = NetGamePhase.Lobby, int players = 1, int max = 4) =>
        new(id, name, phase, players, max, 1, 2, NetGameMode.SingleRace);

    static NetLobbyState Lobby(int gameId, uint epoch, string game, string me, bool op, NetGamePhase phase = NetGamePhase.Lobby,
        byte[]? token = null, params string[] others)
    {
        var members = new List<NetLobbyMember> { new(0, op ? me : others[0], false, -1, true, true) };
        int slot = 1;
        if (op) foreach (var o in others) members.Add(new NetLobbyMember(slot++, o, false, -1, false, true));
        else
        {
            members.Add(new NetLobbyMember(slot++, me, false, -1, false, true));
            foreach (var o in others.Skip(1)) members.Add(new NetLobbyMember(slot++, o, false, -1, false, true));
        }
        return new NetLobbyState(gameId, epoch, game, phase, 1, 2, 4, NetGameMode.SingleRace,
            phase == NetGamePhase.Countdown ? 3000 : 0, members, token ?? new byte[16]);
    }

    delegate T Decoder<T>(NetHeader h, ref WireReader r);

    static NetHeader Header(byte[] data)
    {
        var r = new WireReader(data);
        return NetHeader.Read(ref r);
    }

    /// <summary>The last request that is not the list request following create/join.</summary>
    static T LastSent<T>(Rig rig, NetMessageType type, Decoder<T> decode)
    {
        var data = rig.Sent.Last(m => Header(m.Data).Type != NetMessageType.GameListRequest || type == NetMessageType.GameListRequest).Data;
        var r = new WireReader(data);
        var h = NetHeader.Read(ref r);
        Assert.Equal(type, h.Type);
        return decode(h, ref r);
    }

    static Rig InLobby(bool op = true, NetGamePhase phase = NetGamePhase.Lobby)
    {
        var rig = new Rig();
        rig.Browse();
        if (op) rig.Client.CreateGame("g", 4);
        else rig.Client.JoinGame(7);
        rig.Receive(Lobby(7, 99, "g", "alice", op, phase, others: op ? ["bob"] : ["boss"]).Encode());
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        rig.Sent.Clear();
        return rig;
    }

    [Fact]
    public void ConnectSendsAHelloWithNameVersionsAndTokenAndWaitsForTheFirstMessage()
    {
        var rig = new Rig("alice");
        var token = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        rig.Client.ResumeToken = token;
        Assert.True(rig.Client.Connect());
        Assert.Equal(SessionClientPhase.Connecting, rig.Client.Phase);
        var hello = NetHello.Decode(rig.Hellos.Single());
        Assert.Equal("alice", hello.Name);
        Assert.Equal(NetProtocol.Version, hello.Version);
        Assert.Equal(BuildInfo.AppVersion, hello.AppVersion);
        Assert.Equal(token, hello.ResumeToken);
        Assert.False(rig.Client.Connected);

        rig.Handler.OnConnected();
        Assert.Equal(SessionClientPhase.Connecting, rig.Client.Phase);
        rig.Receive(new NetGameList([Info(1, "one")]).Encode());
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.Equal("one", Assert.Single(rig.Client.Games).Name);
    }

    [Fact]
    public void ConstructorRejectsABadNameAndFingerprint()
    {
        Assert.Throws<ArgumentException>(() => new SessionClient(Fingerprint, "", (_, _) => null!, () => 0));
        Assert.Throws<ArgumentException>(() => new SessionClient(Fingerprint, new string('x', NetProtocol.MaxNameLength + 1), (_, _) => null!, () => 0));
        Assert.Throws<ArgumentException>(() => new SessionClient(new byte[5], "alice", (_, _) => null!, () => 0));
    }

    [Fact]
    public void ARejectBeforeTheFirstMessageEndsTheSessionWithTheServersReason()
    {
        var rig = new Rig();
        rig.Client.Connect();
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.AppVersionMismatch).Encode());
        Assert.Equal(SessionClientPhase.Rejected, rig.Client.Phase);
        Assert.Equal(NetRejectReason.AppVersionMismatch, rig.Client.LastReject!.Reason);
        Assert.Equal(SessionErrorKind.Rejected, rig.Client.LastError!.Kind);
        Assert.Equal(NetRejectReason.AppVersionMismatch, rig.Client.LastError.Reason);
        Assert.Equal(rig.Client.LastError, rig.Errors.Last());
        Assert.False(rig.Client.CreateGame("g", 4));
        Assert.Equal(SessionErrorKind.NotConnected, rig.Client.LastError!.Kind);
    }

    [Fact]
    public void AnUnreachableServerIsATimeoutRejectOfTheFirstConnection()
    {
        var rig = new Rig();
        rig.Client.Connect();
        rig.Handler.OnDisconnected(null);
        Assert.Equal(SessionClientPhase.Rejected, rig.Client.Phase);
        Assert.Equal(NetRejectReason.Timeout, rig.Client.LastReject!.Reason);
        Assert.Single(rig.Hellos);   // no reconnect before the first answer
    }

    [Fact]
    public void ARejectedClientMayStartOverAndKeepsTheTokenUnlessItWasInvalid()
    {
        var rig = new Rig();
        var token = Enumerable.Repeat((byte)5, 16).ToArray();
        rig.Client.ResumeToken = token;
        rig.Client.Connect();
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.NameTaken).Encode());
        Assert.Equal(token, rig.Client.ResumeToken);
        Assert.True(rig.Client.Connect());
        Assert.Equal(token, NetHello.Decode(rig.Hellos[1]).ResumeToken);

        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.ResumeTokenInvalid).Encode());
        Assert.Equal(SessionClientPhase.Rejected, rig.Client.Phase);
        Assert.Null(rig.Client.ResumeToken);
        Assert.True(rig.Client.Connect());
        Assert.Null(NetHello.Decode(rig.Hellos[2]).ResumeToken);
    }

    [Fact]
    public void ConnectTwiceIsRefused()
    {
        var rig = new Rig();
        Assert.True(rig.Client.Connect());
        Assert.False(rig.Client.Connect());
        Assert.Equal(SessionErrorKind.WrongPhase, rig.Client.LastError!.Kind);
        Assert.Single(rig.Hellos);
    }

    [Fact]
    public void CreateGameSendsOnlyNameLimitAndModeAndTheLobbyStateAnswers()
    {
        var rig = new Rig();
        rig.Browse();
        Assert.True(rig.Client.CreateGame("night run", 5, NetGameMode.Knockout));
        Assert.True(rig.Client.Pending);
        var create = LastSent(rig, NetMessageType.GameCreate, NetGameCreate.Decode);
        Assert.Equal(new NetGameCreate("night run", 5, NetGameMode.Knockout), create);
        Assert.Equal(NetSessionProtocol.SessionRaceId, Header(rig.Sent.Last().Data).RaceId);
        Assert.Equal(NetMessageType.GameListRequest, Header(rig.Sent.Last().Data).Type);   // the answer-separating list request

        var token = Enumerable.Repeat((byte)9, 16).ToArray();
        rig.Receive(Lobby(3, 77, "night run", "alice", true, token: token).Encode());
        Assert.False(rig.Client.Pending);
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        Assert.Equal(3, rig.Client.GameId);
        Assert.Equal(77u, rig.Client.Epoch);
        Assert.True(rig.Client.IsOperator);
        Assert.Equal(token, rig.Client.ResumeToken);
        Assert.Null(rig.Client.LastError);
    }

    [Fact]
    public void CreateGameWithInvalidArgumentsSendsNothing()
    {
        var rig = new Rig();
        rig.Browse();
        int sent = rig.SentCount;
        Assert.False(rig.Client.CreateGame("", 4));
        Assert.False(rig.Client.CreateGame("g", 0));
        Assert.False(rig.Client.CreateGame("g", NetProtocol.MaxSeats + 1));
        Assert.False(rig.Client.CreateGame("g", 4, (NetGameMode)0));
        Assert.False(rig.Client.CreateGame(new string('x', NetProtocol.MaxGameNameLength + 1), 4));
        Assert.Equal(sent, rig.SentCount);
        Assert.Equal(SessionErrorKind.InvalidArgument, rig.Client.LastError!.Kind);
        Assert.False(rig.Client.Pending);
        Assert.Equal(5, rig.Errors.Count);
        Assert.True(rig.Client.CreateGame("g", NetProtocol.MaxSeats));   // the edge values are fine
        Assert.True(rig.Client.Pending);
    }

    [Fact]
    public void ARefusedCreateIsRecognisedFromTheListAndExplainedWhenTheNameIsUsed()
    {
        var rig = new Rig();
        rig.Browse(Info(1, "taken"));
        Assert.True(rig.Client.CreateGame("TAKEN", 4));
        rig.Refuse(Info(1, "taken"));
        Assert.False(rig.Client.Pending);
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.Equal(SessionErrorKind.CreateRefused, rig.Client.LastError!.Kind);
        Assert.Contains("TAKEN", rig.Client.LastError.Message);
        Assert.Equal(1, rig.Client.Stats.RefusedCreates);

        // Another attempt is possible at once, and a refusal without a clash says the server decided (limit).
        Assert.True(rig.Client.CreateGame("fresh", 4));
        rig.Refuse(Info(1, "taken"));
        Assert.Equal(SessionErrorKind.CreateRefused, rig.Client.LastError!.Kind);
        Assert.DoesNotContain("fresh' already", rig.Client.LastError.Message);
        Assert.Equal(2, rig.Client.Stats.RefusedCreates);
    }

    [Fact]
    public void ARequestWaitingForTheServerBlocksTheNextOne()
    {
        var rig = new Rig();
        rig.Browse(Info(1, "one"));
        Assert.True(rig.Client.JoinGame(1));
        int sent = rig.SentCount;
        Assert.False(rig.Client.JoinGame(1));
        Assert.Equal(SessionErrorKind.Busy, rig.Client.LastError!.Kind);
        Assert.False(rig.Client.CreateGame("x", 4));
        Assert.Equal(SessionErrorKind.Busy, rig.Client.LastError!.Kind);
        Assert.Equal(sent, rig.SentCount);
    }

    [Theory]
    [InlineData(NetGamePhase.Lobby, 4, "full")]
    [InlineData(NetGamePhase.Race, 2, "Race")]
    [InlineData(NetGamePhase.Countdown, 2, "Countdown")]
    public void ARefusedJoinIsExplainedFromTheListThatAnswers(NetGamePhase phase, int players, string expected)
    {
        var rig = new Rig();
        rig.Browse(Info(5, "game", NetGamePhase.Lobby, 2));
        Assert.True(rig.Client.JoinGame(5));
        var joined = LastSent(rig, NetMessageType.GameJoin, NetGameJoin.Decode);
        Assert.Equal(5, joined.GameId);
        rig.Refuse(Info(5, "game", phase, players));
        Assert.Equal(SessionErrorKind.JoinRefused, rig.Client.LastError!.Kind);
        Assert.Contains(expected, rig.Client.LastError.Message);
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.False(rig.Client.Pending);
    }

    [Fact]
    public void JoiningAGameThatVanishedIsRefusedWithAReadableReason()
    {
        var rig = new Rig();
        rig.Browse(Info(5, "game"));
        rig.Client.JoinGame(5);
        rig.Refuse();
        Assert.Equal(SessionErrorKind.JoinRefused, rig.Client.LastError!.Kind);
        Assert.Contains("no longer exists", rig.Client.LastError.Message);
        Assert.False(rig.Client.JoinGame(0));
        Assert.Equal(SessionErrorKind.InvalidArgument, rig.Client.LastError!.Kind);
    }

    [Fact]
    public void ALobbyStateAfterBurstOfStaleListsSupersedesTheApparentRefusal()
    {
        // One stale broadcast list overtakes the answer: nothing is reported. Two of them look like a refusal until the lobby
        // state of the join arrives.
        var rig = new Rig();
        rig.Browse(Info(5, "game"));
        rig.Client.JoinGame(5);
        rig.Receive(new NetGameList([Info(5, "game")]).Encode());
        Assert.Null(rig.Client.LastError);
        Assert.True(rig.Client.Pending);
        rig.Receive(new NetGameList([Info(5, "game")]).Encode());
        Assert.Equal(SessionErrorKind.JoinRefused, rig.Client.LastError!.Kind);
        rig.Receive(Lobby(5, 11, "game", "alice", false, others: ["boss"]).Encode());
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        Assert.Null(rig.Client.LastError);
        Assert.Equal(5, rig.Client.GameId);
    }

    [Fact]
    public void LobbyStatesOfOtherGamesAreIgnoredUntilAskedFor()
    {
        var rig = new Rig();
        rig.Browse(Info(5, "game"));
        rig.Receive(Lobby(9, 3, "other", "alice", false, others: ["boss"]).Encode());
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.Null(rig.Client.Lobby);
        Assert.Equal(1, rig.Client.Stats.StaleLobbyStates);

        rig.Client.JoinGame(5);
        rig.Receive(Lobby(9, 3, "other", "alice", false, others: ["boss"]).Encode());
        Assert.Null(rig.Client.Lobby);
        Assert.True(rig.Client.Pending);
        rig.Receive(Lobby(5, 4, "game", "alice", false, others: ["boss"]).Encode());
        Assert.Equal(5, rig.Client.GameId);
    }

    [Fact]
    public void LeavingSendsTheCurrentGameAndEpochAndStaleStatesOfThatGameAreIgnored()
    {
        var rig = InLobby();
        var token = rig.Client.ResumeToken;
        Assert.True(rig.Client.LeaveGame());
        var leave = LastSent(rig, NetMessageType.GameLeave, NetGameLeave.Decode);
        Assert.Equal(new NetGameLeave(7, 99), leave);
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.Null(rig.Client.Lobby);
        Assert.Null(rig.Client.ResumeToken);   // a lobby seat is freed by leaving: the token is useless
        Assert.NotNull(token);

        rig.Receive(Lobby(7, 99, "g", "alice", true, others: ["bob"]).Encode());   // sent before the server saw the leave
        Assert.Null(rig.Client.Lobby);
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);

        rig.Client.JoinGame(7);   // the same game again: only the fresh epoch is accepted
        rig.Receive(Lobby(7, 99, "g", "alice", true, others: ["bob"]).Encode());
        Assert.Null(rig.Client.Lobby);
        rig.Receive(Lobby(7, 100, "g", "alice", false, others: ["bob"]).Encode());
        Assert.Equal(100u, rig.Client.Epoch);
    }

    [Fact]
    public void LeavingARunningRaceKeepsTheTokenForTheReservedSeat()
    {
        var rig = InLobby(phase: NetGamePhase.Race);
        var token = rig.Client.ResumeToken;
        Assert.True(rig.Client.LeaveGame());
        Assert.Equal(token, rig.Client.ResumeToken);
    }

    [Fact]
    public void LobbyOperationsOutsideAGameAreRefusedLocally()
    {
        var rig = new Rig();
        rig.Browse();
        int sent = rig.SentCount;
        Assert.False(rig.Client.SetReady(true));
        Assert.False(rig.Client.SetCar(3));
        Assert.False(rig.Client.SetOptions(1, 1));
        Assert.False(rig.Client.StartRace());
        Assert.False(rig.Client.LeaveGame());
        Assert.False(rig.Client.ReturnToLobby());
        Assert.Equal(SessionErrorKind.NotInGame, rig.Client.LastError!.Kind);
        Assert.Equal(sent, rig.SentCount);
        Assert.Equal(6, rig.Client.Stats.LocalRefusals);
    }

    [Fact]
    public void ReadyCarOptionsStartAndReturnCarryTheGameIdAndEpochOfTheLobby()
    {
        var rig = InLobby();
        Assert.True(rig.Client.SetReady(true));
        var ready = LastSent(rig, NetMessageType.LobbyReady, NetLobbyReady.Decode);
        Assert.Equal(new NetLobbyReady(7, 99, true), ready);

        Assert.True(rig.Client.SetCar(NetProtocol.MaxCarType, 33));
        var car = LastSent(rig, NetMessageType.LobbyCar, NetLobbyCar.Decode);
        Assert.Equal(new NetLobbyCar(7, 99, NetProtocol.MaxCarType, 33), car);

        Assert.True(rig.Client.SetOptions(5, 3, backward: true, bots: 2, traffic: true, skill: 0, catchUp: false, style: 2));
        var options = LastSent(rig, NetMessageType.LobbyOptions, NetLobbyOptions.Decode);
        Assert.Equal(new NetLobbyOptions(7, 99, 5, 3, true, 2, true, 0, false, 2), options);

        Assert.True(rig.Client.StartRace());
        var start = LastSent(rig, NetMessageType.LobbyStart, NetLobbyStart.Decode);
        Assert.Equal(new NetLobbyStart(7, 99), start);

        Assert.True(rig.Client.ReturnToLobby());
        var back = LastSent(rig, NetMessageType.LobbyReturn, NetLobbyReturn.Decode);
        Assert.Equal(new NetLobbyReturn(7, 99), back);
        Assert.All(rig.Sent, s => Assert.Equal(NetChannel.ReliableOrdered, s.Channel));
    }

    [Fact]
    public void CancelStartIsTheOperatorsCountdownOnlyMessage()
    {
        var lobby = InLobby();
        Assert.False(lobby.Client.CancelStart());
        Assert.Equal(SessionErrorKind.WrongPhase, lobby.Client.LastError!.Kind);
        Assert.Empty(lobby.Sent);

        var player = InLobby(op: false, phase: NetGamePhase.Countdown);
        Assert.False(player.Client.CancelStart());
        Assert.Equal(SessionErrorKind.NotOperator, player.Client.LastError!.Kind);
        Assert.Empty(player.Sent);

        var rig = InLobby(phase: NetGamePhase.Countdown);
        Assert.True(rig.Client.CancelStart());
        Assert.Equal(new NetLobbyCancel(7, 99), LastSent(rig, NetMessageType.LobbyCancel, NetLobbyCancel.Decode));
    }

    [Fact]
    public void OnlyTheOperatorSendsOptionsAndStart()
    {
        var rig = InLobby(op: false);
        Assert.False(rig.Client.IsOperator);
        Assert.False(rig.Client.SetOptions(2, 2));
        Assert.Equal(SessionErrorKind.NotOperator, rig.Client.LastError!.Kind);
        Assert.False(rig.Client.StartRace());
        Assert.Equal(SessionErrorKind.NotOperator, rig.Client.LastError!.Kind);
        Assert.Empty(rig.Sent);
        // Readiness and the car are everybody's own business.
        Assert.True(rig.Client.SetReady(true));
        Assert.True(rig.Client.SetCar(1));

        // The operator role moves with the server's state: after the handover the client may change options.
        var members = new[] { new NetLobbyMember(1, "alice", false, -1, true, true) };
        rig.Receive(new NetLobbyState(7, 99, "g", NetGamePhase.Lobby, 1, 2, 4, NetGameMode.SingleRace, 0, members).Encode());
        Assert.True(rig.Client.IsOperator);
        Assert.True(rig.Client.SetOptions(2, 2));
    }

    [Fact]
    public void OptionsAndStartAreRefusedOutsideTheLobbyPhase()
    {
        var rig = InLobby(phase: NetGamePhase.Countdown);
        Assert.False(rig.Client.SetOptions(2, 2));
        Assert.Equal(SessionErrorKind.WrongPhase, rig.Client.LastError!.Kind);
        Assert.False(rig.Client.StartRace());
        Assert.False(rig.Client.SetReady(true));
        Assert.Empty(rig.Sent);
        Assert.True(rig.Client.SetCar(2));   // the car may still change during the countdown

        rig.Receive(Lobby(7, 99, "g", "alice", true, NetGamePhase.Race, others: ["bob"]).Encode());
        Assert.False(rig.Client.SetCar(3));
        Assert.Equal(SessionErrorKind.WrongPhase, rig.Client.LastError!.Kind);
    }

    [Fact]
    public void InvalidOptionAndCarValuesAreRefusedLocally()
    {
        var rig = InLobby();
        Assert.False(rig.Client.SetOptions(99, 1));
        Assert.False(rig.Client.SetOptions(1, 99));
        Assert.False(rig.Client.SetCar(0x7F));
        Assert.False(rig.Client.SetCar(-1));
        Assert.Equal(SessionErrorKind.InvalidArgument, rig.Client.LastError!.Kind);
        Assert.Empty(rig.Sent);
    }

    [Fact]
    public void CreatingOrJoiningWhileInAGameIsRefused()
    {
        var rig = InLobby();
        Assert.False(rig.Client.CreateGame("another", 4));
        Assert.Equal(SessionErrorKind.WrongPhase, rig.Client.LastError!.Kind);
        Assert.False(rig.Client.JoinGame(3));
        Assert.Empty(rig.Sent);
        Assert.True(rig.Client.RequestGames());   // the list can still be read in a game
        Assert.Equal(NetMessageType.GameListRequest, Header(rig.Sent.Single().Data).Type);
        rig.Receive(new NetGameList([Info(7, "g")]).Encode());
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        Assert.Single(rig.Client.Games);
    }

    [Fact]
    public void EveryLobbyStateReplacesTheOldOneAndIsAnnounced()
    {
        var rig = InLobby();
        var seen = new List<NetLobbyState>();
        rig.Client.LobbyChanged += seen.Add;
        var ready = new NetLobbyState(7, 99, "g", NetGamePhase.Lobby, 4, 5, 4, NetGameMode.SingleRace, 0,
            [new NetLobbyMember(0, "alice", true, 3, true, true)], new byte[16], Backward: true, Bots: 3, Traffic: true);
        rig.Receive(ready.Encode());
        Assert.Single(seen);
        Assert.Equal(4, rig.Client.Lobby!.Track);
        Assert.Equal(5, rig.Client.Lobby.Laps);
        Assert.True(rig.Client.Lobby.Backward);
        Assert.Equal(3, rig.Client.Lobby.Bots);
        Assert.True(rig.Client.Lobby.Traffic);
        Assert.True(rig.Client.IsReady);
        Assert.Equal(3, rig.Client.Me!.CarType);
    }

    [Fact]
    public void RaceMessagesAreForwardedUnchangedAndOtherMessageTypesAreNotTreatedAsRace()
    {
        var rig = InLobby();
        var start = new NetStart(1_000_000, 5, 10, 500_000).Encode();
        rig.Receive(start, NetChannel.ReliableUnordered);
        var ack = NetResultsAck.Encode(1_000_000, 5);
        rig.Receive(ack);
        Assert.Equal(2, rig.Race.Count);
        Assert.Equal(start, rig.Race[0].Data);
        Assert.Equal(NetChannel.ReliableUnordered, rig.Race[0].Channel);
        Assert.Equal(ack, rig.Race[1].Data);
        Assert.Equal(2, rig.Client.Stats.RaceMessages);
        Assert.Equal(0, rig.Client.Stats.Malformed);

        // A client→server message type from the server is a protocol violation: counted, no state change.
        rig.Receive(new NetLobbyStart(7, 99).Encode());
        rig.Receive(new NetGameListRequest().Encode());
        Assert.Equal(2, rig.Client.Stats.Malformed);
        Assert.Equal(2, rig.Race.Count);
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
    }

    [Fact]
    public void RaceMessagesWithoutAHookAreCounted()
    {
        var rig = new Rig();
        rig.Client = new SessionClient(Fingerprint, "alice", (h, _) => { rig.Handlers.Add(h); return null!; }, () => 0);
        rig.Client.Connect();
        rig.Receive(new NetStart(1_000_000, 5, 10, 500_000).Encode());
        Assert.Equal(1, rig.Client.Stats.DroppedRaceMessages);
    }

    [Fact]
    public void MalformedAndTruncatedMessagesAreCountedAndDoNotChangeState()
    {
        var rig = InLobby();
        var good = Lobby(7, 99, "g", "alice", true, others: ["bob"]).Encode();
        rig.Receive(good[..^3]);
        rig.Receive([1, 2]);
        rig.Receive(new byte[NetProtocol.MaxReliableMessage + 1]);
        Assert.Equal(3, rig.Client.Stats.Malformed);
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        Assert.Equal(2, rig.Client.Lobby!.Members.Count);
    }

    [Fact]
    public void RaceTransportSendsOnTheSessionConnectionAndSharesItsLifecycle()
    {
        var rig = InLobby();
        rig.Client.RaceTransport.Send(NetChannel.Unreliable, [9, 9]);
        var (channel, data) = rig.Sent.Single();
        Assert.Equal(NetChannel.Unreliable, channel);
        Assert.Equal(new byte[] { 9, 9 }, data);
        rig.Client.RaceTransport.Disconnect();
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        rig.Client.RaceTransport.Send(NetChannel.Unreliable, [1]);   // nothing is sent after the end
        Assert.Single(rig.Sent);
    }

    [Fact]
    public void ALateCallbackOfAnOldConnectionAttemptIsIgnored()
    {
        var rig = new Rig(options: new SessionClientOptions { RetryIntervalMicros = 1 });
        rig.Browse(Info(1, "one"));
        var old = rig.Handler;
        old.OnDisconnected(null);   // lost: reconnecting
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);
        rig.Now += 10;
        rig.Client.Update(rig.Now);
        Assert.Equal(2, rig.Handlers.Count);

        old.OnReceive(NetChannel.ReliableOrdered, Lobby(7, 99, "g", "alice", true, others: ["bob"]).Encode());
        old.OnDisconnected(new NetReject(NetRejectReason.ServerShutdown).Encode());
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);
        Assert.Null(rig.Client.Lobby);
    }

    [Fact]
    public void ALostLinkReconnectsWithTheSameNameAndTokenAndReportsWhetherTheSeatCameBack()
    {
        var rig = InLobby(phase: NetGamePhase.Race);
        var token = rig.Client.ResumeToken!.ToArray();
        var reconnected = new List<bool>();
        rig.Client.Reconnected += reconnected.Add;
        rig.Handler.OnDisconnected(null);
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);
        Assert.Equal(SessionErrorKind.ConnectionLost, rig.Client.LastError!.Kind);
        Assert.NotNull(rig.Client.Lobby);   // last known state stays for display
        Assert.False(rig.Client.SetReady(true));
        Assert.Equal(SessionErrorKind.NotConnected, rig.Client.LastError!.Kind);

        rig.Client.Update(rig.Now);
        var hello = NetHello.Decode(rig.Hellos[^1]);
        Assert.Equal("alice", hello.Name);
        Assert.Equal(token, hello.ResumeToken);

        // The server resumed the seat: the lobby state comes first.
        rig.Receive(Lobby(7, 120, "g", "alice", false, NetGamePhase.Race, token, "boss").Encode());
        Assert.Equal(SessionClientPhase.InGame, rig.Client.Phase);
        Assert.Equal(120u, rig.Client.Epoch);
        Assert.Equal([true], reconnected);
        Assert.Equal(1, rig.Client.Stats.Reconnects);
    }

    [Fact]
    public void ReconnectWithoutAResumedSeatLandsOnTheListAndDropsTheStaleLobby()
    {
        var rig = InLobby();
        var reconnected = new List<bool>();
        rig.Client.Reconnected += reconnected.Add;
        rig.Handler.OnDisconnected(null);
        rig.Client.Update(rig.Now);
        rig.Receive(new NetGameList([Info(7, "g")]).Encode());
        Assert.Equal(SessionClientPhase.Browsing, rig.Client.Phase);
        Assert.Null(rig.Client.Lobby);
        Assert.Equal([false], reconnected);
    }

    [Fact]
    public void TransientAnswersRetryInsideTheWindowSpacedByTheRetryIntervalAndTheWindowEndsIt()
    {
        var rig = new Rig(options: new SessionClientOptions { ReconnectWindowMicros = 1_000_000, RetryIntervalMicros = 300_000 });
        rig.Now = 5_000_000;
        rig.Browse();
        rig.Handler.OnDisconnected(null);
        rig.Client.Update(rig.Now);                 // first attempt right away
        Assert.Equal(2, rig.Hellos.Count);
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.NameTaken).Encode());
        rig.Now += 100_000;
        rig.Client.Update(rig.Now);                 // too early
        Assert.Equal(2, rig.Hellos.Count);
        rig.Now += 250_000;
        rig.Client.Update(rig.Now);
        Assert.Equal(3, rig.Hellos.Count);
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);

        rig.Now += 2_000_000;                        // the attempt is hanging and the window runs out
        rig.Client.Update(rig.Now);
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Equal(SessionErrorKind.ReconnectExpired, rig.Client.LastError!.Kind);
        rig.Now += 1_000_000;
        rig.Client.Update(rig.Now);
        Assert.Equal(3, rig.Hellos.Count);
    }

    [Fact]
    public void TheReconnectWindowCountsDownAndIsNullOutsideAReconnect()
    {
        var rig = new Rig(options: new SessionClientOptions { ReconnectWindowMicros = 4_000_000, RetryIntervalMicros = 300_000 });
        rig.Now = 5_000_000;
        rig.Browse();
        Assert.Null(rig.Client.ReconnectRemainingMicros);
        rig.Handler.OnDisconnected(null);
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);
        Assert.Equal(4_000_000, rig.Client.ReconnectRemainingMicros);
        rig.Now += 1_500_000;
        Assert.Equal(2_500_000, rig.Client.ReconnectRemainingMicros);
        rig.Now += 9_000_000;
        Assert.Equal(0, rig.Client.ReconnectRemainingMicros);                 // never negative
        rig.Client.Update(rig.Now);
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Null(rig.Client.ReconnectRemainingMicros);
    }

    [Theory]
    [InlineData(NetRejectReason.ServerShutdown)]
    [InlineData(NetRejectReason.SlowClient)]
    [InlineData(NetRejectReason.RaceClosed)]
    public void AFinalAnswerWhileReconnectingEndsTheSessionWithoutFurtherAttempts(NetRejectReason reason)
    {
        var rig = InLobby(phase: NetGamePhase.Race);
        rig.Handler.OnDisconnected(null);
        rig.Client.Update(rig.Now);
        rig.Handler.OnDisconnected(new NetReject(reason).Encode());
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Equal(reason, rig.Client.LastReject!.Reason);
        Assert.Equal(reason, rig.Client.LastError!.Reason);
        int hellos = rig.Hellos.Count;
        rig.Now += 10_000_000;
        rig.Client.Update(rig.Now);
        Assert.Equal(hellos, rig.Hellos.Count);
    }

    [Fact]
    public void ATokenTheServerNoLongerKnowsIsDroppedAndTheNextAttemptConnectsByNameOnly()
    {
        var rig = InLobby(phase: NetGamePhase.Race);
        rig.Handler.OnDisconnected(null);
        rig.Client.Update(rig.Now);
        Assert.NotNull(NetHello.Decode(rig.Hellos[^1]).ResumeToken);
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.ResumeTokenInvalid).Encode());
        Assert.Equal(SessionClientPhase.Reconnecting, rig.Client.Phase);
        Assert.Null(rig.Client.ResumeToken);
        rig.Now += 250_000;
        rig.Client.Update(rig.Now);
        Assert.Null(NetHello.Decode(rig.Hellos[^1]).ResumeToken);
        Assert.Equal(3, rig.Hellos.Count);

        // The same answer to a hello without a token is final.
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.ResumeTokenInvalid).Encode());
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Equal(NetRejectReason.ResumeTokenInvalid, rig.Client.LastError!.Reason);
    }

    [Fact]
    public void ALostLobbySeatIsNotResumedWithItsUselessToken()
    {
        var rig = InLobby();
        Assert.NotNull(rig.Client.ResumeToken);
        rig.Handler.OnDisconnected(null);
        rig.Client.Update(rig.Now);
        Assert.Null(NetHello.Decode(rig.Hellos[^1]).ResumeToken);
        Assert.Equal("alice", NetHello.Decode(rig.Hellos[^1]).Name);
    }

    [Fact]
    public void AServerCloseOfAnEstablishedSessionDoesNotReconnect()
    {
        var rig = InLobby();
        rig.Handler.OnDisconnected(new NetReject(NetRejectReason.RaceClosed).Encode());
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Equal(NetRejectReason.RaceClosed, rig.Client.LastError!.Reason);
        Assert.Null(rig.Client.Lobby);
        rig.Client.Update(rig.Now + 10_000_000);
        Assert.Single(rig.Hellos);
    }

    [Fact]
    public void WithAutoReconnectOffALostLinkEndsTheSession()
    {
        var rig = new Rig(options: new SessionClientOptions { AutoReconnect = false });
        rig.Browse();
        rig.Handler.OnDisconnected(null);
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Equal(SessionErrorKind.ConnectionLost, rig.Client.LastError!.Kind);
        Assert.Single(rig.Hellos);
    }

    [Fact]
    public void ADeliberateDisconnectEndsTheSessionAndNeverReconnects()
    {
        var rig = InLobby();
        rig.Client.Disconnect();
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        Assert.Null(rig.Client.Lobby);
        rig.Handler.OnDisconnected(null);   // the transport confirms the close
        rig.Client.Update(rig.Now + 10_000_000);
        Assert.Single(rig.Hellos);
        Assert.Equal(SessionClientPhase.Closed, rig.Client.Phase);
        rig.Client.Disconnect();            // harmless twice
    }

    [Fact]
    public void PhaseChangesAreAnnouncedInOrder()
    {
        var rig = new Rig();
        rig.Browse();
        rig.Client.CreateGame("g", 4);
        rig.Receive(Lobby(1, 5, "g", "alice", true, others: ["bob"]).Encode());
        rig.Client.LeaveGame();
        rig.Client.Disconnect();
        Assert.Equal([SessionClientPhase.Connecting, SessionClientPhase.Browsing, SessionClientPhase.InGame,
            SessionClientPhase.Browsing, SessionClientPhase.Closed], rig.Phases);
    }
}
