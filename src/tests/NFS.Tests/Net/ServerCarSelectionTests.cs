using NFS.Core.Constants;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>TASKS item 15: the car model a player selected in the menu travels in the hello, is stored per lobby slot and is
/// used for that player's human car in every authoritative start – without confusing the model with the car slot.</summary>
public class ServerCarSelectionTests
{
    const int StartStep = 256;

    static Func<int, int, int, IReadOnlyList<int>, RaceHost> Types(ServerRaceConfig race) =>
        (id, humans, bots, carTypes) => race.CreateHost(GameData.Dir, id, humans, bots, carTypes);

    static string Tail(NetHarness h) => string.Join("\n", h.Log.TakeLast(20));

    static int DistinctType(RaceSetup setup, params int[] avoid)
    {
        for (int t = 0; t <= NetProtocol.MaxCarType; t++)
            if (!avoid.Contains(t) && setup.Car(0).CarType != t && setup.Car(1).CarType != t) return t;
        throw new InvalidOperationException("no free car model type");
    }

    static NetReject? Handshake(NetHarness h, byte[] hello)
    {
        var raw = new RecordingHandler();
        h.Net.AddClient(raw, LinkProfile.Rtt(10), LinkProfile.Rtt(10)).Connect(hello);
        h.RunUntil(() => raw.Connected || raw.Disconnected, 1_000_000);
        return raw.Disconnected ? NetReject.TryDecode(raw.DisconnectData ?? []) : null;
    }

