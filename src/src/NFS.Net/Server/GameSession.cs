using NFS.Game.Cars;
using NFS.Game.Race;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

/// <summary>One human participant of a <see cref="GameSession"/>. The slot is the stable lobby seat shown to clients; the join
/// sequence orders the roster (the first member is the operator) and is never reused.</summary>
public sealed class GameMember
{
    internal GameMember(ClientConnection connection, int slot, long joinSequence, byte[] resumeToken)
    {
        Connection = connection;
        Slot = slot;
        JoinSequence = joinSequence;
        Name = connection.Name;
        ResumeToken = resumeToken;
    }

    public int Slot { get; }
    public long JoinSequence { get; }
    public string Name { get; }
    public bool Ready { get; internal set; }
    public int CarType { get; internal set; } = NetProtocol.NoCarType;
    /// <summary>Selected car colour (a palette entry of <see cref="CarType"/>) or <see cref="NetProtocol.NoColour"/>: the colour of the frozen menu record.</summary>
    public int Colour { get; internal set; } = NetProtocol.NoColour;
    public bool Connected => Connection != null;
    public ClientConnection? Connection { get; internal set; }
    /// <summary>Token handed to the member and required to resume its reserved seat after a lost connection.</summary>
    public byte[] ResumeToken { get; }
    /// <summary>When the connection was lost in Countdown/Race; the seat stays reserved until the grace window ends.</summary>
    internal long? DisconnectedSinceMicros;
    internal RaceMember? RoundMember;
    /// <summary>The member only watches the current round (an eliminated knockout player): <see cref="RoundMember"/> is a spectator
    /// of the round, which owns no seat and does not wait for it.</summary>
    internal bool Spectating;
}

/// <summary>
/// One game of the session server: its lobby (join order, operator, readiness, car selections and options) and, while it
/// runs, one <see cref="RaceRound"/>. The game owns no socket: every message leaves through <see cref="ISessionOutput"/> to
/// the member's connection. The player limit and mode are fixed at creation (the other options start with the server defaults); the operator
/// changes track, direction, laps, bots, traffic, bot skill, catch-up and physics style in the lobby before every start. New members join only in <see cref="NetGamePhase.Lobby"/> (a reserved former member may return in any phase); a race start switches to a five-second
/// countdown, composes a fresh host (N humans + bots filling the grid) and returns to the lobby immediately after the
/// result, preserving options, cars, roster and operator.
/// <para>A <see cref="NetGameMode.Knockout"/> game runs a whole series in the same game (the server is the only authority): the first
/// start fixes the humans of the series (identities 0..N-1, the rest of the nine entrants are AI), every start of the operator races
/// the next round of the remaining entrants on the next track, the last place is eliminated when the result arrives (once per
/// round) and eliminated humans stay in the game as spectators: they get the series state and, as spectators of the round (no seat, no controls), the whole race stream. A human that is gone from a running round
/// is DNF, ranked last and eliminated; a human that is gone between rounds forfeits. The clients only show the
/// <see cref="NetKnockoutState"/> of the lobby state (and the stream of a round they watch). The lobby options are frozen while the series runs.</para>
/// </summary>
public sealed class GameSession : IDisposable
{
    readonly int _gameId;
    readonly string _name;
    readonly int _maxPlayers;
    readonly NetGameMode _mode;
    readonly ISessionOutput _output;
    readonly string? _gameDir;
    readonly byte[] _fingerprint;
    readonly SessionServerOptions _options;
    readonly Func<int> _nextRaceId;
    readonly Func<byte[]> _newResumeToken;
    readonly Action<string>? _log;
    readonly List<GameMember> _members = [];
    int _track;
    int _laps;
    bool _backward;
    int _bots;
    bool _traffic;
    int _skill;
    bool _catchUp;
    int _style;
    long _joinSequence;
    long _now;
    long _countdownEnd;
    long _lastActivity;
    RaceRound? _round;
    GameMember? _operator;
    // Knockout series (null until the first start of a knockout game and again after a start that begins a new one).
    KnockoutSeries? _series;
    int[] _humanSlots = [];                 // lobby slot of every human identity
    string[] _koNames = new string[NetProtocol.KnockoutEntrants];
    bool _cutoffShown;
    bool _roundScored;
    bool _disposed;
    /// <summary>A member with a place in the round left (or its grace ended) before the countdown of the round: the round would
    /// wait for a human that is gone, so <see cref="Update"/> recomposes it for the remaining connected members.</summary>
    bool _rebuildRound;

