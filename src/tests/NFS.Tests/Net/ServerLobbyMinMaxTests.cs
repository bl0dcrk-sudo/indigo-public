using NFS.Core;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Dedicated lobby with a true minimum/maximum seat range: the configured `Players` is the lobby capacity, and the
/// operator `start` composes the actual race for exactly the connected humans on a fresh host (N human cars, class 1, plus
/// eight minus N real AI opponents, class 2) which every client accepts through a pre-start welcome. A full lobby reuses the
/// capacity host unchanged, so exact-count races keep their original setup and replay behaviour.</summary>
public class ServerLobbyMinMaxTests
{
    /// <summary>Green light at host step 256 (tick 0x200), as in the other network races.</summary>
    const int StartStep = 256;

    static RaceServerOptions Options(ServerRaceConfig race, int raceSteps) => new()
    {
        ManualStart = true,
        MaxRaceSteps = raceSteps,
        WaitTimeoutMicros = 60_000_000,
        LingerMicros = 0, // an interactive server keeps the result until `next` or `quit`
        BotCount = race.BotCount,
        MinPlayers = race.EffectiveMinimumPlayers,
    };

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static bool Moved(CarObj car, Vec3 start) =>
        Math.Abs(car.Pos.X - start.X) > Fx.One || Math.Abs(car.Pos.Z - start.Z) > Fx.One;

    /// <summary>Waits until every client accepted the (possibly recomposed) round welcome and the countdown began.</summary>
    static bool Started(NetHarness h, params RaceClient[] clients) => h.RunUntil(() =>
        (h.Server.Phase is NetRacePhase.Countdown or NetRacePhase.Racing) &&
        clients.All(c => c.Welcome!.RaceId == h.Server.RaceId && c.Baseline!.RaceId == h.Server.RaceId),
        10_000_000);

    static (int players, int opponents) Counts(NFS.Game.Race.RaceSetup setup) => (setup.NumPlayerCars, setup.NumOpponentCars);

