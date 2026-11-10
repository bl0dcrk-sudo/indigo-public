using NFS.Core;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

public class AiDriverTests
{
    /// <summary>Straight synthetic track along +Z, 4 m per slice.</summary>
    private static TrackSlice[] Straight(int n = 64) => Enumerable.Range(0, n).Select(i => new TrackSlice(
        0, 0, i * 4 * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0x22, 8, 8, 0, 0)).ToArray();

    [GameFact]
    public void TargetSpeed_IsSpeedByteTimesSkill_OnStraight()
    {
        var ai = new AiDriver(Straight(), false, s => 40, 0) { Skill = 0xC000 };
        Assert.Equal(Fx.One, ai.TurnFactor(10));
        Assert.Equal(30 * Fx.One, ai.ComputeTargetSpeed(10));          // 40 m/s × 0.75
        var def = new AiDriver(Straight(), false, null, 0);
        Assert.Equal(AiDriver.DefaultSpeedByte << 16, def.ComputeTargetSpeed(3));
    }

    [Fact]
    public void LanePoint_IsOffsetAlongSliceRight_AndMirroredBackward()
    {
        var fwd = new AiDriver(Straight(), false, null, 2 * Fx.One);
        var p = fwd.LanePoint(5, 3);
        Assert.Equal(32 * Fx.One, p.Z);
        Assert.InRange(p.X, 2 * Fx.One - 200, 2 * Fx.One + 200);
        var back = new AiDriver(Straight(), true, null, 2 * Fx.One);
        var q = back.LanePoint(10, 3);
        Assert.Equal(28 * Fx.One, q.Z);
        Assert.InRange(q.X, -2 * Fx.One - 200, -2 * Fx.One + 200);
    }

    [GameFact]
    public void Think_GasBelowTarget_BrakeAbove_SteersTowardLane()
    {
        var ai = new AiDriver(Straight(), false, s => 30, 3 * Fx.One);
        var c = new CarObj { Orient = Mat3.Identity, Gear = 2 };
        c.Trk.Slice = 5; c.Pos = new Vec3(0, 0, 20 * Fx.One);
        var i1 = ai.Think(c);
        Assert.True(i1.Gas > 0 && i1.Brake == 0);
        Assert.True(i1.Steer > 0, $"lane is to the right (+X), steer {i1.Steer}");
        c.LocalVel = new Vec3(0, 0, 40 * Fx.One);
        var i2 = ai.Think(c);
        Assert.True(i2.Gas == 0 && i2.Brake > 0);
        c.Gear = 1;
        Assert.Equal(PlayerInput.BtnGearUp, ai.Think(c).Buttons);
    }

    [Fact]
    public void LaneMath_FromOffset_Centre_Roadway()
    {
        var sl = Straight()[0];                                       // lanes 4 m wide, b1F = 0x22
        Assert.Equal(7, AiDriver.LaneFromOffset(sl, Fx.One));
        Assert.Equal(8, AiDriver.LaneFromOffset(sl, 5 * Fx.One));
        Assert.Equal(6, AiDriver.LaneFromOffset(sl, -Fx.One));
        Assert.Equal(5, AiDriver.LaneFromOffset(sl, -5 * Fx.One));
        Assert.Equal(13, AiDriver.LaneFromOffset(sl, 100 * Fx.One));
        Assert.Equal(2 * Fx.One, AiDriver.LaneCentre(sl, 7));
        Assert.Equal(-6 * Fx.One, AiDriver.LaneCentre(sl, 5));
        Assert.Equal((5, 8), AiDriver.Roadway(sl));
    }

    [GameFact]
    public void ChooseLane_OvertakesSlowCarAhead_StaysOnRoadway()
    {
        var ai = new AiDriver(Straight(), false, null, 2 * Fx.One);    // lane 7
        var me = new CarObj { LateralOffset = 2 * Fx.One }; me.Trk.Slice = 10;
        var slow = new CarObj { LateralOffset = 2 * Fx.One }; slow.Trk.Slice = 14; // 4 slices ahead, standing
        ai.ChooseLane(me, [me]);
        Assert.Equal(7, ai.TargetLane);                                // free road: keep lane
        ai.ChooseLane(me, [me, slow]);
        Assert.Equal(7, ai.Lane);                                      // still physically in lane 7
        Assert.NotEqual(7, ai.TargetLane);
        Assert.InRange(ai.TargetLane, 5, 8);
        Assert.InRange(Math.Abs(ai.SmoothOffset - 2 * Fx.One), 0, 0x3333 * 2);
        var edge = new AiDriver(Straight(), false, null, 6 * Fx.One);  // lane 8 = last roadway lane
        var me2 = new CarObj { LateralOffset = 6 * Fx.One }; me2.Trk.Slice = 10;
        edge.ChooseLane(me2, [me2]);
        Assert.Equal(-1000 << 16, edge.LaneSums[2]);
        Assert.Equal(8, edge.TargetLane);
    }

