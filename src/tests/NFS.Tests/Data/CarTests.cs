using NFS.Data;
using NFS.Data.Car;

namespace NFS.Tests.Data;

public class CarTests
{
    private static Viv CarData() => Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
    private static int Fx(double v) => (int)Math.Round(v * 65536);

    [GameFact]
    public void AllGeoModelsParse()
    {
        var viv = CarData();
        int n = 0;
        foreach (var e in viv.Entries.Where(e => e.Name.EndsWith(".geo", StringComparison.OrdinalIgnoreCase)))
        {
            var d = viv.Get(e.Name);
            var g = CarGeo.Parse(d);
            Assert.InRange(g.Unknown_0x00, 0u, 2u);
            Assert.Equal(d.Length, g.ParsedLength); // sum of parts == file size
            foreach (var p in g.Parts)
                Assert.All(p.Polygons, q => Assert.True(Math.Max(Math.Max(q.V0, q.V1), Math.Max(q.V2, q.V3)) < Math.Max(1, p.NumVerts), $"{e.Name} part {p.Index}"));
            n++;
        }
        Assert.Equal(53, n);
        var f355 = CarGeo.Parse(viv.Get("F355.GEO"));
        Assert.Equal((40, 20, 39321), (f355.Parts[0].NumVerts, f355.Parts[0].NumPolys, f355.Parts[0].Y));
        Assert.Equal([1, 0, 0, 0, 1, 0], f355.Parts[0].AngleRanges.Select(x => (int)x));
        Assert.All(f355.Parts[0].Polygons, p => Assert.Equal(4, p.Texture.Length));
    }

    /// <summary>relocates the wheels of the loaded copy of the geo before anything reads it: [ref:3116]
    /// moves parts 0xC/0xE for car type 4 (IDGO) only [ref:3117]
    /// moves parts 0x10/0x12 for every type &lt; 0xF (gated on polygon 0 flag bit 1). Each shift adds ±5 [ref:3118]
    /// (1/256 m) to the x of polygon 0's four vertices: +5 right part, −5 left. This is what makes the JAGR body box
    /// 270/256 m wide instead of the 266/256 m of the raw file (original's trace: +0x108 = 69120).</summary>
    [GameFact]
    public void GeoWheelShift_MovesWheelPolygonOutward()
    {
        var d = CarData().Get("jagr.geo");
        var plain = CarGeo.Parse(d);
        var racer = CarGeo.Parse(d);
        racer.ApplyWheelShift(0);      // racer, type < 0xF: rear wheels only
        var idgo = CarGeo.Parse(d);
        idgo.ApplyWheelShift(4);       // type 4 IDGO: front + rear
        var traffic = CarGeo.Parse(d);
        traffic.ApplyWheelShift(0x11); // ≥ 0xF: nothing moves

        Assert.NotEqual(0, plain.Parts[0x10].Polygons[0].Flags & 2); // the rear shift's flag gate is satisfied
        void Check(CarGeo g, int part, int dx)
        {
            var a = plain.Parts[part];
            var b = g.Parts[part];
            Assert.Equal(4, a.NumVerts);
            foreach (byte v in new[] { a.Polygons[0].V0, a.Polygons[0].V1, a.Polygons[0].V2, a.Polygons[0].V3 })
            {
                Assert.Equal(a.Vertices[3 * v] + dx, b.Vertices[3 * v]);
                Assert.Equal(a.Vertices[3 * v + 1], b.Vertices[3 * v + 1]);
                Assert.Equal(a.Vertices[3 * v + 2], b.Vertices[3 * v + 2]);
            }
        }
        Check(racer, 0x10, +5);
        Check(racer, 0x12, -5);
        Check(racer, 0x0C, 0);
        Check(racer, 0x0E, 0);
        Check(idgo, 0x0C, +5);
        Check(idgo, 0x0E, -5);
        Check(idgo, 0x10, +5);
        Check(idgo, 0x12, -5);
        Check(traffic, 0x10, 0);
        Check(traffic, 0x0C, 0);

        // a model whose wheel parts are empty (traffic/object geo): the shift is a no-op, not a crash
        var trex = CarGeo.Parse(CarData().Get("trex.geo"));
        Assert.Equal(0, trex.Parts[0x10].NumVerts);
        trex.ApplyWheelShift(0);
        Assert.Equal(0, trex.Parts[0x10].NumVerts);
    }

