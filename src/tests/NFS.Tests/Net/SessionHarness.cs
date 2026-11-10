using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Session server + raw protocol clients over <see cref="SimNetwork"/> with a fake clock advanced in 1 ms ticks
/// (no sockets, no windows). The clients speak the session/lobby/race wire protocol directly.</summary>
sealed class SessionHarness : IDisposable
{
    // Without game data (e.g. CI), protocol and lobby tests do not need a real fingerprint: 32 bytes are enough,
    // because the handshake only compares what the client gets from the harness. Tests that really start a race,
    // are marked [GameFact]/[GameTheory] and are skipped.
    static readonly Lazy<byte[]> LazyFingerprint = new(() =>
        GameData.Dir is { } dir ? SimDataFingerprint.Compute(dir) : new byte[32]);
    public static byte[] Fingerprint => LazyFingerprint.Value;

    public SimNetwork Net { get; }
    public SessionServer Server { get; }
    public List<SessionTestClient> Clients { get; } = [];
    public List<string> Log { get; } = [];

    public SessionHarness(int seed, SessionServerOptions? options = null)
    {
        Net = new SimNetwork(seed);
        Server = new SessionServer(Net.ServerTransport, GameData.Dir, Fingerprint, 0, options,
            s => Log.Add($"{Net.Now / 1000} {s}"), seed + 1) { Clock = () => Net.Now };
        Net.Server = Server;
    }

    public SessionTestClient Join(string name, LinkProfile? up = null, LinkProfile? down = null, int carType = NetProtocol.NoCarType,
        byte[]? hello = null)
    {
        var client = new SessionTestClient(name, Fingerprint, carType);
        var link = Net.AddClient(client, up ?? new LinkProfile(), down ?? new LinkProfile());
        client.Link = link;
        link.Connect(hello ?? client.Hello());
        Clients.Add(client);
        return client;
    }

    public void Run(long micros) => RunUntil(() => false, micros);

    /// <summary>Advances in 1 ms ticks until <paramref name="done"/> or the time budget; returns whether <paramref name="done"/> held.</summary>
    public bool RunUntil(Func<bool> done, long maxMicros)
    {
        long end = Net.Now + maxMicros;
        while (Net.Now < end)
        {
            if (done()) return true;
            long t = Net.Now + 1000;
            Net.AdvanceTo(t);
            Server.Update(t);
        }
        return done();
    }

    public void Dispose() => Server.Dispose();
}

/// <summary>A raw session client: connects with a hello, tracks the complete games list, its lobby state and the race messages
/// of the round it is in, and can issue every session/lobby request of the protocol.</summary>
sealed class SessionTestClient : INetClientHandler
{
    readonly byte[] _fingerprint;
    readonly int _carType;
    int _readyRace;

    public SessionTestClient(string name, byte[] fingerprint, int carType)
    {
        Name = name;
        _fingerprint = fingerprint;
        _carType = carType;
    }

    public string Name { get; }
    /// <summary>Answers every new race welcome with a Ready (a client that loaded the race); false simulates a client that never
    /// finishes loading.</summary>
    public bool AutoReady { get; set; } = true;
    public SimNetwork.SimClient Link { get; set; } = null!;
    public bool Connected { get; private set; }
    public bool Disconnected { get; private set; }
    public NetReject? Reject { get; private set; }
    public NetGameList? List { get; private set; }
    public NetLobbyState? Lobby { get; private set; }
    public NetWelcome? Welcome { get; private set; }
    public List<NetWelcome> Welcomes { get; } = [];
    public NetStart? Start { get; private set; }
    public NetResults? Results { get; private set; }
    public List<NetLobbyState> States { get; } = [];
    public List<NetResults> AllResults { get; } = [];
    public List<NetGameList> Lists { get; } = [];
    public List<NetPlayerLeft> PlayerLefts { get; } = [];
    public List<NetChatMessage> Chats { get; } = [];
    public int Malformed { get; private set; }
    /// <summary>Snapshot datagrams received since the latest welcome (any part of any snapshot) and durable messages.</summary>
    public int SnapshotParts { get; private set; }
    public int DurableMessages { get; private set; }
    public int EventMessages { get; private set; }
    public int GameId => Lobby?.GameId ?? 0;
    public uint Epoch => Lobby?.Epoch ?? 0;
    public bool InLobby => Lobby?.Phase == NetGamePhase.Lobby;
    public bool InCountdown => Lobby?.Phase == NetGamePhase.Countdown;
    public bool InRace => Lobby?.Phase == NetGamePhase.Race;
    public NetLobbyMember? Me => Lobby?.Members.FirstOrDefault(m => m.Name == Name);
    public bool IsOperator => Me?.Operator == true;
    public bool Ready => Me?.Ready == true;

