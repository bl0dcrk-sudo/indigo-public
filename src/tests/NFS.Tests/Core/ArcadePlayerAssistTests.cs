using NFS.Core;
using NFS.Data.Car;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>landing alignment + player auto-drift. [ref:3050]
/// Expected values hand-computed from the original's rules: [ref:3051]
/// every product is a rounded FX, the auto-drift division is a 32-bit product into a 64-bit division by 0x68.</summary>
public class PlayerAssistTests
{
    /// <summary>MaxVelocity 80 m/s; nothing else of the specs is read by the assist.</summary>
    private static readonly DerivedCarSpecs Specs = new()
    {
        Source = new CarSpecs(1000 << 16, 3, 4, new int[8], new int[8], new int[41], 6000, 80 << 16, 0, 0, 0,
            new byte[8], new byte[8], new byte[8], new byte[8], 2 << 16, 0, 18 << 16, 16, 32,
            3 << 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
    };

    /// <summary>Lowered onto the road (0x15C &lt; 1 m) while airborne and falling (0x14C ≠ 0, Vel.y &lt; 0),
    /// level car (Orient = I → carUp = +y), road normal tilted 45° towards +x. Flags = 0: no player drift part.</summary>
    private static CarObj Landing() => new()
    {
        Orient = Mat3.Identity,
        RoadFrame = new Mat3(0x10000, 0, 0, 0x8000, 0x8000, 0, 0, 0, 0x10000), // roadUp = (0.5, 0.5, 0)
        DriveFrame = Mat3.Identity,
        AirTicks = 3,
        HeightAboveRoad = 0x8000,
        Vel = new Vec3(0, -0x10000, 0),
        AngVel = new Vec3(7, 8, 9),
    };

    [Fact]
    public void LandingAlignment_OverwritesAngVel()
    {
        // M = I → ω = carUp × roadUp = (0·0 − 0·0.5, 0·0.5 − 0·0, 0·0.5 − 1·0.5) = (0, 0, −0x8000); +0xE8 =
        // overwritten (AngVel 7/8/9 gone), not accumulated.
        var c = Landing();
        var e = new ArcadeEnv();
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(new Vec3(0, 0, -0x8000), c.AngVel);
    }

    [Fact]
    public void LandingAlignment_RotatesByTransposedDriveFrame()
    {
        // DriveFrame M with M01 = 0x2000 (M11 = 1 → guard passes), carUp = (0,0,+1), roadUp = (0,0.5,0.5):
        // M·carUp = (0, 0, 0x10000); M·roadUp = (0x1000, 0x8000, 0x8000) → cross = (−0x8000, 0x1000, 0);
        // ω = Mᵀ·cross = (−0x8000, FX(−0x8000,0x2000) + FX(0x1000,0x10000) + 0, 0) = (−0x8000, −0x1000+0x1000, 0).
        // (Mᵀ, not M: M·cross would be (−0x7E00, 0x1000, 0) – the transposed multiply is the one in the original.)
        var c = Landing();
        c.Orient = new Mat3(0x10000, 0, 0, 0, 0, 0x10000, 0, 0x10000, 0);          // carUp = +z
        c.RoadFrame = new Mat3(0x10000, 0, 0, 0, 0x8000, 0x8000, 0, 0, 0x10000);   // roadUp = (0, 0.5, 0.5)
        c.DriveFrame = new Mat3(0x10000, 0x2000, 0, 0, 0x10000, 0, 0, 0, 0x10000);
        var e = new ArcadeEnv();
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(new Vec3(-0x8000, 0, 0), c.AngVel);
    }

    [Theory]
    [InlineData("height")]   // 0x15C = 1 m exactly (not < 1 m)
    [InlineData("air")]      // 0x14C = 0 – on the ground
    [InlineData("vely")]     // Vel.y = 0 (not falling)
    [InlineData("ground")]   // 0x198 = 0x8000 – ground normal y = 0.5 exactly (not > 0.5)
    [InlineData("updot")]    // carUp ⊥ roadUp – dot = 0 (not > 0)
    public void LandingAlignment_EachGuardBlocks(string which)
    {
        var c = Landing();
        switch (which)
        {
            case "height": c.HeightAboveRoad = Fx.One; break;
            case "air": c.AirTicks = 0; break;
            case "vely": c.Vel = new Vec3(0, 0, 0); break;
            case "ground": c.DriveFrame = new Mat3(0x10000, 0, 0, 0, 0x8000, 0, 0, 0, 0x10000); break;
            case "updot": c.RoadFrame = new Mat3(0x10000, 0, 0, 0x10000, 0, 0, 0, 0, 0x10000); break;
        }
        var e = new ArcadeEnv();
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(new Vec3(7, 8, 9), c.AngVel);
    }
}

public class PlayerAssistDriftTests
{
    private const int Threshold = 2268538;   // see AutoDrift_ThresholdIsAiSpeedTimesVmaxBelow

