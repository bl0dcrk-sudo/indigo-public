using System.Diagnostics;
using System.Net;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

/// <summary>
/// Race of a dedicated server: a grid of eight cars whose seat records consist of human cars and AI opponents. The configured
/// <see cref="Players"/> is the lobby capacity (1..8); the actual race is composed at the operator's `start` from the humans
/// that are connected then (<see cref="BuildSetup(string?, int)"/> / <see cref="CreateHost(string?, int, int)"/>): N human
/// cars (class 1, LocalHuman, counted in +0x6C) plus 8-N real AI opponents (class 2), exactly as if the menu had been
/// configured for N players. Seats 2+ of a list race take the remaining records of the full split-screen lineup and are
/// marked human; for N == 1 the menu's second player record is demoted to an AI opponent. The default exact two-player
/// configuration is byte for byte the old split-screen setup (player 1, player 2, six opponents), so existing recordings and
/// tests stay valid.
/// </summary>
public sealed record ServerRaceConfig(int Track = 1, int Laps = 2, int Opponents = 2, bool Traffic = false, int Players = 2)
{
    public const int MinPlayers = NetProtocol.MinSeats;
    public const int MaxPlayers = NetProtocol.MaxSeats;
    public const int MaxRacers = 8;

    /// <summary>Operator bot setting that fills the grid with AI opponents (8 − humans).</summary>
    public const int AutoBots = -1;

    /// <summary>Minimum connected+ready humans an interactive lobby needs before `start`; -1 = exactly <see cref="Players"/>
    /// (the noninteractive exact-count race).</summary>
    public int MinimumPlayers { get; init; } = -1;

    /// <summary>Operator bot selection: <see cref="AutoBots"/> fills the grid (8 − humans); 0..7 requests that many class-2
    /// AI racing cars, capped so the grid never exceeds eight cars. The noninteractive default is auto, which keeps the
    /// legacy lineups byte-identical.</summary>
    public int BotSetting { get; init; } = AutoBots;

    /// <summary>Track direction of the race: false = forward, true = backward (the front-end "backwards" toggle, setup +0x30).
    /// Defaults to forward, which is the menu's normal direction; the CONFIG.DAT direction byte is no longer read, so a
    /// headless server is deterministic instead of inheriting a stale menu value.</summary>
    public bool Backward { get; init; }

    /// <summary>Bot skill of every race (setup +0x08: 0 = beginner, 1 = expert), applied after the setup is parsed, so it also
    /// holds in a knockout round (the front-end forces the expert there; a network game deliberately does not). Null keeps the
    /// value of the host's CONFIG.DAT.</summary>
    public int? Skill { get; init; }

    /// <summary>AI/human catch-up ("rubber band", setup +0x18) of every race; null keeps the CONFIG.DAT value of the host.</summary>
    public bool? CatchUp { get; init; }

    /// <summary>Physics style of every race (<c>FeConfig.Style</c>: 0 = Simulation, 1 = Wild, 2 = Arcade); null keeps the
    /// CONFIG.DAT value of the host.</summary>
    public int? Style { get; init; }

    /// <summary>Configured seat count (the maximum number of humans); invalid values are rejected before a host is created.</summary>
    public int EffectivePlayers => Players is >= MinPlayers and <= MaxPlayers
        ? Players : throw new ArgumentOutOfRangeException(nameof(Players), $"human seats must be in {MinPlayers}..{MaxPlayers}");

    /// <summary>Minimum humans the operator needs to start; the default (unset) requires every configured seat.</summary>
    public int EffectiveMinimumPlayers => MinimumPlayers switch
    {
        < 0 => EffectivePlayers,
        var min when min >= MinPlayers && min <= EffectivePlayers => min,
        _ => throw new ArgumentOutOfRangeException(nameof(MinimumPlayers),
            $"minimum human seats must be in {MinPlayers}..{EffectivePlayers} (configured {EffectivePlayers})"),
    };

    /// <summary>Largest bot count that still leaves room for <paramref name="humans"/> human cars (at least one human).</summary>
    public int BotCapFor(int humans)
    {
        if (humans is < MinPlayers or > MaxRacers) throw new ArgumentOutOfRangeException(nameof(humans), $"human cars must be in {MinPlayers}..{MaxRacers}");
        return MaxRacers - humans;
    }

