using NFS.Core;
using NFS.Core.Tables;
using NFS.Data.Car;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>Race option flags set by cheat codes: SLIP (8, table), CHASE (0x100), [ref:3079]
/// ROADRAGE (0x800, ->), RAIN (0x40). Without the flag [ref:3080]
/// nothing changes.</summary>
public class RaceOptionEffectsTests
{
    // ---------------------------------------------------------------- SLIP

    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(true, false));

    private static (int Dist, int Speed, int Grip) Brake(bool slip, int ticks = 40)
    {
        var sp = F355();
        var c = new CarObj
        {
            DriveFrame = Mat3.Identity, Orient = Mat3.Identity, Vel = new Vec3(0, 0, 0x300000), TimeScale = Fx.One,
            Gear = 3, PrevGear = 3, Rpm = 3000, SurfaceType = 0,
        };
        int dist = 0;
        for (int i = 0; i < ticks; i++)
        {
            c.ThrottleRamped = 0; c.BrakeRamped = 0xF8;
            var e = new ArcadeEnv { Brake = 0x10000 };
            ArcadeModel.Step(c, sp, ref e, raceStarted: true, gameOption8: slip);
            dist += c.Vel.Z / 32;
        }
        return (dist, c.Vel.Z, c.GripClass);
    }

    [GameFact]
    public void Slip_UsesTheSlipperyGripClassAndBrakingGetsLonger()
    {
        var normal = Brake(false);
        var slip = Brake(true);
        Assert.Equal(0, normal.Grip);       // road surface 0 -> grip class table [ref:3081]
        Assert.Equal(9, slip.Grip);         // every surface class 9 (mu 0.6) [ref:3082]
        Assert.True(slip.Speed > normal.Speed, $"{slip.Speed:X} vs {normal.Speed:X}");
        Assert.True(slip.Dist > normal.Dist);
    }

    [GameFact]
    public void Slip_IsDeterministic() => Assert.Equal(Brake(true), Brake(true));

    // ---------------------------------------------------------------- CHASE

    [Fact]
    public void Chase_OpponentNearTheLeadingHumanTargetsHim()
    {
        var human = new CarObj { Flags = CarFlags.LocalHuman | CarFlags.Racer, Progress = 1004 };
        var ai = new CarObj { Flags = CarFlags.Opponent, Progress = 1001 };
        RaceOptionEffects.Chase(ai, human, RaceOptionEffects.OptChase);
        Assert.Equal(3, ai.PsychoTicks);                 // |lead - car| slices
        Assert.Same(human, ai.PsychoTarget);

        ai = new CarObj { Flags = CarFlags.Opponent, Progress = 1005 };   // human 1 behind
        RaceOptionEffects.Chase(ai, human, RaceOptionEffects.OptChase);
        Assert.Equal(1, ai.PsychoTicks);
    }

    [Theory]
    [InlineData(0)]                       // option off
    [InlineData(RaceOptionEffects.OptSlip | RaceOptionEffects.OptRoadRage | 0x40 | 0x400)]
    public void Chase_NoFlagNoEffect(int options)
    {
        var human = new CarObj { Flags = CarFlags.LocalHuman, Progress = 1002 };
        var ai = new CarObj { Flags = CarFlags.Opponent, Progress = 1000 };
        RaceOptionEffects.Chase(ai, human, options);
        Assert.Equal(0, ai.PsychoTicks);
        Assert.Null(ai.PsychoTarget);
    }

    [Theory]
    [InlineData(1005, 1000, false)]       // human 5 ahead: out (< 5)
    [InlineData(1004, 1000, true)]
    [InlineData(1000, 1002, false)]       // human 2 behind: out (> -2)
    [InlineData(1000, 1001, true)]
    public void Chase_WindowIsMinusOneToPlusFour(int humanProgress, int aiProgress, bool chases)
    {
        var human = new CarObj { Flags = CarFlags.LocalHuman, Progress = (ushort)humanProgress };
        var ai = new CarObj { Flags = CarFlags.Opponent, Progress = (ushort)aiProgress };
        RaceOptionEffects.Chase(ai, human, RaceOptionEffects.OptChase);
        Assert.Equal(chases, ai.PsychoTarget != null);
    }

    [Fact]
    public void Chase_OnlyRacingOpponentsAndOnlyWithAHuman()
    {
        var human = new CarObj { Flags = CarFlags.LocalHuman, Progress = 1002 };
        var traffic = new CarObj { Flags = CarFlags.Traffic, Progress = 1000 };
        RaceOptionEffects.Chase(traffic, human, RaceOptionEffects.OptChase);
        Assert.Null(traffic.PsychoTarget);
        var ai = new CarObj { Flags = CarFlags.Opponent, Progress = 1000 };
        RaceOptionEffects.Chase(ai, null, RaceOptionEffects.OptChase);
        Assert.Null(ai.PsychoTarget);
    }

    [Fact]
    public void LeadHuman_IsTheOneWithTheMostProgress_AndKeepsThePreviousAtZero()
    {
        var a = new CarObj { Progress = 10 }; var b = new CarObj { Progress = 30 }; var c = new CarObj { Progress = 20 };
        Assert.Same(b, RaceOptionEffects.FindLeadHuman([a, b, c], null));
        var z = new CarObj();
        Assert.Same(a, RaceOptionEffects.FindLeadHuman([z], a));
        Assert.Null(RaceOptionEffects.FindLeadHuman([z], null));
    }

    // ---------------------------------------------------------------- ROADRAGE

    private static CarObj Racer(int x, int z) => new()
    {
        Pos = new Vec3(x, 0, z), Orient = Mat3.Identity, DriveFrame = Mat3.Identity, Active = 1,
    };

    private static (CarObj Me, CarObj Ahead, CarObj Far, CarObj Behind) Scene()
    {
        var me = Racer(0, 0); me.Flags = CarFlags.LocalHuman | CarFlags.Racer; me.Horn = 1;
        return (me, Racer(0, 20 << 16), Racer(0, 60 << 16), Racer(0, -10 << 16));
    }

    [GameFact]
    public void RoadRage_HornFlingsTheNearestCarAhead()
    {
        var (me, ahead, far, behind) = Scene();
        var hit = RaceOptionEffects.HornLaunch(me, [me, behind, far, ahead], RaceOptionEffects.OptRoadRage, true, 1, 0x10, GameTables.Default);
        Assert.Same(ahead, hit);
        Assert.Equal(1, ahead.AirTicks);                  // +0x14C [ref:3083]
        Assert.True(ahead.Vel.Y > 0);                     // kicked up along the drive frame up vector
        Assert.Equal(0, far.AirTicks); Assert.Equal(0, behind.AirTicks); Assert.Equal(0, me.AirTicks);
    }

    [GameFact]
    public void RoadRage_NothingWithoutTheOptionHornRaceStartOrAnAirborneTarget()
    {
        var (me, ahead, far, behind) = Scene();
        var all = new[] { me, behind, far, ahead };
        Assert.Null(RaceOptionEffects.HornLaunch(me, all, 0, true, 1, 0, GameTables.Default));
        Assert.Null(RaceOptionEffects.HornLaunch(me, all, RaceOptionEffects.OptSlip | RaceOptionEffects.OptChase | 0x400, true, 1, 0, GameTables.Default));
        Assert.Null(RaceOptionEffects.HornLaunch(me, all, RaceOptionEffects.OptRoadRage, false, 1, 0, GameTables.Default));
        me.Horn = 0;
        Assert.Null(RaceOptionEffects.HornLaunch(me, all, RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
        me.Horn = 1; ahead.AirTicks = 3;
        Assert.Null(RaceOptionEffects.HornLaunch(me, all, RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
        Assert.All(all, c => Assert.Equal(c == ahead ? 3 : 0, c.AirTicks));
    }

    [Fact]
    public void RoadRage_ConeAndRangeAreNarrow()
    {
        var (me, _, _, _) = Scene();
        var side = Racer(20 << 16, 20 << 16);     // 45 degrees off the nose: cos 0.71 < 0.8
        var tooFar = Racer(0, 41 << 16);
        Assert.Null(RaceOptionEffects.HornLaunch(me, [me, side, tooFar], RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
        var edge = Racer(8 << 16, 30 << 16);      // cos about 0.97
        Assert.Same(edge, RaceOptionEffects.HornLaunch(me, [me, side, edge], RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
    }

    [Fact]
    public void RoadRage_OnlyForLocalHumansNotFinishedOrCrashed()
    {
        var (me, ahead, _, _) = Scene();
        me.Flags = CarFlags.Opponent;
        Assert.Null(RaceOptionEffects.HornLaunch(me, [me, ahead], RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
        me.Flags = CarFlags.LocalHuman; me.RaceState = 2;
        Assert.Null(RaceOptionEffects.HornLaunch(me, [me, ahead], RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
        me.RaceState = 0; me.CrashSlice = 5;
        Assert.Null(RaceOptionEffects.HornLaunch(me, [me, ahead], RaceOptionEffects.OptRoadRage, true, 1, 0, GameTables.Default));
    }

    [Theory]
    [InlineData(10, 0, 0, 10)]
    [InlineData(10, 20, 0, 22)]               // max + mid/4 (mid = 10 >> 2 = 2), min 0
    [InlineData(100, 100, 100, 150)]          // 100 + 25, then + 100 >> 2
    [InlineData(-100, 0, 40, 110)]
    public void DistApprox3_FollowsTheExeNesting(int a, int b, int c, int expected) =>
        Assert.Equal(expected, RaceOptionEffects.DistApprox3(new Vec3(a, b, c), default));
}
