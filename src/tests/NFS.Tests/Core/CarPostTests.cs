using NFS.Core;
using Xunit;
using NFS.Core.Tables;

namespace NFS.Tests.Core;

/// <summary>Tests of the parts (CarPost) and the CarDump field map. The physics was checked tick by tick [ref:3058]
/// against a state dump of the original (checked by the private trace tests); these are the hand-computed unit cases. [ref:3473]</summary>
public class CarPostTests
{
    [Fact]
    public void TakeoffSpin_ScalesPitchAndRoll_InCarAxes()
    {
        var st = new int[29];
        st[CarPost.StJumpPitchDamp] = 0x8000;      // pitch ×0.5
        st[CarPost.StJumpRollAttenuate] = 0x4000;  // roll ×0.25
        st[CarPost.StJumpPitchAccel] = 0x10000;
        st[CarPost.StJumpRollTurn] = 0x10000;
        var c = new CarObj { TookOff = 1, Orient = Mat3.Identity, AngVel = new Vec3(0x10000, 0x20000, 0x40000) };
        CarPost.TakeoffSpin(c, st);
        Assert.Equal(new Vec3(0x8000, 0x20000, 0x10000), c.AngVel);
        Assert.Equal(c.AngVel, c.LocalAngVel);

        // local human: + FX(Fz/2, st[4]) pitch, − FX(Fx/2, st[2]) roll (/2 truncating)
        var h = new CarObj { TookOff = 1, Flags = CarFlags.LocalHuman, Orient = Mat3.Identity, LocalForce = new Vec3(-3, 0, 0x20001) };
        CarPost.TakeoffSpin(h, st);
        Assert.Equal(new Vec3(0x10000, 0, 1), h.AngVel);

        var notOff = new CarObj { Orient = Mat3.Identity, AngVel = new Vec3(5, 6, 7) };
        CarPost.TakeoffSpin(notOff, st);
        Assert.Equal(new Vec3(5, 6, 7), notOff.AngVel);
    }

    [Fact]
    public void DriveLandingReset_ArcadeSideways_SetsDriftState()
    {
        var c = new CarObj { DriveFrame = Mat3.Identity, Vel = new Vec3(-0x50000, 0, 0x140000), SlipState = 1, Skid324 = 0x300000 };
        CarPost.DriveLandingReset(c, 1);
        Assert.Equal(new Vec3(-0x50000, 0, 0x140000), c.LocalVel);
        Assert.Equal(3, c.SlipState);                 // vx < 0 → +3
        Assert.Equal(-0x50000 / 4, c.SlipAngle);
        Assert.Equal(0x20000, c.Skid324);             // cleared, then +2.0

        var straight = new CarObj { DriveFrame = Mat3.Identity, Vel = new Vec3(0x100, 0, 0x140000), SlipState = 2 };
        CarPost.DriveLandingReset(straight, 1);
        Assert.Equal(0, straight.SlipState);
    }

    [Fact]
    public void SlopeSlide_OnSlideSurface_AddsRoadNormal()
    {
        var t = GameTables.FromConstants();
        var rf = new Mat3(); rf.Row1 = new Vec3(0x4000, 0xA000, -0x2000);
        var c = new CarObj { SurfaceType = 3, RoadFrame = rf, HeightAboveRoad = 0x700, Vel = new Vec3(1, 2, 3) };
        CarPost.SlopeSlide(c, t);
        Assert.Equal(new Vec3(1 + 0x4000, 2 - 0x14CCC, 3 - 0x2000), c.Vel);
        var flat = new CarObj { SurfaceType = 1, RoadFrame = rf, Vel = new Vec3(1, 2, 3) };
        CarPost.SlopeSlide(flat, t);                 // surface 1 has no slide entry
        Assert.Equal(new Vec3(1, 2, 3), flat.Vel);
    }