    internal GameSession(int gameId, string name, int maxPlayers, NetGameMode mode, long now,
        ISessionOutput output, string? gameDir, byte[] fingerprint, SessionServerOptions options, Func<int> nextRaceId,
        Func<byte[]> newResumeToken, Action<string>? log)
    {
        _gameId = gameId;
        _name = name;
        _maxPlayers = maxPlayers;
        _track = NetProtocol.DefaultTrack; // a series starts on the first track and follows its stage
        _laps = NetProtocol.DefaultLaps;
        _backward = NetProtocol.DefaultBackward;
        _bots = NetProtocol.AutoBots;
        _traffic = NetProtocol.DefaultTraffic;
        _skill = NetProtocol.DefaultSkill;
        _catchUp = NetProtocol.DefaultCatchUp;
        _style = NetProtocol.DefaultStyle;
        _mode = mode;
        _now = now;
        _lastActivity = now;
        _output = output;
        _gameDir = gameDir;
        _fingerprint = fingerprint;
        _options = options;
        _nextRaceId = nextRaceId;
        _newResumeToken = newResumeToken;
        _log = log;
    }

    public int GameId => _gameId;
    public string Name => _name;
    public int MaxPlayers => _maxPlayers;
    public int Track => _track;
    public int Laps => _laps;
    public bool Backward => _backward;
    /// <summary>Bots of a single race (-1 = auto: fill the grid to eight cars, 0..7 = that many, capped by free grid slots); changed by the operator in the lobby. Without effect in a knockout series (nine entrants).</summary>
    public int Bots => _bots;
    /// <summary>Road traffic of a single race; changed by the operator in the lobby. Without effect in a knockout series.</summary>
    public bool Traffic => _traffic;
    /// <summary>Bot skill of every race (0 = beginner, 1 = expert).</summary>
    public int Skill => _skill;
    /// <summary>Catch-up ("rubber band") of every race.</summary>
    public bool CatchUp => _catchUp;
    /// <summary>Physics style of every race (0 = Simulation, 1 = Wild, 2 = Arcade).</summary>
    public int Style => _style;
    public NetGameMode Mode => _mode;
    /// <summary>Knockout series of the game (null in a single race and before the first start); read-only view for diagnostics/tests.</summary>
    public KnockoutSeries? Series => _series;
    /// <summary>A knockout series is running: its rounds have started and it is not over, so nobody new joins and the options are frozen.</summary>
    bool SeriesRunning => _mode == NetGameMode.Knockout && _series is { Finished: false } series && (series.Stage > 0 || series.ResultsApplied);
    public NetGamePhase Phase { get; private set; } = NetGamePhase.Lobby;
    /// <summary>Monotone counter of every visible lobby/game change (the session server refreshes the list when it moves).</summary>
    public int Revision { get; private set; }
    /// <summary>Roster ordered by join time.</summary>
    public IReadOnlyList<GameMember> Members => _members;
    /// <summary>The operator is always a connected member: the creator first, then (permanently) the earliest joined connected
    /// member once the operator leaves or loses the connection. Null while nobody is connected; the first member that
    /// returns or joins then takes over.</summary>
    public GameMember? Operator => _operator;
    public int MemberCount => _members.Count;
    public long LastActivityMicros => _lastActivity;
    /// <summary>The lobby timed out or the last member left: the owner removes the game (a running race is never removed by
    /// the idle timeout).</summary>
    public bool ShouldRemove => _members.Count == 0;
    /// <summary>Active round, if the game is in its countdown/race phase (diagnostics and tests).</summary>
    public RaceRound? Round => _round;
    public int CountdownRemainingMillis => Phase != NetGamePhase.Countdown ? 0
        : (int)Math.Clamp((_countdownEnd - _now + 999) / 1000, 0, NetProtocol.MaxCountdownMillis);

