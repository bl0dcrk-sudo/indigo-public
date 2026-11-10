using NFS.Core;
using NFS.Data;
using NFS.Data.Car;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>AiDriveModel. Expected values computed by hand from the original's pseudocode [ref:3022]
/// (FX (a*b+0x8000)>>16; clamp order max-then-min). [ref:3023]</summary>
public class AiDriveModelTests(ITestOutputHelper output)
{
    private static TrackSlice[] Straight(int n) => Enumerable.Range(0, n).Select(i => new TrackSlice(
        0, 0, i * 4 * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0x22, 8, 8, 0, 0)).ToArray();

    private static CarObj State()
    {
        var c = new CarObj
        {
            LateralOffsetCur = 0x20000, SmoothedLaneOffset = 0, MaxLatAccel = 0x80000, MaxYawAccel = 0x40000,
            Glue = Fx.One, HeadingError = 0x10, LateralSpeedAhead = 0x10000, AiSpeed = 0x140000,
            AiSpeedApprox = 0x140000, AiTargetSpeed = 0x200000, Orient = Mat3.Identity, RoadFrame = Mat3.Identity,
        };
        c.LocalVel = new Vec3(0, 0, 0x140000);
        c.AccelCurve[20] = 0x100;
        return c;
    }

    [Fact]
    public void RailStep_HandComputed_AcceleratesAlongRoadAndFollowsLaneOffset()
    {
        // lateral speed (lane target − lateral offset)·8; [ref:3024]
        // accelerates by accel·dt toward the target; v = right·lateral + forward·|speed|; ω = 0; orientation = road frame.
        var c = State();
        c.AiSpeedApprox = 0x148000;
        c.AngVel = new Vec3(5, 6, 7);
        AiDriveModel.RailStep(c, 0x8000, trackReverse: false, arcade: true);
        Assert.Equal(-0x100000, c.RailLateralSpeed);                    // (0 − 0x20000)·8
        Assert.Equal(0x140000 + 0x8000, c.AiSpeed);                     // curve[20]<<8 = 1 m/s² · 0.5 s
        Assert.Equal(new Vec3(-0x100000, 0, 0x148000), c.Vel);
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal(0x148000, c.LocalVel.Z);                           // +0x2B8 = +0x3A0
        Assert.Equal(0x10000, c.LocalForce.Z);
        Assert.Equal(Mat3.Identity, c.Orient);
        Assert.Equal(0, c.BrakeRamped);

        // Faster than the target: brake (12 m/s² x2 in arcade) down to the target, never below it.
        c.AiTargetSpeed = 0x13F000;
        AiDriveModel.RailStep(c, 0x8000, trackReverse: false, arcade: true);
        Assert.Equal(0x13F000, c.AiSpeed);
        Assert.Equal(0xFF, c.BrakeRamped);
        Assert.Equal(24 * Fx.One, c.LocalForce.Z);
    }

    [Fact]
    public void RailStep_TrackReverse_FlipsOrientationAndSlowsTowardNegativeTarget()
    {
        // The wrong-way flag (+0x3A9 & 0x10) selects the opposite case: opposite speed sense, orientation rows 0 and 2 negated. [ref:3025]
        var c = State();
        c.AiSpeed = -0x140000; c.AiTargetSpeed = -0x200000; c.AiSpeedApprox = 0x140000;
        AiDriveModel.RailStep(c, 0x8000, trackReverse: true, arcade: true);
        Assert.Equal(-0x140000 - 0x8000, c.AiSpeed);                    // accelerates away from zero
        Assert.Equal(new Mat3(-Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, -Fx.One), c.Orient);
    }

