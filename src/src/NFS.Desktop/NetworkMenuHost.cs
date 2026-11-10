using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NFS.Game.FrontEnd;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// The window-independent host of the network menu (change network-game-sessions, task 5.3): owns the
/// <see cref="NetworkMenuController"/>, the <see cref="RemoteDesktopSession"/> (always a client of an external session server,
/// never a server in this process) and the race presentation of the current round, and carries out the controller's
/// <see cref="NetMenuAction"/>s. <c>Game1</c> only feeds it input and time, draws what it exposes and plays the race audio of
/// <see cref="Drive"/>.
/// <para>Flow: <see cref="NetMenuAction.Connect"/> resolves the address and opens a session client which is attached to the
/// menu; the menu follows the server's game phase. <see cref="NetMenuAction.EnterRace"/> makes the host build a
/// <see cref="RaceDrive"/> on the round's provider as soon as the baseline arrived; <see cref="NetMenuAction.LeaveRace"/> (the
/// summary, a left game, a lost session) drops it and releases the round. A round that replaces the shown one (the next race, or
/// the resumed seat after a reconnect) swaps the drive. In the race the pause menu's "leave" asks the menu for the exit
/// confirmation (<see cref="NetworkMenuController.RequestExit"/>).</para></summary>
internal sealed class NetworkMenuHost : IDisposable
{
    readonly string _gameDir;
    readonly Func<int> _carType;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Func<string, int, CancellationToken, Task<IPEndPoint>> _resolve;
    RemoteDesktopSession? _session;
    CancellationTokenSource? _resolving;
    Task<IPEndPoint>? _resolveTask;
    string _connectNick = "";
    bool _wantRace;
    bool _disposed;
    int _carGameId;
    int _pauseSelection;
    bool _orbit, _fixed;

    /// <param name="gameDir">Game data directory (race rounds are loaded from the server's welcome).</param>
    /// <param name="carType">Car model of the player's menu setup, sent once on entering each game lobby; a value that is not a
    /// selectable model (<see cref="NetProtocol.IsCarType"/>) is not sent.</param>
    /// <param name="resolve">Host name to endpoint (default: IP literal or DNS, IPv4); injectable for tests.</param>
    /// <param name="remembered">The nick and the server come from the saved settings (not the first run): the cursor starts on CONNECT.</param>
    public NetworkMenuHost(string? gameDir, Func<int> carType, string nick, string host = "127.0.0.1", int port = NetworkMenuController.DefaultPort,
        Func<string, int, CancellationToken, Task<IPEndPoint>>? resolve = null, bool remembered = false)
    {
        _gameDir = gameDir ?? "";
        _carType = carType;
        _resolve = resolve ?? Resolve;
        Menu = new NetworkMenuController(nick, host, port, remembered) { TrackNames = NetworkTrackNames.Load(gameDir), CarNames = NetworkCarNames.Load(gameDir), Cars = NFS.Net.Server.ServerLoop.ModCars(gameDir), VictoryArt = LoadVictory };
    }

    VictoryScreen? _victory;
    /// <summary>Victory art (VICTORY.QFS, English text: the network menu has no language setting); loaded once on first use.</summary>
    VictoryScreen LoadVictory() => _victory ??= new VictoryScreen(_gameDir.Length == 0 ? null : _gameDir);

    public NetworkMenuController Menu { get; }
    /// <summary>Chat lines and the line being typed (key T; see <see cref="NetworkChat"/>).</summary>
    public NetworkChat Chat { get; } = new();
    /// <summary>Raised when the player confirms Connect (nick, host, port): the owner remembers the entries.</summary>
    public event Action<string, string, int>? Connecting;
    /// <summary>How long a connection attempt may stay unanswered before it is given up.</summary>
    public long HandshakeTimeoutMicros { get; init; } = RemoteDesktopSession.DefaultHandshakeTimeoutMicros;
    public RemoteDesktopSession? Session => _session;
    /// <summary>The race of the current round; non-null only while the menu shows the race (<see cref="NetScreen.Race"/>).</summary>
    public RaceDrive? Drive
    {
        get => _drive;
        private set
        {
            // Saved when the drive changes (not a replay, != 1): the network race stores its HUD choices too. [ref:1307]
            if (!ReferenceEquals(_drive, value)) SaveHudcon(_drive);
            _drive = value;
        }
    }
    RaceDrive? _drive;
    /// <summary>User folder of HUDCON.DAT (shared with the local races); null = no persistence. Set before connecting.</summary>
    public string? HudconDir { get; set; }

