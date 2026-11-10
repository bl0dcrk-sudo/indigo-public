using NFS.Game.Race;
using NFS.Core;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>
/// WRONG WAY band end to end: the player car turns around and drives against the race direction [ref:3005]
/// counts the ticks it spends that way and shows hud5 → hud6 ("WRONG WAY!") → hud7. [ref:3006]
/// The frames are also written to out/scratch/wrongway/ as visual evidence. (The file still has its working name.)
/// </summary>
public class WrongWayBandTests
{
    static readonly PlayerInput Turn = new(-127, 255, 0, 0);   // full lock + throttle: spins the car around
    static readonly PlayerInput Straight = new(0, 255, 0, 0);  // once it points against the race direction

    [GameDataFact]
    public void DrivingTheWrongWayRaisesTheHazardBandThroughTheRaceStep()
    {
        var d = new RaceDrive(new RaceScene("replay"), withAi: true, countdown: false, automatic: true);
        string dir = Path.Combine(ProjectRoot(), "out", "scratch", "wrongway");
        Directory.CreateDirectory(dir);
        var fb = new FrameBuffer();
        int first = 0, second = 0;
        for (int i = 0; i < 32 * 30 && WrongWayCounter.Band(d.Car.WrongWayTicks) < 2; i++)
        {
            var slice = d.Scene.Session.Slices[d.Car.Trk.Slice];
            var f = d.Car.Forward;
            int dot = Fx.Mul(f.X, slice.Forward[0]) + Fx.Mul(f.Y, slice.Forward[1]) + Fx.Mul(f.Z, slice.Forward[2]);
            bool against = d.Scene.Session.Backward ? dot > 100 : dot < -100;
            d.Step(against ? Straight : Turn);
            if (WrongWayCounter.Band(d.Car.WrongWayTicks) == 1 && first == 0)
            {
                first = d.Runner.Tick;
                Snap(d, fb);
                fb.SavePng(Path.Combine(dir, "wrongway_band1.png"));
                Assert.Equal(0x40, d.Car.WrongWayTicks);
                Assert.Equal(1, d.OriginalHudValues().Band); // the HUD reads the counter, not the other way round
            }
            if (WrongWayCounter.Band(d.Car.WrongWayTicks) == 2 && second == 0) second = d.Runner.Tick;
        }
        Assert.True(first > 0, $"band 1 not reached ({d.Summary()})");
        Assert.True(second > 0, $"band 2 not reached ({d.Summary()})");
        Assert.Equal(0x94, d.Car.WrongWayTicks);
        Snap(d, fb);
        fb.SavePng(Path.Combine(dir, "wrongway_band2.png"));
    }

    static void Snap(RaceDrive d, FrameBuffer fb)
    {
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        d.Render(fb);
    }

    static string ProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NFS2SE.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("NFS2SE.sln");
    }
}
