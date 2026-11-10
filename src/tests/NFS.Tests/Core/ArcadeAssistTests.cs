using System.Linq;
using NFS.Core;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>
/// Expected values computed by hand from the original's formulas (FX = (a*b+0x8000)>>16 per product; final
/// "(x + sign*0xFFFF) >> 16" = round toward zero; 32-bit product).</summary>
public class ArcadeAssistTests
{
    [Theory]
    // vz = 20 m/s, specs+0x168[2] = 0x1001: FX(0x140000,0x1001) = 0x14014 = 81940; 6000*81940 = 491640000; >>16 → 7501
    [InlineData(0x140000, 7501)]
    // negative: FX → -81940; -491640000 / 65536 = -7501.8 → toward zero −7501 (floor would give −7502)
    [InlineData(-0x140000, -7501)]
    public void WallSlowdown_RpmFromForwardSpeed(int vz, int rpm)
    {
        var f = new int[8]; f[2] = 0x1001;
        var c = new CarObj { Vel = new(0x30000, 0, vz), DriveFrame = Mat3.Identity, Gear = 2, Rpm = 1, LocalVel = new(5, 6, 7),
            SlipAngle = 9, Skid324 = 9, Skid328 = 9, SlipState = 3 };
        WallCollision.ArcadeWallSlowdown(c, f, 6000);
        Assert.Equal(rpm, c.Rpm);
        Assert.Equal(new Vec3(5, 6, vz), c.LocalVel);          // only +0x2B8 rewritten
        Assert.Equal(0, c.SlipAngle); Assert.Equal(0, c.Skid324); Assert.Equal(0, c.Skid328); Assert.Equal(0, c.SlipState);
    }

    [Fact]
    public void WallSlowdown_NeutralKeepsRpm()
    {
        var c = new CarObj { Vel = new(0, 0, 0x100000), DriveFrame = Mat3.Identity, Gear = 1, Rpm = 4321 };
        WallCollision.ArcadeWallSlowdown(c, new int[8], 6000);
        Assert.Equal(4321, c.Rpm);
        Assert.Equal(0x100000, c.LocalVel.Z);
    }
}

public class SteerAssistTests
{
    // straight track along +z, slice i at z = 4·i m; Lanes 0x22, half widths 8/8 (as AiDriverTests)
    private static NFS.Data.Track.TrackSlice[] Straight(int n = 16) => Enumerable.Range(0, n).Select(i => new NFS.Data.Track.TrackSlice(
        0, 0, i * 4 * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0x22, 8, 8, 0, 0)).ToArray();

    [Fact]
    public void LaneTargetOffset_BothSides()
    {
        var s = Straight()[0];
        Assert.Equal(0x40000, ArcadeModel.LaneTargetOffset(s, 1));    // (2·8·0x8000)/2 (unsigned)
        Assert.Equal(-0x40000, ArcadeModel.LaneTargetOffset(s, 0));   // (2·8·−0x8000)/2 (division)
    }

    [Fact]
    public void SteerAssist_StraightForward_LaneError()
    {
        // fwd: (0 < +0x288) == (0 == 0); tgt = 0x40000 + 0; err = 0x50000 − 0x40000 → lane = 1 → −1 (fwd) [ref:3047]
        // curvature 0, heading error 0; gain = FX(FX(0x200000, 0x1999), 0x40000/64) = FX(0x33320, 0x1000) = 0x3332;
        // 0x3332/128 = 102 → +0x30C = 102·(−1) = −102, +0x368 = 0.
        var e = new ArcadeEnv { SteerAssist = true, Slices = Straight(), TrackReverse = 0, LaneSide = 1 };
        var c = new CarObj { LateralOffsetCur = 0x50000, LocalVel = new(0, 0, 0x200000), FrontGripDemand = 77 };
        c.Trk.Slice = 5;
        ArcadeModel.SteerAssist(c, new DerivedCarSpecs { MaxSteering = 0x40000 }, ref e);
        Assert.Equal(0x40000, c.LaneTargetOffset);
        Assert.Equal(-102, c.YawTarget);
        Assert.Equal(0, c.FrontGripDemand);
    }

    [Fact]
    public void SteerAssist_Reverse_SteerShiftsTarget()
    {
        // reverse track: fwd = false → steer negated: −0x7C·0xA0000/0x7C = −0xA0000; tgt = 0x40000 − 0xA0000 = −0x60000.
        // err = 0; slices −1/−2 colinear → curvature 0; heading = a1 → 0; sum 0 → +0x30C = 0.
        var sl = Straight();
        var e = new ArcadeEnv { SteerAssist = true, Slices = sl, TrackReverse = 1, LaneSide = 1 };
        var c = new CarObj { Steer = 0x7C, LateralOffsetCur = -0x60000, LocalVel = new(0, 0, 0x100000), YawTarget = 5,
            Heading = FxMath.Atan2(sl[4].X - sl[5].X, sl[4].Z - sl[5].Z) };
        c.Trk.Slice = 5;
        ArcadeModel.SteerAssist(c, new DerivedCarSpecs { MaxSteering = 0x40000 }, ref e);
        Assert.Equal(-0x60000, c.LaneTargetOffset);
        Assert.Equal(0, c.YawTarget);
    }
}
