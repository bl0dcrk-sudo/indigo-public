using NFS.Core;
using NFS.Core.Constants;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Change multiplayer-fixes (protocol v15): the server's default options, the operator's edits of every option in the lobby
/// (and the knockout freeze), bot skill / catch-up / physics style and car colour in the composed race, only the showcase cars
/// 0..11. Protocol round trips need no game data; the server tests run on the simulated network.</summary>
public class MultiplayerFixesTests
{
    const uint Epoch = 5;

    static NetGameCreate DecodeCreate(byte[] data)
    {
        var r = new WireReader(data);
        return NetGameCreate.Decode(NetHeader.Read(ref r), ref r);
    }

    static NetLobbyOptions DecodeOptions(byte[] data)
    {
        var r = new WireReader(data);
        return NetLobbyOptions.Decode(NetHeader.Read(ref r), ref r);
    }

    static NetLobbyCar DecodeCar(byte[] data)
    {
        var r = new WireReader(data);
        return NetLobbyCar.Decode(NetHeader.Read(ref r), ref r);
    }

    static NetLobbyState DecodeState(byte[] data)
    {
        var r = new WireReader(data);
        return NetLobbyState.Decode(NetHeader.Read(ref r), ref r);
    }

    static NetGameInfo DecodeInfo(NetGameInfo info)
    {
        var r = new WireReader(new NetGameList([info]).Encode());
        return NetGameList.Decode(NetHeader.Read(ref r), ref r).Games.Single();
    }

    // ---------------- protocol ----------------

    [Fact]
    public void TheProtocolIsV15AndOnlyTheShowcaseCarsAreSelectable()
    {
        Assert.Equal(19, NetProtocol.Version);
        Assert.Equal(11, NetProtocol.MaxCarType);
        for (int car = 0; car <= 11; car++) Assert.True(NetProtocol.IsCarType(car));
        foreach (int cheat in new[] { 12, 13, 14, 15, -1 }) Assert.False(NetProtocol.IsCarType(cheat));
        Assert.True(NetProtocol.IsCarTypeOrNone(NetProtocol.NoCarType));
        Assert.False(NetProtocol.IsCarTypeOrNone(12));
        // the key of a cheat car travels (the server refuses it); a malformed key never decodes
        Assert.Equal(12, DecodeCar(new NetLobbyCar(1, Epoch, 12).Encode()).CarType);
        var bytes = new NetLobbyCar(1, Epoch, 11).Encode();
        bytes[NetHeader.Size + 1] = (byte)' '; // the first character of the key
        Assert.Throws<NetProtocolException>(() => DecodeCar(bytes));
    }

    [Fact]
    public void SkillStyleAndColourRangesAreValidated()
    {
        Assert.True(NetProtocol.IsSkill(0) && NetProtocol.IsSkill(1));
        Assert.False(NetProtocol.IsSkill(2) || NetProtocol.IsSkill(-1));
        Assert.True(NetProtocol.IsStyle(0) && NetProtocol.IsStyle(1) && NetProtocol.IsStyle(2));
        Assert.False(NetProtocol.IsStyle(3) || NetProtocol.IsStyle(-1));
        Assert.True(NetProtocol.IsColourOrNone(NetProtocol.NoColour) && NetProtocol.IsColourOrNone(0x57));
        Assert.False(NetProtocol.IsColourOrNone(NetProtocol.NoColour - 1));
    }