    [Fact]
    public void InControl_HandComputedForces()
    {
        var c = State();
        var P = AiDriveModel.Params.DefaultInControl;
        Assert.False(AiDriveModel.IsOutOfControl(c, AiDriveModel.Params.DefaultOutOfControl));
        var f = AiDriveModel.SteerInControl(c, P, 1, true);
        // fs = FX(20.0, 0x62)+1 = 0x107A8; latMax = 8·fs = 0x83D40; lat = −FX(1+10, 4·fs) → clamped to −latMax
        Assert.Equal(new Vec3(-0x83D40, 0, 0x10000), f.Road);          // lon = curve[20] << 8
        Assert.Equal(Vec3.Zero, f.Car);
        // dav = FX(0x80, 1.25)·(−0x10) = −0xA00; yaw = −FX(0 + 0xA00, 5.0) = −0x3200
        Assert.Equal(new Vec3(0, -0x3200, 0), f.Ang);
        Assert.Equal(0, c.BrakeRamped);

        c.AiTargetSpeed = 0x100000;                                     // slower than v → brake 15 (opponent) ×2 arcade
        c.Flags = CarFlags.Opponent;
        f = AiDriveModel.SteerInControl(c, P, 1, true);
        Assert.Equal(-30 * Fx.One, f.Road.Z);
        Assert.Equal(0xFF, c.BrakeRamped);
        c.AiFlags = 0x1000;                                             // wrong way: car frame
        f = AiDriveModel.SteerInControl(c, P, 1, false);
        Assert.Equal(new Vec3(-0x83D40, 0, -15 * Fx.One), f.Car);
        Assert.Equal(Vec3.Zero, f.Road);
    }

    [Fact]
    public void OutOfControl_SlowCar_HandComputed()
    {
        var c = State();
        c.AiSpeed = 0x10000; c.AiSpeedApprox = 0x10000; c.LocalVel = new Vec3(0x8000, 0, 0x10000);
        var Q = AiDriveModel.Params.DefaultOutOfControl;
        Assert.True(AiDriveModel.IsOutOfControl(c, Q));                 // |v| < 10 − 3
        var f = AiDriveModel.SteerOutOfControl(c, Q, 1, true);
        // |v| < 2 → t = 0, g = 1 + COSQ(0x100) = 1; dav = FX(0x80, 0.625)·(−0x10) = −0x500;
        // dlv = FX(dav, 10) = −0x3200; |v| < 10: dlv clamp ±FX(1, 0.32)=0x51EB, dav clamp ±FX(1, 0.013)=0x353 → −0x353
        // lat = −FX(0.5 + 0x3200, 3.0) = −0x21600; lon = accel(1 m/s) = curve[1] << 8 = 0; yaw = 0 (|v| < 2)
        Assert.Equal(new Vec3(-0x21600, 0, 0), f.Car);
        Assert.Equal(Vec3.Zero, f.Ang);
        Assert.Equal(0, c.SlipDamping);
    }

    [Fact]
    public void Integrate_RoadAndCarFrame_ToWorld()
    {
        var c = new CarObj { Orient = Mat3.Identity, RoadFrame = new Mat3(0, 0, -Fx.One, 0, Fx.One, 0, Fx.One, 0, 0) };
        var f = new AiDriveModel.Forces { Car = new Vec3(Fx.One, 0, 0), Road = new Vec3(0, 0, 2 * Fx.One), Ang = new Vec3(0, 0x10000, 0) };
        AiDriveModel.Integrate(c, f, 0x800);
        // Mᵀ·(1,0,0) = (1,0,0); Rᵀ·(0,0,2) = 2·row2 = (2,0,0) → Δv = (3,0,0); v += Δv/32
        Assert.Equal(new Vec3(3 * Fx.One, 0, 0), c.DeltaV);
        Assert.Equal(new Vec3(0x1800, 0, 0), c.Vel);
        Assert.Equal(new Vec3(0, 0x800, 0), c.AngVel);
    }

    private static (CarObj Car, int[] Trace) RunStraight(CarAiModel model, int seconds)
    {
        var slices = Straight(4000);
        var ai = new AiDriver(slices, false, _ => 80, 0);
        var c = new CarObj { Orient = Mat3.Identity, RoadFrame = Mat3.Identity, Flags = CarFlags.Opponent };
        AiDriver.LoadModel(c, model, true);
        c.Trk.Slice = 2; c.Pos = new Vec3(0, 0, 8 * Fx.One);
        var trace = new int[seconds];
        for (int t = 0; t < 32 * seconds; t++)
        {
            // the caller's progress task fills +0x3AC; hand-simulated here [ref:3026]
            // (Straight() puts every slice at x = 0 with RoadFrame = I, so this is just Pos.X)
            c.LateralOffsetCur = TrackRoadQuery.LateralOffset(c, slices);
            ai.Drive(c);
            c.Pos += Vec3.Scale(c.Vel, 0x800);
            c.LateralOffset = c.Pos.X;
            c.Trk.Slice = Math.Clamp(c.Pos.Z / (4 * Fx.One), 0, slices.Length - 1);
            if ((t & 31) == 31) trace[t >> 5] = c.Vel.Z;
        }
        return (c, trace);
    }

