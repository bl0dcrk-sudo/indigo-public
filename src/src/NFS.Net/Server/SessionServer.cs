using System.Security.Cryptography;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

/// <summary>Administrative settings of a session server. Game flow (track, laps, cars, readiness) belongs to the clients and
/// the games they create; this record only bounds resources and lifetimes. <see cref="GraceMicros"/> is the reconnect window:
/// a connection lost in Countdown/Race keeps its seat and car reserved that long (0 = no reservation, immediate leave).</summary>
public sealed record SessionServerOptions
{
    /// <summary>Data mods of this server; a client with another set is rejected with <see cref="NetRejectReason.ModsMismatch"/>.</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;
    /// <summary>The installed mods behind <see cref="Mods"/>; a client that lacks them may download them (5.4). Empty = no downloads.</summary>
    public IReadOnlyList<NFS.Game.Mods.LoadedMod> ModSources { get; init; } = [];
    /// <summary>The operator may switch downloads of mods off (clients with another mod set are then only refused).</summary>
    public bool AllowModDownloads { get; init; } = true;
    /// <summary>Download connections at the same time; more are refused with ModDownloadRefused.</summary>
    public int MaxModDownloads { get; init; } = 4;
    /// <summary>A download connection without any traffic for this long is closed.</summary>
    public long ModDownloadIdleMicros { get; init; } = 60_000_000;
    /// <summary>Cars of this server (original + mod cars); null = the original cars only.</summary>
    public NFS.Game.Cars.CarCatalog? Cars { get; init; }
    /// <summary>Games that may exist at the same time; a creation above this limit is refused.</summary>
    public int MaxGames { get; init; } = 16;
    public int MaxConnections { get; init; } = 256;
    /// <summary>A lobby with no activity for this long is closed and its members are disconnected with RaceClosed. A running
    /// race is never removed by this timeout.</summary>
    public long LobbyIdleTimeoutMicros { get; init; } = 15 * 60_000_000;
    /// <summary>Deterministic start countdown; the protocol allows any value up to <see cref="NetProtocol.MaxCountdownMillis"/>.</summary>
    public long CountdownMicros { get; init; } = 5_000_000;
    /// <summary>Reconnect grace window: a seat lost in Countdown/Race stays reserved for its token this long; 0 disables it.</summary>
    public long GraceMicros { get; init; } = 30_000_000;
    /// <summary>Abort a running round after this many host steps (0 = no limit); useful for bounded smoke runs.</summary>
    public int MaxRaceSteps { get; init; }
    /// <summary>Stragglers: from the moment the first human finishes, the others have this long to finish; then the round ends and the
    /// unfinished cars are DNF, ranked behind the finishers by their progress (0 = no limit). Applies to every game mode.</summary>
    public long StragglerLimitMicros { get; init; } = 120_000_000;
    /// <summary>A composed round whose clients never all report ready is closed after this time.</summary>
    public long RaceWaitTimeoutMicros { get; init; } = 120_000_000;
    /// <summary>How long a finished round is kept open before the session replaces it with the lobby. The session handles the
    /// result in the same update, so this only bounds a bug/host that never reports its result.</summary>
    public long RaceLingerMicros { get; init; } = 60_000_000;
    /// <summary>Client messages accepted per connection and second; more are dropped without touching state.</summary>
    public int MaxReliableMessagesPerSecond { get; init; } = 256;
    /// <summary>Chat lines one connection may send per <see cref="ChatWindowMicros"/>; more are dropped (counted) without a reply.</summary>
    public int ChatMaxPerWindow { get; init; } = 5;
    public long ChatWindowMicros { get; init; } = 10_000_000;
    /// <summary>Malformed / foreign messages tolerated per connection before it is disconnected.</summary>
    public int MaxBadMessages { get; init; } = 64;
    /// <summary>Reliable backlog above which a slow client is disconnected with SlowClient.</summary>
    public int MaxReliableBacklog { get; init; } = NetProtocol.MaxReliableBacklog;
}

/// <summary>Counters of the session server (diagnostics and tests).</summary>
public sealed class SessionServerStats
{
    public long Messages, RateLimited, Malformed, WrongSession, Rejected, Disconnects, SlowClientDrops,
        GamesCreated, RefusedCreates, RefusedJoins, RefusedLobby, GamesJoined, GamesClosed, Resumed, ResumeRefused,
        ChatMessages, ChatRateLimited, ModDownloadConnections, ModDownloadRefused, ModsSent, ModBytesSent;
}