    [Theory]
    [InlineData(NetGameMode.SingleRace, 1)]
    [InlineData(NetGameMode.Knockout, 8)]
    public void CreateCarriesOnlyNameLimitAndMode(NetGameMode mode, int max)
    {
        var create = new NetGameCreate("Friday", max, mode);
        var bytes = create.Encode();
        Assert.Equal(create, DecodeCreate(bytes));
        // header + name (length byte + text) + limit + mode: nothing else
        Assert.Equal(NetHeader.Size + 1 + "Friday".Length + 2, bytes.Length);
        // the former (v14) body with track/laps/direction/bots/traffic is no longer a create message
        Assert.Throws<NetProtocolException>(() => DecodeCreate([.. bytes, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void OptionsCarryAllEightOptionsAndRejectSkillAndStyleOutOfRange()
    {
        var options = new NetLobbyOptions(7, Epoch, NetProtocol.MaxTrack, NetProtocol.MaxLaps, true, 3, true, 0, false, 2);
        var back = DecodeOptions(options.Encode());
        Assert.Equal(options, back);
        Assert.Equal((3, true, 0, false, 2), (back.Bots, back.Traffic, back.Skill, back.CatchUp, back.Style));

        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { Skill = 2 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { Style = 3 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { Bots = 8 }).Encode());
        var bytes = options.Encode();
        int skillAt = NetHeader.Size + 5, styleAt = NetHeader.Size + 7; // track, laps, direction, bots, traffic, skill, catch-up, style
        var badSkill = (byte[])bytes.Clone();
        badSkill[skillAt] = 2;
        Assert.Throws<NetProtocolException>(() => DecodeOptions(badSkill));
        var badStyle = (byte[])bytes.Clone();
        badStyle[styleAt] = 3;
        Assert.Throws<NetProtocolException>(() => DecodeOptions(badStyle));
        var badCatchUp = (byte[])bytes.Clone();
        badCatchUp[styleAt - 1] = 2;
        Assert.Throws<NetProtocolException>(() => DecodeOptions(badCatchUp));
    }

    [Fact]
    public void CarAndMemberCarryTheColour()
    {
        var car = new NetLobbyCar(7, Epoch, 3, 0x45);
        Assert.Equal(car, DecodeCar(car.Encode()));
        Assert.Equal(NetProtocol.NoColour, new NetLobbyCar(7, Epoch, 3).Colour);
        Assert.Throws<ArgumentOutOfRangeException>(() => (car with { Colour = -2 }).Encode());
        var bad = car.Encode();
        bad[^1] = 0xFE; // -2
        Assert.Throws<NetProtocolException>(() => DecodeCar(bad));

        var members = new[]
        {
            new NetLobbyMember(0, "alice", true, 3, true, true, 0x45),
            new NetLobbyMember(1, "bob", false, 4, false, true),
        };
        var state = new NetLobbyState(7, Epoch, "G", NetGamePhase.Lobby, 1, 2, 4, NetGameMode.SingleRace, 0, members);
        var back = DecodeState(state.Encode());
        Assert.Equal([0x45, NetProtocol.NoColour], back.Members.Select(m => m.Colour));
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Members = [members[0] with { Colour = -2 }] }).Encode());
    }

    [Fact]
    public void InfoAndStateCarrySkillCatchUpAndStyle()
    {
        var info = new NetGameInfo(7, "Game", NetGamePhase.Lobby, 1, 4, 3, 2, NetGameMode.Knockout, false, -1, false, 0, false, 2);
        var infoBack = DecodeInfo(info);
        Assert.Equal((0, false, 2), (infoBack.Skill, infoBack.CatchUp, infoBack.Style));
        var wild = DecodeInfo(info with { Skill = 1, CatchUp = true, Style = 1 });
        Assert.Equal((1, true, 1), (wild.Skill, wild.CatchUp, wild.Style)); // expert, on, Wild
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Skill = 2 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Style = 3 }]).Encode());

        var member = new NetLobbyMember(0, "alice", false, NetProtocol.NoCarType, true, true);
        var state = new NetLobbyState(7, Epoch, "G", NetGamePhase.Lobby, 1, 2, 4, NetGameMode.SingleRace, 0, [member],
            Skill: 0, CatchUp: false, Style: 2);
        var back = DecodeState(state.Encode());
        Assert.Equal((0, false, 2), (back.Skill, back.CatchUp, back.Style));
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Skill = 2 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Style = 3 }).Encode());
        // the server defaults when a caller says nothing: expert, catch-up on, Wild
        var plain = new NetLobbyState(7, Epoch, "G", NetGamePhase.Lobby, 1, 2, 4, NetGameMode.SingleRace, 0, [member]);
        Assert.Equal((1, true, 1), (plain.Skill, plain.CatchUp, plain.Style));
    }

    // ---------------- the palette of a model ----------------

    [Theory]
    [InlineData(0, 0x3C, 6)]
    [InlineData(3, 0x42, 6)]
    [InlineData(8, 0x2A, 9)]
    [InlineData(4, 0x1E, 1)]    // count 0 in the table: the single colour
    public void ThePaletteOfAModelIsFirstToFirstPlusMaxOfOneAndCountMinusOne(int car, int first, int size)
    {
        Assert.False(GameSession.IsColourOfCar(car, first - 1));
        for (int i = 0; i < size; i++) Assert.True(GameSession.IsColourOfCar(car, first + i));
        Assert.False(GameSession.IsColourOfCar(car, first + size));
    }

    [Fact]
    public void ThePaletteCheckRefusesModelsOutsideTheShowcaseCars()
    {
        Assert.False(GameSession.IsColourOfCar(12, FrontEndConstants.ColourTable[24]));
        Assert.False(GameSession.IsColourOfCar(-1, 0));
    }

    // ---------------- server: defaults and operator edits ----------------

    static SessionHarness NewGame(int seed, NetGameMode mode, int max, out SessionTestClient alice, SessionServerOptions? options = null)
    {
        var h = new SessionHarness(seed, options ?? new SessionServerOptions { MaxRaceSteps = 30, CountdownMicros = 0 });
        alice = h.Join("alice");
        var a = alice;
        Assert.True(h.RunUntil(() => a.List != null, 1_000_000));
        a.Send(new NetGameCreate("game", max, mode).Encode());
        Assert.True(h.RunUntil(() => a.InLobby, 1_000_000));
        return h;
    }

    static SessionTestClient JoinLobby(SessionHarness h, SessionTestClient alice, string name)
    {
        var c = h.Join(name);
        Assert.True(h.RunUntil(() => c.List != null, 1_000_000));
        c.JoinGame(alice.GameId);
        Assert.True(h.RunUntil(() => c.InLobby && alice.Lobby!.Members.Count == h.Clients.Count, 1_000_000));
        return c;
    }

    [GameTheory]
    [InlineData(NetGameMode.SingleRace, 1)]
    [InlineData(NetGameMode.Knockout, 8)]
    public void AGameStartsWithTheServerDefaultsInLobbyAndList(NetGameMode mode, int max)
    {
        using var h = NewGame(500, mode, max, out var alice);
        var lobby = alice.Lobby!;
        Assert.Equal((0, 2, false, NetProtocol.AutoBots, false, 1, true, 1), (lobby.Track, lobby.Laps, lobby.Backward, lobby.Bots,
            lobby.Traffic, lobby.Skill, lobby.CatchUp, lobby.Style));
        Assert.Equal((max, mode), (lobby.MaxPlayers, lobby.Mode));
        Assert.True(alice.IsOperator);
        var bob = h.Join("bob");
        Assert.True(h.RunUntil(() => bob.List != null, 1_000_000));
        var info = bob.List!.Games.Single();
        Assert.Equal((0, 2, false, NetProtocol.AutoBots, false, 1, true, 1), (info.Track, info.Laps, info.Backward, info.Bots,
            info.Traffic, info.Skill, info.CatchUp, info.Style));
    }

    [GameFact]
    public void TheOperatorChangesEveryOptionOfASingleRaceAndEveryPlayerSeesIt()
    {
        using var h = NewGame(501, NetGameMode.SingleRace, 4, out var alice);
        var bob = JoinLobby(h, alice, "bob");

        alice.SetOptions(5, 4, true, 2, true, 0, false, 2);
        Assert.True(h.RunUntil(() => bob.Lobby!.Style == 2, 1_000_000));
        foreach (var l in new[] { alice.Lobby!, bob.Lobby! })
            Assert.Equal((5, 4, true, 2, true, 0, false, 2), (l.Track, l.Laps, l.Backward, l.Bots, l.Traffic, l.Skill, l.CatchUp, l.Style));
        var game = h.Server.Games.Single();
        Assert.Equal((0, false, 2), (game.Skill, game.CatchUp, game.Style));
        var info = game.ToInfo();
        Assert.Equal((0, false, 2, 2, true), (info.Skill, info.CatchUp, info.Style, info.Bots, info.Traffic));

        // each option on its own
        alice.SetOptions(5, 4, bots: -1);
        Assert.True(h.RunUntil(() => bob.Lobby!.Bots == -1, 1_000_000));
        alice.SetOptions(5, 4, traffic: false);
        Assert.True(h.RunUntil(() => !bob.Lobby!.Traffic, 1_000_000));
        alice.SetOptions(5, 4, skill: 1);
        Assert.True(h.RunUntil(() => bob.Lobby!.Skill == 1, 1_000_000));
        alice.SetOptions(5, 4, catchUp: true);
        Assert.True(h.RunUntil(() => bob.Lobby!.CatchUp, 1_000_000));
        alice.SetOptions(5, 4, style: 0);
        Assert.True(h.RunUntil(() => bob.Lobby!.Style == 0, 1_000_000));
    }

    [GameFact]
    public void AnotherPlayerCannotChangeTheOptionsAndAnInvalidValueChangesNothing()
    {
        using var h = NewGame(502, NetGameMode.SingleRace, 4, out var alice);
        var bob = JoinLobby(h, alice, "bob");
        long refused = h.Server.Stats.RefusedLobby;
        bob.SetOptions(3, 3, skill: 0, style: 2);
        h.Run(300_000);
        Assert.True(h.Server.Stats.RefusedLobby > refused);
        var game = h.Server.Games.Single();
        Assert.Equal((0, 2, 1, 1), (game.Track, game.Laps, game.Skill, game.Style));
        Assert.Equal(1, alice.Lobby!.Skill);
    }

    [GameFact]
    public void TheOptionsCanBeChangedAgainAfterARaceReturnedToTheLobby()
    {
        using var h = NewGame(503, NetGameMode.SingleRace, 2, out var alice);
        alice.SetOptions(1, 2, skill: 0, style: 2);
        Assert.True(h.RunUntil(() => alice.Lobby!.Style == 2, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        var game = h.Server.Games.Single();
        Assert.Equal((0, 1), (game.Round!.Host.Runner.Session.Setup.Skill, game.Round.Host.Runner.Session.Setup.Style)); // Arcade = style tag 1
        Assert.True(h.RunUntil(() => game.Phase == NetGamePhase.Lobby && alice.InLobby, 60_000_000));

        alice.SetOptions(3, 3, true, 1, true, 1, false, 0);
        Assert.True(h.RunUntil(() => alice.Lobby!.Style == 0 && alice.Lobby.Bots == 1, 1_000_000));
        Assert.Equal((3, 3, true, 1, true, 1, false, 0),
            (game.Track, game.Laps, game.Backward, game.Bots, game.Traffic, game.Skill, game.CatchUp, game.Style));
    }

    [GameFact]
    public void KnockoutBeforeTheFirstRaceChangesDirectionLapsSkillCatchUpAndStyleButNotTrackBotsOrTraffic()
    {
        using var h = NewGame(504, NetGameMode.Knockout, 4, out var alice);
        alice.SetOptions(6, 5, true, 3, true, 0, false, 2);
        Assert.True(h.RunUntil(() => alice.Lobby!.Style == 2, 1_000_000));
        var l = alice.Lobby!;
        Assert.Equal((5, true, 0, false, 2), (l.Laps, l.Backward, l.Skill, l.CatchUp, l.Style));
        Assert.Equal(0, l.Track);                          // the track follows the stage of the series
        Assert.Equal((NetProtocol.AutoBots, false), (l.Bots, l.Traffic)); // no effect in a series: ignored
    }

    [GameFact]
    public void AfterTheFirstRaceOfASeriesEveryOptionIsRefused()
    {
        using var h = SessionKnockoutTests.Knockout(505, SessionKnockoutTests.Options(), "alice", "bob");
        var alice = h.Clients[0];
        SessionKnockoutTests.PlayRound(h, alice);
        Assert.False(alice.Lobby!.Series!.Finished);
        var before = alice.Lobby;
        long refused = h.Server.Stats.RefusedLobby;
        alice.SetOptions(1, 7, true, 0, true, 0, false, 2);
        h.Run(500_000);
        Assert.True(h.Server.Stats.RefusedLobby > refused);
        var after = alice.Lobby!;
        Assert.Equal((before.Laps, before.Backward, before.Skill, before.CatchUp, before.Style, before.Bots, before.Traffic),
            (after.Laps, after.Backward, after.Skill, after.CatchUp, after.Style, after.Bots, after.Traffic));
    }

    // ---------------- server: car and colour ----------------

    [GameFact]
    public void AColourOfThePaletteOfTheModelIsAcceptedAndShownInTheRoster()
    {
        using var h = NewGame(506, NetGameMode.SingleRace, 4, out var alice);
        var bob = JoinLobby(h, alice, "bob");
        int first = FrontEndConstants.ColourTable[3 * 2];
        alice.SetCar(3, first + 2);
        Assert.True(h.RunUntil(() => bob.Lobby!.Members.Single(m => m.Name == "alice").Colour == first + 2, 1_000_000));
        Assert.Equal(3, bob.Lobby!.Members.Single(m => m.Name == "alice").CarType);
        Assert.Equal(NetProtocol.NoColour, bob.Lobby.Members.Single(m => m.Name == "bob").Colour);
        // back to the model's default colour
        alice.SetCar(3);
        Assert.True(h.RunUntil(() => bob.Lobby!.Members.Single(m => m.Name == "alice").Colour == NetProtocol.NoColour, 1_000_000));
    }

    [GameFact]
    public void AColourOutsideThePaletteIsRefusedAndThePreviousChoiceStays()
    {
        using var h = NewGame(507, NetGameMode.SingleRace, 4, out var alice);
        int first = FrontEndConstants.ColourTable[3 * 2], count = FrontEndConstants.ColourTable[3 * 2 + 1];
        alice.SetCar(3, first);
        Assert.True(h.RunUntil(() => alice.Me!.Colour == first, 1_000_000));
        long refused = h.Server.Stats.RefusedLobby;
        alice.SetCar(3, first + count);        // one past the palette
        alice.SetCar(5, first);                // a colour of another model
        h.Run(300_000);
        Assert.True(h.Server.Stats.RefusedLobby >= refused + 2);
        Assert.Equal((3, first), (alice.Me!.CarType, alice.Me.Colour));
    }

    [GameFact]
    public void TheModelWithASingleColourAcceptsOnlyThatColourOrNone()
    {
        using var h = NewGame(508, NetGameMode.SingleRace, 4, out var alice);
        int only = FrontEndConstants.ColourTable[4 * 2];
        Assert.Equal(0, FrontEndConstants.ColourTable[4 * 2 + 1]);
        alice.SetCar(4, only + 1);
        h.Run(200_000);
        Assert.NotEqual(4, alice.Me!.CarType);
        alice.SetCar(4, only);
        Assert.True(h.RunUntil(() => alice.Me!.CarType == 4 && alice.Me.Colour == only, 1_000_000));
        alice.SetCar(4);
        Assert.True(h.RunUntil(() => alice.Me!.Colour == NetProtocol.NoColour, 1_000_000));
    }

    [GameFact]
    public void TheCheatCarsAreRefusedByTheServer()
    {
        using var h = NewGame(509, NetGameMode.SingleRace, 4, out var alice);
        alice.SetCar(2);
        Assert.True(h.RunUntil(() => alice.Me!.CarType == 2, 1_000_000));
        var bytes = new NetLobbyCar(alice.GameId, alice.Epoch, 11).Encode();
        foreach (byte cheat in new byte[] { 12, 13, 14 })
        {
            bytes[NetHeader.Size] = cheat;
            long malformed = h.Server.Stats.Malformed;
            alice.Send(bytes);
            h.Run(100_000);
            Assert.True(h.Server.Stats.Malformed > malformed);
            Assert.Equal(2, alice.Me!.CarType);
        }
    }

    [GameFact]
    public void ACarChangeInASeriesIsOpenBeforeTheFirstRaceAndFrozenAfterIt()
    {
        using var h = SessionKnockoutTests.Knockout(510, SessionKnockoutTests.Options(), "alice", "bob");
        var alice = h.Clients[0];
        int first = FrontEndConstants.ColourTable[7 * 2];
        alice.SetCar(7, first);
        Assert.True(h.RunUntil(() => alice.Me!.CarType == 7 && alice.Me.Colour == first, 1_000_000));

        SessionKnockoutTests.PlayRound(h, alice);
        Assert.False(alice.Lobby!.Series!.Finished);
        long refused = h.Server.Stats.RefusedLobby;
        alice.SetCar(2);
        alice.SetCar(7, first + 1);
        h.Run(500_000);
        Assert.True(h.Server.Stats.RefusedLobby >= refused + 2);
        Assert.Equal((7, first), (alice.Me!.CarType, alice.Me.Colour));
    }

    // ---------------- the composed race ----------------

    static ServerRaceConfig Config(int? skill = null, bool? catchUp = null, int? style = null) =>
        new() { Skill = skill, CatchUp = catchUp, Style = style };

    [GameTheory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public void ASingleRaceSetupTakesSkillAndCatchUpFromTheConfig(int skill, bool catchUp)
    {
        var setup = Config(skill, catchUp).BuildSetup(GameData.Dir, 2, 6);
        Assert.Equal(skill, setup.Skill);
        Assert.Equal(catchUp, setup.Get(0x18) != 0);
    }

    [GameTheory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public void AKnockoutSetupTakesSkillAndCatchUpEvenThoughTheFrontEndForcesTheExpert(int skill, bool catchUp)
    {
        var series = KnockoutSeries.ForNetwork(2);
        var setup = Config(skill, catchUp).BuildKnockoutSetup(GameData.Dir, series, null);
        Assert.Equal(2, setup.RaceType);
        Assert.Equal(skill, setup.Skill);   // the original forces the expert here; a network game does not
        Assert.Equal(catchUp, setup.Get(0x18) != 0);
        // the offline front-end keeps forcing the expert in a knockout (the original behaviour)
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 1;
        cfg.RaceType = 2;
        cfg.Skill = 0;
        var offline = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        Assert.Equal(1, offline.Skill);
    }

    [GameFact]
    public void WithoutAnOverrideTheSetupKeepsTheValuesOfTheHostConfig()
    {
        var plain = new ServerRaceConfig().BuildSetup(GameData.Dir, 2, 6);
        var same = Config().BuildSetup(GameData.Dir, 2, 6);
        Assert.Equal(plain.Block, same.Block);
    }

    [GameTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ThePhysicsStyleReachesBothSetupsAsTheMenuEncodesIt(int style)
    {
        var single = Config(style: style).BuildSetup(GameData.Dir, 2, 6);
        var ko = Config(style: style).BuildKnockoutSetup(GameData.Dir, KnockoutSeries.ForNetwork(2), null);
        foreach (var setup in new[] { single, ko })
        {
            Assert.Equal(style == 2 ? 1 : style, setup.Style); // Arcade is style tag 1 ...
            Assert.Equal(style == 1, setup.WildArcade);         // ... without the aids of Wild (tag 0x21 = 0)
        }
    }

    [GameFact]
    public void AnInvalidStyleOrSkillIsRefusedByTheConfig()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Config(style: 3).BuildSetup(GameData.Dir, 2, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => Config(skill: 2).BuildSetup(GameData.Dir, 2, 6));
    }

    [GameFact]
    public void TheColourOfEachHumanCarReachesItsRecordAndNoColourLeavesTheRecordAlone()
    {
        var race = new ServerRaceConfig();
        var plain = race.BuildSetup(GameData.Dir, 2, 6, [3, 5], null);
        var chosen = race.BuildSetup(GameData.Dir, 2, 6, [3, 5], [0x45, NetProtocol.NoColour]);
        Assert.Equal(0x45, chosen.Car(0).Colour);
        Assert.Equal(plain.Car(1).Colour, chosen.Car(1).Colour);
        int offset = 0x78 + 0xC;
        for (int i = 0; i < plain.Block.Length; i++)
            if (i < offset || i >= offset + 4) Assert.Equal(plain.Block[i], chosen.Block[i]);
        Assert.Equal(plain.Block, race.BuildSetup(GameData.Dir, 2, 6, [3, 5], [NetProtocol.NoColour, NetProtocol.NoColour]).Block);
        Assert.Throws<ArgumentOutOfRangeException>(() => race.BuildSetup(GameData.Dir, 2, 6, [3, 5], [1]));

        var series = KnockoutSeries.ForNetwork(2);
        var koPlain = race.BuildKnockoutSetup(GameData.Dir, series, [3, 5]);
        var ko = race.BuildKnockoutSetup(GameData.Dir, series, [3, 5], [0x21, 0x22]);
        Assert.Equal([0x21, 0x22], [ko.Car(0).Colour, ko.Car(1).Colour]);
        Assert.Equal(koPlain.Car(2).Colour, ko.Car(2).Colour);
        Assert.Equal(koPlain.Block, race.BuildKnockoutSetup(GameData.Dir, series, [3, 5], [NetProtocol.NoColour, NetProtocol.NoColour]).Block);
    }

    [GameFact]
    public void TheSessionComposesSingleRaceAndKnockoutWithTheChosenSkillCatchUpStyleAndColour()
    {
        using var h = NewGame(511, NetGameMode.SingleRace, 2, out var alice);
        int first = FrontEndConstants.ColourTable[2 * 2];
        alice.SetOptions(1, 2, skill: 0, catchUp: false, style: 2);
        alice.SetCar(2, first + 1);
        Assert.True(h.RunUntil(() => alice.Lobby!.Style == 2 && alice.Me!.Colour == first + 1, 1_000_000));
        alice.RequestStart();
        Assert.True(h.RunUntil(() => h.Server.Games.Single().Round != null, 8_000_000));
        var setup = h.Server.Games.Single().Round!.Host.Runner.Session.Setup;
        Assert.Equal((0, false, 1), (setup.Skill, setup.Get(0x18) != 0, setup.Style));
        Assert.Equal((2, first + 1), (setup.Car(0).CarType, setup.Car(0).Colour));

        using var k = SessionKnockoutTests.Knockout(512, SessionKnockoutTests.Options(), "alice", "bob");
        var op = k.Clients[0];
        op.SetOptions(0, 2, skill: 0, catchUp: false, style: 0);
        int f7 = FrontEndConstants.ColourTable[7 * 2];
        op.SetCar(7, f7 + 2);
        Assert.True(k.RunUntil(() => op.Lobby!.Style == 0 && op.Lobby.Skill == 0 && op.Me!.Colour == f7 + 2, 1_000_000));
        op.RequestStart();
        Assert.True(k.RunUntil(() => k.Server.Games.Single().Round != null, 8_000_000));
        var ko = k.Server.Games.Single().Round!.Host.Runner.Session.Setup;
        Assert.Equal((2, 0, false, 0), (ko.RaceType, ko.Skill, ko.Get(0x18) != 0, ko.Style));
        Assert.Equal((7, f7 + 2), (ko.Car(0).CarType, ko.Car(0).Colour));
    }

    // ---------------- the options really change the race ----------------

    /// <summary>Positions of the AI cars after <paramref name="steps"/> host steps of the same race (same race id = same seed);
    /// the human drives straight on full throttle.</summary>
    static long[] AiState(ServerRaceConfig config, int steps)
    {
        using var host = config.CreateHost(GameData.Dir, 1234, 1, 7);
        for (int i = 0; i < steps; i++) host.Step(new PlayerInput(0, 255, 0, 0));
        return host.Runner.Cars.Where(c => c.Ai != null).SelectMany(c => new long[] { c.Obj.Pos.X, c.Obj.Pos.Y, c.Obj.Pos.Z }).ToArray();
    }

    [GameFact]
    public void TheSameRaceRunsDifferentlyForBeginnerAndExpertBots()
    {
        var expert = AiState(Config(skill: 1, catchUp: true), 900);
        var beginner = AiState(Config(skill: 0, catchUp: true), 900);
        Assert.NotEqual(expert, beginner);
        Assert.Equal(expert, AiState(Config(skill: 1, catchUp: true), 900)); // deterministic for equal options
    }

    [GameFact]
    public void TheSameRaceRunsDifferentlyWithAndWithoutCatchUp()
    {
        var on = AiState(Config(skill: 1, catchUp: true), 1500);
        var off = AiState(Config(skill: 1, catchUp: false), 1500);
        Assert.NotEqual(on, off);
    }
}
