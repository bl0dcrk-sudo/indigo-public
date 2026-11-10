using NFS.Core;
using NFS.Data.Ai;
using NFS.Data.Track;
using NFS.Tests.Data;
using NFS.Game.Race;

namespace NFS.Tests.Core;

/// <summary>
/// Details found by comparing the port with the original game in simulation mode (TR06,
/// whole race: all 8 cars bit-exact after these fixes): rounding in, the inverse-lane-width table [ref:3091]
/// road-edge hit = in sim, the sim branch, and the AI's [ref:3092]
/// row. [ref:3093]
/// </summary>
public class SimFixesTests
{
    [GameFact]
    public void Rotate2d_UsesFxSinFxCos_NotFxSincos()
    {
        _ = FxMath.Tables;
        int differing = 0;
        for (int ang = -0x40000; ang < 0x40000; ang += 7)
        {
            SimModel.Rotate2d(Fx.One, 0, ang, out int cosGot, out int sinGot);
            int a = Fx.DivPow2(ang, 8);                                              // tab ((−sin_tab >> 2) · f >> 21) [ref:3094]
            FxMath.SinCosTab(a >> 6, out int s0, out int c0);
            int f = ((a & 63) * 0x6487E) >> 9;
            Assert.Equal(c0 + (((-s0 >> 2) * f) >> 21), cosGot);                    // rotating (1, 0): ox = cos, oz = sin
            Assert.Equal(s0 + (((c0 >> 2) * f) >> 21), sinGot);
            if (cosGot != FxMath.Cos(a)) differing++;
        }
        Assert.True(differing > 0, "the cosine function differs from the sincos cosine by one LSB for some angles (floor of the negated product)");
    }

    private static TrackSlice Slice(byte left, byte right, byte lanes = 0x33) =>
        new(0, 0, 0, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, lanes, left, right, 0, 0);

    [Fact]
    public void LaneFromOffset_UsesInverseWidthTable_ZeroWidthGivesLane6Or7()
    {
        _ = FxMath.Tables;
        // width byte 12 (6 m):[24] = FXDIV(1.0, 6.0·…) ≈ 0x2AAA, so exactly −6.0 m is still lane 6 (division would give 5) [ref:3095]
        var sl = Slice(12, 12);
        Assert.Equal(6, AiDriver.LaneFromOffset(sl, -0x60000));
        Assert.Equal(5, AiDriver.LaneFromOffset(sl, -0x60001 - 0x10000));
        Assert.Equal(7, AiDriver.LaneFromOffset(sl, 0));
        Assert.Equal(7, AiDriver.LaneFromOffset(sl, 0x5FFFF));
        Assert.Equal(8, AiDriver.LaneFromOffset(sl, 0x60000 + 0x40));
        Assert.Equal(13, AiDriver.LaneFromOffset(sl, 0x7FFFFFF));
        // one-way track: left width 0 → inverse 0 → lane 6 (not 0) for any negative offset; right side likewise lane 7
        var oneWay = Slice(0, 12);
        Assert.Equal(6, AiDriver.LaneFromOffset(oneWay, -0x50000));
        Assert.Equal(7, AiDriver.LaneFromOffset(Slice(12, 0), 0x50000));
    }

    [GameFact]
    public void CrashReset_Sim_PlacesOnTheEightSliceGrid_ByCarIndex()
    {
        var run = new RaceRunner(TrackSessions.FreshReplaySim(), 0);
        var c = run.Car;
        var env = new CarPostEnv { SimTune = run.SimTune, ArcadeMode = 0, Slices = run.Session.Slices, Reverse = run.Session.Backward, TrackUpdate = run.Road.UpdateCar };
        int n = env.Slices.Length, dir = env.Reverse ? -1 : 1;
        foreach (int idx in new[] { 0, 1, 2, 5 })
        {
            c.PlayerIndex = idx;
            c.CrashSlice = 300;
            CarPost.CrashReset(c, env, force: true);
            int s = 300 + run.SimTune[CarPost.StResetSlices] * dir;
            int expected = (s / 8 * 8 + 8 * dir + idx / 2 * dir) % n;   // human (flag 4): +8 in race direction, + (index/2)
            Assert.Equal(expected, c.Trk.Slice);
            Assert.Equal(2, c.Gear);                                     // [ref:3096]
            Assert.Equal(0, c.CrashSlice);
        }
    }

    [Fact]
    public void BlockHuman_AddsFourAndAHalfOnTheHumansLane_OnlyWithSkillAndStraightRoad()
    {
        TrackSlice[] slices = Enumerable.Range(0, 100).Select(_ => Slice(12, 12, 0x33)).ToArray();
        var pers = new Personality((byte)'a', new byte[3], new int[21], new int[21], 0, 0, 30 /* ×4 m */, -0x100000 /* −16 m */,
            0, 0, 30, new int[4], 0, 0, 0);
        CarObj Car(int slice, int lane, CarFlags f, int aiSpeed, int z)
        {
            var o = new CarObj { Flags = f, Active = 1, Lane = lane, AiSpeed = aiSpeed, ListPosition = 99, Progress = 500 };
            o.Trk.Slice = slice; o.Pos = new Vec3(0, 0, z); o.RoadFrame = Mat3.Identity;
            return o;
        }
        var ai = Car(50, 7, CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, 0x300000, 0);
        var human = Car(48, 8, CarFlags.Racer | CarFlags.LocalHuman, 0x300000, -0x30000);   // 2 slices behind, 3 m behind along the road
        var scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0, 0, 0x48000], scores);                           // human lane 8 > AI lane 7 → right column
        human.Lane = 7;
        scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0, 0x48000, 0], scores);
        human.Lane = 6;
        scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0x48000, 0, 0], scores);

        scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 0, 0, pers, scores);   // skill 0 (the shipped replays): off
        Assert.Equal([0, 0, 0], scores);
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 5, pers, scores);   // in a bend: off
        Assert.Equal([0, 0, 0], scores);
        human.AiSpeed = 0x100000;                                                                // slower than 20 m/s: off
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0, 0, 0], scores);
        human.AiSpeed = 0x300000;
        human.Trk.Slice = 40;                                                                    // 10 slices (40 m) behind: counted by slices
        scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0x48000, 0, 0], scores);
        human.Trk.Slice = 10;                                                                    // 40 slices (160 m) behind ≥ 30·4 m: out of range
        scores = new int[3];
        AiSpeedPlanning.ScoreBlockHuman(ai, [ai, human], slices, false, 7, 1, 0, pers, scores);
        Assert.Equal([0, 0, 0], scores);
    }
}