    public byte[] Hello(byte[]? resumeToken = null) => new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint,
        Name, _carType, BuildInfo.AppVersion, resumeToken).Encode();

    public void OnConnected() => Connected = true;

    public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray();
        try
        {
            var r = new WireReader(bytes);
            var h = NetHeader.Read(ref r);
            switch (h.Type)
            {
                case NetMessageType.GameList:
                    List = NetGameList.Decode(h, ref r);
                    Lists.Add(List);
                    break;
                case NetMessageType.LobbyState:
                    Lobby = NetLobbyState.Decode(h, ref r);
                    States.Add(Lobby);
                    if (_pendingOptions is { } o && Lobby.Members.Any(m => m.Name == Name && m.Operator))
                    {
                        _pendingOptions = null;
                        Link.Send(NetChannel.ReliableOrdered, new NetLobbyOptions(h.RaceId, h.Epoch, o.Track, o.Laps, o.Backward, o.Bots,
                            o.Traffic, o.Skill, o.CatchUp, o.Style).Encode());
                    }
                    break;
                case NetMessageType.Welcome:
                    Welcome = NetWelcome.Decode(bytes);
                    Welcomes.Add(Welcome);
                    SnapshotParts = 0;
                    DurableMessages = 0;
                    EventMessages = 0;
                    if (AutoReady && Welcome.RaceId != _readyRace)
                    {
                        _readyRace = Welcome.RaceId;
                        Link.Send(NetChannel.ReliableOrdered, new NetReady(Welcome.RaceId, Welcome.Epoch, Welcome.SetupHash).Encode());
                    }
                    break;
                case NetMessageType.Snapshot: SnapshotParts++; break;
                case NetMessageType.Durable: DurableMessages++; break;
                case NetMessageType.Events: EventMessages++; break;
                case NetMessageType.Start:
                    Start = NetStart.Decode(h, ref r);
                    break;
                case NetMessageType.Results:
                    Results = NetResults.Decode(h, ref r);
                    AllResults.Add(Results);
                    Link.Send(NetChannel.ReliableOrdered, NetResultsAck.Encode(Results.RaceId, Results.Epoch));
                    break;
                case NetMessageType.ChatMessage:
                    Chats.Add(NetChatMessage.Decode(h, ref r));
                    break;
                case NetMessageType.PlayerLeft:
                    PlayerLefts.Add(NetPlayerLeft.Decode(h, ref r));
                    break;
            }
        }
        catch (NetProtocolException)
        {
            Malformed++;
        }
    }

    public void OnDisconnected(byte[]? reason)
    {
        Disconnected = true;
        Connected = false;
        Reject = reason == null ? null : NetReject.TryDecode(reason);
    }

    public void Send(byte[] data) => Link.Send(NetChannel.ReliableOrdered, data);
    public void RequestList() => Send(new NetGameListRequest().Encode());
    /// <summary>Creates a game (name, player limit and mode only, as the protocol does); the options that differ from the server's
    /// defaults are sent by this client as the operator as soon as its lobby state arrives, so a test that waits for the lobby and
    /// then lets the server run one round trip sees them applied.</summary>
    public void Create(string name, int maxPlayers, int track, int laps, NetGameMode mode = NetGameMode.SingleRace, bool backward = false,
        int bots = NetProtocol.AutoBots, bool traffic = false, int skill = NetProtocol.DefaultSkill, bool catchUp = NetProtocol.DefaultCatchUp,
        int style = NetProtocol.DefaultStyle)
    {
        _pendingOptions = (track, laps, backward, bots, traffic, skill, catchUp, style);
        if (_pendingOptions == (NetProtocol.DefaultTrack, NetProtocol.DefaultLaps, NetProtocol.DefaultBackward, NetProtocol.AutoBots,
                NetProtocol.DefaultTraffic, NetProtocol.DefaultSkill, NetProtocol.DefaultCatchUp, NetProtocol.DefaultStyle)) _pendingOptions = null;
        Send(new NetGameCreate(name, maxPlayers, mode).Encode());
    }
    (int Track, int Laps, bool Backward, int Bots, bool Traffic, int Skill, bool CatchUp, int Style)? _pendingOptions;
    public void JoinGame(int gameId) => Send(new NetGameJoin(gameId).Encode());
    public void LeaveGame() => Send(new NetGameLeave(GameId, Epoch).Encode());
    public void SetReady(bool ready) => Send(new NetLobbyReady(GameId, Epoch, ready).Encode());
    public void SetCar(int carType, int colour = NetProtocol.NoColour) => Send(new NetLobbyCar(GameId, Epoch, carType, colour).Encode());
    public void SetCar(NFS.ModApi.CarKey car, int colour = NetProtocol.NoColour) => Send(new NetLobbyCar(GameId, Epoch, car, colour).Encode());
    /// <summary>Operator: the options of the next race; an option left null keeps its value of the current lobby state.</summary>
    public void SetOptions(int track, int laps, bool? backward = null, int? bots = null, bool? traffic = null, int? skill = null,
        bool? catchUp = null, int? style = null) =>
        Send(new NetLobbyOptions(GameId, Epoch, track, laps, backward ?? Lobby!.Backward, bots ?? Lobby!.Bots, traffic ?? Lobby!.Traffic,
            skill ?? Lobby!.Skill, catchUp ?? Lobby!.CatchUp, style ?? Lobby!.Style).Encode());
    public void Chat(string text) => Send(new NetChatSend(GameId, Epoch, text).Encode());
    public void RequestStart() => Send(new NetLobbyStart(GameId, Epoch).Encode());
    public void CancelStart() => Send(new NetLobbyCancel(GameId, Epoch).Encode());
    public void ReturnToLobby() => Send(new NetLobbyReturn(GameId, Epoch).Encode());
    public void Disconnect() => Link.Disconnect();
}
