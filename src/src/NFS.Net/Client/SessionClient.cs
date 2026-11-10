using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Client;

/// <summary>Connection phase of a <see cref="SessionClient"/>. Lobby/Countdown/Race are sub-phases of
/// <see cref="InGame"/> (see <see cref="NetLobbyState.Phase"/> of <see cref="SessionClient.Lobby"/>).</summary>
public enum SessionClientPhase
{
    /// <summary>Not connected yet (before <see cref="SessionClient.Connect"/>).</summary>
    Idle,
    /// <summary>The hello was sent; waiting for the server's answer (the first games list, or the lobby state of a resumed seat).</summary>
    Connecting,
    /// <summary>Connected, no game: the games list is available and a game can be created or joined.</summary>
    Browsing,
    /// <summary>Member of a game: <see cref="SessionClient.Lobby"/> is the latest complete lobby state.</summary>
    InGame,
    /// <summary>The link was lost; the client reconnects (with the stored token and the same name) until the window ends.</summary>
    Reconnecting,
    /// <summary>Ended: left deliberately, closed by the server, or the reconnect window ran out. <see cref="SessionClient.Connect"/> starts over.</summary>
    Closed,
    /// <summary>The first handshake was refused (<see cref="SessionClient.LastReject"/> says why).</summary>
    Rejected,
}

public enum SessionErrorKind
{
    /// <summary>The server refused the handshake or closed the connection (<see cref="SessionError.Reason"/>).</summary>
    Rejected,
    ConnectionLost,
    ReconnectExpired,
    /// <summary>Game creation was answered with the plain games list (limit reached, name used, bad options).</summary>
    CreateRefused,
    /// <summary>Joining was answered with the plain games list (unknown game, full, running without a reserved seat).</summary>
    JoinRefused,
    /// <summary>The operation needs a connection (or a finished handshake).</summary>
    NotConnected,
    NotInGame,
    /// <summary>Only the operator changes the options and starts the race (checked locally before anything is sent).</summary>
    NotOperator,
    /// <summary>The operation is not possible in the current phase (e.g. options during the countdown, create while in a game).</summary>
    WrongPhase,
    /// <summary>A create/join request is still waiting for the server's answer.</summary>
    Busy,
    InvalidArgument,
}

/// <summary>The last problem of a <see cref="SessionClient"/>: a local refusal of an operation, a refused create/join or a
/// connection problem. <see cref="Reason"/> is set for a server reject/close.</summary>
public sealed record SessionError(SessionErrorKind Kind, string Message, NetRejectReason? Reason = null);

/// <summary>Reconnect policy of a session client.</summary>
public sealed record SessionClientOptions
{
    /// <summary>Data mods of this client, sent in the hello (D9).</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;
    /// <summary>Cars of this client (original + mod cars) for converting car numbers to lobby keys; null = the original cars only.</summary>
    public NFS.Game.Cars.CarCatalog? Cars { get; init; }
    /// <summary>Reconnect automatically after the link is lost (transport timeout). A final server answer never reconnects.</summary>
    public bool AutoReconnect { get; init; } = true;
    /// <summary>How long after the first noticed loss attempts are made (the server's default grace window is 30 s).</summary>
    public long ReconnectWindowMicros { get; init; } = 30_000_000;
    /// <summary>Minimum time between two connection attempts.</summary>
    public long RetryIntervalMicros { get; init; } = 250_000;
    /// <summary>Hook of every round's <see cref="RaceClient"/> (race data loading/validation of the welcome; false = leave the
    /// race). Null = ready at once. See the <c>prepare</c> parameter of <see cref="RaceClient"/>.</summary>
    public Func<NetWelcome, bool>? PrepareRace { get; init; }
}

/// <summary>Counters of a session client (diagnostics, tests).</summary>
public sealed class SessionClientStats
{
    public long Attempts, GameLists, LobbyStates, StaleLobbyStates, RaceMessages, DroppedRaceMessages, Malformed, LocalRefusals,
        RefusedCreates, RefusedJoins, Reconnects, Resumes, RaceClients, StaleWelcomes, BufferedRaceMessages, DiscardedRaceMessages;
}

/// <summary>Receives a race-protocol message (<c>Welcome</c> … <c>ResultsAck</c> types) unchanged, with its channel.</summary>
/// <summary>One received chat line; the sender name was set by the server.</summary>
public sealed record ChatLine(string Sender, string Text);

public delegate void RaceMessageHandler(NetChannel channel, ReadOnlySpan<byte> data);

