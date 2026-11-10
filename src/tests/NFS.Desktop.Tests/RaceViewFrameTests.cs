using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Session;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Stage 5a.2: the Desktop presentation reads the published frames, not the live host.</summary>
public class RaceViewFrameTests
{
    static RaceDrive Started()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 400; i++) d.Frame(1.0 / 60, gas);
        return d;
    }

    [GameDataFact]
    public void Presentation_IgnoresUnpublishedHostChanges_UntilTheHostPublishes()
    {
        var d = Started();
        d.Frame(0, default);
        var before = new FrameBuffer(); d.Render(before);
        var cam = d.Cam.Position;
        var car = d.Car;
        car.Pos = new Vec3(car.Pos.X + 120 * Fx.One, car.Pos.Y, car.Pos.Z);   // edits of the live host, no step
        car.Rpm = 7000; car.Gear = 5; car.RaceState = 2;
        foreach (var c in d.Runner.Cars) c.Obj.CollisionCooldown = 0x30;
        d.Frame(0, default);
        var same = new FrameBuffer(); d.Render(same);
        Assert.Equal(before.Rgba, same.Rgba);
        Assert.Equal(cam, d.Cam.Position);
        Assert.NotEqual(7000, d.PlayerView.Rpm);
        Assert.NotEqual(2, d.PlayerView.RaceState);
        d.SnapToHost();                                                        // now the host publishes its state
        Assert.Equal(car.Pos, d.PlayerView.Pos);
        Assert.Equal(2, d.PlayerView.RaceState);
        var after = new FrameBuffer(); d.Render(after);
        Assert.NotEqual(before.Rgba, after.Rgba);
    }

    [GameDataFact]
    public void EveryStepFrameIsPresentedOnceInOrder()
    {
        var d = Started();
        var seen = new List<RaceViewFrame>();
        d.FramePresented += (f, _) => seen.Add(f);
        long first = d.View.SnapshotSequence;
        int n = d.Frame(3.0 / 32 + 1e-6, default);                              // three catch-up steps in one render frame
        Assert.Equal(3, n);
        Assert.Equal([first + 1, first + 2, first + 3], seen.Select(f => f.SnapshotSequence));
        Assert.All(seen, f => Assert.Equal(RaceViewFrameKind.Step, f.Kind));
        Assert.Equal(seen[^1], d.View);
        Assert.Equal(d.Runner.Tick, d.View.SimulationTick64);
    }

    [Fact]
    public void PoseHistory_SnapsOnEpochChangeEvenForSmallMoves()
    {
        var o = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);
        RenderPose At(int metres) => RenderPose.From(new Vec3(metres * Fx.One, 0, 0), o);
        var h = new PoseHistory(At(0));
        h.Snap(At(0), epoch: 4);
        Assert.False(h.Push(At(2), epoch: 4));
        Assert.Equal(1f, h.Sample(0.5).Position.X, 3);                         // same epoch: blended
        Assert.True(h.Push(At(3), epoch: 5));                                   // reset 1 m away: not interpolated across
        Assert.Equal(3f, h.Sample(0.5).Position.X, 3);
        Assert.False(h.Push(At(4), epoch: 5));
        Assert.Equal(new Vector3(3.5f, 0, 0), h.Sample(0.5).Position);
    }
}
