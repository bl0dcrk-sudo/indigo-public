using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Bot count and road traffic of a network game: the server starts with auto bots and no traffic, the operator changes
/// them in the lobby before every start of a single race (a knockout series ignores both), carried by lobby options, games list
/// and lobby state, and composed into the race.</summary>
public class SessionBotsTrafficTests
{
    static NetLobbyMember Alice => new(0, "alice", false, NetProtocol.NoCarType, true, true);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(7, true)]
    public void BotsAndTrafficRoundTripThroughOptionsInfoAndLobbyState(int bots, bool traffic)
    {
        var options = new NetLobbyOptions(7, 5, 3, 2, true, bots, traffic);
        var or = new WireReader(options.Encode());
        var optionsBack = NetLobbyOptions.Decode(NetHeader.Read(ref or), ref or);
        Assert.Equal(options, optionsBack);
        Assert.Equal((bots, traffic), (optionsBack.Bots, optionsBack.Traffic));

        var info = new NetGameInfo(7, "Game", NetGamePhase.Lobby, 1, 4, 3, 2, NetGameMode.SingleRace, true, bots, traffic);
        var lr = new WireReader(new NetGameList([info]).Encode());
        var infoBack = NetGameList.Decode(NetHeader.Read(ref lr), ref lr).Games.Single();
        Assert.Equal((bots, traffic), (infoBack.Bots, infoBack.Traffic));
        Assert.True(infoBack.Backward);

        var state = new NetLobbyState(7, 5, "Game", NetGamePhase.Lobby, 3, 2, 4, NetGameMode.SingleRace, 0,
            [Alice], null, true, bots, traffic);
        var sr = new WireReader(state.Encode());
        var stateBack = NetLobbyState.Decode(NetHeader.Read(ref sr), ref sr);
        Assert.Equal((bots, traffic, true), (stateBack.Bots, stateBack.Traffic, stateBack.Backward));
    }

    [Fact]
    public void DefaultsAreAutoBotsAndNoTraffic()
    {
        var options = new NetLobbyOptions(7, 5, 3, 2);
        Assert.Equal((NetProtocol.AutoBots, false), (options.Bots, options.Traffic));
        Assert.Equal(ServerRaceConfig.AutoBots, NetProtocol.AutoBots);
    }

    [Fact]
    public void MessagesRejectAnInvalidBotCountOrTrafficByte()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetGameInfo(7, "Game", NetGamePhase.Lobby, 1, 4, 3, 2, NetGameMode.SingleRace, false, 8).Write(new WireWriter(64)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetLobbyState(7, 5, "Game", NetGamePhase.Lobby, 3, 2, 4, NetGameMode.SingleRace, 0, [Alice], null, false, -2).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyOptions(7, 5, 3, 2, false, 8).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyOptions(7, 5, 3, 2, false, -2).Encode());

        byte[] Encoded(int bots) => new NetLobbyState(7, 5, "Game", NetGamePhase.Lobby, 3, 2, 4, NetGameMode.SingleRace, 0,
            [Alice], null, false, bots, true).Encode();
        var bad = Encoded(3);
        var other = Encoded(4);
        int at = Enumerable.Range(0, bad.Length).Single(i => bad[i] != other[i]); // the bot byte
        bad[at] = 8;
        Assert.Throws<NetProtocolException>(() => DecodeState(bad));
        bad[at] = 3;
        Assert.Equal(3, DecodeState(bad).Bots);
        bad[at + 1] = 2; // the traffic byte is strictly 0 or 1
        Assert.Throws<NetProtocolException>(() => DecodeState(bad));
    }

    static NetLobbyState DecodeState(byte[] bytes)
    {
        var r = new WireReader(bytes);
        return NetLobbyState.Decode(NetHeader.Read(ref r), ref r);
    }

    static SessionHarness Lobby(int seed, int maxPlayers, int bots, bool traffic, out SessionTestClient alice)
    {
        var h = new SessionHarness(seed, new SessionServerOptions { MaxRaceSteps = 30 });
        alice = h.Join("alice");
        var a = alice;
        Assert.True(h.RunUntil(() => a.List != null, 1_000_000));
        a.Create("game", maxPlayers, 1, 2, bots: bots, traffic: traffic);   // the operator then sets bots and traffic in the lobby
        Assert.True(h.RunUntil(() => a.InLobby && a.Lobby!.Bots == bots && a.Lobby.Traffic == traffic && a.Lobby.Track == 1, 1_000_000));
        return h;
    }

    static SessionTestClient JoinLobby(SessionHarness h, SessionTestClient alice, string name)
    {
        var c = h.Join(name);
        Assert.True(h.RunUntil(() => c.List != null, 1_000_000));
        c.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => c.InLobby, 1_000_000));
        return c;
    }

    [Theory]
    [InlineData(-9)]
    [InlineData(-2)]
    [InlineData(8)]
    [InlineData(127)]
    public void LobbyOptionsWithAnInvalidBotCountAreMalformedAndChangeNothing(int bots)
    {
        using var h = Lobby(80, 4, 2, false, out var alice);
        var bytes = new NetLobbyOptions(alice.GameId, alice.Epoch, 5, 3, false, 0).Encode();
        bytes[NetHeader.Size + 3] = (byte)(sbyte)bots; // track, laps, direction, then the bot byte
        long malformed = h.Server.Stats.Malformed;
        alice.Send(bytes);
        h.Run(200_000);
        Assert.True(h.Server.Stats.Malformed > malformed);
        var game = h.Server.Games.Single();
        Assert.Equal((1, 2, 2), (game.Track, game.Laps, game.Bots));
    }

    [GameTheory]
    [InlineData(3, 1, 3)]   // one human, three requested bots
    [InlineData(0, 1, 0)]
    [InlineData(-1, 1, 7)]  // auto fills the grid to eight cars
    [InlineData(-1, 3, 5)]
    [InlineData(7, 3, 5)]   // a large request is capped: humans + bots <= 8
    [InlineData(7, 8, 0)]   // a full grid of humans has no room for bots
    [InlineData(2, 4, 2)]
    public void RaceIsComposedWithTheChosenBotsCappedAtEightCars(int bots, int humans, int expectedBots)
    {
        using var h = Lobby(81 + bots + humans, 8, bots, false, out var alice);
        for (int i = 1; i < humans; i++) JoinLobby(h, alice, $"p{i}");
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.Count == humans, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        var game = h.Server.Games.Single();
        Assert.Equal(bots, game.Bots);
        var host = game.Round!.Host;
        var setup = host.Runner.Session.Setup;
        Assert.Equal(humans, host.Seats);
        Assert.Equal(humans + expectedBots, setup.NumCars);
        Assert.InRange(setup.NumCars, 1, ServerRaceConfig.MaxRacers);
        Assert.Equal(expectedBots, setup.NumOpponentCars);
        Assert.Equal(expectedBots, host.Runner.Cars.Count(car => car.Ai != null));
        Assert.Equal(humans, Enumerable.Range(0, setup.NumCars).Count(i => setup.Car(i).CarClass == 1));
    }

    [GameTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrafficChoiceReachesTheRaceSetup(bool traffic)
    {
        // The front-end race setup emits traffic cars (class 4) only for a pair race without extra opponents, so that is the
        // composition that shows the flag: two seats, two humans, no bots.
        using var h = Lobby(90, 2, 0, traffic, out var alice);
        JoinLobby(h, alice, "bob");
        Assert.True(h.RunUntil(() => alice.Lobby!.Members.Count == 2, 1_000_000));
        Assert.Equal(traffic, alice.Lobby!.Traffic);
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        var game = h.Server.Games.Single();
        Assert.Equal(traffic, game.Traffic);
        var setup = game.Round!.Host.Runner.Session.Setup;
        Assert.Equal(traffic, Enumerable.Range(0, setup.NumCars).Any(i => setup.Car(i).CarClass == 4));
        Assert.Equal(0, setup.NumOpponentCars);
    }

    [GameFact]
    public void TheOperatorChangesBotsAndTrafficAndInfoAndStateReportThem()
    {
        using var h = Lobby(91, 4, 3, true, out var alice);
        var bob = JoinLobby(h, alice, "bob");
        var carol = h.Join("carol");
        Assert.True(h.RunUntil(() => carol.List != null, 1_000_000));
        var listed = carol.List!.Games.Single();
        Assert.Equal((3, true), (listed.Bots, listed.Traffic));
        Assert.Equal((3, true), (bob.Lobby!.Bots, bob.Lobby.Traffic));

        // another player cannot change them
        bob.SetOptions(1, 2, bots: 0, traffic: false);
        h.Run(200_000);
        Assert.Equal((3, true), (alice.Lobby!.Bots, alice.Lobby.Traffic));

        alice.SetOptions(4, 5, true, bots: 1, traffic: false);
        Assert.True(h.RunUntil(() => bob.Lobby!.Track == 4 && bob.Lobby.Laps == 5 && bob.Lobby.Bots == 1, 1_000_000));
        Assert.Equal((1, false), (bob.Lobby.Bots, bob.Lobby.Traffic));
        Assert.Equal((1, false), (alice.Lobby!.Bots, alice.Lobby.Traffic));
        var game = h.Server.Games.Single();
        Assert.Equal((1, false), (game.Bots, game.Traffic));
        Assert.Equal((1, false), (game.ToInfo().Bots, game.ToInfo().Traffic));
        Assert.True(h.RunUntil(() => carol.List!.Games.Single().Bots == 1, 1_000_000));
    }
}