    void SaveHudcon(RaceDrive? drive)
    {
        if (HudconDir != null && drive != null && HudConPersistence.ShouldSave(drive))
            HudConPersistence.Save(HudconDir, drive.HudconSnapshot());
    }
    /// <summary>The player left the network menu (back on the connect screen): the window returns to the main menu.</summary>
    public bool ExitRequested { get; private set; }
    public long NowMicros => _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
    /// <summary>The menu follows a round and a drive is up (the race picture is drawn instead of the menu panels).</summary>
    public bool RaceShown => Drive != null;
    public int PauseSelection => _pauseSelection;
    /// <summary>The shown round is only watched: the player was eliminated from the knockout series and the server streams it the race.</summary>
    public bool Spectating => Drive != null && _session?.Client?.Spectator == true;

    /// <summary>Splits the <c>--server HOST[:PORT]</c> option into the Connect screen's host and port (default port when absent
    /// or not a number; a bare IPv6 literal with several colons is a host).</summary>
    public static (string Host, int Port) SplitServer(string server)
    {
        server = server.Trim();
        int colon = server.LastIndexOf(':');
        if (colon > 0 && server.IndexOf(':') == colon && int.TryParse(server[(colon + 1)..], out int port) && port is >= 1 and <= 65535)
            return (server[..colon], port);
        return (server, NetworkMenuController.DefaultPort);
    }

    /// <summary>IP literal or DNS name (first IPv4 address) to an endpoint.</summary>
    public static IPEndPoint ResolveBlocking(string host, int port)
    {
        var address = IPAddress.TryParse(host, out var ip) ? ip
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(address, port);
    }

    static Task<IPEndPoint> Resolve(string host, int port, CancellationToken cancel) =>
        IPAddress.TryParse(host, out var ip) ? Task.FromResult(new IPEndPoint(ip, port)) : Task.Run(() => ResolveBlocking(host, port), cancel);

    /// <summary>One frame: input to the menu or the race, session pump, menu state, actions, race drive. <paramref name="dt"/> is
    /// the frame time in seconds.</summary>
    public void Frame(DesktopInput input, double dt)
    {
        if (_disposed) return;
        _orbit = input.OrbitCameraActivation;
        _fixed = input.FixedCameraActivation;
        bool racing = Drive != null && Menu.Screen == NetScreen.Race && !Menu.Reconnecting;
        // Chat: while the line is open it owns the keyboard - the menu, the pause and the car get nothing from this frame.
        bool chatScreen = Menu.Lobby != null && !Menu.Reconnecting && !Menu.Editable &&
                          Menu.Screen is NetScreen.Lobby or NetScreen.Countdown or NetScreen.Race or NetScreen.Summary;
        Chat.Follow(Menu.Lobby?.GameId ?? 0);
        if (Chat.Typing && !chatScreen) Chat.Close();
        bool chatTook = Chat.Apply(input, chatScreen && Drive?.RemotePaused != true, text => _session?.Session.SendChat(text) == true);
        if (chatTook) { }
        else if (racing)
        {
            var action = NetworkRaceInput.Apply(input, Drive!.RemotePaused, Drive.Results != null, ref _pauseSelection);
            if (action == NetworkRaceAction.Leave) Menu.RequestExit();
            else if (action is NetworkRaceAction.Pause or NetworkRaceAction.Resume)
            {
                Drive.ToggleRemotePause();
                if (action == NetworkRaceAction.Pause) _pauseSelection = 0;
            }
        }
        else NetworkMenuInput.Apply(Menu, input);
        Pump();
        if (Drive is { } drive)
        {
            // Under the exit confirmation or a reconnect banner the keys belong to the menu: the car gets no input.
            // A spectator (eliminated knockout player) only watches: nothing it presses reaches the race (the provider drops the inputs); the
            // drive reads only its car-choice keys (Q/E or the gear keys, W) and the camera keys.
            var keys = racing && !chatTook ? input.Drive : default;
            drive.Frame(dt, keys, afterRemoteInput: PollSession);
            Menu.WatchedName = Spectating ? drive.WatchedName : null;
        }
    }

    void PollSession() => _session?.Poll();

    /// <summary>Session pump, menu update and the actions they produce, without input (also used by <see cref="Frame"/>).</summary>
    public void Pump()
    {
        if (_disposed) return;
        PumpConnect();
        _session?.Poll();
        if (_session is { HasNewRound: true })
        {
            // The server has another round (the next race, or the seat resumed after a reconnect): the shown one is stale.
            DropDrive();
            _session.ReleaseRound();
            _session.Poll();
        }
        if (_session is { Error: { } error }) OnSessionError(error);
        Menu.Update(NowMicros);
        FollowLobby();
        SendCarOnEntry();
        Process();
        TryStartDrive();
    }

    NetLobbyState? _noticeState;