/// <summary>
/// Client of the session layer (change network-game-sessions, task 4.1): handshake, games list, create/join, lobby state,
/// readiness, car, options and start by the operator, leaving a game, the resume token and automatic reconnect. It is
/// headless and independent of sockets and the wall clock (the caller feeds the transport and calls <see cref="Update"/>).
/// <para>Handshake: the hello (name, versions, data fingerprint, optional resume token) travels as connection data, so the
/// server's answer is either a disconnect carrying a <see cref="NetReject"/> (<see cref="SessionClientPhase.Rejected"/>) or
/// the first message: the games list (<see cref="SessionClientPhase.Browsing"/>) or, when the name/token resumed a reserved
/// seat, the lobby state (<see cref="SessionClientPhase.InGame"/>).</para>
/// <para>The server answers a refused create/join with the plain games list instead of a lobby state. Every request is
/// therefore followed by a list request, so a refusal always yields two lists while a success yields the lobby state first:
/// the second list without a lobby state is reported as <see cref="SessionErrorKind.CreateRefused"/>/<see cref="SessionErrorKind.JoinRefused"/>.
/// A burst of stale broadcast lists that overtakes the answer could be taken for a refusal; the lobby state that follows
/// supersedes it (the error is cleared). Operations that the server silently ignores (options/start by a non-operator,
/// wrong phase) are refused locally with a <see cref="SessionError"/> and never sent.</para>
/// <para>Race (task 4.2): every race welcome starts a new <see cref="RaceClient"/> (<see cref="Race"/>, announced by
/// <see cref="RaceClientChanged"/>) on <see cref="RaceTransport"/> - the same connection, no hello of its own - and all later
/// race messages go to it, so an old round can never take over a message of a new one (the client checks race id and epoch of
/// the welcome of its own round). A resume welcome (same race, fresh epoch) and a welcome of the next round start a clean
/// instance as well; a welcome already seen on this session is ignored. The finished round stays in <see cref="Race"/> (its
/// results) while the lobby/countdown of the next round goes on, until the next welcome replaces it; leaving the game or
/// landing on the games list after a reconnect drops it. After a resume/join the server sends the welcome before the lobby
/// state, so race messages that arrive before the session is in the game are kept and delivered once the lobby state is
/// accepted. The owner drives <see cref="RaceClient.Update"/> (e.g. through the provider). Messages are also forwarded
/// unchanged to <see cref="RaceMessage"/>. The session keeps <see cref="GameId"/> and the connection <see cref="Epoch"/>
/// of the lobby messages.</para>
/// <para>Reconnect: one <see cref="SessionClient"/> outlives connections. Each connection attempt has its own handler, so a late
/// callback of an old attempt is ignored. After a loss the client opens new connections with the same name and the stored
/// token (a seat reserved in Countdown/Race resumes; a lost lobby seat is gone and the client lands on the games list).
/// A final server answer (bad token, shutdown, slow client …) ends the session; the token is cleared when it is invalid.</para>
/// </summary>
public sealed class SessionClient
{
    /// <summary>Opens a connection: creates the transport endpoint for <paramref name="handler"/>, starts connecting with
    /// <paramref name="hello"/> as connection data and returns the transport.</summary>
    public delegate INetClientTransport OpenTransport(INetClientHandler handler, byte[] hello);

    readonly byte[] _fingerprint;
    readonly OpenTransport _open;
    readonly Func<long> _clock;
    readonly SessionClientOptions _options;
    readonly Action<string>? _log;
    Attempt? _current;
    INetClientTransport? _transport;
    int _attemptId;
    bool _accepted;
    bool _pending;
    bool _hasTarget;
    int _listsSinceRequest;
    string _targetName = "";
    int _targetGameId;
    uint _staleEpoch;
    long _lostAt = long.MinValue, _lastAttempt = long.MinValue;
    bool _lostWasInGame;
    byte[]? _token;
    SessionClientPhase _phase = SessionClientPhase.Idle;
    readonly RaceTransportAdapter _raceTransport;
    readonly List<(NetChannel Channel, byte[] Data)> _raceBuffer = [];
    readonly HashSet<(int RaceId, uint Epoch)> _seenWelcomes = [];
    const int RaceBufferLimit = 512;

    public SessionClient(byte[] dataFingerprint, string name, OpenTransport open, Func<long> clock,
        SessionClientOptions? options = null, Action<string>? log = null)
    {
        if (dataFingerprint.Length != 32) throw new ArgumentException("data fingerprint must be 32 bytes", nameof(dataFingerprint));
        if (name.Length is < 1 or > NetProtocol.MaxNameLength || name.Any(c => c is < ' ' or >= (char)0x7F))
            throw new ArgumentException($"name must be 1..{NetProtocol.MaxNameLength} printable ASCII characters", nameof(name));
        _fingerprint = dataFingerprint;
        Name = name;
        _open = open;
        _clock = clock;
        _options = options ?? new SessionClientOptions();
        _log = log;
        _raceTransport = new RaceTransportAdapter(this);
    }

