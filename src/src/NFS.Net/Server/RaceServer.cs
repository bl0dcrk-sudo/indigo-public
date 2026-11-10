using System.Security.Cryptography;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

public sealed record RaceServerOptions
{
    /// <summary>Data mods of this server; a client with another set is rejected with <see cref="NetRejectReason.ModsMismatch"/>.</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;
    /// <summary>Cars of this server (original + mod cars); null = the original cars only.</summary>
    public NFS.Game.Cars.CarCatalog? Cars { get; init; }
    /// <summary>Optional owner identity required for the first seat before accepting other menu-host players.</summary>
    public string? RequiredFirstPlayerName { get; init; }
    /// <summary>Close the race when the seats are not all ready within this time (no endless waiting service).</summary>
    public long WaitTimeoutMicros { get; init; } = 120_000_000;
    /// <summary>Abort with a result after this many host steps (0 = no limit). Deterministic bounded finish for tests/smoke runs.</summary>
    public int MaxRaceSteps { get; init; }
    /// <summary>Time after the result before the server closes all connections.</summary>
    public long LingerMicros { get; init; } = 1_000_000;
    /// <summary>Upper bound of the graceful close: queued reliable messages (the ServerStop result, player-left) are drained
    /// until the transport has acknowledged them, then the connections are closed. Never waits longer than this.</summary>
    public long ShutdownDrainMicros { get; init; } = 1_000_000;
    public int MaxReliableBacklog { get; init; } = NetProtocol.MaxReliableBacklog;
    /// <summary>Malformed / foreign messages tolerated per connection before it is dropped.</summary>
    public int MaxBadMessages { get; init; } = 64;
    /// <summary>Interactive lobby: the operator must issue `start`; filling the seats and readiness never start the race.</summary>
    public bool ManualStart { get; init; }
    /// <summary>AI opponents of the capacity lobby race (legacy display value); a dynamic start derives its bot count from
    /// <see cref="BotSetting"/> and the connected humans instead.</summary>
    public int BotCount { get; init; }
    /// <summary>Initial operator bot setting of the interactive lobby: <see cref="ServerRaceConfig.AutoBots"/> fills the grid,
    /// 0..7 requests that many class-2 AI cars (capped so humans + bots ≤ 8).</summary>
    public int BotSetting { get; init; } = ServerRaceConfig.AutoBots;
    /// <summary>Minimum connected-and-ready humans required by `start`; -1 = every configured seat (exact-count race). A
    /// dynamic start composes N human cars and the remaining grid slots as real AI opponents.</summary>
    public int MinPlayers { get; init; } = -1;
    /// <summary>Straggler limit: once the first human has finished, the round ends this long after it and every unfinished car is DNF
    /// (ranked behind the finishers, see <see cref="RoundClassification"/>). 0 = no limit (the legacy race server).</summary>
    public long StragglerLimitMicros { get; init; }
}

/// <summary>Counters of the server (diagnostics, tests).</summary>
public sealed class RaceServerStats
{
    public long Steps, ClockSlipSteps, SnapshotParts, SnapshotBytes, MaxSnapshotPart, MaxPartsPerSnapshot, DurableMessages, DurableBytes, MaxDurableMessage,
        EventMessages, InputPackets, RateLimited, Malformed, WrongSession, Rejected, SlowClientDrops, Disconnects, MaxReliableBacklog;
}

/// <summary>
/// Authoritative race server logic (network stage 5a.3), independent of sockets and of the wall clock: the host process feeds it
/// transport callbacks and the current monotonic time (<see cref="Update"/>). It owns the <see cref="RaceHost"/> (the same one the
/// local game uses): only <see cref="Update"/> steps it, one whole RaceRunner.Step per 32 Hz server step (two 64 Hz ticks), with
/// inputs chosen by the per-seat <see cref="SeatInputScheduler"/>. Clients send controls, never positions or laps.
/// Seats are tied to a connection and its epoch; the race configuration is frozen before the countdown (an interactive start
/// composes a fresh host with exactly the connected human cars, the rest being real AI opponents); the start is common and
/// timed by the server clock. Disconnect policy (MVP, no reconnect): before the countdown the seat is freed; afterwards the car
/// drives neutral and is DNF — the race is aborted with a result when no human is left or every remaining human has finished.
/// </summary>
public sealed class RaceServer : INetServerHandler, IDisposable
{
    sealed class Seat(int index)
    {
        /// <summary>Lobby slot (stable for the entire server run; roster/console use it).</summary>
        public readonly int Index = index;
        /// <summary>Seat index in the current race host (welcome seat); -1 when this lobby slot is not one of its human cars.</summary>
        public int RaceSeat = index;
        /// <summary>Human car index in the current host (<see cref="RaceSessionDescription.LocalSeats"/>); -1 when unused.</summary>
        public int CarId = index;
        /// <summary>Car model type this player selected in the menu (<see cref="NetProtocol.NoCarType"/> = keep the frozen
        /// model). Stored per lobby slot and kept across rounds; a start composes the human car with it.</summary>
        public int CarType = NetProtocol.NoCarType;
        public Connection? Conn;
        public bool Ready, Dnf;
        /// <summary>The human's connection is lost but its seat is reserved for a reconnect (session grace window): the car drives
        /// neutral and the round does not wait for it once every other human has finished.</summary>
        public bool Away;
        public SeatInputScheduler Inputs = new();
        /// <summary>A spectator of the round (not one of <c>_seats</c>/<c>_roundSeats</c>): it gets the whole stream, owns no seat,
        /// sends no controls and never influences the simulation, the roster, readiness or the end of the round.</summary>
        public bool Spectator;
        /// <summary>Human seat whose car a spectator's view starts on (the spectator welcome names it as its seat).</summary>
        public int WatchSeat;
    }

    sealed class Connection(int id, uint epoch, Seat seat, string name)
    {
        public readonly int Id = id;
        public uint Epoch = epoch;
        public readonly Seat Seat = seat;
        public readonly string Name = name;
        public bool ResultsSent;
        /// <summary>The client confirmed the result: a graceful stop may close the connection (drain criterion).</summary>
        public bool ResultsAck;
        public long RateWindowStart, RateCount;
        public int Bad;
    }

    /// <summary>Creates a fresh race host with an exact number of human cars (race id, humans); null = one host for this server.</summary>
    readonly Func<int, int, RaceHost>? _hostFactory;
    /// <summary>Creates a fresh race host with an exact number of human cars and real AI opponents (race id, humans, bots);
    /// required for operator bot changes and preferred over <see cref="_hostFactory"/> when present.</summary>
    readonly Func<int, int, int, RaceHost>? _hostFactoryBots;
    /// <summary>Creates a fresh race host with an exact number of human cars, real AI opponents and the car model types the
    /// human players selected (race id, humans, bots, types indexed by human car; <see cref="NetProtocol.NoCarType"/> keeps
    /// the frozen model). A host that cannot honour an explicit selection is never started with the wrong model.</summary>
    readonly Func<int, int, int, IReadOnlyList<int>, RaceHost>? _hostFactoryTypes;
    /// <summary>Creates a fresh race host on a given track and direction (race id, humans, bots, types, track, backward).
    /// When present it supersedes the other factories: every recomposed round goes through it, which is what lets the
    /// operator change the track and the direction. The server without one keeps the legacy factories (and their frozen
    /// track/direction) byte-identically.</summary>
    readonly Func<int, int, int, IReadOnlyList<int>?, int, bool, RaceHost>? _hostFactoryTrack;
    RaceHost _host;
    /// <summary>Menu track index (0..7, the same number as <see cref="ServerRaceConfig.Track"/> and the configuration dialog)
    /// of the next composed round (`track N` in the lobby). The race setup stores the data track number instead
    /// (<see cref="FrontEndConstants.TrackMap"/>); starts as the menu index of the initial host's track.</summary>
    int _nextTrack;
    /// <summary>Data track number (TRnn directory, what the setup and <see cref="RaceLoader"/> use) of the next composed
    /// round; starts as the track of the initial host and follows <see cref="_nextTrack"/>. Kept apart so a host whose track
    /// is not in the menu map still matches itself.</summary>
    int _nextRaceTrack;
    /// <summary>Direction of the next composed round (`direction forward|backward` in the lobby): true = backward. Starts as
    /// the direction of the initial host and is what <see cref="CreateHostFor"/> passes to the track-aware factory.</summary>
    bool _nextBackward;
    readonly INetServerTransport _transport;
    readonly RaceServerOptions _options;
    readonly Action<string>? _log;
    readonly Random _random;
    readonly byte[] _fingerprint;
    readonly Seat[] _seats;
    /// <summary>Seats of the current host in human-car order (welcome seat index = position); rebuilt for every round.</summary>
    Seat[] _roundSeats = [];
    readonly Dictionary<int, Connection> _connections = [];
    readonly HashSet<int> _usedRaceIds = [];
    readonly HashSet<uint> _usedEpochs = [];
    NetWelcome _welcomeTemplate;
    RaceViewFrame _baseline;
    byte[][] _baselineParked;
    bool[] _parkedDirty;
    readonly List<RaceEvent> _stepEvents = [];
    long _created;
    long _t1 = -1;              // server time of host step 1
    long _finishedAt = -1;
    long _firstFinishStep = -1; // host step at which the first racing car finished (starts the straggler countdown), -1 = none yet
    IReadOnlyList<int> _forcedLast = [];
    long _durableSequence;
    SkidBuffer? _sentSkids;
    NetResults? _results;
    int _botSetting;                // operator bot selection: AutoBots (-1) fills the grid, 0..7 requests that many AI cars
    bool _operatorStartRequired;    // a prepared next round always waits for the operator, even without ManualStart
    bool _startPending;             // a dynamic round is composed and waits for every client to accept its new welcome
    bool _startAborted;             // a client left while the dynamic round waited: rebuild it for the remaining humans
    bool _lobbyBroken;              // cannot recreate the lobby host after an aborted dynamic start (reject joins)
    bool _disposed;
    bool _stopping;                 // graceful close in progress: no joins, no steps, drain reliable output, then disconnect
    NetRejectReason _stopReason;
    string _stopWhy = "";
    long _stopDeadline;

