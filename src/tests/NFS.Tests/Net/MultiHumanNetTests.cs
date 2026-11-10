using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Net;

/// <summary>Stage 5a.3b: human counts 1..8 on the dedicated server. The race configuration turns the remaining grid slots into
/// human seats, the server schedules one input per seat and every client drives its own car; the default two-player
/// configuration keeps the legacy split-screen setup unchanged.</summary>
public class MultiHumanNetTests
{
    /// <summary>Green light at host step 256 (tick 0x200), as in <see cref="NetRaceTests"/>.</summary>
    const int StartStep = 256;

    static RaceServerOptions Bounded(int extraSteps = 160) =>
        new() { MaxRaceSteps = StartStep + extraSteps, LingerMicros = 3_000_000 };

    static string Key(RaceResults r) => string.Join("|", r.Racers.Select(x =>
        $"{x.Place},{x.CarIndex},{x.Model},{x.Human},{x.Estimated},{x.RaceTicks},{string.Join(".", x.LapTicks)},{x.BestLapTicks},{x.RaceState},{x.Driver},{x.TopSpeedFx}"));

    static bool Moved(CarObj car, Vec3 start) =>
        Math.Abs(car.Pos.X - start.X) > Fx.One || Math.Abs(car.Pos.Z - start.Z) > Fx.One;

    [Fact]
    public void RosterCarriesEightFullLengthNamesAndReadySeats()
    {
        var names = Enumerable.Range(0, 8).ToDictionary(i => i, i => $"player{i:D2}name");
        var original = new NetRoster(42, 7, names, Enumerable.Range(0, 8).ToArray());
        var encoded = original.Encode();
        var reader = new WireReader(encoded);
        var header = NetHeader.Read(ref reader);
        var decoded = NetRoster.Decode(header, ref reader);
        Assert.Equal(names.OrderBy(x => x.Key), decoded.Players.OrderBy(x => x.Key));
        Assert.Equal(Enumerable.Range(0, 8), decoded.ReadySeats);
    }