/// <summary>One accepted client connection: its protocol identity (connection id and session epoch), its globally unique
/// player name and the game it currently belongs to.</summary>
public sealed class ClientConnection
{
    internal ClientConnection(int id, uint epoch, string name)
    {
        Id = id;
        Epoch = epoch;
        Name = name;
    }

    public int Id { get; }
    /// <summary>Epoch of the current session/game; reissued on every join/leave so stale lobby messages are rejected.</summary>
    public uint Epoch { get; internal set; }
    public string Name { get; }
    public GameSession? Game { get; internal set; }
    internal long RateWindowStart, RateCount, ChatWindowStart = long.MinValue, ChatCount;
    internal int Bad;
}

/// <summary>Output of a game session towards the connections (implemented by <see cref="SessionServer"/>).</summary>
internal interface ISessionOutput
{
    void Send(ClientConnection connection, NetChannel channel, byte[] data);
    void Disconnect(ClientConnection connection, NetRejectReason reason);
    /// <summary>Takes the connection out of its game (the connection stays and gets the games list).</summary>
    void LeaveGame(ClientConnection connection);
    int PendingReliable(ClientConnection connection);
}

/// <summary>
/// Session layer of the authoritative server: handshake and version checks, the global player-name registry, the connection
/// registry and the game registry. It routes session messages (lists, create/join), lobby messages of the sender's game and
/// race messages of the game's active round. Flooding and malformed traffic are counted and bounded; slow clients are
/// disconnected without blocking the loop. The actual race is a <see cref="RaceRound"/> owned by a <see cref="GameSession"/>.
/// </summary>
public sealed partial class SessionServer : INetServerHandler, ISessionOutput, IDisposable
{
    readonly INetServerTransport _transport;
    readonly string? _gameDir;
    readonly byte[] _fingerprint;
    readonly SessionServerOptions _options;
    readonly Action<string>? _log;
    readonly Random _random;
    readonly Dictionary<int, ClientConnection> _connections = [];
    readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    readonly List<GameSession> _games = [];
    long _now;
    int _nextGameId = 1;
    int _nextRaceId = 1_000_000;
    uint _nextEpoch;
    /// <summary>The list last sent to idle clients; a new one is broadcast once per update, only when its content changed.</summary>
    byte[]? _broadcastList;
    bool _stopping;
    bool _stopped;

    public SessionServer(INetServerTransport transport, string? gameDir, byte[] dataFingerprint, long now,
        SessionServerOptions? options = null, Action<string>? log = null, int seed = 0)
    {
        if (dataFingerprint.Length != 32) throw new ArgumentException("data fingerprint must be 32 bytes", nameof(dataFingerprint));
        _transport = transport;
        _gameDir = gameDir;
        _fingerprint = dataFingerprint;
        _options = options ?? new SessionServerOptions();
        if (_options.MaxGames is < 1 or > NetProtocol.MaxGameListEntries)
            throw new ArgumentOutOfRangeException(nameof(options), $"max games must be in 1..{NetProtocol.MaxGameListEntries}");
        if (_options.CountdownMicros is < 0 or > NetProtocol.MaxCountdownMillis * 1000L)
            throw new ArgumentOutOfRangeException(nameof(options), $"countdown must be in 0..{NetProtocol.MaxCountdownMillis} ms");
        _log = log;
        _random = seed == 0 ? new Random() : new Random(seed);
        _nextEpoch = (uint)_random.NextInt64(1, uint.MaxValue);
        _now = now;
    }

    /// <summary>Monotonic clock read when data arrives (flood windows); without it the last <see cref="Update"/> time is used.</summary>
    public Func<long>? Clock { get; init; }
    public SessionServerStats Stats { get; } = new();
    public bool Stopping => _stopping;
    public bool Stopped => _stopped;

    /// <summary>True while the loop must keep its fast cadence: the server is stopping or some game is in its countdown or race.
    /// Lobby-only servers may wait longer (<see cref="ServerIdlePolicy"/>).</summary>
    public bool NeedsFastLoop => _stopping || _games.Any(g => g.Phase is NetGamePhase.Countdown or NetGamePhase.Race) || ModDownloadsActive;
    public int ConnectionCount => _connections.Count;
    public int GameCount => _games.Count;
    public SessionServerOptions Options => _options;
    public IReadOnlyList<GameSession> Games => _games;

