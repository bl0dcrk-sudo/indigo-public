using System.Globalization;
using System.Net;
using System.Text;
using NFS.Net.Protocol;

namespace NFS.Net.Server;

/// <summary>Configuration entered in the interactive dedicated-server console (TASKS item 15). This build runs a single race
/// with a true lobby: it needs <see cref="MinPlayers"/> connected-and-ready humans (1..8) and has <see cref="MaxPlayers"/>
/// seats (never fewer than the minimum). The race is composed at `start` for exactly the connected humans plus the operator's
/// <see cref="Bots"/> selection: <see cref="ServerRaceConfig.AutoBots"/> fills the eight-car grid, 0..7 requests that many
/// real AI opponents (capped so the total never exceeds eight). Players can join or leave between races: each round gets a
/// fresh race id/epoch on the same connection. Any seat count other than two uses the full eight-car lineup, so the traffic
/// question only applies to two.</summary>
public sealed record ServerConsoleConfig(string Mode, IPAddress Bind, int Port, int Track, int Laps,
    int MinPlayers, int MaxPlayers, int Bots, bool Traffic, bool Backward = false)
{
    /// <summary>Seat capacity of the race (the configured maximum).</summary>
    public int Players => MaxPlayers;

    public ServerRaceConfig ToRaceConfig() => new(Track, Laps, Opponents: 2, Traffic, Players)
    {
        MinimumPlayers = MinPlayers,
        BotSetting = Bots,
        Backward = Backward,
    };
}

public enum ServerCommandKind { None, Start, Next, Status, Help, Quit, Bots, BotAdd, BotRemove, Track, Direction }

/// <summary>One parsed line of the operator console. <see cref="Bots"/> is the requested setting for
/// <see cref="ServerCommandKind.Bots"/> (-1 = auto fills the grid); <see cref="Track"/> is the requested track (0..7) for
/// <see cref="ServerCommandKind.Track"/>; <see cref="Backward"/> is the requested direction (true = backward) for
/// <see cref="ServerCommandKind.Direction"/>.</summary>
public readonly record struct ServerCommand(ServerCommandKind Kind, int Bots = ServerRaceConfig.AutoBots, int Track = -1,
    bool Backward = false);

/// <summary>One seat as shown in the console lobby (name/car/readiness; empty before a client takes it).</summary>
public sealed record ServerLobbySeat(int Seat, string? Name, int CarId, string? Model, bool Connected, bool Ready);

/// <summary>Snapshot of the console lobby. <see cref="Bots"/> is the actual planned bot count for the current connected
/// humans (what `start` will compose); <see cref="BotSetting"/> is the operator selection (-1 = auto) and <see cref="BotCap"/>
/// is the largest possible count for those humans.</summary>
public sealed record ServerLobbyState(NetRacePhase Phase, int Track, int Laps, int Bots, int MinPlayers,
    IReadOnlyList<ServerLobbySeat> Seats, int BotSetting = ServerRaceConfig.AutoBots, int BotCap = -1, bool Backward = false)
{
    public int Connected => Seats.Count(s => s.Connected);
    public int Ready => Seats.Count(s => s.Connected && s.Ready);
    /// <summary>At least the minimum number of humans connected, and every connected human loaded (ready).</summary>
    public bool CanStart => Phase is NetRacePhase.WaitingForPlayers or NetRacePhase.Loading &&
                            MinPlayers > 0 && Connected >= MinPlayers && Seats.Where(s => s.Connected).All(s => s.Ready);
}

/// <summary>Pure parsing/formatting of the interactive dedicated-server console (stage 15). No console I/O here so the same
/// validation and lobby text are covered by headless tests.</summary>
public static class ServerConsole
{
    public const string ModeSingle = "single";

    public static IReadOnlyList<string> HelpLines { get; } =
    [
        "console commands: start | next | status | help | quit",
        "  start     - begin the race once the required players (1..8) are connected and ready; the grid is composed for them",
        "  next      - after the results, prepare another race on the same connections",
        "  status    - show the lobby (players, cars, readiness, planned bots and the cap), plus the race progress in a round",
        "  bots N    - request N real AI racing cars (0..7) for the next start",
        "  bots auto - fill the eight-car grid with AI opponents (default)",
        "  bot + / - - add or remove one bot relative to the current plan",
        "  track N   - select the track (0..7) for the next start; the configuration default is updated too",
        "  direction - select the track direction (forward | backward) for the next start; the configuration default is updated too",
        "  quit      - stop the server (an active race ends with a ServerStop result before the connections close)",
        "bots are real class-2 opponents: the total number of cars is humans + bots and never exceeds eight;",
        "bot commands are accepted in the lobby and between rounds, never during a race",
        "configuration: the last confirmed interactive setup and the lobby bot/track/direction selection are saved to server-config.json",
        "               offered as the defaults of the next interactive run",
    ];

