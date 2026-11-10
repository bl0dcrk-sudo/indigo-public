using NFS.Core;
using NFS.Core.Tables;
using NFS.Data.Ai;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>Values taken from the original's assembly. [ref:3040]
/// Synthetic states exercise paths absent from the beginner replay (catchup = 0).</summary>
public class AiReactionsTests
{
    [Theory]
    [InlineData(-300, 0)] [InlineData(-16, 9)] [InlineData(-14, 10)]
    [InlineData(14, 10)] [InlineData(15, 11)] [InlineData(300, 20)]
    public void AccelGlue_UsesTruncatedProgressDifferenceAndClampedPersonalityIndex(int delta, int index)
    {
        var c = new CarObj { Flags = CarFlags.Opponent, Progress = (ushort)(1000 + delta) };
        var lead = new CarObj { Progress = 1000, AiSpeed = 0x20000 };
        int[] curve = Enumerable.Range(0, 21).Select(i => Fx.One + i * 100).ToArray();
        Assert.Equal(Fx.One + index * 100, AiDriver.AccelGlue(c, lead, curve, true, false, 0, Fx.One, Fx.One, Fx.One));
    }

    [Theory]
    [InlineData(0, 147456)] [InlineData(1, 98304)] [InlineData(2, 98304)]
    public void AccelGlue_MultipliesAccThenSkill_ExceptTournamentAndKnockout(int raceType, int expected)
    {
        var c = new CarObj { Flags = CarFlags.Opponent, Progress = 101 };
        var lead = new CarObj { Progress = 100, AiSpeed = 0x20000 };
        Assert.Equal(expected, AiDriver.AccelGlue(c, lead, Enumerable.Repeat(0x20000, 21).ToArray(),
            true, false, raceType, 0x18000, 0xC000, 0x14000)); // 2 × .75 × 1.5; no progress/lap ramp
        Assert.Equal(raceType == 0 ? 61440 : 40960, AiDriver.AccelGlue(c, lead,
            Enumerable.Repeat(0x8000, 21).ToArray(), true, false, raceType, 0x18000, 0xC000, 0x14000));
    }

    [Fact]
    public void AccelGlue_DisabledSlowLeaderCrashReverseAndUnsignedProgress()
    {
        int[] curve = Enumerable.Repeat(0x18000, 21).ToArray();
        var c = new CarObj { Flags = CarFlags.Opponent, Progress = 90 };
        var lead = new CarObj { Progress = 100, AiSpeed = 0x1FFFF };
        int Glue(bool enabled = true, bool reverse = false) => AiDriver.AccelGlue(c, lead, curve, enabled, reverse, 0, Fx.One, Fx.One, Fx.One);
        Assert.Equal(Fx.One, Glue());
        lead.AiSpeed = 0x20000;
        Assert.Equal(0x18000, Glue()); // inclusive 2 m/s
        Assert.Equal(Fx.One, Glue(false));
        Assert.Equal(Fx.One, Glue(reverse: true));
        lead.AiSpeed = -0x20000;
        Assert.Equal(0x18000, Glue(reverse: true));
        lead.Unk14E = 1;
        Assert.Equal(Fx.One, Glue()); // behind a crashed leader, boost suppressed
        curve[10] = 0x8000;
        Assert.Equal(0x8000, Glue()); // penalty remains
        c.Progress = 101; curve[10] = 0x18000;
        Assert.Equal(0x18000, Glue()); // ahead of crashed leader
        c.Flags = CarFlags.Traffic;
        Assert.Equal(Fx.One, Glue());
        c.Flags = CarFlags.Opponent; c.Progress = ushort.MaxValue;
        curve[20] = 12345;
        Assert.Equal(12345, Glue()); // MOVZX ushort(-1) = 65535, last bin
    }

    [Fact]
    public void Accel_AppliesGlueBeforeBeginnerAndPsycho_RoundsEachMultiply()
    {
        var c = new CarObj { AiSpeed = 0, Progress = 101, PsychoTicks = 1 };
        c.AccelCurve[0] = 257;
        // 65792 × 0x10001 => 65793; ×.75 => 49345; ×0x14CCC => 64148.
        Assert.Equal(64148, AiDriveModel.Accel(c, skillLevel: 0, accelGlue: 0x10001));
        c.ShiftCounter = 1;
        Assert.Equal(0, AiDriveModel.Accel(c, accelGlue: 0x20000));
    }