    // ---------------- handshake ----------------

    public byte[]? OnConnectRequest(ReadOnlySpan<byte> data)
    {
        // Versions first: a client of another wire version cannot be decoded with this layout.
        if (NetHello.TryReadVersions(data, out ushort version, out ushort physics))
        {
            if (version != NetProtocol.Version) return Reject(NetRejectReason.ProtocolMismatch, version);
            if (physics != NetProtocol.PhysicsVersion) return Reject(NetRejectReason.PhysicsMismatch);
        }
        NetHello hello;
        try { hello = NetHello.Decode(data); }
        catch (NetProtocolException) { return Reject(NetRejectReason.Malformed); }
        if (hello.Version != NetProtocol.Version) return Reject(NetRejectReason.ProtocolMismatch);
        if (hello.PhysicsVersion != NetProtocol.PhysicsVersion) return Reject(NetRejectReason.PhysicsMismatch);
        if (hello.AppVersion != BuildInfo.AppVersion) return Reject(NetRejectReason.AppVersionMismatch);
        if (!hello.DataFingerprint.AsSpan().SequenceEqual(_fingerprint)) return Reject(NetRejectReason.DataMismatch);
        if (hello.Purpose == NetHelloPurpose.ModDownload) return CheckModDownload();
        if (!hello.Mods.SameAs(_options.Mods))
        {
            Log($"reject connection: ModsMismatch, {_options.Mods.Diff(hello.Mods, "server", "client")}");
            return Reject(NetRejectReason.ModsMismatch);
        }
        if (_stopping || _stopped) return Reject(NetRejectReason.ServerShutdown);
        if (!IsPlayerName(hello.Name)) return Reject(NetRejectReason.Malformed);
        if (Clock != null) _now = Clock();
        // A live connection under the name answers NameTaken (retryable: a reconnecting client may be faster than the
        // server noticing the loss of its old link), with or without a token. A name kept by a reserved seat resumes that
        // seat: without a token (restarted client) or with that seat's own token; any other token is refused.
        if (_names.Contains(hello.Name) || IsServerName(hello.Name)) return Reject(NetRejectReason.NameTaken);
        var reservation = FindReservation(hello.Name);
        if (hello.ResumeToken is { } token && (reservation == null || !TokenMatches(reservation.Value.Member, token)))
        {
            Stats.ResumeRefused++;
            return Reject(NetRejectReason.ResumeTokenInvalid);
        }
        if (_connections.Count >= _options.MaxConnections) return Reject(NetRejectReason.RaceFull);
        return null;
    }

    /// <summary>The reserved seat (unexpired grace window, no connection) that keeps <paramref name="name"/>; when several games
    /// reserve it the most recently lost seat wins.</summary>
    (GameSession Game, GameMember Member)? FindReservation(string name)
    {
        (GameSession, GameMember)? best = null;
        foreach (var game in _games)
        {
            if (game.FindReservation(name, _now) is not { } member) continue;
            if (best is not { } current || member.DisconnectedSinceMicros > current.Item2.DisconnectedSinceMicros) best = (game, member);
        }
        return best;
    }

    static bool TokenMatches(GameMember member, ReadOnlySpan<byte> token) =>
        token.Length == NetProtocol.ResumeTokenLength && CryptographicOperations.FixedTimeEquals(member.ResumeToken, token);

    byte[] Reject(NetRejectReason reason, ushort clientVersion = NetProtocol.Version)
    {
        Stats.Rejected++;
        Log($"reject connection: {reason}");
        return new NetReject(reason) { ServerMods = _options.Mods }.Encode(clientVersion);
    }