    private static readonly DerivedCarSpecs Specs = new()
    {
        Source = new CarSpecs(1000 << 16, 3, 4, new int[8], new int[8], new int[41], 6000, 80 << 16, 0, 0, 0,
            new byte[8], new byte[8], new byte[8], new byte[8], 2 << 16, 0, 18 << 16, 16, 32,
            3 << 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
    };

    /// <summary>Player car on the ground at slice 5; 0x32 = 50 m/s at[4], roadUp tilted 45°, [ref:3052]
    /// |carUp · roadUp| = 0x8000 (below the 0xD999 clear threshold).</summary>
    private static CarObj Drift(int vz, int yaw, int slip = 0)
    {
        var c = new CarObj
        {
            Flags = CarFlags.LocalHuman,
            Orient = Mat3.Identity,
            DriveFrame = Mat3.Identity,
            RoadFrame = new Mat3(0x10000, 0, 0, 0, 0x8000, 0, 0, 0, 0x10000),
            HeightAboveRoad = Fx.One,
            Vel = new Vec3(0, 0, vz),
            LocalVel = new Vec3(0, 0, vz),
            YawTarget = yaw,
            SlipState = slip,
        };
        c.Trk.Slice = 5;
        return c;
    }

    private static readonly byte[] Speeds = [0, 0, 0, 0, 0x32, 0];

    [Theory]
    [InlineData(0x51E, 3)]
    [InlineData(0x51F, 3)]
    [InlineData(0x51D, 1)]     // below the yaw target – untouched
    [InlineData(0, 1)]
    [InlineData(-0x51D, 1)]
    [InlineData(-0x51E, -3)]
    public void AutoDrift_SignOfYawTarget(int yaw, int slip)
    {
        var c = Drift(Threshold, yaw, slip: 1);
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(slip, c.SlipState);
    }

    [Theory]
    [InlineData(Threshold, true)]        // tgt == v → drift applies (the original returns only for tgt > v)
    [InlineData(Threshold - 1, false)]
    public void AutoDrift_ThresholdIsAiSpeedTimesVmaxBelow(int vz, bool drift)
    {
        // tgt (FX([4] << 16, 0xE666) · (80 << 16)>>16) / 0x68 (2949100 · 80) / 104 = 2268538: [ref:3053]
        // 50 m/s (0x32) → FX(·, 0.9) = 2949100 (0xE666 = 0.89999), and +0xF4 = 80 m/s. Slice 5 → byte 4.
        var c = Drift(vz, 0x51E);
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(drift ? 3 : 0, c.SlipState);
    }

    [Fact]
    public void AutoDrift_ClearsDriftWhenBackOnTheRoadNormal()
    {
        // |carUp · roadUp| = 1 > 0xD999 with SlipState ±3 → 0 and return (v and yaw target ignored).
        var c = Drift(Threshold, 0x51E, slip: 3);
        c.RoadFrame = new Mat3(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000);
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(0, c.SlipState);

        c = Drift(Threshold, -0x51E, slip: -3);
        c.RoadFrame = new Mat3(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000);
        e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(0, c.SlipState);
    }

    [Fact]
    public void AutoDrift_ClearIsOnlyForAbs3()
    {
        // SlipState 2 is not "3" → no clear; the drift branch then overwrites it with 3.
        var c = Drift(Threshold, 0x51E, slip: 2);
        c.RoadFrame = new Mat3(0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x10000);
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(3, c.SlipState);
    }

    [Fact]
    public void AutoDrift_OnlyForTheLocalHuman()
    {
        var c = Drift(Threshold, 0x51E, slip: 1);
        c.Flags = CarFlags.Racer;                       // an AI car: no drift
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(1, c.SlipState);
    }

    [Fact]
    public void AutoDrift_NeedsTheSpeedTable()
    {
        var c = Drift(Threshold, 0x51E, slip: 1);
        var e = new ArcadeEnv { AiSpeeds = null };       // "AI_SPEED file not loaded" (fatal in the original)
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(1, c.SlipState);
    }

    [Fact]
    public void AutoDrift_NotWhileAirborne()
    {
        var c = Drift(Threshold, 0x51E, slip: 1);
        c.AirTicks = 7;
        var e = new ArcadeEnv { AiSpeeds = Speeds };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(1, c.SlipState);
    }

    [Fact]
    public void AutoDrift_SpeedTableTooShort_IsSkipped()
    {
        // Port-only guard: the original indexes the table (and its 2 B of slack) unconditionally; a short table
        // must not throw here.
        var c = Drift(Threshold, 0x51E, slip: 1);
        var e = new ArcadeEnv { AiSpeeds = [0, 0] };
        ArcadeModel.PlayerAssist(c, Specs, ref e);
        Assert.Equal(1, c.SlipState);
    }

    [GameFact]
    public void Step_AppliesTheAssist_WhenConfigured()
    {
        var sp = CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")),
            new DeriveOptions(true, false));
        CarObj Make() => new()
        {
            Orient = Mat3.Identity,
            RoadFrame = new Mat3(0x10000, 0, 0, 0x8000, 0x8000, 0, 0, 0, 0x10000),
            DriveFrame = Mat3.Identity,
            Vel = new Vec3(0, -0x10000, 0),
            AngVel = new Vec3(7, 8, 9),
            AirTicks = 3,
            HeightAboveRoad = 0x8000,
            Gear = 3, PrevGear = 3, Rpm = 3000, SurfaceType = 0, TimeScale = Fx.One,
        };
        var off = Make();
        var e1 = new ArcadeEnv { Throttle = 0x10000 };
        ArcadeModel.Step(off, sp, ref e1, raceStarted: true);

        var on = Make();
        var e2 = new ArcadeEnv { Throttle = 0x10000, PlayerAssist = true };
        ArcadeModel.Step(on, sp, ref e2, raceStarted: true);

        // Airborne (0x14C ≠ 0) → the step returns before the forces, so the only writer of ω is the assist
        // (125, before the early-out). [ref:3054]
        Assert.Equal(new Vec3(7, 8, 9), off.AngVel);
        Assert.Equal(new Vec3(0, 0, -0x8000), on.AngVel);
    }
}
