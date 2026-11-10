using NFS.Core.Constants;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>Screens of the network menu (change network-game-sessions, spec network-client-menu).</summary>
internal enum NetScreen { Connect, Connecting, Games, CreateOptions, Lobby, Countdown, Race, Summary, ConfirmExit, ConfirmStart }

/// <summary>Text field being edited (typed characters go there); None = navigation.</summary>
internal enum NetField { None, Nick, Host, Port, CreateName }

internal enum ConnectRow { Nick, Host, Port, Connect, Back }
internal enum GamesRow { Game, Create, Refresh, Back }
internal enum CreateRow { Name, MaxPlayers, Mode, Create, Back }
/// <summary>Lobby rows in the order of the screen (top level: car, settings, ready, start, leave; the options in between are the
/// rows of the settings mode). <see cref="Car"/> opens the car screen (<see cref="CarRow"/>), <see cref="Settings"/> the settings
/// (operator only).</summary>
internal enum LobbyRow { Car, Settings, Track, Direction, Laps, Bots, Skill, CatchUp, Traffic, Style, Ready, Start, Leave }

/// <summary>Rows of the car screen of the lobby (the PLAYER ONE screen of the original): the model, its colour, then done and
/// cancel side by side.</summary>
internal enum CarRow { Car, Colour, Done, Cancel }

/// <summary>What the player can do with a lobby row: change it, only see it (frozen for the series, fixed colour) or neither
/// because the option has no effect in this mode (<c>n/a</c>).</summary>
internal enum OptionAccess { Editable, ReadOnly, NotApplicable }
internal enum SummaryRow { ReturnToLobby, Exit }

/// <summary>Whether a listed game can be joined from here.</summary>
internal enum JoinAvailability
{
    Available,
    /// <summary>Countdown/Race game in which this player's seat is still reserved (grace window of a deliberate exit).</summary>
    Reserved,
    Full,
    /// <summary>Countdown or Race without a reservation of this player.</summary>
    InProgress,
}

/// <summary>Requests to the host application (window/session owner); the menu never creates sockets or race scenes itself.</summary>
internal abstract record NetMenuAction
{
    /// <summary>Create a session client for these settings, open its connection and <see cref="NetworkMenuController.Attach"/> it.</summary>
    public sealed record Connect(string Nick, string Host, int Port) : NetMenuAction;
    /// <summary>The connect attempt was cancelled (the attached client, if any, was disconnected).</summary>
    public sealed record CancelConnect : NetMenuAction;
    /// <summary>Back to the main menu; the session is already disconnected.</summary>
    public sealed record ExitToMainMenu : NetMenuAction;
    /// <summary>The game is in Race: show the race of the session client.</summary>
    public sealed record EnterRace : NetMenuAction;
    /// <summary>Stop showing the race (finished, left, or the game is back in the lobby/countdown).</summary>
    public sealed record LeaveRace : NetMenuAction;
}

/// <summary>
/// State and navigation of the network screens (task 5.1), independent of drawing and of the window. It reads an
/// <see cref="INetSessionView"/> (a <see cref="SessionClient"/> or a fake) on <see cref="Update"/>, reacts to
/// <see cref="Key"/>/<see cref="Type"/>, calls the session operations on the view and queues <see cref="NetMenuAction"/>s for the
/// host (<see cref="DrainActions"/>). The screen always follows the server's game phase: Lobby, Countdown, Race, Summary
/// (local, until acknowledged), Lobby; a start by the operator forces the summary (and any open exit confirmation) over to the
/// countdown without asking.
/// </summary>
internal sealed class NetworkMenuController
{
    public const int MaxNick = NetProtocol.MaxNameLength, MaxHost = 80, MaxGameName = NetProtocol.MaxGameNameLength;
    public const int DefaultPort = 27100;
    /// <summary>Seats of a race grid; humans (player limit) plus bots may not exceed it.</summary>
    public const int GridCars = 8;
    /// <summary>Default length of the grace window in which a deliberately left Countdown/Race game can be rejoined.</summary>
    public const long DefaultReservationMicros = 30_000_000;
    /// <summary>The games list is requested again this often while the GAMES screen is open (and nothing is pending).</summary>
    public const long GamesRefreshMicros = 3_000_000;
    /// <summary>The summary returns to the lobby by itself after this long.</summary>
    public const long SummaryAutoReturnMicros = 30_000_000;
    /// <summary>Bot setting of the lobby: <see cref="NetProtocol.AutoBots"/> or 0..<see cref="MaxBotsFor"/>.</summary>
    public const int AutoBots = NetProtocol.AutoBots;

    INetSessionView? _view;
    readonly Queue<NetMenuAction> _actions = new();
    long _now;
    SessionError? _seenError;
    bool _raceShown;
    int _ackResults;
    int _reservedGameId;
    long _reservedUntil;
    NetScreen _confirmOrigin;

    /// <summary>Menu sound to play (the slots of the front-end UI sounds, see <see cref="MenuSound"/>); raised only when the key
    /// press actually did something. The host (window) plays them; without a subscriber the menu is silent.</summary>
    public event Action<MenuSound>? Sound;
    void Play(MenuSound sound) => Sound?.Invoke(sound);

    /// <param name="remembered">The nick and the server come from the saved settings (not the very first run): the cursor starts on
    /// <c>CONNECT</c>, so joining is one ENTER; else it starts on the nick, which the player has not seen yet.</param>
    public NetworkMenuController(string nick, string host = "127.0.0.1", int port = DefaultPort, bool remembered = false)
    {
        Nick = Clean(nick, MaxNick);
        Host = host;
        PortText = port.ToString();
        CreateName = "";
        if (remembered) Selected = (int)ConnectRow.Connect;
    }

    // ---------------- state ----------------

