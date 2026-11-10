using System.Numerics;
using NFS.Core;
using NFS.Core.Tables;
using NFS.Desktop;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class SmackableGroundTests
{
    static RaceScene Scene(int track)
    {
        var setup = RaceInfo.Setup("replay");
        setup.Set(0x24, track);
        setup.Set(0x4F8, int.MaxValue);
        return new RaceScene(new RaceLoader().Load(setup));
    }

    static TrackRoadQuery Road(RaceScene scene) => new(
        new TrackLocator(new TrackGeometry(scene.Session.Trk, scene.Session.Slices)), scene.Session.TrackNumber);

    // Ground tasks of RaceRunner.StepParked plus two 64 Hz position integrations.
    // SM records are initial placements, not guaranteed resting heights (e.g. TREX).
    static void Rest(RaceScene scene, TrackRoadQuery road, CarObj obj)
    {
        var tune = scene.Session.SimTune!.For(true).Values;
        for (int tick = 0; tick < 256; tick++)
        {
            road.UpdateCar(obj);
            StaticCar.Drive(obj);
            Newton.Step(obj, tune, tick * 2, road);
            NewtonIntegrate.Rot32(obj);
            StaticCar.Post(obj, tune, 1, tick * 2, GameTables.Default);
            NewtonIntegrate.Pos(obj);
            NewtonIntegrate.Pos(obj);
        }
    }

    static float Height(CarObj obj, Vector3 point) => Newton.PlaneHeightAt(
        new Vec3((int)Math.Round(point.X * Fx.One), 0, (int)Math.Round(point.Z * Fx.One)),
        obj.RoadFrame.Row1, obj.RoadPoint) / (float)Fx.One;

    static void Measure(List<string> rows, string label, CarInstance instance, CarObj obj, bool resting)
    {
        var vertices = instance.Polys().SelectMany(p => p.P).ToArray();
        var lowest = vertices.MinBy(v => v.Y);
        // On a slope lowest world Y need not be the point nearest the surface.
        float clearance = vertices.Min(v => v.Y - Height(obj, v));
        rows.Add(FormattableString.Invariant($"{label},{obj.Trk.Slice},{obj.Pos.Y / (float)Fx.One:F6},{lowest.Y:F6},{Height(obj, lowest):F6},{clearance:F6}"));
        if (resting)
            Assert.True(MathF.Abs(clearance) <= 0.04f, $"{label}: bottom/road clearance {clearance:F6} m");
    }

    [GameDataFact]
    public void EverySmRecordHasItsBaseOnTheRoadInAGroundAlignedRestPose()
    {
        var rows = new List<string> { "object,slice,posY,minWorldY,roadAtMinWorldY,minClearance" };
        var pictures = new List<FrameBuffer>();
        int count = 0;
        foreach (int track in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var scene = Scene(track);
            var road = Road(scene);
            for (int i = 0; i < scene.Smackables.Count; i++)
            {
                var obj = scene.Session.Parked![i];
                var model = scene.Smackables[i].Model;
                // FF50 records on TR00 are regular cars; exercise their root LOD geometry.
                var root = scene.Smackables[i] with { Lod = 1 };
                road.UpdateCar(obj);
                Measure(rows, $"TR{track:00}_{model.Name}_{i}_rawSM", root, obj, false);
                Rest(scene, road, obj);
                Measure(rows, $"TR{track:00}_{model.Name}_{i}_physicsRest", RaceScene.Instance(model, obj.Pos, obj.Orient) with { Lod = 1 }, obj, false);
                // Isolate model anchoring from the original GroundContact angular dead zone:
                // a large non-box mesh (TREX) need not touch its conservative collision box's
                // lowest corner on a slope. Use a controlled face parallel to the actual road.
                var up = FxMath.Normalized(obj.RoadFrame.Row1);
                var forward = FxMath.Normalized(Vec3.Cross(obj.Orient.Row0, up));
                var right = FxMath.Normalized(Vec3.Cross(up, forward));
                obj.Orient = Mat3.Identity;
                obj.Orient.Row0 = right;
                obj.Orient.Row1 = up;
                obj.Orient.Row2 = forward;
                StaticCar.Settle(obj); // uses the actual road plane and model collision dimensions [ref:2933]
                var pose = RaceScene.Instance(model, obj.Pos, obj.Orient);
                foreach (int lod in scene.Session.Smackables!.Cars[i].Model >= 40 ? new[] { 0, 1, 2 } : new[] { 1, 2 })
                    Measure(rows, $"TR{track:00}_{model.Name}_{i}_rest_LOD{lod}", pose with { Lod = lod }, obj, true);
                pictures.Add(Picture(scene, pose with { Lod = 1 }));
                count++;
            }
        }
        Assert.Equal(23, count); // all SM00/02/03/04/05/06/07/08 records
        Save("all_records", rows, pictures, 4);
    }

    [GameDataFact]
    public void RuinBeerAndCartRestOnTheRoadAfterBeingPushedAside()
    {
        var scene = Scene(4);
        var road = Road(scene);
        var rows = new List<string> { "object,slice,posY,minWorldY,roadAtMinWorldY,minClearance" };
        var pictures = new List<FrameBuffer>();
        foreach (string name in new[] { "RUIN", "BEER", "CART" })
        {
            int index = scene.Smackables.ToList().FindIndex(c => c.Model.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var obj = scene.Session.Parked![index];
            var model = scene.Smackables[index].Model;
            road.UpdateCar(obj);
            Measure(rows, name + "_rawSM", RaceScene.Instance(model, obj.Pos, obj.Orient), obj, false);
            pictures.Add(Picture(scene, RaceScene.Instance(model, obj.Pos, obj.Orient)));
            Rest(scene, road, obj);
            Measure(rows, name + "_before", RaceScene.Instance(model, obj.Pos, obj.Orient), obj, true);
            pictures.Add(Picture(scene, RaceScene.Instance(model, obj.Pos, obj.Orient)));
            var start = obj.Pos;
            // Reported aftermath: displacement to the verge plus yaw, then physical ground contact.
            obj.Pos += Vec3.Scale(obj.Orient.Row0, 3 * Fx.One);
            obj.Orient = Mat3.Mul(obj.Orient, FxMath.RotY(0x1000));
            obj.Vel = Vec3.Scale(obj.Orient.Row0, Fx.One);
            Rest(scene, road, obj);
            Assert.NotEqual(start.X, obj.Pos.X);
            Assert.Equal(0, obj.Rail);
            var instance = RaceScene.Instance(model, obj.Pos, obj.Orient);
            foreach (int lod in new[] { 0, 1, 2 })
                Measure(rows, name + $"_after_LOD{lod}", instance with { Lod = lod }, obj, true);
            pictures.Add(Picture(scene, instance));
        }
        Save("tr04_raw_before_after", rows, pictures, 3);
    }

    static FrameBuffer Picture(RaceScene scene, CarInstance instance)
    {
        var fb = new FrameBuffer();
        var camera = new Camera(fb.Width, fb.Height);
        camera.Position = instance.Position - instance.Forward * 7 + Vector3.UnitY * 2 + instance.Right * 3;
        camera.LookAlong(Vector3.Normalize(instance.Position - camera.Position), Vector3.UnitY);
        scene.Track.Render(fb, camera, new RenderOptions(), new[] { instance });
        return fb;
    }

    static void Save(string name, List<string> rows, List<FrameBuffer> pictures, int columns)
    {
        if (Environment.GetEnvironmentVariable("NFS_SMACKABLE_GROUND_DIR") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        File.WriteAllLines(Path.Combine(output, name + ".csv"), rows);
        var mosaic = new FrameBuffer(640 * columns, 480 * ((pictures.Count + columns - 1) / columns));
        for (int i = 0; i < pictures.Count; i++)
        {
            pictures[i].SavePng(Path.Combine(output, $"{name}_{i:00}.png"));
            for (int y = 0; y < 480; y++)
                Array.Copy(pictures[i].Rgba, y * 640 * 4, mosaic.Rgba,
                    ((i / columns * 480 + y) * mosaic.Width + i % columns * 640) * 4, 640 * 4);
        }
        mosaic.SavePng(Path.Combine(output, name + "_mosaic.png"));
    }
}