    [Fact]
    public void GlueLapRamp_FadesIn_ByLapThresholds()
    {
        int g = 0x18000; // 1.5
        Assert.Equal(Fx.One, AiDriver.GlueLapRamp(300, g, 2));
        Assert.Equal(Fx.One + 0x2000, AiDriver.GlueLapRamp(301, g, 2));   // ¼ of +0.5
        Assert.Equal(Fx.One + 0x4000, AiDriver.GlueLapRamp(501, g, 2));
        Assert.Equal(Fx.One + 0x6000, AiDriver.GlueLapRamp(651, g, 2));
        Assert.Equal(g, AiDriver.GlueLapRamp(801, g, 2));
        Assert.Equal(Fx.One, AiDriver.GlueLapRamp(750, g, 8));             // laps 8: thresholds 750..1500
        Assert.Equal(g, AiDriver.GlueLapRamp(1501, g, 8));
    }

    [Fact]
    public void SpeedGlue_IndexesByProgressGap_ResetsWhenLeaderStops_StartBoost()
    {
        var table = Enumerable.Range(0, 21).Select(i => Fx.One + (10 - i) * 0x1000).ToArray(); // behind → faster
        Assert.Equal(Fx.One + 5 * 0x1000, AiDriver.SpeedGlue(table, 1000, 1075, 30 * Fx.One, false, 2)); // d = −75 → i 5
        Assert.Equal(Fx.One - 10 * 0x1000, AiDriver.SpeedGlue(table, 2000, 1000, 30 * Fx.One, false, 2)); // clamp i 20
        Assert.Equal(Fx.One, AiDriver.SpeedGlue(table, 1100, 1000, Fx.One, false, 2));     // leader slow, we lead
        Assert.Equal(Fx.One, AiDriver.SpeedGlue(table, 900, 1000, 30 * Fx.One, true, 2));  // leader crashed, g > 1
        Assert.Equal(Fx.One + 0x41 * 0x3F0, AiDriver.SpeedGlue(table, 0, 0, 0, false, 2)); // start boost ×(1+65·0x3F0)
        Assert.Equal(Fx.Mul(Fx.One + 5 * 0x1000, 0x18000),
            AiDriver.SpeedGlue(table, 1000, 1075, 30 * Fx.One, false, 2, glueSpeed: 0x18000)); // |d| > 30
    }

    [Fact]
    public void ScoreStatic_ParkedCarAhead_PenalisesItsLane()
    {
        var sl = Straight();
        var me = new CarObj { AiSpeed = 20 * Fx.One, Lane = 7 }; me.Trk.Slice = 10;
        var p = new CarObj { LateralOffset = 2 * Fx.One }; p.Trk.Slice = 15;   // lane 7, 5 slices ahead
        var far = new CarObj { LateralOffset = 6 * Fx.One }; far.Trk.Slice = 40; // ≥ |v| slices: ignored
        var behind = new CarObj { LateralOffset = -2 * Fx.One }; behind.Trk.Slice = 8;
        Span<int> sums = stackalloc int[3];
        AiDriver.ScoreStatic(me, [p, far, behind], sl, false, false, sums);
        // x = 5·0x1B77C = 0x8956C; FX(20.0 − x, −0.5) = −(0x140000 − 0x89556C)/2
        Assert.Equal(Fx.Mul(20 * Fx.One - 5 * 0x1B77C, -0x8000), sums[1]);
        Assert.Equal(0, sums[0]);
        Assert.Equal(0, sums[2]);
        sums.Clear();
        AiDriver.ScoreStatic(me, [p], sl, true, false, sums);                    // wrong way: car behind in race direction
        Assert.Equal(0, sums[1]);
    }
}