    [Theory]
    [InlineData(0x9FFFF, -1)] [InlineData(-0x9FFFF, -1)]
    [InlineData(0xA0000, 1)] [InlineData(-0xA0000, 1)]
    public void OnHit_AndOnWallHit_ReverseBelowTenMetresPerSecond(int speed, int direction)
    {
        var c = new CarObj { AiSpeed = speed, Impact = 1, ImpactOther = new CarObj() };
        AiReactions.OnHit(c, 0, 0, 0, 100);
        Assert.Equal(direction, c.Direction);
        if (direction == -1) Assert.Equal((0x60, 100), (c.DirDuration, c.DirChangeTick));
        var wall = new CarObj { AiSpeed = speed };
        AiReactions.OnWallHit(wall, 100);
        Assert.Equal(direction, wall.Direction);
        wall.Flags = CarFlags.LocalHuman; wall.Direction = 1;
        AiReactions.OnWallHit(wall, 100);
        Assert.Equal(1, wall.Direction);
    }

    [Fact]
    public void OnHit_CountsOnlyHumanHits_UsesStrictThreshold_AndRestartsForAnotherTarget()
    {
        var human = new CarObj { Flags = CarFlags.LocalHuman };
        var c = new CarObj { Flags = CarFlags.Opponent, AiSpeed = 0xA0000, Impact = 1, ImpactOther = human };
        AiReactions.OnHit(c, 1, 2, 250, 100);
        AiReactions.OnHit(c, 1, 2, 250, 102);
        Assert.Equal((2, 0), (c.PsychoHits, c.PsychoTicks));
        Assert.Same(human, c.PsychoTarget);
        AiReactions.OnHit(c, 1, 2, 250, 104);
        Assert.Equal((3, 250), (c.PsychoHits, c.PsychoTicks));
        c.ImpactOther = new CarObj { Flags = CarFlags.LocalHuman };
        AiReactions.OnHit(c, 1, 2, 250, 106);
        Assert.Same(human, c.PsychoTarget); // active chase ignores new target
        c.PsychoTicks = 0;
        AiReactions.OnHit(c, 1, 2, 250, 108);
        Assert.Equal((1, 0), (c.PsychoHits, c.PsychoTicks));
        Assert.Same(c.ImpactOther, c.PsychoTarget);
        c.AiSpeed = 0;
        AiReactions.OnHit(c, 0, 0, 250, 110);
        Assert.Equal(-1, c.Direction); // beginner still reverses
        Assert.Equal(1, c.PsychoHits);
    }

    [Fact]
    public void OnHit_GuardsImpactOtherOpponentAndHumanFlags()
    {
        var c = new CarObj { Flags = CarFlags.Opponent, AiSpeed = 0, ImpactOther = new CarObj { Flags = CarFlags.LocalHuman } };
        AiReactions.OnHit(c, 1, 0, 250, 100);
        Assert.Equal((1, 0), (c.Direction, c.PsychoTicks));
        c.Impact = 1; c.ImpactOther = null;
        AiReactions.OnHit(c, 1, 0, 250, 100);
        Assert.Equal(1, c.Direction);
        c.ImpactOther = new CarObj { Flags = CarFlags.Opponent };
        AiReactions.OnHit(c, 1, 0, 250, 100);
        Assert.Equal((-1, 0), (c.Direction, c.PsychoTicks));
        c.Flags = CarFlags.Traffic; c.ImpactOther.Flags = CarFlags.LocalHuman;
        AiReactions.OnHit(c, 1, 0, 250, 100);
        Assert.Equal(0, c.PsychoTicks);
    }

    private static CarObj Chaser(int selfSlice, int targetSlice, int speed = 0, int flags = 0)
    {
        var target = new CarObj { AiSpeed = speed, Pos = new Vec3(100, 200, 300), LateralOffsetCur = 76543 };
        target.Trk.Slice = targetSlice;
        var c = new CarObj { PsychoTicks = 10, PsychoTarget = target, AiFlags = flags, Pos = new Vec3(10, 20, 30) };
        c.Trk.Slice = selfSlice;
        return c;
    }

    [Theory]
    [InlineData(75, false, true)] [InlineData(76, false, false)]
    [InlineData(-200, false, true)] [InlineData(-75, true, true)] [InlineData(-76, true, false)]
    public void PsychoActive_SignedWrappedDistanceHasOnlyAnUpperBound(int delta, bool reverse, bool expected)
    {
        var c = Chaser((1000 + delta) % 1000, 0);
        Assert.Equal(expected, AiReactions.PsychoActive(c, 1000, reverse, true));
        Assert.Equal(expected ? 10 : 0, c.PsychoTicks);
    }

