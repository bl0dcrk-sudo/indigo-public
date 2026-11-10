using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Race;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>
/// Effect particles against the cars of the real scene (inserts the sprites into the same z-sorted list as the cars [ref:2854]
/// "Particles (sfx)"): a sprite behind a car does not cover it, one in front does, the rear-view mirror gets them too. [ref:2855]
/// PNG (before = sprites over the finished scene as before, after = the game's frame): out/gfx_check/particles_z.
/// </summary>
public class ParticleZSortTests
{
    static string OutDir()
    {
        if (Environment.GetEnvironmentVariable("NFS_PARTICLES_PNG_DIR") is { Length: > 0 } custom) { Directory.CreateDirectory(custom); return custom; }
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, Path.Combine("src", "NFS2SE.sln")))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "out", "gfx_check", "particles_z");
        Directory.CreateDirectory(path);
        return path;
    }

    static Vec3 Fx3(Vector3 v) => new((int)(v.X * 65536), (int)(v.Y * 65536), (int)(v.Z * 65536));

    static int Diff(FrameBuffer a, FrameBuffer b)
    {
        int n = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
            if (a.Rgba[i] != b.Rgba[i] || a.Rgba[i + 1] != b.Rgba[i + 1] || a.Rgba[i + 2] != b.Rgba[i + 2]) n++;
        return n;
    }

    /// <summary>Chase view; returns the camera and the car nearest to it (the player's own).</summary>
    static (RaceDrive Drive, Camera Camera, CarInstance Car, float Z) ChaseScene()
    {
        var d = new RaceDrive(new RaceScene("replay"), countdown: false, automatic: true);
        for (int i = 0; i < 40; i++) d.Step(Autopilot.Input(d.Runner, 255));
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        var cam = d.Camera();
        var cars = new List<CarInstance>();
        d.Scene.FillRenderCars(cars, d.View.SimulationTick64, false, -1);
        var car = cars.Where(c => cam.ToView(c.Position).Z > 2).OrderBy(c => cam.ToView(c.Position).Z).First();
        return (d, cam, car, cam.ToView(car.Position).Z);
    }

    /// <summary>Puts a dust cloud on the camera → car ray, at view depth <paramref name="z"/>.</summary>
    static void Put(RaceDrive d, Camera cam, CarInstance car, float z)
    {
        var system = d.Host.View.Particles!.Particles;
        system.Reset(d.Runner.Tick);
        var target = car.Position + car.Up * 0.5f;
        var ray = target - cam.Position;
        float k = z / cam.ToView(target).Z;
        Assert.True(system.Spawn(Fx3(cam.Position + ray * k), 6, default) >= 0);
        d.SnapToHost();
    }

    [GameDataFact]
    public void CloudBehindTheCar_DoesNotCoverIt_CloudInFrontOfItDoes()
    {
        var (d, cam, car, zc) = ChaseScene();
        var off = new FrameBuffer(); var behind = new FrameBuffer(); var front = new FrameBuffer();
        d.ShowParticles = false; d.Render(off);
        d.ShowParticles = true;
        Put(d, cam, car, zc + 5f); d.Render(behind);
        Put(d, cam, car, zc - 2.5f); d.Render(front);
        int dBehind = Diff(off, behind), dFront = Diff(off, front);
        Assert.True(dFront > 1500, $"a cloud in front of the car is drawn over it ({dFront} px)");
        Assert.True(dBehind < dFront / 3, $"a cloud behind the car is mostly hidden by it ({dBehind} px vs {dFront} px)");
        off.SavePng(Path.Combine(OutDir(), "chase_off.png"));
        behind.SavePng(Path.Combine(OutDir(), "chase_behind_after.png"));
        front.SavePng(Path.Combine(OutDir(), "chase_front_after.png"));
        // before: the same cloud drawn over the finished scene, regardless of the car
        Put(d, cam, car, zc + 5f);
        var before = new FrameBuffer(); d.ShowParticles = false; d.Render(before);
        new ParticleRenderer().Draw(before, cam, d.View.Particles!.ToArray(), new ParticleSprites(d.Scene.Session.TrackNumber));
        before.SavePng(Path.Combine(OutDir(), "chase_behind_before.png"));
        Assert.True(Diff(off, before) > dBehind * 2, "before the change the cloud covered the car");
    }

    [GameDataFact]
    public void EveryPixelOfTheCarSurvives_WhenTheCloudIsBehindIt()
    {
        var (d, cam, car, zc) = ChaseScene();
        var off = new FrameBuffer(); var on = new FrameBuffer(); var without = new FrameBuffer();
        d.ShowParticles = false; d.Render(off);
        d.ShowParticles = true; Put(d, cam, car, zc + 4f); d.Render(on);
        // car pixels = what changes when the car is left out of the scene
        var opt = new RenderOptions();
        var others = new List<CarInstance>();
        d.Scene.FillRenderCars(others, d.View.SimulationTick64, false, -1);
        others.RemoveAll(c => ReferenceEquals(c, car));
        d.Scene.Track.Render(without, cam, opt, others, d.View.Knocked, d.View.SimulationTick64, d.View.Skids,
            targetSlice: d.WatchedView.Slice, targetWrongWayTicks: d.WatchedView.WrongWayTicks);
        int carPixels = 0, damaged = 0;
        for (int i = 0; i < off.Rgba.Length; i += 4)
        {
            bool isCar = off.Rgba[i] != without.Rgba[i] || off.Rgba[i + 1] != without.Rgba[i + 1] || off.Rgba[i + 2] != without.Rgba[i + 2];
            if (!isCar) continue;
            carPixels++;
            if (off.Rgba[i] != on.Rgba[i] || off.Rgba[i + 1] != on.Rgba[i + 1] || off.Rgba[i + 2] != on.Rgba[i + 2]) damaged++;
        }
        Assert.True(carPixels > 3000, $"the car covers {carPixels} px");
        Assert.Equal(0, damaged);
    }

    [GameDataFact]
    public void RearViewMirror_ShowsTheParticles()
    {
        var setup = RaceInfo.Setup("replay"); setup.Set(0x24, 2); setup.Set(0x78, 2);
        var d = new RaceDrive(new RaceScene(new RaceLoader().Load(setup)), countdown: false, automatic: true);
        while (d.Cam.Mode != 0) d.Cam.Cycle(d.Scene.PresentPoses(1), d.Car);
        if (!d.Scene.Cockpit!.MirrorEnabled) d.Scene.Cockpit.ToggleMirror();
        for (int i = 0; i < 40; i++) d.Step(Autopilot.Input(d.Runner, 255));
        d.SnapToHost();
        d.Cam.Snap(RenderPose.From(d.Car.Pos, d.Car.Orient), d.Car);
        Assert.True(d.ShowMirror);
        var player = d.Scene.Cars[d.Scene.PlayerInstance];
        var system = d.Host.View.Particles!.Particles;
        system.Reset(d.Runner.Tick);
        // a cloud cluster straight behind the car, 6 to 14 m
        for (int i = 0; i < 6; i++)
            Assert.True(system.Spawn(Fx3(player.Position - player.Forward * (6 + i * 1.6f) + player.Right * ((i % 3 - 1) * 1.2f) + player.Up * 0.7f), 6, default) >= 0);
        d.SnapToHost();
        var off = new FrameBuffer(); var on = new FrameBuffer();
        d.ShowParticles = false; d.Render(off);
        d.ShowParticles = true; d.Render(on);
        int changed = Diff(off, on);
        Assert.True(changed > 200, $"the mirror strip shows the cloud ({changed} px)");
        off.SavePng(Path.Combine(OutDir(), "mirror_off.png"));
        on.SavePng(Path.Combine(OutDir(), "mirror_on.png"));
    }

    [GameDataFact]
    public void CarSilhouettePass_MatchesTheCarInTheFullScene_AndIsCheap()
    {
        var (d, cam, car, _) = ChaseScene();
        var opt = new RenderOptions();
        var all = new List<CarInstance>();
        d.Scene.FillRenderCars(all, d.View.SimulationTick64, false, -1);
        var full = new FrameBuffer(); var without = new FrameBuffer(); var sil = new FrameBuffer();
        d.Scene.Track.Render(full, cam, opt, all, d.View.Knocked, d.View.SimulationTick64, d.View.Skids,
            targetSlice: d.WatchedView.Slice, targetWrongWayTicks: d.WatchedView.WrongWayTicks);
        var others = all.Where(c => !ReferenceEquals(c, car)).ToList();
        d.Scene.Track.Render(without, cam, opt, others, d.View.Knocked, d.View.SimulationTick64, d.View.Skids,
            targetSlice: d.WatchedView.Slice, targetWrongWayTicks: d.WatchedView.WrongWayTicks);
        var one = new List<CarInstance> { car };
        d.Scene.Track.RenderCarsOnly(sil, cam, opt, one);
        int carPixels = 0, covered = 0, extra = 0;
        for (int i = 0; i < full.Rgba.Length; i += 4)
        {
            bool isCar = full.Rgba[i] != without.Rgba[i] || full.Rgba[i + 1] != without.Rgba[i + 1] || full.Rgba[i + 2] != without.Rgba[i + 2];
            bool inSil = sil.Rgba[i + 3] != 0;
            if (isCar) { carPixels++; if (inSil) covered++; }
            else if (inSil) extra++;
        }
        Assert.True(carPixels > 3000);
        Assert.True(covered >= carPixels * 0.99, $"silhouette covers {covered} of {carPixels} car pixels");
        Assert.True(extra < carPixels * 0.05, $"{extra} px outside the car");
        // cost and allocations of the silhouette pass versus the whole scene
        for (int i = 0; i < 3; i++) { d.Scene.Track.RenderCarsOnly(sil, cam, opt, one); }
        long alloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) d.Scene.Track.RenderCarsOnly(sil, cam, opt, one);
        double silMs = sw.Elapsed.TotalMilliseconds / 20;
        alloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
        sw.Restart();
        for (int i = 0; i < 20; i++)
            d.Scene.Track.Render(full, cam, opt, all, d.View.Knocked, d.View.SimulationTick64, d.View.Skids,
                targetSlice: d.WatchedView.Slice, targetWrongWayTicks: d.WatchedView.WrongWayTicks);
        double fullMs = sw.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"silhouette {silMs:F2} ms, whole scene {fullMs:F2} ms, allocated {alloc} B");
        Assert.Equal(0, alloc);
        Assert.True(silMs < fullMs / 3, $"silhouette {silMs:F2} ms vs scene {fullMs:F2} ms");
    }
}
