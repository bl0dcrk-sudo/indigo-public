using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Core;

public class TrackLocatorTests(ITestOutputHelper output)
{
    [Fact]
    public void Dist2_Formulas()
    {
        // (d>>9)²; (d>>12)²>>6 [ref:3104]
        Assert.Equal(2 * 2 + 3 * 3, TrackLocator.Dist2Coarse(0, 0, new Vec3(2 << 9, 0, 3 << 9)));
        Assert.Equal((64 * 64 >> 6) + (8 * 8 >> 6), TrackLocator.Dist2(0, 0, new Vec3(64 << 12, 0, 8 << 12)));
        Assert.Equal(-4, TrackLocator.SearchSteps.Sum()); // restored by −sum after the 8th try
    }

    public static TheoryData<string> Sessions => new() { "replay", "default" };
    private static RaceSession Session(string n) => n == "replay" ? TrackSessions.Replay.Value : TrackSessions.Default.Value;

    [GameTheory]
    [MemberData(nameof(Sessions))]
    public void GridCars_GetGridSlice(string name)
    {
        var r = Session(name);
        var loc = TrackSessions.Locator(r);
        foreach (var car in r.Cars)
        {
            var tp = new TrackPos { Slice = -1 }; // unknown → global search
            loc.Update(car.Obj.Pos, tp);
            output.WriteLine($"car {car.Index}: grid {car.StartSlice} → {tp.Slice} state {tp.State} col {tp.Column}");
            Assert.Equal(car.StartSlice, tp.Slice);
            Assert.Equal(r.Slices[car.StartSlice].BlockNumber, tp.Block);
            if (car.Setup.CarClass is 1 or 2) Assert.Equal(2, tp.State); // racers stand on a road polygon
        }
    }

    [GameFact]
    public void Walk_AlongTrack_VisitsEverySlice()
    {
        var r = TrackSessions.Replay.Value;
        var loc = TrackSessions.Locator(r);
        var tp = new TrackPos { Slice = 0 };
        int onPoly = 0, n = r.Slices.Length;
        for (int s = 0; s < n; s++)
        {
            var sl = r.Slices[s];
            loc.Update(new Vec3(sl.X, sl.Y + Fx.One, sl.Z), tp);
            Assert.Equal(s, tp.Slice);
            if (tp.State == 2) onPoly++;
            Assert.Equal(s > 0 ? 1 : 0, tp.SliceChanged);
        }
        output.WriteLine($"{onPoly}/{n} slice centres on a road polygon");
        Assert.True(onPoly > n * 9 / 10);
    }

    [GameFact]
    public void Walk_Interpolated_SliceMonotonic()
    {
        var r = TrackSessions.Replay.Value;
        var loc = TrackSessions.Locator(r);
        var tp = new TrackPos { Slice = 100 };
        for (int s = 100; s < 300; s++)
        {
            var a = r.Slices[s]; var b = r.Slices[s + 1];
            for (int k = 0; k < 4; k++)
            {
                var p = new Vec3(a.X + (b.X - a.X) / 4 * k, a.Y, a.Z + (b.Z - a.Z) / 4 * k);
                loc.Update(p, tp);
                Assert.InRange(tp.Slice, s, s + 1); // nearest centre
                Assert.Equal(2, tp.State);
                // the found quad contains the point (XZ) and lies near its height
                Assert.InRange(p.Y - tp.Corners[0].Y, -4 * Fx.One, 4 * Fx.One);
            }
        }
    }

    [GameFact]
    public void Teleport_UsesGlobalSearch()
    {
        var r = TrackSessions.Replay.Value;
        var loc = TrackSessions.Locator(r);
        var tp = new TrackPos { Slice = 10 };
        loc.Update(new Vec3(r.Slices[10].X, r.Slices[10].Y, r.Slices[10].Z), tp);
        int target = r.Slices.Length / 2 + 3;
        var t = r.Slices[target];
        loc.Update(new Vec3(t.X, t.Y, t.Z), tp);
        Assert.Equal(target, tp.Slice);
        Assert.Equal(1, tp.SliceChanged);
        Assert.Equal(2, tp.State);
    }

    [GameFact]
    public void OffTrack_State1()
    {
        var r = TrackSessions.Replay.Value;
        var loc = TrackSessions.Locator(r);
        var sl = r.Slices[200];
        var tp = new TrackPos { Slice = 200 };
        // 200 m to the side along the slice right vector
        var p = new Vec3(sl.X + sl.Right[0] * 200 * Fx.One / 127, sl.Y, sl.Z + sl.Right[2] * 200 * Fx.One / 127);
        loc.Update(p, tp);
        Assert.Equal(1, tp.State);
        Assert.Null(tp.RoadPoly);
        Assert.Equal(-1, tp.Column);
        Assert.Equal(1, TrackLocator.SurfaceFlags(tp));
    }
}