    /// <summary>Adds a connection to the lobby. Only <see cref="NetGamePhase.Lobby"/> accepts new members, except the former
    /// member of a reserved seat (same name) who is resumed in any phase; names are unique within a game (the session server
    /// additionally keeps them globally unique).</summary>
    public bool TryJoin(ClientConnection connection, long now, out string error)
    {
        // A name reserved by this game (left or lost in Countdown/Race, still inside the grace window) resumes that member.
        ExpireGrace(now);
        if (FindReservation(connection.Name, now) is { } reserved) return TryResume(connection, reserved, now, out error);
        if (Phase != NetGamePhase.Lobby) { error = $"game is {Phase}"; return false; }
        if (SeriesRunning) { error = "a knockout series is running"; return false; }
        if (_members.Count >= _maxPlayers) { error = "game is full"; return false; }
        if (_members.Any(m => string.Equals(m.Name, connection.Name, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"name '{connection.Name}' is already in this game";
            return false;
        }
        int slot = 0;
        while (_members.Any(m => m.Slot == slot)) slot++;
        connection.Game = this;
        var member = new GameMember(connection, slot, ++_joinSequence, _newResumeToken());
        _members.Add(member);
        _operator ??= member;
        _lastActivity = now;
        Revision++;
        BroadcastState();
        error = "";
        return true;
    }

    /// <summary>Removes the member of <paramref name="connection"/> (explicit leave or a lost connection outside the grace
    /// window). A running race marks the car DNF through the round and continues without that human.</summary>
    public void Leave(ClientConnection connection, long now)
    {
        var member = _members.FirstOrDefault(m => ReferenceEquals(m.Connection, connection));
        if (member == null) return;
        RemoveMember(member);
        if (ReferenceEquals(connection.Game, this)) connection.Game = null;
        _lastActivity = now;
        Revision++;
        if (_members.Count > 0) BroadcastState();
    }

    /// <summary>A lost connection or a deliberate leave. In the lobby it is an ordinary leave (the seat is freed at once);
    /// in Countdown/Race the seat and car stay reserved for the reconnect grace window and the others see the member as not
    /// connected. Returns true when the seat was reserved.</summary>
    internal bool Disconnect(ClientConnection connection, long now)
    {
        var member = _members.FirstOrDefault(m => ReferenceEquals(m.Connection, connection));
        if (member == null) return false;
        // The lobby frees the seat at once, except for an eliminated member of a running knockout series: it keeps a reservation
        // for the grace window too, so that it can come back (as a watcher) for the rest of the series.
        if ((Phase == NetGamePhase.Lobby && !IsEliminatedSeriesMember(member)) || _options.GraceMicros <= 0)
        {
            Leave(connection, now);
            return false;
        }
        member.Connection = null;
        member.DisconnectedSinceMicros = now;
        if (_round != null && member.RoundMember is { } away && !member.Spectating) _round.SetMemberAway(away, true);
        if (ReferenceEquals(connection.Game, this)) connection.Game = null;
        if (ReferenceEquals(member, _operator)) AssignOperator();
        _lastActivity = now;
        _now = now;
        Revision++;
        BroadcastState();
        _log?.Invoke($"game {_gameId:X8} '{_name}': '{member.Name}' disconnected in {Phase}; seat {member.Slot} reserved for " +
            $"{_options.GraceMicros / 1000} ms");
        return true;
    }

    /// <summary>The seat that keeps <paramref name="name"/> reserved: a member that lost its connection or left in
    /// Countdown/Race and whose grace window has not run out (null when the name is not reserved here).</summary>
    internal GameMember? FindReservation(string name, long now)
    {
        foreach (var member in _members)
        {
            if (member.Connection != null || member.DisconnectedSinceMicros is not { } since || now - since >= _options.GraceMicros)
                continue;
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)) return member;
        }
        return null;
    }

    /// <summary>Re-attaches a connection to a reserved seat and lets the active round issue a fresh epoch, a resume welcome
    /// and the current state. The member keeps its slot, join order and car; the operator role is not restored (a returning
    /// former operator is an ordinary player unless nobody else is connected).</summary>
    internal bool TryResume(ClientConnection connection, GameMember member, long now, out string error)
    {
        if (connection.Game != null) { error = "connection is already in a game"; return false; }
        if (!_members.Contains(member) || member.Connection != null || member.DisconnectedSinceMicros == null)
        {
            error = "seat is not reserved";
            return false;
        }
        long? previousSince = member.DisconnectedSinceMicros;
        member.Connection = connection;
        member.DisconnectedSinceMicros = null;
        connection.Game = this;
        if (_round != null && member.RoundMember is { } roundMember)
        {
            if (!_round.TryResumeMember(roundMember, out NetRejectReason reason))
            {
                member.Connection = null;
                member.DisconnectedSinceMicros = previousSince ?? now;
                connection.Game = null;
                error = $"round refused the resume ({reason})";
                return false;
            }
        }
        _operator ??= member;
        _lastActivity = now;
        _now = now;
        Revision++;
        BroadcastState();
        _log?.Invoke($"game {_gameId:X8} '{_name}': '{member.Name}' resumed seat {member.Slot} in {Phase}");
        error = "";
        return true;
    }

    /// <summary>Moves the operator role to the earliest joined connected member (null when nobody is connected).</summary>
    void AssignOperator() => _operator = _members.FirstOrDefault(m => m.Connected);

    void RemoveMember(GameMember member)
    {
        if (member.RoundMember is { } roundMember && _round != null)
        {
            _round.RemoveMember(roundMember);
            // A spectator is no place of the round: its loss never forces a recomposition.
            if (Phase == NetGamePhase.Race && _round.Phase < NetRacePhase.Countdown && !member.Spectating) _rebuildRound = true;
        }
        member.RoundMember = null;
        member.Spectating = false;
        _members.Remove(member);
        if (ReferenceEquals(member, _operator)) AssignOperator();
    }

    /// <summary>Ends the reservation of members whose grace window ran out: the round marks the car DNF (or frees the lobby
    /// seat) and the member leaves the game as before.</summary>
    void ExpireGrace(long now)
    {
        if (_options.GraceMicros <= 0) return;
        GameMember[] expired = _members
            .Where(m => m.DisconnectedSinceMicros is { } since && now - since >= _options.GraceMicros).ToArray();
        if (expired.Length == 0) return;
        foreach (var member in expired)
        {
            _log?.Invoke($"game {_gameId:X8} '{_name}': grace of '{member.Name}' expired; seat {member.Slot} freed");
            RemoveMember(member);
        }
        _now = now;
        Revision++;
        if (_members.Count > 0) BroadcastState();
    }