    /// <summary>Defaults offered by the configuration dialog when no saved configuration is available.</summary>
    public static ServerConsoleConfig DefaultConfig { get; } = new(ModeSingle, IPAddress.Loopback, 0, 1, 2,
        ServerRaceConfig.MinPlayers, ServerRaceConfig.MaxPlayers, ServerRaceConfig.AutoBots, false);

    // ---------------- configuration dialog ----------------

    /// <summary>Asks for the configuration on <paramref name="read"/>/<paramref name="write"/> and confirms it before the caller
    /// starts listening. Empty answers use the defaults; invalid answers are explained and asked again.</summary>
    public static bool TryReadConfig(Func<string?> read, Action<string> write, out ServerConsoleConfig? config, out string error) =>
        TryReadConfig(read, write, out config, out error, null);

    /// <summary>Same as the four-argument overload, with the defaults of the previous approved interactive setup.</summary>
    public static bool TryReadConfig(Func<string?> read, Action<string> write, out ServerConsoleConfig? config, out string error,
        ServerConsoleConfig? defaults)
    {
        config = null;
        var d = defaults ?? DefaultConfig;
        string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
        if (!Ask<string>(read, write, $"mode [{d.Mode}]: ", d.Mode, ParseMode, out string mode, out error)) return false;
        string bindDefault = d.Bind.Equals(IPAddress.Any) ? "0.0.0.0" : d.Bind.ToString();
        if (!Ask<IPAddress>(read, write, $"bind address [{bindDefault}]: ", bindDefault, ParseBind, out IPAddress bind, out error)) return false;
        if (!Ask<int>(read, write, $"UDP port (0 = ephemeral) [{d.Port}]: ", Inv(d.Port), ParsePort, out int port, out error)) return false;
        if (!Ask<int>(read, write, $"track 0..7 [{d.Track}]: ", Inv(d.Track), ParseTrack, out int track, out error)) return false;
        string dirDefault = d.Backward ? "backward" : "forward";
        if (!Ask<bool>(read, write, $"direction forward/backward [{dirDefault}]: ", dirDefault, ParseDirection, out bool backward, out error)) return false;
        if (!Ask<int>(read, write, $"laps 1..8 [{d.Laps}]: ", Inv(d.Laps), ParseLaps, out int laps, out error)) return false;
        int minPlayers, maxPlayers;
        while (true)
        {
            if (!Ask<int>(read, write, $"min players {ServerRaceConfig.MinPlayers}..{ServerRaceConfig.MaxPlayers} [{d.MinPlayers}]: ",
                    Inv(d.MinPlayers), ParsePlayers, out minPlayers, out error)) return false;
            // A seat range, not an exact count: the operator may start with the minimum and leave the other seats to AI.
            if (!Ask<int>(read, write, $"max players {minPlayers}..{ServerRaceConfig.MaxPlayers} [{d.MaxPlayers}]: ",
                    Inv(d.MaxPlayers), ParsePlayers, out maxPlayers, out error)) return false;
            if (maxPlayers < minPlayers)
            {
                write($"invalid: max players ({maxPlayers}) must not be below min players ({minPlayers})\n");
                continue;
            }
            break;
        }
        string botsDefault = d.Bots == ServerRaceConfig.AutoBots ? "auto" : Inv(d.Bots);
        if (!Ask<int>(read, write, $"bots 0..{ServerRaceConfig.MaxRacers - 1} or auto (-1 = fill the grid) [{botsDefault}]: ",
                botsDefault, ParseBots, out int bots, out error)) return false;
        // The menu only emits traffic on the split-screen pair and never with the full opponent line-up; other capacities
        // always use the full eight-car lineup, so the question would only be a value the race ignores.
        bool traffic = false;
        if (maxPlayers == 2)
        {
            string trafficDefault = d.Traffic ? "yes" : "no";
            if (!Ask<bool>(read, write, $"traffic yes/no [{trafficDefault}]: ", trafficDefault, ParseYesNo, out traffic, out error)) return false;
        }

        var cfg = new ServerConsoleConfig(mode, bind, port, track, laps, minPlayers, maxPlayers, bots, traffic, backward);
        int capacityCap = ServerRaceConfig.MaxRacers - maxPlayers;
        int planned = bots == ServerRaceConfig.AutoBots ? capacityCap : Math.Min(bots, capacityCap);
        write($"configuration: mode={cfg.Mode} bind={cfg.Bind}:{cfg.Port} track={cfg.Track} direction={(cfg.Backward ? "backward" : "forward")} laps={cfg.Laps} " +
              $"players={cfg.MinPlayers}..{cfg.MaxPlayers} bots={(bots == ServerRaceConfig.AutoBots ? "auto" : Inv(bots))} " +
              $"planned={planned} cap={capacityCap} (with {maxPlayers} humans) traffic={(cfg.Traffic ? "on" : "off")}\n");
        write("start listening? (yes/no) [no]: ");
        string? answer = read();
        if (answer == null) { error = "console input closed"; return false; }
        string confirm = answer.Trim();
        if (confirm.Length == 0) confirm = "no";
        if (!TryParseYesNo(confirm, out bool accepted, out string confirmError))
        {
            error = $"invalid confirmation: {confirmError}";
            return false;
        }
        if (!accepted) { error = "configuration not confirmed"; return false; }
        config = cfg;
        error = "";
        return true;
    }

