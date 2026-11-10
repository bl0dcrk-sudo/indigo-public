using System.Numerics;
using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class TrackWallTests
{
    // Camera reconstructed from codex_0c/tr04_raw_before_after_05.png (BEER after pushing).
    static readonly Vector3 Position = new(-405.84692f, 2.7183533f, 646.2198f);
    static readonly Vector3 Forward = new(0.011797878f, -0.2540032f, -0.96713144f);

    static Camera CameraAt(Vector3 position, float yaw = 0)
    {
        var camera = new Camera { Position = position };
        camera.LookAlong(Vector3.Transform(Forward, Matrix4x4.CreateRotationY(yaw * MathF.PI / 180)), Vector3.UnitY);
        return camera;
    }

    static bool White(uint rgb)
    {
        int r = (int)(rgb >> 16), g = (int)(rgb >> 8 & 255), b = (int)(rgb & 255);
        return Math.Min(r, Math.Min(g, b)) > 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 55;
    }

    [GameFact]
    public void Tr04WallRemainsContinuousInTheReportedCamera()
    {
        var track = new TrackRenderer(4, GameData.Dir);
        var picture = new FrameBuffer();
        track.Render(picture, CameraAt(Position));
        Save(picture, "reported");
        // Continuous strip inside the white top, across the sand-coloured gap in the old renderer.
        for (int y = 280; y <= 420; y++)
        {
            int x = (int)(540 - 0.75 * y), white = 0;
            for (int dx = -2; dx <= 2; dx++) if (White(picture.Get(x + dx, y))) white++;
            Assert.True(white >= 4, $"Wall interrupted at ({x},{y}): {white}/5 white pixels.");
        }
    }

    [GameFact]
    public void Tr04WallTopSurvivesCloseCamerasAndObliqueAngles()
    {
        var track = new TrackRenderer(4, GameData.Dir);
        // Road-facing edge of the wall, TR04 block 23, segment crossing the camera's XZ.
        var edge = new Vector3(-402.7461f, 0, 647.35547f);
        var tangent = Vector3.Normalize(new Vector3(-404.61328f, 0, 643.28906f) - edge);
        var normal = Vector3.Cross(Vector3.UnitY, tangent);
        var foot = edge + tangent * Vector3.Dot(Position - edge, tangent);
        var pictures = new List<FrameBuffer>();
        var rows = new List<string> { "distance,yaw,x,y,z,checked,missing" };
        var failures = new List<string>();
        foreach (float distance in new[] { 0.3f, 0.6f, 1f, 2f, 3f })
        foreach (float yaw in new[] { -15f, 0f, 15f })
        {
            var camera = CameraAt(foot + normal * distance + Vector3.UnitY * Position.Y, yaw);
            var picture = new FrameBuffer();
            track.Render(picture, camera);
            pictures.Add(picture);
            int checks = 0, missing = 0;
            foreach (var p in track.Trk.Blocks[23].PolyGroups[5].Where(p => p.Texture == 66))
            {
                var a = (World(track.Trk, 23, p.V0) + World(track.Trk, 23, p.V1)) / 2;
                var b = (World(track.Trk, 23, p.V2) + World(track.Trk, 23, p.V3)) / 2;
                foreach (float t in new[] { 0.2f, 0.5f, 0.8f })
                {
                    var v = camera.ToView(Vector3.Lerp(a, b, t));
                    if (v.Z < 1 || v.Z > 12) continue;
                    var (x, y) = camera.Project(v);
                    if (x < 5 || x >= 635 || y < 5 || y >= 475) continue;
                    checks++;
                    if (!White(picture.Get((int)x, (int)y))) missing++;
                }
            }
            rows.Add(FormattableString.Invariant($"{distance},{yaw},{camera.Position.X},{camera.Position.Y},{camera.Position.Z},{checks},{missing}"));
            if (checks == 0 || missing != 0) failures.Add($"distance {distance}, yaw {yaw}: {missing}/{checks} missing");
        }
        if (Environment.GetEnvironmentVariable("NFS_WALL_REVIEW_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.WriteAllLines(Path.Combine(output, "cameras.csv"), rows);
            var mosaic = new FrameBuffer(640 * 3, 480 * 5);
            for (int i = 0; i < pictures.Count; i++)
            {
                Save(pictures[i], $"camera_{i:00}");
                for (int y = 0; y < 480; y++)
                    Array.Copy(pictures[i].Rgba, y * 640 * 4, mosaic.Rgba,
                        ((i / 3 * 480 + y) * mosaic.Width + i % 3 * 640) * 4, 640 * 4);
            }
            Save(mosaic, "mosaic");
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    static Vector3 World(Trk trk, int block, int index)
    {
        var b = trk.Blocks[block];
        int reference = index < b.NumStickToNext ? (block + 1) % trk.Blocks.Count : block;
        return new Vector3(trk.BlockRefs[reference * 3], trk.BlockRefs[reference * 3 + 1], trk.BlockRefs[reference * 3 + 2]) / 65536f
            + new Vector3(b.Vertices[index * 3], b.Vertices[index * 3 + 1], b.Vertices[index * 3 + 2]) / 256f;
    }

    static void Save(FrameBuffer picture, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_WALL_REVIEW_DIR") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        picture.SavePng(Path.Combine(output, name + ".png"));
    }
}