    public void OnConnected(int connection, ReadOnlySpan<byte> data)
    {
        NetHello hello;
        try { hello = NetHello.Decode(data); }
        catch (NetProtocolException) { RejectConnection(connection, NetRejectReason.Malformed); return; }
        if (hello.Purpose == NetHelloPurpose.ModDownload) { AcceptModDownload(connection); return; }
        // The request was already validated; re-check the two conditions that another connection may have changed in between.
        if (_stopping || _stopped) { RejectConnection(connection, NetRejectReason.ServerShutdown); return; }
        if (Clock != null) _now = Clock();
        if (_connections.ContainsKey(connection) || _names.Contains(hello.Name) || IsServerName(hello.Name) || !IsPlayerName(hello.Name))
        {
            RejectConnection(connection, NetRejectReason.NameTaken);
            return;
        }
        // A reserved seat of this name (grace window) is resumed: with its own token or, without a token, by the name alone.
        // A token that does not belong to that reservation (or an expired one) is refused and changes nothing.
        var reservation = FindReservation(hello.Name);
        if (hello.ResumeToken is { } token && (reservation == null || !TokenMatches(reservation.Value.Member, token)))
        {
            Stats.ResumeRefused++;
            RejectConnection(connection, NetRejectReason.ResumeTokenInvalid);
            Log($"conn {connection} '{hello.Name}': resume token does not match a reserved seat");
            return;
        }
        if (reservation is { } reserved)
        {
            ResumeConnection(connection, hello, reserved.Game, reserved.Member);
            return;
        }
        var conn = new ClientConnection(connection, NewEpoch(), hello.Name);
        _connections[connection] = conn;
        _names.Add(hello.Name);
        Log($"conn {connection} '{hello.Name}' epoch {conn.Epoch:X8}");
        SendGameList(conn);
    }

    /// <summary>Hands a reconnecting client to the game that reserved its name. The game issues a new epoch to the round,
    /// sends a resume welcome and broadcasts the state; a game that can no longer resume is answered with
    /// <see cref="NetRejectReason.ResumeTokenInvalid"/>.</summary>
    void ResumeConnection(int id, NetHello hello, GameSession game, GameMember member)
    {
        var conn = new ClientConnection(id, NewEpoch(), hello.Name);
        _connections[id] = conn;
        _names.Add(hello.Name);
        if (!game.TryResume(conn, member, _now, out string error))
        {
            _connections.Remove(id);
            _names.Remove(hello.Name);
            Stats.ResumeRefused++;
            RejectConnection(id, NetRejectReason.ResumeTokenInvalid);
            Log($"conn {id} '{hello.Name}': resume refused ({error})");
            return;
        }
        Stats.Resumed++;
        Log($"conn {id} '{hello.Name}' resumed game {game.GameId:X8} '{game.Name}', epoch {conn.Epoch:X8}");
    }

    void RejectConnection(int connection, NetRejectReason reason)
    {
        Stats.Rejected++;
        _transport.Disconnect(connection, new NetReject(reason).Encode());
    }

    /// <summary>The chat sender name of the server console is reserved; a player cannot impersonate it.</summary>
    static bool IsServerName(string name) => string.Equals(name, NetProtocol.ServerChatName, StringComparison.OrdinalIgnoreCase);

    static bool IsPlayerName(string name) =>
        name.Length is >= 1 and <= NetProtocol.MaxNameLength && name.All(c => c is >= ' ' and < (char)0x7F);

    /// <summary>Next session epoch: a counter from a random start, so no value repeats before 2^32 issues and nothing has to
    /// be remembered (the session epoch 0 is skipped).</summary>
    uint NewEpoch()
    {
        if (++_nextEpoch == NetSessionProtocol.SessionEpoch) ++_nextEpoch;
        return _nextEpoch;
    }

    /// <summary>New 16-byte resume token from the OS CSPRNG: not predictable from other tokens and, at 128 bits, not repeated
    /// in practice (an ended reservation can therefore never be resumed by an old token).</summary>
    static byte[] NewResumeToken()
    {
        var token = new byte[NetProtocol.ResumeTokenLength];
        RandomNumberGenerator.Fill(token);
        return token;
    }

    // ---------------- receive / routing ----------------

