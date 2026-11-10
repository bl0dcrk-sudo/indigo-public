using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class SmackableRenderTests
{
    static RaceScene Scene(int track = 4, int? limit = null)
    {
        var setup = RaceInfo.Setup("replay");
        setup.Set(0x24, track);
        setup.Set(0x4F8, limit ?? int.MaxValue);
        return new RaceScene(new RaceLoader().Load(setup));
    }

    [GameDataFact]
    public void EveryTrackCreatesAnInstanceForEachEnabledSmackable()
    {
        foreach (int track in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var scene = Scene(track);
            var records = scene.Session.Smackables!.Cars.Take(scene.Session.Setup.NumSmackables).ToArray();
            Assert.Equal(scene.Session.Smackables.Cars.Count, records.Length);
            Assert.Equal(records.Length, scene.Smackables.Count);
            Assert.Equal(scene.Cars.Count + records.Length, scene.RenderCars.Count());
            var loader = new RaceLoader();
            for (int i = 0; i < records.Length; i++)
            {
                var render = scene.Smackables[i];
                Assert.Equal(loader.ModelName(records[i].Model), render.Model.Name, ignoreCase: true);
                Assert.Equal(RaceScene.ToWorld(scene.Session.Parked![i].Pos), render.Position);
                Assert.Null(render.Anim);
                Assert.NotEmpty(render.Polys());
            }
        }
        Assert.Single(Scene(limit: 1).Smackables);
        Assert.Empty(Scene(limit: 0).Smackables);
    }

    [GameDataFact]
    public void PublishedSmackablePoseFollowsTranslationRotationAndCatchupSteps()
    {
        var scene = Scene();
        using var host = new RaceHost(scene.Session);   // stage 5a.2: the scene reads published frames, not the parked CarObj
        var obj = scene.Session.Parked![2]; // RUIN
        var initial = RenderPose.From(obj.Pos, obj.Orient);
        for (int step = 1; step <= 3; step++)
        {
            obj.Pos = new Vec3(obj.Pos.X + Fx.One, obj.Pos.Y, obj.Pos.Z);
            obj.Orient = FxMath.RotY(0x4000 + step * 0x800);
            scene.CapturePoses(host.View.Publish(RaceViewFrameKind.Step, step));
        }
        var current = RenderPose.From(obj.Pos, obj.Orient);
        var previous = RenderPose.From(new Vec3(obj.Pos.X - Fx.One, obj.Pos.Y, obj.Pos.Z), FxMath.RotY(0x5000));
        scene.PresentPoses(0.25);
        var expected = RenderPose.Interpolate(previous, current, 0.25);
        Assert.Equal(expected.Position, scene.Smackables[2].Position);
        Assert.Equal(expected.Forward, scene.Smackables[2].Forward);
        Assert.NotEqual(initial.Position, scene.Smackables[2].Position);
        Assert.Equal(current.Position, RaceScene.ToWorld(obj.Pos)); // presentation does not write CarObj
        scene.SyncAllPoses();
        Assert.Equal(current.Position, scene.Smackables[2].Position);
        Assert.Equal(current.Forward, scene.Smackables[2].Forward);
        scene.SnapPoses(host.View.Publish(RaceViewFrameKind.Resync, 3));
        scene.PresentPoses(0);
        Assert.Equal(current.Position, scene.Smackables[2].Position);
        Assert.Equal(current.Forward, scene.Smackables[2].Forward);
        obj.Pos = new Vec3(obj.Pos.X + 50 * Fx.One, obj.Pos.Y, obj.Pos.Z);   // unpublished host change: not visible
        scene.PresentPoses(0);
        Assert.Equal(current.Position, scene.Smackables[2].Position);
    }

    [GameDataFact]
    public void RuinBeerAndCartAreVisibleFromThePlayerCamera()
    {
        var scene = Scene();
        var pictures = new List<FrameBuffer>();
        foreach (var name in new[] { "RUIN", "BEER", "CART" })
        {
            var target = scene.Smackables.Single(c => c.Model.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var obj = scene.Session.Parked![scene.Smackables.ToList().IndexOf(target)];
            if (name == "RUIN") Assert.Equal(1505, obj.Trk.Slice);
            var player = RaceScene.PlayerCar(scene.Session.Cars)!.Obj;
            // Eye height / chase distance are the same as PlayerCamera; park the player behind the object.
            player.Orient = Mat3.Identity;
            player.Pos = new Vec3(obj.Pos.X - 3 * Fx.One, obj.Pos.Y, obj.Pos.Z - 6 * Fx.One);
            scene.SyncAllPoses();
            var camera = scene.PlayerCamera();
            var without = new FrameBuffer();
            var with = new FrameBuffer();
            scene.Track.Render(without, camera, new RenderOptions(), scene.RenderCars.Where(c => c.Model.Name != target.Model.Name));
            scene.Render(with);
            int changed = Enumerable.Range(0, with.Width * with.Height)
                .Count(i => !with.Rgba.AsSpan(i * 4, 3).SequenceEqual(without.Rgba.AsSpan(i * 4, 3)));
            pictures.Add(with);
            if (Environment.GetEnvironmentVariable("NFS_SMACKABLE_REVIEW_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                with.SavePng(Path.Combine(dir, name.ToLowerInvariant() + ".png"));
                without.SavePng(Path.Combine(dir, name.ToLowerInvariant() + "_without.png"));
            }
            Assert.True(changed > 50, $"{name} at slice {obj.Trk.Slice}: {changed} visible pixels");
        }
        if (Environment.GetEnvironmentVariable("NFS_SMACKABLE_REVIEW_DIR") is { Length: > 0 } output)
        {
            var mosaic = new FrameBuffer(640 * pictures.Count, 480);
            for (int i = 0; i < pictures.Count; i++)
                for (int y = 0; y < 480; y++)
                    Array.Copy(pictures[i].Rgba, y * 640 * 4, mosaic.Rgba, (y * mosaic.Width + i * 640) * 4, 640 * 4);
            mosaic.SavePng(Path.Combine(output, "smackables_mosaic.png"));
        }
    }
}