    // ---------------- state ----------------

    public string Name { get; }
    public SessionClientPhase Phase => _phase;
    public SessionClientStats Stats { get; } = new();
    /// <summary>Latest games list (empty before the first one; kept while in a game).</summary>
    public IReadOnlyList<NetGameInfo> Games { get; private set; } = [];
    /// <summary>Latest complete lobby state while <see cref="SessionClientPhase.InGame"/> (and the last known one while
    /// <see cref="SessionClientPhase.Reconnecting"/>); null otherwise.</summary>
    public NetLobbyState? Lobby { get; private set; }
    public int GameId => Lobby?.GameId ?? 0;
    /// <summary>Connection epoch of the lobby/game messages (0 outside a game).</summary>
    public uint Epoch => Lobby?.Epoch ?? 0;
    public NetLobbyMember? Me => Lobby?.Members.FirstOrDefault(m => string.Equals(m.Name, Name, StringComparison.OrdinalIgnoreCase));
    public bool IsOperator => Me?.Operator == true;
    public bool IsReady => Me?.Ready == true;
    /// <summary>Resume token of the seat (null before the first join and after the seat was given up). May be set before
    /// <see cref="Connect"/> (e.g. kept by the application across a restart); an invalid token is cleared by the client.</summary>
    public byte[]? ResumeToken
    {
        get => _token;
        set => _token = value is { Length: 0 } ? null : value;
    }
    public SessionError? LastError { get; private set; }
    /// <summary>Last reject or server close that ended a connection (a lost link is reported as <see cref="NetRejectReason.Timeout"/>).</summary>
    public NetReject? LastReject { get; private set; }
    /// <summary>A create/join request waits for the server's answer.</summary>
    public bool Pending => _pending;
    /// <summary>Connection attempts so far, including the first one (also in <see cref="SessionClientStats.Attempts"/>).</summary>
    public int Attempts => (int)Stats.Attempts;
    public bool Connected => _phase is SessionClientPhase.Browsing or SessionClientPhase.InGame;
    /// <summary>Time left of the reconnect window (the client gives up when it runs out) while <see cref="SessionClientPhase.Reconnecting"/>;
    /// null otherwise. Read from the client's own clock.</summary>
    public long? ReconnectRemainingMicros => _phase == SessionClientPhase.Reconnecting && _lostAt != long.MinValue
        ? Math.Max(0, _options.ReconnectWindowMicros - (_clock() - _lostAt)) : null;
    /// <summary>Transport of the current connection for the race client (task 4.2): sends on the same connection with no hello
    /// of its own; <c>Disconnect()</c> ends the whole session.</summary>
    public INetClientTransport RaceTransport => _raceTransport;
    /// <summary>Race client of the latest round of the current game (null before the first welcome, after leaving the game and
    /// after a reconnect that did not resume the seat). A new instance for every round and every resume.</summary>
    public RaceClient? Race { get; private set; }
    /// <summary>Most recent chat lines of the current game, oldest first (at most <see cref="ChatHistoryLimit"/>); cleared when the
    /// client enters another game or the session ends.</summary>
    public IReadOnlyList<ChatLine> Chat => _chat;
    public const int ChatHistoryLimit = 20;
    readonly List<ChatLine> _chat = [];

    // ---------------- events ----------------

    public event Action<SessionClientPhase>? PhaseChanged;
    public event Action<IReadOnlyList<NetGameInfo>>? GamesChanged;
    /// <summary>Every accepted lobby state (also those that change nothing visible: the server repeats complete states).</summary>
    public event Action<NetLobbyState>? LobbyChanged;
    public event Action<SessionError>? Error;
    /// <summary>A reconnect succeeded; the argument tells whether the reserved seat was resumed (lobby state follows) or the
    /// client is back on the games list.</summary>
    public event Action<bool>? Reconnected;
    /// <summary>Race messages of the game's round, unchanged (hook for the race client, task 4.2).</summary>
    public event RaceMessageHandler? RaceMessage;
    /// <summary>A new round's <see cref="RaceClient"/> was created, or the current one was dropped (null).</summary>
    public event Action<RaceClient?>? RaceClientChanged;
    /// <summary>A chat line arrived (it is already in <see cref="Chat"/>).</summary>
    public event Action<ChatLine>? ChatReceived;

    // ---------------- connection ----------------

