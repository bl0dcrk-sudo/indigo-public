using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Game;

/// <summary>Tyre marks: presentation state fed from CarObj, never feeding back. [ref:3232]</summary>
public class SkidMarkTests(ITestOutputHelper output)
{
    static (RaceRunner Run, SkidMarkTask Skids) Start(int warmSeconds = 5)
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
        var skids = new SkidMarkTask(run, SkidMarkTask.SurfaceTable());
        while (!run.RaceStarted) run.Step(default);
        for (int t = 0; t < 32 * warmSeconds; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        return (run, skids);
    }

    [GameFact]
    public void NormalDriving_LeavesNoMarks()
    {
        var (run, skids) = Start(0);
        for (int t = 0; t < 32 * 4; t++) run.Step(AutoPilot(run, 150));
        output.WriteLine($"quads {skids.Buffer.Added}, skid324 {run.Car.Skid324:X} 328 {run.Car.Skid328:X}");
        Assert.Equal(0, skids.Buffer.Added);
    }

    [GameFact]
    public void Handbrake_LeavesMarksBehindTheRearWheels()
    {
        var (run, skids) = Start();
        var c = run.Car;
        for (int t = 0; t < 40; t++) run.Step(new PlayerInput(40, 255, 0, PlayerInput.BtnHandbrake));
        int quads = skids.Buffer.Added;
        output.WriteLine($"handbrake 40 steps: quads {quads}, segments {skids.Buffer.Count}, v {c.SpeedApprox / 65536.0:F1}");
        Assert.True(quads > 5, $"quads {quads}");
        // marks lie on the road under/behind the car: within a few metres of its path, at road height
        var seg = skids.Buffer.Segments[skids.Buffer.Current];
        var v = SkidBuffer.Vertex(seg, 0, 0);
        Assert.InRange(Math.Abs(v.Y - c.PrevRoadHeight), 0, 2 * Fx.One);
    }

    [GameFact]
    public void HardBraking_LeavesMarks_ReleasedMarksStop()
    {
        var (run, skids) = Start();
        for (int t = 0; t < 48; t++) run.Step(new PlayerInput(0, 0, 255, 0));
        int braking = skids.Buffer.Added;
        output.WriteLine($"brake: quads {braking}, 324={run.Car.Skid324:X} 328={run.Car.Skid328:X}, v {run.Car.SpeedApprox / 65536.0:F1}");
        Assert.True(braking > 0, "hard braking from speed must leave marks");
        for (int t = 0; t < 64; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        int after = skids.Buffer.Added;
        for (int t = 0; t < 64; t++) run.Step(new PlayerInput(0, 255, 0, 0));
        Assert.Equal(after, skids.Buffer.Added);
    }

    [GameFact]
    public void Buffer_IsA32SegmentRing_OfAtMost16Quads()
    {
        var b = new SkidBuffer();
        // 20 segments' worth of quads in a row (each 0.5 m step), the origin moves on after 64 units or 16 quads
        for (int i = 0; i < 16 * 40; i++)
            b.Add(new Vec3(i * 0x8000, 0, 0), new Vec3((i + 1) * 0x8000, 0, 0), 1);
        Assert.Equal(32, b.Count);
        Assert.All(b.Segments, s => Assert.InRange(s.Quads, 0, 16));
        Assert.InRange(b.Current, 0, 31);
        Assert.Equal(16 * 40, b.Added);
        // the oldest segments were overwritten: the newest quad is the end of the last add
        var s = b.Segments[b.Current];
        var last = SkidBuffer.Vertex(s, s.Quads - 1, 3);
        Assert.InRange(last.X, 16 * 40 * 0x8000 - 0x100, 16 * 40 * 0x8000 + 0x100);
        // half width 0.15 m on each side of a +x track
        var l = SkidBuffer.Vertex(s, 0, 0); var r = SkidBuffer.Vertex(s, 0, 1);
        Assert.InRange(Math.Abs(l.Z - r.Z), 0x4C00, 0x4D00);
    }

    [GameFact]
    public void Marks_DoNotChangeTheSimulation()
    {
        static (int, int, int) Run(bool withMarks)
        {
            var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true);
            using var skids = withMarks ? new SkidMarkTask(run, SkidMarkTask.SurfaceTable()) : null;
            while (!run.RaceStarted) run.Step(default);
            for (int t = 0; t < 32 * 8; t++) run.Step(new PlayerInput((sbyte)(t % 64 < 32 ? 40 : -40), 255, 0, t % 100 < 50 ? PlayerInput.BtnHandbrake : (byte)0));
            return (run.Car.Pos.X, run.Car.Pos.Z, run.Car.Rpm);
        }
        Assert.Equal(Run(false), Run(true));
    }

    public static PlayerInput AutoPilot(RaceRunner run, byte gas)
    {
        var c = run.Car;
        int dir = run.Session.Backward ? -1 : 1;
        var sl = run.Session.Slices[c.Trk.Slice];
        int want = FxMath.Atan2(sl.Forward[0] * dir, sl.Forward[2] * dir);
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int err = ((want - have + 0x200) & 0x3FF) - 0x200;
        int steer = Math.Clamp(err * 2 - c.LateralOffset * dir / 0x4000, -127, 127);
        return new PlayerInput((sbyte)steer, gas, 0, 0);
    }
}
