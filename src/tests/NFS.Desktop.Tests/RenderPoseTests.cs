using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Smoothness of what is drawn at 60 FPS while the simulation runs at its fixed 32 Hz.</summary>
public class RenderPoseTests
{
    const double Frame60 = 1.0 / 60;

    /// <summary>Autopilot as analog keys: the car keeps to the road at a roughly steady speed.</summary>
    static DriveKeys Pilot(RaceDrive d)
    {
        var i = Autopilot.Input(d.Runner);
        return new DriveKeys(false, false, false, false, false, false, false, AnalogSteer: i.Steer, AnalogGas: i.Gas);
    }

    static RaceDrive Started(int frames = 600)
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        for (int i = 0; i < frames; i++) d.Frame(Frame60, Pilot(d));
        return d;
    }

    static Vector3 PlayerDrawn(RaceDrive d) => d.Scene.Cars[d.Scene.PlayerInstance].Position;

    /// <summary>Per-frame displacement of the drawn player car and the camera over <paramref name="frames"/> frames.</summary>
    static (List<float> Car, List<float> Cam, List<int> Steps) Measure(RaceDrive d, int frames)
    {
        var car = new List<float>(); var cam = new List<float>(); var steps = new List<int>();
        var pc = PlayerDrawn(d); var cc = d.Cam.Position;
        for (int i = 0; i < frames; i++)
        {
            steps.Add(d.Frame(Frame60, Pilot(d)));
            var p = PlayerDrawn(d); var c = d.Cam.Position;
            car.Add(Vector3.Distance(pc, p)); cam.Add(Vector3.Distance(cc, c));
            pc = p; cc = c;
        }
        return (car, cam, steps);
    }

    [GameDataFact]
    public void At60FpsDrawnCarAndCameraAdvanceEvenly()
    {
        var d = Started(1200);
        var (car, cam, steps) = Measure(d, 120);
        Assert.Contains(0, steps); // 32 Hz sim under 60 FPS: some frames have no sim step at all...
        Assert.Contains(1, steps);
        Assert.True(car.Average() > 0.2f, "car must be moving"); // ~>12 m/s
        Assert.All(car, x => Assert.True(x > 0, "drawn car froze for a frame"));
        Assert.True(car.Max() / car.Min() < 1.15f, $"car step min {car.Min():F4} max {car.Max():F4} ratio {car.Max() / car.Min():F3}");
        Assert.All(cam, x => Assert.True(x > 0, "camera froze for a frame"));
        Assert.True(cam.Max() / cam.Min() < 1.15f, $"camera step min {cam.Min():F4} max {cam.Max():F4} ratio {cam.Max() / cam.Min():F3}");
    }

    [GameDataFact]
    public void AllDrawnCarsMoveEvenly()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        for (int i = 0; i < 900; i++) d.Frame(Frame60, Pilot(d));
        var prev = d.Scene.Cars.Select(c => c.Position).ToArray();
        var min = Enumerable.Repeat(float.MaxValue, prev.Length).ToArray();
        var max = new float[prev.Length];
        for (int f = 0; f < 120; f++)
        {
            d.Frame(Frame60, Pilot(d));
            for (int i = 0; i < prev.Length; i++)
            {
                float s = Vector3.Distance(prev[i], d.Scene.Cars[i].Position);
                min[i] = Math.Min(min[i], s); max[i] = Math.Max(max[i], s);
                prev[i] = d.Scene.Cars[i].Position;
            }
        }
        for (int i = 0; i < prev.Length; i++)
            Assert.True(min[i] > 0 && max[i] / min[i] < 1.5f, $"car {i}: min {min[i]:F4} max {max[i]:F4}");
    }

    [GameDataFact]
    public void SimulationIsUnchangedByPresentation()
    {
        // Same sim steps => same CarObj regardless of how wall time is sliced into frames.
        var a = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        var b = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 320; i++) a.Frame(1.0 / 32, gas);
        while (b.Steps < a.Steps) b.Frame(Frame60, gas); // 1/60 s frames: never more than one step per frame
        Assert.Equal(a.Steps, b.Steps);
        Assert.Equal(a.Car.Pos, b.Car.Pos);
        Assert.Equal(a.Car.Vel, b.Car.Vel);
        Assert.Equal(a.Car.Orient, b.Car.Orient);
        Assert.True(a.Car.Vel.Z != 0 || a.Car.Vel.X != 0);
    }

    [Fact]
    public void Interpolate_AtFractionZeroAndOne_ReturnsSimulationStates()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);
        var yaw = new Mat3(0, 0, Fx.One, 0, Fx.One, 0, -Fx.One, 0, 0);
        var a = RenderPose.From(new Vec3(0, 0, 0), o);
        var b = RenderPose.From(new Vec3(2 * Fx.One, 0, 0), yaw);
        Assert.Equal(a, RenderPose.Interpolate(a, b, 0));
        Assert.Equal(b, RenderPose.Interpolate(a, b, 1));
        var mid = RenderPose.Interpolate(a, b, 0.5);
        Assert.Equal(1f, mid.Position.X, 4);
        Assert.Equal(1f, mid.Forward.Length(), 4);
        Assert.True(Vector3.Dot(mid.Forward, a.Forward) > 0.6f && Vector3.Dot(mid.Forward, b.Forward) > 0.6f);
    }

    [Fact]
    public void InterpolateBeyondNewestStatePredictsPositionAndOrientationForTheRenderInterval()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);
        var yaw = new Mat3(0, 0, Fx.One, 0, Fx.One, 0, -Fx.One, 0, 0);
        var a = RenderPose.From(new Vec3(0, 0, 0), o);
        var b = RenderPose.From(new Vec3(2 * Fx.One, 0, 0), yaw);

        var predicted = RenderPose.Interpolate(a, b, 1.5);

        Assert.Equal(3f, predicted.Position.X, 4);
        Assert.Equal(1f, predicted.Forward.Length(), 4);
        Assert.True(Vector3.Dot(predicted.Forward, b.Forward) < .99f); // yaw continues past the latest sample
    }

    [Fact]
    public void PoseHistory_DoesNotInterpolateAcrossTeleports()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);
        RenderPose At(int metres) => RenderPose.From(new Vec3(metres * Fx.One, 0, 0), o);

        var h = new PoseHistory(At(0));
        Assert.False(h.Push(At(1)));
        Assert.Equal(0.5f, h.Sample(0.5).Position.X, 3); // normal motion is blended

        Assert.True(h.Push(At(100)));                    // > 8 m in one sim step = teleport
        Assert.Equal(100f, h.Sample(0.25).Position.X, 3);
        Assert.Equal(100f, h.Sample(1.5).Position.X, 3);  // no prediction across a snapped discontinuity

        Assert.False(h.Push(At(101)));
        Assert.True(h.Push(At(50), discontinuity: true)); // explicit reset flag
        Assert.Equal(50f, h.Sample(0.5).Position.X, 3);
        Assert.Equal(50f, h.Sample(1.5).Position.X, 3);
    }

    [GameDataFact]
    public void ManualReset_PlacesDrawnCarAndCameraAtOnceWithoutBlending()
    {
        var d = Started(1200);
        var before = PlayerDrawn(d);
        d.Frame(Frame60, Pilot(d));
        int resets = d.Resets;
        d.ResetToRoad();
        Assert.True(d.Resets > resets, "reset must be accepted here");
        var after = PlayerDrawn(d);
        Assert.Equal(RaceScene.ToWorld(d.Car.Pos), after); // exactly the new sim state, no blend from the old one
        Assert.NotEqual(before, after);
        for (int i = 0; i < 3; i++)
        {
            var p0 = PlayerDrawn(d);
            d.Frame(Frame60, default);
            Assert.True(Vector3.Distance(p0, PlayerDrawn(d)) < 3f);
        }
    }

    [GameDataFact]
    public void Pause_FreezesDrawnPoseAndCamera()
    {
        var d = Started(1200);
        Assert.True(d.Pause());
        var car = PlayerDrawn(d); var cam = d.Cam.Position;
        var all = d.Scene.Cars.Select(c => c.Position).ToArray();
        for (int i = 0; i < 30; i++) d.Frame(Frame60, Pilot(d));
        Assert.Equal(car, PlayerDrawn(d));
        Assert.Equal(cam, d.Cam.Position);
        Assert.Equal(all, d.Scene.Cars.Select(c => c.Position).ToArray());
    }
}
