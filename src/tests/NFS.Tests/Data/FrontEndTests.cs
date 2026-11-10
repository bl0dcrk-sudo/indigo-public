using NFS.Data;
using NFS.Data.FrontEnd;

namespace NFS.Tests.Data;

public class FrontEndTests
{
    [GameFact]
    public void TextFiles()
    {
        var eng = TextFile.Load("ENG", GameData.Dir);
        Assert.Equal(1498, eng.Entries.Count);
        Assert.Equal((0x12, 0, 2, 1), (eng.Entries[0].FontSize, eng.Entries[0].Colour, eng.Entries[0].HighlightColour, eng.Entries[0].Align));
        Assert.Equal((168, 126), (eng.Entries[0].X, eng.Entries[0].Y));
        Assert.False(string.IsNullOrEmpty(eng[0]));
        foreach (var lang in new[] { "GER", "FRE", "SPA", "ITA", "SWE" })
            Assert.Equal(1498, TextFile.Load(lang, GameData.Dir).Entries.Count);
    }

    [GameFact]
    public void Config()
    {
        var c = FeConfig.Parse(GameData.ReadRaw("FEDATA/PC/CONFIG/CONFIG.DAT"));
        Assert.InRange(c.Language, 0, 5);
        Assert.InRange(c.Laps, 0, 16);
        Assert.InRange(c.Skill, 0, 1);
        Assert.Throws<InvalidDataException>(() => FeConfig.Parse(new byte[10]));
    }

    [GameFact]
    public void ConfigRoundTripAndLapsOffset()
    {
        var raw = GameData.ReadRaw("FEDATA/PC/CONFIG/CONFIG.DAT");
        var c = FeConfig.Parse(raw);
        Assert.Equal(raw, c.ToBytes());                       // 1:1 0x2AC B
        Assert.Equal(raw[0x18], c.Laps);                      // not +0x19 [ref:3120]
        Assert.Equal(raw[0x19], c.LapsMenuIndex);
        c.Laps = 7; c.Track = 0x0102; c.Skill = 1;
        var b = c.ToBytes();
        Assert.Equal(0x2AC, b.Length);
        Assert.Equal(7, b[0x18]);
        Assert.Equal(raw[0x19], b[0x19]);
        Assert.Equal(new byte[] { 0x02, 0x01 }, b[0x10..0x12]);
        Assert.Equal(1, b[0x0D]);
        Assert.Equal(raw[0x18], GameData.ReadRaw("FEDATA/PC/CONFIG/CONFIG.DAT")[0x18]);
    }

    [Fact]
    public void ConfigSetters_WriteExpectedOffsets()
    {
        var c = new FeConfig(new byte[FeConfig.Size]) { Laps = 8, LapsMenuIndex = 3, Seed = 0xBEEF, OpponentCount = 2 };
        var tmp = Path.GetTempFileName();
        try
        {
            c.Save(tmp);
            var b = File.ReadAllBytes(tmp);
            Assert.Equal(FeConfig.Size, b.Length);
            Assert.Equal((8, 3, 0xEF, 0xBE, 2), (b[0x18], b[0x19], b[0], b[1], b[0x56]));
            Assert.Equal(8, FeConfig.Parse(b).Laps);
        }
        finally { File.Delete(tmp); }
    }

    [GameFact]
    public void TrackStatsMatchSsf()
    {
        int n = 0;
        foreach (var rel in GameData.Files("FEDATA/PC/STATS", ".STF").Where(r => !r.Contains("/PRH/", StringComparison.OrdinalIgnoreCase)))
        {
            var raw = GameData.ReadRaw(rel);
            Assert.Equal(620, raw.Length);
            var stf = StatsFile.Parse(raw);
            Assert.Equal(31, stf.Records.Count);
            Assert.All(stf.Records, r => { Assert.InRange(r.Car, 0, 15); Assert.InRange(r.Time, 1, 0x38400); Assert.InRange(r.Skill, 0, 2); });
            var ssf = StatsFile.FromTrackSsf(GameData.ReadRaw(Path.ChangeExtension(rel, ".SSF")));
            Assert.Equal(31, ssf.Records.Count);
            n++;
        }
        Assert.Equal(8, n);
        foreach (var rel in GameData.Files("FEDATA/PC/STATS/PRH", ".STF"))
            Assert.Equal(451, StatsFile.Parse(GameData.ReadRaw(rel)).Records.Count);
    }

    [GameFact]
    public void ReplayFromGame()
    {
        var raw = GameData.ReadRaw("REPLAY.RPY");
        Assert.Equal(0x1E5B4, raw.Length);
        var r = Replay.Parse(raw);
        Assert.Equal(0x8D34, r.RaceTicks);
        // track 4, 8 cars (1 human + 7 AI), wild arcade, backwards, 8 laps [ref:3121]
        Assert.Equal(2, r.Config.ReplayMode);
        Assert.Equal(4, r.Config.Track);
        Assert.Equal(8, r.Config.NumLaps);
        Assert.Equal((8, 1, 7), (r.Config.NumCars, r.Config.NumPlayerCars, r.Config.NumOpponentCars));
        Assert.NotEqual(0, r.Config.WildArcade);
        Assert.NotEqual(0, r.Config.Backward);
        Assert.Equal(1, r.Config.Car(0).CarClass);
        Assert.All(Enumerable.Range(1, 7), i => Assert.Equal(2, r.Config.Car(i).CarClass));
        // 1 player: one packet per 32 ticks at 32 Hz = race ticks (64 Hz) / 64
        Assert.InRange(r.Packets.Count, r.RaceTicks / 64, r.RaceTicks / 64 + 2);
        Assert.All(r.Packets, p =>
        {
            Assert.Equal(32, p.Steer.Length); Assert.Equal(32, p.Gas.Length);
            Assert.Equal(32, p.Brake.Length); Assert.Equal(32, p.Buttons.Length);
        });
        Assert.All(r.Packets.SelectMany(p => p.Gas), g => Assert.InRange(g, 0, 31));
        Assert.Contains(r.Packets, p => p.Gas.Any(g => g > 0));
    }

    [Fact]
    public void RleDecode()
    {
        Assert.Equal([1, 7, 7, 7, 2], Replay.DecodeRle([1, 0xFF, 3, 7, 2]));
    }
}

public class PrefsTests
{
    [GameFact]
    public void DefaultTxt()
    {
        var p = PrefsText.Load("GAMEDATA/PREFS/PC/DEFAULT.TXT", GameData.Dir);
        Assert.Equal(["GameSetup_kSingleRace"], p.Find("RaceType")!.Values);
        Assert.Equal(["1234567"], p.Find("random")!.Values);
        Assert.Equal(12, p.Find("lineup")!.Values.Length);
        Assert.Equal(["JOYSTICK"], p.Find("CONTROL")!.Values);
        foreach (var rel in GameData.Files("GAMEDATA/PREFS/PC", ".TXT"))
            Assert.NotEmpty(PrefsText.Load(rel, GameData.Dir).Entries);
    }

    [GameFact]
    public void HudCon()
    {
        Assert.Equal(HudConfig.Size, HudConfig.Parse(GameData.ReadRaw("FEDATA/PC/CONFIG/HUDCON.DAT")).Raw.Length);
    }

    [Fact]
    public void ParsesCommentsAndLists()
    {
        var p = PrefsText.Parse("# c\nA = 1, 2 # x\n\nb=GameSetup_kExpert\n"u8.ToArray());
        Assert.Equal(2, p.Entries.Count);
        Assert.Equal(["1", "2"], p.Find("a")!.Values);
    }
}
