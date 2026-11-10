using NFS.Core;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>"no contact" branch (UpDotN +0x100 &lt; 0x199A), [ref:3065]
/// and the XBID 11 object collision. [ref:3066]
/// Expected values hand-computed from the original's formulas (FX products rounded).</summary>
public class DriveTaskNoContactTests
{
    private static CarObj Tilted(int upDotN, int height) => new()
    {
        UpDotN = upDotN, HeightAboveRoad = height, Rpm = 3000, SlipState = -3, SlipAngle = 484899, Skid324 = 7, Skid328 = 9,
        Vel = new Vec3(0x10000, 0x20000, -0x30000), AngVel = new Vec3(1, 0x10000, 3),
    };

    [Fact]
    public void NoContact_TiltedHigh_ResetsSlipAndDropsRpm_KeepsVelocity()
    {
        var c = Tilted(0x1000, 0x20000);                       // > 0x7FFF above the road: no damping
        Assert.True(ArcadeModel.NoContactDrive(c));
        Assert.Equal(0, c.SlipState);
        Assert.Equal(0, c.SlipAngle);
        Assert.Equal(0, c.Skid324);
        Assert.Equal(0, c.Skid328);
        Assert.Equal(2500, c.Rpm);
        Assert.Equal(new Vec3(0x10000, 0x20000, -0x30000), c.Vel);
        Assert.Equal(0x10000, c.AngVel.Y);
    }

    [Fact]
    public void NoContact_LowRpm_ClampsToZero()
    {
        var c = Tilted(0x1000, 0x20000);
        c.Rpm = 0x1F4;                                          // < 0x1F5 → 0
        ArcadeModel.NoContactDrive(c);
        Assert.Equal(0, c.Rpm);
        c.Rpm = 0x1F5;
        ArcadeModel.NoContactDrive(c);
        Assert.Equal(1, c.Rpm);
    }

    [Fact]
    public void NoContact_NearRoad_DampsVelocityBy094_AndYawRateOnlyBelow0x3333()
    {
        var c = Tilted(0x1000, 0x4000);
        Assert.True(ArcadeModel.NoContactDrive(c));
        Assert.Equal(new Vec3(Fx.Mul(0x10000, 0xF0A3), Fx.Mul(0x20000, 0xF0A3), Fx.Mul(-0x30000, 0xF0A3)), c.Vel);
        Assert.Equal(0xF0A3, c.Vel.X);
        Assert.Equal(0xFAE1, c.AngVel.Y);                       // UpDotN ≤ 0x3332
        var d = Tilted(0x3333, 0x4000);                         // UpDotN > 0x3332 leaves ω.y alone [ref:3067]
        ArcadeModel.AirDamping(d);
        Assert.Equal(0x10000, d.AngVel.Y);
        Assert.Equal(0xF0A3, d.Vel.X);
    }

    [Fact]
    public void NoContact_NotTaken_WhenUpDotNAtLeast0x199A()
    {
        var c = Tilted(0x199A, 0x4000);
        Assert.False(ArcadeModel.NoContactDrive(c));
        Assert.Equal(-3, c.SlipState);
        Assert.Equal(3000, c.Rpm);
        Assert.Equal(0x10000, c.Vel.X);
    }

    private static CarObj Box() => new()
    {
        Orient = Mat3.Identity, HalfWidth = 1 << 16, HalfHeight = 1 << 15, HalfLength = 2 << 16, Pos = new Vec3(100 << 16, 0, 50 << 16),
    };

    [Theory]
    [InlineData(1.5, 0.0, true)]      // |d·right| = 1.5 = half width 1 + r 0.5 (inclusive)
    [InlineData(1.51, 0.0, false)]
    [InlineData(0.0, 2.5, true)]      // |d·fwd| = 2.5 = half length 2 + r 0.5 (inclusive)
    [InlineData(0.0, -2.51, false)]
    [InlineData(-1.0, -2.0, true)]
    public void ObjBoxTest_IsInclusiveRectangleOfHalfExtentsPlusRadius(double dx, double dz, bool hit)
    {
        var c = Box();
        var pt = new Vec3(c.Pos.X + (int)(dx * 65536), 12345, c.Pos.Z + (int)(dz * 65536));
        Assert.Equal(hit, DynamicObjects.BoxTest(c, pt, 0x8000));
    }

    [Fact]
    public void DistXz_IsMaxPlusQuarterMin()
    {
        Assert.Equal(40 + (8 >> 2), DynamicObjects.DistXz(new Vec3(40, 7, -8), Vec3.Zero));
        Assert.Equal(40 + (8 >> 2), DynamicObjects.DistXz(new Vec3(-8, 7, 40), Vec3.Zero));
    }

    [GameFact]
    public void DynObjHit_FirstHitKnocksOverAtSpeed_ThenObjectIsPassable_SlowHumanStaysSolid()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: null, withAi: false);
        var objs = run.DynObjects;
        var type2 = new TrackPoint(0, 0, 0, 0x100, 5, 0, 0, 0, 2);
        var other = new TrackPoint(0, 0, 0, 0x100, 6, 0, 0, 0, 1);
        var human = new CarObj { Flags = CarFlags.LocalHuman, Vel = new Vec3(5 << 16, 0, 5 << 16) };   // 10 m/s: < 11
        Assert.Equal(1, objs.Hit(human, type2));                                    // too slow: solid
        human.Vel = new Vec3(6 << 16, 0, -5 << 16);                                // |vx|+|vz| = 11 m/s
        Assert.Equal(-1, objs.Hit(human, type2));                                   // knocked over
        Assert.Equal(0, objs.Hit(human, type2));                                    // already down: passable
        Assert.Equal(1, objs.Hit(human, other));                                    // other type: solid, other object id
        var cpu = new CarObj { Flags = CarFlags.NotLocal | CarFlags.Opponent };
        Assert.Equal(1, objs.Hit(cpu, other));
        var type2b = new TrackPoint(0, 0, 0, 0x100, 7, 0, 0, 0, 2);
        Assert.Equal(-1, objs.Hit(cpu, type2b));                                    // computer cars: any speed
    }
}
