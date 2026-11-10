using NFS.Data;
using NFS.Data.Ai;
using NFS.Data.Track;

namespace NFS.Tests.Data;

public class AiFilesTests
{
    private static int Fx(double v) => (int)Math.Round(v * 65536);

    [GameFact]
    public void Prsonal()
    {
        var p = NFS.Data.Ai.Prsonal.Parse(GameData.ReadRaw("GAMEDATA/SIM/AI/PRSONAL.DAT"));
        Assert.Equal(13, p.Personalities.Count);
        for (int i = 0; i < 13; i++) Assert.Equal((char)('a' + i), p.Personalities[i].Letter);
        Assert.All(p.Grid[2], b => Assert.Equal((byte)'m', b));
        Assert.All(p.Grid.SelectMany(r => r), b => Assert.InRange(b, (byte)'a', (byte)'n'));
        Assert.All(p.Personalities, x => Assert.InRange(CarFx(x.SpeedGlue[10]), 0.5, 3.0));
        Assert.NotNull(p.ByLetter('c'));
    }

    private static double CarFx(int v) => v / 65536.0;

    [GameFact]
    public void TrafCfg()
    {
        var raw = GameData.ReadRaw("GAMEDATA/SIM/AI/TRAFCFG.DAT");
        Assert.Equal(NFS.Data.Ai.TrafCfg.Size, raw.Length);
        var t = NFS.Data.Ai.TrafCfg.Parse(raw);
        Assert.Equal(Fx(21.0), t.OppDesiredAhead);
        Assert.Equal(4, t.NumTrafficCars);
        Assert.Equal(Fx(1.0), t.TrafficSpeedFactors[0]);
        Assert.Equal(Fx(150), t.TrafficBaseSpeedKmh);
        Assert.Equal(Fx(-1.5), t.TrafficLaneOffsets[0]);
        Assert.Equal((4, 2), (t.LatVelLookahead, t.MinLookahead));
        Assert.Equal(Fx(11.0), t.SkidValue);
        Assert.Equal(Fx(5.0), t.InControl[0]);
        Assert.Equal(Fx(16.0), t.OutOfControl[1]);
        // the copy inside CARDATA.VIV is identical
        Assert.Equal(raw, Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir).Get("TRAFCFG.DAT"));
    }

    [GameFact]
    public void SpeedsAndLines()
    {
        int n = 0;
        foreach (var rel in GameData.Files("GAMEDATA/TRACKS/SPEEDS", ".QPS", ".QAS", ".QPL", ".QAL"))
        {
            var raw = GameData.ReadRaw(rel);
            Assert.True(EaCompression.IsCompressed(raw), rel);
            bool speed = rel.EndsWith('S');
            var unpacked = speed ? AiSpeeds.Parse(raw).Raw : AiLine.Parse(raw).Raw;
            var plain = Path.ChangeExtension(rel, (rel[^3..]) switch { "QPS" => ".PSP", "QAS" => ".ASP", "QPL" => ".PLN", _ => ".ALN" });
            // TR00.QPS/.QPL == unpacked.PSP/.PLN byte for byte (other loose copies are older revisions) [ref:3115]
            if (Path.GetFileName(rel) is "TR00.QPS" or "TR00.QPL") Assert.Equal(GameData.ReadRaw(plain), unpacked);
            if (speed) Assert.Equal(0, unpacked.Length % 2);
            n++;
        }
        Assert.Equal(64, n);
        var col = Col.Load("GAMEDATA/TRACKS/SE/TR00.COL", GameData.Dir);
        var sp = AiSpeeds.Parse(GameData.ReadRaw("GAMEDATA/TRACKS/SPEEDS/TR00.QPS"));
        var ln = AiLine.Parse(GameData.ReadRaw("GAMEDATA/TRACKS/SPEEDS/TR00.QPL"));
        Assert.InRange(sp.NumSlices, col.Slices.Length - 8, col.Slices.Length + 8);
        Assert.InRange(sp.SpeedAt(100), 10, 255);
        Assert.InRange(ln.LaneAt(100), 0, 16);
    }

    [GameFact]
    public void ParkedCars()
    {
        int n = 0;
        foreach (var rel in GameData.Files("GAMEDATA/TRACKS/DATA/PC", ".DAT").Where(r => Path.GetFileName(r).StartsWith("SM", StringComparison.OrdinalIgnoreCase)))
        {
            var s = StaticCars.Parse(GameData.ReadRaw(rel));
            Assert.Equal(rel.Contains("SM08", StringComparison.OrdinalIgnoreCase) ? 2 : 3, s.Cars.Count);
            n++;
        }
        Assert.Equal(8, n);
        var sm0 = StaticCars.Parse(GameData.ReadRaw("GAMEDATA/TRACKS/DATA/PC/SM00.DAT"));
        Assert.Equal((1, 0x4DC), (sm0.Cars[0].Model, sm0.Cars[0].Slice));
    }
}
