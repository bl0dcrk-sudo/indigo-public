using System.Numerics;
using Microsoft.Xna.Framework.Input;
using NFS.Core;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class RaceMapTests
{
    [GameDataFact]
    public void ProjectionQuantisesCarObjBeforeRotationAndKeepsPlayerCentred()
    {
        // shift right by 19, then multiply and shift right by 16,... Fractional/negative positions [ref:2884]
        // distinguish quantising absolute positions from quantising their difference.
        var pos = new Vec3(-65536, 0, 9 * 65536);
        var player = RaceScene.ToWorld(pos);
        Assert.Equal((53, 68), RaceMap.Project(player, player, 256, false, false));
        Assert.Equal((54, 66), RaceMap.Project(new Vector3(1, 0, 25), player, 256, false, false));
        Assert.Equal((51, 67), RaceMap.Project(new Vector3(1, 0, 25), player, 0, false, false));
        Assert.Equal((52, 66), RaceMap.Project(new Vector3(1, 0, 25), player, 256, false, true));
        Assert.Equal((52, 70), RaceMap.Project(new Vector3(1, 0, 25), player, 256, true, false));
        Assert.Equal((54, 70), RaceMap.Project(new Vector3(1, 0, 25), player, 256, true, true));
    }

    static RaceScene Scene(int track)
    {
        var setup = RaceInfo.Setup("replay");
        setup.Set(0x24, track);
        return new RaceScene(new RaceLoader().Load(setup));
    }

    [GameDataFact]
    public void OutlineSamplesRealSlicesAndDrawsTheirCentreLineOnThreeTracks()
    {
        foreach (int track in new[] { 4, 0, 3 })
        {
            var scene = Scene(track);
            var slices = scene.Session.Slices;
            for (int i = 0; i < 128; i++)
            {
                var slice = slices[i * slices.Length / 128];
                Assert.Equal((slice.X >> 19, slice.Z >> 19), scene.Map.Points[i]);
                if (i % 16 != 8) continue;
                var p = RaceScene.ToWorld(new Vec3(slice.X, slice.Y, slice.Z));
                // No cars over the point: put player one map pixel to the right of it.
                var fb = new FrameBuffer();
                scene.Map.Draw(fb, [new(p + new Vector3(80, 0, 0), 0, true)], 0, i * slices.Length / 128);
                var projected = RaceMap.Project(p, p + new Vector3(80, 0, 0),
                    scene.Map.Rotation(i * slices.Length / 128), false, false);
                Assert.Equal(0xE0E0E0u, fb.Get(RaceMap.X + projected.X, RaceMap.Y + projected.Y));
            }
        }
    }

    [GameDataFact]
    public void RotationFollows32ForwardVectorsWrapsAndAppliesOriginalGuard()
    {
        var scene = Scene(4);
        var slices = scene.Session.Slices;
        int index = slices.Length - 7, x = 0, z = 0;
        for (int i = 0; i < 32; i++)
        {
            x += slices[(index + i) % slices.Length].Forward[0] * 512;
            z += slices[(index + i) % slices.Length].Forward[2] * 512;
        }
        int angle = FxMath.Atan2(z >> 5, x >> 5);
        Assert.Equal(angle, scene.Map.Rotation(index, 147));
        Assert.Equal(angle >= 0 ? angle - 512 : angle + 512, scene.Map.Rotation(index, 148));
    }

    [GameDataFact]
    public void MarkersUseOriginalArtworkHotspotsClipAndPlayerDrawsLast()
    {
        var scene = Scene(4);
        // A synthetic straight makes the original's projection exactly x=53+dx/8, y=68-dz/8.
        foreach (var slice in scene.Session.Slices) { slice.Forward[0] = 0; slice.Forward[2] = 126; }
        var map = scene.Map;
        var fb = new FrameBuffer();
        var baseline = new FrameBuffer();
        MapCar player = new(Vector3.Zero, 0, true);
        map.Draw(baseline, [player], 0, 0);
        map.Draw(fb, [player, new(new Vector3(160, 0, 0), 1, false)], 0, 0);
        var dots = NFS.Data.Fsh.Load("GAMEDATA/DASHHUD/PC/MAPDOTS.FSH");
        var e = dots.Find("ms01")!;
        var img = dots.Decode(e, dots.GlobalPalette())!;
        var indices = dots.Indices(e);
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (indices[y * img.Width + x] != 255)
                {
                    int o = 4 * (y * img.Width + x);
                    Assert.Equal((uint)(img.Rgba[o] << 16 | img.Rgba[o + 1] << 8 | img.Rgba[o + 2]),
                        fb.Get(RaceMap.X + 73 - img.CenterX + x, RaceMap.Y + 68 - img.CenterY + y));
                }
        map.Draw(fb, [player, new(Vector3.Zero, 1, false), new(new Vector3(2000, 0, 0), 2, false)], 0, 0);
        Assert.Equal(baseline.Rgba, fb.Rgba); // hidden underneath player, and entirely beyond clip
        map.Draw(fb, [player, new(new Vector3(384, 0, 0), 1, false)], 0, 0); // centre x=101
        Assert.NotEqual(baseline.Sha256(), fb.Sha256()); // partially clipped, not rejected
        for (int y = 0; y < RaceMap.Height; y++)
            for (int x = 0; x < RaceMap.Width; x++)
                if (!RaceMap.Inside(x, y)) Assert.Equal(baseline.Get(RaceMap.X + x, RaceMap.Y + y), fb.Get(RaceMap.X + x, RaceMap.Y + y));
    }

    [GameDataFact]
    public void MapUsesInterpolatedPositionsOfEveryRacerAndExcludesTraffic()
    {
        var scene = Scene(4);
        var d = new RaceDrive(scene, countdown: false, withAi: true);
        var before = scene.MapCars();
        d.Step(default);
        var after = scene.Session.Cars.Select(c => RaceScene.ToWorld(c.Obj.Pos)).ToArray();
        scene.PresentPoses(0.5);
        var map = scene.MapCars();
        Assert.Equal(scene.Session.Cars.Count(c => (c.Setup.CarClass & 3) != 0), map.Length);
        for (int i = 0; i < map.Length; i++)
            Assert.True(Vector3.Distance(Vector3.Lerp(before[i].Position, after[i], .5f), map[i].Position) < .0001f);
        Assert.True(map[scene.MapPlayer].Human);
        Assert.Equal(scene.Cars[scene.PlayerInstance].Position, map[scene.MapPlayer].Position);
        var prototype = scene.Session.Cars[1];
        var traffic = prototype with { Setup = prototype.Setup with { CarClass = 4 }, Index = 8 };
        var cop = prototype with { Setup = prototype.Setup with { CarClass = 8 }, Index = 9 };
        var withTraffic = new RaceScene(scene.Session with { Cars = scene.Session.Cars.Concat([traffic, cop]).ToArray() });
        Assert.Equal(map.Length, withTraffic.MapCars().Length);
    }

    [GameDataFact]
    public void F5CyclesMapHudAndNothingOncePerPressEvenWithoutSimulationStep()
    {
        var mapping = new InputMapping();
        var pressed = mapping.Read(new KeyboardState(Keys.F5), default).Drive;
        Assert.True(pressed.ToggleHud);
        var d = new RaceDrive(Scene(4), countdown: false);
        var visible = new FrameBuffer();
        var hidden = new FrameBuffer();
        d.Render(visible);
        d.Frame(0, pressed);
        Assert.True(d.ShowHud); Assert.False(d.ShowMap);
        d.Render(hidden);
        Assert.NotEqual(visible.Get(RaceMap.X + 53, RaceMap.Y + 68), hidden.Get(RaceMap.X + 53, RaceMap.Y + 68));
        for (int y = 0; y < RaceMap.Y; y++)
            for (int x = 0; x < 640; x++) Assert.Equal(visible.Get(x, y), hidden.Get(x, y));
        d.Frame(0, pressed);
        Assert.True(d.ShowHud); Assert.False(d.ShowMap);
        d.Frame(0, default); d.Frame(0, pressed);
        Assert.False(d.ShowHud); Assert.False(d.ShowMap);
        d.Frame(0, default); d.Frame(0, pressed);
        Assert.True(d.ShowHud); Assert.True(d.ShowMap);
        Assert.Equal(0, d.Steps);
    }

    [Fact]
    public void HudconVisibilityUsesOriginalOffsetsAndDefaultsForMissingOrWrongSize()
    {
        Assert.Equal((true, true), OriginalHud.Visibility([]));
        Assert.Equal((true, true), OriginalHud.Visibility(new byte[151]));
        var bytes = new byte[152];
        Assert.Equal((false, false), OriginalHud.Visibility(bytes));
        bytes[0x0C] = 1;
        Assert.Equal((true, false), OriginalHud.Visibility(bytes));
        bytes[0x3C] = 1;
        Assert.Equal((true, true), OriginalHud.Visibility(bytes));
        bytes[0x0C] = 0;
        Assert.Equal((false, true), OriginalHud.Visibility(bytes));
    }

    [GameDataFact]
    public void RealRaceRendersPaintIdsAbove15AndMapInEveryDrivingCamera()
    {
        var d = new RaceDrive(Scene(4), countdown: false, withAi: true);
        var fb = new FrameBuffer();
        for (int i = 0; i < 4; i++)
        {
            d.Render(fb);
            Assert.NotEqual(0u, fb.Get(RaceMap.X + 53, RaceMap.Y + 68));
            d.Frame(0, default);
            d.Frame(0, default(DriveKeys) with { CycleCamera = true });
        }
    }
}