    /// <summary>Planned bot count for <paramref name="humans"/> human cars: auto fills the grid, an explicit request is
    /// capped so humans + bots ≤ 8.</summary>
    public int BotsFor(int humans)
    {
        int cap = BotCapFor(humans);
        if (BotSetting == AutoBots) return cap;
        if (BotSetting is < 0 or > MaxRacers - 1)
            throw new ArgumentOutOfRangeException(nameof(BotSetting), $"bot setting must be {AutoBots} (auto) or 0..{MaxRacers - 1}");
        return Math.Min(BotSetting, cap);
    }

    /// <summary>AI cars on the grid of a race with <paramref name="humans"/> human cars: the operator's bot setting (auto =
    /// eight-car lineup minus the humans), except the legacy exact two-player split-screen race, whose opponent count comes
    /// from the menu option (0 or 2).</summary>
    public int AiCars(int humans)
    {
        if (BotSetting != AutoBots) return BotsFor(humans);
        if (humans is < MinPlayers or > MaxRacers) throw new ArgumentOutOfRangeException(nameof(humans), $"human cars must be in {MinPlayers}..{MaxRacers}");
        return humans == 2 && EffectivePlayers == 2 ? (Opponents == 2 ? MaxRacers - 2 : 0) : MaxRacers - humans;
    }

    /// <summary>AI cars of the configured capacity race (shown in the lobby; a dynamic start with fewer humans has
    /// <see cref="AiCars"/> opponents).</summary>
    public int BotCount => AiCars(EffectivePlayers);

    /// <summary>Frozen race setup from the game's CONFIG.DAT with the server's choices for the configured capacity
    /// (split-screen game type line-up: two menu human records plus six opponent records; seats 2+ convert opponent records
    /// to humans).</summary>
    public RaceSetup BuildSetup(string? gameDir) => BuildSetup(gameDir, EffectivePlayers);

    /// <summary>
    /// Frozen race setup for exactly <paramref name="humans"/> human cars (1..<see cref="Players"/>). The full eight-car
    /// split-screen lineup is used (and the remaining records become real AI opponents), except for the legacy exact
    /// two-player race, which keeps the menu's own opponent/traffic choice.
    /// </summary>
    public RaceSetup BuildSetup(string? gameDir, int humans) => BuildSetup(gameDir, humans, AiCars(humans));

    /// <summary>
    /// Frozen race setup for exactly <paramref name="humans"/> human cars and <paramref name="bots"/> class-2 AI racing cars
    /// (humans + bots ≤ 8). The legacy exact two-player race (2 humans + 0 or 6 bots) keeps the menu's own setup block; every
    /// other combination uses the full lineup and truncates it to the actual grid size, so fewer bots are missing cars rather
    /// than hidden ones.
    /// </summary>
    public RaceSetup BuildSetup(string? gameDir, int humans, int bots) => BuildSetup(gameDir, humans, bots, null);

    /// <summary>
    /// Same as the three-argument overload, but the human car records take the car model types the players selected in the
    /// menu (<see cref="NetProtocol.NoCarType"/> keeps the model of the frozen setup). <paramref name="humanCarTypes"/> is
    /// indexed by human car (the order of <see cref="RaceSetup.Car(int)"/>, i.e. the local seats), never by lobby slot.
    /// </summary>
    public RaceSetup BuildSetup(string? gameDir, int humans, int bots, IReadOnlyList<int>? humanCarTypes) =>
        BuildSetup(gameDir, humans, bots, humanCarTypes, null);