    delegate bool FieldParser<T>(string text, out T value, out string error);

    static bool Ask<T>(Func<string?> read, Action<string> write, string prompt, string defaultText,
        FieldParser<T> parse, out T value, out string error)
    {
        value = default!;
        error = "";
        while (true)
        {
            write(prompt);
            string? line = read();
            if (line == null) { error = "console input closed"; return false; }
            string text = line.Trim();
            if (text.Length == 0) text = defaultText;
            if (parse(text, out value, out error)) return true;
            write($"invalid: {error}\n");
        }
    }

    static bool ParseMode(string text, out string value, out string error)
    {
        value = text.Trim().ToLowerInvariant();
        if (value is "single" or "single race") { value = ModeSingle; error = ""; return true; }
        error = value == "knockout"
            ? "knockout is not implemented yet (TASKS item 16)"
            : "only 'single' is available in this build";
        return false;
    }

    /// <summary>Parses the bind field of the dialog and of a saved configuration.</summary>
    public static bool TryParseBind(string text, out IPAddress value, out string error)
    {
        value = IPAddress.Loopback;
        string t = text.Trim();
        if (t.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { error = ""; return true; }
        if (t is "*" or "any") { value = IPAddress.Any; error = ""; return true; }
        if (IPAddress.TryParse(t, out var address)) { value = address; error = ""; return true; }
        error = $"'{text}' is not an IP address";
        return false;
    }

    static bool ParseBind(string text, out IPAddress value, out string error) =>
        TryParseBind(text, out value, out error);

    static bool ParsePort(string text, out int value, out string error) =>
        ParseInt(text, 0, 65535, "port", out value, out error);

    static bool ParseTrack(string text, out int value, out string error) =>
        ParseInt(text, 0, 7, "track", out value, out error);

    /// <summary>Track direction field: <c>forward</c>/<c>fwd</c>/<c>f</c> = forward, <c>backward</c>/<c>back</c>/<c>bwd</c>/<c>b</c>/
    /// <c>reverse</c> = backward.</summary>
    static bool ParseDirection(string text, out bool value, out string error)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "forward" or "fwd" or "f": value = false; error = ""; return true;
            case "backward" or "backwards" or "back" or "bwd" or "b" or "reverse" or "reversed": value = true; error = ""; return true;
            default: value = false; error = "expected forward or backward"; return false;
        }
    }

    static bool ParseLaps(string text, out int value, out string error) =>
        ParseInt(text, 1, 8, "laps", out value, out error);

    static bool ParsePlayers(string text, out int value, out string error)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = "expected a number";
            return false;
        }
        if (value is < ServerRaceConfig.MinPlayers or > ServerRaceConfig.MaxPlayers)
        {
            error = $"human seats must be in {ServerRaceConfig.MinPlayers}..{ServerRaceConfig.MaxPlayers}";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Bot setting field: <c>auto</c>/<c>full</c>/<c>-1</c> fills the grid, otherwise 0..7 requested AI cars.</summary>
    static bool ParseBots(string text, out int value, out string error)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "auto" or "full" or "fill" or "-1":
                value = ServerRaceConfig.AutoBots;
                error = "";
                return true;
        }
        return ParseInt(text, 0, ServerRaceConfig.MaxRacers - 1, "bots", out value, out error);
    }

    static bool ParseInt(string text, int min, int max, string name, out int value, out string error)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} expected a number in {min}..{max}";
            return false;
        }
        if (value < min || value > max)
        {
            error = $"{name} must be in {min}..{max}";
            return false;
        }
        error = "";
        return true;
    }

    static bool ParseYesNo(string text, out bool value, out string error) =>
        TryParseYesNo(text, out value, out error);

    static bool TryParseYesNo(string text, out bool value, out string error)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "yes" or "y" or "true" or "1": value = true; error = ""; return true;
            case "no" or "n" or "false" or "0": value = false; error = ""; return true;
            default: value = false; error = "expected yes or no"; return false;
        }
    }

    // ---------------- lobby commands ----------------

    /// <summary>Parses one console line. An empty line is <see cref="ServerCommandKind.None"/>; unknown commands/arguments
    /// return false with an explanation for the operator.</summary>
    public static bool TryParseCommand(string? line, out ServerCommand command, out string error)
    {
        command = new ServerCommand(ServerCommandKind.None);
        error = "";
        var parts = (line ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        string name = parts[0].ToLowerInvariant();
        switch (name)
        {
            case "bots":
                if (parts.Length == 1) { command = new ServerCommand(ServerCommandKind.Status); return true; }
                if (parts.Length > 2)
                {
                    error = "usage: bots N (0..7) | bots auto";
                    return false;
                }
                if (!ParseBots(parts[1], out int setting, out error)) return false;
                command = new ServerCommand(ServerCommandKind.Bots, setting);
                return true;
            case "track":
                if (parts.Length == 1) { command = new ServerCommand(ServerCommandKind.Status); return true; }
                if (parts.Length > 2)
                {
                    error = "usage: track N (0..7)";
                    return false;
                }
                if (!ParseTrack(parts[1], out int track, out error)) return false;
                command = new ServerCommand(ServerCommandKind.Track, Track: track);
                return true;
            case "direction" or "dir":
                if (parts.Length == 1) { command = new ServerCommand(ServerCommandKind.Status); return true; }
                if (parts.Length > 2)
                {
                    error = "usage: direction forward|backward";
                    return false;
                }
                if (!ParseDirection(parts[1], out bool backward, out error)) return false;
                command = new ServerCommand(ServerCommandKind.Direction, Backward: backward);
                return true;
            case "bot":
                if (parts.Length != 2)
                {
                    error = "usage: bot + | bot -";
                    return false;
                }
                switch (parts[1])
                {
                    case "+" or "add": command = new ServerCommand(ServerCommandKind.BotAdd); return true;
                    case "-" or "remove" or "del": command = new ServerCommand(ServerCommandKind.BotRemove); return true;
                    default: error = "usage: bot + | bot -"; return false;
                }
        }
        if (parts.Length > 1)
        {
            error = $"'{name}' takes no arguments (commands: start, next, status, help, quit, bots N, bot +/-, track N, direction forward|backward)";
            return false;
        }
        switch (name)
        {
            case "start" or "go": command = new ServerCommand(ServerCommandKind.Start); return true;
            case "next" or "again": command = new ServerCommand(ServerCommandKind.Next); return true;
            case "status" or "lobby" or "players": command = new ServerCommand(ServerCommandKind.Status); return true;
            case "help" or "?": command = new ServerCommand(ServerCommandKind.Help); return true;
            case "quit" or "exit": command = new ServerCommand(ServerCommandKind.Quit); return true;
            default:
                error = $"unknown command '{name}' (start, next, status, help, quit, bots N, bot +/-, track N, direction forward|backward)";
                return false;
        }
    }

    /// <summary>Multi-line lobby text for the operator: phase, race options, then every seat with its car and readiness.</summary>
    public static string FormatLobby(ServerLobbyState lobby)
    {
        var sb = new StringBuilder();
        sb.Append($"lobby phase={lobby.Phase} track=TR{lobby.Track:00} direction={(lobby.Backward ? "backward" : "forward")} laps={lobby.Laps} bots={lobby.Bots}");
        if (lobby.BotCap >= 0)
            sb.Append(lobby.BotSetting == ServerRaceConfig.AutoBots
                ? $" (auto, cap {lobby.BotCap})"
                : $" (requested {lobby.BotSetting}, cap {lobby.BotCap})");
        sb.Append($" players={lobby.Connected}/{lobby.Seats.Count} min={lobby.MinPlayers} ready={lobby.Ready}/{lobby.Connected}");
        if (lobby.CanStart) sb.Append(" - type 'start'");
        foreach (var seat in lobby.Seats)
        {
            sb.Append('\n');
            sb.Append($"  seat {seat.Seat}: car {(seat.Connected ? seat.CarId.ToString(CultureInfo.InvariantCulture) : "-")}");
            if (seat.Model != null) sb.Append($" {seat.Model}");
            sb.Append($" {seat.Name ?? "(open)"}");
            sb.Append(seat.Connected ? (seat.Ready ? " ready" : " not ready") : " empty (no human; bots are added after humans)");
        }
        return sb.ToString();
    }

    /// <summary>One compact line of live round progress for `status`: phase, race clock from the host's whole steps
    /// (<see cref="NetProtocol.StepHz"/> per second), simulation tick and the car counts of the current grid. The server
    /// console prints no periodic heartbeat, so this is what the operator asks for while a race runs.</summary>
    public static string FormatProgress(NetRacePhase phase, int steps, int tick, int humans, int racers) =>
        $"race phase={phase} step={steps} ({(steps / (double)NetProtocol.StepHz).ToString("0.0", CultureInfo.InvariantCulture)} s) " +
        $"tick={tick} humans={humans} racers={racers}";
}