    [Fact]
    public void Launch_KicksAndCountsAirborne()
    {
        var t = GameTables.FromConstants();
        Assert.Equal([0x3333, 0x5999, 0x10000, 0x3D999, 0x10000, 0x1D999, 0x10000, 0x3B333], t.LaunchK);
        var c = new CarObj { DriveFrame = Mat3.Identity, SpeedApprox = 0x2C0000 };
        CarPost.Launch(c, 1, 0, t);                  // tick 0: both signs +, |speed − 44| = 0
        // ω = 0 + right·(K1/256)/256 + fwd·(K2/256)/256, then ±2.4; v += up·(K3/256)/256 [ref:3059]
        Assert.Equal(new Vec3(Math.Min(0x10000 * (0x3D999 / 256) / 256, 0x26666), 0, 0x10000 * (0x1D999 / 256) / 256), c.AngVel);
        Assert.Equal(0x10000 * (0x3B333 / 256) / 256, c.Vel.Y);
        Assert.Equal(1, c.AirTicks);
    }

    [Fact]
    public void ImpactProcess_AboveCrashLimit_ArmsResetTimerAndClearsImpact()
    {
        var st = new int[29];
        st[CarPost.StCrashImpact] = 10 * Fx.One;
        st[CarPost.StCrashTime] = 0x40;
        var env = new CarPostEnv { SimTune = st, Slices = [], TrackUpdate = _ => { }, Tables = GameTables.FromConstants() };
        var c = new CarObj { Impact = 20 * Fx.One, ImpactType = 0x40004, HeightAboveRoad = 2 * Fx.One, UpDotN = Fx.One, SpeedApprox = 10 * Fx.One };
        c.RoadFrame = Mat3.Identity; c.Orient = Mat3.Identity; c.SurfaceType = 1;
        CarPost.ImpactProcess(c, env, 0);
        Assert.Equal(1, c.CrashCount);
        Assert.Equal(1, c.Crashing);
        Assert.Equal((0, 0), (c.Impact, c.ImpactType));
        Assert.Equal(0x40 - 1, c.AutoResetCounter);  // armed, then one count (no human on its wheels)
    }

    [Fact]
    public void LandingSmoke_CountsDownOncePerPostUpdate_OnlyWithin100m()
    {
        // for cars closer than 0x640000 (100 m). [ref:3060]
        var env = new CarPostEnv { SimTune = new int[29], Slices = [], TrackUpdate = _ => { }, Tables = GameTables.FromConstants() };
        CarObj Fresh(int dist) => new() { LandingSmoke = 5, DistToPlayer = dist, Active = 1, UpDotN = Fx.One,
            RoadFrame = Mat3.Identity, Orient = Mat3.Identity, HeightAboveRoad = Fx.One };
        var near = Fresh(0x640000 - 1);
        for (int i = 0; i < 7; i++) CarPost.Update(near, env, 0);
        Assert.Equal(0, near.LandingSmoke);
        var after2 = Fresh(0x100000);
        CarPost.Update(after2, env, 0); CarPost.Update(after2, env, 0);
        Assert.Equal(3, after2.LandingSmoke);
        var far = Fresh(0x640000);
        CarPost.Update(far, env, 0);
        Assert.Equal(5, far.LandingSmoke);
    }

    [Fact]
    public void CarDump_RoundTrip_UsesOriginalOffsets()
    {
        var s = CarDump.Slots;
        Assert.Equal(0x09C, s.First(x => x.Name == "Pos.X").Offset);
        Assert.Equal(0x0D4, s.First(x => x.Name == "Orient.M11").Offset);
        Assert.Equal(0x014, s.First(x => x.Name == "Trk.Slice").Offset);
        Assert.Equal(0x2DA, s.First(x => x.Name == "Gear").Offset);
        var a = new CarObj { Pos = new Vec3(1, -2, 3), Gear = 5, AirTicks = 0xFFFF, Steer = -100, Flags = CarFlags.LocalHuman };
        a.Trk.Slice = 1234; a.LapTimes[3] = 777; a.Orient = Mat3.Identity;
        var raw = new byte[CarObj.Size];
        CarDump.Write(a, raw);
        var b = new CarObj();
        CarDump.Load(b, raw);
        Assert.Empty(CarDump.Diff(b, raw));
        Assert.Equal((a.Pos, a.Gear, a.AirTicks, a.Steer, a.Trk.Slice, a.LapTimes[3]), (b.Pos, b.Gear, b.AirTicks, b.Steer, b.Trk.Slice, b.LapTimes[3]));
    }
}