    NetScreen _screen = NetScreen.Connect;
    // the games list has its default focus until the player moves the cursor there (UX1)
    bool _gamesUntouched = true;
    long _gamesRefreshAt;
    IReadOnlyList<NetGameInfo>? _gamesSeen;
    long _summaryStart, _prevNow;
    public NetScreen Screen
    {
        get => _screen;
        private set
        {
            if (value == NetScreen.Games && _screen != NetScreen.Games)
            {
                _gamesUntouched = true;
                _gamesRefreshAt = _now;
                _gamesSeen = _view?.Games;
            }
            if (value == NetScreen.Summary && _screen != NetScreen.Summary) _summaryStart = _now;
            _screen = value;
        }
    }
    public NetField Editing { get; private set; }
    public int Selected { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    /// <summary>The link was lost and the client reconnects (the current screen stays; only giving up is possible).</summary>
    public bool Reconnecting { get; private set; }
    /// <summary>Seconds (rounded up) the client still keeps trying to reconnect before it gives up; null when the view does not say.</summary>
    public int? ReconnectSecondsLeft => Reconnecting && _view?.ReconnectRemainingMicros is { } left ? (int)((left + 999_999) / 1_000_000) : null;
    public long ReservationMicros { get; set; } = DefaultReservationMicros;

    // connect
    public string Nick { get; private set; }
    public string Host { get; private set; }
    public string PortText { get; private set; }

    // create options (the rest of the options is the operator's business in the lobby)
    public string CreateName { get; private set; }
    public int MaxPlayers { get; private set; } = GridCars;
    public NetGameMode Mode { get; private set; } = NetGameMode.SingleRace;
    /// <summary>Track names from the game data (null = numbered fallback); set by the host.</summary>
    public IReadOnlyList<string>? TrackNames { get; set; }
    public string TrackName(int track) => NetworkTrackNames.Name(TrackNames, track);
    /// <summary>Full car names (<c>McLaren F1</c>, upper case) of the models 0..11 from the game data (null = the short names of the
    /// port); set by the host.</summary>
    public IReadOnlyList<string>? CarNames { get; set; }
    /// <summary>Cars of this client (original + mod cars); null = original cars only. Mod cars are selectable and shown by their display name.</summary>
    public NFS.Game.Cars.CarCatalog? Cars { get; set; }
    public string CarName(int car) =>
        car >= NFS.Game.Cars.CarCatalog.VanillaCount && Cars is { } cars && cars.TryGetByNumber(car, out var def) ? NetText.Upper(def.DisplayName) : NetworkCarNames.Name(CarNames, car);
    /// <summary>Car number of a lobby member (<see cref="NetProtocol.NoCarType"/> = none yet).</summary>
    public string CarShort(int car) =>
        car >= NFS.Game.Cars.CarCatalog.VanillaCount ? CarName(car) : NetworkCarNames.Short(car);
    public int CarOf(NetLobbyMember? member) => NetCars.NumberOf(member?.Car, Cars);

    // confirmation
    public NetScreen ConfirmOrigin => _confirmOrigin;
    /// <summary>The exit confirmation has "yes" selected (default "no"); on <see cref="NetScreen.ConfirmStart"/> "start anyway".</summary>
    public bool ConfirmYes { get; private set; }

    /// <summary>At least one connected player other than the operator is not ready (the operator's own flag does not count: pressing
    /// RACE is its declaration). Disconnected members are ignored. Starting then asks for confirmation.</summary>
    public bool NotAllReady => NotReadyCount > 0;

    /// <summary>How many connected members other than the operator are not ready (the number behind <see cref="NotAllReady"/>).</summary>
    public int NotReadyCount
    {
        get
        {
            if (Lobby is not { } lobby) return 0;
            var members = lobby.Members;
            int n = 0;
            for (int i = 0; i < members.Count; i++)
                if (members[i].Connected && !members[i].Operator && !members[i].Ready) n++;
            return n;
        }
    }

    public INetSessionView? View => _view;
    public NetLobbyState? Lobby => _view?.Lobby;
    /// <summary>Knockout series of the game as the server reports it (null in a single race and before the first round).</summary>
    public NetKnockoutState? Series => Lobby?.Series;
    /// <summary>This player was eliminated from the knockout series: it stays in the game and only watches the series state.</summary>
    public bool Spectating
    {
        get
        {
            if (Series is not { } s || Me is not { } me) return false;
            var entries = s.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Slot == me.Slot && !entries[i].Active) return true;
            return false;
        }
    }
    /// <summary>Spectator: name of the car the race picture follows (set by the host each frame; null for a driver or without a picture).</summary>
    public string? WatchedName { get; set; }
    /// <summary>Time the stragglers of the running round have left (0 = no countdown): the server states it once, when the first
    /// car finishes, and the client counts it down from the arrival of that state.</summary>
    public int CutoffRemainingMillis
    {
        get
        {
            if (Lobby is not { CutoffMillis: > 0 } lobby || !ReferenceEquals(lobby, _cutoffState)) return 0;
            return (int)Math.Max(1, lobby.CutoffMillis - (_now - _cutoffSeenAt) / 1000);
        }
    }
    NetLobbyState? _cutoffState;
    long _cutoffSeenAt;
    /// <summary>Time left of the start countdown: the server states it only when the lobby state changes (at the start and on each
    /// change of the lobby), so it counts down from the arrival of that state; 0 at the end. Before <see cref="Update"/> has seen the
    /// state it is the stated value.</summary>
    public int CountdownRemainingMillis
    {
        get
        {
            if (Lobby is not { } lobby) return 0;
            if (!ReferenceEquals(lobby, _countdownState)) return Math.Max(0, lobby.CountdownMillis);
            return (int)Math.Max(0, lobby.CountdownMillis - (_now - _countdownSeenAt) / 1000);
        }
    }
    NetLobbyState? _countdownState;
    long _countdownSeenAt;
    public IReadOnlyList<NetGameInfo> Games => _view?.Games ?? [];
    public bool Pending => _view?.Pending == true;
    public NetLobbyMember? Me
    {
        get
        {
            if (Lobby is not { } lobby) return null;
            string name = _view!.Name;
            var members = lobby.Members;
            for (int i = 0; i < members.Count; i++)
                if (string.Equals(members[i].Name, name, StringComparison.OrdinalIgnoreCase)) return members[i];
            return null;
        }
    }
    public bool IsOperator => Me?.Operator == true;
    public bool Editable => Editing != NetField.None;
    /// <summary>The summary screen shows results of a finished round.</summary>
    /// <summary>Winner shown on the victory screen (network: the local player won a single race) until it ends by a key or by [ref:1304]
    /// <see cref="VictoryRules.DurationTicks"/> ticks of 128 Hz; null otherwise. The knockout series has no victory screen.</summary>
    public RacerResult? VictoryWinner => Screen == NetScreen.Summary && _victoryStart >= 0 && _victoryId == (_view?.ResultsId ?? 0) ? _view?.LocalWinner : null;
    /// <summary>Ticks of the 128 Hz clock since the victory screen appeared.</summary>
    public long VictoryTicks => _victoryStart < 0 ? 0 : (_now - _victoryStart) * 128 / 1_000_000;
    /// <summary>Source of the victory art (the host supplies it; null/throwing = no victory screen).</summary>
    public Func<VictoryScreen?>? VictoryArt { get; set; }
    /// <summary>Menu track index (0..7) for <see cref="VictoryScreen.Draw"/> (inverse of <see cref="VictoryRules.MenuTrack"/>). [ref:1305]</summary>
    public static int TrackIdOfMenuTrack(int menuTrack) => menuTrack is >= 0 and < 8 ? new[] { 0, 2, 4, 5, 3, 6, 7, 8 }[menuTrack] : 0;
    public void EndVictory() { _victoryStart = -1; }
    long _victoryStart = -1;
    int _victoryId;
    int _victoryShown;

    void SyncVictory()
    {
        int id = _view?.ResultsId ?? 0;
        bool want = Screen == NetScreen.Summary && id != 0 && id != _ackResults && id != _victoryShown && !Reconnecting
            && _view?.Lobby is { Mode: not NetGameMode.Knockout } && _view.LocalWinner != null;
        if (want)
        {
            _victoryShown = id;
            _victoryId = id;
            _victoryStart = _now;
        }
        else if (_victoryStart >= 0 && (Screen != NetScreen.Summary || id != _victoryId || VictoryTicks > VictoryRules.DurationTicks))
            _victoryStart = -1;
    }

    public bool HasSummary => _view != null && _view.ResultsId != 0 && _view.ResultsId != _ackResults;
    public int Port => int.TryParse(PortText, out var p) ? p : 0;

    /// <summary>Largest bot count that keeps humans (player limit) + bots within the eight-car grid.</summary>
    public static int MaxBotsFor(int maxPlayers) => Math.Clamp(GridCars - maxPlayers, 0, NetProtocol.MaxSeats - 1);

    public IReadOnlyList<NetMenuAction> DrainActions()
    {
        var list = _actions.ToArray();
        _actions.Clear();
        return list;
    }

    // ---------------- rows of each screen ----------------

    public static IReadOnlyList<ConnectRow> ConnectRows { get; } = Enum.GetValues<ConnectRow>();
    public static IReadOnlyList<CreateRow> CreateRows { get; } = Enum.GetValues<CreateRow>();
    public static IReadOnlyList<SummaryRow> SummaryRows { get; } = Enum.GetValues<SummaryRow>();