    [Fact]
    public void PsychoActive_ClearsOnRailsFinishBeforeStartOrMissingTarget()
    {
        foreach (int condition in Enumerable.Range(0, 4))
        {
            var c = Chaser(10, 11);
            if (condition == 0) c.Rail = 1;
            if (condition == 1) { c.Flags = CarFlags.Racer; c.RaceState = 2; }
            if (condition == 2) c.PsychoTarget = null;
            Assert.False(AiReactions.PsychoActive(c, 1000, false, condition != 3));
            Assert.Equal(0, c.PsychoTicks);
        }
        var traffic = Chaser(10, 11); traffic.RaceState = 2;
        Assert.True(AiReactions.PsychoActive(traffic, 1000, false, true));
    }

    [Theory]
    // self−target, target velocity, initial wrong-way, track reverse, final wrong-way, speed
    [InlineData(-10, 40, 0, false, 0, 50)]
    [InlineData(10, 40, 0, false, 0, 32)]
    [InlineData(10, 20, 0, false, 0x1000, -50)]
    [InlineData(-10, -20, 0x1000, false, 0, 50)]
    [InlineData(-10, -40, 0x1000, false, 0x1000, 38)]
    [InlineData(-100, -40, 0x1000, false, 0x1000, -50)]
    [InlineData(100, 40, 0, false, 0, 50)]
    [InlineData(101, 40, 0, false, 0x1000, -50)]
    [InlineData(-101, -40, 0x1000, false, 0, 50)]
    [InlineData(10, -40, 0, true, 0, -50)]
    [InlineData(10, 40, 0, true, 0, 38)]
    [InlineData(-10, -40, 0x1000, true, 0x1000, 32)]
    public void PsychoChaseSpeed_TurnsAndUsesStrictSpeedAndDistanceBounds(int delta, int v, int flags, bool reverse, int wantFlags, int speed)
    {
        var c = Chaser(200 + delta, 200, v * Fx.One, flags | 0x80);
        AiReactions.PsychoChaseSpeed(c, 50 * Fx.One, 1000, reverse);
        Assert.Equal(wantFlags | 0x80, c.AiFlags);
        Assert.Equal(speed * Fx.One, c.AiTargetSpeed);
    }

    [Theory]
    [InlineData(-29, 0, true)] [InlineData(-30, 0, false)]
    [InlineData(29, 0x1000, true)] [InlineData(30, 0x1000, false)]
    [InlineData(0, 0, false)] [InlineData(10, 0, false)]
    public void TargetPsycho_UsesNearbyCarPositionAndLateralOffset(int delta, int flags, bool expected)
    {
        var c = Chaser((1000 + delta) % 1000, 0, flags: flags);
        c.ToTarget = new Vec3(1, 2, 3); c.SmoothedLaneOffset = 5;
        Assert.Equal(expected, AiReactions.TargetPsycho(c, 1000));
        Assert.Equal(expected ? new Vec3(90, 180, 270) : new Vec3(1, 2, 3), c.ToTarget);
        Assert.Equal(expected ? 76543 : 5, c.SmoothedLaneOffset);
        c.Direction = -1;
        Assert.False(AiReactions.TargetPsycho(c, 1000));
        c.Direction = 1; c.PsychoTicks = 0;
        Assert.False(AiReactions.TargetPsycho(c, 1000));
    }

    [Theory]
    [InlineData(0, 2, 10, 5)] [InlineData(1, 1, 50, 4)]
    [InlineData(2, 2, 30, 2)] [InlineData(3, 3, 1, 40)]
    public void UpdatePsycho_RequestsHornDecrementsOnceAndClearsAtExpiry(int slice, int pattern, int length, int volume)
    {
        var c = Chaser(slice, slice + 1); c.PsychoTicks = 1; c.PsychoHits = 9; c.AiFlags = 0x1080;
        (int, int, int)? horn = null;
        AiReactions.UpdatePsycho(c, 1000, false, true, (_, p, l, v) => horn = (p, l, v));
        Assert.Equal((pattern, length, volume), horn);
        Assert.Equal((0, 0, 0x80), (c.PsychoTicks, c.PsychoHits, c.AiFlags));
        c.PsychoTicks = 4; c.Rail = 1; c.PsychoHits = 9; c.AiFlags = 0x1000;
        AiReactions.UpdatePsycho(c, 1000, false, true);
        Assert.Equal((-1, 0, 0), (c.PsychoTicks, c.PsychoHits, c.AiFlags)); // cleared by active, THEN DEC
        AiReactions.UpdatePsycho(c, 1000, false, true);
        Assert.Equal(-1, c.PsychoTicks);
    }

