using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>composition (without assists/boost): "set the velocity to the target" behaviour. [ref:3057]</summary>
public class ArcadeStepTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(true, false));

    private static CarObj Car(int vz) => new()
    {
        DriveFrame = Mat3.Identity, Orient = Mat3.Identity, Vel = new Vec3(0, 0, vz), TimeScale = Fx.One,
        Gear = 3, PrevGear = 3, Rpm = 3000, SurfaceType = 0,
    };

    [GameFact]
    public void FlatIdentityFrame_NewVelocityEqualsTargets()
    {
        var sp = F355();
        var c = Car(0xC0000);
        c.ThrottleRamped = 0xF8; c.Steer = 40;
        var e = new ArcadeEnv { Throttle = 0x10000, SteerAbs = 40 * 0x10000 / 128 };
        ArcadeModel.Step(c, sp, ref e, raceStarted: true);
        // identity frame, level car: Δv = F = −v + targets  ⇒  v_new = (target lateral, 0, target longitudinal)
        Assert.Equal(c.SlipAngle, c.Vel.X);
        Assert.Equal(0, c.Vel.Y);
        Assert.Equal(c.DeltaV.Z + 0xC0000, c.Vel.Z);
        Assert.True(c.Vel.Z > 0xC0000);                    // accelerating
        Assert.Equal(c.YawTarget, c.AngVel.Y);              // ω.y := target yaw rate
        Assert.True(c.YawTarget > 0);
        Assert.Equal(0, c.Heading);
        Assert.Equal(0xC0000 << 0, c.SignedSpeed);         // final +0x304 = max + min/4 of the old v_loc
    }

    [GameFact]
    public void ThrottleThenBrake_SpeedRisesThenFalls()
    {
        var sp = F355();
        var c = Car(0x60000);
        for (int i = 0; i < 64; i++)
        {
            c.ThrottleRamped = 0xF8;
            var e = new ArcadeEnv { Throttle = 0x10000 };
            ArcadeModel.Step(c, sp, ref e, raceStarted: true);
        }
        int fast = c.Vel.Z;
        Assert.True(fast > 0x80000, $"{fast:X}");
        for (int i = 0; i < 32; i++)
        {
            c.ThrottleRamped = 0; c.BrakeRamped = 0xF8;
            var e = new ArcadeEnv { Brake = 0x10000 };
            ArcadeModel.Step(c, sp, ref e, raceStarted: true);
        }
        Assert.True(c.Vel.Z < fast, $"{c.Vel.Z:X} vs {fast:X}");
        Assert.Equal(0, c.Vel.X);
    }

    [GameFact]
    public void Airborne_NoDriveForces()
    {
        var c = Car(0x100000);
        c.AirTicks = 3;
        var e = new ArcadeEnv { Throttle = 0x10000 };
        ArcadeModel.Step(c, F355(), ref e, raceStarted: true);
        Assert.Equal(new Vec3(0, 0, 0x100000), c.Vel);
        Assert.Equal(0, c.LocalForce.Z);
    }

    [Fact]
    public void GravitySlope_ArcadeOnlyWhenPushing()
    {
        var c = new CarObj { Orient = Mat3.Identity, TimeScale = Fx.One };
        c.Orient = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0x4000, Fx.One);  // nose up (fwd.y = 0.25)
        c.LocalForce = new Vec3(0, 0, 0x10000);
        ArcadeModel.ApplyGravitySlope(c, true);
        int g = Fx.DivPow2((int)(((long)0x4000 * -0xA0000) >> 16), 3);       // −2.5 / 8
        Assert.Equal(0x10000 + Fx.DivPow2(g, 2), c.LocalForce.Z);              // t < −0x1999 → g/4
        c.LocalForce = new Vec3(0, 0, 0x100);
        ArcadeModel.ApplyGravitySlope(c, true);
        Assert.Equal(0x100, c.LocalForce.Z);                                   // |t| small → nothing
        ArcadeModel.ApplyGravitySlope(c, false);
        Assert.Equal(0x100 + (int)(((long)0x4000 * -0xA0000) >> 16), c.LocalForce.Z);
    }
}
