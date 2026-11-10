using System.Diagnostics;
using System.Globalization;
using System.Net;
using NFS.Core;
using NFS.Game.Race;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;

namespace NFS.Net.Client;

/// <summary>Command line of <c>net-client</c> of the CLI tools: what the headless client does in the session server (list the games,
/// create or join one, choose a car, report ready, start as the operator, drive one round, print the results).</summary>
public sealed record HeadlessOptions
{
    public string? Server { get; init; }
    public string? PortFile { get; init; }
    public string Name { get; init; } = "headless";
    /// <summary>Data mods sent in the hello; null = the mods of <see cref="NFS.Game.Mods.ModSession.Current"/>.</summary>
    public NetModSet? Mods { get; init; }
    public int Seconds { get; init; } = 60;
    public string? LogPath { get; init; }
    /// <summary>Only connect, print the games list and stop.</summary>
    public bool List { get; init; }
    /// <summary>Creates a game with this name (the client becomes its operator).</summary>
    public string? Create { get; init; }
    public int MaxPlayers { get; init; } = 2;
    public int Track { get; init; }
    public int Laps { get; init; } = 1;
    public int Bots { get; init; } = NetProtocol.AutoBots;
    public bool Backward { get; init; }
    public bool Traffic { get; init; }
    /// <summary>Joins the game with this name (waits for it to appear).</summary>
    public string? Join { get; init; }
    /// <summary>Joins the first game in the lobby phase with a free seat (waits for one to appear).</summary>
    public bool JoinFirst { get; init; }
    public int? JoinId { get; init; }
    public int? Car { get; init; }
    /// <summary>Car selected by key (<c>author.mod/car</c>), e.g. a mod car; wins over <see cref="Car"/>.</summary>
    public NFS.ModApi.CarKey? CarKey { get; init; }
    /// <summary>Does not report ready (the operator then cannot start a race that needs everyone ready).</summary>
    public bool NoReady { get; init; }
    /// <summary>Operator: starts when at least this many members are present and ready (default: the game's player limit).</summary>
    public int? StartWhen { get; init; }
    /// <summary>Creates a knockout series (<c>--mode ko</c>) instead of a single race: the operator starts every round, the client
    /// drives while it is in the series and watches (as a spectator) after its elimination, and stops after
    /// <see cref="KoRounds"/> completed rounds or at the end of the series.</summary>
    public bool Knockout { get; init; }
    public int KoRounds { get; init; } = 2;
    public int ResetAtStep { get; init; }
    public int LeaveAtStep { get; init; }
    public int Steer { get; init; }
    /// <summary>One chat line sent once, when every member of the game is in the lobby (the received lines are always logged).</summary>
    public string? Chat { get; init; }
    /// <summary>Accepts the download of the server's mods that this client lacks (5.4). Without it the client only reports what would be downloaded and stops.</summary>
    public bool AcceptModDownloads { get; init; }

    public HeadlessScript Script => new(ResetAtStep, LeaveAtStep, (sbyte)Steer);

    public static readonly string Usage =
        "net-client (--server IP:PORT | --port-file FILE) [--game DIR] [--name N] [--seconds 60] [--log FILE] [--mods DIR]\n" +
        "  action (one of; default --list):\n" +
        "    --list                       connect, print the games list, exit 0\n" +
        "    --create NAME                create a game and become its operator\n" +
        "        [--max-players 2] [--track 0..7] [--laps 1..8] [--bots N|auto] [--backward] [--traffic]\n" +
        "        [--mode single|ko]            single race (default) or a knockout series (nine entrants, one eliminated per round)\n" +
        "        [--ko-rounds 1..8]            knockout: stop after this many completed rounds (default 2) or at the end of the series\n" +
        "    --join NAME | --join-first | --join-id N     join a game (waits until it is listed and has a free seat)\n" +
        "  in the game: [--car 0..11 | --car-key author.mod/car] [--no-ready] [--start-when N]   (operator starts when N members are present and ready;\n" +
        "               default N = the game's player limit)\n" +
        "  driving:     [--steer -128..127] [--reset-at STEP] [--leave-at STEP]   (full throttle after the green light)\n" +
        "  mods:        [--accept-mod-downloads]   mods the server has and this client lacks (or has in another version) are downloaded to\n" +
        "               mods/<id>/<version>/ and the session uses the server's versions; without the flag the client prints what would be\n" +
        "               downloaded and exits with 3\n" +
        "  chat:        [--chat TEXT]   one chat line, sent once all players are in the lobby; received lines are logged as \"chat SENDER: TEXT\"\n" +
        "  knockout:    an eliminated client watches the following rounds as a spectator (race stream, no controls) and logs\n" +
        "               \"spectator: watched race ID: N snapshots up to step S, result DIGEST\"; drivers log the same with \"driver: drove\"\n" +
        "  exit code: 0 = results received (or games listed), 1 = bad arguments, 2 = no result in time or left early,\n" +
        "             3 = refused by the server (handshake, create or join)";