    [Fact]
    public void GearboxRpm_ShiftsUp_HandComputed()
    {
        var c = new CarObj { AiSpeed = 15 * Fx.One, MaxRpm = 6000 << 16, ShiftTime = 8, Gear = 0 };
        c.GearSpeeds[0] = 10 << 16; c.GearSpeeds[1] = 20 << 16; c.GearSpeeds[2] = 30 << 16;
        c.GearSpeedsInv[1] = Fx.Div(Fx.One, 10 << 16);                  // 0x1999
        AiDriveModel.GearboxRpm(c);
        Assert.Equal(1, c.Gear);
        Assert.Equal(8, c.ShiftCounter);
        // target = (FX(FX(5.0, 0x1999), 3000·65536) + 3000·65536) >> 16 = 4499; rpm += 4499/8
        Assert.Equal(562, c.Rpm);
        AiDriveModel.GearboxRpm(c);                                     // shifting: target 3000
        Assert.Equal(7, c.ShiftCounter);
        Assert.Equal(562 + (3000 - 562) / 8, c.Rpm);
        Assert.Equal(0, AiDriveModel.Accel(c));                         // no drive while shifting
    }

    [GameFact]
    public void Accel_ModelVmaxCap()
    {
        var vm = GameTables.FromConstants().ModelVmax;
        Assert.Equal(15, vm.Length);
        Assert.InRange(vm[0] / 65536.0, 103.2, 103.4);                  // 103.3 m/s [ref:3469]
        var c = State();
        Assert.Equal(0x10000, AiDriveModel.Accel(c, 30 * Fx.One));
        Assert.Equal(0, AiDriveModel.Accel(c, 19 * Fx.One));            // v 20 > vmax
        c.Glue = 0x8000;
        Assert.Equal(0, AiDriveModel.Accel(c, 30 * Fx.One));            // v 20 > vmax·glue = 15
        c.Glue = Fx.One; c.ShiftCounter = 1;
        Assert.Equal(0, AiDriveModel.Accel(c));
        c.ShiftCounter = 0; c.PsychoTicks = 5;
        Assert.Equal(0x14CCC, AiDriveModel.Accel(c));
    }

    [GameFact]
    public void Straight_ReachesOver34mps_Deterministic()
    {
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", GameData.Dir);
        var model = CarAiModel.Parse(viv.Get("f355.dat"));
        var (c, trace) = RunStraight(model, 40);
        output.WriteLine(string.Join(" ", trace.Select(v => (v / 65536.0).ToString("F1"))));
        Assert.True(c.Vel.Z > 34 * Fx.One, $"v = {c.Vel.Z / 65536.0:F1} m/s");
        Assert.True(Math.Abs(c.Pos.X) < Fx.One);                        // stays on its lane (offset 0)
        Assert.Equal(trace, RunStraight(model, 40).Trace);
    }

    [Fact]
    public void Step_ReversingUsesTheRearHeading_AndKeepsTheSpeedApproxOfTheTaskStart()
    {
        // +0x524 != 1 → +0x52C = atan2(−fwd.x, −fwd.z) (heading of the REAR of the car); [ref:3027]
        // +0x3A0 is written before the drive step and not recomputed by it [ref:3028]
        // although the step's own wall hit may have changed v since.
        int Run(int direction, out CarObj car)
        {
            var c = State();
            c.Direction = direction;
            c.Vel = new Vec3(0, 0, 0x10000);
            c.AiSpeedApprox = 0x777777;
            AiDriveModel.Step(c, new Vec3(0, 0, 50 * Fx.One), new Vec3(Fx.One, 0, 0), AiDriveModel.Params.DefaultInControl,
                AiDriveModel.Params.DefaultOutOfControl, 1, true, 0x800, 0x10000);
            car = c;
            return c.AiHeading;
        }
        int fwd = Run(1, out var f), rear = Run(-1, out var r);
        Assert.Equal(FxMath.Atan2(0, Fx.One), fwd);
        Assert.Equal(FxMath.Atan2(0, -Fx.One), rear);
        Assert.Equal(0x200, Math.Abs(rear - fwd));
        Assert.Equal(0x777777, f.AiSpeedApprox);
        Assert.Equal(0x777777, r.AiSpeedApprox);
    }
}
