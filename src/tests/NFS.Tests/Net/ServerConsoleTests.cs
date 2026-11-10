using System.Net;
using System.Text;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Tests.Net;

/// <summary>Interactive dedicated-server console (TASKS item 15, first stage): configuration validation and confirmation
/// before listening, operator command parsing and lobby text. No game data, sockets or console needed.</summary>
public class ServerConsoleTests
{
    static bool Read(IEnumerable<string> lines, out ServerConsoleConfig? config, out string error, out string output)
    {
        var queue = new Queue<string>(lines);
        var text = new StringBuilder();
        Func<string?> read = () => queue.Count > 0 ? queue.Dequeue() : null;
        bool ok = ServerConsole.TryReadConfig(read, s => text.Append(s), out config, out error);
        output = text.ToString();
        return ok;
    }

    [Fact]
    public void DefaultsAreWithinTheSupportedConfigurationAndNeedConfirmation()
    {
        // A true range now: the minimum is one human, the maximum is the full eight-seat grid. The bot setting defaults to
        // auto (fill the grid); traffic is only asked for the split-screen pair.
        Assert.True(Read(["", "", "", "", "", "", "", "", "", "yes"], out var cfg, out var error, out var output), error);
        Assert.Equal("", error);
        Assert.NotNull(cfg);
        Assert.Equal("single", cfg.Mode);
        Assert.Equal(IPAddress.Loopback, cfg.Bind);
        Assert.Equal(0, cfg.Port);
        Assert.Equal(1, cfg.Track);
        Assert.Equal(2, cfg.Laps);
        Assert.Equal((1, 8, ServerRaceConfig.AutoBots), (cfg.MinPlayers, cfg.MaxPlayers, cfg.Bots));
        Assert.False(cfg.Traffic);
        Assert.False(cfg.Backward);
        var race = cfg.ToRaceConfig();
        Assert.Equal(new ServerRaceConfig(1, 2, 2, false, 8) { MinimumPlayers = 1, BotSetting = ServerRaceConfig.AutoBots }, race);
        Assert.Equal(0, race.BotCount); // a full lobby of eight humans has no free grid slot
        Assert.False(race.Backward);
        Assert.Contains("start listening? (yes/no)", output);
        Assert.Contains("players=1..8", output);
        Assert.Contains("bots=auto planned=0 cap=0", output);
        Assert.Contains("bots 0..7 or auto", output);
        Assert.Contains("direction=forward", output);
    }

    [Fact]
    public void InvalidAnswersAreExplainedAndAskedAgain()
    {
        string[] lines =
        [
            "knockout", "single",          // unsupported mode, then single race
            "not-an-ip", "0.0.0.0",        // bind
            "70000", "27100",              // port range
            "9", "3",                      // track range
            "sideways", "forward",         // direction: invalid, then forward
            "0", "1",                      // laps range
            "0", "3",                      // min players: below 1, then 3
            "2",                           // max below min: the pair is asked again
            "3", "3",                      // min 3, max 3
            "8", "3",                      // bots above 7, then an explicit 3
            "y",                           // confirmation (traffic is only asked for a 2-seat capacity)
        ];
        Assert.True(Read(lines, out var cfg, out var error, out var output), error);
        Assert.NotNull(cfg);
        Assert.Equal(IPAddress.Any, cfg.Bind);
        Assert.Equal(27100, cfg.Port);
        Assert.Equal(3, cfg.Track);
        Assert.Equal(1, cfg.Laps);
        Assert.Equal((3, 3, 3), (cfg.MinPlayers, cfg.MaxPlayers, cfg.Bots));
        Assert.Equal(3, cfg.ToRaceConfig().BotCount);
        Assert.False(cfg.Traffic);
        Assert.False(cfg.Backward);
        Assert.Contains("knockout is not implemented", output);
        Assert.Contains("human seats must be in 1..8", output);
        Assert.Contains("max players (2) must not be below min players (3)", output);
        Assert.Contains("expected forward or backward", output);
        Assert.Contains("bots=3 planned=3 cap=5", output);
        Assert.Contains("traffic=off", output);
        Assert.True(Count(output, "invalid:") >= 9, output);
    }