    public static bool TryParse(IReadOnlyList<string> args, out HeadlessOptions options, out string error)
    {
        options = new HeadlessOptions();
        error = "";
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "list", "join-first", "backward", "traffic", "no-ready", "accept-mod-downloads" };
        var valued = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "server", "port-file", "name", "seconds", "log", "create", "max-players", "track", "laps", "bots", "join", "join-id",
            "car", "car-key", "start-when", "reset-at", "leave-at", "steer", "chat", "mode", "ko-rounds",
        };
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2) { error = $"unexpected argument '{a}'"; return false; }
            string key = a[2..];
            if (switches.Contains(key)) { values[key] = null; continue; }
            if (!valued.Contains(key)) { error = $"unknown flag --{key}"; return false; }
            if (i + 1 >= args.Count) { error = $"flag --{key} needs a value"; return false; }
            values[key] = args[++i];
        }
        string problem = "";
        bool Int(string key, int min, int max, int fallback, out int result)
        {
            result = fallback;
            if (!values.TryGetValue(key, out var text) || text == null) return true;
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result) && result >= min && result <= max) return true;
            problem = $"--{key} must be an integer in {min}..{max}";
            return false;
        }
        if (!Int("seconds", 1, 600, 60, out int seconds) || !Int("max-players", NetProtocol.MinSeats, NetProtocol.MaxSeats, 2, out int maxPlayers) ||
            !Int("track", NetProtocol.MinTrack, NetProtocol.MaxTrack, 0, out int track) || !Int("laps", NetProtocol.MinLaps, NetProtocol.MaxLaps, 1, out int laps) ||
            !Int("join-id", 1, int.MaxValue, 0, out int joinId) || !Int("car", 0, NetProtocol.MaxCarType, -1, out int car) ||
            !Int("start-when", 1, NetProtocol.MaxSeats, 0, out int startWhen) || !Int("reset-at", 0, int.MaxValue, 0, out int resetAt) ||
            !Int("leave-at", 0, int.MaxValue, 0, out int leaveAt) || !Int("steer", -128, 127, 0, out int steer) ||
            !Int("ko-rounds", 1, NetProtocol.KnockoutRounds, 2, out int koRounds)) { error = problem; return false; }
        NFS.ModApi.CarKey? carKey = null;
        if (values.TryGetValue("car-key", out var carKeyText) && carKeyText != null)
        {
            if (!NFS.ModApi.CarKey.TryParse(carKeyText, out var parsedKey)) { error = "--car-key must look like author.mod/car"; return false; }
            carKey = parsedKey;
        }
        string modeText = (values.TryGetValue("mode", out var rawMode) ? rawMode : null) ?? "single";
        bool knockout;
        if (string.Equals(modeText, "ko", StringComparison.OrdinalIgnoreCase) || string.Equals(modeText, "knockout", StringComparison.OrdinalIgnoreCase)) knockout = true;
        else if (string.Equals(modeText, "single", StringComparison.OrdinalIgnoreCase)) knockout = false;
        else { error = "--mode must be 'single' or 'ko'"; return false; }
        int bots = NetProtocol.AutoBots;
        if (values.TryGetValue("bots", out var botText) && botText != null && !string.Equals(botText, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(botText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out bots) || !NetProtocol.IsBotSetting(bots))
            { error = $"--bots must be 'auto' or an integer in 0..{NetProtocol.MaxSeats - 1}"; return false; }
        }
        string? Text(string key) => values.TryGetValue(key, out var v) ? v : null;
        string name = Text("name") ?? "headless";
        if (name.Length is < 1 or > NetProtocol.MaxNameLength || name.Any(c => c is < ' ' or >= (char)0x7F))
        { error = $"--name must be 1..{NetProtocol.MaxNameLength} printable ASCII characters"; return false; }
        string? chat = Text("chat") is { } rawChat ? NetProtocol.SanitizeChat(rawChat) : null;
        if (chat is { Length: 0 }) { error = "--chat needs printable ASCII text"; return false; }
        string? create = Text("create");
        if (create != null && !NetProtocol.IsGameName(create))
        { error = $"--create name must be 1..{NetProtocol.MaxGameNameLength} printable ASCII characters"; return false; }
        string? join = Text("join");
        bool list = values.ContainsKey("list"), joinFirst = values.ContainsKey("join-first");
        int actions = (list ? 1 : 0) + (create != null ? 1 : 0) + (join != null ? 1 : 0) + (joinFirst ? 1 : 0) + (joinId > 0 ? 1 : 0);
        if (actions > 1) { error = "choose one of --list, --create, --join, --join-first, --join-id"; return false; }
        if (actions == 0) list = true;
        if (Text("server") == null && Text("port-file") == null) { error = "--server or --port-file is required"; return false; }
        if (Text("server") is { } server && !IPEndPoint.TryParse(server, out _)) { error = $"--server '{server}' is not IP:PORT"; return false; }
        options = new HeadlessOptions
        {
            Server = Text("server"), PortFile = Text("port-file"), Name = name, Seconds = seconds, LogPath = Text("log"), List = list,
            Create = create, MaxPlayers = maxPlayers, Track = track, Laps = laps, Bots = bots, Backward = values.ContainsKey("backward"),
            Traffic = values.ContainsKey("traffic"), Join = join, JoinFirst = joinFirst, JoinId = joinId > 0 ? joinId : null,
            Car = car >= 0 ? car : null, CarKey = carKey, NoReady = values.ContainsKey("no-ready"), StartWhen = startWhen > 0 ? startWhen : null,
            ResetAtStep = resetAt, LeaveAtStep = leaveAt, Steer = steer, Chat = chat, Knockout = knockout, KoRounds = koRounds, AcceptModDownloads = values.ContainsKey("accept-mod-downloads"),
        };
        return true;
    }
}