    /// <summary>Starts the session: opens the first connection. Allowed in <see cref="SessionClientPhase.Idle"/>,
    /// <see cref="SessionClientPhase.Closed"/> and <see cref="SessionClientPhase.Rejected"/> (a fresh start keeping the token).</summary>
    public bool Connect()
    {
        if (_phase is not (SessionClientPhase.Idle or SessionClientPhase.Closed or SessionClientPhase.Rejected))
            return Refuse(SessionErrorKind.WrongPhase, $"already {_phase}");
        Games = [];
        Lobby = null;
        _chat.Clear();
        LastError = null;
        LastReject = null;
        ClearRequest();
        DropRace();
        SetPhase(SessionClientPhase.Connecting);
        StartAttempt();
        return true;
    }

    /// <summary>Drives the reconnect (retry spacing and the window); call it regularly with the same clock the client uses.</summary>
    public void Update(long now)
    {
        if (_phase != SessionClientPhase.Reconnecting) return;
        if (now - _lostAt >= _options.ReconnectWindowMicros)
        {
            DropTransport();
            _current = null;
            Fail(SessionErrorKind.ReconnectExpired, $"reconnect window ({_options.ReconnectWindowMicros / 1000} ms) expired", null);
            SetPhase(SessionClientPhase.Closed);
            return;
        }
        if (_current != null) return;   // an attempt is still connecting
        if (_lastAttempt != long.MinValue && now - _lastAttempt < _options.RetryIntervalMicros) return;
        _lastAttempt = now;
        StartAttempt();
    }

    /// <summary>Ends the session on purpose: disconnects and never reconnects. The seat of a running race stays reserved for
    /// its grace window on the server like after a lost link.</summary>
    public void Disconnect()
    {
        if (_phase is SessionClientPhase.Idle or SessionClientPhase.Closed or SessionClientPhase.Rejected) return;
        DropTransport();
        _current = null;
        if (Lobby is { Phase: NetGamePhase.Lobby }) _token = null;   // the server frees a lobby seat at once
        Lobby = null;
        ClearRequest();
        CloseRace(null);
        SetPhase(SessionClientPhase.Closed);
    }

    /// <summary>Forgets the resume token (a later connection is a fresh one, or resumes by name only).</summary>
    public void ForgetToken() => _token = null;

    // ---------------- operations ----------------

    public bool RequestGames()
    {
        if (!Connected) return Refuse(SessionErrorKind.NotConnected, "not connected");
        return Transmit(new NetGameListRequest().Encode());
    }

    public bool CreateGame(string name, int maxPlayers, NetGameMode mode = NetGameMode.SingleRace) =>
        CreateGame(new NetGameCreate(name, maxPlayers, mode));

    /// <summary>Creates a game; the creator becomes its operator and the lobby state answers. A refusal (limit, used name)
    /// is reported when the server answers with the games list.</summary>
    public bool CreateGame(NetGameCreate request)
    {
        if (!CanRequest()) return false;
        byte[] bytes;
        try
        {
            bytes = request.Encode();
        }
        catch (ArgumentException e)
        {
            return Refuse(SessionErrorKind.InvalidArgument, e.Message);
        }
        LastError = null;
        _raceBuffer.Clear();
        _pending = true;
        _hasTarget = true;
        _targetName = request.Name;
        _targetGameId = 0;
        return TransmitRequest(bytes);
    }

    /// <summary>Joins a game of the list (also the game whose reserved seat this client holds, in any phase).</summary>
    public bool JoinGame(int gameId)
    {
        if (!CanRequest()) return false;
        if (!NetProtocol.IsGameId(gameId)) return Refuse(SessionErrorKind.InvalidArgument, "game id must be positive");
        LastError = null;
        _raceBuffer.Clear();
        _pending = true;
        _hasTarget = true;
        _targetName = "";
        _targetGameId = gameId;
        return TransmitRequest(new NetGameJoin(gameId).Encode());
    }

    /// <summary>Leaves the current game and returns to <see cref="SessionClientPhase.Browsing"/> at once (the server answers with
    /// a fresh list). In Countdown/Race the seat stays reserved on the server, so the same game can be rejoined from the list
    /// and the token is kept; leaving the lobby frees the seat and the token is dropped.</summary>
    public bool LeaveGame()
    {
        if (!InGameLobby(out var lobby)) return false;
        if (!Transmit(new NetGameLeave(lobby.GameId, lobby.Epoch).Encode())) return false;
        _staleEpoch = lobby.Epoch;
        if (lobby.Phase == NetGamePhase.Lobby) _token = null;
        Lobby = null;
        _chat.Clear();
        ClearRequest();
        DropRace();
        SetPhase(SessionClientPhase.Browsing);
        return true;
    }