    /// <summary>
    /// Same as the overload above, and the human car records take the colours the players selected
    /// (<see cref="NetProtocol.NoColour"/> keeps the colour of the frozen record); indexed by human car like the car types.
    /// </summary>
    public RaceSetup BuildSetup(string? gameDir, int humans, int bots, IReadOnlyList<int>? humanCarTypes, IReadOnlyList<int>? humanColours)
    {
        if (humans is < MinPlayers or > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(humans), $"human cars must be in {MinPlayers}..{MaxPlayers}");
        if (humans > EffectivePlayers) throw new ArgumentOutOfRangeException(nameof(humans), $"{humans} humans exceed the {EffectivePlayers}-seat capacity");
        if (bots < 0 || bots > MaxRacers - humans) throw new ArgumentOutOfRangeException(nameof(bots), $"bots must be in 0..{MaxRacers - humans}");
        bool pairCapacity = humans == 2 && EffectivePlayers == 2;
        bool legacyPair = pairCapacity && bots is 0 or (MaxRacers - 2);
        var cfg = FeConfig.Load(gameDir);
        ApplyStyle(cfg);
        cfg.GameType = 1;
        cfg.RaceType = 0;
        cfg.Track = (ushort)Math.Clamp(Track, 0, 7);
        cfg.Laps = (byte)Math.Clamp(Laps, 1, 8);
        cfg.Backward = (byte)(Backward ? 1 : 0);
        // The menu's split-screen branch emits six opponents only for OpponentCount == 2. Every race other than the legacy
        // pair needs that full lineup (the remaining records become AI opponents), so the menu value is forced otherwise.
        cfg.OpponentCount = (byte)Math.Clamp(legacyPair ? (bots == 0 ? 0 : 2) : 2, 0, 2);
        cfg.TrafficOff = (byte)(Traffic ? 0 : 1);
        ForceAutomaticGearbox(cfg);
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        ApplyBotDifficulty(setup);
        if (!legacyPair)
        {
            MakeHumanSeats(setup, humans);
            setup.Set(0x68, humans + bots); // NumCars: the remaining lineup records are dropped, not merely hidden
            setup.Set(0x70, bots);          // NumOpponentCars
        }
        ApplyHumanCarTypes(setup, humans, humanCarTypes, humanColours);
        return setup;
    }

    /// <summary>
    /// Frozen setup of one round of a knockout series: the entrants of <see cref="KnockoutSeries.RaceIdentities"/> (up to nine; humans
    /// are the identities below <see cref="KnockoutSeries.Humans"/>, the rest AI) on the track of the stage (menu index = stage, like
    /// the local knockout) and the lap count of the game. Built from the menu's nine-car knockout lineup (two player records, seven
    /// opponents): records of the entrants are packed in identity order, humans first, and every car takes the grid slot the series
    /// gives it (previous round's best at the front).
    /// </summary>
    public RaceSetup BuildKnockoutSetup(string? gameDir, KnockoutSeries series, IReadOnlyList<int>? humanCarTypes) =>
        BuildKnockoutSetup(gameDir, series, humanCarTypes, null);

    /// <summary>Same as the overload above, with the colours the human players selected (indexed by human car).</summary>
    public RaceSetup BuildKnockoutSetup(string? gameDir, KnockoutSeries series, IReadOnlyList<int>? humanCarTypes, IReadOnlyList<int>? humanColours)
    {
        var ids = series.RaceIdentities;
        int humans = ids.Count(id => id < series.Humans);
        if (humans < 1) throw new InvalidOperationException("a knockout round needs a human entrant");
        var cfg = FeConfig.Load(gameDir);
        ApplyStyle(cfg);
        cfg.GameType = 1;
        cfg.RaceType = 2;
        cfg.Track = (ushort)Math.Clamp(series.Stage, 0, 7);
        cfg.Backward = (byte)(Backward ? 1 : 0);
        cfg.OpponentCount = 2;
        ForceAutomaticGearbox(cfg);
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        ApplyBotDifficulty(setup);
        if (setup.NumCars != KnockoutLineupCars) throw new InvalidOperationException($"the knockout lineup has {setup.NumCars} cars, expected {KnockoutLineupCars}");
        setup.Set(0x04, Math.Clamp(Laps, 1, 8));
        MakeHumanSeats(setup, series.Humans);
        // Pack the records of the entrants (identity k = record k of the lineup) and give each its grid slot.
        const int first = 0x78, size = RaceConfig.CarRecordSize;
        var records = new byte[KnockoutLineupCars][];
        for (int k = 0; k < records.Length; k++) records[k] = setup.Block.AsSpan(first + k * size, size).ToArray();
        setup.Block.AsSpan(first, KnockoutLineupCars * size).Clear();
        for (int j = 0; j < ids.Count; j++)
        {
            records[ids[j]].CopyTo(setup.Block, first + j * size);
            setup.Set(0x478 + 4 * j, series.GridSlot(ids[j]));
        }
        setup.Set(0x68, ids.Count);
        setup.Set(0x6C, humans);
        setup.Set(0x70, ids.Count - humans);
        ApplyHumanCarTypes(setup, humans, humanCarTypes, humanColours);
        return setup;
    }