    public void OnReceive(int connection, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (_downloads.TryGetValue(connection, out var download)) { ReceiveModDownload(download, data); return; }
        if (!_connections.TryGetValue(connection, out var conn)) return;
        if (Clock != null) _now = Clock();
        Stats.Messages++;
        if (!RateOk(conn)) { Stats.RateLimited++; return; }
        try
        {
            if (data.Length > NetProtocol.MaxReliableMessage) throw new NetProtocolException("oversized session message");
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            bool sessionHeader = h.RaceId == NetSessionProtocol.SessionRaceId && h.Epoch == NetSessionProtocol.SessionEpoch;
            if (conn.Game is not { } game)
            {
                if (!sessionHeader)
                {
                    // A leave that crosses the server's own removal of the member (the round core ended the seat) is harmless.
                    if (h.Type != NetMessageType.GameLeave) Foreign(conn);
                    return;
                }
                switch (h.Type)
                {
                    case NetMessageType.GameListRequest:
                        NetGameListRequest.Decode(h, ref r);
                        SendGameList(conn);
                        break;
                    case NetMessageType.GameCreate:
                        TryCreateGame(conn, NetGameCreate.Decode(h, ref r));
                        break;
                    case NetMessageType.GameJoin:
                        TryJoinGame(conn, NetGameJoin.Decode(h, ref r));
                        break;
                    default:
                        throw new NetProtocolException($"message {h.Type} is not a session message");
                }
                return;
            }
            if (h.RaceId == game.GameId && h.Epoch == conn.Epoch && !sessionHeader)
            {
                switch (h.Type)
                {
                    case NetMessageType.GameLeave:
                        NetGameLeave.Decode(h, ref r);
                        LeaveGame(conn);
                        break;
                    case NetMessageType.LobbyReady:
                    {
                        var ready = NetLobbyReady.Decode(h, ref r);
                        if (Member(game, conn) is { } member) game.SetReady(member, ready.Ready, _now);
                        else Foreign(conn);
                        break;
                    }
                    case NetMessageType.LobbyCar:
                    {
                        var car = NetLobbyCar.Decode(h, ref r);
                        if (Member(game, conn) is { } member)
                        {
                            if (!game.SetCar(member, car.Car, car.Colour, _now, out string why))
                            {
                                Stats.RefusedLobby++;
                                Log($"conn {conn.Id}: car refused ({why})");
                            }
                        }
                        else Foreign(conn);
                        break;
                    }
                    case NetMessageType.LobbyOptions:
                    {
                        var options = NetLobbyOptions.Decode(h, ref r);
                        if (Member(game, conn) is { } member)
                        {
                            if (!game.TrySetOptions(member, options.Track, options.Laps, options.Backward, options.Bots, options.Traffic,
                                options.Skill, options.CatchUp, options.Style, _now, out string why))
                            {
                                Stats.RefusedLobby++;
                                Log($"conn {conn.Id}: options refused ({why})");
                            }
                        }
                        else Foreign(conn);
                        break;
                    }
                    case NetMessageType.LobbyStart:
                        NetLobbyStart.Decode(h, ref r);
                        if (Member(game, conn) is { } starter)
                        {
                            if (!game.TryStart(starter, _now, out string why))
                            {
                                Stats.RefusedLobby++;
                                Log($"conn {conn.Id}: start refused ({why})");
                            }
                        }
                        else Foreign(conn);
                        break;
                    case NetMessageType.LobbyCancel:
                        NetLobbyCancel.Decode(h, ref r);
                        if (Member(game, conn) is { } canceller)
                        {
                            if (!game.TryCancelStart(canceller, _now, out string why))
                            {
                                Stats.RefusedLobby++;
                                Log($"conn {conn.Id}: cancel refused ({why})");
                            }
                        }
                        else Foreign(conn);
                        break;
                    case NetMessageType.ChatSend:
                    {
                        var chat = NetChatSend.Decode(h, ref r);
                        if (Member(game, conn) is { } speaker) HandleChat(conn, game, speaker, chat.Text);
                        else Foreign(conn);
                        break;
                    }
                    case NetMessageType.LobbyReturn:
                        NetLobbyReturn.Decode(h, ref r);
                        game.AcknowledgeReturn(_now);
                        break;
                    default:
                        throw new NetProtocolException($"message {h.Type} is not a lobby message");
                }
                return;
            }
            if (sessionHeader)
            {
                // A client in a game may still read the list; creating/joining another game requires leaving first.
                if (h.Type == NetMessageType.GameListRequest)
                {
                    NetGameListRequest.Decode(h, ref r);
                    SendGameList(conn);
                }
                else if (h.Type is NetMessageType.GameCreate or NetMessageType.GameJoin) Stats.RefusedLobby++;
                else throw new NetProtocolException($"message {h.Type} is not allowed inside a game");
                return;
            }
            // Race messages keep the round's own id/epoch; the round validates them and counts foreign ones itself.
            if (game.TryReceiveRace(conn, channel, data)) return;
            // After the result the game returns to the lobby at once; an acknowledgement that crosses that switch is harmless
            // (the result reached the client, which is all the ack proves) and must not count as a foreign message.
            if (h.Type == NetMessageType.ResultsAck && Member(game, conn) != null)
            {
                game.AcknowledgeReturn(_now);
                return;
            }
            // The same holds for controls still in flight when the round ended: an input datagram is unreliable and the client sends
            // it until it has the result, so one can arrive after the game is back in the lobby.
            if (h.Type == NetMessageType.Input && game.Round == null && Member(game, conn) != null) return;
            Foreign(conn);
        }
        catch (NetProtocolException e)
        {
            Stats.Malformed++;
            Log($"conn {conn.Id}: malformed message ({e.Message})");
            Bad(conn);
        }
        finally
        {
            // Idle clients see a created/joined/left game at once; an unchanged list is not resent.
            BroadcastGameListIfChanged();
        }
    }