    [GameFact]
    public void TwoPlayersWithDifferentMenuModelsRaceTheirOwnCars()
    {
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9101, race: race, hostFactoryTypes: Types(race));
        var frozen = h.Host.Runner.Session.Setup;
        int typeA = DistinctType(frozen);
        int typeB = DistinctType(frozen, typeA);
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: typeA);
        var b = h.Join("bob", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: typeB);

        // The lobby shows what each player chose, before the race is composed.
        Assert.True(h.RunUntil(() => h.Server.Lobby.Seats.All(s => s.Connected && s.Ready), 5_000_000), Tail(h));
        var lobby = h.Server.Lobby;
        Assert.Equal(RaceConstants.CarNames[typeA], lobby.Seats.Single(s => s.Name == "alice").Model);
        Assert.Equal(RaceConstants.CarNames[typeB], lobby.Seats.Single(s => s.Name == "bob").Model);

        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 10_000_000), Tail(h));
        Assert.True(h.RunUntil(() => h.Server.Host.Steps > 0, 2_000_000), Tail(h));

        // The start recomposed the frozen race with exactly the two selected models; models reach the physics car and the
        // welcome the clients load their local scene from.
        Assert.NotSame(h.Host, h.Server.Host);
        var host = h.Server.Host;
        Assert.Equal(2, host.Seats);
        Assert.Equal(typeA, host.Runner.Session.Setup.Car(a.Welcome!.CarId).CarType);
        Assert.Equal(typeB, host.Runner.Session.Setup.Car(b.Welcome!.CarId).CarType);
        Assert.Equal(RaceConstants.CarNames[typeA], host.Runner.Session.Cars[a.Welcome.CarId].Model);
        Assert.Equal(RaceConstants.CarNames[typeB], host.Runner.Session.Cars[b.Welcome.CarId].Model);
        Assert.NotEqual(a.Welcome.CarId, b.Welcome.CarId);
        Assert.Equal(host.Runner.Session.Setup.Block, a.Welcome.SetupBlock);
        Assert.Equal(a.Welcome.SetupBlock, b.Welcome.SetupBlock);
        Assert.Equal(h.Server.SetupHash, a.Welcome.SetupHash);
        Assert.Equal(h.Server.SetupHash, b.Welcome.SetupHash);
        // The remaining six cars are still the frozen AI opponents.
        Assert.Equal(6, host.Runner.Session.Setup.NumOpponentCars);
        Assert.All(Enumerable.Range(2, 6), i => Assert.Equal(frozen.Car(i).CarType, host.Runner.Session.Setup.Car(i).CarType));
    }

    [GameFact]
    public void OneHumanKeepsSelectedModelWithThreeBots()
    {
        var race = new ServerRaceConfig(Players: 8) { MinimumPlayers = 1, BotSetting = 3 };
        var options = new RaceServerOptions { ManualStart = true, MinPlayers = 1, BotSetting = 3 };
        using var h = new NetHarness(9107, options, race, hostFactoryTypes: Types(race));
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: 9);
        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string error), error);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started, 10_000_000), Tail(h));
        var setup = h.Server.Host.Runner.Session.Setup;
        Assert.Equal(4, setup.NumCars);
        Assert.Equal(3, setup.NumOpponentCars);
        Assert.Equal(9, setup.Car(a.Welcome!.CarId).CarType);
        Assert.Equal(setup.Block, a.Welcome.SetupBlock);
    }

    [GameFact]
    public void SelectionsThatAlreadyMatchTheFrozenRaceReuseItByteForByte()
    {
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9104, race: race, hostFactoryTypes: Types(race));
        var frozen = h.Host.Runner.Session.Setup;
        int typeA = frozen.Car(0).CarType, typeB = frozen.Car(1).CarType;
        Assert.True(NetProtocol.IsCarType(typeA) && NetProtocol.IsCarType(typeB), $"frozen models {typeA}/{typeB}");
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: typeA);
        var b = h.Join("bob", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: typeB);

        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 10_000_000), Tail(h));
        Assert.Same(h.Host, h.Server.Host); // no recomposition, no second welcome
        Assert.Equal(frozen.Block, a.Welcome!.SetupBlock);
        Assert.Equal(frozen.Block, b.Welcome!.SetupBlock);
        Assert.Equal(h.Server.SetupHash, a.Welcome.SetupHash);
    }

    [GameFact]
    public void ClientsWithoutASelectionKeepTheFrozenSetupByteForByte()
    {
        var race = new ServerRaceConfig();
        using var h = new NetHarness(9105, race: race, hostFactoryTypes: Types(race));
        var frozen = h.Host.Runner.Session.Setup.Block.ToArray();
        var a = h.Join("alice", LinkProfile.Rtt(10));
        var b = h.Join("bob", LinkProfile.Rtt(10));

        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 10_000_000), Tail(h));
        Assert.Same(h.Host, h.Server.Host);
        Assert.Equal(frozen, h.Server.Host.Runner.Session.Setup.Block);
        Assert.Equal(frozen, a.Welcome!.SetupBlock);
        Assert.Equal(frozen, b.Welcome!.SetupBlock);
        Assert.Equal(h.Server.Lobby.Seats.Single(s => s.Name == "alice").Model,
            h.Server.Host.Runner.Session.Cars[a.Welcome.CarId].Model);
    }

    [GameFact]
    public void AModelTheServerCannotComposeNeverStartsTheFrozenCar()
    {
        using var h = new NetHarness(9103);
        var frozen = h.Host.Runner.Session.Setup;
        int chosen = DistinctType(frozen);
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: chosen);
        var b = h.Join("bob", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: chosen);
        Assert.True(h.RunUntil(() => h.Server.Lobby.Seats.All(s => s.Connected && s.Ready), 5_000_000), Tail(h));

        h.Run(2_000_000); // readiness alone must never race the frozen model instead of the chosen one
        Assert.Equal(NetRacePhase.Loading, h.Server.Phase);
        Assert.Equal(0, h.Host.Steps);
        Assert.Equal(h.Host.Runner.Session.Setup.Block, a.Welcome!.SetupBlock); // still the frozen round, never a wrong-model start
        Assert.False(h.Server.TryOperatorStart(out string error));
        Assert.Contains("car models", error);
        Assert.Same(h.Host, h.Server.Host);
        Assert.Equal(0, h.Host.Steps);
    }

    [GameFact]
    public void AHelloWithAnOutOfRangeModelIsRejectedAsMalformed()
    {
        using var h = new NetHarness(9106);
        var valid = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, NetHarness.Fingerprint, "eve", NetProtocol.MaxCarType).Encode();
        Assert.Equal(NetRejectReason.Malformed, Handshake(h, [.. valid[..^1], NetProtocol.MaxCarType + 1])!.Reason);
        Assert.Equal(NetRejectReason.Malformed, Handshake(h, [.. valid[..^1], 14])!.Reason); // the cheat cars are not selectable
        Assert.Equal(NetRejectReason.Malformed, Handshake(h, [.. valid[..^1], 0xFE])!.Reason);
        Assert.Equal(0, h.Server.ConnectedPlayers);
        Assert.Equal(0, h.Host.Steps);

        // The same hello with a valid selection takes a seat and reports it in the lobby.
        var a = h.Join("alice", LinkProfile.Rtt(10), LinkProfile.Rtt(10), carType: 2);
        Assert.True(h.RunUntil(() => h.Server.Lobby.Seats.Any(s => s.Name == "alice"), 2_000_000), Tail(h));
        Assert.Equal(RaceConstants.CarNames[2], h.Server.Lobby.Seats.Single(s => s.Name == "alice").Model);
        Assert.Equal(2, a.CarType);
    }

    [GameFact]
    public void SelectionIsKeptAcrossNextAndReappliedToTheFollowingRound()
    {
        var race = new ServerRaceConfig();
        var options = new RaceServerOptions
        {
            ManualStart = true,
            MaxRaceSteps = StartStep + 40,
            WaitTimeoutMicros = 60_000_000,
            LingerMicros = 0,
            BotCount = 2,
        };
        using var h = new NetHarness(9102, options, race,
            (id, humans) => race.CreateHost(GameData.Dir, id, humans), hostFactoryTypes: Types(race));
        var frozen = h.Host.Runner.Session.Setup;
        int typeA = DistinctType(frozen), typeB = DistinctType(frozen, typeA);
        var a = h.Join("alice", LinkProfile.Rtt(20), LinkProfile.Rtt(20), carType: typeA);
        var b = h.Join("bob", LinkProfile.Rtt(20), LinkProfile.Rtt(20), carType: typeB);

        Assert.True(h.RunUntil(() => h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.True(h.Server.TryOperatorStart(out string why), why);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 10_000_000), Tail(h));
        int firstRaceId = h.Server.RaceId;
        Assert.Equal(typeA, h.Server.Host.Runner.Session.Setup.Car(a.Welcome!.CarId).CarType);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Finished && b.State == RaceClientState.Finished, 40_000_000), Tail(h));

        // `next` builds the capacity lobby with the frozen models, but each seat keeps the menu selection for the next start.
        Assert.True(h.Server.TryOperatorNext(out string error), error);
        Assert.True(h.RunUntil(() => a.Welcome!.RaceId == h.Server.RaceId && a.State == RaceClientState.Ready &&
                                     b.State == RaceClientState.Ready && h.Server.Lobby.CanStart, 5_000_000), Tail(h));
        Assert.NotEqual(firstRaceId, h.Server.RaceId);
        var lobby = h.Server.Lobby;
        Assert.Equal(RaceConstants.CarNames[typeA], lobby.Seats.Single(s => s.Name == "alice").Model);
        Assert.Equal(RaceConstants.CarNames[typeB], lobby.Seats.Single(s => s.Name == "bob").Model);

        Assert.True(h.Server.TryOperatorStart(out error), error);
        Assert.True(h.RunUntil(() => a.State == RaceClientState.Started && b.State == RaceClientState.Started, 10_000_000), Tail(h));
        Assert.Equal(typeA, h.Server.Host.Runner.Session.Setup.Car(a.Welcome!.CarId).CarType);
        Assert.Equal(typeB, h.Server.Host.Runner.Session.Setup.Car(b.Welcome!.CarId).CarType);
        Assert.Equal(RaceConstants.CarNames[typeB], h.Server.Host.Runner.Session.Cars[b.Welcome.CarId].Model);
        Assert.Equal(h.Server.SetupHash, b.Welcome.SetupHash);
    }

    [GameFact]
    public void SetupPatchesOnlyTheExplicitHumanCarRecord()
    {
        var race = new ServerRaceConfig();
        var plain = race.BuildSetup(GameData.Dir, 2, 6);
        var chosen = race.BuildSetup(GameData.Dir, 2, 6, [NetProtocol.NoCarType, 7]);
        Assert.Equal(plain.Car(0).CarType, chosen.Car(0).CarType);
        Assert.Equal(7, chosen.Car(1).CarType);
        int offset = 0x78 + RaceConfig.CarRecordSize;
        for (int i = 0; i < plain.Block.Length; i++)
            if (i < offset || i >= offset + 4)
                Assert.Equal(plain.Block[i], chosen.Block[i]);
        Assert.Throws<ArgumentOutOfRangeException>(() => race.BuildSetup(GameData.Dir, 2, 6, [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => race.BuildSetup(GameData.Dir, 2, 6, [0, NetProtocol.MaxCarNumber + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => race.BuildSetup(GameData.Dir, 2, 6, [NetProtocol.NoCarType - 1, 0]));
    }
}