    /// <summary>The physics style goes into the front-end config before the race arguments are built, so that the builder encodes
    /// the style tags (0x05 and 0x21) exactly as for a menu race.</summary>
    void ApplyStyle(FeConfig cfg)
    {
        if (Style is not { } style) return;
        if (!NetProtocol.IsStyle(style)) throw new ArgumentOutOfRangeException(nameof(Style), $"style must be {NetProtocol.MinStyle}..{NetProtocol.MaxStyle}");
        cfg.Style = (byte)style;
    }

    /// <summary>Bot skill (+0x08) and catch-up (+0x18) are set on the parsed block: the argument builder forces the expert skill
    /// for a knockout race, which a network game overrides on purpose.</summary>
    void ApplyBotDifficulty(RaceSetup setup)
    {
        if (Skill is { } skill)
        {
            if (!NetProtocol.IsSkill(skill)) throw new ArgumentOutOfRangeException(nameof(Skill), $"skill must be {NetProtocol.MinSkill}..{NetProtocol.MaxSkill}");
            setup.Set(0x08, skill);
        }
        if (CatchUp is { } catchUp) setup.Set(0x18, catchUp ? 1 : 0);
    }

    /// <summary>The wire protocol carries no gearbox choice, so every human of a dedicated race drives an automatic gearbox.
    /// Without this the host's own CONFIG.DAT decided: its player 1 may be set to manual, and the seat that got that record
    /// (random, by connection order) then sat in neutral on gas alone.</summary>
    static void ForceAutomaticGearbox(FeConfig cfg)
    {
        cfg.P1Transmission = 1; // record value 1 = automatic (RaceRunner: Transmission == 1)
        cfg[0x3B] = 1;          // player 2
    }

    /// <summary>Cars of the menu's knockout lineup: nine entrants.</summary>
    public const int KnockoutLineupCars = 9;

    /// <summary>The authoritative host of a knockout round (see <see cref="BuildKnockoutSetup"/>); two humans keep the legacy
    /// split-screen constructor, one or three and more use the list-seat runner, as in <see cref="CreateHost(string?, int, int, int, IReadOnlyList{int}?)"/>.</summary>
    public RaceHost CreateKnockoutHost(string? gameDir, int raceId, KnockoutSeries series, IReadOnlyList<int>? humanCarTypes,
        IReadOnlyList<int>? humanColours = null)
    {
        int humans = series.RaceIdentities.Count(id => id < series.Humans);
        var session = new RaceLoader(gameDir).Load(BuildKnockoutSetup(gameDir, series, humanCarTypes, humanColours));
        if (humans == 2)
            return new RaceHost(session, 0, withAi: true, countdown: true, secondPlayerIndex: 1, raceId: raceId);
        return new RaceHost(session, 0, withAi: true, countdown: true, raceId: raceId,
            humanIndices: Enumerable.Range(0, humans).ToArray());
    }

    /// <summary>Replaces the car model type of the human records with explicit menu selections; no explicit selection keeps
    /// the frozen menu model, so the block stays byte-identical for clients that did not choose a car.</summary>
    static void ApplyHumanCarTypes(RaceSetup setup, int humans, IReadOnlyList<int>? humanCarTypes, IReadOnlyList<int>? humanColours = null)
    {
        if (humanColours != null && humanColours.Count != humans)
            throw new ArgumentOutOfRangeException(nameof(humanColours), $"{humanColours.Count} colours for {humans} human cars");
        if (humanCarTypes != null)
        {
            if (humanCarTypes.Count != humans)
                throw new ArgumentOutOfRangeException(nameof(humanCarTypes), $"{humanCarTypes.Count} car types for {humans} human cars");
            for (int i = 0; i < humans; i++)
            {
                int carType = humanCarTypes[i];
                if (!NetProtocol.IsCarNumberOrNone(carType))
                    throw new ArgumentOutOfRangeException(nameof(humanCarTypes),
                        $"car type of human {i} must be {NetProtocol.NoCarType} (none) or 0..{NetProtocol.MaxCarNumber}");
                if (NetProtocol.IsCarNumber(carType)) setup.Set(0x78 + i * RaceConfig.CarRecordSize, carType);
            }
        }
        if (humanColours == null) return;
        for (int i = 0; i < humans; i++)
        {
            int colour = humanColours[i];
            if (!NetProtocol.IsColourOrNone(colour))
                throw new ArgumentOutOfRangeException(nameof(humanColours), $"colour of human {i} must be {NetProtocol.NoColour} (none) or a palette entry");
            // CarSetup.Colour = record +0xC; no selection keeps the colour of the frozen menu record.
            if (colour != NetProtocol.NoColour) setup.Set(0x78 + i * RaceConfig.CarRecordSize + 0xC, colour);
        }
    }