    [GameFact]
    public void Drive_PsychoTargetSurvivesHeadingAndReplacesTheNormalLanePoint()
    {
        TrackSlice[] track = Enumerable.Range(0, 100).Select(i => new TrackSlice(0, 0, i * Fx.One,
            [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0x22, 8, 8, 0, 0)).ToArray();
        var c = Chaser(0, 1); c.Orient = c.RoadFrame = Mat3.Identity;
        var ai = new AiDriver(track, false, null, 0);
        ai.Drive(c, dt: 0, planned: true, updateGearbox: false);
        Assert.Equal(new Vec3(90, 180, 270), c.ToTarget);
        Assert.Equal(76543, c.SmoothedLaneOffset);
        Assert.Equal(FxMath.Atan2(90, 270), c.TargetHeading);
    }

    [GameFact]
    public void PostUpdate_ReactsBeforeImpactFieldsAreCleared_AndSkipsRails()
    {
        var p = Prsonal.Parse(GameData.ReadRaw("GAMEDATA/SIM/AI/PRSONAL.DAT")).ByLetter('g')!;
        Assert.Equal((14, 250), (p.PsychoTriggerCount, p.PsychoTime));
        int[] st = new int[29]; st[CarPost.StCrashImpact] = int.MaxValue;
        var human = new CarObj { Flags = CarFlags.LocalHuman };
        var c = new CarObj { Flags = CarFlags.Opponent, Active = 1, Impact = 1, ImpactOther = human,
            PsychoTarget = human, PsychoHits = 14, AiSpeed = 0xA0000, DistToPlayer = 0x640000 };
        var env = new CarPostEnv { SimTune = st, Slices = [], TrackUpdate = _ => { },
            SkillLevel = 1, PersonalityOf = _ => p };
        CarPost.Update(c, env, 100);
        Assert.Equal((15, 250), (c.PsychoHits, c.PsychoTicks));
        Assert.Equal(0, c.Impact); Assert.Null(c.ImpactOther);
        c.Rail = 1; c.Impact = 1; c.ImpactOther = human; c.PsychoTicks = 0;
        CarPost.Update(c, env, 102);
        Assert.Equal((15, 0), (c.PsychoHits, c.PsychoTicks));
    }

    [GameFact]
    public void SkillAcc_UsesExePersonalityAndLevelTable()
    {
        // Independent dump with inputs: a=(1,1), b=(0xFAE1,0xFD70), l=(0xC7AE,0xE3D7), m=(1,1). [ref:3041]
        var t = GameTables.Default;
        Assert.Equal(Fx.One, t.AiSkillAccOf(0, 0));
        Assert.Equal(0xFAE1, t.AiSkillAccOf(1, 0));
        Assert.Equal(0xFD70, t.AiSkillAccOf(1, 1));
        Assert.Equal(0xC7AE, t.AiSkillAccOf(11, 0));
        Assert.Equal(0xE3D7, t.AiSkillAccOf(11, 1));
        Assert.Equal(Fx.One, t.AiSkillAccOf(12, 1));
    }

    [GameFact]
    public void AccelerationGlue_ReachesBothSteeringModelsAndRailVelocity()
    {
        var c = new CarObj { AiSpeed = Fx.One, LocalVel = new Vec3(0, 0, Fx.One),
            AiSpeedApprox = Fx.One, AiTargetSpeed = 5 * Fx.One, RoadFrame = Mat3.Identity, Orient = Mat3.Identity };
        Array.Fill(c.AccelCurve, (short)256); // 1 m/s², factor 2 = 2 m/s²
        var ctl = AiDriveModel.SteerInControl(c, AiDriveModel.Params.DefaultInControl, 1, false, accelGlue: 2 * Fx.One);
        Assert.Equal(2 * Fx.One, ctl.Road.Z);
        var ooc = AiDriveModel.SteerOutOfControl(c, AiDriveModel.Params.DefaultOutOfControl, 1, false, accelGlue: 2 * Fx.One);
        Assert.Equal(2 * Fx.One, ooc.Car.Z); // heading = velocity heading → g = 1
        AiDriveModel.RailStep(c, 0x800, false, false, accelGlue: 2 * Fx.One);
        Assert.Equal(Fx.One + 4096, c.AiSpeed); // dt=1/32: 1 + 2/32
        Assert.Equal(new Vec3(0, 0, Fx.One + 4096), c.Vel);
    }
}
