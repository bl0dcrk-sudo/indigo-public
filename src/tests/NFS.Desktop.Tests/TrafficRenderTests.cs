using System.Numerics;
using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Traffic in the race view: parked (hidden) cars are not drawn (needs +0x8D), traffic [ref:3000]
/// is not on the minimap (only classes 1 and 2), active traffic is drawn. NFS_TRAFFIC_REVIEW_DIR writes review PNGs.</summary>
public class TrafficRenderTests
{
    static RaceScene Scene(int menuTrack, bool backward = false)
    {
        var dir = GameFiles.FindGameDir();
        var cfg = FeConfig.Load(dir);
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.OpponentCar = 0; cfg.TrafficOff = 0;
        cfg.Track = (ushort)menuTrack; cfg.Style = 2; cfg.Backward = (byte)(backward ? 1 : 0); cfg.Mirrored = 0;
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        return new RaceScene(new RaceLoader(dir).Load(setup), dir);
    }

    [GameDataFact]
    public void ParkedTrafficIsNotDrawn_ActiveTrafficIs_AndTheMapShowsOnlyRacers()
    {
        var scene = Scene(2);
        var drive = new RaceDrive(scene, countdown: true, withAi: true, automatic: true);
        var traffic = drive.Runner.TrafficCars;
        Assert.Equal(4, traffic.Count);
        int racers = scene.Session.Cars.Count(c => (c.Setup.CarClass & 3) != 0);
        Assert.Equal(2, racers);
        Assert.Equal(racers, scene.RenderCars.Count() - scene.Smackables.Count);        // the four parked cars are skipped
        Assert.Equal(racers, scene.MapCars().Length);
        for (int s = 0; s < 120 && traffic.All(t => (t.Obj.AiFlags & AiTraffic.FlagParked) != 0); s++) drive.RunAutopilot(1);
        int active = traffic.Count(t => (t.Obj.AiFlags & AiTraffic.FlagParked) == 0);
        Assert.True(active > 0, "a traffic car spawned");
        Assert.Equal(racers + active, scene.RenderCars.Count() - scene.Smackables.Count);
        Assert.Equal(racers + active - 1, scene.RenderCarsExceptPlayer.Count() - scene.Smackables.Count);
        Assert.Equal(racers, scene.MapCars().Length);                                    // still racers only (classes 4/8 are not on the map)
    }

    [GameDataFact]
    public void Review_TrafficFrames()
    {
        if (Environment.GetEnvironmentVariable("NFS_TRAFFIC_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var (menuTrack, name, frames) in new[] { (2, "tr04", 3), (3, "tr05", 2), (1, "tr02", 1) })
        {
            var scene = Scene(menuTrack);
            var drive = new RaceDrive(scene, countdown: true, withAi: true, automatic: true);
            int saved = 0, lastSaved = -100;
            for (int s = 0; s < 400 && saved < frames; s++)
            {
                drive.RunAutopilot(1);
                if (s - lastSaved < 8) continue;
                var p = drive.Car;
                foreach (var t in drive.Runner.TrafficCars.Where(t => t.Obj.Active != 0 && (t.Obj.AiFlags & AiTraffic.FlagParked) == 0))
                {
                    var d = RaceScene.ToWorld(t.Obj.Pos) - RaceScene.ToWorld(p.Pos);
                    var fwd = RaceScene.ToWorld(new Vec3(p.Orient.M20, p.Orient.M21, p.Orient.M22));
                    float ahead = Vector3.Dot(d, fwd), side = Math.Abs(Vector3.Dot(d, RaceScene.ToWorld(p.Orient.Row0)));
                    if (ahead is < 12 or > 55 || side > 8) continue;
                    var fb = new FrameBuffer();
                    drive.Render(fb);
                    fb.SavePng(Path.Combine(dir, $"{name}_traffic_{saved + 1}.png"));
                    saved++; lastSaved = s;
                    break;
                }
            }
            Assert.True(saved > 0, $"{name}: no frame with traffic ahead of the player");
        }
    }

}

/// <summary>Palette variants of traffic cars: registers three palettes (colour c, and the two [ref:3001]
/// derived ones) for types 0x11 &lt; t &lt; 0x28, advances +0x634 at every spawn. [ref:3002]</summary>
public class TrafficSkinTests
{
    [Fact]
    public void PaintVariants_FollowTheLoaderFormula()
    {
        Assert.Equal([5, ((5 + 31) * 2) % 0x49 + 0xF, ((31 / 2 + 5 / 2) * 2 + 1) % 0x49 + 0xF], CarModel.PaintVariants(31, 5));
        foreach (int c in Enumerable.Range(0, CarModel.MaxPaint + 1))
            foreach (int t in new[] { 18, 24, 31, 35 })
                Assert.All(CarModel.PaintVariants(t, c), p => Assert.InRange(p, 0, CarModel.MaxPaint));
        Assert.Equal([7], CarModel.PaintVariants(0x24, 7));   // pjagr and all racers: one palette
        Assert.Equal([7], CarModel.PaintVariants(3, 7));
        Assert.Equal([7], CarModel.PaintVariants(0x10, 7));
        Assert.Equal(3, CarModel.PaintVariants(0x11, 7).Length);
        Assert.Equal([7], CarModel.PaintVariants(0x28, 7));
    }

    [GameDataFact]
    public void TrafficCar_ShowsAnotherPaletteAtEachSpawn()
    {
        var dir = GameFiles.FindGameDir();
        var cfg = FeConfig.Load(dir);
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.OpponentCar = 0; cfg.TrafficOff = 0;
        cfg.Track = 2; cfg.Style = 2; cfg.Backward = 0; cfg.Mirrored = 0;
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        var scene = new RaceScene(new RaceLoader(dir).Load(setup), dir);
        var drive = new RaceDrive(scene, countdown: true, withAi: true, automatic: true);
        int idx = scene.Session.Cars.ToList().FindIndex(c => (c.Obj.Flags & CarFlags.Traffic) != 0);
        var car = scene.Session.Cars[idx];
        Assert.Equal(0, car.Obj.TrafficSkin);
        var shown = new List<CarModel>();
        for (int k = 0; k < 3; k++)
        {
            AiTraffic.Spawn(car.Obj, drive.Runner.Traffic!);
            Assert.Equal((k + 1) % 3, car.Obj.TrafficSkin);
            drive.SnapToHost();   // stage 5a.2: the skin index comes from the published frame
            shown.Add(scene.Cars[idx].Model);
        }
        Assert.Equal(3, shown.Distinct().Count());
        // the variants differ in the body palette: the body textures of two variants are not the same picture
        var a = shown[0]; var b = shown[1];
        Assert.Contains(a.Geo.Parts.SelectMany(p => p.Polygons).Select(q => q.Texture).Distinct(), n => a.TextureFor(n) is { } ta && b.TextureFor(n) is { } tb && !ta.Rgb.SequenceEqual(tb.Rgb));
    }
}
