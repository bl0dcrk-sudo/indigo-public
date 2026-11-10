using System.Globalization;
using System.Text;
using NFS.Core;

namespace NFS.Game.Race;

/// <summary>
/// Plays back a whole replay: player from <see cref="ReplayInputSource"/> (sample index = race tick / 2; the recording starts [ref:2350]
/// at race tick 0, the runner at <see cref="RaceRunner.StartTick"/>), AI cars by <see cref="RaceRunner"/>. Writes a CSV
/// row every 32 sim ticks (1 s). Reset requests (button 0x40) are only counted. [ref:2351]
/// </summary>
public sealed class ReplayRun(RaceRunner runner, ReplayInputSource src)
{
    public const string CsvHeader = "tick,slice,x,y,z,speed_ms,gear,lap,distance,wall_hits,edge_hits,car_hits";
    /// <summary>Extra columns of the diagnostic rows: recorded steering input, the filtered steering the arcade model
    /// sees (car +0x364), the lateral offset from the slice centre (car +0x3AC, + = right of it), the road half width
    /// on that side (slice +0x20/+0x21, 0.5 m), slip state (+0x2F4), airborne ticks (+0x14C), throttle (+0x0F8),
    /// and the yaw target +0x30C (the drift branch needs |+0x2F4| = 3 [ref:2352]
    /// and |+0x30C| &gt; 0x40; forces ±3 once |+0x30C| ≥ 0x51E). [ref:2353]</summary>
    public const string DiagHeader = ",steer,steer_c,lat,w,slip,air,thr,rc,yaw";
    public RaceRunner Runner { get; } = runner;
    public ReplayInputSource Source { get; } = src;
    public int Resets { get; private set; }
    public int Steps { get; private set; }
    /// <summary>First tick (64 Hz) at which the player's distance stopped growing for 5 s while gas was held, or -1.</summary>
    public int StallTick { get; private set; } = -1;

    /// <summary>Runs until the header length or the race end (+ <paramref name="maxSteps"/> limit); returns the CSV.</summary>
    public string Run(int maxSteps = int.MaxValue)
    {
        var sb = new StringBuilder(CsvHeader).Append(DiagHeader).Append('\n');
        var c = Runner.Car;
        if (CountdownGears && Runner.Tick == RaceRunner.StartTick && Steps == 0) Countdown();
        int bestDist = int.MinValue, bestAt = Runner.Tick;
        while (Runner.Tick < Source.RaceTicks64 && Steps < maxSteps && !Runner.Laps.RaceOver)
        {
            var input = Source.ForRaceTick(Runner.Tick);
            _input = input;
            if ((input.Buttons & PlayerInput.BtnReset) != 0) Resets++;
            Runner.Step(input);
            Steps++;
            int d = Runner.PlayerSim.Distance;
            if (d > bestDist) { bestDist = d; bestAt = Runner.Tick; }
            else if (StallTick < 0 && Runner.Tick - bestAt > 5 * 64 && Runner.Tick > RaceRunner.StartTick + 10 * 64) StallTick = bestAt;
            if (Steps % RowEvery == 0) sb.Append(Row());
        }
        sb.Append(Row());
        return sb.ToString();

        string Row()
        {
            var inv = CultureInfo.InvariantCulture;
            double m(int v) => v / (double)Fx.One;
            var sl = Runner.Session.Slices[Math.Clamp(c.Trk.Slice, 0, Runner.Session.Slices.Length - 1)];
            int w = c.LateralOffset < 0 ? sl.HalfWidthLeft : sl.HalfWidthRight;
            return string.Create(inv, $"{Runner.Tick},{c.Trk.Slice},{m(c.Pos.X):F3},{m(c.Pos.Y):F3},{m(c.Pos.Z):F3}," +
                $"{m(c.SpeedApprox):F2},{c.Gear},{Runner.Laps.Cars[0].LapsDone},{Runner.PlayerSim.Distance}," +
                $"{Runner.WallHits},{Runner.EdgeHits},{Runner.PlayerSim.CarHits}," +
                $"{_input.Steer},{c.Steer},{m(c.LateralOffset):F2},{w * 0.5:F1},{c.SlipState},{c.AirTicks}," +
                $"{Runner.PlayerSim.Env.Throttle >> 8},{Runner.PlayerSim.Env.RoadCurveAhead},{c.YawTarget}\n");
        }
    }

    private PlayerInput _input;

    /// <summary>Gear presses during the countdown (samples 0..0xFF, car held on the grid): the runner starts at
    /// tick 0x200, but the driver task already runs and the REPLAY.RPY player shifts N→1→2 at samples 78/106 [ref:2354]
    /// (manual gearbox). Only the gear is applied (approximation: the car itself is not simulated before the start).</summary>
    public int Countdown()
    {
        var c = Runner.Car;
        int shifts = 0;
        for (int t = 0; t < RaceRunner.StartTick / 2; t++)
        {
            var b = Source.At(t).Buttons;
            byte g = Runner.Automatic ? PlayerDriver.GearAuto(c.Gear, b) : PlayerDriver.GearManual(c.Gear, Runner.Controls.NumGears, b);
            if (g != c.Gear) { c.Gear = g; c.RequestedGear = g; shifts++; }
        }
        CountdownShifts = shifts;
        return shifts;
    }

    public int CountdownShifts { get; private set; }
    /// <summary>Apply gear presses of the countdown (off by default: with them the player is two gears too high – the
    /// recorded shift pattern 1st@0.3 s, 2nd@4.6 s, 3rd@7.7 s, 4th@11.7 s, 5th@16.9 s only makes sense from neutral,
    /// so the original apparently ignores or resets them before the start [hypothesis]).</summary>
    public bool CountdownGears { get; init; }

    /// <summary>CSV row every N sim ticks (1 s by default; a smaller value gives a finer divergence trace).</summary>
    public int RowEvery { get; init; } = 32;

    public string Summary()
    {
        var lap = Runner.Laps.Cars[0];
        int n = Runner.Session.Slices.Length;
        return string.Create(CultureInfo.InvariantCulture,
            $"replay: {Steps} steps, tick {Runner.Tick}/{Source.RaceTicks64} (header), samples {Source.Length()}; " +
            $"player laps {lap.LapsDone}/{Runner.Laps.NumLaps}, finished {(lap.FinishTick > 0 ? "yes" : "no")}, " +
            $"distance {Runner.PlayerSim.Distance} slices ({Runner.PlayerSim.Distance / (double)n:F2} laps of {n}), " +
            $"race time {(Runner.Tick - RaceRunner.StartTick) / 64.0:F1} s, wall {Runner.WallHits}, edge {Runner.EdgeHits}, " +
            $"car hits {Runner.PlayerSim.CarHits}, resets {Resets}, stall tick {StallTick}, place {Runner.Laps.PlaceOf(0)}/{Runner.Cars.Count}");
    }
}