    /// <summary>Broadcasts a chat line inside the sender's game. The sender name is the connection's own name (the message
    /// carries none), and a connection that talks faster than the chat rate limit is dropped silently (counted).</summary>
    void HandleChat(ClientConnection conn, GameSession game, GameMember speaker, string text)
    {
        if (conn.ChatWindowStart == long.MinValue || _now - conn.ChatWindowStart >= _options.ChatWindowMicros)
        {
            conn.ChatWindowStart = _now;
            conn.ChatCount = 0;
        }
        if (++conn.ChatCount > _options.ChatMaxPerWindow) { Stats.ChatRateLimited++; return; }
        Stats.ChatMessages++;
        game.BroadcastChat(speaker.Name, text);
    }

    /// <summary>Console `msg`: sends <paramref name="text"/> from <see cref="NetProtocol.ServerChatName"/> to every connected
    /// player that is in a game (chat is a game feature). Returns the number of recipients; text that is not 1..80 printable
    /// ASCII characters is refused with -1.</summary>
    public int BroadcastServerMessage(string text)
    {
        if (!NetProtocol.IsChatText(text)) return -1;
        int count = 0;
        foreach (var game in _games.ToArray()) count += game.BroadcastChat(NetProtocol.ServerChatName, text);
        Stats.ChatMessages++;
        return count;
    }

    static GameMember? Member(GameSession game, ClientConnection conn) =>
        game.Members.FirstOrDefault(m => ReferenceEquals(m.Connection, conn));

    void Foreign(ClientConnection conn)
    {
        Stats.WrongSession++;
        Bad(conn);
    }

    bool RateOk(ClientConnection c)
    {
        if (_now - c.RateWindowStart >= 1_000_000) { c.RateWindowStart = _now; c.RateCount = 0; }
        return ++c.RateCount <= _options.MaxReliableMessagesPerSecond;
    }

    void Bad(ClientConnection c)
    {
        if (++c.Bad > _options.MaxBadMessages) CloseConnection(c, NetRejectReason.Malformed);
    }

    // ---------------- game registry ----------------

