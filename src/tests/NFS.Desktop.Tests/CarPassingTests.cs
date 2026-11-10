using NFS.Core;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class CarPassingTests
{
    [GameDataFact]
    public void PassingMclarenAtRightEdgeShowsItsSideAndFrontWheel()
    {
        var track = new TrackRenderer(4);
        var road = track.CameraAtSlice(20, height: 0);
        var pose = new RenderPose(road.Position, road.Right, road.Up, road.Forward);
        var model = new CarModel("MCF1", carType: 0);
        // the model is drawn around CarObj.Pos (the box centre): lift it by parts[1].Y so that it stands on the road under the camera pose, as before
        var centre = pose.Position + pose.Up * (model.Geo.Parts[1].Y / 65536f);
        var player = new CarInstance(model, centre, pose.Right, pose.Up, pose.Forward);
        var passing = player with { Position = centre + pose.Right * 4 - pose.Forward * 3 };

        // Use Desktop's default far chase camera, including its original projection.
        var raceCamera = new RaceCamera(0);
        raceCamera.Snap(pose, new CarObj { UpDotN = Fx.One });
        var camera = raceCamera.Camera();
        var vertices = passing.Polys().SelectMany(p => p.P).Select(camera.ToView).ToArray();
        Assert.Contains(vertices, v => camera.Project(v).X >= 640);

        var picture = new FrameBuffer();
        track.Render(picture, camera, new RenderOptions { Fog = false }, [player, passing]);
        if (Environment.GetEnvironmentVariable("NFS_PASSING_REVIEW_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            picture.SavePng(Path.Combine(output, "tr04_mcf1_passing_x4_z-3.png"));
        }

        // Below the window, behind the visible front wheel: the red side/sill must cover this
        // rectangle. Drawing hidden faces over it produces the user's nearly black wedge.
        int body = Count(picture, 565, 260, 600, 285,
            (r, g, b) => r > 30 && r > 2 * g && r > 2 * b);
        Assert.True(body > 700, $"Side/sill: {body}/875 red pixels; a hidden face may cover the body.");

        // The exposed silver rim must remain visible inside the front wheel, independently of
        // the body assertion (a red patch over the entire wheel would not be a valid repair).
        int rim = Count(picture, 527, 245, 548, 270,
            (r, g, b) => Math.Min(r, Math.Min(g, b)) > 100
                && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40);
        Assert.True(rim > 80, $"Front wheel: {rim}/525 bright neutral rim pixels.");
    }

    static int Count(FrameBuffer picture, int left, int top, int right, int bottom,
        Func<int, int, int, bool> matches)
    {
        int count = 0;
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                uint rgb = picture.Get(x, y);
                if (matches((int)(rgb >> 16), (int)((rgb >> 8) & 255), (int)(rgb & 255))) count++;
            }
        return count;
    }
}