    /// <summary>
    /// Rewrites the frozen block to exactly <paramref name="players"/> human seats (race class 1 is set to [ref:2433]
    /// Racer | LocalHuman, hence human mass/inertia applies) and counts them in +0x6C/+0x70. Records 2..players-1 [ref:2434]
    /// are promoted to humans; for a one-human race the menu's second split-screen player record is demoted to an AI opponent.
    /// The control ramps (+0x10/+0x14/+0x18) are copied from seat 0 so every network human drives [ref:2435]
    /// with the same filter as the menu's player 1. Names stay in the block (the wire roster carries the client names).
    /// </summary>
    static void MakeHumanSeats(RaceSetup setup, int players)
    {
        if (setup.NumCars < players)
            throw new InvalidOperationException($"the race setup has {setup.NumCars} cars, {players} humans requested");
        int first = 0x78;
        for (int i = 0; i < setup.NumCars; i++)
        {
            int o = first + i * RaceConfig.CarRecordSize;
            if (i >= players)
            {
                // Only the menu's player records (class 1) need demoting; grid records the menu already built as AI stay.
                if (i < 2) setup.Set(o + 4, 2);
                continue;
            }
            if (i < 2) continue;
            setup.Set(o + 0x10, setup.Get(first + 0x10));
            setup.Set(o + 0x14, setup.Get(first + 0x14));
            setup.Set(o + 0x18, setup.Get(first + 0x18));
            setup.Set(o + 4, 1); // CarClass: 1 = human, 2 = AI
        }
        setup.Set(0x6C, players);                 // NumPlayerCars
        setup.Set(0x70, setup.NumCars - players); // NumOpponentCars
    }

    /// <summary>The authoritative host of the configured capacity race: AI on, countdown, every configured human seat, a
    /// random race id. Two players keep the legacy split-screen constructor (Player2 aliases); 3+ use the list-seat runner.</summary>
    public RaceHost CreateHost(string? gameDir, int raceId) => CreateHost(gameDir, raceId, EffectivePlayers);

    /// <summary>
    /// The authoritative host of a race with exactly <paramref name="humans"/> human cars; every other car of the eight-car
    /// lineup is a real AI opponent (driver, personality, class 2). Two humans keep the legacy split-screen constructor
    /// (Player2 aliases); 1 or 3+ use the list-seat runner.
    /// </summary>
    public RaceHost CreateHost(string? gameDir, int raceId, int humans) => CreateHost(gameDir, raceId, humans, AiCars(humans));

    /// <summary>
    /// The authoritative host of a race with exactly <paramref name="humans"/> human cars and <paramref name="bots"/> real
    /// AI opponents (humans + bots ≤ 8); a two-human race keeps the legacy split-screen constructor.
    /// </summary>
    public RaceHost CreateHost(string? gameDir, int raceId, int humans, int bots) => CreateHost(gameDir, raceId, humans, bots, null);

    /// <summary>
    /// Same as the four-argument overload, with the car model types selected by the human players
    /// (<see cref="NetProtocol.NoCarType"/> keeps the frozen menu model); indexed by human car, i.e. local seat.
    /// </summary>
    public RaceHost CreateHost(string? gameDir, int raceId, int humans, int bots, IReadOnlyList<int>? humanCarTypes,
        IReadOnlyList<int>? humanColours = null)
    {
        var session = new RaceLoader(gameDir).Load(BuildSetup(gameDir, humans, bots, humanCarTypes, humanColours));
        if (humans == 2)
            return new RaceHost(session, 0, withAi: true, countdown: true, secondPlayerIndex: 1, raceId: raceId);
        return new RaceHost(session, 0, withAi: true, countdown: true, raceId: raceId,
            humanIndices: Enumerable.Range(0, humans).ToArray());
    }
}