    public bool SetReady(bool ready) =>
        LobbyPhaseOnly(NetGamePhase.Lobby, requireOperator: false, out var l) && Transmit(new NetLobbyReady(l.GameId, l.Epoch, ready).Encode());

    public bool SetCar(int carType, int colour = NetProtocol.NoColour)
    {
        if (NetCars.KeyOf(carType, _options.Cars) is not { } key)
            return Refuse(SessionErrorKind.InvalidArgument, $"unknown car number {carType}");
        return SetCar(key, colour);
    }

    /// <summary>Selects the own car by key (the server accepts the cars of its catalog: showcase cars and mod cars).</summary>
    public bool SetCar(NFS.ModApi.CarKey car, int colour = NetProtocol.NoColour)
    {
        if (!InGameLobby(out var l)) return false;
        if (l.Phase == NetGamePhase.Race) return Refuse(SessionErrorKind.WrongPhase, "the car cannot be changed during the race");
        if (!NetProtocol.IsColourOrNone(colour)) return Refuse(SessionErrorKind.InvalidArgument, "invalid car colour");
        return Transmit(new NetLobbyCar(l.GameId, l.Epoch, car, colour).Encode());
    }

    /// <summary>Operator only: the options of the next race (lobby phase only). An option left null keeps its current value in
    /// the lobby state, so a caller changes one option without restating the others.</summary>
    public bool SetOptions(int track, int laps, bool backward = false, int? bots = null, bool? traffic = null, int? skill = null,
        bool? catchUp = null, int? style = null)
    {
        if (!LobbyPhaseOnly(NetGamePhase.Lobby, requireOperator: true, out var l)) return false;
        if (!NetProtocol.IsTrack(track) || !NetProtocol.IsLaps(laps))
            return Refuse(SessionErrorKind.InvalidArgument, "invalid track or lap count");
        var options = new NetLobbyOptions(l.GameId, l.Epoch, track, laps, backward, bots ?? l.Bots, traffic ?? l.Traffic,
            skill ?? l.Skill, catchUp ?? l.CatchUp, style ?? l.Style);
        byte[] bytes;
        try { bytes = options.Encode(); }
        catch (ArgumentException e) { return Refuse(SessionErrorKind.InvalidArgument, e.Message); }
        return Transmit(bytes);
    }

    /// <summary>Operator only: starts the countdown (lobby phase only).</summary>
    public bool StartRace() =>
        LobbyPhaseOnly(NetGamePhase.Lobby, requireOperator: true, out var l) && Transmit(new NetLobbyStart(l.GameId, l.Epoch).Encode());

    /// <summary>Operator only: aborts the start countdown (countdown phase only); the server answers with the lobby state.</summary>
    public bool CancelStart() =>
        LobbyPhaseOnly(NetGamePhase.Countdown, requireOperator: true, out var l) && Transmit(new NetLobbyCancel(l.GameId, l.Epoch).Encode());

    /// <summary>Acknowledges the summary screen (refreshes the activity stamp on the server; the game is already in the lobby).</summary>
    public bool ReturnToLobby() =>
        InGameLobby(out var l) && Transmit(new NetLobbyReturn(l.GameId, l.Epoch).Encode());

    /// <summary>Sends a chat line to the game (lobby, countdown and race alike). The text is reduced to printable ASCII, trimmed and
    /// cut to <see cref="NetProtocol.MaxChatLength"/>; an empty result is refused. The server adds the sender and may drop the line
    /// when the sender talks too fast.</summary>
    public bool SendChat(string text)
    {
        if (!InGameLobby(out var l)) return false;
        string clean = NetProtocol.SanitizeChat(text);
        if (clean.Length == 0) return Refuse(SessionErrorKind.InvalidArgument, "empty chat text");
        return Transmit(new NetChatSend(l.GameId, l.Epoch, clean).Encode());
    }

    /// <summary>Sends a race message on the session connection (used by the race client through <see cref="RaceTransport"/>).</summary>
    public bool SendRace(NetChannel channel, byte[] data)
    {
        if (!Connected || _transport == null) return false;
        _transport.Send(channel, data);
        return true;
    }

    // ---------------- guards ----------------

    bool CanRequest()
    {
        if (_phase == SessionClientPhase.InGame) return Refuse(SessionErrorKind.WrongPhase, "already in a game; leave it first");
        if (_phase != SessionClientPhase.Browsing) return Refuse(SessionErrorKind.NotConnected, $"not connected ({_phase})");
        if (_pending) return Refuse(SessionErrorKind.Busy, "a create/join request is still waiting for the server");
        return true;
    }

