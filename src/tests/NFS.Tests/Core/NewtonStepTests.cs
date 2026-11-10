using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>
/// and helpers. Expected values: (emulation of the original's assembly [ref:3076] [ref:3479]
/// with a flat <see cref="IRoadQuery"/>, test simtune <see cref="St"/>, 0x300, player at distance 0). [ref:3077]
/// </summary>
public class NewtonStepTests
{
    /// <summary>Test simtune (st[5] 0.5, st[13] 0.7, st[14] 0.092, st[15] 5.0, st[17] 0.3, st[18] 0.5).</summary>
    private static readonly int[] St = MakeSt();

    private static int[] MakeSt()
    {
        var st = new int[29];
        st[5] = 0x8000; st[13] = 0xB333; st[14] = 0x178D; st[15] = 0x50000; st[17] = 0x4CCC; st[18] = 0x8000;
        return st;
    }

    private static readonly Mat3 Tilt = new(0xFDE2, -0xA2A, -0x1F40, 0xC86, 0xFEFF, 0x12D5, 0x1E61, -0x1435, 0xFD62);
    private static readonly Vec3 SlopeN = new(0x10E8, 0xFD99, -0x21D0);

    private static CarObj Car()
    {
        var c = new CarObj
        {
            Active = 1, Flags = CarFlags.LocalHuman, TimeScale = Fx.One, CatchUp = Fx.One,
            Pos = new(0, 0x8000, 0), Vel = new(0, 0, 0x140000), Orient = Mat3.Identity,
            HalfHeight = 0x8000, HalfWidth = 0xE000, HalfLength = 0x20000,
        };
        c.RoadFrame.Row1 = new Vec3(0, Fx.One, 0);
        c.Trk.RoadPoly = new object();
        return c;
    }

    private static void Run(CarObj c, int tick = 0x300)
    {
        Newton.Step(c, St, tick);
    }

    private static readonly Mat3 FlatDrive = new(0xFED8, 0, 0, 0, Fx.One, 0, 0, 0, 0xFDE4); // normalize quirk (sqrt table)

    [Fact]
    public void Flat_StaysOnRoad()
    {
        var c = Car();
        Run(c);
        Assert.Equal(new Vec3(0, 0x8000, 0), c.Pos);
        Assert.Equal(new Vec3(0, 0, 0x140000), c.Vel);       // gravity removed again by snapping (v.y = road v)
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal(FlatDrive, c.DriveFrame);
        Assert.Equal((0, 0, 0, Fx.One, 0x140000, 0x640000), (c.AirTicks, c.TookOff, c.HeightAboveRoad, c.UpDotN, c.SpeedApprox, c.F150));
    }

    [Fact]
    public void Flight_TakesOffWithExtraGravity()
    {
        var c = Car();
        c.Pos = new(0, 0x30000, 0);
        c.Vel = new(0x10000, 0x20000, 0x140000);
        Run(c);
        Assert.Equal(new Vec3(0x10000, 0x14F9A, 0x140000), c.Vel);   // 2 − 0.459 − 0.5·0.459
        Assert.Equal((1, 1, 0x28000, 0x144000), (c.AirTicks, c.TookOff, c.HeightAboveRoad, c.SpeedApprox));
    }

    [Fact]
    public void Fall_TwelveSteps()
    {
        var c = Car();
        c.Pos = new(0, 0x30000, 0);
        for (int t = 0; t < 12; t++) Run(c, 0x300 + 2 * t);
        Assert.Equal(new Vec3(0, -0x844C8, 0x140000), c.Vel);
        Assert.Equal((12, 0), (c.AirTicks, c.TookOff));
    }

    [Fact]
    public void Landing_BouncesAfterLongFlight()
    {
        var c = Car();
        c.Pos = new(0, 0xA000, 0);
        c.Vel = new(0, -0x80000, 0x140000);
        c.AirTicks = 20;
        Run(c);
        Assert.Equal(new Vec3(0, 0x8F5C, 0), c.Pos);
        Assert.Equal(new Vec3(0, 0x5179, 0x140000), c.Vel);
        Assert.Equal((0, 0xF5C, 0x10EB32, Fx.One, (byte)5), (c.AirTicks, c.HeightAboveRoad, c.Impact, c.ImpactType, c.LandingSmoke));
        Assert.Equal(c.Pos, c.LastHit);
    }

    [Fact]
    public void Landing_ShortFlightGoesToPlaneCollision()
    {
        var c = Car();
        c.Pos = new(0, 0x9000, 0);
        c.Vel = new(0, -0x30000, 0x100000);
        c.AirTicks = 5;
        c.AngVel = new(0x1000, 0x500, 0x2000);
        Run(c);
        Assert.Equal(new Vec3(0, 0x9000, 0), c.Pos);
        Assert.Equal(new Vec3(0, -0x37599, 0x100000), c.Vel);
        Assert.Equal(new Vec3(0x1000, 0x500, 0x2000), c.AngVel);
        Assert.Equal((0, 0x1000, 0), (c.AirTicks, c.HeightAboveRoad, c.Impact));
    }