    public void SetReady(GameMember member, bool ready, long now)
    {
        if (Phase != NetGamePhase.Lobby || !_members.Contains(member) || member.Ready == ready) return;
        member.Ready = ready;
        _lastActivity = now;
        Revision++;
        BroadcastState();
    }

    /// <summary>Colours of a car model: the palette is <c>first .. first + max(1, count) - 1</c> of the model's pair in
    /// <see cref="NFS.Core.Constants.FrontEndConstants.ColourTable"/> (a model with count 0 has the single colour <c>first</c>).</summary>
    public static bool IsColourOfCar(int carType, int colour)
    {
        if (!NetProtocol.IsCarType(carType)) return false;
        var table = NFS.Core.Constants.FrontEndConstants.ColourTable;
        int first = table[carType * 2], count = Math.Max(1, (int)table[carType * 2 + 1]);
        return colour >= first && colour < first + count;
    }

    /// <summary>Cars of this server: the original cars and the mod cars of <see cref="SessionServerOptions.Cars"/>.</summary>
    CarCatalog Cars => _options.Cars ?? CarCatalog.VanillaTables;

    /// <summary>Colour of a mod car is an index into its car.json "colors" (a car without colours has the single colour 0).</summary>
    bool IsColourOfCarNumber(int carType, int colour)
    {
        if (carType < CarCatalog.VanillaCount) return IsColourOfCar(carType, colour);
        return Cars.TryGetByNumber(carType, out var def) && colour >= 0 && colour < Math.Max(1, def.Colors.Count);
    }

    /// <summary>Selects the car by key: only a car of the server's catalog that a player may select (showcase cars 0..11 and mod cars)
    /// is accepted; an unknown key and the other original cars (the cheat cars 12..14 among them) are refused.</summary>
    public bool SetCar(GameMember member, NFS.ModApi.CarKey car, int colour, long now, out string error)
    {
        if (!Cars.TryGet(car, out var def) || !NetCars.IsSelectable(def)) { error = $"unknown or unavailable car '{car}'"; return false; }
        return SetCar(member, Cars.NumberOf(car), colour, now, out error);
    }

    /// <summary>Selects the member's car model and colour (<see cref="NetProtocol.NoColour"/> = the model's default colour). The
    /// choice is atomic: a model outside 0..11 or a colour outside the palette of the model refuses the whole change. Open in the
    /// lobby and the countdown, except while a knockout series runs (the cars of the series are frozen).</summary>
    public bool SetCar(GameMember member, int carType, int colour, long now, out string error)
    {
        if (Phase is not (NetGamePhase.Lobby or NetGamePhase.Countdown) || !_members.Contains(member)) { error = $"the car cannot change while the game is {Phase}"; return false; }
        if (SeriesRunning) { error = "the car is frozen while the knockout series runs"; return false; }
        if (!NetCars.IsSelectableNumber(carType, Cars)) { error = "invalid car model"; return false; }
        if (colour != NetProtocol.NoColour && !IsColourOfCarNumber(carType, colour)) { error = "the colour is not in the palette of the model"; return false; }
        error = "";
        if (member.CarType == carType && member.Colour == colour) return true;
        member.CarType = carType;
        member.Colour = colour;
        _lastActivity = now;
        Revision++;
        BroadcastState();
        return true;
    }

    public void SetCar(GameMember member, int carType, long now) => SetCar(member, carType, NetProtocol.NoColour, now, out _);

    /// <summary>Operator changes the options of the next start. Every option is open before each start of a single race; in a
    /// knockout series the track follows the stage and bots/traffic have no effect (both are ignored), the others are open
    /// before the first race only.</summary>
    public bool TrySetOptions(GameMember member, int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style,
        long now, out string error)
    {
        if (!ReferenceEquals(member, Operator)) { error = "only the operator changes the options"; return false; }
        if (Phase != NetGamePhase.Lobby) { error = $"options are frozen while the game is {Phase}"; return false; }
        if (SeriesRunning) { error = "options are frozen while the knockout series runs"; return false; }
        if (!NetProtocol.IsTrack(track) || !NetProtocol.IsLaps(laps) || !NetProtocol.IsBotSetting(bots) ||
            !NetProtocol.IsSkill(skill) || !NetProtocol.IsStyle(style)) { error = "invalid options"; return false; }
        if (_mode == NetGameMode.Knockout)
        {
            track = _track; // the track follows the stage of the series
            bots = _bots;   // a series always has nine entrants and keeps the road traffic of the host
            traffic = _traffic;
        }
        if (_track == track && _laps == laps && _backward == backward && _bots == bots && _traffic == traffic &&
            _skill == skill && _catchUp == catchUp && _style == style) { error = ""; return true; }
        _track = track;
        _laps = laps;
        _backward = backward;
        _bots = bots;
        _traffic = traffic;
        _skill = skill;
        _catchUp = catchUp;
        _style = style;
        _lastActivity = now;
        Revision++;
        BroadcastState();
        error = "";
        return true;
    }

