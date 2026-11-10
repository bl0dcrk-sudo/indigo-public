using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;
using Xunit;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>
/// Tests of the round "AI 1:1" (commit 914c9989): the per-car physics LOD [ref:3032]
/// and the AI acceleration curve scaling [ref:3033]
/// </summary>
public class AiPhysicsLodTests
{
    // ---------------------------------------------------------------- [ref:3035]

    [Fact]
    public void SliceDistance_WrapsAroundTheTrack()
    {
        Assert.Equal(10, TrackLocator.SliceDistance(10, 20, 100));
        Assert.Equal(10, TrackLocator.SliceDistance(20, 10, 100));   // symmetric
        Assert.Equal(0, TrackLocator.SliceDistance(7, 7, 100));
        Assert.Equal(10, TrackLocator.SliceDistance(95, 5, 100));    // 90 the long way, 10 across slice 0
        Assert.Equal(1, TrackLocator.SliceDistance(99, 0, 100));
        Assert.Equal(50, TrackLocator.SliceDistance(0, 50, 100));    // a tie stays the direct distance
    }

    // ---------------------------------------------------------------- [ref:3036]

    /// <summary>v' = ((v &lt;&lt; 8) · scale + 0x8000) &gt;&gt; 16 &gt;&gt; 8, checked on hand-computed values.</summary>
    [Fact]
    public void ScaleAccel_MatchesTheAsmFormula()
    {
        Assert.Equal(0x10B8, AiDriver.ScaleAccel(0x1000, 0x10B85)); // 0x1000 × 68485/65536 (model 3 arcade) = 4280
        Assert.Equal(0x1000, AiDriver.ScaleAccel(0x1000, 0x10000)); // ×1 = identity
        Assert.Equal(-0x1000, AiDriver.ScaleAccel(-0x1000, 0x10000)); // identity holds for negatives too
        Assert.Equal(0x10, AiDriver.ScaleAccel(0x40, 0x4000));      // ÷4
        Assert.Equal(0x0C, AiDriver.ScaleAccel(0x30, 0x4000));
        Assert.Equal(0, AiDriver.ScaleAccel(3, 0x4000));            // 0.75 truncated (rounding bit: 0x8000 < low 16)
        Assert.Equal(2007, AiDriver.ScaleAccel(0x800, 0xFAE1));     // model 6 arcade ×0.98
        Assert.Equal(0, AiDriver.ScaleAccel(0, 0x12345));
    }

    /// <summary>A 524 B model .dat with a constant curve in both slots (byte offsets from <see cref="CarAiModel.Parse"/>).</summary>
    private static CarAiModel Model(short sim, short arcade)
    {
        var d = new byte[CarAiModel.Size];
        for (int i = 0; i < 112; i++)
        {
            BitConverter.TryWriteBytes(d.AsSpan(4 * 9 + 2 * i), sim);
            BitConverter.TryWriteBytes(d.AsSpan(4 * 67 + 2 * i), arcade);
        }
        return CarAiModel.Parse(d);
    }

    [Fact]
    public void LoadModel_ScalesTheFreshCurve_ByCarType()
    {
        var t = GameTables.FromConstants();
        var m = Model(0x1000, 0x1000);
        var c = new CarObj();

        AiDriver.LoadModel(c, m, arcade: true, carType: 3, tables: t);   // model 3 arcade factor 0x10B85
        Assert.All(c.AccelCurve, v => Assert.Equal(4280, v));

        AiDriver.LoadModel(c, m, arcade: false, carType: 0, tables: t);  // model 0 sim factor 0x10F5C
        Assert.All(c.AccelCurve, v => Assert.Equal(4341, v));

        AiDriver.LoadModel(c, m, arcade: true, carType: 0x0F, tables: t); // car type ≥ 0xF → curve as read
        Assert.All(c.AccelCurve, v => Assert.Equal(0x1000, v));
        AiDriver.LoadModel(c, m, arcade: true, carType: -1, tables: t);  // no car type → also unscaled
        Assert.All(c.AccelCurve, v => Assert.Equal(0x1000, v));
    }

    // ---------------------------------------------------------------- [ref:3037]

    /// <summary>Replay session (track 4) with a locator; the player's car is the local human, moved to a known slice.</summary>
    private static (TrackRoadQuery Q, CarObj Human) LodSetup()
    {
        var r = TrackSessions.FreshReplay();
        var q = new TrackRoadQuery(TrackSessions.Locator(r), r.TrackNumber, r.SimTune!.For(true).Values) { HumanCount = 1 };
        var h = r.Cars[0].Obj;
        h.Trk.Slice = 100;
        q.LocalHuman = h;
        return (q, h);
    }