    [Fact]
    public void ExactHigherPlayerCountIsAcceptedAndUsedByTheRaceConfig()
    {
        Assert.True(Read(["", "", "", "", "", "", "4", "4", "", "yes"], out var cfg, out var error, out var output), error);
        Assert.NotNull(cfg);
        Assert.Equal((4, 4), (cfg.MinPlayers, cfg.MaxPlayers));
        Assert.Equal(4, cfg.Players);
        var race = cfg.ToRaceConfig();
        Assert.Equal(4, race.Players);
        Assert.Equal(4, race.EffectivePlayers);
        Assert.Equal(4, race.BotCount); // eight-car grid: the other four slots are AI
        Assert.Contains("players=4", output);
        Assert.Contains("bots=auto planned=4 cap=4", output);
    }

    [Fact]
    public void SingleSeatRaceUsesTheFullEightCarLineup()
    {
        // The minimum is one human: the operator may configure a one-seat race, whose remaining seven cars are AI.
        Assert.True(Read(["", "", "", "", "", "", "1", "1", "", "yes"], out var cfg, out var error, out var output), error);
        Assert.NotNull(cfg);
        Assert.Equal((1, 1, ServerRaceConfig.AutoBots), (cfg.MinPlayers, cfg.MaxPlayers, cfg.Bots));
        var race = cfg.ToRaceConfig();
        Assert.Equal(1, race.EffectivePlayers);
        Assert.Equal(1, race.EffectiveMinimumPlayers);
        Assert.Equal(7, race.BotCount);
        Assert.Contains("players=1..1", output);
        Assert.Contains("bots=auto planned=7 cap=7", output);
    }

    [Fact]
    public void ExplicitBotSettingIsCappedByTheConfiguredCapacity()
    {
        // A 3-seat capacity has room for five AI cars; asking for seven stores the request and shows the cap.
        Assert.True(Read(["", "", "", "", "", "", "1", "3", "7", "yes"], out var cfg, out var error, out var output), error);
        Assert.NotNull(cfg);
        Assert.Equal(7, cfg.Bots);
        var race = cfg.ToRaceConfig();
        Assert.Equal(7, race.BotSetting);
        Assert.Equal(5, race.BotsFor(3));
        Assert.Equal(7, race.BotsFor(1));
        Assert.Contains("bots=7 planned=5 cap=5", output);
    }