/// <summary>Scripted controls of a headless client: neutral before the green light, then full throttle; one gear-up at the start
/// and optionally one reset request (one-shot actions, acknowledged by the server).</summary>
public sealed record HeadlessScript(int ResetAtStep = 0, int LeaveAtStep = 0, sbyte Steer = 0, byte Gas = 255);

/// <summary>Outcome of one headless client run (logged by the CLI, asserted by the smoke tests).</summary>
public sealed record HeadlessClientResult(
    SessionClientPhase Phase, RaceClientState State, NetRejectReason? Reject, int GameId, int RaceId, int Seat, int CarId,
    int FirstSnapshotSteps, int LastSteps, int LastTick64, long SnapshotsComplete, long SnapshotsIncomplete, long LastAppliedInput,
    long SamplesSent, long LastActionAck, long ActionsQueued, long LateInputs, long HeldSteps, long NeutralSteps, long DurableApplied,
    long Events, int ManualResets, NetResults? Results, long RoundTripMicros, int Mtu, int MaxUnreliablePayload, int CarMovedMeters,
    long Malformed, int GamesListed, bool Refused, bool Left, bool Operator,
    /// <summary>Knockout: completed rounds of the series as the server reported them, whether the run reached its target (or the end
    /// of the series), whether this player is out of the series and the number of rounds it drove.</summary>
    int SeriesRounds = 0, bool SeriesDone = false, bool Eliminated = false, int RoundsDriven = 0,
    /// <summary>Rounds this client only watched (spectator welcome: an eliminated knockout player) and every round it took part in or
    /// watched, oldest first, with the snapshots it assembled and a digest of the result it received.</summary>
    int RoundsWatched = 0, IReadOnlyList<HeadlessRound>? Rounds = null);