    public int GamesRowCount => Games.Count + 3;

    public GamesRow GamesRowAt(int index, out int gameIndex)
    {
        gameIndex = -1;
        int n = Games.Count;
        if (index < n) { gameIndex = Math.Max(0, index); return GamesRow.Game; }
        return (index - n) switch { 0 => GamesRow.Create, 1 => GamesRow.Refresh, _ => GamesRow.Back };
    }

    /// <summary>Rows of the lobby: the operator also gets the settings and start; the others only the car, ready and leave (the
    /// options are shown read-only).</summary>
    public IReadOnlyList<LobbyRow> LobbyRows => IsOperator ? OperatorRows : PlayerRows;

    // shared, read-only lists: the lobby asks for the rows every frame
    static readonly LobbyRow[] OperatorRows = Enum.GetValues<LobbyRow>(),
        PlayerRows = [LobbyRow.Car, LobbyRow.Ready, LobbyRow.Leave];

    /// <summary>The game options: the rows of the settings mode.</summary>
    public static bool IsSetting(LobbyRow row) => row is >= LobbyRow.Track and <= LobbyRow.Style;

    public static IReadOnlyList<CarRow> CarRows { get; } = Enum.GetValues<CarRow>();

    /// <summary>The car screen is open (a sub-state of the lobby: ENTER on the car row, left with done, cancel or ESC).</summary>
    public bool InCar { get; private set; }

    /// <summary>Cursor of the car screen.</summary>
    public CarRow CarCursor { get; private set; }
    int _carBackup, _colourBackup;

    /// <summary>The cursor is in the settings (entered with ENTER on the settings row, left with ESC); otherwise it moves
    /// along the vertical list of the car, settings and the actions.</summary>
    public bool InSettings { get; private set; }

    /// <summary>At least one setting can be changed (otherwise the settings row is dimmed and skipped).</summary>
    public bool SettingsAvailable
    {
        get
        {
            var rows = LobbyRows;
            for (int i = 0; i < rows.Count; i++)
                if (IsSetting(rows[i]) && Access(rows[i]) == OptionAccess.Editable) return true;
            return false;
        }
    }

    /// <summary>Ready, start and leave: the actions of the left column (under the car and settings rows).</summary>
    public static bool IsAction(LobbyRow row) => row is LobbyRow.Ready or LobbyRow.Start or LobbyRow.Leave;

    /// <summary>Row of the cursor on entering the lobby: READY.</summary>
    int LobbyHome => Math.Max(0, Array.IndexOf(LobbyRows.ToArray(), LobbyRow.Ready));

    /// <summary>The knockout series of the lobby has begun (a race was started or finished and the series is not over): the
    /// options and the car and colour of the players are frozen until it ends.</summary>
    public bool SeriesFrozen => Lobby is { Mode: NetGameMode.Knockout, Series: { Finished: false, CompletedRounds: > 0 } };

    /// <summary>Colours of a car model: <c>count</c> palette entries starting at <see cref="PaletteFirst"/> (a model whose table
    /// count is 0 has the single colour <c>first</c>).</summary>
    public static int PaletteSize(int car) => car is >= 0 and <= NetProtocol.MaxCarType ? Math.Max(1, (int)FrontEndConstants.ColourTable[car * 2 + 1]) : 1;
    public static int PaletteFirst(int car) => car is >= 0 and <= NetProtocol.MaxCarType ? FrontEndConstants.ColourTable[car * 2] : 0;

    /// <summary>Position (0-based) of <paramref name="colour"/> in the palette of <paramref name="car"/>; -1 = no explicit colour
    /// (or one outside the palette).</summary>
    public static int PaletteIndex(int car, int colour)
    {
        int index = colour - PaletteFirst(car);
        return colour != NetProtocol.NoColour && index >= 0 && index < PaletteSize(car) ? index : -1;
    }

    /// <summary>Access to a lobby row in the current game: single race = everything editable before each start; knockout = the track
    /// follows the series (read-only), bots and traffic are <c>n/a</c>, the other options, the car and the colour change only
    /// before the first race of the series.</summary>
    public OptionAccess Access(LobbyRow row)
    {
        bool knockout = Lobby?.Mode == NetGameMode.Knockout;
        return row switch
        {
            LobbyRow.Track => knockout ? OptionAccess.ReadOnly : OptionAccess.Editable,
            LobbyRow.Bots or LobbyRow.Traffic => knockout ? OptionAccess.NotApplicable : OptionAccess.Editable,
            LobbyRow.Direction or LobbyRow.Laps or LobbyRow.Skill or LobbyRow.CatchUp or LobbyRow.Style =>
                SeriesFrozen ? OptionAccess.ReadOnly : OptionAccess.Editable,
            LobbyRow.Car => SeriesFrozen || Me == null ? OptionAccess.ReadOnly : OptionAccess.Editable,
            LobbyRow.Settings => SettingsAvailable ? OptionAccess.Editable : OptionAccess.ReadOnly,
            _ => OptionAccess.Editable,
        };
    }

    /// <summary>Access to a row of the car screen: the model changes until the series begins, the colour also needs a palette with
    /// more than one entry.</summary>
    public OptionAccess CarAccess(CarRow row) => row switch
    {
        CarRow.Car => SeriesFrozen ? OptionAccess.ReadOnly : OptionAccess.Editable,
        CarRow.Colour => SeriesFrozen || PaletteSize(Math.Max(0, CarOf(Me))) <= 1 ? OptionAccess.ReadOnly : OptionAccess.Editable,
        _ => OptionAccess.Editable,
    };

    /// <summary>Whether <paramref name="game"/> can be joined: a Lobby game with a free seat, or a Countdown/Race game in which
    /// this player's seat is reserved (a deliberate exit still inside the grace window).</summary>
    public JoinAvailability CanJoin(NetGameInfo game)
    {
        if (ReservedFor(game.GameId)) return JoinAvailability.Reserved;
        if (game.Phase != NetGamePhase.Lobby) return JoinAvailability.InProgress;
        return game.Players >= game.MaxPlayers ? JoinAvailability.Full : JoinAvailability.Available;
    }

    public bool ReservedFor(int gameId) => gameId != 0 && gameId == _reservedGameId && _now < _reservedUntil;

    // ---------------- host interface ----------------

    /// <summary>The host created and started a session client for <see cref="NetMenuAction.Connect"/>.</summary>
    public void Attach(INetSessionView view)
    {
        _view = view;
        _seenError = null;
        _ackResults = 0;
        Reconnecting = false;
    }

    /// <summary>The host asks to leave the game (e.g. the pause menu of the race): shows the exit confirmation.</summary>
    public void RequestExit()
    {
        if (Screen is NetScreen.Lobby or NetScreen.Countdown or NetScreen.Race or NetScreen.Summary) OpenConfirm();
        Settle();
    }

    /// <summary>The host could not open the connection (bad address, socket error, no answer in time): back to the Connect
    /// screen with the reason, so the data can be corrected.</summary>
    public void ConnectFailed(string reason)
    {
        _view = null;
        Reconnecting = false;
        _reservedGameId = 0;
        Editing = NetField.None;
        Screen = NetScreen.Connect;
        // address lookup, socket and timeout failures are about the server entry; a bad nick comes back as a refusal
        Selected = (int)ConnectRow.Host;
        SetMessage(reason, true);
        Settle();
    }

    /// <summary>The host shows an error on the current screen (e.g. the race data of a round could not be loaded).</summary>
    public void NotifyError(string text) => SetMessage(text, true);

