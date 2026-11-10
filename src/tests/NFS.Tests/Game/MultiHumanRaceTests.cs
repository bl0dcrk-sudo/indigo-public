using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Foundation of 3+ human racers: the list-seat constructor of <see cref="RaceRunner"/> (human cars, seat order,
/// no AI driver for them, &gt; 2 LOD rule) and the list-based input API of the runner and the host. The legacy [ref:3160]
/// one/two-human path is untouched – its exactness is covered by <see cref="SplitRaceTests"/>, <see cref="RaceHostTests"/>
/// and the replay comparisons.</summary>
public class MultiHumanRaceTests
{
    /// <summary>Session with two human cars from the front end; the third car of the lineup is turned into a human too,
    /// as a 3-player session builder would ("human +4" of the car record).</summary>
    static RaceSession Session()
    {
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 1; cfg.RaceType = 0; cfg.OpponentCount = 2; cfg.TrafficOff = 1; cfg.Track = 1; cfg.P1Transmission = 1;   // automatic like every human on the server (ServerHosting.ForceAutomaticGearbox)
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        setup.Set(0x6C, 3);
        setup.Set(0x78 + 2 * RaceConfig.CarRecordSize + 4, 1);   // car record +4 = race class (1 = human)
        return new RaceLoader(GameData.Dir).Load(setup);
    }

    static RaceRunner Runner(bool withAi = false, bool countdown = false) =>
        new(Session(), withAi: withAi, countdown: countdown, humanIndices: [0, 1, 2]);

    static PlayerInput Gas => new(0, 255, 0, 0);

    [GameFact]
    public void ThreeHumans_AreThreeLocalCarsWithoutAiAndLodIsNeutral()
    {
        var run = Runner(withAi: true, countdown: true);
        Assert.Equal(3, run.Humans.Count);
        Assert.Equal(3, run.HumanSims.Count);
        Assert.Equal(new[] { 0, 1, 2 }, run.Humans.Select(h => h.HumanIndex).ToArray());
        Assert.All(run.HumanSims, c => Assert.Null(c.Ai));
        Assert.Equal(3, run.Cars.Count(c => c.Ai == null));
        Assert.Equal(5, run.Cars.Count(c => c.Ai != null));                 // 8 cars, 3 humans
        Assert.All(run.Humans, h => Assert.NotEqual(0, (int)(h.Flags & CarFlags.LocalHuman)));
        Assert.All(run.Humans, h => Assert.Equal(0, (int)(h.Flags & CarFlags.NotLocal)));
        Assert.Equal(3, run.Road.HumanCount);
        Assert.Same(run.Humans[0], run.Road.LocalHuman);
        Assert.Same(run.HumanSims[0], run.PlayerSim);

        // > 2 → distance 0 and no rails, unlike the one/two-human path. [ref:3161]
        var ai = run.Cars.First(c => c.Ai != null).Obj;
        ai.DistToPlayer = 0x12345;
        ai.Rail = 0;
        run.Road.LodUpdate(ai);
        Assert.Equal(0, ai.DistToPlayer);
        Assert.Equal(0, ai.Rail);
    }

    [GameFact]
    public void EachOfThreeHumansFollowsOnlyItsOwnSeatInput()
    {
        // 2 s: long enough to prove each car responds to its own throttle, short enough that seat 0 (two grid rows behind
        // the third human in the same lane) does not reach and push that stationary car.
        const int ticks = 32 * 2;
        for (int seat = 0; seat < 3; seat++)
        {
            var run = Runner();
            var start = run.Humans.Select(h => h.Pos).ToArray();
            for (int t = 0; t < ticks; t++)
            {
                var inputs = new PlayerInput[3];
                inputs[seat] = Gas;
                run.StepHumans(inputs);
            }
            for (int i = 0; i < 3; i++)
            {
                var car = run.Humans[i];
                if (i == seat)
                {
                    Assert.True(car.SpeedApprox > 5 * Fx.One, $"seat {seat} must drive away on its own input");
                    Assert.True(Math.Abs(car.Pos.X - start[i].X) > Fx.One || Math.Abs(car.Pos.Z - start[i].Z) > Fx.One,
                        $"seat {seat} must move");
                }
                else
                {
                    Assert.True(car.SpeedApprox < Fx.One / 2, $"seat {i} gave no input and must stand still (speed {car.SpeedApprox})");
                    Assert.True(Math.Abs(car.Pos.X - start[i].X) < Fx.One && Math.Abs(car.Pos.Z - start[i].Z) < Fx.One,
                        $"seat {i} must not move");
                }
            }
        }
    }

    [GameFact]
    public void ListInputApi_RejectsTheWrongSeatCount()
    {
        var run = Runner();
        Assert.Throws<ArgumentException>(() => run.StepHumans([Gas, default]));
        Assert.Throws<ArgumentException>(() => run.StepHumans([Gas, default, default, default]));
    }

    [GameFact]
    public void ListSeatConstructor_RejectsDuplicateOrMissingCars()
    {
        var s = Session();
        Assert.Throws<ArgumentException>(() => new RaceRunner(s, withAi: false, humanIndices: [0, 1, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaceRunner(s, withAi: false, humanIndices: [0, 1, 8]));
    }

    [GameFact]
    public void Host_ThreeSeats_AcknowledgeAndApplyEachSeatIndependently()
    {
        var host = new RaceHost(Session(), withAi: false, countdown: false, humanIndices: [0, 1, 2]);
        Assert.Equal(3, host.Seats);
        Assert.Equal(new[] { 0, 1, 2 }, host.View.Description.LocalSeats);
        Assert.Equal(3, host.View.Latest.AppliedInput.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Submit(3, default));

        var before = host.Runner.Humans.Select(h => h.Pos).ToArray();
        long sequence = 0;
        for (int t = 0; t < 32 * 6; t++)
        {
            sequence = host.Submit(2, Gas);
            Assert.True(host.StepQueued());
        }

        var frame = host.View.Latest;
        Assert.Equal(3, frame.AppliedInput.Count);
        Assert.Equal(sequence, frame.AppliedInput[2]);   // seat 2's sample was consumed
        Assert.Equal(0, frame.AppliedInput[0]);          // seats 0/1 queued nothing
        Assert.Equal(0, frame.AppliedInput[1]);

        var cars = host.Runner.Humans;
        Assert.True(cars[2].SpeedApprox > 5 * Fx.One, "seat 2 must drive away");
        Assert.True(cars[0].SpeedApprox < Fx.One / 2 && cars[1].SpeedApprox < Fx.One / 2, "unfed seats must stand still");
        Assert.True(Math.Abs(cars[0].Pos.X - before[0].X) < Fx.One && Math.Abs(cars[1].Pos.X - before[1].X) < Fx.One);
        Assert.Equal(32 * 6, host.Steps);
    }

    [GameFact]
    public void ThreeSeatHost_UsesTheListSchedulerWithoutSilentlyDroppingTheThirdSeat()
    {
        using var host = new RaceHost(Session(), withAi: false, countdown: false, humanIndices: [0, 1, 2]);
        Assert.Throws<InvalidOperationException>(() => host.StepScheduled(default, 1));
        Assert.Throws<InvalidOperationException>(() => host.Step(default));
        Assert.Throws<InvalidOperationException>(() => host.Runner.Step(default, null));

        var inputs = new PlayerInput[] { default, default, Gas };
        for (int i = 1; i <= 64; i++)
            Assert.True(host.StepScheduled(inputs, new long[] { 0, 0, i }));
        Assert.Equal(64, host.View.Latest.AppliedInput[2]);
        Assert.Equal(0, host.View.Latest.AppliedInput[0]);
        Assert.True(host.Runner.Humans[2].SpeedApprox > 5 * Fx.One);
    }

    [GameFact]
    public void ThirdHumanReset_DoesNotClaimTheFirstSeatsResetCounter()
    {
        var run = Runner();
        Assert.True(run.ManualReset(run.Humans[2]));
        Assert.Equal(0, run.ManualResets);
        Assert.Equal(0, run.ManualResets2);
    }

    [GameFact]
    public void LegacyTwoHumanRunner_KeepsItsSeatsAndPlayer2Aliases()
    {
        var legacy = new RaceRunner(Session(), 0, withAi: true, countdown: false, secondPlayerIndex: 1);
        Assert.Equal(2, legacy.HumanSims.Count);
        Assert.Same(legacy.Player2Sim, legacy.HumanSims[1]);
        Assert.Same(legacy.Player2!.Obj, legacy.Humans[1]);
        Assert.Equal(2, legacy.Road.HumanCount);
        Assert.Same(legacy.Humans[1], legacy.Road.OtherHuman);
    }
}