    /// <summary>Operator start: the five-second countdown begins even when some players are not ready. The player limit and
    /// mode are fixed; options are frozen from now on and only car changes remain possible.</summary>
    public bool TryStart(GameMember member, long now, out string error)
    {
        if (!ReferenceEquals(member, Operator)) { error = "only the operator starts the race"; return false; }
        if (Phase != NetGamePhase.Lobby) { error = $"cannot start while the game is {Phase}"; return false; }
        if (_members.Count == 0) { error = "no players"; return false; }
        Phase = NetGamePhase.Countdown;
        _countdownEnd = now + Math.Max(0, _options.CountdownMicros);
        _now = now;
        _lastActivity = now;
        Revision++;
        BroadcastState();
        _log?.Invoke($"game {_gameId:X8} '{_name}': countdown {_options.CountdownMicros / 1000} ms for {_members.Count} humans");
        error = "";
        return true;
    }

    /// <summary>Operator abort of the countdown: the game returns to the lobby phase at once, ready flags, cars and options stay
    /// (no round exists yet). Refused once the race has begun.</summary>
    public bool TryCancelStart(GameMember member, long now, out string error)
    {
        if (!ReferenceEquals(member, Operator)) { error = "only the operator cancels the start"; return false; }
        if (Phase != NetGamePhase.Countdown) { error = $"cannot cancel while the game is {Phase}"; return false; }
        Phase = NetGamePhase.Lobby;
        _countdownEnd = 0;
        _now = now;
        _lastActivity = now;
        Revision++;
        BroadcastState();
        _log?.Invoke($"game {_gameId:X8} '{_name}': countdown cancelled by the operator");
        error = "";
        return true;
    }

    /// <summary>The client confirmed the summary screen; the server already returned the game to the lobby (a late ack never
    /// blocks it), this only refreshes the activity stamp.</summary>
    public void AcknowledgeReturn(long now)
    {
        _lastActivity = now;
    }

    /// <summary>Advances the countdown and the active round. A finished round returns the game to the lobby at once.</summary>
    public void Update(long now)
    {
        _now = now;
        ExpireGrace(now);
        if (Phase == NetGamePhase.Countdown && now >= _countdownEnd) BeginRace(now);
        if (Phase == NetGamePhase.Race && _round != null && _rebuildRound)
        {
            _rebuildRound = false;
            if (_round.Phase < NetRacePhase.Countdown && !_round.Finished) RebuildRound(now);
        }
        if (Phase == NetGamePhase.Race && _round != null)
        {
            _round.Update(now);
            // The straggler countdown starts with the first finisher: tell the clients once (they count it down themselves).
            bool cutoff = _round.CutoffRemainingMillis > 0;
            if (cutoff != _cutoffShown)
            {
                _cutoffShown = cutoff;
                Revision++;
                BroadcastState();
            }
            // A round that ended without a result (its wait for the clients timed out, it was stopped) is over as well: the game
            // never stays in Race with a round that will not run again.
            if (_round.Finished || _round.Stopping || _round.Phase == NetRacePhase.Closed) EndRace(now);
        }
    }

    void BeginRace(long now)
    {
        _now = now;
        // A member without a connection has no car in the round: nobody is there to load the race and report ready. Its
        // reservation ends with the start (it can join again in the lobby once the round is over). With nobody connected the
        // game just goes back to the lobby and the reservations run out on their own.
        if (!_members.Any(m => m.Connected)) { ToLobby(now); return; }
        DropAbsentMembers();
        if (!ComposeRound(now)) ToLobby(now);
    }

    /// <summary>An eliminated knockout human that is disconnected inside its grace window keeps its reservation across the start of
    /// the next round (it is added to the round as a spectator that is away and watches when it returns); a human that is still
    /// in the series follows the DNF rules instead (forfeit), as does everyone outside a running series.</summary>
    bool KeepsSpectatorSeat(GameMember member)
    {
        if (member.Connected || member.DisconnectedSinceMicros is not { } since || _now - since >= _options.GraceMicros) return false;
        return IsEliminatedSeriesMember(member);
    }

    /// <summary>The member is a human of the running knockout series that has already been eliminated (it only watches).</summary>
    bool IsEliminatedSeriesMember(GameMember member)
    {
        if (_mode != NetGameMode.Knockout || !SeriesRunning || _series is not { } series) return false;
        int id = Array.IndexOf(_humanSlots, member.Slot);
        return id >= 0 && id < series.Humans && !series.HumanActive(id);
    }

    /// <summary>Ends the reservation of members that have no connection (their round places no longer exist).</summary>
    void DropAbsentMembers()
    {
        foreach (var member in _members.Where(m => !m.Connected && !KeepsSpectatorSeat(m)).ToArray())
        {
            _log?.Invoke($"game {_gameId:X8} '{_name}': '{member.Name}' is absent at the round start; seat {member.Slot} released");
            RemoveMember(member);
        }
    }

