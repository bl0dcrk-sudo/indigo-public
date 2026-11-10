using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Split-screen simulation: two humans in one race (RaceRunner with a second input), LOD by the nearest human,
/// race ends after both humans finish, human catch-up (tag 9). One player stays bit-for-bit [ref:3239]
/// (replay-compare); here the behaviour of two is checked.</summary>
public class SplitRaceTests
{
    static RaceSession Session(int opponentCount, Action<FeConfig>? edit = null)
    {
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 1; cfg.RaceType = 0; cfg.OpponentCount = (byte)opponentCount; cfg.TrafficOff = 1; cfg.Track = 1;
        edit?.Invoke(cfg);
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        return new RaceLoader(GameData.Dir).Load(setup);
    }

    static RaceRunner Runner(RaceSession s) => new(s, 0, automatic: true, withAi: true, secondPlayerIndex: 1, automatic2: true);

    static void ToGreenLight(RaceRunner r, PlayerInput? p1 = null, PlayerInput? p2 = null)
    {
        while (!r.RaceStarted) r.Step(p1 ?? default, p2);
    }

    [GameFact]
    public void BothHumansAreLocalCars_OpponentsAreDrivenByTheAi()
    {
        var run = Runner(Session(2));
        Assert.Equal(2, run.Humans.Count);
        Assert.Same(run.Player.Obj, run.Humans[0]);
        Assert.Same(run.Player2!.Obj, run.Humans[1]);
        Assert.Equal(0, run.Player.Obj.HumanIndex);
        Assert.Equal(1, run.Player2.Obj.HumanIndex);
        Assert.Null(run.PlayerSim.Ai);
        Assert.Null(run.Player2Sim!.Ai);
        Assert.Equal(8, run.Cars.Count);
        Assert.Equal(2, run.Cars.Count(c => c.Ai == null));
        Assert.All(run.Cars, c => Assert.Equal(c.Ai == null, (c.Obj.Flags & CarFlags.LocalHuman) != 0));
        Assert.Equal(2, run.Road.HumanCount);
    }

    [GameFact]
    public void OnePlayerRunner_HasNoSecondHuman()
    {
        var s = Session(2, c => { c.GameType = 0; });
        var run = new RaceRunner(s, 0, automatic: true, withAi: true);
        Assert.Null(run.Player2);
        Assert.Single(run.Humans);
        Assert.Equal(1, run.Road.HumanCount);
    }

    [GameFact]
    public void EachHumanFollowsOnlyItsOwnInput()
    {
        var run = Runner(Session(0));
        ToGreenLight(run);
        var p1Before = run.Player.Obj.Pos;
        for (int t = 0; t < 32 * 6; t++) run.Step(default, new PlayerInput(0, 255, 0, 0));   // only player 2 presses the throttle
        Assert.True(run.Player2!.Obj.SpeedApprox > 5 * Fx.One, "player 2 must drive away on its own input");
        Assert.True(run.Player.Obj.SpeedApprox < Fx.One / 2, "player 1 gave no input and must stand still");
        Assert.True(Math.Abs(run.Player.Obj.Pos.X - p1Before.X) < Fx.One && Math.Abs(run.Player.Obj.Pos.Z - p1Before.Z) < Fx.One);
        // and the other way round
        var run2 = Runner(Session(0));
        ToGreenLight(run2);
        for (int t = 0; t < 32 * 6; t++) run2.Step(new PlayerInput(0, 255, 0, 0), default);
        Assert.True(run2.Player.Obj.SpeedApprox > 5 * Fx.One);
        Assert.True(run2.Player2!.Obj.SpeedApprox < Fx.One / 2);
    }

    [GameFact]
    public void RaceEndsOnlyWhenBothHumansHaveFinished()
    {
        var run = Runner(Session(0));
        run.Player.Obj.RaceState = 2;
        run.Player.Obj.LastProgressTick = 100;
        run.Laps.CheckFinish(1000);
        Assert.False(run.Laps.RaceOver, "the second human is still racing");
        run.Player2!.Obj.RaceState = 2;
        run.Player2.Obj.LastProgressTick = 900;
        run.Laps.CheckFinish(1000);
        Assert.False(run.Laps.RaceOver, "0x140 ticks have not passed since the last human finished");
        run.Laps.CheckFinish(900 + 0x141);
        Assert.True(run.Laps.RaceOver);
    }