/// <summary>One round as a headless client saw it: whether it only watched, how many complete snapshots it assembled, the last server
/// step it saw and a digest of the received result (equal for every participant and spectator of the round).</summary>
public sealed record HeadlessRound(int RaceId, bool Spectator, long Snapshots, int LastSteps, string ResultDigest);

public static class HeadlessClient
{
    /// <summary>Runs the scenario of <paramref name="opt"/> against the session server and returns its outcome. <paramref name="prepare"/>
    /// loads the frozen race of a welcome with the client's own data (what the presentation renders); false rejects the race.</summary>
    public static HeadlessClientResult Run(IPEndPoint server, byte[] fingerprint, HeadlessOptions opt, TimeSpan timeout,
        Action<string> log, Func<NetWelcome, bool>? prepare = null)
    {
        var started = Stopwatch.StartNew();
        var (first, serverMods) = RunOnce(server, fingerprint, opt, timeout, log, prepare);
        if (serverMods == null || first.Reject != NetRejectReason.ModsMismatch) return first;
        // The server plays with other mods: install what is missing (with consent) and play with exactly its versions.
        if (!SyncMods(server, fingerprint, opt, serverMods, timeout - started.Elapsed, log)) return first;
        var (second, _) = RunOnce(server, fingerprint, opt with { Mods = null }, timeout - started.Elapsed, log, prepare);
        return second;
    }

    /// <summary>Mod sync of a client that the server refused with ModsMismatch (5.4): everything local is used as it is; missing
    /// versions are downloaded when <see cref="HeadlessOptions.AcceptModDownloads"/> says so. True = the session now has the server's set.</summary>
    static bool SyncMods(IPEndPoint server, byte[] fingerprint, HeadlessOptions opt, NetModSet serverMods, TimeSpan timeout, Action<string> log)
    {
        string modsDir = NFS.Game.Mods.ModSession.Current.ModsDir is { Length: > 0 } d ? d : ModSync.ModsDir();
        var (missing, conflicts) = ModSync.Plan(serverMods, modsDir);
        if (conflicts.Count > 0) { log($"mods: cannot use the server's mods: {string.Join("; ", conflicts)}"); return false; }
        if (missing.Count > 0)
        {
            var clock = Stopwatch.StartNew();
            LiteNetClientHost? net = null;
            INetClientTransport Open(INetClientHandler handler, byte[] hello)
            {
                net?.Dispose();
                net = new LiteNetClientHost(handler);
                net.Connect(server, hello);
                return net;
            }
            var download = new ModDownloadClient(fingerprint, opt.Name, modsDir, serverMods, Open, () => ServerLoop.Micros(clock), new ModDownloadOptions
            {
                Consent = items =>
                {
                    if (opt.AcceptModDownloads) { log($"mods: downloading from the server: {string.Join(", ", items.Select(i => i.Display))}"); return true; }
                    log($"mods: the server needs mods you do not have: {string.Join(", ", items.Select(i => i.Display))}; they were not downloaded (pass --accept-mod-downloads to allow it)");
                    return false;
                },
            }, log);
            download.Start();
            long limit = (long)timeout.TotalMicroseconds;
            while (!download.Finished && ServerLoop.Micros(clock) < limit)
            {
                net?.Poll();
                download.Update(ServerLoop.Micros(clock));
                Thread.Sleep(1);
            }
            net?.Dispose();
            if (download.Phase != ModDownloadPhase.Done)
            {
                log($"mods: {(download.Phase == ModDownloadPhase.Declined ? "download declined" : download.Error ?? "download did not finish in time")}");
                return false;
            }
        }
        if (ModSync.UseServerSet(serverMods, modsDir) is { } error) { log($"mods: cannot use the server's mods: {error}"); return false; }
        log($"mods: playing with the server's set ({serverMods.Entries.Count} mods)");
        return true;
    }