    [GameFact]
    public void LodUpdate_DistanceMetric_IsMaxPlusAQuarterOfMin()
    {
        var (q, h) = LodSetup();
        var c = new CarObj { Active = 1 };
        c.Trk.Slice = h.Trk.Slice;                                  // slice distance 0: only the XZ metric counts
        c.Pos = new Vec3(h.Pos.X + 60 * Fx.One, h.Pos.Y, h.Pos.Z + 20 * Fx.One);
        q.LodUpdate(c);
        Assert.Equal(0, c.Rail);                                    // 65 m < 244: still fully simulated
        Assert.Equal(65 * Fx.One, c.DistToPlayer);                  // 60 + 20/4, not 60 + 20 = 80
        c.Pos = new Vec3(h.Pos.X + 20 * Fx.One, h.Pos.Y, h.Pos.Z + 60 * Fx.One);
        q.LodUpdate(c);
        Assert.Equal(65 * Fx.One, c.DistToPlayer);                  // symmetric: it is the larger axis that survives
    }

    [GameFact]
    public void LodUpdate_SliceDistanceFloorsAtFourMetresPerSlice()
    {
        var (q, h) = LodSetup();
        var c = new CarObj { Active = 1 };
        c.Pos = h.Pos;                                             // same spot …
        c.Trk.Slice = h.Trk.Slice + 61;                           // … but 61 slices away = 244.0 m exactly
        q.LodUpdate(c);
        Assert.Equal(61 * 0x40000, c.DistToPlayer);               // one slice of track distance = 4 m
        Assert.Equal(0, c.Rail);                                  // d ≤ 0xF40000 stays simulated
        c.Trk.Slice = h.Trk.Slice + 62;                           // 248 m: past the threshold
        q.LodUpdate(c);
        Assert.Equal(62 * 0x40000, c.DistToPlayer);
        Assert.Equal(1, c.Rail);
    }

    [GameFact]
    public void LodUpdate_AboveThreshold_GoesOnRails_AndZeroesAngVelXZ()
    {
        var (q, h) = LodSetup();
        var c = new CarObj { Active = 1 };
        c.Trk.Slice = h.Trk.Slice;
        c.Pos = new Vec3(h.Pos.X + 300 * Fx.One, h.Pos.Y, h.Pos.Z);
        c.AngVel = new Vec3(0x1234, 0x5678, 0x9ABC);
        q.LodUpdate(c);
        Assert.Equal(1, c.Rail);
        Assert.Equal(300 * Fx.One, c.DistToPlayer);
        Assert.Equal(new Vec3(0, 0x5678, 0), c.AngVel);            // ω.x / ω.z zeroed, spin about Y kept
        Assert.Equal(1, c.SurfaceByte);
        Assert.Equal(1, c.SurfaceType);
    }

    [GameFact]
    public void LodUpdate_NoLocalHuman_OrThreeHumans_IsNeutral()
    {
        var (q, h) = LodSetup();
        var c = new CarObj { Active = 1, Rail = 1 };
        c.Trk.Slice = h.Trk.Slice;
        c.Pos = new Vec3(h.Pos.X + 500 * Fx.One, h.Pos.Y, h.Pos.Z);
        c.DistToPlayer = 0x1234;
        q.HumanCount = 3;                                          // > 2 [ref:3038]
        q.LodUpdate(c);
        Assert.Equal(0, c.Rail);
        Assert.Equal(0, c.DistToPlayer);
        q.HumanCount = 1;
        q.LocalHuman = null;                                       // nobody set it
        c.Rail = 1; c.DistToPlayer = 0x1234;
        q.LodUpdate(c);
        Assert.Equal(0, c.Rail);
        Assert.Equal(0, c.DistToPlayer);
    }

    [GameFact]
    public void LodUpdate_ComingBackUnderThreshold_PutsTheCarBackOnTheRoad()
    {
        var r = TrackSessions.FreshReplay();
        var q = new TrackRoadQuery(TrackSessions.Locator(r), r.TrackNumber, r.SimTune!.For(true).Values) { HumanCount = 1 };
        var h = r.Cars[0].Obj;                                     // player on the grid
        q.LocalHuman = h;
        var c = r.Cars[1].Obj;                                     // another grid car
        c.Active = 1;
        c.Trk.Slice = h.Trk.Slice;
        c.Rail = 1; c.CrashSlice = 7; c.Crashing = 1; c.SurfaceByte = 0x55;
        c.Pos = new Vec3(h.Pos.X + 500 * Fx.One, h.Pos.Y, h.Pos.Z);
        q.LodUpdate(c);                                            // still far: stays on rails
        Assert.Equal(1, c.Rail);

        c.Pos = new Vec3(h.Pos.X, h.Pos.Y, h.Pos.Z);               // back with the player
        q.LodUpdate(c);
        Assert.Equal(0, c.Rail);                                   // rails cleared
        Assert.Equal(0, c.CrashSlice);                             // [ref:3039]
        Assert.Equal(0, c.Crashing);
        Assert.Equal(1, c.SurfaceByte);
        Assert.Equal(1, c.SurfaceType);
        Assert.Equal(0, c.HeightAboveRoad);
        Assert.Equal(0, c.Impact);
        Assert.Null(c.ImpactOther);
        Assert.Equal(0, c.RoadVySmoothed);
        int rh = Newton.RoadHeightAtCar(c);
        Assert.Equal(rh, c.PrevRoadHeight);                        // +0x154 = road height of this step
        Assert.InRange(c.Pos.Y - rh, c.HalfHeight - 0x8000, c.HalfHeight + 0x8000); // back on its wheels
    }
}
