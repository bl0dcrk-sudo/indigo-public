using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Game;

/// <summary>
/// Handbrake (space) in the arcade model: PlayerInput.BtnHandbrake → PlayerDriver → c.Handbrake (+0x2DC) → ArcadeModel
/// (drive force /2, brake 0x80 with full brake force, SlipState ±4, Skid328 = 0x280000). [ref:3137]
/// </summary>
public class HandbrakeTests(ITestOutputHelper output)
{
    record Snap(int Speed, int SlipAngle, int Skid328, int SlipState, int Skid324, bool HandbrakeSeen, int Heading, int Slice);

    static RaceRunner Warm(out int tick)
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        while (!run.RaceStarted) run.Step(default);
        for (int t = 0; t < 32 * 5; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        tick = run.Tick;
        return run;
    }

    static Snap Run(bool handbrake, int steer, int gas, int ticks, out List<Snap> trace, int brake = 0)
    {
        var run = Warm(out _);
        var c = run.Car;
        trace = [];
        bool seen = false;
        for (int t = 0; t < ticks; t++)
        {
            run.Step(new PlayerInput((sbyte)steer, (byte)gas, (byte)brake, handbrake ? PlayerInput.BtnHandbrake : (byte)0));
            seen |= c.Handbrake != 0;
            trace.Add(new Snap(c.SpeedApprox, c.SlipAngle, c.Skid328, c.SlipState, c.Skid324, seen, c.Orient.M11, c.Trk.Slice));
        }
        return trace[^1];
    }

    [GameFact]
    public void Handbrake_BitReachesArcadeModel_AndSlowsTheCar()
    {
        var run = Warm(out _);
        var c = run.Car;
        int v0 = c.SpeedApprox;
        Assert.True(v0 > 15 * Fx.One, $"warm-up speed {v0 / 65536.0}");
        run.Step(new PlayerInput(0, 0, 0, PlayerInput.BtnHandbrake));
        Assert.Equal(1, c.Handbrake);
        Assert.Equal(0x280000, c.Skid328);                 // rear lock: Skid328 = 40.0 [ref:3138]
        run.Step(new PlayerInput(0, 0, 0, 0));
        Assert.Equal(0, c.Handbrake);                      // released with the key

        Run(false, 0, 0, 64, out var free);
        Run(true, 0, 0, 64, out var held);
        output.WriteLine($"coast 2 s from {v0 / 65536.0:F1} m/s: free {free[^1].Speed / 65536.0:F1}, handbrake {held[^1].Speed / 65536.0:F1}");
        Run(false, 0, 0, 64, out var brk, 255);
        output.WriteLine($"full brake: {brk[^1].Speed / 65536.0:F1} m/s");
        Assert.True(held[^1].Speed < free[^1].Speed - 2 * Fx.One, "handbrake must brake noticeably");
    }

    [GameFact]
    public void Handbrake_InTurn_SetsSlideStateAndSlip()
    {
        Run(false, 60, 255, 40, out var norm);
        Run(true, 60, 255, 40, out var hb);
        for (int i = 0; i < 40; i += 8) 
            output.WriteLine($"t{i}: norm v={norm[i].Speed / 65536.0:F1} slip={norm[i].SlipAngle / 65536.0:F3} st={norm[i].SlipState} sk={norm[i].Skid328:X}" +
                $" | hb v={hb[i].Speed / 65536.0:F1} slip={hb[i].SlipAngle / 65536.0:F3} st={hb[i].SlipState} sk={hb[i].Skid328:X}");
        Assert.Contains(hb, s => Math.Abs(s.SlipState) == 4);
        Assert.Contains(hb, s => s.Skid328 == 0x280000);
        Assert.True(Math.Abs(hb[^1].SlipAngle) > Math.Abs(norm[^1].SlipAngle) || hb[^1].Speed < norm[^1].Speed - Fx.One);
    }
}