    static (HeadlessClientResult Result, NetModSet? ServerMods) RunOnce(IPEndPoint server, byte[] fingerprint, HeadlessOptions opt, TimeSpan timeout,
        Action<string> log, Func<NetWelcome, bool>? prepare)
    {
        var clock = Stopwatch.StartNew();
        long Now() => ServerLoop.Micros(clock);
        LiteNetClientHost? net = null;
        INetClientTransport Open(INetClientHandler handler, byte[] hello)
        {
            net?.Dispose();
            net = new LiteNetClientHost(handler);
            net.Connect(server, hello);
            return net;
        }
        var session = new SessionClient(fingerprint, opt.Name, Open, Now, new SessionClientOptions { PrepareRace = prepare, Mods = opt.Mods ?? NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods), Cars = NFS.Net.Server.ServerLoop.ModCars(null) }, log);
        bool refused = false, left = false, listed = false, requested = false, ready = false, carSent = false, started = false, optionsSent = false;
        int gamesListed = 0;
        session.Error += e =>
        {
            log($"error: {e.Kind}: {e.Message}{(e.Reason != null ? $" ({e.Reason})" : "")}");
            if (session.LastReject is { Reason: NetRejectReason.ModsMismatch } rejected)
                log($"mods: {(opt.Mods ?? NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods)).Diff(rejected.ServerMods, "you", "the server")}");
            if (e.Kind is SessionErrorKind.CreateRefused or SessionErrorKind.JoinRefused or SessionErrorKind.Rejected) refused = true;
        };
        session.GamesChanged += games =>
        {
            gamesListed = games.Count;
            if (opt.List) listed = true;
        };
        session.PhaseChanged += p => log($"phase {p}");
        session.ChatReceived += line => log($"chat {line.Sender}: {line.Text}");
        bool chatSent = opt.Chat == null;
        RaceClient? raceNow = null, lastRace = null, driven = null;
        var allRaces = new List<RaceClient>();
        session.RaceClientChanged += r => { raceNow = r; if (r != null) { lastRace = r; allRaces.Add(r); } };
        int firstSteps = -1, actionsQueued = 0, resets = 0, lastLock = 0, moved = 0;
        bool gearQueued = false, resetQueued = false;
        // knockout: the round whose result was already taken, the completed rounds the lobby flags belong to, outcome
        RaceClient? handled = null;
        int lobbyRounds = -1, seriesRounds = 0;
        bool seriesDone = false, eliminated = false;
        Vec3? startPos = null;
        long deadline = (long)timeout.TotalMicroseconds, finishedAt = -1, lastReport = 0, lastListRequest = long.MinValue;
        session.Connect();
        while (true)
        {
            long now = Now();
            if (now > deadline) { log($"client timeout after {timeout.TotalSeconds:0} s"); break; }
            net?.Poll();
            session.Update(now);
            if (session.Phase is SessionClientPhase.Closed or SessionClientPhase.Rejected || refused) break;
            if (listed)
            {
                foreach (var g in session.Games) log(DescribeGame(g));
                log($"games listed: {session.Games.Count}");
                break;
            }
            if (session.Phase == SessionClientPhase.Browsing && !session.Pending) BrowseStep(session, opt, now, ref requested, ref lastListRequest, log);
            var lobby = session.Lobby;
            if (opt.Knockout && session.Phase == SessionClientPhase.InGame && lobby is { Phase: NetGamePhase.Lobby, Series: { } ser })
            {
                // every completed round (or a new series) starts over with readiness and the operator's start
                if (ser.CompletedRounds != lobbyRounds) { lobbyRounds = ser.CompletedRounds; ready = false; started = false; }
                seriesRounds = ser.CompletedRounds;
                eliminated = ser.Entries.Any(e => e.Slot == lobby.Members.FirstOrDefault(m => m.Name == opt.Name)?.Slot && e.Slot >= 0 && !e.Active);
                if (ser.Finished || ser.CompletedRounds >= opt.KoRounds)
                {
                    log($"series: {ser.CompletedRounds} rounds done{(ser.Finished ? ", finished" : "")}; " +
                        $"table {string.Join(", ", ser.Entries.Select(e => $"{e.Name}:{(e.Active ? "in" : $"out{e.EliminatedRound + 1}")}"))}");
                    seriesDone = true;
                    break;
                }
            }
            if (session.Phase == SessionClientPhase.InGame && lobby is { Phase: NetGamePhase.Lobby })
            {
                // A created game starts with the server's defaults: the operator applies the options of the command line once.
                if (opt.Create != null && !optionsSent && session.IsOperator &&
                    session.SetOptions(opt.Track, opt.Laps, opt.Backward, opt.Bots, opt.Traffic)) optionsSent = true;
                if (opt.CarKey is { } carKey && !carSent && session.SetCar(carKey)) carSent = true;
                else if (opt.CarKey == null && opt.Car is { } car && !carSent && session.SetCar(car)) carSent = true;
                if (!opt.NoReady && !ready && session.SetReady(true)) ready = true;
                int want = opt.StartWhen ?? lobby.MaxPlayers;
                if (!chatSent && lobby.Members.Count >= lobby.MaxPlayers && session.SendChat(opt.Chat!)) { chatSent = true; log($"chat sent: {opt.Chat}"); }
                if (session.IsOperator && !started && (opt.Knockout || lastRace == null) && lobby.Members.Count >= want
                    && lobby.Members.Count(m => m.Ready) >= want && (opt.NoReady || session.IsReady))
                {
                    log($"starting the race with {lobby.Members.Count} members");
                    started = session.StartRace();
                }
            }
            var race = raceNow;
            if (race != null)
            {
                if (!ReferenceEquals(driven, race)) { driven = race; gearQueued = resetQueued = false; }
                var frame = race.Latest;
                if (opt.Knockout && ReferenceEquals(race, handled)) { }
                else if (race.State == RaceClientState.Finished && race.Results != null)
                {
                    if (finishedAt < 0) { finishedAt = now; log("results received"); }
                    else if (now - finishedAt > 300_000)
                    {
                        if (!opt.Knockout) break;
                        // knockout: the round is over; the lobby state of the server tells whether the series goes on
                        handled = race;
                        finishedAt = -1;
                        firstSteps = -1;
                        startPos = null;
                        foreach (var r in race.Results.Results.Racers)
                            log($"result place={r.Place} car={r.CarIndex} {r.Model} human={r.Human} estimated={r.Estimated} ticks={r.RaceTicks} state={r.RaceState}");
                    }
                }
                else if (frame != null && frame.SnapshotSequence > 0 && race.Welcome is { } w)
                {
                    if (firstSteps < 0) firstSteps = frame.Steps;
                    var car = frame.Car(w.CarId);
                    startPos ??= car.Pos;
                    moved = (int)Math.Sqrt(Math.Pow((car.Pos.X - startPos.Value.X) / 65536.0, 2) + Math.Pow((car.Pos.Z - startPos.Value.Z) / 65536.0, 2));
                    if (car.ManualResetLock > lastLock) resets++;
                    lastLock = car.ManualResetLock;
                    var script = opt.Script;
                    // A spectator (eliminated knockout player) only follows the stream: no controls, no actions, no leave script.
                    if (!race.Spectator)
                    {
                        race.Controls = frame.RaceStarted ? new PlayerInput(script.Steer, script.Gas, 0, 0) : default;
                        if (frame.RaceStarted && !gearQueued) { gearQueued = race.QueueAction(NetActionKind.GearUp); actionsQueued += gearQueued ? 1 : 0; }
                        if (script.ResetAtStep > 0 && frame.Steps >= script.ResetAtStep && !resetQueued)
                        {
                            resetQueued = race.QueueAction(NetActionKind.Reset);
                            actionsQueued += resetQueued ? 1 : 0;
                        }
                    }
                    if (!race.Spectator && script.LeaveAtStep > 0 && frame.Steps >= script.LeaveAtStep)
                    {
                        log($"leaving at step {frame.Steps}");
                        left = true;
                        break;
                    }
                }
                race.Update(now);
            }
            if (now - lastReport >= 1_000_000)
            {
                lastReport = now;
                var f = race?.Latest;
                var a = race?.LatestAck ?? default;
                log($"t={now / 1000} ms phase={session.Phase} game={session.GameId} lobby={session.Lobby?.Phase.ToString() ?? "-"} state={race?.State.ToString() ?? "-"} " +
                    $"steps={f?.Steps} tick64={f?.SimulationTick64} rtt={(net?.RoundTripMicros ?? 0) / 1000} ms " +
                    $"ack={a.AppliedInput}/{race?.LastSentSequence} actionAck={a.LastAction} late={a.LateInputs} held={a.HeldSteps} " +
                    $"neutral={a.NeutralSteps} snaps={race?.Stats.SnapshotsComplete} incomplete={race?.Stats.SnapshotsIncomplete} moved={moved} m");
            }
            Thread.Sleep(1);
        }
        var rounds = allRaces.Where(r => r.Welcome != null).Select(r => new HeadlessRound(r.Welcome!.RaceId, r.Spectator,
            r.Stats.SnapshotsComplete, r.Latest?.Steps ?? 0, ResultDigest(r.Results))).ToArray();
        // snapshot the outcome, then leave politely (return to the lobby, leave the game, disconnect) and let the packets flush
        var last = lastRace ?? raceNow;
        int maxSeq = net?.MaxSinglePacket(NetChannel.Unreliable) ?? 0, mtu = net?.Mtu ?? 0;
        long rtt = net?.RoundTripMicros ?? 0;
        var ack = last?.LatestAck ?? default;
        var wl = last?.Welcome;
        var lf = last?.Latest;
        var result = new HeadlessClientResult(session.Phase, last?.State ?? RaceClientState.Connecting, session.LastReject?.Reason, session.GameId,
            wl?.RaceId ?? 0, wl?.Seat ?? -1, wl?.CarId ?? -1, firstSteps, lf?.Steps ?? 0, lf?.SimulationTick64 ?? 0,
            last?.Stats.SnapshotsComplete ?? 0, last?.Stats.SnapshotsIncomplete ?? 0, ack.AppliedInput, last?.Stats.SamplesSent ?? 0,
            ack.LastAction, actionsQueued, ack.LateInputs, ack.HeldSteps, ack.NeutralSteps, last?.Stats.DurableApplied ?? 0,
            last?.Stats.EventsReceived ?? 0, resets, last?.Results, rtt, mtu, maxSeq, moved,
            session.Stats.Malformed + (last?.Stats.Malformed ?? 0), gamesListed, refused || session.Phase == SessionClientPhase.Rejected, left,
            session.IsOperator, seriesRounds, seriesDone, eliminated, rounds.Count(r => !r.Spectator), rounds.Count(r => r.Spectator), rounds);
        if (session.Connected)
        {
            if (!left && last?.Results != null && !opt.Knockout) session.ReturnToLobby();
            net?.Poll();
            if (session.Phase == SessionClientPhase.InGame) session.LeaveGame();
            session.Disconnect();
        }
        var flush = Now() + 200_000;
        while (Now() < flush) { net?.Poll(); Thread.Sleep(5); }
        net?.Dispose();
        foreach (var round in rounds)
            log($"{(round.Spectator ? "spectator: watched" : "driver: drove")} race {round.RaceId:X8}: {round.Snapshots} snapshots up to step {round.LastSteps}, result {round.ResultDigest}");
        log($"client done: {Describe(result)}");
        if (result.Results is { } res)
            foreach (var r in res.Results.Racers)
                log($"result place={r.Place} car={r.CarIndex} {r.Model} human={r.Human} estimated={r.Estimated} ticks={r.RaceTicks} state={r.RaceState}");
        return (result, session.LastReject is { Reason: NetRejectReason.ModsMismatch } mm ? mm.ServerMods : null);
    }

    /// <summary>Compact text of a received result (finish reason, step and every racer's place/car/time/state): participants and
    /// spectators of one round must produce the same text. "none" before the result arrived.</summary>
    public static string ResultDigest(NetResults? results) => results == null ? "none"
        : $"{results.Reason}@{results.Step}/{results.SimulationTick64}/dnf{results.DnfSeats}:" +
          string.Join(",", results.Results.Racers.Select(r => $"{r.Place}.{r.CarIndex}.{r.RaceTicks}.{r.RaceState}"));

    static void BrowseStep(SessionClient session, HeadlessOptions opt, long now, ref bool requested, ref long lastListRequest, Action<string> log)
    {
        if (opt.List) return;
        if (opt.Create != null)
        {
            if (requested) return;
            requested = true;
            log($"creating game '{opt.Create}' (players {opt.MaxPlayers}, track {opt.Track}, laps {opt.Laps}, bots {(opt.Bots < 0 ? "auto" : opt.Bots)})");
            session.CreateGame(new NetGameCreate(opt.Create, opt.MaxPlayers, opt.Knockout ? NetGameMode.Knockout : NetGameMode.SingleRace));
            return;
        }
        NetGameInfo? target = null;
        foreach (var g in session.Games)
        {
            bool free = g.Phase == NetGamePhase.Lobby && g.Players < g.MaxPlayers;
            if (opt.JoinId is { } id ? g.GameId == id : opt.Join != null ? string.Equals(g.Name, opt.Join, StringComparison.OrdinalIgnoreCase) : free)
            { target = g; break; }
        }
        if (target != null)
        {
            log($"joining {DescribeGame(target)}");
            session.JoinGame(target.GameId);
            return;
        }
        if (now - lastListRequest >= 500_000)
        {
            lastListRequest = now;
            session.RequestGames();
        }
    }

    public static string DescribeGame(NetGameInfo g) =>
        $"game id={g.GameId} name={g.Name} phase={g.Phase} players={g.Players}/{g.MaxPlayers} track={g.Track} laps={g.Laps} " +
        $"mode={g.Mode} bots={(g.Bots < 0 ? "auto" : g.Bots)} backward={g.Backward} traffic={g.Traffic} skill={g.Skill} catchUp={g.CatchUp} style={g.Style}";

    public static string Describe(HeadlessClientResult r) =>
        $"phase={r.Phase} state={r.State} reject={r.Reject?.ToString() ?? "-"} game={r.GameId} operator={r.Operator} race={r.RaceId:X8} seat={r.Seat} car={r.CarId} " +
        $"firstSteps={r.FirstSnapshotSteps} lastSteps={r.LastSteps} tick64={r.LastTick64} snapshots={r.SnapshotsComplete} incomplete={r.SnapshotsIncomplete} " +
        $"inputAck={r.LastAppliedInput}/{r.SamplesSent} actionAck={r.LastActionAck}/{r.ActionsQueued} late={r.LateInputs} held={r.HeldSteps} " +
        $"neutral={r.NeutralSteps} durable={r.DurableApplied} events={r.Events} resets={r.ManualResets} moved={r.CarMovedMeters} m " +
        $"rtt={r.RoundTripMicros / 1000} ms mtu={r.Mtu} maxUnreliablePayload={r.MaxUnreliablePayload} malformed={r.Malformed} " +
        $"results={(r.Results == null ? "none" : $"{r.Results.Reason} step {r.Results.Step} dnf {r.Results.DnfSeats}")}" +
        (r.SeriesRounds > 0 || r.SeriesDone ? $" seriesRounds={r.SeriesRounds} seriesDone={r.SeriesDone} eliminated={r.Eliminated} driven={r.RoundsDriven} watched={r.RoundsWatched}" : "");
}