    [Fact]
    public void ServerConfigAcceptsOneToEightHumanSeatsAndValidatesTheMinimum()
    {
        Assert.Equal(1, new ServerRaceConfig(Players: 1).EffectivePlayers); // one human is enough to start a dedicated race
        Assert.Equal(1, new ServerRaceConfig(Players: 1).EffectiveMinimumPlayers); // unset = exact count
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerRaceConfig(Players: 0).EffectivePlayers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerRaceConfig(Players: 9).EffectivePlayers);
        Assert.Equal(1, new ServerRaceConfig(Players: 3) { MinimumPlayers = 1 }.EffectiveMinimumPlayers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerRaceConfig(Players: 3) { MinimumPlayers = 0 }.EffectiveMinimumPlayers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServerRaceConfig(Players: 3) { MinimumPlayers = 4 }.EffectiveMinimumPlayers);
    }

    [GameTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Setup_TurnsTheExactNumberOfGridSlotsIntoHumans(int players)
    {
        var setup = new ServerRaceConfig(Players: players).BuildSetup(GameData.Dir);
        Assert.Equal(8, setup.NumCars);
        Assert.Equal(players, setup.NumPlayerCars);
        Assert.Equal(8 - players, setup.NumOpponentCars);
        Assert.Equal(players, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 1));
        Assert.Equal(8 - players, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 2));
        // every extra human drives with the same control filter as the menu's player 1
        for (int i = 2; i < players; i++)
            Assert.Equal((setup.Car(0).SteerRamp, setup.Car(0).SlowThrottle, setup.Car(0).BrakeRamp),
                (setup.Car(i).SteerRamp, setup.Car(i).SlowThrottle, setup.Car(i).BrakeRamp));
    }

    [GameTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Host_HasExactlyTheConfiguredHumanSeatsAndTheRestAreAi(int players)
    {
        using var host = new ServerRaceConfig(Players: players).CreateHost(GameData.Dir, 0x51E7 + players);
        Assert.Equal(players, host.Seats);
        Assert.Equal(players, host.Runner.HumanSims.Count);
        Assert.Equal(Enumerable.Range(0, players), host.View.Description.LocalSeats);
        Assert.All(host.Runner.HumanSims, car => Assert.Null(car.Ai));
        Assert.Equal(8 - players, host.Runner.Cars.Count(car => car.Ai != null));
        Assert.Equal(players, host.Runner.Road.HumanCount);
        Assert.Equal(0x51E7 + players, host.View.RaceId);
    }

    [GameTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void EveryHumanSeatDrivesAnAutomaticGearbox_RegardlessOfTheHostsConfigDat(int players)
    {
        // The wire protocol has no gearbox choice; a manual player 1 in the host's CONFIG.DAT used to leave whichever client got
        // seat 0 in neutral (seat assignment is random), which made input-driven tests flaky.
        var setup = new ServerRaceConfig(Players: players).BuildSetup(GameData.Dir);
        for (int i = 0; i < players; i++) Assert.Equal(1, setup.Car(i).Transmission);
        using var host = new ServerRaceConfig(Players: players).CreateHost(GameData.Dir, 0x7A00 + players);
        Assert.All(host.Runner.HumanSims, sim => Assert.True(sim.Automatic));
    }

    [GameFact]
    public void FullHumanGridKeepsTheEightCarLineupWithoutTraffic()
    {
        // The menu emits traffic only when the opponent count is not the split-screen pair; a race with more than two
        // humans always uses that eight-car lineup, so a traffic request cannot add class-4 cars beyond the grid.
        var setup = new ServerRaceConfig(Players: 4, Opponents: 0, Traffic: true).BuildSetup(GameData.Dir);
        Assert.Equal(8, setup.NumCars);
        Assert.Equal(4, setup.NumPlayerCars);
        Assert.Equal(4, setup.NumOpponentCars);
        Assert.DoesNotContain(Enumerable.Range(0, setup.NumCars), i => setup.Car(i).CarClass == 4);
    }

    [GameFact]
    public void SingleHumanConfiguration_UsesTheFullLineupWithSevenAiOpponents()
    {
        var race = new ServerRaceConfig(Players: 1);
        Assert.Equal(1, race.EffectivePlayers);
        Assert.Equal(7, race.BotCount);
        var setup = race.BuildSetup(GameData.Dir);
        Assert.Equal(8, setup.NumCars);
        Assert.Equal(1, setup.NumPlayerCars);
        Assert.Equal(7, setup.NumOpponentCars);
        using var host = race.CreateHost(GameData.Dir, 777);
        Assert.Equal(1, host.Seats);
        Assert.Equal(1, host.Runner.Road.HumanCount);
        Assert.Equal(7, host.Runner.Cars.Count(car => car.Ai != null));
        Assert.DoesNotContain(host.Runner.Cars, car => car.Ai == null && !ReferenceEquals(car, host.Runner.HumanSims[0]));
    }

    [GameFact]
    public void DefaultConfiguration_StillBuildsTheLegacyTwoPlayerSplitScreenRace()
    {
        var race = new ServerRaceConfig();
        Assert.Equal(2, race.Players);
        Assert.Equal(2, race.EffectivePlayers);
        Assert.Equal(6, race.BotCount);

        var setup = race.BuildSetup(GameData.Dir);
        Assert.Equal(8, setup.NumCars);
        Assert.Equal(2, setup.NumPlayerCars);
        Assert.Equal(6, setup.NumOpponentCars);
        Assert.Equal(2, Enumerable.Range(0, 8).Count(i => setup.Car(i).CarClass == 1));
        Assert.Equal(6, Enumerable.Range(0, 8).Count(i => setup.Car(i).CarClass == 2));

        using var host = race.CreateHost(GameData.Dir, 1234);
        Assert.Equal(2, host.Seats);
        Assert.NotNull(host.Runner.Player2);
        Assert.Same(host.Runner.Player2Sim, host.Runner.HumanSims[1]);
        Assert.Same(host.Runner.Player2!.Obj, host.Runner.Humans[1]);
    }

    [GameFact]
    public void TwoClients_OnTheDefaultConfiguration_RaceTheOldTwoSeatRace()
    {
        using var h = new NetHarness(202, Bounded(96));
        Assert.Equal(2, h.Host.Seats);
        Assert.NotNull(h.Host.Runner.Player2);
        var a = h.Join("alice", LinkProfile.Rtt(20, 2, 0.01, 0));
        var b = h.Join("bob", LinkProfile.Rtt(20, 2, 0.01, 0));
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished, 40_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        Assert.Equal([0, 1], new[] { a.Welcome!.Seat, b.Welcome!.Seat }.Order());
        Assert.Equal(h.Host.View.Description.LocalSeats.Order(), new[] { a.Welcome!.CarId, b.Welcome!.CarId }.Order());
        Assert.Equal(Key(h.Host.Results!), Key(a.Results!.Results));
        Assert.Equal(Key(h.Host.Results!), Key(b.Results!.Results));
        Assert.True(a.LatestAck.AppliedInput > 0);
        Assert.True(b.LatestAck.AppliedInput > 0);
    }

    [GameFact]
    public void ThreeClients_GetDistinctSeats_DriveTheirOwnCars_AndShareOneResult()
    {
        using var h = new NetHarness(303, Bounded(), new ServerRaceConfig(Players: 3));
        var a = h.Join("alice", LinkProfile.Rtt(20, 2, 0.01, 0));
        var b = h.Join("bob", LinkProfile.Rtt(20, 2, 0.01, 0));
        var c = h.Join("carol", LinkProfile.Rtt(20, 2, 0.01, 0));
        h.Controls = NetHarness.Weave;

        Assert.Equal(3, h.Host.Seats);
        Assert.Equal([0, 1, 2], h.Host.View.Description.LocalSeats);
        var start = h.Host.Runner.Humans.Select(car => car.Pos).ToArray();

        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished &&
                                     c.State == RaceClientState.Finished, 40_000_000), string.Join("\n", h.Log.TakeLast(20)));

        var clients = new[] { a, b, c };
        // the server assigned three distinct seats/cars; each client only sees its own car as local
        Assert.Equal([0, 1, 2], clients.Select(x => x.Welcome!.Seat).Order());
        var cars = clients.Select(x => x.Welcome!.CarId).ToArray();
        Assert.Equal(3, cars.Distinct().Count());
        Assert.Equal(h.Host.View.Description.LocalSeats.Order(), cars.Order());
        foreach (var client in clients)
        {
            Assert.Equal([client.Welcome!.CarId], client.Description!.LocalSeats);
            Assert.True(client.LatestAck.AppliedInput > 0, $"{client.Welcome.Seat}: its controls were acknowledged");
        }

        // every seat's samples reached the host: all three human cars drove away from their grid slots
        Assert.Equal(0, h.Server.Stats.ClockSlipSteps);
        var humanCars = h.Host.Runner.Humans;
        for (int seat = 0; seat < 3; seat++)
            Assert.True(Moved(humanCars[seat], start[seat]), $"seat {seat} must drive on its own client's input");

        // one common authoritative result
        Assert.Equal(StartStep + 160, h.Host.Steps);
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.Single(h.Server.PublishedEvents!.OfType<RaceFinishedEvent>());
        foreach (var client in clients)
            Assert.Equal(Key(h.Host.Results!), Key(client.Results!.Results));
    }

    [GameFact]
    public void ManualStartNeedsEveryOneOfTheThreeSeats()
    {
        var options = new RaceServerOptions
        {
            ManualStart = true, MaxRaceSteps = 600, WaitTimeoutMicros = 60_000_000, LingerMicros = 3_000_000,
        };
        using var h = new NetHarness(404, options, new ServerRaceConfig(Players: 3));
        var a = h.Join("alice", LinkProfile.Rtt(20, 2, 0.01, 0));
        var b = h.Join("bob", LinkProfile.Rtt(20, 2, 0.01, 0));
        Assert.True(h.RunUntil(() => h.Server.Lobby.Connected == 2, 5_000_000), string.Join("\n", h.Log.TakeLast(10)));

        var lobby = h.Server.Lobby;
        Assert.Equal(3, lobby.Seats.Count);
        Assert.Equal(2, lobby.Connected);
        Assert.False(lobby.CanStart);
        Assert.False(h.Server.TryOperatorStart(out string why));
        Assert.Contains("2/3", why);
        Assert.Equal(0, h.Host.Steps);

        var c = h.Join("carol", LinkProfile.Rtt(20, 2, 0.01, 0));
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), string.Join("\n", h.Log.TakeLast(10)));
        Assert.Equal(3, h.Server.Lobby.Ready);
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.Equal(NetRacePhase.Countdown, h.Server.Phase);
        Assert.False(h.Server.TryOperatorStart(out _)); // exactly once

        h.Controls = NetHarness.Weave;
        var clients = new[] { a, b, c };
        Assert.True(h.RunUntil(() => clients.All(x => x.State == RaceClientState.Finished), 40_000_000),
            string.Join("\n", h.Log.TakeLast(20)));
        Assert.Equal(600, h.Host.Steps);
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.Equal([0, 1, 2], clients.Select(x => x.Welcome!.Seat).Order());
        foreach (var client in clients)
            Assert.Equal(Key(h.Host.Results!), Key(client.Results!.Results));
    }
}
