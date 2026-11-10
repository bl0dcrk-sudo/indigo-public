using System.Numerics;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

public class RenderMathTests
{
    [Fact]
    public void ProjectionCentreAndFov()
    {
        var c = new Camera();
        var (x, y) = c.Project(new Vector3(0, 0, 10));
        Assert.Equal(320, x, 6); Assert.Equal(240, y, 6);
        // 45° right edge → x = 640; vertical half 36° → y = 0 at top
        Assert.Equal(640, c.Project(new Vector3(10, 0, 10)).X, 6);
        Assert.Equal(0, c.Project(new Vector3(0, 10 * (float)Math.Tan(36 * Math.PI / 180), 10)).Y, 3);
        Assert.Equal(320, c.Fx, 6);
    }

    [Fact]
    public void CameraBasisAndView()
    {
        var c = new Camera { Position = new Vector3(1, 2, 3) };
        c.LookAlong(Vector3.UnitX, Vector3.UnitY);
        var v = c.ToView(new Vector3(11, 2, 3));
        Assert.Equal(new Vector3(0, 0, 10), v);
        Assert.Equal(1, c.ToView(new Vector3(1, 3, 3)).Y, 5);
    }

    [Fact]
    public void ZSortBucketsMatchRisk()
    {
        // (z) (z + 0xA0000) >> 15 → world polygon at z = 0 is bucket 20 (+10 m) [ref:3299]
        Assert.Equal(20, ZSort<int>.BucketFor(0));
        Assert.Equal(21, ZSort<int>.BucketFor(0.5));
        Assert.Equal(0, ZSort<int>.BucketFor(0, ZSort<int>.CarBias));
        Assert.Equal(-1, ZSort<int>.BucketFor(-11));
        Assert.Equal(-1, ZSort<int>.BucketFor(700));
        var z = new ZSort<string>();
        z.Insert(1, "near1"); z.Insert(100, "far"); z.Insert(1, "near2");
        Assert.Equal(["far", "near2", "near1"], z.FarToNear().Select(t => t.Item).ToArray());
    }

    [Fact]
    public void RasterizerFillsSquareExactly()
    {
        var fb = new FrameBuffer(16, 16);
        fb.Clear(0, 0, 0);
        ScreenVertex[] q = [new(2, 2, 0, 0), new(6, 2, 0, 0), new(6, 6, 0, 0), new(2, 6, 0, 0)];
        Rasterizer.Fill(fb, q, null, 0xFF0000);
        int n = 0;
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) if (fb.Get(x, y) == 0xFF0000) n++;
        Assert.Equal(16, n);
        Assert.Equal(0xFF0000u, fb.Get(2, 2)); Assert.Equal(0u, fb.Get(6, 6));
    }

    [Fact]
    public void RasterizerTextureAndTransparency()
    {
        var tex = new Texture(2, 1, [0x112233, 0x445566], [false, true]);
        var fb = new FrameBuffer(4, 1);
        fb.Clear(9, 9, 9);
        ScreenVertex[] q = [new(0, 0, 0, 0), new(4, 0, 2, 0), new(4, 1, 2, 1), new(0, 1, 0, 1)];
        Rasterizer.Fill(fb, q, tex, 0, transparent: true);
        Assert.Equal(0x112233u, fb.Get(0, 0)); Assert.Equal(0x112233u, fb.Get(1, 0));
        Assert.Equal(0x090909u, fb.Get(3, 0));
    }

    [Fact]
    public void TextureOrientation()
    {
        var t = new Texture(2, 1, [1, 2], [false, false]);
        Assert.Equal([2u, 1u], t.Oriented(0x10).Rgb);           // mirror H
        var r = t.Oriented(0x02);                               // 90° CW: 2×1 → 1×2
        Assert.Equal((1, 2), (r.Width, r.Height));
        Assert.Equal([1u, 2u], r.Rgb);
        Assert.Equal([2u, 1u], t.Oriented(0x04).Rgb);           // 180°
    }

    [Fact]
    public void FogLerp()
    {
        Assert.Equal(0x102030u, Rasterizer.Fog(0x102030, 0, 0xFFFFFF));
        Assert.Equal(0x7F7F7Fu, Rasterizer.Fog(0x000000, 8, 0xFFFFFF));
    }
}

public class TrackRenderTests
{
    static readonly Lazy<TrackRenderer> Tr00 = new(() => new TrackRenderer(0, GameData.Dir));