    /// <summary>Starts connecting with the current Connect fields as if the Connect row was accepted (command-line launch).</summary>
    public void StartConnect()
    {
        if (Screen == NetScreen.Connect) BeginConnect();
    }

    /// <summary>Follows the session state; call every frame with the clock of the session client.</summary>
    public void Update(long now)
    {
        long elapsed = Math.Max(0, now - _prevNow);
        _prevNow = now;
        _now = now;
        if (InSettings && (Screen != NetScreen.Lobby || !SettingsAvailable)) CloseSettings(LobbyRows);   // frozen: back to the top
        if (InCar && (Screen != NetScreen.Lobby || Access(LobbyRow.Car) != OptionAccess.Editable))   // frozen or left
        {
            InCar = false;
            if (Screen == NetScreen.Lobby) Selected = LobbyHome;
        }
        if (Lobby is { CutoffMillis: > 0 } cutoff && !ReferenceEquals(cutoff, _cutoffState)) { _cutoffState = cutoff; _cutoffSeenAt = now; }
        if (Lobby is { Phase: NetGamePhase.Countdown } countdown && !ReferenceEquals(countdown, _countdownState)) { _countdownState = countdown; _countdownSeenAt = now; }
        if (_reservedGameId != 0 && (now >= _reservedUntil ||
                _view?.Phase == SessionClientPhase.Browsing && Games.All(g => g.GameId != _reservedGameId)))
            _reservedGameId = 0;
        var v = _view;
        if (v == null) { _victoryStart = -1; Settle(); return; }
        switch (v.Phase)
        {
            case SessionClientPhase.Rejected:
            case SessionClientPhase.Closed:
                EndSession(v);
                break;
            case SessionClientPhase.Reconnecting:
                Reconnecting = true;
                break;
            case SessionClientPhase.Browsing:
                Reconnecting = false;
                SyncBrowsing(v);
                break;
            case SessionClientPhase.InGame:
                Reconnecting = false;
                SyncInGame(v);
                break;
        }
        SyncVictory();
        TickSummary(elapsed);
        Settle();
    }

    /// <summary>Seconds (rounded up) until the summary returns to the lobby by itself; 0 outside the summary. The clock is held while
    /// the victory screen, the exit confirmation or a reconnect has the player's attention.</summary>
    public int SummaryRemainingSeconds
    {
        get
        {
            if (Screen != NetScreen.Summary || !HasSummary) return 0;
            long left = SummaryAutoReturnMicros - (_now - _summaryStart);
            return (int)Math.Clamp((left + 999_999) / 1_000_000, 1, SummaryAutoReturnMicros / 1_000_000);
        }
    }

    void TickSummary(long elapsed)
    {
        bool onSummary = Screen == NetScreen.Summary, underConfirm = Screen == NetScreen.ConfirmExit && _confirmOrigin == NetScreen.Summary;
        if (!onSummary && !underConfirm) return;
        if (Reconnecting || VictoryWinner != null || underConfirm) { _summaryStart += elapsed; return; }
        if (HasSummary && _now - _summaryStart >= SummaryAutoReturnMicros) ReturnToLobby();
    }

    // ---------------- following the session ----------------

    void EndSession(INetSessionView v)
    {
        var reject = v.LastReject;
        var error = v.LastError;
        string text;
        if (error?.Kind == SessionErrorKind.ReconnectExpired) text = "Connection lost - the reconnect time ran out";
        else if (error?.Kind == SessionErrorKind.ConnectionLost) text = "Connection lost";
        else text = DescribeReject(reject, Screen == NetScreen.Connecting);
        bool handshake = Screen == NetScreen.Connecting;
        _view = null;
        Reconnecting = false;
        _reservedGameId = 0;
        Editing = NetField.None;
        Screen = NetScreen.Connect;
        Selected = (int)FailedFocus(error, reject, handshake);
        SetMessage(text, true);
    }

    /// <summary>The Connect row to put the cursor on after a failed attempt: the nick when the server refused it, the server when it
    /// could not be reached, else CONNECT (nothing to correct, or not told).</summary>
    static ConnectRow FailedFocus(SessionError? error, NetReject? reject, bool handshake)
    {
        if (error?.Kind is SessionErrorKind.ConnectionLost or SessionErrorKind.ReconnectExpired) return ConnectRow.Connect;
        return reject?.Reason switch
        {
            NetRejectReason.NameTaken => ConnectRow.Nick,
            NetRejectReason.Timeout when handshake => ConnectRow.Host,
            null when handshake => ConnectRow.Host,
            _ => ConnectRow.Connect,
        };
    }

    /// <summary>Human-readable reason of a refused handshake or a server close.</summary>
    public static string DescribeReject(NetReject? reject, bool handshake = true)
    {
        if (reject == null) return handshake ? "Could not connect" : "Disconnected";
        return reject.Reason switch
        {
            NetRejectReason.NameTaken => "That nick is already used on this server",
            NetRejectReason.ResumeTokenInvalid => "Cannot return to the game: the seat is gone or taken",
            NetRejectReason.ModsMismatch => DescribeMods(reject),
            // Each check of the handshake (RaceServer / SessionServer) gets its own text: a player with another install of the
            // original game (patch, language version) must learn that the game data differ, not the port's version.
            NetRejectReason.ProtocolMismatch => $"Other network protocol: server {reject.ServerVersion}, you {NetProtocol.Version}",
            NetRejectReason.AppVersionMismatch => $"Other version: server {reject.ServerAppVersion}, you {BuildInfo.AppVersion}",
            NetRejectReason.PhysicsMismatch => $"Other physics: server {reject.ServerPhysics}, you {NetProtocol.PhysicsVersion}",
            NetRejectReason.DataMismatch => "Game data (NFS2SE files) differ from the server",
            NetRejectReason.Timeout => handshake ? "Server unreachable" : "Connection lost",
            NetRejectReason.ServerShutdown => "The server is shutting down",
            NetRejectReason.RaceClosed => "The game was closed by the server",
            NetRejectReason.SlowClient => "Disconnected: connection too slow",
            NetRejectReason.Left => "Disconnected",
            _ => $"The server refused the connection ({reject.Reason})",
        };
    }