    [GameFact]
    public void SingleReadyHumanOfTwo_StartsOneHumanAndSevenAiOpponentsFromAComposedHost()
    {
        var race = new ServerRaceConfig(Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9101, Options(race, StartStep + 220), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        var lobby = h.Server.Lobby;
        Assert.Equal(2, lobby.Seats.Count);
        Assert.Equal(1, lobby.MinPlayers);
        Assert.Equal(1, lobby.Connected);
        Assert.Equal(1, lobby.Ready);
        Assert.False(lobby.Seats[1].Connected); // the second capacity seat is empty and stays outside the race

        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.Server.StartPending);                 // the clients must accept the composed welcome first
        Assert.NotSame(h.Host, h.Server.Host);             // a fresh host for the actual human count
        var host = h.Server.Host;
        Assert.Equal(1, host.Seats);
        Assert.Single(host.Runner.HumanSims);
        Assert.Null(host.Runner.HumanSims[0].Ai);
        Assert.Equal(7, host.Runner.Cars.Count(car => car.Ai != null));
        var setup = host.Runner.Session.Setup;
        Assert.Equal((1, 7), Counts(setup));
        Assert.Equal(1, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 1));
        Assert.Equal(7, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 2));
        Assert.Equal([0], host.View.Description.LocalSeats);

        Assert.True(Started(h, alice), Tail(h));
        Assert.Equal(h.Server.RaceId, alice.Welcome!.RaceId);
        Assert.Equal(0, alice.Welcome.Seat);
        Assert.Equal(0, alice.Welcome.CarId);
        Assert.Equal([0], alice.Description!.LocalSeats);

        var grid = host.Runner.Cars.Where(car => car.Ai != null).Select(car => (car.Obj, car.Obj.Pos)).ToArray();
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
        Assert.Equal(NetFinishReason.TimeLimit, h.Server.FinishReason);
        Assert.All(grid, b => Assert.True(Moved(b.Obj, b.Pos), "every AI opponent must drive its own car"));

        // the frozen result reflects the composed setup: exactly one human car, the rest real opponents
        var results = host.Results!;
        Assert.Equal(8, results.Racers.Count);
        Assert.Equal(1, results.Racers.Count(r => r.Human));
        Assert.Equal(alice.Welcome.CarId, results.Racers.Single(r => r.Human).CarIndex);
    }

    [GameFact]
    public void SingleReadyHumanOfEight_ComposesOneHumanWithSevenAiOpponents()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        Assert.Equal(0, race.BotCount); // a full lobby of eight seats would have no AI at all
        using var h = new NetHarness(9102, Options(race, StartStep + 120), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var alice = h.Join("alice", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.Equal(8, h.Server.Lobby.Seats.Count);
        Assert.Equal(1, h.Server.Lobby.Connected);
        Assert.Equal(7, h.Server.Lobby.Seats.Count(s => !s.Connected));

        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.Equal(1, h.Server.Host.Seats);
        Assert.Equal(7, h.Server.Host.Runner.Cars.Count(car => car.Ai != null));
        Assert.Equal((1, 7), Counts(h.Server.Host.Runner.Session.Setup));
        Assert.True(Started(h, alice), Tail(h));
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
        Assert.Equal(1, h.Server.Host.Results!.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void ThreeReadyHumansOfEight_ComposeThreeHumanCarsAndFiveAiOpponents()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 3 };
        using var h = new NetHarness(9103, Options(race, StartStep + 120), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var clients = new[] { h.Join("alice", LinkProfile.Rtt(20)), h.Join("bob", LinkProfile.Rtt(20)), h.Join("carol", LinkProfile.Rtt(20)) };
        h.Controls = NetHarness.Weave;

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.Equal(3, h.Server.Lobby.Connected);
        Assert.Equal(3, h.Server.Lobby.Ready);
        Assert.Equal(8, h.Server.Lobby.Seats.Count);

        Assert.True(h.Server.TryOperatorStart(out string error), error);
        var host = h.Server.Host;
        Assert.Equal(3, host.Seats);
        Assert.Equal(3, host.Runner.HumanSims.Count);
        Assert.All(host.Runner.HumanSims, sim => Assert.Null(sim.Ai));
        Assert.Equal(3, host.Runner.Road.HumanCount);
        Assert.Equal(5, host.Runner.Cars.Count(car => car.Ai != null));
        var setup = host.Runner.Session.Setup;
        Assert.Equal((3, 5), Counts(setup));
        Assert.Equal(3, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 1));
        Assert.Equal(5, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 2));
        Assert.Equal([0, 1, 2], host.View.Description.LocalSeats);

        Assert.True(Started(h, clients), Tail(h));
        Assert.Equal([0, 1, 2], clients.Select(c => c.Welcome!.Seat).Order());
        Assert.Equal([0, 1, 2], clients.Select(c => c.Welcome!.CarId).Order());
        Assert.True(h.RunUntil(() => clients.All(c => c.State == RaceClientState.Finished), 40_000_000), Tail(h));
        Assert.Equal(3, host.Results!.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void FullLobbyOfEight_ReusesTheCapacityHostWithEverySeatHuman()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1 };
        using var h = new NetHarness(9104, Options(race, StartStep + 40), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var clients = Enumerable.Range(0, 8).Select(i => h.Join($"player{i}", LinkProfile.Rtt(20))).ToArray();

        Assert.True(h.RunUntil(() => h.Server.Lobby.Connected == 8 && h.Server.Lobby.Ready == 8, 10_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        // No recomposition: eight humans already fill the configured lineup (byte-identical setup, no re-welcome).
        Assert.False(h.Server.StartPending);
        Assert.Same(h.Host, h.Server.Host);
        Assert.Equal(NetRacePhase.Countdown, h.Server.Phase);
        Assert.All(clients, c => Assert.Equal(h.Server.RaceId, c.Welcome!.RaceId));
        Assert.Equal(8, h.Host.Runner.HumanSims.Count);
        Assert.All(h.Host.Runner.HumanSims, sim => Assert.Null(sim.Ai));
        Assert.DoesNotContain(h.Host.Runner.Cars, car => car.Ai != null);
        Assert.Equal((8, 0), Counts(h.Host.Runner.Session.Setup));
        Assert.Equal(Enumerable.Range(0, 8), h.Host.View.Description.LocalSeats);

        Assert.True(h.RunUntil(() => clients.All(c => c.State == RaceClientState.Finished), 40_000_000), Tail(h));
        Assert.Equal(8, h.Host.Results!.Racers.Count(r => r.Human));
    }

    [GameFact]
    public void GappedLobby_CompactsTheConnectedSeatsIntoDenseRaceSeats()
    {
        var race = new ServerRaceConfig(Players: 3) { MinimumPlayers = 1 };
        using var h = new NetHarness(9108, Options(race, StartStep + 40), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        h.Join("alice", LinkProfile.Rtt(20));
        var bob = h.Join("bob", LinkProfile.Rtt(20));
        var carol = h.Join("carol", LinkProfile.Rtt(20));
        Assert.True(h.RunUntil(() => h.Server.Lobby.Ready == 3, 5_000_000), Tail(h));

        // Alice leaves before the start: seats 1 and 2 stay occupied and must become race seats 0 and 1.
        h.Clients[0].Link.Disconnect();
        Assert.True(h.RunUntil(() => h.Server.Lobby.Connected == 2, 5_000_000), Tail(h));

        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(Started(h, bob, carol), Tail(h));
        Assert.Equal(2, h.Server.Host.Seats);
        Assert.Equal([0, 1], h.Server.Host.View.Description.LocalSeats);
        Assert.Equal((0, 0), (bob.Welcome!.Seat, bob.Welcome.CarId));
        Assert.Equal((1, 1), (carol.Welcome!.Seat, carol.Welcome.CarId));
        Assert.Equal((2, 6), Counts(h.Server.Host.Runner.Session.Setup));
        Assert.Equal(6, h.Server.Host.Runner.Cars.Count(car => car.Ai != null));
    }

    [GameFact]
    public void DefaultTwoPlayerLobby_StartsTheLegacySetupWithoutRecomposition()
    {
        // Replay/regression guard: the two connected humans already fill the default lineup, so `start` must not rewrite
        // the frozen setup block (the original split-screen race) and must keep the two-input server scheduler.
        var race = new ServerRaceConfig();
        var expected = race.BuildSetup(GameData.Dir);
        using var h = new NetHarness(9109, Options(race, StartStep + 40), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var a = h.Join("alice", LinkProfile.Rtt(20));
        var b = h.Join("bob", LinkProfile.Rtt(20));

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.Same(h.Host, h.Server.Host);
        Assert.Equal(NetRacePhase.Countdown, h.Server.Phase);
        Assert.Equal(expected.Block, h.Server.Host.Runner.Session.Setup.Block);
        Assert.Equal((2, 6), Counts(h.Server.Host.Runner.Session.Setup));
        Assert.NotNull(h.Server.Host.Runner.Player2);
        Assert.Equal(h.Server.RaceId, a.Welcome!.RaceId);
        Assert.Equal(h.Server.RaceId, b.Welcome!.RaceId);
    }

    [GameFact]
    public void StartIsRefusedWithoutAConnectedPlayer()
    {
        var race = new ServerRaceConfig(Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9105, Options(race, StartStep + 20), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        Assert.False(h.Server.TryOperatorStart(out string why));
        Assert.Contains("0/1 connected", why);
        Assert.Equal(NetRacePhase.WaitingForPlayers, h.Server.Phase);
        Assert.Equal(0, h.Host.Steps);
    }

    [GameFact]
    public void StartIsRefusedWhileAConnectedPlayerIsNotReady()
    {
        var race = new ServerRaceConfig(Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9106, Options(race, StartStep + 20), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        h.Join("alice", LinkProfile.Rtt(20));

        // A raw endpoint completes the handshake and takes the second seat but never sends Ready.
        var hello = new RaceClient(null, NetHarness.Fingerprint, "raw").Hello();
        var raw = h.Net.AddClient(new RecordingHandler(), LinkProfile.Rtt(20), LinkProfile.Rtt(20));
        raw.Connect(hello);

        Assert.True(h.RunUntil(() => h.Server.Lobby.Connected == 2 && h.Server.Lobby.Ready == 1, 5_000_000), Tail(h));
        Assert.False(h.Server.Lobby.CanStart); // every CONNECTED player must be ready
        Assert.False(h.Server.TryOperatorStart(out string why));
        Assert.Contains("1/2 ready", why);
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.Equal(0, h.Host.Steps);
    }

    [GameFact]
    public void ClientRefusingTheComposedWelcome_ReturnsTheServerToTheLobby()
    {
        var race = new ServerRaceConfig(Players: 2) { MinimumPlayers = 1 };
        using var h = new NetHarness(9110, Options(race, StartStep + 20), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        int prepared = 0;
        h.Join("alice", LinkProfile.Rtt(20), LinkProfile.Rtt(20), prepare: _ => ++prepared == 1); // refuse the second welcome

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.Server.StartPending);

        // The client leaves instead of loading the composed race: the server rebuilds the capacity lobby on a fresh id.
        Assert.True(h.RunUntil(() => !h.Server.StartPending && h.Server.Phase == NetRacePhase.WaitingForPlayers, 5_000_000), Tail(h));
        Assert.Equal(2, h.Server.Host.Seats);
        Assert.Equal(0, h.Server.Lobby.Connected);
        Assert.False(h.Server.TryOperatorStart(out string why));
        Assert.Contains("0/1 connected", why);
    }

    [GameFact]
    public void NextRoundCanRunWithADifferentNumberOfHumansAndRejectsOldSessionPackets()
    {
        var race = new ServerRaceConfig(Players: 3) { MinimumPlayers = 1 };
        using var h = new NetHarness(9107, Options(race, StartStep + 30), race, (id, humans) => race.CreateHost(GameData.Dir, id, humans));
        var alice = h.Join("alice", LinkProfile.Rtt(20));
        var bob = h.Join("bob", LinkProfile.Rtt(20));
        h.Controls = NetHarness.Weave;

        // Round 1: two humans of the three-seat capacity; the composed host has two human cars and six AI opponents.
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string why), why);
        Assert.Equal(2, h.Server.Host.Seats);
        Assert.Equal(6, h.Server.Host.Runner.Cars.Count(car => car.Ai != null));
        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished && bob.State == RaceClientState.Finished,
            40_000_000), Tail(h));
        Assert.Equal(2, h.Server.Host.Results!.Racers.Count(r => r.Human));

        var oldWelcome = alice.Welcome!;
        int oldRaceId = h.Server.RaceId;
        Assert.True(h.Server.TryOperatorNext(out string error), error);

        // One human leaves before the second round: the same three-seat lobby now composes a one-human race.
        h.Clients[1].Link.Disconnect();
        Assert.True(h.RunUntil(() => h.Server.Lobby.Connected == 1, 5_000_000), Tail(h));
        Assert.True(h.RunUntil(() => alice.Welcome!.RaceId == h.Server.RaceId && h.Server.Lobby.CanStart, 5_000_000), Tail(h));

        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.True(h.Server.StartPending);
        var second = h.Server.Host;
        Assert.Equal(1, second.Seats);
        Assert.Equal(7, second.Runner.Cars.Count(car => car.Ai != null));
        Assert.Equal((1, 7), Counts(second.Runner.Session.Setup));
        Assert.True(h.RunUntil(() => alice.Welcome!.RaceId == h.Server.RaceId && alice.State == RaceClientState.Started, 10_000_000), Tail(h));
        Assert.Equal(0, alice.Welcome!.Seat); // alice keeps her seat across rounds
        Assert.Equal(0, alice.Welcome.CarId);

        // A packet of the finished round still carries the old race id/epoch and must never step the new host.
        long wrong = h.Server.Stats.WrongSession;
        var staleInput = new NetInputPacket(oldRaceId, oldWelcome.Epoch, 0,
            [new NetInputSample(999, 0, 0, 255, 0, 0)], []).Encode();
        h.Clients[0].Link.Send(NetChannel.Unreliable, staleInput);
        h.Run(200_000);
        Assert.Equal(wrong + 1, h.Server.Stats.WrongSession);
        Assert.Equal(0, h.Server.Host.Steps); // the stale packet never reached the new host

        Assert.True(h.RunUntil(() => alice.State == RaceClientState.Finished, 40_000_000), Tail(h));
        Assert.Equal(1, h.Server.Host.Results!.Racers.Count(r => r.Human));
    }
}