    public NetRacePhase Phase { get; private set; } = NetRacePhase.WaitingForPlayers;
    /// <summary>A close was requested and the server is draining reliable output (no joins, no steps) before disconnecting.</summary>
    public bool Stopping => _stopping;

    /// <summary>True while the loop must keep its fast cadence: a countdown/race is running, a round start is being prepared or
    /// the server is draining a stop. Otherwise the loop may wait longer (<see cref="ServerIdlePolicy"/>).</summary>
    public bool NeedsFastLoop => _stopping || _startPending || Phase is NetRacePhase.Countdown or NetRacePhase.Racing;
    public int RaceId { get; private set; }
    public RaceServerStats Stats { get; } = new();
    public RaceHost Host => _host;
    public NetFinishReason? FinishReason => _results?.Reason;
    /// <summary>The result the clients received: after an aborted round it carries the DNF classification of
    /// <see cref="RoundClassification"/> (null before the end of the round).</summary>
    public RaceResults? FinalResults => _results?.Results;
    /// <summary>Race car indices of humans that were gone (left, or away) from the round unfinished and were ranked last.</summary>
    public IReadOnlyList<int> ForcedLastCars => _forcedLast;
    /// <summary>Time the stragglers have left in milliseconds (0 = no countdown running: nobody finished yet, no limit, or the round
    /// is over). The first human finisher starts it; at zero the round ends with <see cref="NetFinishReason.StragglerLimit"/>.</summary>
    public int CutoffRemainingMillis
    {
        get
        {
            if (_options.StragglerLimitMicros <= 0 || _firstFinishStep < 0 || _results != null) return 0;
            long left = _options.StragglerLimitMicros - (_host.Steps - _firstFinishStep) * NetProtocol.StepMicros;
            return (int)Math.Clamp((left + 999) / 1000, 1, NetProtocol.MaxCutoffMillis);
        }
    }
    public byte[] SetupHash { get; private set; }
    /// <summary>True when the server can start another race of the same configuration after the result.</summary>
    public bool SupportsNextRound => _hostFactory != null || _hostFactoryBots != null || _hostFactoryTrack != null;
    /// <summary>True when a start may compose a fresh host (fewer humans, another bot count, the selected car models or another
    /// track).</summary>
    bool CanCompose => SupportsNextRound || _hostFactoryTypes != null;
    /// <summary>True when the operator may change the bot count (the server can compose hosts with an explicit AI count).</summary>
    public bool SupportsBotCounts => _hostFactoryBots != null || _hostFactoryTrack != null;
    /// <summary>True when the operator may change the track (the server can compose a host on another track).</summary>
    public bool SupportsTrackChange => _hostFactoryTrack != null;
    /// <summary>True when the operator may change the direction (the server can compose a host forward or backward).</summary>
    public bool SupportsDirectionChange => _hostFactoryTrack != null;
    /// <summary>Menu track index (0..7, the number of the configuration dialog and of <c>track N</c>) of the next composed
    /// round. The setup of a race stores the data track number (menu 1 = data track 2), see <see cref="DataTrack"/>.</summary>
    public int Track => _nextTrack;
    /// <summary>Data track number (TRnn) the next composed round loads; this is what the lobby shows.</summary>
    public int DataTrack => _nextRaceTrack;
    /// <summary>Direction of the next composed round: true = backward, false = forward.</summary>
    public bool Backward => _nextBackward;
    /// <summary>Operator bot selection: <see cref="ServerRaceConfig.AutoBots"/> (fill the grid) or 0..7 requested AI cars.</summary>
    public int BotSetting => _botSetting;
    /// <summary>Largest useful bot count for the connected humans: 8 − humans, with one human assumed while the lobby is empty.</summary>
    public int BotCap => ServerRaceConfig.MaxRacers - Math.Clamp(Math.Max(1, ConnectedPlayers), 1, ServerRaceConfig.MaxRacers);
    /// <summary>Bot count the next `start` will use for the currently connected humans (auto fills the grid, an explicit
    /// request is capped by <see cref="BotCap"/>).</summary>
    public int PlannedBots => _botSetting == ServerRaceConfig.AutoBots ? BotCap : Math.Min(_botSetting, BotCap);
    /// <summary>Raised on the server thread after every roster change (connect, ready, leave) for the console lobby.</summary>
    public event Action? RosterChanged;
    /// <summary>Humans the operator needs: the configured minimum, or every configured seat for an exact-count race.</summary>
    public int RequiredPlayers => _options.MinPlayers < 0
        ? _seats.Length
        : Math.Clamp(_options.MinPlayers, NetProtocol.MinSeats, _seats.Length);
    /// <summary>Connected humans; empty seats are AI-driven once the race starts.</summary>
    public int ConnectedPlayers => _seats.Count(s => s.Conn != null);
    /// <summary>Lobby start condition: at least <see cref="RequiredPlayers"/> connected, and every connected player ready.</summary>
    public bool CanStart => !_startPending && Phase is (NetRacePhase.WaitingForPlayers or NetRacePhase.Loading) &&
                            ConnectedPlayers >= RequiredPlayers && _seats.Where(s => s.Conn != null).All(s => s.Ready);
    /// <summary>True while a composed round waits for the clients to accept its new welcome (no join, no second start).</summary>
    public bool StartPending => _startPending;
    /// <summary>Current lobby (seat names/cars/readiness, race options). Safe to read from the server thread only.</summary>
    public ServerLobbyState Lobby
    {
        get
        {
            var cars = _host.Runner.Session.Cars;
            // A player's menu selection is authoritative for the next start; otherwise show the frozen host model of the slot.
            var seats = _seats.Select(s => new ServerLobbySeat(s.Index, s.Conn?.Name, s.CarId,
                NetProtocol.IsCarNumber(s.CarType) ? CarName(s.CarType)
                : s.CarId >= 0 && s.CarId < cars.Count ? cars[s.CarId].Model : null, s.Conn != null, s.Ready)).ToArray();
            return new ServerLobbyState(Phase, _nextRaceTrack, _baseline.NumLaps, PlannedBots,
                RequiredPlayers, seats, _botSetting, BotCap, _nextBackward);
        }
    }
    /// <summary>When set (tests/diagnostics), every event the host published is kept here (what clients should receive once).</summary>
    public List<RaceEvent>? PublishedEvents { get; set; }

    public RaceServer(RaceHost host, byte[] dataFingerprint, INetServerTransport transport, long now, RaceServerOptions? options = null,
        Action<string>? log = null, int seed = 0, Func<int, int, RaceHost>? hostFactory = null,
        Func<int, int, int, RaceHost>? hostFactoryBots = null,
        Func<int, int, int, IReadOnlyList<int>, RaceHost>? hostFactoryTypes = null,
        Func<int, int, int, IReadOnlyList<int>?, int, bool, RaceHost>? hostFactoryTrack = null)
    {
        _host = host;
        _hostFactory = hostFactory;
        _hostFactoryBots = hostFactoryBots;
        _hostFactoryTypes = hostFactoryTypes;
        _hostFactoryTrack = hostFactoryTrack;
        _nextRaceTrack = host.Runner.Session.TrackNumber;
        _nextTrack = MenuTrackFor(_nextRaceTrack);
        _nextBackward = host.Runner.Session.Backward;
        _transport = transport;
        _options = options ?? new RaceServerOptions();
        _botSetting = _options.BotSetting;
        if (_botSetting is < ServerRaceConfig.AutoBots or > ServerRaceConfig.MaxRacers - ServerRaceConfig.MinPlayers)
            throw new ArgumentOutOfRangeException(nameof(options), $"bot setting must be {ServerRaceConfig.AutoBots} (auto) or 0..{ServerRaceConfig.MaxRacers - 1}");
        _log = log;
        _random = seed == 0 ? new Random() : new Random(seed);
        _fingerprint = dataFingerprint;
        _created = now;
        RaceId = host.View.RaceId;
        _usedRaceIds.Add(RaceId);
        var desc = host.View.Description;
        if (desc.LocalSeats.Count is < NetProtocol.MinSeats or > NetProtocol.MaxSeats)
            throw new ArgumentException($"host needs {NetProtocol.MinSeats}..{NetProtocol.MaxSeats} human seats");
        _seats = desc.LocalSeats.Select((car, i) => new Seat(i) { CarId = car }).ToArray();
        _roundSeats = _seats; // the initial host owns one human car per lobby seat
        var round = PrepareHost(host);
        SetupHash = round.SetupHash;
        _baseline = round.Baseline;
        _baselineParked = round.BaselineParked;
        _parkedDirty = round.ParkedDirty;
        _welcomeTemplate = round.Welcome;
        host.View.Published += OnPublished;
    }

    /// <summary>Everything the server derives from a host (welcome template, setup hash, baseline). Computed before the old host
    /// is released, so a failure to prepare the next round leaves the finished round and its host untouched.</summary>
    readonly record struct RoundView(byte[] SetupHash, RaceViewFrame Baseline, byte[][] BaselineParked, bool[] ParkedDirty, NetWelcome Welcome);

