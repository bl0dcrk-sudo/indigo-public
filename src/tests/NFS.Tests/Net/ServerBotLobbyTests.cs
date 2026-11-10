using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Operator-controlled AI opponents in the dedicated lobby (TASKS item 15): `bots N` / `bots auto` / `bot +/-`
/// change the number of real class-2 AI racing cars composed at `start` (and therefore the result rows), capped so humans +
/// bots never exceeds the eight-car grid. The setting follows joins/leaves and survives `next`; it is refused while a race
/// is running.</summary>
public class ServerBotLobbyTests
{
    const int StartStep = 256;

    static RaceServerOptions Options(ServerRaceConfig race, int raceSteps) => new()
    {
        ManualStart = true,
        MaxRaceSteps = raceSteps,
        WaitTimeoutMicros = 60_000_000,
        LingerMicros = 0, // an interactive server keeps the result until `next` or `quit`
        BotCount = race.BotCount,
        BotSetting = race.BotSetting,
        MinPlayers = race.EffectiveMinimumPlayers,
    };

    static NetHarness Harness(int seed, ServerRaceConfig race, int raceSteps) => new(
        seed, Options(race, raceSteps), race,
        hostFactory: (id, humans) => race.CreateHost(GameData.Dir, id, humans),
        hostFactoryBots: (id, humans, bots) => race.CreateHost(GameData.Dir, id, humans, bots));

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static bool Started(NetHarness h, params RaceClient[] clients) => h.RunUntil(() =>
        (h.Server.Phase is NetRacePhase.Countdown or NetRacePhase.Racing) &&
        clients.All(c => c.Welcome!.RaceId == h.Server.RaceId && c.Baseline!.RaceId == h.Server.RaceId),
        10_000_000);