    [Fact]
    public void HighBotRequestWithAHigherMinimumIsCappedInsteadOfRejected()
    {
        // A saved setting of 7 is valid for a 3-human minimum: the capacity lobby has no free slot (8 humans), and a start
        // with three humans plans five bots. The request must never make host composition throw.
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 3, BotSetting = 7 };
        Assert.Equal(5, race.BotsFor(3));
        Assert.Equal(0, race.BotsFor(8));
        Assert.Equal(0, race.BotCount);
    }

    [Fact]
    public void MismatchedMinAndMaxAreExplainedAndThePairIsAskedAgain()
    {
        // min 5, max 3 (max below min, re-ask), min 2, max 2, then bots out of range and the traffic default.
        Assert.True(Read(["", "", "", "", "", "", "5", "3", "2", "2", "9", "3", "", "yes"], out var cfg, out var error, out var output), error);
        Assert.NotNull(cfg);
        Assert.Equal((2, 2, 3), (cfg.MinPlayers, cfg.MaxPlayers, cfg.Bots));
        Assert.Contains("max players (3) must not be below min players (5)", output);
        Assert.Contains("bots must be in 0..7", output);
        Assert.True(Count(output, "invalid:") >= 2, output);
    }

    [Fact]
    public void UnconfirmedConfigurationIsNotAccepted()
    {
        Assert.False(Read(["", "", "", "", "", "", "", "", "", "no"], out var cfg, out var error, out _));
        Assert.Null(cfg);
        Assert.Contains("not confirmed", error);
    }

    [Fact]
    public void ClosedInputIsNotAccepted()
    {
        Assert.False(Read([], out var cfg, out var error, out _));
        Assert.Null(cfg);
        Assert.Contains("closed", error);
    }

    [Theory]
    [InlineData("start", ServerCommandKind.Start)]
    [InlineData(" START ", ServerCommandKind.Start)]
    [InlineData("go", ServerCommandKind.Start)]
    [InlineData("next", ServerCommandKind.Next)]
    [InlineData("again", ServerCommandKind.Next)]
    [InlineData("status", ServerCommandKind.Status)]
    [InlineData("lobby", ServerCommandKind.Status)]
    [InlineData("players", ServerCommandKind.Status)]
    [InlineData("help", ServerCommandKind.Help)]
    [InlineData("?", ServerCommandKind.Help)]
    [InlineData("quit", ServerCommandKind.Quit)]
    [InlineData("exit", ServerCommandKind.Quit)]
    [InlineData("bots 3", ServerCommandKind.Bots)]
    [InlineData("BOTS 0", ServerCommandKind.Bots)]
    [InlineData("bots auto", ServerCommandKind.Bots)]
    [InlineData("bots full", ServerCommandKind.Bots)]
    [InlineData("bots", ServerCommandKind.Status)]
    [InlineData("track 4", ServerCommandKind.Track)]
    [InlineData("TRACK 0", ServerCommandKind.Track)]
    [InlineData("track", ServerCommandKind.Status)]
    [InlineData("direction forward", ServerCommandKind.Direction)]
    [InlineData("dir backward", ServerCommandKind.Direction)]
    [InlineData("direction", ServerCommandKind.Status)]
    [InlineData("bot +", ServerCommandKind.BotAdd)]
    [InlineData("bot add", ServerCommandKind.BotAdd)]
    [InlineData("bot -", ServerCommandKind.BotRemove)]
    [InlineData("bot remove", ServerCommandKind.BotRemove)]
    [InlineData("", ServerCommandKind.None)]
    public void ConsoleCommandsAreParsed(string line, ServerCommandKind expected)
    {
        Assert.True(ServerConsole.TryParseCommand(line, out var command, out string error), error);
        Assert.Equal(expected, command.Kind);
    }

    [Theory]
    [InlineData("bots 0", 0)]
    [InlineData("bots 7", 7)]
    [InlineData("bots auto", ServerRaceConfig.AutoBots)]
    [InlineData("bots full", ServerRaceConfig.AutoBots)]
    [InlineData("bots -1", ServerRaceConfig.AutoBots)]
    public void BotCommandsCarryTheRequestedSetting(string line, int expected)
    {
        Assert.True(ServerConsole.TryParseCommand(line, out var command, out string error), error);
        Assert.Equal(ServerCommandKind.Bots, command.Kind);
        Assert.Equal(expected, command.Bots);
    }

    [Theory]
    [InlineData("bots 8")]
    [InlineData("bots -2")]
    [InlineData("bots many")]
    [InlineData("bots 1 2")]
    [InlineData("bot")]
    [InlineData("bot ++")]
    [InlineData("bot + 2")]
    public void InvalidBotCommandsAreExplained(string line)
    {
        Assert.False(ServerConsole.TryParseCommand(line, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("track 0", 0)]
    [InlineData("track 7", 7)]
    [InlineData("track 3 ", 3)]
    public void TrackCommandCarriesTheRequestedTrack(string line, int expected)
    {
        Assert.True(ServerConsole.TryParseCommand(line, out var command, out string error), error);
        Assert.Equal(ServerCommandKind.Track, command.Kind);
        Assert.Equal(expected, command.Track);
    }

    [Theory]
    [InlineData("track 8")]
    [InlineData("track -1")]
    [InlineData("track west")]
    [InlineData("track 1 2")]
    public void InvalidTrackCommandsAreExplained(string line)
    {
        Assert.False(ServerConsole.TryParseCommand(line, out _, out string error));
        Assert.Contains("track", error);
        Assert.Contains("0..7", error);
    }

    [Theory]
    [InlineData("direction forward", false)]
    [InlineData("direction fwd", false)]
    [InlineData("dir f", false)]
    [InlineData("direction backward", true)]
    [InlineData("direction back", true)]
    [InlineData("dir bwd", true)]
    [InlineData("dir b", true)]
    [InlineData("direction reverse", true)]
    public void DirectionCommandCarriesTheRequestedDirection(string line, bool expected)
    {
        Assert.True(ServerConsole.TryParseCommand(line, out var command, out string error), error);
        Assert.Equal(ServerCommandKind.Direction, command.Kind);
        Assert.Equal(expected, command.Backward);
    }

    [Theory]
    [InlineData("direction sideways")]
    [InlineData("direction 0")]
    [InlineData("direction 1 2")]
    [InlineData("direction forward back")]
    public void InvalidDirectionCommandsAreExplained(string line)
    {
        Assert.False(ServerConsole.TryParseCommand(line, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("start now")]
    [InlineData("kick 1")]
    [InlineData("status now")]
    [InlineData("next now")]
    public void CommandsWithArgumentsAreRejected(string line)
    {
        Assert.False(ServerConsole.TryParseCommand(line, out _, out string error));
        Assert.Contains("takes no arguments", error);
    }

    [Fact]
    public void UnknownCommandsAreRejectedWithTheCommandList()
    {
        Assert.False(ServerConsole.TryParseCommand("warp", out _, out string error));
        Assert.Contains("unknown command", error);
        Assert.Contains("start", error);
        Assert.Contains("next", error);
    }

    [Fact]
    public void LobbyTextShowsPhaseSeatsCarsReadinessAndBots()
    {
        var lobby = new ServerLobbyState(NetRacePhase.Loading, 3, 2, 2, 2,
        [
            new ServerLobbySeat(0, "alice", 4, "F355", Connected: true, Ready: true),
            new ServerLobbySeat(1, null, 6, "MCLAREN", Connected: false, Ready: false),
        ]);
        Assert.Equal(1, lobby.Connected);
        Assert.Equal(1, lobby.Ready);
        Assert.False(lobby.CanStart); // the minimum is two humans

        string text = ServerConsole.FormatLobby(lobby);
        Assert.Contains("phase=Loading", text);
        Assert.Contains("track=TR03", text);
        Assert.Contains("direction=forward", text);
        Assert.Contains("laps=2", text);
        Assert.Contains("bots=2", text);
        Assert.Contains("players=1/2 min=2 ready=1/1", text);
        Assert.Contains("seat 0: car 4 F355 alice ready", text);
        Assert.Contains("seat 1: car - MCLAREN (open) empty (no human; bots are added after humans)", text);
    }

    [Fact]
    public void LobbyTextShowsThePlannedBotsForTheConnectedHumansAndTheCap()
    {
        ServerLobbySeat[] Seats(int connected) => Enumerable.Range(0, 3)
            .Select(i => new ServerLobbySeat(i, i < connected ? $"p{i}" : null, i, null, i < connected, i < connected))
            .ToArray();

        // Auto: two humans, so the plan fills the remaining six grid slots.
        var auto = new ServerLobbyState(NetRacePhase.Loading, 1, 2, 6, 1, Seats(2),
            ServerRaceConfig.AutoBots, 6);
        string text = ServerConsole.FormatLobby(auto);
        Assert.Contains("bots=6 (auto, cap 6)", text);

        // Explicit request above the cap: the actual plan is capped, and the request stays visible.
        var requested = new ServerLobbyState(NetRacePhase.Loading, 1, 2, 6, 1, Seats(2), 7, 6);
        text = ServerConsole.FormatLobby(requested);
        Assert.Contains("bots=6 (requested 7, cap 6)", text);

        // Three humans leave room for five AI cars; an explicit 3 is planned as 3.
        var three = new ServerLobbyState(NetRacePhase.Loading, 1, 2, 3, 1, Seats(3), 3, 5);
        text = ServerConsole.FormatLobby(three);
        Assert.Contains("bots=3 (requested 3, cap 5)", text);
        Assert.Contains("players=3/3", text);
    }

    [Fact]
    public void OneReadyHumanOfTwoCanStartAndTheEmptySeatIsAnAiBot()
    {
        var lobby = new ServerLobbyState(NetRacePhase.WaitingForPlayers, 3, 2, 0, 1,
        [
            new ServerLobbySeat(0, "alice", 4, "F355", Connected: true, Ready: true),
            new ServerLobbySeat(1, null, 6, "MCLAREN", Connected: false, Ready: false),
        ]);
        Assert.True(lobby.CanStart);
        string text = ServerConsole.FormatLobby(lobby);
        Assert.Contains("players=1/2 min=1 ready=1/1 - type 'start'", text);
        Assert.Contains("empty (no human; bots are added after humans)", text);
    }

    [Fact]
    public void ConnectedButUnreadyPlayerBlocksTheStart()
    {
        var lobby = new ServerLobbyState(NetRacePhase.Loading, 3, 2, 6, 1,
        [
            new ServerLobbySeat(0, "alice", 4, "F355", Connected: true, Ready: true),
            new ServerLobbySeat(1, "bob", 6, "MCLAREN", Connected: true, Ready: false),
        ]);
        Assert.Equal(1, lobby.Ready);
        Assert.False(lobby.CanStart);
        Assert.DoesNotContain("type 'start'", ServerConsole.FormatLobby(lobby));
    }

    [Fact]
    public void StatusProgressLineShowsThePhaseRaceClockTickAndCars()
    {
        // 32 whole server steps = one second at the protocol's 32 Hz host rate.
        Assert.Equal("race phase=Racing step=1040 (32.5 s) tick=2080 humans=2 racers=8",
            ServerConsole.FormatProgress(NetRacePhase.Racing, 1040, 2080, 2, 8));
        Assert.Equal("race phase=Countdown step=0 (0.0 s) tick=512 humans=1 racers=5",
            ServerConsole.FormatProgress(NetRacePhase.Countdown, 0, 512, 1, 5));
    }

    static int Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