/// <summary>Limits of one server process run. A null <see cref="MaxRun"/> / <see cref="WaitForPlayers"/> means no hard run
/// deadline / no waiting timeout, so an interactive operator lobby lives until `quit`, Ctrl+C or EOF; unattended runs are
/// always bounded.</summary>
public sealed record ServerRunLimits(TimeSpan? MaxRun, TimeSpan? WaitForPlayers, int MaxRaceSteps)
{
    /// <summary>Persistent operator lobby (interactive console): successive races, ends only on an operator/process stop.</summary>
    public static ServerRunLimits Interactive(int maxRaceSteps = 0) => new(null, null, Math.Max(0, maxRaceSteps));

    /// <summary>Unattended one-race run: hard deadline and waiting timeout from the command line (clamped to 1 s..1 h).</summary>
    public static ServerRunLimits NonInteractive(int maxRunSeconds, int waitSeconds, int maxRaceSteps = 0) => new(
        TimeSpan.FromSeconds(Math.Clamp(maxRunSeconds, 1, 3600)),
        TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 3600)),
        Math.Max(0, maxRaceSteps));
}

/// <summary>Optional interactive operator console attached to a server run (TASKS item 15, first stage).
/// Lines come from a non-blocking queue owned by the caller; the server thread applies them, so <see cref="RaceServer"/>
/// stays single-threaded.</summary>
public sealed class ServerInteraction
{
    /// <summary>The operator must issue `start`; readiness alone never starts the race.</summary>
    public bool ManualStart { get; init; }
    /// <summary>Non-blocking console source: one line per call, null when no line is pending (EOF is reported as "quit").</summary>
    public Func<string?>? NextLine { get; init; }
    /// <summary>Called on the server thread whenever the lobby roster changes.</summary>
    public Action<ServerLobbyState>? LobbyChanged { get; init; }
    /// <summary>Called after an operator changes the bot selection in the lobby.</summary>
    public Action<int>? BotSettingChanged { get; init; }
    /// <summary>Called after an operator changes the track of the next start in the lobby.</summary>
    public Action<int>? TrackChanged { get; init; }
    /// <summary>Called after an operator changes the track direction (forward/backward) of the next start in the lobby.</summary>
    public Action<bool>? DirectionChanged { get; init; }
}

/// <summary>Real-clock loop of the dedicated server: poll the UDP transport, apply operator commands, advance
/// <see cref="RaceServer"/> by the monotonic clock, sleep until the next step. One thread owns transport callbacks and the race.</summary>
public static class ServerLoop
{
    /// <summary>Car catalog of a server with data mods (original + mod cars of <see cref="NFS.Game.Mods.ModSession.Current"/>), null without mods.</summary>
    public static NFS.Game.Cars.CarCatalog? ModCars(string? gameDir) =>
        NFS.Game.Mods.ModSession.Current.Mods.Count == 0 ? null : NFS.Game.Race.RaceLoader.DefaultCatalog(gameDir);

    public static int Run(RaceServer server, LiteNetServerHost net, Stopwatch clock, TimeSpan? maxRun, Action<string> log, CancellationToken cancel,
        ServerInteraction? interaction = null)
    {
        long deadline = maxRun is { } limit ? (long)limit.TotalMicroseconds : long.MaxValue;
        bool stopRequested = false;
        while (server.Phase != NetRacePhase.Closed)
        {
            long now = Micros(clock);
            if (!stopRequested && (cancel.IsCancellationRequested || now > deadline))
            {
                // Do not break: the server drains its reliable output (an active race's ServerStop result) and calls
                // CompleteStop itself, which the Update below drives to Closed within the shutdown drain bound.
                log(cancel.IsCancellationRequested ? "cancelled" : $"run limit {maxRun?.TotalSeconds ?? 0:0} s reached");
                server.Shutdown();
                stopRequested = true;
            }
            net.Poll();
            if (interaction != null && !server.Stopping) ApplyConsole(server, interaction, log);
            server.Update(Micros(clock));
            // A round keeps the 1 ms cadence; a lobby-only server waits longer (bounded by the run limit).
            Thread.Sleep(ServerIdlePolicy.WaitMillis(server.NeedsFastLoop, Micros(clock), stopRequested ? null : deadline));
        }
        // let the disconnect messages leave the socket
        var flushUntil = Micros(clock) + 300_000;
        while (Micros(clock) < flushUntil) { net.Poll(); Thread.Sleep(5); }
        return server.FinishReason == null ? 2 : 0;
    }