    [Fact]
    public void Slope_TiltedCarGetsAngularVelocityAndImpact()
    {
        var c = Car();
        c.Pos = new(0x30000, 0x12000, 0x50000);
        c.Vel = new(0x50000, 0x2000, 0x180000);
        c.Orient = Tilt;
        c.RoadFrame.Row1 = SlopeN;
        c.RoadPoint = new(0, 0x4000, 0);
        c.PrevRoadHeight = 0x3000;
        c.RoadVySmoothed = 0x1000;
        Run(c);
        Assert.Equal(new Vec3(0x3019D, 0x13837, 0x4FCC5), c.Pos);
        Assert.Equal(new Vec3(0x4ECC8, 0, 0x17A3C0), c.Vel);
        Assert.Equal(new Vec3(-0x34A5, 0, -0x462), c.AngVel);
        Assert.Equal(new Mat3(0xFC2F, -0x14C6, -0x1DB5, 0x10E8, 0xFD99, -0x21D0, 0x2004, 0x1F31, 0xF9F6), c.DriveFrame);
        Assert.Equal((0, 0, 0xB776, 0xFAF1, 0x194000, 0x1C9088, 0x20003),
            (c.HeightAboveRoad, c.RoadVySmoothed, c.PrevRoadHeight, c.UpDotN, c.SpeedApprox, c.Impact, c.ImpactType));
    }

    [Fact]
    public void Parked_UsesBoxCornerAndGroundContact()
    {
        var c = Car();
        c.Flags = CarFlags.Static;
        c.Pos = new(0x10000, 0x4000, 0);
        c.Vel = new(0x1000, 0, 0x3000);
        c.Orient = Tilt;
        Run(c);
        Assert.Equal(new Vec3(0x10000, 0xB0CF, 0), c.Pos);
        Assert.Equal(new Vec3(0x1000, 0, 0x3000), c.Vel);
        Assert.Equal(new Vec3(-0x12D5, 0, 0xC86), c.AngVel);
        Assert.Equal(new Mat3(0xFDE2, -0xA2A, -0x1F40, 0, Fx.One, 0, 0x1E61, -0x1435, 0xFD62), c.DriveFrame);
        Assert.Equal((0xFEFF, 0x3400), (c.UpDotN, c.SpeedApprox));
    }

    [Fact]
    public void Rail_StaticSnapsToRoadPoint()
    {
        var c = Car();
        c.Rail = 1;
        c.Flags = CarFlags.Static;
        c.RoadPoint = new(0, 0x23456, 0);
        c.AirTicks = 3; c.TookOff = 1; c.Vel = new(1, 2, 3);
        Newton.Step(c, St, 0x300);                                   // no LOD / original-game tables needed
        Assert.Equal(new Vec3(0, 0x23456, 0), c.Pos);
        Assert.Equal(new Vec3(1, 0, 3), c.Vel);
        Assert.Equal((0, 0), (c.AirTicks, c.TookOff));
    }

    [Fact]
    public void RoadDrop_ZeroesRollPitchAndLifts()
    {
        var c = Car();
        c.PrevRoadHeight = 0x40000;                                  // road fell by 4 m in one step
        c.Vel = new(0x300000, 0, 0x200000);
        c.AngVel = new(0x100, 0, 0x100);
        Run(c);
        Assert.Equal(new Vec3(0x300000, -0xB066, 0x200000), c.Vel);
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal((1, 1, 0x4CCC8, -0x400000, 0x380000), (c.AirTicks, c.TookOff, c.HeightAboveRoad, c.RoadVySmoothed, c.SpeedApprox));
    }

    [Fact]
    public void Inactive_DoesNothing()
    {
        var c = Car();
        c.Active = 0;
        Newton.Step(c, St, 0x300);
        Assert.Equal(new Vec3(0, 0, 0x140000), c.Vel);
    }

    // ------------------------------------------------------------ helpers (hand-computed)

    [Fact]
    public void PlaneHeightAt_ClampsSmallNy()
    {
        Assert.Equal(0x18000, Newton.PlaneHeightAt(new(0x20000, 0, 0x10000), new(0, Fx.One, 0), new(5, 0x18000, 7)));
        // n = (0.5, 0.5, 0): y = FXDIV(-FX(0.5, 2), 0.5) + 0 = -2
        Assert.Equal(-0x20000, Newton.PlaneHeightAt(new(0x20000, 0, 0), new(0x8000, 0x8000, 0), Vec3.Zero));
        // |ny| < 10 → divisor +10 (also for a negative ny)
        Assert.Equal(Fx.Div(-Fx.Mul(Fx.One, 1), 10), Newton.PlaneHeightAt(new(1, 0, 0), new(Fx.One, -3, 0), Vec3.Zero));
    }

    [Fact]
    public void RoadHeightAtCar_SteepNormalGivesNoHeight()
    {
        var c = Car();
        c.RoadFrame.Row1 = new Vec3(Fx.One, 0x1999, 0);
        Assert.Equal(Newton.NoHeight, Newton.RoadHeightAtCar(c));
    }

    [Fact]
    public void ClampAngVel_LimitsEachComponent()
    {
        var c = Car();
        c.AngVel = new(0x30000, -0x30000, 0x26666);
        Newton.ClampAngVel(c);
        Assert.Equal(new Vec3(0x26666, -0x26666, 0x26666), c.AngVel);
    }

    [Fact]
    public void GroundImpact_SettledCarStopsRolling()
    {
        var c = Car();
        c.Flags = CarFlags.None;
        c.UpDotN = Fx.One;
        c.AngVel = new(0x1000, 0x500, 0x1000);
        Newton.GroundImpact(c, new Vec3(0xA3D, Fx.One, -0xA3D), FlatRoadQuery.Instance);  // |e| = 0xA3D ≤ thr
        Assert.Equal(new Vec3(0, 0x500, 0), c.AngVel);
        c.AngVel = new(0x1000, 0x500, 0x1000);
        Newton.GroundImpact(c, new Vec3(0xA3E, Fx.One, 0), FlatRoadQuery.Instance);
        Assert.Equal(new Vec3(0, 0x500, -0xA3E), c.AngVel);                              // ω.z = ex = up.x − n.x
    }
}