    /// <summary>The round lost a member before its countdown: compose a new one (new race id and welcome) for the members that
    /// are still connected. Clients accept a new welcome while their round has not started.</summary>
    void RebuildRound(long now)
    {
        _log?.Invoke($"game {_gameId:X8} '{_name}': a member left before the countdown; recomposing the round");
        _round?.Dispose();
        _round = null;
        foreach (var member in _members) { member.RoundMember = null; member.Spectating = false; }
        if (!_members.Any(m => m.Connected)) { ToLobby(now); return; }
        DropAbsentMembers();
        if (!ComposeRound(now)) ToLobby(now);
    }

    /// <summary>Composes the host and the round for the connected members; false = the round could not be built (the caller
    /// returns to the lobby).</summary>
    bool ComposeRound(long now)
    {
        var racers = _members.Where(m => m.Connected).ToArray();
        RaceRound? round = null;
        try
        {
            var config = new ServerRaceConfig(Track: _track, Laps: _laps, Traffic: _traffic, Players: _maxPlayers)
            {
                Backward = _backward,
                BotSetting = _bots,
                Skill = _skill,
                CatchUp = _catchUp,
                Style = _style,
            };
            int raceId = _nextRaceId();
            RaceHost host;
            int bots;
            if (_mode == NetGameMode.Knockout)
            {
                // Racers of this round: the connected humans that are still in the series, in identity order (= human car order).
                racers = PrepareKnockoutRound(racers);
                host = config.CreateKnockoutHost(_gameDir, raceId, _series!, racers.Select(m => m.CarType).ToArray(),
                    racers.Select(m => m.Colour).ToArray());
                bots = _series!.RaceIdentities.Count - racers.Length;
                NameKnockoutBots(host);
            }
            else
            {
                bots = config.BotsFor(racers.Length);
                host = config.CreateHost(_gameDir, raceId, racers.Length, bots, racers.Select(m => m.CarType).ToArray(),
                    racers.Select(m => m.Colour).ToArray());
            }
            int humans = racers.Length;
            round = new RaceRound(host, _fingerprint, now, new RaceServerOptions
            {
                MaxRaceSteps = _options.MaxRaceSteps,
                WaitTimeoutMicros = _options.RaceWaitTimeoutMicros,
                LingerMicros = _options.RaceLingerMicros,
                MaxReliableBacklog = _options.MaxReliableBacklog,
                MaxBadMessages = _options.MaxBadMessages,
                StragglerLimitMicros = _options.StragglerLimitMicros,
                ManualStart = false,
                BotSetting = _bots,
                MinPlayers = -1,
                Cars = _options.Cars,
            }, _log, seed: raceId, clock: () => _now);
            foreach (var member in racers)
            {
                var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint, member.Name, member.CarType);
                var raceMember = round.TryAddMember(new SessionRaceEndpoint(this, member), hello, out var reason);
                if (raceMember == null) throw new InvalidOperationException($"round refused member '{member.Name}': {reason}");
                member.RoundMember = raceMember;
                member.Spectating = false;
            }
            // Connected members that do not race this round (eliminated knockout players) watch it: the same stream, no seat.
            foreach (var watcher in _members.Where(m => (m.Connected || KeepsSpectatorSeat(m)) && !racers.Contains(m)))
            {
                var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint, watcher.Name, watcher.CarType);
                var spectator = round.TryAddSpectator(new SessionRaceEndpoint(this, watcher), hello, out var why);
                if (spectator == null)
                {
                    _log?.Invoke($"game {_gameId:X8} '{_name}': '{watcher.Name}' cannot watch the round ({why})");
                    continue;
                }
                watcher.RoundMember = spectator;
                watcher.Spectating = true;
            }
            _round = round;
            Phase = NetGamePhase.Race;
            _cutoffShown = false;
            _lastActivity = now;
            Revision++;
            BroadcastState();
            _log?.Invoke($"game {_gameId:X8} '{_name}': race {raceId:X8} on track {_track} laps {_laps}, {humans} humans, {bots} bots, traffic {(_traffic ? "on" : "off")}, skill {_skill}, catch-up {(_catchUp ? "on" : "off")}, style {_style}" +
                (_series != null && _mode == NetGameMode.Knockout ? $", knockout round {_series.Stage + 1}/{NetProtocol.KnockoutRounds}, {_series.RaceIdentities.Count} entrants" : ""));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            _log?.Invoke($"game {_gameId:X8} '{_name}': cannot start the race: {e.Message}");
            round?.Dispose();
            foreach (var member in _members) { member.RoundMember = null; member.Spectating = false; }
            return false;
        }
    }

    /// <summary>Starts a new series or moves the running one to its next round for the connected members; returns the racers of the
    /// round (connected humans still in the series, in identity order). Humans that are gone since the last round forfeit.</summary>
    GameMember[] PrepareKnockoutRound(GameMember[] connected)
    {
        bool fresh = _series == null || _series.Finished || (_series.Stage == 0 && !_series.ResultsApplied);
        if (!fresh)
        {
            for (int id = 0; id < _series!.Humans; id++)
                if (_series.HumanActive(id) && !connected.Any(m => m.Slot == _humanSlots[id]))
                {
                    _log?.Invoke($"game {_gameId:X8} '{_name}': '{_koNames[id]}' is gone and forfeits the series");
                    _series.Forfeit(id);
                }
            fresh = _series.Finished;
        }
        if (fresh)
        {
            // Nobody has raced yet (or the last series is over): all connected members take part, the grid is filled to nine.
            var members = connected.OrderBy(m => m.Slot).Take(KnockoutSeries.MaxNetworkHumans).ToArray();
            _series = KnockoutSeries.ForNetwork(members.Length);
            _humanSlots = members.Select(m => m.Slot).ToArray();
            _koNames = new string[NetProtocol.KnockoutEntrants];
            for (int i = 0; i < members.Length; i++) _koNames[i] = members[i].Name;
        }
        else if (_series!.ResultsApplied) _series.NextRace();
        _track = Math.Clamp(_series!.Stage, NetProtocol.MinTrack, NetProtocol.MaxTrack);
        _roundScored = false;
        return Enumerable.Range(0, _series.Humans).Where(_series.HumanActive)
            .Select(id => connected.First(m => m.Slot == _humanSlots[id])).ToArray();
    }

    /// <summary>Names of the AI entrants of the round (the drivers of the lineup); fixed for the series once seen.</summary>
    void NameKnockoutBots(RaceHost host)
    {
        var ids = _series!.RaceIdentities;
        var setup = host.Runner.Session.Setup;
        for (int j = 0; j < ids.Count; j++)
        {
            int id = ids[j];
            if (id < _series.Humans || _koNames[id] != null) continue;
            var car = setup.Car(j);
            _koNames[id] = car.CarClass == 2 && car.Personality is >= 0 and < 11
                ? NFS.Core.Constants.RaceConstants.AiDriverNames[car.Personality]
                : $"BOT {id + 1}";
            if (_koNames[id].Length > NetProtocol.MaxNameLength) _koNames[id] = _koNames[id][..NetProtocol.MaxNameLength];
        }
        for (int id = _series.Humans; id < NetProtocol.KnockoutEntrants; id++) _koNames[id] ??= $"BOT {id + 1}";
    }

    /// <summary>The result of the round is counted exactly once: the last place (and the humans that were gone) leave the series.</summary>
    void ScoreKnockoutRound()
    {
        if (_series == null || _round == null || _roundScored || _series.ResultsApplied) return;
        if (_round.FinishReason is null or NetFinishReason.ServerStop || _round.FinalResults is not { } results) return;
        _roundScored = true;
        try { _series.Complete(results, _round.ForcedLastCars); }
        catch (ArgumentException e)
        {
            _log?.Invoke($"game {_gameId:X8} '{_name}': the result does not fit the series ({e.Message}); the round is repeated");
            return;
        }
        _log?.Invoke($"game {_gameId:X8} '{_name}': round {_series.Stage + 1} done, eliminated: " +
            string.Join(", ", _series.Eliminated.Select(id => _koNames[id])) +
            (_series.Finished ? $"; series over{(_series.Winner >= 0 ? $", winner {_koNames[_series.Winner]}" : "")}" : ""));
        _track = Math.Clamp(_series.Finished ? 0 : _series.Stage + 1, NetProtocol.MinTrack, NetProtocol.MaxTrack);
    }

    void EndRace(long now)
    {
        if (_mode == NetGameMode.Knockout) ScoreKnockoutRound();
        _log?.Invoke($"game {_gameId:X8} '{_name}': race over ({(_round?.FinishReason is { } reason ? reason.ToString() : "closed without a result")}), returning to the lobby");
        ToLobby(now);
    }

    void ToLobby(long now)
    {
        _round?.Dispose();
        _round = null;
        _rebuildRound = false;
        foreach (var member in _members)
        {
            member.Ready = false;
            member.RoundMember = null;
            member.Spectating = false;
        }
        Phase = NetGamePhase.Lobby;
        _countdownEnd = 0;
        _lastActivity = now;
        _now = now;
        Revision++;
        if (_members.Count > 0) BroadcastState();
    }

    NetLobbyState BuildState(ClientConnection recipient)
    {
        var recipientMember = _members.FirstOrDefault(m => ReferenceEquals(m.Connection, recipient));
        return new(
            _gameId, recipient.Epoch, _name, Phase, _track, _laps, _maxPlayers, _mode, CountdownRemainingMillis,
            _members.Select(m => new NetLobbyMember(m.Slot, m.Name, m.Ready, NetCars.KeyOf(m.CarType, Cars), ReferenceEquals(m, Operator), m.Connected, m.Colour)).ToArray(),
            recipientMember?.ResumeToken, _backward, _bots, _traffic, _round?.CutoffRemainingMillis ?? 0, SeriesState(), _skill, _catchUp, _style);
    }

    NetKnockoutState? SeriesState()
    {
        if (_mode != NetGameMode.Knockout || _series is not { } series) return null;
        int completed = series.ResultsApplied ? series.Stage + 1 : series.Stage;
        var entries = Enumerable.Range(0, NetProtocol.KnockoutEntrants).Select(id => new NetKnockoutEntry(
            _koNames[id] ?? $"BOT {id + 1}", id < series.Humans ? _humanSlots[id] : -1, series.Entrants.Contains(id),
            completed > 0 ? series.Place(id) : 0, series.EliminatedRound(id))).ToArray();
        return new NetKnockoutState(completed, series.Finished, series.Winner, entries);
    }

    /// <summary>Sends a chat line to every connected member of this game (any phase), each with its own connection epoch.
    /// Returns the number of recipients.</summary>
    internal int BroadcastChat(string sender, string text)
    {
        int count = 0;
        foreach (var member in _members.ToArray())
        {
            if (member.Connection is not { } connection) continue;
            _output.Send(connection, NetChannel.ReliableOrdered, new NetChatMessage(_gameId, connection.Epoch, sender, text).Encode());
            count++;
        }
        return count;
    }

    void BroadcastState()
    {
        // Every connected member receives its own state (with its own resume token); reserved members have no connection.
        foreach (var member in _members.ToArray())
        {
            if (member.Connection is not { } connection) continue;
            var bytes = BuildState(connection).Encode();
            if (_output.PendingReliable(connection) > _options.MaxReliableBacklog)
            {
                _output.Disconnect(connection, NetRejectReason.SlowClient);
                continue;
            }
            _output.Send(connection, NetChannel.ReliableOrdered, bytes);
        }
    }

    /// <summary>The complete lobby state for one connection (reliable, idempotent); used by the session server after join.</summary>
    public NetLobbyState StateFor(ClientConnection connection) => BuildState(connection);

    public NetGameInfo ToInfo() => new(_gameId, _name, Phase, _members.Count, _maxPlayers, _track, _laps, _mode, _backward, _bots, _traffic, _skill, _catchUp, _style);

    /// <summary>Routes a message with the round's own header to the active round of this game. Returns false when the game is
    /// not racing or the connection is not a member (the session server then counts a foreign message).</summary>
    internal bool TryReceiveRace(ClientConnection connection, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (_round == null) return false;
        var member = _members.FirstOrDefault(m => ReferenceEquals(m.Connection, connection));
        if (member?.RoundMember is not { } raceMember) return false;
        _round.Receive(raceMember, channel, data);
        return true;
    }

    internal void SendRace(GameMember member, NetChannel channel, byte[] data)
    {
        if (member.Connection is { } connection && _members.Contains(member)) _output.Send(connection, channel, data);
    }

    internal int PendingReliable(GameMember member) => member.Connection is { } c ? _output.PendingReliable(c) : 0;

    /// <summary>The round core ended a member (its own Drop or the stop of the round). Only the round is affected: a member that
    /// left the race or sent a Ready for another setup just leaves the game (the connection stays and gets the games list),
    /// and the stop of a round never touches the connections. A misbehaving client (malformed traffic, slow, timeout) still
    /// loses its connection.</summary>
    internal void RaceDisconnect(GameMember member, byte[] reason)
    {
        if (member.Connection is not { } connection) return;
        var why = NetReject.TryDecode(reason)?.Reason ?? NetRejectReason.Malformed;
        switch (why)
        {
            case NetRejectReason.RaceClosed or NetRejectReason.ServerShutdown:
                return;
            case NetRejectReason.Left or NetRejectReason.ConfigMismatch:
                _log?.Invoke($"game {_gameId:X8} '{_name}': '{member.Name}' left the race ({why}); back to the games list");
                _output.LeaveGame(connection);
                return;
            default:
                _output.Disconnect(connection, why);
                return;
        }
    }

    /// <summary>Ends the reservation of <paramref name="name"/> (a former member that lost its connection or left in
    /// Countdown/Race): the member is removed, its car in a running race is marked DNF. Used when the same player joins or
    /// creates another game. Returns whether a reservation existed.</summary>
    internal bool ReleaseReservation(string name, long now)
    {
        var member = _members.FirstOrDefault(m => !m.Connected && m.DisconnectedSinceMicros != null &&
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (member == null) return false;
        _log?.Invoke($"game {_gameId:X8} '{_name}': reservation of '{member.Name}' ended (the player joined another game)");
        RemoveMember(member);
        _now = now;
        _lastActivity = now;
        Revision++;
        if (_members.Count > 0) BroadcastState();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _round?.Dispose();
        _round = null;
    }

    sealed class SessionRaceEndpoint(GameSession game, GameMember member) : IRaceEndpoint
    {
        public void Send(NetChannel channel, byte[] data) => game.SendRace(member, channel, data);
        public int PendingReliable => game.PendingReliable(member);
        public void Disconnect(byte[] reason) => game.RaceDisconnect(member, reason);
    }
}