    /// <summary>Monotonic microseconds. Avoids the tick×10^6 overflow of a persistent lobby left running for weeks.</summary>
    public static long Micros(Stopwatch clock) => (long)clock.Elapsed.TotalMicroseconds;

    static void ApplyConsole(RaceServer server, ServerInteraction interaction, Action<string> log)
    {
        if (interaction.NextLine == null) return;
        while (interaction.NextLine() is { } line)
        {
            if (!ServerConsole.TryParseCommand(line, out var command, out string error))
            {
                log($"console: {error}");
                continue;
            }
            switch (command.Kind)
            {
                case ServerCommandKind.Start:
                    log(server.TryOperatorStart(out string why) ? "console: start accepted" : $"console: start refused ({why})");
                    break;
                case ServerCommandKind.Next:
                    log(server.TryOperatorNext(out string nextWhy) ? "console: next round prepared" : $"console: next refused ({nextWhy})");
                    break;
                case ServerCommandKind.Status:
                    foreach (var l in ServerConsole.FormatLobby(server.Lobby).Split('\n')) log(l);
                    // The console has no periodic heartbeat: during a round `status` is the way to see its progress.
                    if (server.Phase is NetRacePhase.Countdown or NetRacePhase.Racing or NetRacePhase.Finished or NetRacePhase.Aborted)
                        log(ServerConsole.FormatProgress(server.Phase, server.Host.Steps, server.Host.Runner.Tick,
                            server.Host.Runner.HumanSims.Count, server.Host.Runner.RacerCount));
                    break;
                case ServerCommandKind.Bots:
                    if (server.TrySetBotSetting(command.Bots, out string botWhy))
                    {
                        interaction.BotSettingChanged?.Invoke(server.BotSetting);
                        log($"console: bots set ({server.PlannedBots} planned for {server.ConnectedPlayers} humans, cap {server.BotCap})");
                    }
                    else log($"console: bots refused ({botWhy})");
                    break;
                case ServerCommandKind.BotAdd:
                    if (server.TryAddBot(out string addWhy))
                    {
                        interaction.BotSettingChanged?.Invoke(server.BotSetting);
                        log($"console: bot added ({server.PlannedBots} planned for {server.ConnectedPlayers} humans, cap {server.BotCap})");
                    }
                    else log($"console: bot + refused ({addWhy})");
                    break;
                case ServerCommandKind.BotRemove:
                    if (server.TryRemoveBot(out string removeWhy))
                    {
                        interaction.BotSettingChanged?.Invoke(server.BotSetting);
                        log($"console: bot removed ({server.PlannedBots} planned for {server.ConnectedPlayers} humans, cap {server.BotCap})");
                    }
                    else log($"console: bot - refused ({removeWhy})");
                    break;
                case ServerCommandKind.Track:
                    if (server.TrySetTrack(command.Track, out string trackWhy))
                    {
                        interaction.TrackChanged?.Invoke(server.Track);
                        log($"console: track set to menu track {server.Track} (TR{server.DataTrack:00}) for the next start");
                    }
                    else log($"console: track refused ({trackWhy})");
                    break;
                case ServerCommandKind.Direction:
                    if (server.TrySetBackward(command.Backward, out string dirWhy))
                    {
                        interaction.DirectionChanged?.Invoke(server.Backward);
                        log($"console: direction set to {(server.Backward ? "backward" : "forward")} for the next start");
                    }
                    else log($"console: direction refused ({dirWhy})");
                    break;
                case ServerCommandKind.Help:
                    foreach (var l in ServerConsole.HelpLines) log(l);
                    break;
                case ServerCommandKind.Quit:
                    log("console: quit");
                    server.Shutdown();
                    return;
            }
        }
    }