    /// <summary>Names the mod (id and version) the client lacks or has in another version, and the client's mods the server lacks.</summary>
    static string DescribeMods(NetReject reject)
    {
        var diff = NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods).Diff(reject.ServerMods, "you", "the server");
        return diff.Length == 0 ? "Mods differ from the server" : char.ToUpperInvariant(diff[0]) + diff[1..];
    }

    void SyncBrowsing(INetSessionView v)
    {
        if (Screen is NetScreen.Games or NetScreen.CreateOptions)
        {
            if (v.LastError is { } e && !ReferenceEquals(e, _seenError))
            {
                _seenError = e;
                SetMessage(Describe(e), true);
            }
            if (Screen == NetScreen.Games) { FollowGamesList(v); ApplyGamesDefault(); AutoRefreshGames(v); }
            return;
        }
        bool lostSeat = Screen is NetScreen.Lobby or NetScreen.Countdown or NetScreen.Race or NetScreen.Summary or NetScreen.ConfirmExit or NetScreen.ConfirmStart;
        Editing = NetField.None;
        Screen = NetScreen.Games;
        Selected = 0;
        Message = null;
        _gamesSeen = v.Games;
        ApplyGamesDefault();
        if (lostSeat) SetMessage("The game is no longer available to you - back on the games list", true);
    }

    /// <summary>Default focus of the games list (UX1): while the cursor has not been moved since the screen opened, it stands on the
    /// first game that can be joined (or is reserved for this player), else on <c>create game</c>. The list arrives and refreshes
    /// asynchronously, so this runs every update; moving the cursor with UP/DOWN ends it. The cursor still passes over every row.</summary>
    void ApplyGamesDefault()
    {
        if (!_gamesUntouched) return;
        var games = Games;
        int target = games.Count;   // create game
        for (int i = 0; i < games.Count; i++)
            if (CanJoin(games[i]) is JoinAvailability.Available or JoinAvailability.Reserved) { target = i; break; }
        Selected = target;
    }

    /// <summary>While the cursor has been moved, a refreshed list keeps it on the same game (by id); if that game is gone the cursor
    /// goes where the default focus would put it, and on one of the buttons it stays on that button.</summary>
    void FollowGamesList(INetSessionView v)
    {
        var now = v.Games;
        var old = _gamesSeen;
        if (ReferenceEquals(now, old)) return;
        _gamesSeen = now;
        if (old == null || _gamesUntouched) return;
        if (Selected < old.Count)
        {
            int id = old[Selected].GameId;
            for (int i = 0; i < now.Count; i++)
                if (now[i].GameId == id) { Selected = i; return; }
            _gamesUntouched = true;   // the game vanished: the default focus applies again
            return;
        }
        Selected = Math.Max(0, now.Count + (Selected - old.Count));   // create game / refresh / back keep their place
    }

    /// <summary>Asks the server for the games list every <see cref="GamesRefreshMicros"/> (not while a request waits).</summary>
    void AutoRefreshGames(INetSessionView v)
    {
        if (v.Pending || _now - _gamesRefreshAt < GamesRefreshMicros) return;
        _gamesRefreshAt = _now;
        v.RequestGames();
    }

    static string Describe(SessionError e) => e.Kind switch
    {
        SessionErrorKind.CreateRefused => "The server refused to create the game (name used or limit reached)",
        SessionErrorKind.JoinRefused => "The server refused the join (game full, running or gone)",
        _ => e.Message,
    };

    void SyncInGame(INetSessionView v)
    {
        var lobby = v.Lobby;
        if (lobby == null) return;
        switch (lobby.Phase)
        {
            case NetGamePhase.Lobby:
                if (Screen is NetScreen.Race or NetScreen.Countdown) Screen = HasSummary ? NetScreen.Summary : NetScreen.Lobby;
                else if (Screen == NetScreen.ConfirmExit && _confirmOrigin is NetScreen.Race or NetScreen.Countdown)
                    _confirmOrigin = HasSummary ? NetScreen.Summary : NetScreen.Lobby;
                else if (Screen is not (NetScreen.Lobby or NetScreen.Summary or NetScreen.ConfirmExit or NetScreen.ConfirmStart)) EnterGame(NetScreen.Lobby);
                break;
            case NetGamePhase.Countdown:
                if (Screen == NetScreen.ConfirmExit)
                {
                    if (_confirmOrigin is NetScreen.Lobby or NetScreen.Summary) Screen = NetScreen.Countdown;   // forced: no question
                    else _confirmOrigin = NetScreen.Countdown;
                }
                else if (Screen != NetScreen.Countdown) EnterGame(NetScreen.Countdown);
                break;
            case NetGamePhase.Race:
                if (Screen == NetScreen.ConfirmExit) _confirmOrigin = NetScreen.Race;
                else if (Screen != NetScreen.Race) EnterGame(NetScreen.Race);
                break;
        }
    }

    void EnterGame(NetScreen screen)
    {
        Editing = NetField.None;
        Screen = screen;
        Selected = screen == NetScreen.Lobby ? LobbyHome : 0;
        InSettings = false;
        InCar = false;
        Message = null;
        _reservedGameId = 0;
    }

    /// <summary>Emits EnterRace/LeaveRace on the edge of "the race is shown" (also under an open exit confirmation).</summary>
    void Settle()
    {
        bool race = Screen == NetScreen.Race || Screen == NetScreen.ConfirmExit && _confirmOrigin == NetScreen.Race;
        if (race == _raceShown) return;
        _raceShown = race;
        _actions.Enqueue(race ? new NetMenuAction.EnterRace() : new NetMenuAction.LeaveRace());
    }

    // ---------------- input ----------------

    /// <summary>A typed character (8 = backspace) for the field being edited; Backspace also cancels a pending connect.</summary>
    public void Type(char c)
    {
        if (Screen == NetScreen.Connecting) { if (c == (char)8) { CancelConnect(); Play(MenuSound.Back); } return; }
        if (Editing == NetField.None) return;
        string value = FieldValue(Editing);
        if (c == (char)8) { if (value.Length > 0) value = value[..^1]; }
        else if (FieldAccepts(Editing, c) && value.Length < FieldMax(Editing)) value += c;
        else return;
        SetField(Editing, value);
    }

    public void Key(MenuKey key)
    {
        Message = null;
        if (Editing != NetField.None)
        {
            if (key is MenuKey.Enter or MenuKey.Escape) { Editing = NetField.None; Play(key == MenuKey.Enter ? MenuSound.Select : MenuSound.Back); }
            return;
        }
        if (Reconnecting)
        {
            if (key == MenuKey.Escape) { GiveUpReconnect(); Play(MenuSound.Back); }
            Settle();
            return;
        }
        switch (Screen)
        {
            case NetScreen.Connect: KeyConnect(key); break;
            case NetScreen.Connecting: if (key == MenuKey.Escape) { CancelConnect(); Play(MenuSound.Back); } break;
            case NetScreen.Games: if (!Pending) KeyGames(key); break;
            case NetScreen.CreateOptions: if (!Pending) KeyCreate(key); break;
            case NetScreen.Lobby: KeyLobby(key); break;
            case NetScreen.Countdown: KeyCountdown(key); break;
            case NetScreen.Race: if (key == MenuKey.Escape) { OpenConfirm(); Play(MenuSound.Back); } break;
            case NetScreen.Summary: KeySummary(key); break;
            case NetScreen.ConfirmExit: KeyConfirm(key); break;
            case NetScreen.ConfirmStart: KeyConfirmStart(key); break;
        }
        Settle();
    }

    static int Move(int selected, int count, MenuKey key) => key switch
    {
        MenuKey.Up => (selected + count - 1) % count,
        MenuKey.Down => (selected + 1) % count,
        _ => selected,
    };

    static int Step(MenuKey key) => key == MenuKey.Left ? -1 : key == MenuKey.Right ? 1 : 0;

    static int Cycle(int value, int delta, int min, int max) =>
        delta == 0 ? value : (value - min + delta + (max - min + 1)) % (max - min + 1) + min;

    // ---- connect ----

    void KeyConnect(MenuKey key)
    {
        Selected = Move(Selected, ConnectRows.Count, key);
        if (key is MenuKey.Up or MenuKey.Down) Play(MenuSound.Move);
        // CONNECT and BACK stand side by side in the original look: LEFT/RIGHT goes from one to the other
        if (Step(key) != 0 && (ConnectRow)Selected is ConnectRow.Connect or ConnectRow.Back)
        {
            Selected = (int)((ConnectRow)Selected == ConnectRow.Connect ? ConnectRow.Back : ConnectRow.Connect);
            Play(MenuSound.Move);
            return;
        }
        if (key == MenuKey.Escape) { ExitToMenu(); Play(MenuSound.Back); return; }
        if (key != MenuKey.Enter) return;
        switch ((ConnectRow)Selected)
        {
            case ConnectRow.Nick: Editing = NetField.Nick; Play(MenuSound.Select); break;
            case ConnectRow.Host: Editing = NetField.Host; Play(MenuSound.Select); break;
            case ConnectRow.Port: Editing = NetField.Port; Play(MenuSound.Select); break;
            case ConnectRow.Connect: BeginConnect(); if (Screen == NetScreen.Connecting) Play(MenuSound.Select); break;
            default: ExitToMenu(); Play(MenuSound.Select); break;
        }
    }

    void BeginConnect()
    {
        string nick = Clean(Nick, MaxNick);
        if (nick.Length == 0) { SetMessage("Enter a nick (1-12 characters)", true); return; }
        if (Host.Trim().Length == 0) { SetMessage("Enter the server address", true); return; }
        if (!int.TryParse(PortText, out int port) || port is < 1 or > 65535) { SetMessage("The port must be 1-65535", true); return; }
        Nick = nick;
        Screen = NetScreen.Connecting;
        _view = null;
        _actions.Enqueue(new NetMenuAction.Connect(nick, Host.Trim(), port));
    }

    void CancelConnect()
    {
        _view?.Disconnect();
        _view = null;
        Screen = NetScreen.Connect;
        Selected = (int)ConnectRow.Connect;
        _actions.Enqueue(new NetMenuAction.CancelConnect());
    }

    void ExitToMenu()
    {
        _view?.Disconnect();
        _view = null;
        Reconnecting = false;
        _actions.Enqueue(new NetMenuAction.ExitToMainMenu());
    }

    void GiveUpReconnect()
    {
        _view?.Disconnect();
        _view = null;
        Reconnecting = false;
        _reservedGameId = 0;
        Screen = NetScreen.Connect;
        Selected = (int)ConnectRow.Connect;
        SetMessage("Disconnected", true);
    }

    // ---- games list ----

    void KeyGames(MenuKey key)
    {
        int count = GamesRowCount;
        Selected = Math.Min(Selected, count - 1);
        if (key is MenuKey.Up or MenuKey.Down) { _gamesUntouched = false; Selected = Move(Selected, count, key); Play(MenuSound.Move); return; }
        if (key == MenuKey.Escape) { ExitToMenu(); Play(MenuSound.Back); return; }
        if (key != MenuKey.Enter) return;
        switch (GamesRowAt(Selected, out int gameIndex))
        {
            case GamesRow.Game: TryJoin(Games[gameIndex]); if (Message == null) Play(MenuSound.Select); break;   // Enter on a game joins it directly
            case GamesRow.Create:
                if (CreateName.Length == 0) CreateName = Clean($"{Nick}'s game", MaxGameName);
                Screen = NetScreen.CreateOptions;
                Selected = 0;
                Play(MenuSound.Select);
                break;
            case GamesRow.Refresh: _view?.RequestGames(); _gamesRefreshAt = _now; Play(MenuSound.Select); break;
            default: ExitToMenu(); Play(MenuSound.Select); break;
        }
    }

    void TryJoin(NetGameInfo game)
    {
        switch (CanJoin(game))
        {
            case JoinAvailability.Available:
            case JoinAvailability.Reserved:
                if (_view?.JoinGame(game.GameId) == false && _view.LastError is { } e) SetMessage(e.Message, true);
                break;
            case JoinAvailability.Full: SetMessage($"\"{game.Name}\" is full", true); break;
            default: SetMessage($"\"{game.Name}\" is already running - it cannot be joined", true); break;
        }
    }

    // ---- create options ----

    void KeyCreate(MenuKey key)
    {
        Selected = Move(Selected, CreateRows.Count, key);
        if (key is MenuKey.Up or MenuKey.Down) Play(MenuSound.Move);
        if (key == MenuKey.Escape) { Screen = NetScreen.Games; Selected = 0; Play(MenuSound.Back); return; }
        var row = CreateRows[Selected];
        int step = Step(key);
        if (step != 0 && row is CreateRow.Create or CreateRow.Back)
        {
            Selected = (int)(row == CreateRow.Create ? CreateRow.Back : CreateRow.Create);   // side by side: LEFT/RIGHT between them
            Play(MenuSound.Move);
            return;
        }
        if (step != 0 && AdjustCreate(row, step)) Play(MenuSound.Change);
        if (key != MenuKey.Enter) return;
        switch (row)
        {
            case CreateRow.Name: Editing = NetField.CreateName; Play(MenuSound.Select); break;
            case CreateRow.Create: Create(); if (Message == null) Play(MenuSound.Select); break;
            case CreateRow.Back: Screen = NetScreen.Games; Selected = 0; Play(MenuSound.Select); break;
            default: if (AdjustCreate(row, 1)) Play(MenuSound.Change); break;
        }
    }

    /// <summary>Changes the value of a create row; false when nothing changed (limit reached or not a value row).</summary>
    bool AdjustCreate(CreateRow row, int step)
    {
        switch (row)
        {
            case CreateRow.MaxPlayers:
                int before = MaxPlayers;
                MaxPlayers = Math.Clamp(MaxPlayers + step, NetProtocol.MinSeats, NetProtocol.MaxSeats);
                return MaxPlayers != before;
            case CreateRow.Mode: Mode = Mode == NetGameMode.SingleRace ? NetGameMode.Knockout : NetGameMode.SingleRace; return true;
        }
        return false;
    }

    void Create()
    {
        string name = CreateName.Trim();
        if (!NetProtocol.IsGameName(name)) { SetMessage("Enter a game name (1-24 characters)", true); return; }
        CreateName = name;
        var request = new NetGameCreate(name, MaxPlayers, Mode);
        if (_view?.CreateGame(request) == false && _view.LastError is { } e) SetMessage(e.Message, true);
    }

    // ---- lobby / countdown ----

    bool Selectable(LobbyRow row) => Access(row) == OptionAccess.Editable;

    /// <summary>Rows the cursor visits: in the settings the usable settings, otherwise the header and the usable actions.</summary>
    bool Navigable(LobbyRow row) => Selectable(row) && (InSettings ? IsSetting(row) : !IsSetting(row));

    /// <summary>UP/DOWN on the top level: along the vertical list of the actions (SETTINGS header, ready, start, leave) in the order
    /// of the screen, without wrap; the header and an action that cannot be used are skipped.</summary>
    int MoveTop(IReadOnlyList<LobbyRow> rows, int selected, MenuKey key)
    {
        int dir = key == MenuKey.Up ? -1 : 1;
        for (int i = selected + dir; i >= 0 && i < rows.Count; i += dir)
            if (Navigable(rows[i])) return i;
        return selected;
    }

    static int IndexOf(IReadOnlyList<LobbyRow> rows, LobbyRow row)
    {
        for (int i = 0; i < rows.Count; i++) if (rows[i] == row) return i;
        return 0;
    }

    void OpenSettings(IReadOnlyList<LobbyRow> rows)
    {
        InSettings = true;
        Selected = MoveLobby(rows, IndexOf(rows, LobbyRow.Settings), MenuKey.Down);
    }

    /// <summary>Back to the top level: on RACE (the operator has just set the game up, so starting is one ENTER away).</summary>
    void CloseSettings(IReadOnlyList<LobbyRow> rows)
    {
        InSettings = false;
        Selected = rows.Contains(LobbyRow.Start) ? IndexOf(rows, LobbyRow.Start) : LobbyHome;
    }

    /// <summary>Moves the cursor over the rows the player can use (read-only and <c>n/a</c> rows are only shown).</summary>
    int MoveLobby(IReadOnlyList<LobbyRow> rows, int selected, MenuKey key)
    {
        int dir = key == MenuKey.Up ? -1 : 1;
        if (key is MenuKey.Up or MenuKey.Down)
            for (int i = 0; i < rows.Count; i++)
            {
                selected = (selected + dir + rows.Count) % rows.Count;
                if (Navigable(rows[selected])) return selected;
            }
        else if (!Navigable(rows[selected]))
            for (int i = 1; i < rows.Count; i++)
                if (Navigable(rows[(selected + i) % rows.Count])) return (selected + i) % rows.Count;
        return selected;
    }

    /// <summary>Next usable action to the left or right of <paramref name="selected"/> on the action line; stays at the end.</summary>
    int MoveAction(IReadOnlyList<LobbyRow> rows, int selected, int step)
    {
        for (int i = selected + Math.Sign(step); i >= 0 && i < rows.Count && IsAction(rows[i]); i += Math.Sign(step))
            if (Selectable(rows[i])) return i;
        return selected;
    }

    void KeyLobby(MenuKey key)
    {
        if (InCar) { KeyCar(key); return; }
        var rows = LobbyRows;
        Selected = Math.Min(Selected, rows.Count - 1);
        if (InSettings && !SettingsAvailable) CloseSettings(rows);   // frozen since the last key press
        if (key == MenuKey.Escape) { if (InSettings) CloseSettings(rows); else OpenConfirm(); Play(MenuSound.Back); return; }
        if (key is MenuKey.Up or MenuKey.Down)
        {
            int before = Selected;
            Selected = InSettings ? MoveLobby(rows, Selected, key) : MoveTop(rows, Selected, key);
            if (Selected != before) Play(MenuSound.Move);
            return;
        }
        var v = _view;
        var lobby = v?.Lobby;
        if (v == null || lobby == null) return;
        var row = rows[Selected];
        int step = Step(key);
        bool enter = key == MenuKey.Enter;
        if (step == 0 && !enter) return;
        if (row == LobbyRow.Settings) { if (enter && SettingsAvailable) { OpenSettings(rows); Play(MenuSound.Select); } return; }
        if (row == LobbyRow.Car)
        {
            if (Access(LobbyRow.Car) != OptionAccess.Editable) return;
            if (enter) { OpenCar(); Play(MenuSound.Select); }
            else if (ChangeCar(v, step)) Play(MenuSound.Change);   // LEFT/RIGHT cycle the model without opening the car screen
            else if (v.LastError is { } ce) SetMessage(ce.Message, true);
            return;
        }
        if (!enter && IsAction(row))   // LEFT/RIGHT along the action line
        {
            int before = Selected;
            Selected = MoveAction(rows, Selected, step);
            if (Selected != before) Play(MenuSound.Move);
            return;
        }
        if (!Selectable(row)) { Selected = MoveLobby(rows, Selected, key); return; }   // frozen since the last key press: the cursor leaves it
        int delta = enter ? 1 : step;
        bool ok = true;
        bool changed = IsSetting(row);   // a setting row changes a value (a clamped one may not: Laps)
        switch (row)
        {
            case LobbyRow.Ready: if (enter) { ok = v.SetReady(Me?.Ready != true); if (ok) Play(MenuSound.Select); } break;
            case LobbyRow.Track: ok = SetOptions(v, lobby, track: Cycle(lobby.Track, delta, NetProtocol.MinTrack, NetProtocol.MaxTrack)); break;
            case LobbyRow.Direction: ok = SetOptions(v, lobby, backward: !lobby.Backward); break;
            case LobbyRow.Laps:
                int laps = Math.Clamp(lobby.Laps + delta, NetProtocol.MinLaps, NetProtocol.MaxLaps);
                changed = laps != lobby.Laps;
                ok = SetOptions(v, lobby, laps: laps);
                break;
            case LobbyRow.Bots: ok = SetOptions(v, lobby, bots: Cycle(Math.Min(lobby.Bots, MaxBotsFor(lobby.MaxPlayers)), delta, AutoBots, MaxBotsFor(lobby.MaxPlayers))); break;
            case LobbyRow.Skill: ok = SetOptions(v, lobby, skill: Cycle(lobby.Skill, delta, NetProtocol.MinSkill, NetProtocol.MaxSkill)); break;
            case LobbyRow.CatchUp: ok = SetOptions(v, lobby, catchUp: !lobby.CatchUp); break;
            case LobbyRow.Traffic: ok = SetOptions(v, lobby, traffic: !lobby.Traffic); break;
            case LobbyRow.Style: ok = SetOptions(v, lobby, style: Cycle(lobby.Style, delta, NetProtocol.MinStyle, NetProtocol.MaxStyle)); break;
            case LobbyRow.Start:
                if (!enter) break;
                if (NotAllReady) { OpenConfirmStart(); Play(MenuSound.Select); }
                else { ok = v.StartRace(); if (ok) Play(MenuSound.StartRace); }
                break;
            case LobbyRow.Leave: if (enter) { OpenConfirm(); Play(MenuSound.Select); } break;
        }
        if (ok && changed) Play(MenuSound.Change);
        if (!ok && v.LastError is { } e) SetMessage(e.Message, true);
    }

    /// <summary>Sends the options of the lobby with the given ones replaced (the operator changes one option at a time).</summary>
    static bool SetOptions(INetSessionView v, NetLobbyState l, int? track = null, int? laps = null, bool? backward = null, int? bots = null,
        bool? traffic = null, int? skill = null, bool? catchUp = null, int? style = null) =>
        v.SetOptions(track ?? l.Track, laps ?? l.Laps, backward ?? l.Backward, bots ?? l.Bots, traffic ?? l.Traffic,
            skill ?? l.Skill, catchUp ?? l.CatchUp, style ?? l.Style);

    /// <summary>Next car model; the colour stays when it is also a colour of the new model, otherwise the model's default is used.</summary>
    bool ChangeCar(INetSessionView v, int delta)
    {
        var cars = NetCars.SelectableNumbers(Cars);
        int car = cars[Cycle(Math.Max(0, cars.IndexOf(Math.Max(0, CarOf(Me)))), delta, 0, cars.Count - 1)];
        int colour = Me?.Colour ?? NetProtocol.NoColour;
        return v.SetCar(car, PaletteIndex(car, colour) >= 0 ? colour : NetProtocol.NoColour);
    }

    /// <summary>Next colour of the model's palette; the model's default colour is the position before the first entry.</summary>
    bool ChangeColour(INetSessionView v, int delta)
    {
        int car = Math.Max(0, CarOf(Me));
        int next = Cycle(PaletteIndex(car, Me?.Colour ?? NetProtocol.NoColour), delta, -1, PaletteSize(car) - 1);
        return v.SetCar(car, next < 0 ? NetProtocol.NoColour : PaletteFirst(car) + next);
    }

    // ---- car screen ----

    void OpenCar()
    {
        var me = Me;
        if (me == null) return;
        InCar = true;
        CarCursor = CarRow.Car;
        _carBackup = CarOf(me);
        _colourBackup = me.Colour;
    }

    bool CarNavigable(CarRow row) => CarAccess(row) == OptionAccess.Editable;

    void KeyCar(MenuKey key)
    {
        var v = _view;
        if (v == null) return;
        if (key == MenuKey.Escape) { CancelCar(v); Play(MenuSound.Back); return; }
        if (key is MenuKey.Up or MenuKey.Down)
        {
            int dir = key == MenuKey.Up ? -1 : 1, n = CarRows.Count, at = (int)CarCursor;
            for (int i = 0; i < n; i++)
            {
                at = (at + dir + n) % n;
                if (CarNavigable((CarRow)at)) { if ((CarRow)at != CarCursor) Play(MenuSound.Move); CarCursor = (CarRow)at; break; }
            }
            return;
        }
        int step = Step(key);
        bool enter = key == MenuKey.Enter;
        if (step == 0 && !enter) return;
        bool ok = true;
        switch (CarCursor)
        {
            case CarRow.Car: ok = ChangeCar(v, enter ? 1 : step); if (ok) Play(MenuSound.Change); break;
            case CarRow.Colour: ok = ChangeColour(v, enter ? 1 : step); if (ok) Play(MenuSound.Change); break;
            case CarRow.Done:
                if (enter) { InCar = false; Play(MenuSound.Select); }
                else { CarCursor = CarRow.Cancel; Play(MenuSound.Move); }   // side by side: LEFT/RIGHT between them
                break;
            default:
                if (enter) { CancelCar(v); Play(MenuSound.Select); }
                else { CarCursor = CarRow.Done; Play(MenuSound.Move); }
                break;
        }
        if (!ok && v.LastError is { } e) SetMessage(e.Message, true);
    }

    /// <summary>Leaves the car screen for the car row; the model and colour from before the screen opened are sent back when they
    /// were changed (each change goes to the server at once, as in the lobby).</summary>
    void CancelCar(INetSessionView v)
    {
        InCar = false;
        var me = Me;
        if (me == null || CarOf(me) == _carBackup && me.Colour == _colourBackup) return;
        if (!v.SetCar(_carBackup, _colourBackup) && v.LastError is { } e) SetMessage(e.Message, true);
    }

    void KeyCountdown(MenuKey key)
    {
        var v = _view;
        if (key == MenuKey.Escape)
        {
            Play(MenuSound.Back);
            // the operator aborts the countdown at once (the screen follows the lobby state that comes back); the others leave
            if (IsOperator && v != null) { if (!v.CancelStart() && v.LastError is { } ce) SetMessage(ce.Message, true); }
            else OpenConfirm();
            return;
        }
        int step = Step(key);
        if (step == 0 || v?.Lobby == null || SeriesFrozen) return;
        // the 5 seconds of the countdown are for a last change of the car
        if (ChangeCar(v, step)) Play(MenuSound.Change);
        else if (v.LastError is { } e) SetMessage(e.Message, true);
    }

    // ---- summary ----

    void KeySummary(MenuKey key)
    {
        if (VictoryWinner != null) { EndVictory(); Play(MenuSound.Select); return; }   // any key ends the victory screen [ref:1306]
        Selected = Math.Min(Selected, SummaryRows.Count - 1);
        if (key is MenuKey.Up or MenuKey.Down) { Selected = Move(Selected, SummaryRows.Count, key); Play(MenuSound.Move); return; }
        if (key == MenuKey.Escape) { OpenConfirm(); Play(MenuSound.Back); return; }
        if (key != MenuKey.Enter) return;
        Play(MenuSound.Select);
        if ((SummaryRow)Selected == SummaryRow.Exit) { OpenConfirm(); return; }
        // the player is not ready on the summary (the server resets readiness on the return to the lobby); nothing to toggle here
        ReturnToLobby();
    }

    /// <summary>Acknowledges the results and goes back to the lobby (ENTER on <c>return to lobby</c>, or the timer of the summary).</summary>
    void ReturnToLobby()
    {
        _ackResults = _view?.ResultsId ?? 0;
        _view?.ReturnToLobby();
        Screen = NetScreen.Lobby;
        Selected = LobbyHome;
        InSettings = false;
        InCar = false;
    }

    // ---- exit confirmation ----

    void OpenConfirm()
    {
        if (Screen == NetScreen.ConfirmExit) return;
        _confirmOrigin = Screen;
        Screen = NetScreen.ConfirmExit;
        ConfirmYes = false;
    }

    void KeyConfirm(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Left or MenuKey.Right or MenuKey.Up or MenuKey.Down: ConfirmYes = !ConfirmYes; Play(MenuSound.Move); break;
            case MenuKey.Escape: CancelConfirm(); Play(MenuSound.Back); break;
            case MenuKey.Enter: if (ConfirmYes) { LeaveGame(); Play(MenuSound.Select); } else { CancelConfirm(); Play(MenuSound.Back); } break;
        }
    }

    // ---- start confirmation (not every player is ready) ----

    void OpenConfirmStart()
    {
        Screen = NetScreen.ConfirmStart;
        ConfirmYes = false;
    }

    /// <summary>"Start anyway" / "cancel" (default cancel): UP/DOWN and LEFT/RIGHT move, ENTER confirms, ESC cancels.</summary>
    void KeyConfirmStart(MenuKey key)
    {
        switch (key)
        {
            case MenuKey.Left or MenuKey.Right or MenuKey.Up or MenuKey.Down: ConfirmYes = !ConfirmYes; Play(MenuSound.Move); break;
            case MenuKey.Escape: CancelConfirmStart(); Play(MenuSound.Back); break;
            case MenuKey.Enter:
                if (!ConfirmYes) { CancelConfirmStart(); Play(MenuSound.Back); break; }
                CancelConfirmStart();
                if (_view is { } v)
                {
                    if (v.StartRace()) Play(MenuSound.StartRace);
                    else if (v.LastError is { } e) SetMessage(e.Message, true);
                }
                break;
        }
    }

    void CancelConfirmStart()
    {
        Screen = NetScreen.Lobby;
        ConfirmYes = false;
    }

    void CancelConfirm()
    {
        Screen = _confirmOrigin;
        ConfirmYes = false;
    }

    void LeaveGame()
    {
        var phase = _view?.Lobby?.Phase;
        int gameId = _view?.Lobby?.GameId ?? 0;
        _view?.LeaveGame();
        ConfirmYes = false;
        Editing = NetField.None;
        Screen = NetScreen.Games;
        Selected = 0;
        if (phase is NetGamePhase.Countdown or NetGamePhase.Race)
        {
            _reservedGameId = gameId;
            _reservedUntil = _now + ReservationMicros;
            SetMessage("You left the race - rejoin the same game within the grace window", false);
        }
    }

    // ---------------- fields ----------------

    string FieldValue(NetField f) => f switch
    {
        NetField.Nick => Nick, NetField.Host => Host, NetField.Port => PortText, _ => CreateName,
    };

    static int FieldMax(NetField f) => f switch
    {
        NetField.Nick => MaxNick, NetField.Host => MaxHost, NetField.Port => 5, _ => MaxGameName,
    };

    static bool FieldAccepts(NetField f, char c) => f == NetField.Port ? c is >= '0' and <= '9' : c is >= ' ' and < (char)0x7F;

    void SetField(NetField f, string value)
    {
        switch (f)
        {
            case NetField.Nick: Nick = value; break;
            case NetField.Host: Host = value; break;
            case NetField.Port: PortText = value; break;
            default: CreateName = value; break;
        }
    }

    void SetMessage(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }

    static string Clean(string value, int max) =>
        new string(value.Where(c => c is >= ' ' and < (char)0x7F).Take(max).ToArray()).Trim();
}

/// <summary>Shared keyboard/pad dispatch of the network menu (same input record as the rest of the Desktop menus; the pad
/// arrives as the d-pad/A/B menu keys).</summary>
internal static class NetworkMenuInput
{
    public static void Apply(NetworkMenuController menu, DesktopInput input)
    {
        if (menu.Editable)
        {
            foreach (char ch in input.Typed ?? []) menu.Type(ch);
            foreach (var command in input.Menu.Where(c => c is MenuKey.Enter or MenuKey.Escape)) menu.Key(command);
            return;
        }
        if (menu.Screen == NetScreen.Connecting && input.Typed?.Contains((char)8) == true) menu.Type((char)8);
        foreach (var command in input.Menu) menu.Key(command);
    }
}