    RoundView PrepareHost(RaceHost host)
    {
        var desc = host.View.Description;
        var baseline = host.View.Latest;
        var setupHash = SHA256.HashData(host.Runner.Session.Setup.Block);
        var welcome = new NetWelcome(host.View.RaceId, 0, 0, desc.LocalSeats, baseline.Cars.Count, baseline.Parked.Count, desc.SimulatedCars,
            desc.Redline, desc.Countdown, baseline.NumLaps, NetProtocol.StepMicros, NetProtocol.MaxFutureSteps, NetProtocol.HoldSteps,
            NetProtocol.ActionExpirySteps, NetProtocol.InputRedundancy, NetProtocol.MaxDatagram, host.Runner.Session.Setup.Block.ToArray(),
            setupHash, _fingerprint);
        return new RoundView(setupHash, baseline, baseline.Parked.Select(EncodeParked).ToArray(), new bool[baseline.Parked.Count], welcome);
    }

    /// <summary>Commits a prepared round view (field assignments only, cannot fail halfway).</summary>
    void AdoptHost(RoundView round)
    {
        SetupHash = round.SetupHash;
        _baseline = round.Baseline;
        _baselineParked = round.BaselineParked;
        _parkedDirty = round.ParkedDirty;
        _welcomeTemplate = round.Welcome;
    }

    static byte[] EncodeParked(CarFrame c)
    {
        var w = new WireWriter(NetProtocol.MaxDatagram);
        CarViewCodec.Write(w, c.State);
        return w.ToArray();
    }

    void Log(string s) => _log?.Invoke(s);

    void OnPublished(RaceViewFrame frame, IReadOnlyList<RaceEvent> events)
    {
        foreach (var e in events)
        {
            PublishedEvents?.Add(e);
            if (e is not RaceFinishedEvent) _stepEvents.Add(e); // the result travels as NetResults
        }
    }