    /// <summary>Whole dedicated server run on <paramref name="bind"/>:<paramref name="port"/> (0 = ephemeral, reported via <paramref name="started"/>).
    /// <paramref name="interaction"/> = interactive operator console; without it the original noninteractive auto-start behaviour is kept.</summary>
    public static int Serve(string? gameDir, ServerRaceConfig race, ServerRunLimits limits, IPAddress bind, int port, Action<string> log,
        Action<int>? started = null, CancellationToken cancel = default, Action<RaceServer>? done = null, ServerInteraction? interaction = null)
    {
        var clock = Stopwatch.StartNew();
        var fingerprint = SimDataFingerprint.Compute(gameDir);
        int raceId = Random.Shared.Next(1, int.MaxValue);
        var host = race.CreateHost(gameDir, raceId);
        using var net = new LiteNetServerHost();
        RaceServer server;
        try
        {
            server = new RaceServer(host, fingerprint, net, Micros(clock),
                new RaceServerOptions
                {
                    WaitTimeoutMicros = limits.WaitForPlayers is { } wait ? (long)wait.TotalMicroseconds : long.MaxValue,
                    MaxRaceSteps = limits.MaxRaceSteps,
                    ManualStart = interaction?.ManualStart == true,
                    BotCount = race.BotCount,
                    BotSetting = race.BotSetting,
                    MinPlayers = race.EffectiveMinimumPlayers,
                    Mods = NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods),
                    Cars = ModCars(gameDir),
                },
                log, hostFactoryBots: interaction != null ? (id, humans, bots) => race.CreateHost(gameDir, id, humans, bots) : null,
                hostFactoryTypes: (id, humans, bots, carTypes) => race.CreateHost(gameDir, id, humans, bots, carTypes),
                // The track-aware factory composes every round of a run that may change the track or direction (`track N` /
                // `direction forward|backward` in the console); without a command it is the same host the other factories
                // would build, so the setup stays byte-identical.
                hostFactoryTrack: (id, humans, bots, carTypes, track, backward) =>
                    (race with { Track = track, Backward = backward }).CreateHost(gameDir, id, humans, bots, carTypes)) { Clock = () => Micros(clock) };
        }
        catch
        {
            host.Dispose(); // the server did not take ownership
            throw;
        }
        if (interaction?.LobbyChanged is { } lobbyChanged) server.RosterChanged += () => lobbyChanged(server.Lobby);
        net.Handler = server;
        try
        {
            net.Start(bind, port);
            log($"server race {raceId:X8} menu track {race.Track} (TR{host.Runner.Session.TrackNumber:00}) laps {race.Laps} " +
                $"players min {race.EffectiveMinimumPlayers} max {race.EffectivePlayers} opponents {race.BotCount} on {bind}:{net.Port}; " +
                $"protocol {NetProtocol.Version} physics {NetProtocol.PhysicsVersion} data {Convert.ToHexString(fingerprint)[..16]} " +
                $"setup {Convert.ToHexString(server.SetupHash)[..16]} budget {NetProtocol.MaxDatagram} B");
            started?.Invoke(net.Port);
            if (interaction?.LobbyChanged is { } initialLobby) initialLobby(server.Lobby);
            int code = ServerLoop.Run(server, net, clock, limits.MaxRun, log, cancel, interaction);
            var s = server.Stats;
            log($"server done: reason={server.FinishReason?.ToString() ?? "none"} phase={server.Phase} steps={s.Steps} tick64={server.Host.Runner.Tick} " +
                $"snapshotParts={s.SnapshotParts} snapshotBytes={s.SnapshotBytes} maxPart={s.MaxSnapshotPart} maxPartsPerSnapshot={s.MaxPartsPerSnapshot} " +
                $"durable={s.DurableMessages}/{s.DurableBytes}B events={s.EventMessages} inputs={s.InputPackets} rateLimited={s.RateLimited} " +
                $"malformed={s.Malformed} wrongSession={s.WrongSession} rejected={s.Rejected} slowDrops={s.SlowClientDrops} " +
                $"slips={s.ClockSlipSteps} sendErrors={net.SendErrors}");
            if (server.Host.Results is { } res)
                foreach (var r in res.Racers)
                    log($"result place={r.Place} car={r.CarIndex} {r.Model} human={r.Human} estimated={r.Estimated} ticks={r.RaceTicks} state={r.RaceState}");
            done?.Invoke(server);
            return code;
        }
        finally
        {
            server.Dispose();
        }
    }
}