    [GameFact]
    public void CarSpecsF355()
    {
        var viv = CarData();
        var s = CarSpecs.Parse(viv.Get("pf355.dat"));
        Assert.InRange(CarSpecs.Fx(s.Mass), 1352.6, 1352.8);
        Assert.Equal((8, 8, 8600), (s.NumGears, s.GearShiftDelay, s.Redline));
        Assert.Equal(Fx(82.0), s.MaxVelocity);
        Assert.Equal(0, s.VelocityToRpm[1]);
        Assert.InRange(CarSpecs.Fx(s.VelocityToRpm[0]), -341.7, -341.5);
        Assert.Equal(41, s.TorqueCurve.Length);
        Assert.Equal(Fx(18.0), s.MaxSteeringAcceleration);
        Assert.Equal((150, 12000), (s.SlideAssistanceFactor, s.PushFactor));
        // loose copy in SIM/CARDATA is the same file (CARMODEL/PC/PF355.DAT is an older, different version)
        Assert.Equal(viv.Get("pf355.dat"), GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT"));
        Assert.Equal(CarGeo.NumParts, CarGeo.Parse(viv.Get("f355.geo")).Parts.Count);
        Assert.Equal(2u, CarGeo.Parse(viv.Get("f355.geo")).Unknown_0x00);
    }

    [GameFact]
    public void AllCarDataFilesParse()
    {
        var viv = CarData();
        int specs = 0, models = 0;
        foreach (var e in viv.Entries.Where(e => e.Name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
        {
            var d = viv.Get(e.Name);
            if (d.Length == CarSpecs.Size)
            {
                var s = CarSpecs.Parse(d);
                Assert.InRange(s.NumGears, 3, 8);
                Assert.InRange(s.Redline, 1000, 12000);
                specs++;
            }
            else if (d.Length == CarAiModel.Size)
            {
                var m = CarAiModel.Parse(d);
                Assert.Equal(112, m.AccelCurve(true).Length);
                models++;
            }
        }
        Assert.Equal(35, specs);
        Assert.True(models >= 15, $"models={models}");
        // QDA (EA-compressed) carSpecs in SIM/CARDATA unpack to the matching .DAT
        foreach (var rel in GameData.Files("GAMEDATA/SIM/CARDATA", ".QDA"))
        {
            var dat = Path.ChangeExtension(rel, ".DAT");
            if (GameFiles.Exists(dat, GameData.Dir))
                Assert.Equal(CarSpecs.Size, GameData.Read(rel).Length);
        }
    }

    [GameFact]
    public void SimTuneFromDatAndQda()
    {
        var raw = GameData.ReadRaw("GAMEDATA/CARMODEL/PC/SIMTUNE.DAT");
        Assert.Equal(raw, GameData.Read("GAMEDATA/CARMODEL/PC/SIMTUNE.QDA"));
        var t = SimTune.Parse(raw);
        Assert.Equal(Fx(95.0), t.Sim.TelepoleLaunchValue);
        Assert.Equal(Fx(175.0), t.Arcade.TelepoleLaunchValue);
        Assert.Equal(700, t.Sim[6] >> 16 == 0 ? t.Sim[6] : t.Sim[6] >> 16);
        Assert.Equal([5, 10, 30, 60], t.Sim.GlueDistances.ToArray().Select(v => v > 1000 ? v >> 16 : v));
        Assert.InRange(CarSpecs.Fx(t.Sim.GlueMultipliers[0]), 1.07, 1.08);
        Assert.InRange(CarSpecs.Fx(t.Arcade.GlueMultipliers[3]), 1.24, 1.26);
    }

    [GameFact]
    public void DesLabels()
    {
        var f = DesFile.Load("GAMEDATA/CARMODEL/PC/PF355.DES", GameData.Dir);
        Assert.Contains(f, x => x.Type == "Integer" && x.Description.StartsWith("number of gears"));
        Assert.Contains(f, x => x.Description.StartsWith("High Turn Factor"));
        Assert.Contains(f, x => x.Description == "torque curve (size 41) in 256rpm increments");
    }
}