    bool InGameLobby(out NetLobbyState lobby)
    {
        lobby = Lobby!;
        if (_phase == SessionClientPhase.InGame && Lobby != null) return true;
        return Refuse(_phase is SessionClientPhase.Browsing ? SessionErrorKind.NotInGame : SessionErrorKind.NotConnected,
            _phase is SessionClientPhase.Browsing ? "not in a game" : $"not connected ({_phase})");
    }

    bool LobbyPhaseOnly(NetGamePhase phase, bool requireOperator, out NetLobbyState lobby)
    {
        if (!InGameLobby(out lobby)) return false;
        if (lobby.Phase != phase) return Refuse(SessionErrorKind.WrongPhase, $"not possible while the game is {lobby.Phase}");
        if (requireOperator && !IsOperator) return Refuse(SessionErrorKind.NotOperator, "only the operator can do this");
        return true;
    }

    /// <summary>Sends a create/join request followed by a games list request. The server answers a refused request with the
    /// plain list and a successful one with the lobby state; the list request makes sure that a refusal produces at least two
    /// lists (answer + reply), which separates it from a single stale broadcast list that overtakes the answer.</summary>
    bool TransmitRequest(byte[] request)
    {
        _listsSinceRequest = 0;
        if (!Transmit(request)) return false;
        return Transmit(new NetGameListRequest().Encode());
    }

    bool Transmit(byte[] data)
    {
        if (_transport == null) return Refuse(SessionErrorKind.NotConnected, "no transport");
        _transport.Send(NetChannel.ReliableOrdered, data);
        return true;
    }

    bool Refuse(SessionErrorKind kind, string message)
    {
        Stats.LocalRefusals++;
        Fail(kind, message, null);
        return false;
    }

    void Fail(SessionErrorKind kind, string message, NetRejectReason? reason)
    {
        LastError = new SessionError(kind, message, reason);
        Log($"error {kind}: {message}");
        Error?.Invoke(LastError);
    }

    // ---------------- connection attempts ----------------