    [GameFact]
    public void LodDistance_IsTheMinimumOverBothHumans()
    {
        var s = Session(2);
        var run = Runner(s);
        var p1 = run.Player.Obj;
        var p2 = run.Player2!.Obj;
        var ai = run.Cars.Last().Obj;
        ai.Pos = new Vec3(p2.Pos.X + Fx.One, p2.Pos.Y, p2.Pos.Z);
        ai.Trk.Slice = p2.Trk.Slice;
        p1.Pos = new Vec3(p1.Pos.X + 3000 * Fx.One, p1.Pos.Y, p1.Pos.Z);   // player 1 is far away
        var both = new TrackRoadQuery(run.Locator, s.TrackNumber, run.SimTune) { LocalHuman = p1, OtherHuman = p2, HumanCount = 2 };
        both.LodUpdate(ai);
        int dBoth = ai.DistToPlayer;
        Assert.Equal(0, ai.Rail);
        Assert.True(dBoth < 20 * Fx.One, $"{dBoth / (double)Fx.One} m: the nearest human (player 2) decides");
        var alone = new TrackRoadQuery(run.Locator, s.TrackNumber, run.SimTune) { LocalHuman = p1, HumanCount = 1 };
        alone.LodUpdate(ai);
        Assert.True(ai.DistToPlayer > 244 * Fx.One);
        Assert.Equal(1, ai.Rail);   // with only player 1 the car would be put on rails
    }

    [Fact]
    public void CatchUpMultiplier_FollowsTheSimtuneBands()
    {
        int[] t = new int[29];
        (t[21], t[22], t[23], t[24]) = (5, 10, 30, 60);
        (t[25], t[26], t[27], t[28]) = (0x11333, 0x12666, 0x13333, 0x14000);
        Assert.Equal(Fx.One, HumanCatchUp.Multiplier(-7, t));
        Assert.Equal(Fx.One, HumanCatchUp.Multiplier(4, t));
        Assert.Equal(t[25], HumanCatchUp.Multiplier(5, t));
        Assert.Equal(t[25], HumanCatchUp.Multiplier(9, t));
        Assert.Equal(t[26], HumanCatchUp.Multiplier(10, t));
        Assert.Equal(t[27], HumanCatchUp.Multiplier(30, t));
        Assert.Equal(t[28], HumanCatchUp.Multiplier(60, t));
        Assert.Equal(t[28], HumanCatchUp.Multiplier(5000, t));
    }

    [GameFact]
    public void CatchUp_BoostsTheHumanBehindTheLeader_OnlyWhenTheFlagIsSet()
    {
        var withFlag = Session(0);
        withFlag.Setup.Set(0x18, 1);   // tag 9 CATCHUPLOGIC (the menu's "catch up" on, default in split screen)
        var run = Runner(withFlag);
        ToGreenLight(run);
        for (int t = 0; t < 32 * 20; t++) run.Step(new PlayerInput(0, 255, 0, 0), default);   // player 1 drives away, player 2 stays
        Assert.True(run.Player2!.Obj.CatchUp > Fx.One, "the human far behind the leader gets a speed boost");
        Assert.Equal(Fx.One, run.Player.Obj.CatchUp);                       // the leader gets none
        Assert.True(run.Player2.Obj.CatchUp >= run.SimTune[HumanCatchUp.MultiplierIndex]);
        Assert.True(run.Player2.Obj.CatchUp <= run.SimTune[HumanCatchUp.MultiplierIndex + 3]);

        var without = Session(0);
        without.Setup.Set(0x18, 0);
        var run2 = Runner(without);
        ToGreenLight(run2);
        for (int t = 0; t < 32 * 20; t++) run2.Step(new PlayerInput(0, 255, 0, 0), default);
        Assert.Equal(Fx.One, run2.Player2!.Obj.CatchUp);
    }

    [GameFact]
    public void OnePlayerRace_NeverGetsAHumanBoostWithTheFlagOff()
    {
        var s = Session(2, c => c.GameType = 0);
        s.Setup.Set(0x18, 0);
        var run = new RaceRunner(s, 0, automatic: true, withAi: true);
        ToGreenLight(run);
        for (int t = 0; t < 64; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        Assert.Equal(Fx.One, run.Player.Obj.CatchUp);
    }
    static int TrafficHorns(RaceRunner run, int draws)
    {
        var env = run.Traffic!;
        var car = run.TrafficCars[0].Obj;
        car.Pos = run.Car.Pos;                                // within 200 m of a human
        car.AiFlags |= AiTraffic.FlagOncoming;
        car.AiSpeed = Fx.One;
        var scan = new AiSpeedPlanning.Scan();
        scan.Speed[1] = 5 * Fx.One;
        int horns = 0;
        for (int i = 0; i < draws; i++) AiTraffic.Honk(car, env, scan, (_, _, _, _) => horns++);
        return horns;
    }

    [GameFact]
    public void TrafficCars_DoNotHonkInSplitScreen_ButStillDrawTheLcg()
    {
        var one = new RaceRunner(Session(1, c => { c.GameType = 0; c.TrafficOff = 0; }), 0, automatic: true, withAi: true);
        Assert.False(one.Traffic!.SplitScreen);
        Assert.True(TrafficHorns(one, 3000) > 20, "one player: oncoming traffic honks in ~4 % of the draws");
        var split = Runner(Session(0, c => c.TrafficOff = 0));
        Assert.True(split.Traffic!.SplitScreen);
        var before = split.Traffic.Rng.State;
        Assert.Equal(0, TrafficHorns(split, 3000));            // == 1: never [ref:3240]
        Assert.NotEqual(before, split.Traffic.Rng.State);      // the LCG step is drawn regardless
    }
}