    static void AssertGrid(RaceHost host, int humans, int bots)
    {
        var setup = host.Runner.Session.Setup;
        Assert.Equal(humans, host.Seats);
        Assert.Equal(humans + bots, setup.NumCars);
        Assert.Equal(humans, setup.NumPlayerCars);
        Assert.Equal(bots, setup.NumOpponentCars);
        Assert.Equal(humans, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 1));
        Assert.Equal(bots, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 2));
        Assert.Equal(bots, host.Runner.Cars.Count(car => car.Ai != null));
    }

    [GameTheory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    [InlineData(7, 8)]
    public void OneHumanCanRaceWithZeroThreeOrSevenRealAiCars(int bots, int totalCars)
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        using var h = Harness(9200 + bots, race, StartStep + 80);
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.Equal(7, h.Server.PlannedBots); // auto fills the grid for one human
        Assert.Equal(7, h.Server.BotCap);
        Assert.True(h.Server.TrySetBotSetting(bots, out string setError), setError);
        Assert.Equal(bots, h.Server.BotSetting);
        Assert.Equal(bots, h.Server.PlannedBots);
        Assert.Equal(bots, h.Server.Lobby.Bots);
        Assert.Equal(7, h.Server.Lobby.BotCap);

        Assert.True(h.Server.TryOperatorStart(out string error), error);
        AssertGrid(h.Server.Host, 1, bots);
        Assert.True(Started(h, alice), Tail(h));
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));

        var results = h.Server.Host.Results!;
        Assert.Equal(totalCars, results.Racers.Count);
        Assert.Equal(1, results.Racers.Count(r => r.Human));
        Assert.Equal(bots, results.Racers.Count(r => !r.Human));
    }

    [GameFact]
    public void ThreeHumansAskingForSevenBotsAreCappedAtFive()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 3 };
        using var h = Harness(9301, race, StartStep + 60);
        var clients = new[]
        {
            h.Join("alice", LinkProfile.Rtt(20)), h.Join("bob", LinkProfile.Rtt(20)), h.Join("carol", LinkProfile.Rtt(20)),
        };
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TrySetBotSetting(7, out string error), error);
        Assert.Equal(7, h.Server.BotSetting);
        Assert.Equal(5, h.Server.BotCap);
        Assert.Equal(5, h.Server.PlannedBots);
        var lobby = h.Server.Lobby;
        Assert.Equal(5, lobby.Bots);
        Assert.Equal(7, lobby.BotSetting);
        Assert.Equal(5, lobby.BotCap);

        Assert.True(h.Server.TryOperatorStart(out error), error);
        AssertGrid(h.Server.Host, 3, 5);
        Assert.True(Started(h, clients), Tail(h));
        Assert.True(h.RunUntil(() => clients.All(c => c.State == RaceClientState.Finished), 40_000_000), Tail(h));
        Assert.Equal(8, h.Server.Host.Results!.Racers.Count);
        Assert.Equal(3, h.Server.Host.Results!.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void BotPlanFollowsJoinsLeavesAndThePlusMinusCommands()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        using var h = Harness(9302, race, StartStep + 40);
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.Equal(7, h.Server.PlannedBots); // auto with one human

        Assert.True(h.Server.TrySetBotSetting(3, out string error), error);
        Assert.Equal(3, h.Server.PlannedBots);

        // A join raises the cap but an explicit request stays the same; a leave lowers the cap again.
        h.Join("bob", LinkProfile.Rtt(20));
        Assert.True(h.RunUntil(() => h.Server.ConnectedPlayers == 2, 5_000_000), Tail(h));
        Assert.Equal(3, h.Server.PlannedBots);
        Assert.Equal(6, h.Server.BotCap);
        h.Clients[1].Link.Disconnect();
        Assert.True(h.RunUntil(() => h.Server.ConnectedPlayers == 1, 5_000_000), Tail(h));
        Assert.Equal(3, h.Server.PlannedBots);
        Assert.Equal(7, h.Server.BotCap);

        // `bot +` / `bot -` move relative to the plan; `bots auto` returns to filling the grid.
        Assert.True(h.Server.TryAddBot(out error), error);
        Assert.Equal(4, h.Server.BotSetting);
        Assert.Equal(4, h.Server.PlannedBots);
        Assert.True(h.Server.TryRemoveBot(out error), error);
        Assert.Equal(3, h.Server.BotSetting);
        Assert.Equal(3, h.Server.PlannedBots);
        Assert.True(h.Server.TrySetBotSetting(ServerRaceConfig.AutoBots, out error), error);
        Assert.Equal(7, h.Server.PlannedBots);

        h.Join("bob", LinkProfile.Rtt(20));
        Assert.True(h.RunUntil(() => h.Server.ConnectedPlayers == 2, 5_000_000), Tail(h));
        Assert.Equal(6, h.Server.PlannedBots); // auto follows the second human
        Assert.False(h.Server.TryAddBot(out string addError)); // already full
        Assert.Contains("auto already fills the grid", addError);
        Assert.False(h.Server.TrySetBotSetting(8, out _));
        Assert.Equal(ServerRaceConfig.AutoBots, h.Server.BotSetting);
    }

    [GameFact]
    public void NextRoundUsesTheBotCountSelectedBetweenRounds()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        using var h = Harness(9303, race, StartStep + 50);
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        var bob = h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TrySetBotSetting(2, out string error), error);
        Assert.True(h.Server.TryOperatorStart(out error), error);
        AssertGrid(h.Server.Host, 2, 2);
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished && bob.State == RaceClientState.Finished,
            40_000_000), Tail(h));
        Assert.Equal(4, h.Server.Host.Results!.Racers.Count);

        Assert.True(h.Server.TryOperatorNext(out error), error);
        // Between rounds the setting can be changed and applies to the freshly composed race.
        Assert.True(h.Server.TrySetBotSetting(4, out error), error);
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart &&
                                     alice.Welcome!.RaceId == h.Server.RaceId && bob.Welcome!.RaceId == h.Server.RaceId,
            5_000_000), Tail(h));
        Assert.Equal(4, h.Server.PlannedBots);
        Assert.True(h.Server.TryOperatorStart(out error), error);
        AssertGrid(h.Server.Host, 2, 4);
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished && bob.State == RaceClientState.Finished,
            40_000_000), Tail(h));
        var results = h.Server.Host.Results!;
        Assert.Equal(6, results.Racers.Count);
        Assert.Equal(2, results.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void BotChangesAreRefusedWhileRacingAndAcceptedAfterTheResult()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        using var h = Harness(9304, race, StartStep + 40);
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TrySetBotSetting(1, out string error), error);
        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.True(Started(h, alice), Tail(h));

        Assert.False(h.Server.TrySetBotSetting(5, out string racingError));
        Assert.Contains("race", racingError);
        Assert.False(h.Server.TryAddBot(out _));
        Assert.False(h.Server.TryRemoveBot(out _));
        Assert.Equal(1, h.Server.BotSetting);

        Assert.True(h.RunUntil(() => h.Server.Phase is NetRacePhase.Finished or NetRacePhase.Aborted, 40_000_000), Tail(h));
        Assert.True(h.Server.TrySetBotSetting(5, out error), error);
        Assert.Equal(5, h.Server.BotSetting);
    }
}
