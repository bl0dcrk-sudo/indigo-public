using NFS.Core;
using Xunit;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>Parked cars. [ref:3101]
/// Expected values follow the original's formulas (damping by k/256 truncating towards zero).</summary>
public class StaticCarTests
{
    private static CarObj Parked() => new()
    {
        Flags = CarFlags.Static, Rail = 0, Orient = Mat3.Identity, RoadFrame = Mat3.Identity,
        HalfWidth = 0x10000, HalfHeight = 0x8000, HalfLength = 0x20000,
    };

    private static readonly int[] St = new int[29];

    [Fact]
    public void Drive_OffRails_LocalVelocityIsOrientationTimesVelocity_OnRailsUntouched()
    {
        var c = Parked();
        c.Orient = new Mat3(0, 0, Fx.One, 0, Fx.One, 0, -Fx.One, 0, 0);   // rows: right = +z, up = +y, fwd = −x
        c.Vel = new Vec3(3 * Fx.One, 5 * Fx.One, 7 * Fx.One);
        StaticCar.Drive(c);
        Assert.Equal(new Vec3(7 * Fx.One, 5 * Fx.One, -3 * Fx.One), c.LocalVel);

        var r = Parked();
        r.Rail = 1; r.Vel = new Vec3(Fx.One, 0, 0); r.LocalVel = new Vec3(9, 9, 9);
        StaticCar.Drive(r);
        Assert.Equal(new Vec3(9, 9, 9), r.LocalVel);
    }

    [Fact]
    public void Post_OnRails_DoesNothing()
    {
        var c = Parked();
        c.Rail = 1; c.Vel = new Vec3(1000, 1000, 1000); c.Impact = 99 * Fx.One;
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(new Vec3(1000, 1000, 1000), c.Vel);
        Assert.Equal(0, c.AutoResetCounter);
    }

    [Fact]
    public void Post_OnTheGround_DampsVelocityAndRotationBy0xE6Over256_TruncatingTowardsZero()
    {
        var c = Parked();
        c.AirTicks = 0; c.SurfaceType = 1;
        c.Vel = new Vec3(1000, -1000, 255);
        c.AngVel = new Vec3(-257, 256, 3);
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(new Vec3(1000 * 0xE6 / 256, -(1000 * 0xE6 / 256), 255 * 0xE6 / 256), c.Vel);   // 898, −898, 228
        Assert.Equal(new Vec3(-(257 * 0xE6 / 256), 0xE6, 3 * 0xE6 / 256), c.AngVel);
        Assert.Equal(0, c.AutoResetCounter);                                                         // on the road: no timer
    }

    [Fact]
    public void Post_InTheAir_DampsWeakerOnlyCloseToTheRoad()
    {
        var low = Parked();
        low.AirTicks = 3; low.HeightAboveRoad = 0x7FFF; low.SurfaceType = 1; low.Vel = new Vec3(1000, 0, 0);
        StaticCar.Post(low, St, 1, 0, null!);
        Assert.Equal(1000 * 0xF8 / 256, low.Vel.X);

        var high = Parked();
        high.AirTicks = 3; high.HeightAboveRoad = 0x8000; high.SurfaceType = 1; high.Vel = new Vec3(1000, 0, 0);
        StaticCar.Post(high, St, 1, 0, null!);
        Assert.Equal(1000, high.Vel.X);
    }

    [Fact]
    public void Post_CarOffTheRoadAtRest_ArmsTheReturnTimer_AndSettlesWhenItRunsOut()
    {
        var c = Parked();
        c.SurfaceType = 0;                                    // off the road
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(0x1C0, c.AutoResetCounter);

        // the timer counts down once per tick …
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(0x1BF, c.AutoResetCounter);

        // … and at 0 the car is stopped and put back on the road plane (flat road, point at y = 3.0)
        c.AutoResetCounter = 1;
        c.Vel = new Vec3(5, 6, 7); c.AngVel = new Vec3(1, 2, 3);
        c.Pos = new Vec3(0, 10 * Fx.One, 0);
        c.RoadPoint = new Vec3(0, 3 * Fx.One, 0);
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(Vec3.Zero, c.Vel);
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal(0, c.HeightAboveRoad);
        Assert.Equal(3 * Fx.One + c.HalfHeight, c.Pos.Y);     // lowest corner of the box (identity orientation) touches the road
        Assert.Equal(0x1C0, c.AutoResetCounter);              // still off the road (+0x184 = 0) → timer armed again
    }

    [Fact]
    public void Post_CarOnTheRoadAtRest_KeepsTimerAtZero()
    {
        var c = Parked();
        c.SurfaceType = 5; c.AutoResetCounter = 1;
        StaticCar.Post(c, St, 1, 0, null!);
        Assert.Equal(0, c.AutoResetCounter);
    }

    [Fact]
    public void Post_StrongImpact_LaunchesTheCar_AndArmsTheTimer()
    {
        var t = GameTables.FromConstants();
        var c = Parked();
        c.SurfaceType = 5; c.AirTicks = 0; c.Orient = Mat3.Identity; c.DriveFrame = Mat3.Identity;
        c.Impact = 100 * Fx.One;                              // > simtune[0]
        var st = new int[29]; st[CarPost.StCrashImpact] = 10 * Fx.One;
        StaticCar.Post(c, st, 1, 4, t);
        Assert.True(c.AirTicks >= 1);                         // +0x14C [ref:3102]
        Assert.NotEqual(Vec3.Zero, c.Vel);                    // kicked along the drive frame's up axis (K3 + speed term)
        Assert.Equal(0x1BF, c.AutoResetCounter);              // armed to 0x1C0, then counted down in the same call
    }
}