    void StartAttempt()
    {
        var attempt = new Attempt(this, ++_attemptId);
        _current = attempt;
        _transport = null;
        _accepted = false;
        _raceBuffer.Clear();
        Stats.Attempts++;
        Log($"connection attempt {Stats.Attempts}");
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint, Name, NetProtocol.NoCarType,
            BuildInfo.AppVersion, _token) { Mods = _options.Mods }.Encode();
        var transport = _open(attempt, hello);
        if (_current == attempt) _transport = transport;
    }

    void DropTransport()
    {
        var t = _transport;
        _transport = null;
        t?.Disconnect();
    }

    void SetPhase(SessionClientPhase phase)
    {
        if (_phase == phase) return;
        _phase = phase;
        Log($"phase {phase}");
        PhaseChanged?.Invoke(phase);
    }

    void ClearRequest()
    {
        _pending = false;
        _hasTarget = false;
        _targetName = "";
        _targetGameId = 0;
    }

    void OnAttemptDisconnected(Attempt attempt, byte[]? data)
    {
        if (attempt != _current) return;
        _current = null;
        _transport = null;
        var reject = data == null ? new NetReject(NetRejectReason.Timeout) : NetReject.TryDecode(data) ?? new NetReject(NetRejectReason.Malformed);
        LastReject = reject;
        Log($"disconnected: {reject.Reason}");
        bool tokenGone = reject.Reason == NetRejectReason.ResumeTokenInvalid;
        bool hadToken = _token != null;
        if (tokenGone) _token = null;
        bool wasConnected = _accepted;
        bool transient = reject.Reason is NetRejectReason.NameTaken or NetRejectReason.Timeout or NetRejectReason.Left;
        if (_phase == SessionClientPhase.Reconnecting && !wasConnected)
        {
            // A reconnect attempt that failed before the server answered: transient answers retry inside the window. A token
            // the server no longer knows (the reservation ended, the game is gone) is dropped and the next attempt connects by
            // name only, which lands on the games list; the same answer to a hello without a token is final.
            if (transient || (tokenGone && hadToken)) return;
            EndSession(reject, handshake: false);
            return;
        }
        if (_phase == SessionClientPhase.Connecting)
        {
            EndSession(reject, handshake: true);
            return;
        }
        if (reject.Reason == NetRejectReason.Timeout && _options.AutoReconnect && _phase is SessionClientPhase.Browsing or SessionClientPhase.InGame)
        {
            // A lost link (the transport saw no server answer). Keep the last lobby for display; the token stays.
            _lostWasInGame = _phase == SessionClientPhase.InGame;
            // A lobby seat is freed by the server as soon as it notices the loss, so its token is useless; a seat of a
            // Countdown/Race stays reserved for the grace window and the token (or the name) resumes it.
            if (Lobby is { Phase: NetGamePhase.Lobby }) _token = null;
            _lostAt = _clock();
            _lastAttempt = long.MinValue;
            ClearRequest();
            Fail(SessionErrorKind.ConnectionLost, "connection lost; reconnecting", NetRejectReason.Timeout);
            SetPhase(SessionClientPhase.Reconnecting);
            return;
        }
        EndSession(reject, handshake: false);
    }

    void EndSession(NetReject reject, bool handshake)
    {
        Lobby = null;
        _chat.Clear();
        ClearRequest();
        CloseRace(reject);
        if (reject.Reason == NetRejectReason.Timeout && !handshake) Fail(SessionErrorKind.ConnectionLost, "connection lost", reject.Reason);
        else Fail(SessionErrorKind.Rejected, $"server closed the connection: {reject.Reason}", reject.Reason);
        SetPhase(handshake ? SessionClientPhase.Rejected : SessionClientPhase.Closed);
    }

    // ---------------- receive ----------------

    void OnAttemptReceive(Attempt attempt, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (attempt != _current) return;
        try
        {
            if (data.Length > NetProtocol.MaxReliableMessage) throw new NetProtocolException("oversized server message");
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            switch (h.Type)
            {
                case NetMessageType.GameList: OnGameList(NetGameList.Decode(h, ref r)); break;
                case NetMessageType.LobbyState: OnLobbyState(NetLobbyState.Decode(h, ref r)); break;
                case NetMessageType.ChatMessage: OnChat(NetChatMessage.Decode(h, ref r)); break;
                case >= NetMessageType.Welcome and <= NetMessageType.ResultsAck:
                    Stats.RaceMessages++;
                    if (RaceMessage is { } hook) hook(channel, data);
                    else if (Race == null) Stats.DroppedRaceMessages++;
                    OnRaceMessage(channel, data.ToArray());
                    break;
                default:
                    throw new NetProtocolException($"unexpected message {h.Type} from the server");
            }
        }
        catch (NetProtocolException e)
        {
            Stats.Malformed++;
            Log($"malformed message: {e.Message}");
        }
    }

    void OnChat(NetChatMessage message)
    {
        if (_phase != SessionClientPhase.InGame || Lobby == null || message.GameId != Lobby.GameId) return;
        var line = new ChatLine(message.Sender, message.Text);
        _chat.Add(line);
        if (_chat.Count > ChatHistoryLimit) _chat.RemoveRange(0, _chat.Count - ChatHistoryLimit);
        ChatReceived?.Invoke(line);
    }

    void OnGameList(NetGameList list)
    {
        Stats.GameLists++;
        Games = list.Games;
        bool first = !_accepted;
        _accepted = true;
        if (first)
        {
            bool reconnect = _phase == SessionClientPhase.Reconnecting;
            // The server did not resume a seat: a lobby seat of the lost connection is gone.
            if (reconnect) { Lobby = null; DropRace(); }
            _raceBuffer.Clear();
            ClearRequest();
            SetPhase(SessionClientPhase.Browsing);
            GamesChanged?.Invoke(Games);
            if (reconnect) OnReconnected(resumed: false);
            return;
        }
        if (_pending && _phase == SessionClientPhase.Browsing && ++_listsSinceRequest >= 2)
        {
            _pending = false;
            if (_targetGameId != 0)
            {
                Stats.RefusedJoins++;
                var info = list.Games.FirstOrDefault(g => g.GameId == _targetGameId);
                string why = info == null ? "the game no longer exists"
                    : info.Players >= info.MaxPlayers ? "the game is full"
                    : info.Phase != NetGamePhase.Lobby ? $"the game is already in {info.Phase}"
                    : "the server refused the join";
                Fail(SessionErrorKind.JoinRefused, why, null);
            }
            else
            {
                Stats.RefusedCreates++;
                bool nameUsed = list.Games.Any(g => string.Equals(g.Name, _targetName, StringComparison.OrdinalIgnoreCase));
                Fail(SessionErrorKind.CreateRefused,
                    nameUsed ? $"a game named '{_targetName}' already exists" : "the server refused to create the game (game limit or options)", null);
            }
        }
        GamesChanged?.Invoke(Games);
    }

    void OnLobbyState(NetLobbyState state)
    {
        bool resume = !_accepted;
        bool expected = _phase == SessionClientPhase.InGame && Lobby != null && state.GameId == Lobby.GameId
            || resume
            || _hasTarget && (_targetGameId != 0 ? state.GameId == _targetGameId
                : string.Equals(state.GameName, _targetName, StringComparison.OrdinalIgnoreCase));
        if (!expected || (_phase != SessionClientPhase.InGame && state.Epoch == _staleEpoch))
        {
            // Left that game (or never asked for it): a state sent before the server processed our leave.
            Stats.StaleLobbyStates++;
            return;
        }
        Stats.LobbyStates++;
        _accepted = true;
        bool reconnect = resume && _phase == SessionClientPhase.Reconnecting;
        bool answered = _pending || LastError is { Kind: SessionErrorKind.CreateRefused or SessionErrorKind.JoinRefused };
        if (answered) LastError = null;
        ClearRequest();
        if (Lobby == null || Lobby.GameId != state.GameId) _chat.Clear();
        Lobby = state;
        if (state.ResumeToken is { } token) _token = token;
        if (resume) Stats.Resumes++;
        SetPhase(SessionClientPhase.InGame);
        FlushRaceBuffer();
        LobbyChanged?.Invoke(state);
        if (reconnect) OnReconnected(resumed: true);
    }

    // ---------------- race rounds ----------------

    void OnRaceMessage(NetChannel channel, byte[] data)
    {
        if (_phase == SessionClientPhase.InGame) { RouteRace(channel, data); return; }
        // Before the lobby state of a resumed/joined seat the server already sends the round's welcome and baseline.
        bool awaiting = _phase is SessionClientPhase.Connecting or SessionClientPhase.Reconnecting
            || _phase == SessionClientPhase.Browsing && _pending && _hasTarget;
        if (!awaiting) return;
        if (_raceBuffer.Count >= RaceBufferLimit) { Stats.DiscardedRaceMessages++; return; }
        _raceBuffer.Add((channel, data));
        Stats.BufferedRaceMessages++;
    }

    void FlushRaceBuffer()
    {
        if (_raceBuffer.Count == 0) return;
        var pending = _raceBuffer.ToArray();
        _raceBuffer.Clear();
        foreach (var (channel, data) in pending)
        {
            try { RouteRace(channel, data); }
            catch (NetProtocolException e)
            {
                Stats.Malformed++;
                Log($"malformed race message: {e.Message}");
            }
        }
    }

    void RouteRace(NetChannel channel, byte[] data)
    {
        if (data[0] == (byte)NetMessageType.Welcome)
        {
            var welcome = NetWelcome.Decode(data);
            if (!_seenWelcomes.Add((welcome.RaceId, welcome.Epoch)))
            {
                Stats.StaleWelcomes++;   // a replayed welcome never replaces the live round
                return;
            }
            var client = new RaceClient(_raceTransport, _fingerprint, Name, _options.PrepareRace,
                _log == null ? null : s => _log($"[race {Name}] {s}")) { Clock = _clock };
            Race = client;
            Stats.RaceClients++;
            Log($"round {welcome.RaceId:X8} epoch {welcome.Epoch:X8}{(welcome.Resume ? " (resume)" : "")}: new race client");
            RaceClientChanged?.Invoke(client);
        }
        Race?.OnReceive(channel, data);
    }

    void DropRace()
    {
        _raceBuffer.Clear();
        if (Race == null) return;
        Race = null;
        RaceClientChanged?.Invoke(null);
    }

    /// <summary>The session ended: the round's client learns about it like a direct client would (the reject, or a timeout).</summary>
    void CloseRace(NetReject? reject) => Race?.OnDisconnected(reject?.Encode());

    void OnReconnected(bool resumed)
    {
        Stats.Reconnects++;
        _lostAt = long.MinValue;
        Log($"reconnected ({(resumed ? "seat resumed" : _lostWasInGame ? "seat not resumed" : "back on the list")})");
        Reconnected?.Invoke(resumed);
    }

    void Log(string message) => _log?.Invoke($"[session {Name}] {message}");

    // ---------------- helpers ----------------

    /// <summary>Handler of one connection attempt; stale attempts are ignored by the client.</summary>
    sealed class Attempt(SessionClient owner, int id) : INetClientHandler
    {
        public int Id { get; } = id;
        public void OnConnected() => owner.Log($"transport connected (attempt {Id})");
        public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data) => owner.OnAttemptReceive(this, channel, data);
        public void OnDisconnected(byte[]? reason) => owner.OnAttemptDisconnected(this, reason);
    }

    sealed class RaceTransportAdapter(SessionClient owner) : INetClientTransport
    {
        public void Send(NetChannel channel, byte[] data) => owner.SendRace(channel, data);
        public void Disconnect() => owner.Disconnect();
        public long RoundTripMicros => owner._transport?.RoundTripMicros ?? 0;
    }
}
