using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;
using NFS.Core.Tables;

namespace NFS.Tests.Game;

public class RaceRunnerAiTests(ITestOutputHelper output)
{
    /// <summary>Same simple autopilot as RaceRunnerTests / NFS.Desktop Autopilot.</summary>
    internal static PlayerInput Autopilot(RaceRunner run)
    {
        var c = run.Car;
        int dir = run.Session.Backward ? -1 : 1;
        var sl = run.Session.Slices[c.Trk.Slice];
        int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int err = ((want - have + 0x200) & 0x3FF) - 0x200;
        int steer = Math.Clamp(err * 2 - c.LateralOffset * dir / 0x4000, -127, 127);
        return new PlayerInput((sbyte)steer, 200, 0, 0);
    }

    private static (RaceRunner Run, string Trace) Simulate(int seconds)
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var trace = new System.Text.StringBuilder();
        for (int t = 0; t < 32 * seconds; t++)
        {
            run.Step(Autopilot(run));
            if ((t & 31) == 31) foreach (var car in run.Cars) trace.Append($"{car.Obj.Pos.X},{car.Obj.Pos.Z};");
        }
        return (run, trace.ToString());
    }

    [GameFact]
    public void Replay_AllCars_Drive30s_OnRoad_Deterministic()
    {
        var (run, trace) = Simulate(30);
        Assert.Equal(8, run.Cars.Count);
        int offRoad = 0;
        foreach (var car in run.Cars)
        {
            var c = car.Obj;
            // beyond 244 m from the player puts the car "on rails": partial locate, Trk.State 1 [ref:3210]
            bool on = (c.Trk.State == 2 || c.Rail == 1 && c.Trk.State == 1) &&Math.Abs(c.Pos.Y - Newton.RoadHeightAtCar(c)) < 3 * Fx.One;
            if (!on) offRoad++;
            output.WriteLine($"{car.Race.Model,-5} {(car.Ai == null ? "player" : "ai    ")} +{run.Progress(car)} slices, " +
                $"v {c.LocalVel.Z / (double)Fx.One:F1} m/s (target {(car.Ai?.TargetSpeed ?? 0) / (double)Fx.One:F1}), " +
                $"gear {c.Gear}, edge {car.EdgeHits}, cars {car.CarHits}, on road {on} (trk {c.Trk.State}, " +
                $"dy {(c.Pos.Y - Newton.RoadHeightAtCar(c)) / (double)Fx.One:F2} m, dist {c.DistToPlayer / (double)Fx.One:F0} m)");
        }
        output.WriteLine($"car–car hits {run.CarHits}");
        Assert.Equal(0, offRoad);
        foreach (var car in run.Cars.Where(x => x.Ai != null))
            Assert.True(run.Progress(car) > 150, $"{car.Race.Model} progress {run.Progress(car)}");
        foreach (var car in run.Cars) Assert.Equal(run.Progress(car), car.Distance); // < 1 lap: unwrapped = wrapped
        Assert.All(run.Cars.Skip(1), c => Assert.NotNull(c.Personality));
        Assert.Equal(trace, Simulate(30).Trace); // deterministic
    }

    [GameFact]
    public void AiAccelCurves_AreScaledByCarType_AsInOriginal()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var t = GameTables.FromConstants();
        foreach (var car in run.Cars.Skip(1))
        {
            var model = car.Race.AiModel!;
            int ct = car.Race.Setup.CarType, scale = t.AiAccelScaleOf(ct, arcade: true);
            var curve = model.AccelCurve(arcade: true);
            for (int i = 0; i < 112; i++)
                Assert.Equal(AiDriver.ScaleAccel(curve[i], scale), car.Obj.AccelCurve[i]); // LoadModel got the car type
        }
        // model .dat is the source; at least one car must actually be rescaled (every arcade factor bar model 8 is ≠ 1)
        Assert.Contains(run.Cars.Skip(1), c => c.Race.Setup.CarType != 8);
    }

    [GameFact]
    public void AiCar_SpeedApproachesTarget()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var ai = run.Cars[1];
        int best = int.MaxValue;
        for (int t = 0; t < 32 * 20; t++)
        {
            run.Step(Autopilot(run));
            if (t > 32 * 8) best = Math.Min(best, Math.Abs(ai.Ai!.TargetSpeed - ai.Obj.LocalVel.Z));
        }
        output.WriteLine($"closest |target − v| {best / (double)Fx.One:F2} m/s");
        Assert.True(best < 2 * Fx.One);
    }

    [GameFact]
    public void AiModel_IsDefault_ArcadeFallbackSwitchable()
    {
        var a = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var b = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true) { AiArcadeFallback = true };
        Assert.All(a.Cars.Skip(1), c => Assert.False(c.Ai!.UseArcadeFallback)); // <model>.dat loaded for every AI car
        while (!a.RaceStarted) { a.Step(default); b.Step(default); } // countdown: AI cars wait for the green light
        for (int t = 0; t < 32 * 5; t++) { a.Step(Autopilot(a)); b.Step(Autopilot(b)); }
        Assert.NotEqual(a.Cars[1].Obj.Pos, b.Cars[1].Obj.Pos);
        Assert.All(a.Cars.Skip(1), c => Assert.True(a.Progress(c) > 20));
        Assert.All(b.Cars.Skip(1), c => Assert.True(b.Progress(c) > 20));
    }

    [GameFact]
    public void WithoutAi_OnlyPlayerIsSimulated()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        Assert.Single(run.Cars);
        var other = run.Session.Cars[1].Obj;
        var p0 = other.Pos;
        for (int t = 0; t < 32; t++) run.Step(default);
        Assert.Equal(p0, other.Pos);
    }

    [GameFact]
    public void LaneChange_AiChangesLanes_FixedLaneWhenOff()
    {
        foreach (bool on in new[] { true, false })
        {
            var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true) { AiLaneChange = on };
            var first = run.Cars.Skip(1).Select(c => c.Ai!.LaneOffset).ToArray();
            int changes = 0;
            var last = run.Cars.Select(c => c.Ai?.TargetLane ?? -1).ToArray();
            for (int t = 0; t < 32 * 20; t++)
            {
                run.Step(Autopilot(run));
                for (int i = 1; i < run.Cars.Count; i++)
                {
                    int l = run.Cars[i].Ai!.TargetLane;
                    if (last[i] >= 0 && l != last[i]) changes++;
                    last[i] = l;
                }
            }
            output.WriteLine($"lane change {on}: {changes} changes, car hits {run.CarHits}");
            if (on) Assert.True(changes > 0);
            else Assert.Equal(first, run.Cars.Skip(1).Select(c => c.Ai!.LaneOffset).ToArray());
        }
    }
}