    /// <summary>SERVER chat lines for people joining, leaving, losing the link and returning (from the change of the lobby state).</summary>
    void FollowLobby()
    {
        var lobby = Menu.Lobby;
        if (ReferenceEquals(lobby, _noticeState)) return;
        var previous = _noticeState;
        _noticeState = lobby;
        if (lobby == null) return;
        Chat.Follow(lobby.GameId);
        foreach (var text in NetworkLobbyNotices.Diff(previous, lobby, _session?.Session.Name ?? Menu.Nick))
            Chat.Add(new ChatLine(NetProtocol.ServerChatName, text), NowMicros);
    }

    void OnSessionError(string error)
    {
        HostLog.Error(error);
        if (Menu.Screen == NetScreen.Connecting)
        {
            DisposeSession();
            Menu.ConnectFailed(error);
            return;
        }
        // Not fatal for the connection (e.g. a round whose data cannot be loaded): back to the games list with the reason.
        var session = _session!;
        session.ClearError();
        session.Session.LeaveGame();
        Menu.Update(NowMicros);
        Menu.NotifyError(error);
    }

    void Process()
    {
        foreach (var action in Menu.DrainActions())
        {
            switch (action)
            {
                case NetMenuAction.Connect connect: BeginConnect(connect); break;
                case NetMenuAction.CancelConnect: DisposeSession(); CancelResolve(); break;
                case NetMenuAction.ExitToMainMenu: DisposeSession(); CancelResolve(); ExitRequested = true; break;
                case NetMenuAction.EnterRace: _wantRace = true; break;
                case NetMenuAction.LeaveRace:
                    _wantRace = false;
                    DropDrive();
                    _session?.ReleaseRound();
                    break;
            }
        }
        // The session of a finished link (rejected, closed) is gone for the menu; free the socket as well.
        if (Menu.View == null && _session != null && _resolveTask == null) DisposeSession();
    }

    void BeginConnect(NetMenuAction.Connect connect)
    {
        DisposeSession();
        CancelResolve();
        _connectNick = connect.Nick;
        Connecting?.Invoke(connect.Nick, connect.Host, connect.Port);
        _resolving = new CancellationTokenSource();
        _resolveTask = _resolve(connect.Host, connect.Port, _resolving.Token);
    }

    void CancelResolve()
    {
        _resolveTask = null;
        _resolving?.Cancel();
        _resolving = null;
    }

    void PumpConnect()
    {
        if (_resolveTask is not { IsCompleted: true } task) return;
        _resolveTask = null;
        _resolving?.Dispose();
        _resolving = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            string why = task.Exception?.GetBaseException().Message ?? "cancelled";
            Menu.ConnectFailed($"Address lookup failed: {why}");
            return;
        }
        try
        {
            _session = RemoteDesktopSession.Connect(_gameDir, task.Result, _connectNick, HandshakeTimeoutMicros, HudconDir);
            _session.Session.ChatReceived += line => Chat.Add(line, NowMicros);
            Menu.Attach(new SessionClientView(_session.Session));
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or SocketException)
        {
            DisposeSession();
            Menu.ConnectFailed(e.Message);
        }
    }

    /// <summary>The car chosen in the game menu goes to the server once per game lobby entered; later changes are the lobby's.</summary>
    void SendCarOnEntry()
    {
        var client = _session?.Session;
        if (client?.Lobby is not { Phase: NetGamePhase.Lobby } lobby) return;
        if (lobby.GameId == _carGameId) return;
        _carGameId = lobby.GameId;
        int car = _carType();
        if (NetProtocol.IsCarType(car) || car >= NFS.Game.Cars.CarCatalog.VanillaCount) client.SetCar(car);
        else HostLog.Info($"menu car {car} is not a selectable model (0..{NetProtocol.MaxCarType}); keeping the server's default");
    }

    void TryStartDrive()
    {
        if (!_wantRace || Drive != null || _session is not { Connected: true, Scene: { } scene, Provider: { } provider, Client: { } client }) return;
        var drive = new RaceDrive(scene, provider: provider, monotonicMicros: () => _session!.NowMicros,
            remoteInterpolationDelaySteps: _session.PresentationDelaySteps,
            orbitCameraActivation: _orbit, fixedCameraActivation: _fixed, spectator: client.Spectator);
        if (client.Welcome is { } welcome)
            drive.DriverNames = client.PlayerNames.Where(p => p.Key >= 0 && p.Key < welcome.SeatCarIds.Count)
                .ToDictionary(p => welcome.SeatCarIds[p.Key], p => p.Value);
        Drive = drive;
        _pauseSelection = 0;
    }

    void DropDrive() => Drive = null;

    void DisposeSession()
    {
        Chat.Clear();
        _noticeState = null;
        DropDrive();
        _wantRace = false;
        _carGameId = 0;
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeSession();
        CancelResolve();
        _disposed = true;
    }
}
