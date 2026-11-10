using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class RaceArgsTests
{
    private const string Prefs = "GAMEDATA/PREFS/PC/";
    private static readonly Lazy<RaceTags> Tags = new(() => RaceTags.FromConstants());

    private static RaceSetup FromText(string file)
    {
        var c = new RaceArgsCompiler(Tags.Value, f => GameFiles.Exists(Prefs + f, GameData.Dir) ? GameData.ReadRaw(Prefs + f) : null);
        c.AddText(GameData.ReadRaw(Prefs + file));
        return RaceArgsParser.Parse(Tags.Value, c.ToArray());
    }

    [GameFact]
    public void Tables_FromExe()
    {
        var t = Tags.Value;
        Assert.Equal(130, t.Keys.Count); // number of keys in the table [ref:3196]
        Assert.Equal(1, t.TagOf("import"));
        Assert.Equal(0x22, t.TagOf("LINEUP"));
        Assert.Equal(0x7B, t.TagOf("car"));
        Assert.Equal(0x86, t.TagOf(".GearRatio"));
        Assert.Equal(0x7A, t.TagOf("DEBUG_KEY31"));
        Assert.Equal(0x10, t.SymbolValue("numCars"));
        Assert.Equal(6, t.SymbolValue("GameSetup_kJaguar_XJ220"));
        Assert.Equal(0x4D, t.SymbolValue("SC_RIGHT"));
        Assert.Equal(["Null", "Analog", "Button", "Key", "Intface"], t.Devices);
        Assert.Equal(3, t.DeviceIndex("key"));
        // RAMP (7), REPLAY (0xE) absent → ignored by parser [ref:3197]
        Assert.False(t.ArgOffsets.ContainsKey(7));
        Assert.False(t.ArgOffsets.ContainsKey(0xE));
        Assert.Equal(0x24, t.ArgOffsets[0xA]);
        Assert.Equal(0x478, t.ArgOffsets[0x22]);
        Assert.Equal(0x4F8, t.ArgOffsets[0xF]);
        Assert.Equal(0x78 + 0x30, t.ArgOffsets[0x87]);
    }

    [GameFact]
    public void DefaultTxt()
    {
        var r = FromText("DEFAULT.TXT");
        Assert.Equal(0, r.RaceType);
        Assert.Equal(2, r.NumLaps);
        Assert.Equal(1, r.Skill);
        Assert.Equal(0, r.CommMode);
        Assert.Equal(5, r.Track);
        Assert.Equal(1234567, r.Random);
        Assert.Equal(8, r.NumCars);
        Assert.Equal(3, r.NumSmackables);
        Assert.Equal(1, r.NumPlayerCars);
        Assert.Equal(127, r.Get(0x5C)); // MUSICLEVEL [ref:3198]
        Assert.Equal(6, r.Car(0).CarType); // car[0 to numCars] = Jaguar XJ220
        Assert.Equal(3, r.Car(1).CarType); // car[1 to numCars] = Ford GT90
        Assert.Equal(3, r.Car(3).CarType);
        for (int i = 0; i < 12; i++) Assert.Equal(i, r.GridSlot(i)); // lineup = 0..11
        Assert.Equal(r.NumLaps, r.Config.NumLaps); // shared layout with RaceConfig
    }

    [GameFact]
    public void TwoPlayerTxt()
    {
        var r = FromText("2PLAYER.TXT");
        Assert.Equal(2, r.NumCars);
        Assert.Equal(2, r.NumPlayerCars);
        Assert.Equal(1, r.CommMode); // split screen
        var c0 = r.Car(0);
        // MacLaren F1, Human, auto; the three ramps (+0x10/+0x14/+0x18) are all on for a [ref:3199]
        // front-end-built player car (the replay's human car has steering off, 0/1/1).
        Assert.Equal(new CarSetup(0, 1, 1, 0, 0, SteerRamp: true, SlowThrottle: true, BrakeRamp: true), c0);
        Assert.Equal(65536, r.Get(0x78 + 0x1C)); // frontDownForce
        Assert.Equal((3, 0x4D), r.Key(0x24)); // C1_RIGHT = key,SC_RIGHT
        Assert.Equal((3, 0x48), r.Key(0x25)); // C1_GAS = key,SC_UP
    }

    [GameFact]
    public void ReplayBlock_RoundTrip()
    {
        var rpy = Replay.Load(gameDir: GameData.Dir);
        var stream = RaceArgsParser.StreamFromBlock(Tags.Value, rpy.Config.Raw);
        var r = RaceArgsParser.Parse(Tags.Value, stream);
        Assert.Equal(4, r.Track);
        Assert.Equal(8, r.NumCars);
        Assert.Equal(8, r.NumLaps);
        Assert.Equal(rpy.Config.Track, r.Config.Track);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(rpy.Config.Car(i).CarType, r.Car(i).CarType);
            Assert.Equal(rpy.Config.Car(i).CarClass, r.Car(i).CarClass);
            Assert.Equal(rpy.Config.GridSlot(i), r.GridSlot(i));
        }
        foreach (var off in Tags.Value.ArgOffsets.Where(kv => kv.Key < 0x22).Select(kv => kv.Value))
            Assert.Equal(rpy.Config.Raw.AsSpan(off, 4).ToArray(), r.Block.AsSpan(off, 4).ToArray());
    }

    [Fact]
    public void Parser_Synthetic()
    {
        var tags = new RaceTags([("TRACK", 0xA), ("LINEUP", 0x22), ("CAR", 0x7B), (".carClass", 0x7C), ("C1_LEFT", 0x23), ("RACETYPE", 2)],
            [("GameSetup_kAI", 2)], ["Null", "Analog", "Button", "Key"],
            new Dictionary<int, int> { [2] = 0, [0xA] = 0x24, [0x22] = 0x478, [0x7C] = 0x7C });
        var c = new RaceArgsCompiler(tags);
        c.AddText("Track = 0x0C\nramp = 5\nlineup = 2,1\ncar[1 to 2].carClass = GameSetup_kAI # c\nC1_LEFT = key, 75\nracetype=1\n"u8.ToArray());
        int[] s = c.ToArray();
        Assert.Equal([0xA, 12, 0x22, 2, 2, 1, 0x7C, 1, 2, 0x7C, 2, 2, 0x23, 75 << 8 | 3, 2, 1, 0, 0], s);
        var r = RaceArgsParser.Parse(tags, s);
        Assert.Equal(12, r.Track);
        Assert.Equal(2, r.GridSlot(0));
        Assert.Equal(1, r.GridSlot(1));
        Assert.Equal(0, r.Car(0).CarClass);
        Assert.Equal(2, r.Car(2).CarClass);
        Assert.Equal((3, 75), r.Key(0x23));
        Assert.Equal(1, r.Skill); // RACETYPE≠0 → SKILL=1
        Assert.Equal(-5, c.ParseNumber("-5"));
    }
}