    // ---------------- transport callbacks ----------------

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
        if (!hello.Mods.SameAs(_options.Mods)) return RejectMods(hello.Mods);
        if (hello.CarType != NetProtocol.NoCarType && !NetCars.IsSelectableNumber(hello.CarType, _options.Cars)) return Reject(NetRejectReason.Malformed);
        // A stopping/closed server never accepts a new seat, even while the last reliable messages are still draining.
        if (_stopping || Phase == NetRacePhase.Closed) return Reject(NetRejectReason.ServerShutdown);
        if (_options.RequiredFirstPlayerName is { } owner && _seats.All(s => s.Conn == null) && !string.Equals(hello.Name, owner, StringComparison.Ordinal))
            return Reject(NetRejectReason.RaceFull);
        // A composed round is being handed to its clients: it already has exactly as many human cars as connections.
        if (Phase >= NetRacePhase.Countdown || _startPending || _lobbyBroken) return Reject(NetRejectReason.RaceInProgress);
        if (_seats.All(s => s.Conn != null)) return Reject(NetRejectReason.RaceFull);
        return null;
    }

    /// <summary>Name of a car number: original name or the display name of a mod car of <see cref="RaceServerOptions.Cars"/>.</summary>
    string CarName(int carType) =>
        carType < RaceConstants.CarNames.Count ? RaceConstants.CarNames[carType]
        : _options.Cars is { } cars && cars.TryGetByNumber(carType, out var def) ? def.DisplayName : $"car {carType}";

    byte[] RejectMods(NetModSet client)
    {
        Stats.Rejected++;
        Log($"reject connection: ModsMismatch, {_options.Mods.Diff(client, "server", "client")}");
        return new NetReject(NetRejectReason.ModsMismatch) { ServerMods = _options.Mods }.Encode();
    }

    byte[] Reject(NetRejectReason reason, ushort clientVersion = NetProtocol.Version)
    {
        Stats.Rejected++;
        Log($"reject connection: {reason}");
        return new NetReject(reason).Encode(clientVersion);
    }

    public void OnConnected(int connection, ReadOnlySpan<byte> data)
    {
        var hello = NetHello.Decode(data);
        var seat = _seats.First(s => s.Conn == null);
        uint epoch = NewEpoch();
        var conn = new Connection(connection, epoch, seat, hello.Name);
        seat.Conn = conn;
        seat.Ready = false;
        seat.CarType = hello.CarType;
        seat.Inputs = new SeatInputScheduler();
        _connections[connection] = conn;
        if (Phase == NetRacePhase.WaitingForPlayers && _seats.All(s => s.Conn != null)) Phase = NetRacePhase.Loading;
        Log($"conn {connection} '{hello.Name}' -> seat {seat.Index} car {seat.CarId}" +
            (NetProtocol.IsCarNumber(hello.CarType) ? $" model {hello.CarType}" : "") + $" epoch {epoch:X8}");
        SendRoundStart(conn, NetRacePhase.WaitingForPlayers);
        BroadcastRoster();
    }

    /// <summary>The round can still take a spectator: it has not started its countdown and is not closing.</summary>
    internal bool CanAddSpectator(out NetRejectReason reason)
    {
        reason = _stopping || Phase == NetRacePhase.Closed ? NetRejectReason.ServerShutdown
            : Phase >= NetRacePhase.Countdown ? NetRejectReason.RaceInProgress : NetRejectReason.None;
        return reason == NetRejectReason.None;
    }

    /// <summary>Adds a spectator (an eliminated knockout player): the same welcome (flagged <see cref="NetWelcome.Spectator"/>), baseline and
    /// roster as a racer, then the whole stream (snapshots, events, durable state, start and result). The spectator has no seat: it is
    /// not part of the lobby, the start condition, the inputs, the straggler rule or the classification, and its loss changes nothing.</summary>
    internal void OnConnectedSpectator(int connection, ReadOnlySpan<byte> data)
    {
        var hello = NetHello.Decode(data);
        var seat = new Seat(-1) { RaceSeat = -1, CarId = -1, Spectator = true, WatchSeat = 0 };
        uint epoch = NewEpoch();
        var conn = new Connection(connection, epoch, seat, hello.Name);
        seat.Conn = conn;
        _connections[connection] = conn;
        Log($"conn {connection} '{hello.Name}' -> spectator, epoch {epoch:X8}");
        SendRoundStart(conn, Phase);
        SendRoster(conn);
    }

    /// <summary>Welcome of a connection: its own seat, or for a spectator the seat whose car the view starts on.</summary>
    NetWelcome WelcomeFor(Connection c) => c.Seat.Spectator
        ? _welcomeTemplate with { Epoch = c.Epoch, Seat = Math.Clamp(c.Seat.WatchSeat, 0, _welcomeTemplate.SeatCarIds.Count - 1), Spectator = true }
        : _welcomeTemplate with { Epoch = c.Epoch, Seat = c.Seat.RaceSeat };

    /// <summary>
    /// Re-attaches a connection whose seat the round kept during the reconnect grace window. The seat and car stay, the input
    /// queue restarts (the new client numbers inputs from 1); the connection gets a fresh epoch, a welcome flagged <see cref="NetWelcome.Resume"/>, the full durable state and
    /// the baseline. Controls carrying the old epoch are rejected by the header check.
    /// </summary>
    internal bool TryResume(int connection, out NetRejectReason reason)
    {
        if (_stopping || Phase == NetRacePhase.Closed) { reason = NetRejectReason.RaceClosed; return false; }
        if (!_connections.TryGetValue(connection, out var conn)) { reason = NetRejectReason.RaceClosed; return false; }
        conn.Epoch = NewEpoch();
        conn.Seat.Inputs.Restart();
        conn.Seat.Dnf = false;
        conn.Seat.Away = false;
        conn.ResultsSent = false;
        conn.ResultsAck = false;
        conn.Bad = 0;
        conn.RateWindowStart = _now;
        conn.RateCount = 0;
        // Welcome, then the full durable state, then the baseline whose durable sequence is the one the client just applied
        // (all reliable ordered, so the client resets on the welcome before it mirrors the state).
        _transport.Send(conn.Id, NetChannel.ReliableOrdered,
            (WelcomeFor(conn) with { Resume = true }).Encode());
        PublishResumeDurable(conn);
        var info = Info(_baseline, Phase);
        foreach (var part in SnapshotCodec.EncodeParts(RaceId, conn.Epoch, info, default, SnapshotCodec.EncodeCars(_baseline, _ => true)))
            _transport.Send(conn.Id, NetChannel.ReliableOrdered, part);
        BroadcastRoster();
        Log($"conn {conn.Id} '{conn.Name}' resumed seat {conn.Seat.Index} at step {_host.Steps}, epoch {conn.Epoch:X8}");
        reason = NetRejectReason.None;
        return true;
    }

    /// <summary>The owner lost the member's connection but keeps its seat for a reconnect (<paramref name="away"/>), or the member is
    /// back. While away the car drives neutral; the round ends without waiting for it once the other humans finished.</summary>
    internal void SetAway(int connection, bool away)
    {
        if (_connections.TryGetValue(connection, out var conn)) conn.Seat.Away = away;
    }

    /// <summary>Connection epoch, never reused on this server process (the client remembers every epoch of its connection).</summary>
    uint NewEpoch()
    {
        uint epoch;
        do epoch = (uint)_random.NextInt64(1, uint.MaxValue); while (!_usedEpochs.Add(epoch));
        return epoch;
    }

    /// <summary>Welcome, baseline snapshot and (by the caller) roster of the current round, reliable and in this order.</summary>
    void SendRoundStart(Connection c, NetRacePhase phase)
    {
        _transport.Send(c.Id, NetChannel.ReliableOrdered, WelcomeFor(c).Encode());
        // baseline: the initial frame with every parked car (reliable, once)
        var info = Info(_baseline, phase);
        foreach (var part in SnapshotCodec.EncodeParts(RaceId, c.Epoch, info, default, SnapshotCodec.EncodeCars(_baseline, _ => true)))
            _transport.Send(c.Id, NetChannel.ReliableOrdered, part);
    }

    void BroadcastRoster()
    {
        foreach (var c in _connections.Values) SendRoster(c);
        RosterChanged?.Invoke();
    }

    void SendRoster(Connection c)
    {
        // Race seat indices (not lobby slots): clients key names/readiness by the seat of their current welcome. Spectators have no seat.
        var players = _connections.Values.Where(x => x.Seat.RaceSeat >= 0).ToDictionary(x => x.Seat.RaceSeat, x => x.Name);
        var ready = _seats.Where(s => s.Conn != null && s.Ready && s.RaceSeat >= 0).Select(s => s.RaceSeat).ToArray();
        _transport.Send(c.Id, NetChannel.ReliableOrdered, new NetRoster(RaceId, c.Epoch, players, ready).Encode());
    }

    /// <summary>Monotonic clock (microseconds) read when data arrives (rate limits); without it the last <see cref="Update"/> time is used.</summary>
    public Func<long>? Clock { get; init; }

    public void OnReceive(int connection, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (Clock != null) _now = Clock();
        if (!_connections.TryGetValue(connection, out var conn)) return;
        if (!RateOk(conn)) { Stats.RateLimited++; return; }
        try
        {
            if (data.Length > NetProtocol.MaxDatagram) throw new NetProtocolException("oversized client datagram");
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            if (h.RaceId != RaceId || h.Epoch != conn.Epoch)
            {
                Stats.WrongSession++;
                Bad(conn);
                return;
            }
            // A closing server stops accepting gameplay traffic, but it still needs the result acknowledgement that ends
            // the graceful drain.
            if (_stopping && h.Type != NetMessageType.ResultsAck) return;
            switch (h.Type)
            {
                case NetMessageType.Ready:
                {
                    var ready = NetReady.Decode(h, ref r);
                    if (!ready.SetupHash.AsSpan().SequenceEqual(SetupHash)) { Drop(conn, NetRejectReason.ConfigMismatch); return; }
                    if (conn.Seat.Spectator) break; // a spectator is never waited for
                    conn.Seat.Ready = true;
                    Log($"seat {conn.Seat.Index} ready");
                    BroadcastRoster();
                    break;
                }
                case NetMessageType.Input:
                {
                    var packet = NetInputPacket.Decode(h, ref r);
                    if (conn.Seat.Spectator) return; // a spectator has no seat: its controls (a modified client) are ignored, never counted
                    Stats.InputPackets++;
                    if (Phase >= NetRacePhase.Finished || conn.Seat.Dnf) return;
                    conn.Seat.Inputs.Accept(packet, _host.Steps + 1);
                    break;
                }
                case NetMessageType.Leave:
                    r.End();
                    Drop(conn, NetRejectReason.Left);
                    break;
                case NetMessageType.ResultsAck:
                    r.End();
                    if (conn.ResultsSent) conn.ResultsAck = true;
                    break;
                default:
                    Stats.Malformed++;
                    Bad(conn);
                    break;
            }
        }
        catch (NetProtocolException e)
        {
            Stats.Malformed++;
            Log($"conn {connection}: malformed message ({e.Message})");
            Bad(conn);
        }
    }

    long _now;

    bool RateOk(Connection c)
    {
        if (_now - c.RateWindowStart >= 1_000_000) { c.RateWindowStart = _now; c.RateCount = 0; }
        return ++c.RateCount <= NetProtocol.MaxInputPacketsPerSecond;
    }

    void Bad(Connection c)
    {
        if (++c.Bad > _options.MaxBadMessages) Drop(c, NetRejectReason.Malformed);
    }

    public void OnDisconnected(int connection)
    {
        // transport-level end (graceful disconnect or timeout); the server keeps no reconnect slot
        if (!_connections.TryGetValue(connection, out var conn)) return;
        if (_stopping)
        {
            // already closing: just forget the peer, no roster/result traffic while the remaining queue drains
            _connections.Remove(connection);
            Stats.Disconnects++;
            conn.Seat.Conn = null;
            conn.Seat.Inputs.Clear();
            return;
        }
        Leave(conn, NetRejectReason.Left);
    }

    /// <summary>Server-side drop of a connection with a reason.</summary>
    void Drop(Connection conn, NetRejectReason reason)
    {
        if (!_connections.ContainsKey(conn.Id)) return;
        Leave(conn, reason);
        _transport.Disconnect(conn.Id, new NetReject(reason).Encode());
    }

    void Leave(Connection conn, NetRejectReason reason)
    {
        _connections.Remove(conn.Id);
        Stats.Disconnects++;
        var seat = conn.Seat;
        seat.Conn = null;
        seat.Inputs.Clear();
        if (seat.Spectator)
        {
            // A spectator owns nothing the round depends on: no DNF, no roster change, no rebuild of a composed start.
            Log($"conn {conn.Id} spectator left ({reason}) in {Phase} at step {_host.Steps}");
            return;
        }
        Log($"conn {conn.Id} left seat {seat.Index} ({reason}) in {Phase} at step {_host.Steps}");
        if (Phase < NetRacePhase.Countdown)
        {
            seat.Ready = false;
            seat.CarType = NetProtocol.NoCarType; // a free lobby slot has no selection; a join sets its own
            seat.Inputs = new SeatInputScheduler();
            if (_startPending)
            {
                // The composed round no longer has one human per human car: rebuild it (Update) before any countdown.
                _startAborted = true;
                return;
            }
            if (Phase == NetRacePhase.Loading) Phase = NetRacePhase.WaitingForPlayers;
            BroadcastRoster();
            return;
        }
        if (Phase >= NetRacePhase.Finished) return;
        seat.Dnf = true;
        foreach (var c in _connections.Values)
            _transport.Send(c.Id, NetChannel.ReliableOrdered, new NetPlayerLeft(RaceId, c.Epoch, seat.RaceSeat, reason, _host.Steps).Encode());
        BroadcastRoster();
        // no human left: the next Update ends the race with a result (not from inside a transport callback / publication)
    }

    // ---------------- clock ----------------

    /// <summary>Advances the server to <paramref name="now"/> (monotonic microseconds): starts the race when all seats are ready,
    /// runs the due host steps (bounded catch-up) and closes the race after the result. Never waits for a client.</summary>
    public void Update(long now)
    {
        _now = now;
        if (_stopping)
        {
            // Never step physics after a stop was requested. Wait (bounded) until every queued reliable message — above all
            // the ServerStop result — has been acknowledged by the transport, then close the connections.
            if (now >= _stopDeadline || ReliableDrained()) CompleteStop();
            return;
        }
        switch (Phase)
        {
            case NetRacePhase.WaitingForPlayers or NetRacePhase.Loading:
                if (now - _created > _options.WaitTimeoutMicros) { Close(NetRejectReason.RaceClosed, "waiting timeout"); return; }
                if (_startPending)
                {
                    // A client left after the round was composed: recompose it for the remaining humans (or return to the
                    // lobby when the operator's minimum is no longer met). Nothing was stepped, so no state is lost.
                    if (_startAborted)
                    {
                        _startAborted = false;
                        int humans = ConnectedPlayers;
                        string why = "";
                        if (humans >= RequiredPlayers && PrepareDynamicRound(humans, out why))
                            Log($"start recomposed for {humans} humans");
                        else
                        {
                            if (why.Length == 0) why = $"{humans} connected, {RequiredPlayers} required";
                            CancelPendingStart(why);
                        }
                    }
                    else if (_roundSeats.Length > 0 && _roundSeats.All(s => s.Conn != null && s.Ready))
                    {
                        StartRound(_roundSeats, now); // every client accepted the composed welcome: countdown now
                    }
                    break;
                }
                // Unattended race: every configured seat connected and ready (an interactive lobby always waits for the
                // operator, and a prepared next round does too). A seat range with fewer connected humans composes like
                // the operator's `start`; a setup that cannot be composed stops retrying instead of looping every tick.
                if (!_options.ManualStart && !_operatorStartRequired && CanStart)
                {
                    int humans = ConnectedPlayers;
                    int bots = EffectiveBotsFor(humans);
                    if (humans == _host.Seats && HostMatches(humans, bots)) StartRound(ConnectedSeats(), now);
                    else if (CanCompose && PrepareDynamicRound(humans, out string why))
                    {
                        _startPending = true;
                        Log($"start prepared: {humans} humans, {bots} AI opponents, waiting for the new welcome to be loaded");
                    }
                    else
                    {
                        _operatorStartRequired = true;
                        Log($"start cancelled: {_host.Seats} human cars and {_host.Runner.Session.Setup.NumOpponentCars} AI cars, " +
                            $"{humans} humans and {bots} bots planned, selected car models not composable");
                    }
                }
                break;
            case NetRacePhase.Countdown or NetRacePhase.Racing:
                if (_roundSeats.All(s => s.Conn == null)) { Finish(NetFinishReason.AllHumansLeft); return; }
                RunDueSteps(now);
                break;
            case NetRacePhase.Finished or NetRacePhase.Aborted:
                // interactive server: keep the result and the connections until the operator starts the next round
                if (!SupportsNextRound && now - _finishedAt >= _options.LingerMicros) Close(NetRejectReason.RaceClosed, "race over");
                break;
        }
    }

    /// <summary>Seats with a connection, in lobby order (= the human car order of the composed round).</summary>
    Seat[] ConnectedSeats() => _seats.Where(s => s.Conn != null).OrderBy(s => s.Index).ToArray();

    /// <summary>Operator command `start` of the interactive lobby. Refused until at least the configured minimum of humans is
    /// connected and every connected player is ready. The race is then composed from exactly those humans: a new host with N
    /// human cars (class 1, LocalHuman) and 8-N real AI opponents, which the connected clients accept through a new welcome
    /// before the countdown. When the current host already has exactly N human seats (a full lobby) it is used as is, so the
    /// exact-count and two-player races keep their byte-identical setup, start message and lead time.</summary>
    public bool TryOperatorStart(out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (_stopping) { error = "server is stopping"; return false; }
        if (_startPending)
        {
            error = "race start is already being prepared";
            return false;
        }
        if (Phase is not (NetRacePhase.WaitingForPlayers or NetRacePhase.Loading))
        {
            error = $"race already {Phase}";
            return false;
        }
        int connected = ConnectedPlayers;
        int ready = _seats.Count(s => s.Conn != null && s.Ready);
        if (connected < RequiredPlayers || ready < connected)
        {
            error = $"waiting for players: {connected}/{RequiredPlayers} connected, {ready}/{connected} ready";
            return false;
        }
        var seats = ConnectedSeats();
        int bots = EffectiveBotsFor(seats.Length);
        if (!HostMatches(seats.Length, bots))
        {
            // The capacity host does not describe this start (fewer humans than the lobby, another bot count or a car model
            // selected in the menu): compose the actual race (N human cars + the planned real AI opponents).
            if (CanCompose)
            {
                if (!PrepareDynamicRound(seats.Length, out error)) return false;
                _startPending = true;
                Log($"start prepared: {seats.Length} humans, {bots} AI opponents, waiting for the new welcome to be loaded");
                return true;
            }
            // A fixed host cannot be recomposed: explain exactly which part of the frozen race differs.
            if (seats.Length != _host.Seats)
            {
                error = $"the race has {_host.Seats} human cars but {seats.Length} humans are connected";
                return false;
            }
            if (_host.Runner.Session.Setup.NumOpponentCars != bots)
            {
                error = $"the race has {_host.Runner.Session.Setup.NumOpponentCars} AI opponents but {bots} were planned";
                return false;
            }
            if (_host.Runner.Session.TrackNumber != _nextRaceTrack)
            {
                error = $"the race is on track TR{_host.Runner.Session.TrackNumber:00} but TR{_nextRaceTrack:00} " +
                        $"(menu track {_nextTrack}) was selected";
                return false;
            }
            if (_host.Runner.Session.Backward != _nextBackward)
            {
                error = $"the race is {( _host.Runner.Session.Backward ? "backward" : "forward")} but " +
                        $"{(_nextBackward ? "backward" : "forward")} was selected";
                return false;
            }
            error = "the race was composed with other car models than the connected players selected";
            return false;
        }
        StartRound(seats, _now);
        error = "";
        return true;
    }

    /// <summary>Data track number (TRnn directory) a menu track index composes; the race setup stores that number.</summary>
    static int RaceTrackNumber(int menuTrack) => FrontEndConstants.TrackMap[menuTrack is >= 0 and <= 7 ? menuTrack : 0];

    /// <summary>Menu track index of a data track number (the initial host's setup); a track no menu entry maps to keeps its
    /// number (the host still matches itself through <see cref="_nextRaceTrack"/>).</summary>
    static int MenuTrackFor(int raceTrack)
    {
        var map = FrontEndConstants.TrackMap;
        for (int i = 0; i < map.Length; i++) if (map[i] == raceTrack) return i;
        return raceTrack;
    }

    /// <summary>True when the current host already is the race this start needs: the selected track, the same human seats, the
    /// planned number of real AI racing cars and the car models the connected players selected in the menu (a seat without an
    /// explicit selection accepts the frozen model). Reusing it keeps the setup block and its replay byte-identical.</summary>
    bool HostMatches(int humans, int bots)
    {
        var setup = _host.Runner.Session.Setup;
        if (_host.Runner.Session.TrackNumber != _nextRaceTrack) return false;
        if (_host.Runner.Session.Backward != _nextBackward) return false;
        if (_host.Seats != humans || setup.NumOpponentCars != bots || setup.NumCars != humans + bots) return false;
        foreach (var seat in _roundSeats)
        {
            if (seat.Conn == null || seat.CarId < 0 || !NetProtocol.IsCarNumber(seat.CarType)) continue;
            if (setup.Car(seat.CarId).CarType != seat.CarType) return false;
        }
        return true;
    }

    /// <summary>Bots for <paramref name="humans"/> connected humans: auto fills the grid, an explicit setting is capped.</summary>
    int EffectiveBotsFor(int humans)
    {
        // A fixed legacy host may intentionally have zero opponents (--opponents 0). Its lineup is authoritative;
        // only the bot-aware interactive factory uses the new operator selection.
        if (_hostFactoryBots == null && humans == _host.Seats)
            return _host.Runner.Session.Setup.NumOpponentCars;
        int cap = ServerRaceConfig.MaxRacers - Math.Clamp(humans, ServerRaceConfig.MinPlayers, ServerRaceConfig.MaxRacers);
        return _botSetting == ServerRaceConfig.AutoBots ? cap : Math.Min(_botSetting, cap);
    }

    /// <summary>Operator command `bots N` (0..7) or `bots auto`: selects the bot count of the next start. Refused while a
    /// race is running or its start is being composed. Between rounds it applies to the next round; the current result and
    /// the running race are never touched.</summary>
    public bool TrySetBotSetting(int setting, out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (!TryBotChangeAllowed(out error)) return false;
        if (setting is < ServerRaceConfig.AutoBots or > ServerRaceConfig.MaxRacers - 1)
        {
            error = $"bots must be {ServerRaceConfig.AutoBots} (auto) or 0..{ServerRaceConfig.MaxRacers - 1}";
            return false;
        }
        _botSetting = setting;
        Log($"bots: {(setting == ServerRaceConfig.AutoBots ? "auto (fill the grid)" : $"{setting} requested")}, " +
            $"{PlannedBots} planned for {ConnectedPlayers} humans (cap {BotCap})");
        error = "";
        return true;
    }

    /// <summary>Operator command `bot +`: one more AI car relative to the current plan, up to <see cref="BotCap"/>.</summary>
    public bool TryAddBot(out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (!TryBotChangeAllowed(out error)) return false;
        int planned = PlannedBots;
        if (planned >= BotCap)
        {
            error = _botSetting == ServerRaceConfig.AutoBots
                ? $"auto already fills the grid ({BotCap} bots for {ConnectedPlayers} humans)"
                : $"grid full for {ConnectedPlayers} humans (cap {BotCap})";
            return false;
        }
        _botSetting = Math.Min(ServerRaceConfig.MaxRacers - 1, planned + 1);
        Log($"bots: +1 -> {_botSetting} requested, {PlannedBots} planned for {ConnectedPlayers} humans (cap {BotCap})");
        error = "";
        return true;
    }

    /// <summary>Operator command `bot -`: one fewer AI car relative to the current plan (never below zero).</summary>
    public bool TryRemoveBot(out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (!TryBotChangeAllowed(out error)) return false;
        int planned = PlannedBots;
        if (planned <= 0)
        {
            error = "no bots to remove";
            return false;
        }
        _botSetting = planned - 1;
        Log($"bots: -1 -> {_botSetting} requested, {PlannedBots} planned for {ConnectedPlayers} humans (cap {BotCap})");
        error = "";
        return true;
    }

    /// <summary>Operator command `track N` (0..7, menu track index – the same number as the configuration dialog and
    /// <see cref="ServerRaceConfig.Track"/>): selects the track of the next composed round. The lobby host is only a
    /// placeholder (the clients load it while they wait); `start` or `next` composes a fresh host on the selected track and
    /// sends the new welcome/baseline, which makes the clients load the other race data. Refused while a race runs or its
    /// start is being composed. Between rounds it applies to the next round; the current result is never touched.</summary>
    public bool TrySetTrack(int track, out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (!TryLobbyChangeAllowed("track", out error)) return false;
        if (track is < 0 or > 7)
        {
            error = "track must be in 0..7";
            return false;
        }
        if (!SupportsTrackChange)
        {
            error = "this server cannot compose a host on another track";
            return false;
        }
        _nextTrack = track;
        _nextRaceTrack = RaceTrackNumber(track);
        Log($"track: menu track {track} (TR{_nextRaceTrack:00}) selected for the next start");
        error = "";
        return true;
    }

    /// <summary>Operator command `direction forward|backward`: selects the direction of the next composed round. Like the
    /// track, the lobby host is only a placeholder; `start` or `next` composes a fresh host in the selected direction and
    /// sends the new welcome/baseline. Refused while a race runs or its start is being composed; between rounds it applies
    /// to the next round and never touches the current result.</summary>
    public bool TrySetBackward(bool backward, out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (!TryLobbyChangeAllowed("direction", out error)) return false;
        if (!SupportsDirectionChange)
        {
            error = "this server cannot compose a host with a changed direction";
            return false;
        }
        _nextBackward = backward;
        Log($"direction: {(backward ? "backward" : "forward")} selected for the next start");
        error = "";
        return true;
    }

    /// <summary>Bot changes are a lobby operation: allowed before a start and between rounds, never during or while a
    /// composed start waits for its clients.</summary>
    bool TryBotChangeAllowed(out string error)
    {
        if (!TryLobbyChangeAllowed("bots", out error)) return false;
        if (!SupportsBotCounts)
        {
            error = "this server cannot compose a host with a changed bot count";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Lobby options (bots, track) are chosen before a start and between rounds, never during a race or while a
    /// composed start waits for its clients.</summary>
    bool TryLobbyChangeAllowed(string what, out string error)
    {
        if (_stopping || Phase == NetRacePhase.Closed) { error = "server is stopping"; return false; }
        if (_startPending) { error = "a race start is being prepared"; return false; }
        if (Phase is NetRacePhase.Countdown or NetRacePhase.Racing)
        {
            error = $"cannot change the {what} while the race is {Phase}";
            return false;
        }
        if (Phase is not (NetRacePhase.WaitingForPlayers or NetRacePhase.Loading or NetRacePhase.Finished or NetRacePhase.Aborted))
        {
            error = $"cannot change the {what} in phase {Phase}";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Operator command `next` after the result: creates a fresh lobby host of the configured capacity on a new race
    /// id, keeps the connections, clears every round-owned server state, issues new epochs and sends the new welcome +
    /// baseline + roster reliable to every connected client. The old round's packets then fail the session check.
    /// Never starts the race itself: the operator still has to issue `start`, which composes the actual human count.</summary>
    public bool TryOperatorNext(out string error)
    {
        _now = Clock?.Invoke() ?? _now;
        if (_stopping) { error = "server is stopping"; return false; }
        if (!SupportsNextRound)
        {
            error = "consecutive races are not enabled for this server";
            return false;
        }
        if (Phase is not (NetRacePhase.Finished or NetRacePhase.Aborted))
        {
            error = Phase is NetRacePhase.Closed ? "server closed" : $"previous race not finished (phase {Phase})";
            return false;
        }
        int oldRaceId = RaceId;
        if (!TryCreateRound(_seats.Length, null, null, "next race", out var next, out var round, out error)) return false;
        AdoptRound(next, round);
        for (int i = 0; i < _seats.Length; i++)
        {
            _seats[i].RaceSeat = i;
            _seats[i].CarId = next.View.Description.LocalSeats[i];
            _seats[i].Ready = false;
            _seats[i].Dnf = false;
            _seats[i].Inputs = new SeatInputScheduler();
        }
        ReissueEpochs();
        _operatorStartRequired = true; // readiness alone never starts a prepared round
        BeginLobbyRound();
        Log($"next round: race {oldRaceId:X8} -> {RaceId:X8}, {_connections.Count} connections kept, waiting for ready");
        error = "";
        return true;
    }

    /// <summary>Creates a host with an exact number of human cars on a fresh race id and prepares its round view. When
    /// <paramref name="bots"/> is given, the host must be exactly that grid (humans + real AI opponents); explicit entries
    /// of <paramref name="carTypes"/> (indexed by human car) must be the actual models of the human records. Everything is
    /// validated before the live host is touched, so a failed factory leaves the current round (and its result) intact.</summary>
    bool TryCreateRound(int humans, int? bots, IReadOnlyList<int>? carTypes, string what, out RaceHost next, out RoundView round,
        out string error)
    {
        next = null!;
        round = default;
        int raceId = NewRaceId();
        try { next = CreateHostFor(raceId, humans, bots, carTypes); }
        catch (Exception e)
        {
            error = $"cannot create the {what}: {e.Message}";
            Log($"{what} refused: {e.Message}");
            return false;
        }
        // Clients gate every packet on the race id; a factory host with any other id (for example a reused id of the
        // finished round) would desynchronise them, so the round is refused before the live state is touched.
        if (next.View.RaceId != raceId)
        {
            int actual = next.View.RaceId;
            next.Dispose();
            next = null!;
            error = $"{what} id {actual:X8} does not match the requested {raceId:X8}";
            Log($"{what} refused: {error}");
            return false;
        }
        var desc = next.View.Description;
        if (desc.LocalSeats.Count != humans)
        {
            int actual = desc.LocalSeats.Count;
            next.Dispose();
            next = null!;
            error = $"{what} has {actual} seats, expected {humans}";
            Log($"{what} refused: {error}");
            return false;
        }
        var roundSetup = next.Runner.Session.Setup;
        if (bots is { } expected)
        {
            if (roundSetup.NumOpponentCars != expected || roundSetup.NumCars != humans + expected)
            {
                int actualBots = roundSetup.NumOpponentCars;
                next.Dispose();
                next = null!;
                error = $"{what} has {actualBots} AI cars, expected {expected}";
                Log($"{what} refused: {error}");
                return false;
            }
        }
        if (carTypes != null)
        {
            if (carTypes.Count != humans)
            {
                next.Dispose();
                next = null!;
                error = $"{what} was composed for {carTypes.Count} car selections, expected {humans}";
                Log($"{what} refused: {error}");
                return false;
            }
            // A factory that ignores the selection must not hand the players a different car than the one in their menu.
            for (int i = 0; i < humans; i++)
            {
                int requested = carTypes[i];
                if (!NetProtocol.IsCarNumber(requested)) continue;
                int actualType = roundSetup.Car(desc.LocalSeats[i]).CarType;
                if (actualType != requested)
                {
                    next.Dispose();
                    next = null!;
                    error = $"{what} uses car model {actualType} for human {i}, expected {requested}";
                    Log($"{what} refused: {error}");
                    return false;
                }
            }
        }
        try { round = PrepareHost(next); }
        catch (Exception e)
        {
            next.Dispose();
            next = null!;
            error = $"cannot prepare the {what}: {e.Message}";
            Log($"{what} refused: {e.Message}");
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Factory call of a round: the track-aware factory when the server has one (it composes the whole round, track
    /// included), otherwise the car-type-aware factory when an explicit model was selected (it is the only one that can honour
    /// it), otherwise the bot-aware factory when the server has one, otherwise the legacy two-argument factory (which derives
    /// its own AI count from the human count). No explicit selection and no track factory keep the old code path, so legacy
    /// set-ups stay byte-identical.</summary>
    RaceHost CreateHostFor(int raceId, int humans, int? bots, IReadOnlyList<int>? carTypes)
    {
        int effectiveBots = bots ?? EffectiveBotsFor(humans);
        // A track-aware factory describes the whole composition (it also honours the car models and the direction), so every
        // round goes through it once the server has one. Without it the legacy factories below keep their frozen track and
        // direction byte-identically.
        if (_hostFactoryTrack != null) return _hostFactoryTrack(raceId, humans, effectiveBots, carTypes, _nextTrack, _nextBackward);
        if (carTypes != null && carTypes.Any(NetProtocol.IsCarNumber))
        {
            if (_hostFactoryTypes == null)
                throw new InvalidOperationException("this server cannot compose the car models selected by the players");
            return _hostFactoryTypes(raceId, humans, effectiveBots, carTypes);
        }
        return _hostFactoryBots != null ? _hostFactoryBots(raceId, humans, effectiveBots)
            : _hostFactory != null ? _hostFactory(raceId, humans)
            : throw new InvalidOperationException("this server has no host factory");
    }

    /// <summary>Swaps in the prepared host (field assignments only; cannot fail halfway) and forgets the finished round.</summary>
    void AdoptRound(RaceHost next, RoundView round)
    {
        var old = _host;
        old.View.Published -= OnPublished;
        old.Dispose();
        _host = next;
        RaceId = next.View.RaceId;
        _usedRaceIds.Add(RaceId);
        AdoptHost(round);
        next.View.Published += OnPublished;
        ResetRoundState();
    }

    /// <summary>Fresh epochs for every connection of the new round; clients accept only a new, never-seen id+epoch.</summary>
    void ReissueEpochs()
    {
        foreach (var c in _connections.Values)
        {
            c.Epoch = NewEpoch();
            c.ResultsSent = false;
            c.ResultsAck = false;
            c.Bad = 0;
            c.RateWindowStart = _now;
            c.RateCount = 0;
        }
    }

    /// <summary>Common tail of every prepared (not yet running) round: welcome + baseline + roster to all connections.</summary>
    void BeginLobbyRound()
    {
        _created = _now;
        _roundSeats = _seats;
        _startPending = false;
        _startAborted = false;
        Phase = _seats.All(s => s.Conn != null) ? NetRacePhase.Loading : NetRacePhase.WaitingForPlayers;
        foreach (var c in _connections.Values) SendRoundStart(c, Phase);
        BroadcastRoster();
    }

    /// <summary>
    /// Composes the actual race for <paramref name="humans"/> connected humans: a fresh host with exactly that many human
    /// cars and the remaining grid slots as real AI opponents, on a new race id. Seat order is compacted in lobby order, so
    /// a lobby with gaps (seats 0 and 2) becomes race seats 0 and 1. Sends the new welcome/baseline/roster reliable and waits
    /// for every client to report ready (the caller sets <see cref="_startPending"/>); nothing is stepped yet.
    /// </summary>
    bool PrepareDynamicRound(int humans, out string error)
    {
        var seats = ConnectedSeats();
        if (seats.Length != humans)
        {
            error = $"{seats.Length} connected humans, {humans} expected";
            return false;
        }
        int bots = EffectiveBotsFor(humans);
        // Indexed by human car (connected seats in lobby order), never by lobby slot.
        var carTypes = seats.Select(s => s.CarType).ToArray();
        if (!TryCreateRound(humans, bots, carTypes, "race", out var next, out var round, out error)) return false;
        AdoptRound(next, round);
        var localSeats = next.View.Description.LocalSeats;
        for (int i = 0; i < seats.Length; i++)
        {
            seats[i].RaceSeat = i;
            seats[i].CarId = localSeats[i];
        }
        var inRound = new HashSet<Seat>(seats, ReferenceEqualityComparer.Instance);
        foreach (var seat in _seats)
        {
            if (!inRound.Contains(seat)) { seat.RaceSeat = -1; seat.CarId = -1; }
            seat.Ready = false;
            seat.Dnf = false;
            seat.Inputs = new SeatInputScheduler();
        }
        ReissueEpochs();
        _roundSeats = seats;
        _created = _now;
        Phase = NetRacePhase.Loading;
        foreach (var c in _connections.Values) SendRoundStart(c, Phase);
        BroadcastRoster();
        Log($"round composed: race {RaceId:X8}, {humans} human cars, {bots} AI opponents");
        return true;
    }

    /// <summary>A human left while the composed round waited for its welcome: go back to the lobby on the configured capacity
    /// (new race id/epoch, fresh welcomes) and let the operator start again once enough humans are ready.</summary>
    void CancelPendingStart(string why)
    {
        _startPending = false;
        _startAborted = false;
        Log($"start cancelled: {why}");
        if (!SupportsNextRound) { _lobbyBroken = true; return; }
        if (!TryCreateRound(_seats.Length, null, null, "lobby race", out var next, out var round, out string error))
        {
            // Keep the composed host, but a lobby with a different seat count cannot hand out valid welcomes.
            _lobbyBroken = true;
            Log($"lobby restore failed: {error}");
            return;
        }
        AdoptRound(next, round);
        for (int i = 0; i < _seats.Length; i++)
        {
            _seats[i].RaceSeat = i;
            _seats[i].CarId = next.View.Description.LocalSeats[i];
            _seats[i].Ready = false;
            _seats[i].Dnf = false;
            _seats[i].Inputs = new SeatInputScheduler();
        }
        ReissueEpochs();
        _operatorStartRequired = true;
        BeginLobbyRound();
    }

    /// <summary>Candidate id of the next round; it is reserved only once the factory host has been accepted, so a failed
    /// `next` does not burn an id.</summary>
    int NewRaceId()
    {
        int id = _random.Next(1, int.MaxValue);
        while (_usedRaceIds.Contains(id)) id = id == int.MaxValue ? 1 : id + 1;
        return id;
    }

    /// <summary>Forgets everything that belongs to the finished round and is not derived from the new host.</summary>
    void ResetRoundState()
    {
        _t1 = -1;
        _finishedAt = -1;
        _firstFinishStep = -1;
        _forcedLast = [];
        _durableSequence = 0;
        _sentSkids = null;
        _results = null;
        _knockedSent.Clear();
        _stepEvents.Clear();
        PublishedEvents?.Clear();
    }

    /// <summary>
    /// Begins the countdown of the current host for exactly <paramref name="seats"/> human cars (in race-seat order). The host
    /// was either created for this human count (dynamic composition) or already has that many human cars (exact-count race),
    /// so no car changes class here: empty seats do not exist in the current race.
    /// </summary>
    void StartRound(Seat[] seats, long now)
    {
        if (seats.Length != _host.Seats)
        {
            // Unreachable through the public commands (both validate first); refuse instead of desynchronising clients.
            Log($"start refused: host has {_host.Seats} human cars, {seats.Length} humans seated");
            return;
        }
        _operatorStartRequired = false;
        _startPending = false;
        _roundSeats = seats;
        Phase = NetRacePhase.Countdown;
        _t1 = now + NetProtocol.StartLeadMicros;
        foreach (var c in _connections.Values)
            _transport.Send(c.Id, NetChannel.ReliableOrdered, new NetStart(RaceId, c.Epoch, 1, NetProtocol.StartLeadMicros).Encode());
        Log($"start: {_connections.Values.Count(c => !c.Seat.Spectator)} humans, {_host.Runner.Session.Setup.NumOpponentCars} AI opponents, " +
            $"first step in {NetProtocol.StartLeadMicros / 1000} ms");
    }

    void RunDueSteps(long now)
    {
        if (now < _t1) return;
        long due = (now - _t1) / NetProtocol.StepMicros + 1;
        long backlog = due - _host.Steps;
        if (backlog > NetProtocol.MaxCatchUpSteps)
        {
            long slip = backlog - NetProtocol.MaxCatchUpSteps;
            _t1 += slip * NetProtocol.StepMicros;
            Stats.ClockSlipSteps += slip;
            backlog = NetProtocol.MaxCatchUpSteps;
        }
        for (long i = 0; i < backlog && Phase is NetRacePhase.Countdown or NetRacePhase.Racing; i++) StepOnce();
    }

    void StepOnce()
    {
        long step = _host.Steps + 1;
        var seats = _roundSeats;
        if (seats.Length <= 2)
        {
            // Legacy one/two-human scheduler (exact wire and replay behaviour of the two-seat server).
            var (a, seqA) = seats[0].Dnf || seats[0].Conn == null ? (default(PlayerInput), 0L) : seats[0].Inputs.Next(step);
            PlayerInput? b = null;
            long seqB = 0;
            if (seats.Length > 1)
            {
                (var bi, seqB) = seats[1].Dnf || seats[1].Conn == null ? (default(PlayerInput), 0L) : seats[1].Inputs.Next(step);
                b = bi;
            }
            _host.StepScheduled(a, seqA, b, seqB);
        }
        else
        {
            // One sample per human seat (3..8); DNF / disconnected seats drive neutral, AI opponents keep their drivers.
            var inputs = new PlayerInput[seats.Length];
            var sequences = new long[seats.Length];
            for (int i = 0; i < seats.Length; i++)
                if (!seats[i].Dnf && seats[i].Conn != null)
                    (inputs[i], sequences[i]) = seats[i].Inputs.Next(step);
            _host.StepScheduled(inputs, sequences);
        }
        Stats.Steps++;
        if (Phase == NetRacePhase.Countdown && _host.Runner.RaceStarted) Phase = NetRacePhase.Racing;
        Publish(_host.View.Latest);
        if (_host.Results != null) { Finish(NetFinishReason.Finished); return; }
        if (_options.MaxRaceSteps > 0 && _host.Steps >= _options.MaxRaceSteps) { Finish(NetFinishReason.TimeLimit); return; }
        // A full lobby keeps the host's own timing (as in the original). With a human gone (DNF or a [ref:2430]
        // seat whose connection dropped) the host would still wait for every local-human car, so a dedicated race ends when
        // all CONNECTED humans have finished: disconnected/DNF seats must not keep the result waiting.
        if (_roundSeats.Any(s => s.Conn == null || s.Dnf || s.Away) && AllConnectedHumansFinished())
        {
            Finish(NetFinishReason.RemainingHumansFinished);
            return;
        }
        if (_options.StragglerLimitMicros > 0) CheckStragglers();
    }

    /// <summary>The first HUMAN finisher starts the straggler countdown (a bot ahead must not cut a lone slow player short; the limit
    /// only stops people waiting for people); when it runs out the round ends and the rest is DNF.</summary>
    void CheckStragglers()
    {
        if (_firstFinishStep < 0)
        {
            var cars = _host.Runner.Cars;
            if (!_roundSeats.Any(s => s.CarId >= 0 && s.CarId < cars.Count && cars[s.CarId].Obj.RaceState == 2)) return;
            _firstFinishStep = _host.Steps;
            Log($"first finisher at step {_host.Steps}: {_options.StragglerLimitMicros / 1_000_000} s for the others");
            return;
        }
        if ((_host.Steps - _firstFinishStep) * NetProtocol.StepMicros >= _options.StragglerLimitMicros)
            Finish(NetFinishReason.StragglerLimit);
    }

    /// <summary>True when every connected human has finished (DNF slots do not block the end). [ref:2431]</summary>
    bool AllConnectedHumansFinished()
    {
        var live = _roundSeats.Where(s => s.Conn != null && !s.Dnf && !s.Away).ToArray();
        // Everybody still in the round is away: wait for the end of the grace window (the owner then removes them).
        if (live.Length == 0) return !_roundSeats.Any(s => s.Conn != null && !s.Dnf && s.Away);
        var frame = _host.View.Latest;
        return live.All(s => frame.Car(s.CarId) is { RaceState: 2 } c && unchecked(c.LastProgressTick + RaceProgress.FinishDelay) < frame.SimulationTick64);
    }

    void Finish(NetFinishReason reason)
    {
        if (_results != null) return;
        bool natural = _host.Results != null;
        // Read before the abort: it marks the unfinished cars (and all humans) as aborted.
        int[] finishedCars = _host.Runner.Cars.Select((c, i) => (c, i)).Where(x => (x.c.Obj.Flags & CarFlags.Racer) != 0 && x.c.Obj.RaceState == 2)
            .Select(x => x.i).ToArray();
        var results = _host.Results ?? _host.Abort(0);
        int dnf = 0;
        foreach (var s in _roundSeats) if ((s.Dnf || s.Away) && s.RaceSeat >= 0) dnf |= 1 << s.RaceSeat;
        if (!natural)
        {
            // Unfinished cars are DNF: behind the finishers by progress, humans that are gone last of all (the host's own
            // order of an aborted race is the order of the moment, unfinished humans of a left seat may still be ahead).
            var gone = _roundSeats.Where(s => s.Conn == null || s.Dnf || s.Away).Select(s => s.CarId).Where(id => id >= 0).ToArray();
            results = RoundClassification.Classify(results, _host.Runner, finishedCars, gone);
            _host.OverrideResults(results);
            _forcedLast = results.Racers.Where(r => gone.Contains(r.CarIndex) && !finishedCars.Contains(r.CarIndex)).Select(r => r.CarIndex).ToArray();
        }
        _results = new NetResults(RaceId, 0, reason, _host.Steps, _host.Runner.Tick, dnf, results);
        Phase = reason is NetFinishReason.Finished or NetFinishReason.StragglerLimit ? NetRacePhase.Finished : NetRacePhase.Aborted;
        _finishedAt = _now;
        // the final snapshot carries HasResults; then the result itself (reliable, once per connection)
        Publish(_host.Resync());
        foreach (var c in _connections.Values) SendResults(c);
        Log($"race over: {reason} at step {_host.Steps}, tick {_host.Runner.Tick}, dnf seats {dnf}");
    }

    void SendResults(Connection c)
    {
        if (c.ResultsSent || _results == null) return;
        c.ResultsSent = true;
        _transport.Send(c.Id, NetChannel.ReliableOrdered, (_results with { Epoch = c.Epoch }).Encode());
    }

    /// <summary>
    /// Starts a graceful close. A race still running gets its <see cref="NetFinishReason.ServerStop"/> result first; both the
    /// result and any other queued reliable message are then drained (bounded by <see cref="RaceServerOptions.ShutdownDrainMicros"/>)
    /// by <see cref="Update"/> before <see cref="CompleteStop"/> disconnects. New joins are refused and physics stops at once.
    /// </summary>
    void Close(NetRejectReason reason, string why)
    {
        if (Phase == NetRacePhase.Closed || _stopping) return;
        if (_results == null && Phase is NetRacePhase.Countdown or NetRacePhase.Racing) Finish(NetFinishReason.ServerStop);
        _stopping = true;
        _stopReason = reason;
        _stopWhy = why;
        _stopDeadline = _now + Math.Max(0, _options.ShutdownDrainMicros);
        Log($"closing: {why} (draining up to {Math.Max(0, _options.ShutdownDrainMicros) / 1000} ms)");
    }

    bool ReliableDrained()
    {
        foreach (var c in _connections.Values)
        {
            if (_transport.PendingReliable(c.Id) > 0) return false;
            // The transport queue empties as soon as a reliable packet leaves the socket (it does not count unacknowledged
            // in-flight packets), so an active race additionally waits for the client's own result acknowledgement.
            if (_results != null && c.ResultsSent && !c.ResultsAck) return false;
        }
        return true;
    }

    /// <summary>Ends the drain: every connection is closed with the requested reason. The host is released by the caller.</summary>
    void CompleteStop()
    {
        if (Phase == NetRacePhase.Closed) return;
        _stopping = false;
        Phase = NetRacePhase.Closed;
        foreach (var c in _connections.Values.ToArray())
        {
            _connections.Remove(c.Id);
            c.Seat.Conn = null;
            c.Seat.Inputs.Clear();
            _transport.Disconnect(c.Id, new NetReject(_stopReason).Encode());
        }
        Log($"closed: {_stopWhy}");
    }

    /// <summary>Stops the server now (process shutdown): ServerStop result if a race was running, a bounded drain of the
    /// reliable output, then every connection is closed. <see cref="Update"/> completes the close.</summary>
    public void Shutdown() => Close(NetRejectReason.ServerShutdown, "shutdown");

    /// <summary>Releases the current host (and its <see cref="RaceViewPublisher.Published"/> subscription).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.View.Published -= OnPublished;
        _host.Dispose();
    }

    // ---------------- publication ----------------

    NetSnapshotInfo Info(RaceViewFrame f, NetRacePhase phase)
    {
        int connected = 0;
        foreach (var s in _roundSeats) if (s.Conn != null && s.RaceSeat >= 0) connected |= 1 << s.RaceSeat;
        return new NetSnapshotInfo(f.SnapshotSequence, f.Kind, f.SimulationTick64, f.Steps, phase, f.RaceStarted, f.Results != null,
            _durableSequence, f.NumLaps, f.RacerCount, f.Ranking, connected);
    }

    void Publish(RaceViewFrame frame)
    {
        PublishDurable(frame);
        if (_stepEvents.Count > 0)
        {
            foreach (var c in _connections.Values)
                foreach (var batch in NetEvents.Batches(RaceId, c.Epoch, _stepEvents))
                {
                    _transport.Send(c.Id, NetChannel.ReliableUnordered, batch.Encode());
                    Stats.EventMessages++;
                }
            _stepEvents.Clear();
        }
        for (int i = 0; i < frame.Parked.Count; i++)
            if (!_parkedDirty[i] && !EncodeParked(frame.Parked[i]).AsSpan().SequenceEqual(_baselineParked[i])) _parkedDirty[i] = true;
        var cars = SnapshotCodec.EncodeCars(frame, i => _parkedDirty[i]);
        var info = Info(frame, Phase);
        foreach (var c in _connections.Values.ToArray())
        {
            var s = c.Seat.Inputs;
            var ack = new NetSeatAck(s.AppliedSequence, s.LastActionId, s.Late, s.Future, s.HeldSteps, s.NeutralSteps);
            var parts = SnapshotCodec.EncodeParts(RaceId, c.Epoch, info, ack, cars);
            Stats.MaxPartsPerSnapshot = Math.Max(Stats.MaxPartsPerSnapshot, parts.Count);
            foreach (var p in parts)
            {
                _transport.Send(c.Id, NetChannel.Unreliable, p);
                Stats.SnapshotParts++;
                Stats.SnapshotBytes += p.Length;
                Stats.MaxSnapshotPart = Math.Max(Stats.MaxSnapshotPart, p.Length);
            }
            int backlog = _transport.PendingReliable(c.Id);
            Stats.MaxReliableBacklog = Math.Max(Stats.MaxReliableBacklog, backlog);
            if (backlog > _options.MaxReliableBacklog)
            {
                Stats.SlowClientDrops++;
                Drop(c, NetRejectReason.SlowClient);
            }
        }
    }

    /// <summary>Knocked objects added and skid segments changed since the previous message, split into datagram-sized messages.</summary>
    void PublishDurable(RaceViewFrame frame)
    {
        // the knocked set is add-only: send the ids not sent yet [ref:2432]
        var knocked = frame.Knocked.Count == _knockedSent.Count ? []
            : frame.Knocked.Where(k => !_knockedSent.Contains(k.Key)).OrderBy(k => k.Key).ToList();
        foreach (var k in knocked) _knockedSent.Add(k.Key);
        NetSkidDelta? skids = null;
        if (!ReferenceEquals(frame.Skids, _sentSkids))
        {
            skids = NetSkidDelta.Diff(_sentSkids, frame.Skids);
            _sentSkids = frame.Skids;
        }
        if (knocked.Count == 0 && skids == null) return;
        foreach (var msg in SplitDurable(frame.SnapshotSequence, knocked, skids))
        {
            foreach (var c in _connections.Values)
            {
                var bytes = (msg with { Epoch = c.Epoch }).Encode();
                _transport.Send(c.Id, NetChannel.ReliableOrdered, bytes);
                Stats.DurableMessages++;
                Stats.DurableBytes += bytes.Length;
                Stats.MaxDurableMessage = Math.Max(Stats.MaxDurableMessage, bytes.Length);
            }
        }
    }

    readonly HashSet<ushort> _knockedSent = [];

    /// <summary>
    /// Sends the complete knocked/skid state to every connection, one durable message per datagram (the same split as the
    /// incremental stream, so the budget holds). The messages extend the global durable sequence, which keeps it contiguous
    /// for the peers that already mirror it; applying the same values again is idempotent. The sequence is tied to the
    /// baseline (<c>SnapshotSequence = 0</c>) sent next, because that is the frame the resumed client starts from.
    /// </summary>
    void PublishResumeDurable(Connection resumed)
    {
        var frame = _host.View.Latest;
        var knocked = frame.Knocked.OrderBy(k => k.Key).ToList();
        var skids = NetSkidDelta.Diff(null, frame.Skids);
        if (knocked.Count == 0 && skids == null)
        {
            // No durable state exists. A resumed client still needs the history entry its baseline refers to; a peer that
            // already knows this sequence ignores the duplicate.
            if (_durableSequence > 0)
                _transport.Send(resumed.Id, NetChannel.ReliableOrdered,
                    new NetDurable(RaceId, resumed.Epoch, _durableSequence, 0, [], null).Encode());
            return;
        }
        foreach (var k in knocked) _knockedSent.Add(k.Key);
        _sentSkids = frame.Skids;
        foreach (var msg in SplitDurable(0, knocked, skids))
        {
            foreach (var c in _connections.Values)
            {
                var bytes = (msg with { Epoch = c.Epoch }).Encode();
                _transport.Send(c.Id, NetChannel.ReliableOrdered, bytes);
                Stats.DurableMessages++;
                Stats.DurableBytes += bytes.Length;
                Stats.MaxDurableMessage = Math.Max(Stats.MaxDurableMessage, bytes.Length);
            }
        }
    }

    IEnumerable<NetDurable> SplitDurable(long snapshot, List<KeyValuePair<ushort, KnockedObject>> knocked, NetSkidDelta? skids)
    {
        const int Budget = NetProtocol.MaxDatagram - NetHeader.Size - 32;
        var ops = skids?.Ops ?? [];
        int k = 0, o = 0;
        do
        {
            int used = 0;
            var kPart = new List<KeyValuePair<ushort, KnockedObject>>();
            while (k < knocked.Count && used + 16 <= Budget) { kPart.Add(knocked[k++]); used += 16; }
            var oPart = new List<NetSkidDelta.Op>();
            while (o < ops.Count)
            {
                int size = 3 + 15 + ops[o].Vertices.Length * 3 + ops[o].Texture.Length;
                if (used + size > Budget && oPart.Count + kPart.Count > 0) break;
                oPart.Add(ops[o++]);
                used += size;
            }
            var delta = skids == null ? null : skids with { Ops = oPart };
            yield return new NetDurable(RaceId, 0, ++_durableSequence, snapshot, kPart, delta);
        } while (k < knocked.Count || o < ops.Count);
    }
}