    void TryCreateGame(ClientConnection conn, NetGameCreate request)
    {
        if (_stopping || conn.Game != null) { Stats.RefusedCreates++; return; }
        // A refused create is answered with the current list (as a refused join is), so the client is not left waiting.
        if (_games.Count >= _options.MaxGames)
        {
            Stats.RefusedCreates++;
            Log($"conn {conn.Id}: game limit {_options.MaxGames} reached");
            SendGameList(conn);
            return;
        }
        if (_games.Any(g => string.Equals(g.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Stats.RefusedCreates++;
            Log($"conn {conn.Id}: game name '{request.Name}' already used");
            SendGameList(conn);
            return;
        }
        conn.Epoch = NewEpoch();
        var game = new GameSession(_nextGameId++, request.Name, request.MaxPlayers, request.Mode,
            _now, this, _gameDir, _fingerprint, _options, NextRaceId, NewResumeToken, Log);
        _games.Add(game);
        if (!game.TryJoin(conn, _now, out string error))
        {
            _games.Remove(game);
            game.Dispose();
            Stats.RefusedCreates++;
            Log($"conn {conn.Id}: create refused ({error})");
            SendGameList(conn);
            return;
        }
        Stats.GamesCreated++;
        ReleaseOtherReservations(conn, game);
        Log($"conn {conn.Id}: game {game.GameId:X8} '{game.Name}' created (track {game.Track}{(game.Backward ? " backward" : "")}, laps {game.Laps}, max {game.MaxPlayers}, bots {(game.Bots == ServerRaceConfig.AutoBots ? "auto" : game.Bots)}, traffic {(game.Traffic ? "on" : "off")}, skill {game.Skill}, catch-up {(game.CatchUp ? "on" : "off")}, style {game.Style})");
    }

    void TryJoinGame(ClientConnection conn, NetGameJoin request)
    {
        if (_stopping || conn.Game != null) { Stats.RefusedJoins++; return; }
        var game = _games.FirstOrDefault(g => g.GameId == request.GameId);
        if (game == null)
        {
            Stats.RefusedJoins++;
            Log($"conn {conn.Id}: join of unknown game {request.GameId:X8}");
            SendGameList(conn);
            return;
        }
        conn.Epoch = NewEpoch();
        bool returning = game.FindReservation(conn.Name, _now) != null;
        if (!game.TryJoin(conn, _now, out string error))
        {
            Stats.RefusedJoins++;
            Log($"conn {conn.Id}: join of game {game.GameId:X8} refused ({error})");
            SendGameList(conn);
            return;
        }
        Stats.GamesJoined++;
        ReleaseOtherReservations(conn, game);
        if (returning) Stats.Resumed++;
        Log($"conn {conn.Id}: {(returning ? "returned to" : "joined")} game {game.GameId:X8} '{game.Name}'");
    }

    /// <summary>A player is a member of one game only: when the connection enters <paramref name="entered"/> every reservation of
    /// its name in the other games ends (a running race marks that car DNF), so the name can never be resumed into the wrong game.</summary>
    void ReleaseOtherReservations(ClientConnection conn, GameSession entered)
    {
        foreach (var other in _games.ToArray())
            if (!ReferenceEquals(other, entered) && other.ReleaseReservation(conn.Name, _now))
                Log($"conn {conn.Id}: reservation in game {other.GameId:X8} ended");
    }

    /// <summary>Deliberate leave: the lobby frees the seat at once; in Countdown/Race the seat is reserved like after a lost
    /// connection (the connection stays, returns to the list and keeps its name, so it may rejoin the game from the list).</summary>
    void LeaveGame(ClientConnection conn)
    {
        if (conn.Game is not { } game) return;
        game.Disconnect(conn, _now);
        conn.Epoch = NewEpoch();
        SendGameList(conn);
        if (game.ShouldRemove) RemoveGame(game);
    }

    void RemoveGame(GameSession game)
    {
        if (!_games.Remove(game)) return;
        game.Dispose();
        Stats.GamesClosed++;
        Log($"game {game.GameId:X8} '{game.Name}' removed");
    }

    /// <summary>Idle lobby timeout: the game is closed and every member receives a readable RaceClosed disconnect.</summary>
    void CloseGame(GameSession game, NetRejectReason reason, string why)
    {
        Log($"game {game.GameId:X8} '{game.Name}': closing ({why})");
        foreach (var member in game.Members.ToArray())
            if (member.Connection is { } connection) CloseConnection(connection, reason);
        RemoveGame(game);
    }

    int NextRaceId()
    {
        int id = _nextRaceId++;
        if (_nextRaceId >= int.MaxValue) _nextRaceId = 1_000_000;
        return id;
    }

    // ---------------- connection registry ----------------

    public void OnDisconnected(int connection)
    {
        if (Clock != null) _now = Clock();
        if (_downloads.ContainsKey(connection)) { EndModDownload(connection); return; }
        if (!_connections.TryGetValue(connection, out var conn)) return;
        // An unplanned loss keeps a Countdown/Race seat reserved for the grace window (the game decides; the lobby always
        // leaves immediately). Server-initiated closes release the seat at once.
        CloseConnection(conn, NetRejectReason.Left, allowGrace: true);
        BroadcastGameListIfChanged();
    }

    /// <summary>Removes a connection from the protocol registries (name released, game membership ended or reserved) and
    /// closes the transport. Server-initiated closes call this directly; the transport does not report them back.</summary>
    void CloseConnection(ClientConnection conn, NetRejectReason reason, bool allowGrace = false)
    {
        if (!_connections.Remove(conn.Id)) return;
        _names.Remove(conn.Name);
        var game = conn.Game;
        conn.Game = null;
        if (game != null)
        {
            if (allowGrace) game.Disconnect(conn, _now);
            else game.Leave(conn, _now);
        }
        _transport.Disconnect(conn.Id, new NetReject(reason).Encode());
        Stats.Disconnects++;
        if (reason == NetRejectReason.SlowClient) Stats.SlowClientDrops++;
        Log($"conn {conn.Id} '{conn.Name}' closed ({reason})");
    }

    // ---------------- list and clock ----------------

    public void Update(long now)
    {
        _now = Clock?.Invoke() ?? now;
        UpdateModDownloads();
        foreach (var game in _games.ToArray())
        {
            game.Update(_now);
            if (game.Phase == NetGamePhase.Lobby && _now - game.LastActivityMicros > _options.LobbyIdleTimeoutMicros)
                CloseGame(game, NetRejectReason.RaceClosed, "lobby idle timeout");
        }
        // Empty games are removed here, never from inside a round update (a round may still be unwinding).
        foreach (var game in _games.ToArray())
            if (game.ShouldRemove) RemoveGame(game);
        // Covers every change since the last update: receives, transport disconnects and this update's own timeouts.
        BroadcastGameListIfChanged();
    }

    void SendGameList(ClientConnection connection)
    {
        var list = new NetGameList(_games.Select(g => g.ToInfo()).OrderBy(i => i.GameId).ToArray());
        _transport.Send(connection.Id, NetChannel.ReliableOrdered, list.Encode());
    }

    void BroadcastGameListIfChanged()
    {
        var list = new NetGameList(_games.Select(g => g.ToInfo()).OrderBy(i => i.GameId).ToArray()).Encode();
        if (_broadcastList != null && list.AsSpan().SequenceEqual(_broadcastList)) return;
        _broadcastList = list;
        foreach (var connection in _connections.Values.ToArray())
        {
            if (connection.Game != null) continue;
            // An idle client that does not drain its lists is dropped like a slow racer instead of growing the backlog.
            if (_transport.PendingReliable(connection.Id) > _options.MaxReliableBacklog)
            {
                CloseConnection(connection, NetRejectReason.SlowClient);
                continue;
            }
            _transport.Send(connection.Id, NetChannel.ReliableOrdered, list);
        }
    }

    void ISessionOutput.Send(ClientConnection connection, NetChannel channel, byte[] data)
    {
        if (_connections.ContainsKey(connection.Id)) _transport.Send(connection.Id, channel, data);
    }

    void ISessionOutput.Disconnect(ClientConnection connection, NetRejectReason reason) => CloseConnection(connection, reason);

    /// <summary>The round core removed the member (it left the race or reported a setup mismatch): the game forgets the member
    /// at once (the core already dropped its seat, so there is nothing to resume) and the connection returns to the list. An
    /// empty game is removed by the next <see cref="Update"/>, never from inside a round callback.</summary>
    void ISessionOutput.LeaveGame(ClientConnection connection)
    {
        if (!_connections.ContainsKey(connection.Id) || connection.Game is not { } game) return;
        game.Leave(connection, _now);
        connection.Epoch = NewEpoch();
        SendGameList(connection);
    }

    int ISessionOutput.PendingReliable(ClientConnection connection) => _transport.PendingReliable(connection.Id);

    void Log(string message) => _log?.Invoke(message);

    // ---------------- shutdown ----------------

    /// <summary>Closes every game and connection with ServerShutdown. Safe to call more than once; the process loop exits on
    /// <see cref="Stopped"/>.</summary>
    public void Shutdown()
    {
        if (_stopping) return;
        _stopping = true;
        foreach (var connection in _connections.Values.ToArray()) CloseConnection(connection, NetRejectReason.ServerShutdown);
        CloseModDownloads(NetRejectReason.ServerShutdown);
        foreach (var game in _games.ToArray()) RemoveGame(game);
        _stopped = true;
        Log("server stopped");
    }

    public void Dispose()
    {
        if (!_stopping) Shutdown();
        foreach (var game in _games.ToArray()) game.Dispose();
        _games.Clear();
    }
}