    public static TheoryData<int> Tracks() => new(0, 2, 3, 4, 5, 6, 7, 8);

    [GameFact]
    public void Tr00FlatFromStart()
    {
        var r = Tr00.Value;
        var fb = new FrameBuffer();
        var cam = r.CameraAtSlice(0);
        r.Render(fb, cam, new RenderOptions { Textured = false, Fog = false });
        Assert.True(r.LastDrawn > 100, $"drawn {r.LastDrawn}");
        fb.SavePng(Path.Combine(GameData.OutDir(), "render_tr00_flat.png"));
        // something non-sky below the horizon at the screen centre (the road)
        Assert.NotEqual(fb.Get(320, 479), fb.Get(320, 0));
    }

    [GameFact]
    public void Tr00TexturedFromStart()
    {
        var r = Tr00.Value;
        var fb = new FrameBuffer();
        r.Render(fb, r.CameraAtSlice(0), new RenderOptions { Fog = false });
        fb.SavePng(Path.Combine(GameData.OutDir(), "render_tr00_tex.png"));
        var fb2 = new FrameBuffer();
        r.Render(fb2, r.CameraAtSlice(0), new RenderOptions());
        fb2.SavePng(Path.Combine(GameData.OutDir(), "render_tr00_fog.png"));
        Assert.True(r.LastDrawn > 100);
    }

    [GameFact]
    public void Tr00WithF355InFront()
    {
        var r = Tr00.Value;
        var car = new CarModel("F355", GameData.Dir);
        Assert.True(car.Geo.Parts[0].Polygons.Length > 0);
        Assert.NotNull(car.TextureFor(car.Geo.Parts[0].Polygons[0].Texture));
        var cam = r.CameraAtSlice(4, height: 1.6f);
        var s = r.Slices[6];
        var up = Camera.Vec(s.Up); var fw = Vector3.Normalize(Camera.Vec(s.Forward));
        var right = Vector3.Normalize(Vector3.Cross(up, fw));
        var inst = new CarInstance(car, Camera.SliceCentre(s) + right * 1.5f, right, Vector3.Cross(fw, right), fw);
        var fb = new FrameBuffer();
        r.Render(fb, cam, new RenderOptions(), [inst]);
        fb.SavePng(Path.Combine(GameData.OutDir(), "render_tr00_f355.png"));
        var fb2 = new FrameBuffer();
        r.Render(fb2, cam, new RenderOptions());
        Assert.NotEqual(fb.Sha256(), fb2.Sha256());
    }

    [GameFact]
    public void Tr00FogCurveFromHrz()
    {
        var r = Tr00.Value;
        Assert.Equal([0, 250, 500, 900], r.Hrz!.DepthCueDistances);
        Assert.Equal([0, 0, 10, 15], r.Hrz.DepthCueLevels);
        Assert.InRange(r.FogLevel(250), 0, 2); // B-spline, not the control point itself [ref:3300]
        Assert.InRange(r.FogLevel(500), 6, 9);
        Assert.InRange(r.FogLevel(700), 11, 14);
        Assert.Equal(15, r.FogLevel(1239));
    }

    [GameFact]
    public void RenderIsDeterministic()
    {
        var r = Tr00.Value;
        var a = new FrameBuffer(); var b = new FrameBuffer();
        r.Render(a, r.CameraAtSlice(40), new RenderOptions());
        r.Render(b, r.CameraAtSlice(40), new RenderOptions());
        Assert.Equal(a.Sha256(), b.Sha256());
    }

    [GameFact]
    public void Tr00SlicesStrip()
    {
        var r = Tr00.Value;
        for (int s = 0; s < r.Slices.Length; s += 200)
        {
            var fb = new FrameBuffer();
            r.Render(fb, r.CameraAtSlice(s), new RenderOptions());
            fb.SavePng(Path.Combine(GameData.OutDir("render_tr00_slices"), $"slice_{s:0000}.png"));
        }
    }

    [GameTheory]
    [MemberData(nameof(Tracks))]
    public void EveryTrackRendersFromStart(int t)
    {
        var r = new TrackRenderer(t, GameData.Dir);
        var fb = new FrameBuffer();
        r.Render(fb, r.CameraAtSlice(0), new RenderOptions());
        Assert.True(r.LastDrawn > 50, $"TR{t:00} drawn {r.LastDrawn}");
        fb.SavePng(Path.Combine(GameData.OutDir("render_tracks"), $"tr{t:00}_start.png"));
    }
}
