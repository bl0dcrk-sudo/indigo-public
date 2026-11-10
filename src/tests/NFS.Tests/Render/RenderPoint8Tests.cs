using System.Numerics;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>tunnel sky, HORIZONDISPLAY [ref:3296] [ref:3497]
/// and fog curve. [ref:3297]</summary>
public class RenderPoint8Tests
{
    static Camera SkyCam(TrackRenderer r)
    {
        var s = r.Slices[SkyTests.OpenSlice(r)];
        var fw = Camera.Vec(s.Forward); fw.Y = 0; fw = Vector3.Normalize(fw);
        var cam = new Camera { Position = Camera.SliceCentre(s) + new Vector3(0, 5000, 0) };
        cam.LookAlong(fw, Vector3.UnitY);
        return cam;
    }

    static FrameBuffer Frame(TrackRenderer r, Camera cam, RenderOptions? o = null, int slice = -1, int flag = 0, bool mirror = false)
    {
        var fb = new FrameBuffer();
        r.Render(fb, cam, o ?? new RenderOptions { Objects = false }, rearMirror: mirror, targetSlice: slice, targetWrongWayTicks: flag);
        return fb;
    }

    static bool AllColour(FrameBuffer fb, uint rgb)
    {
        for (int y = 0; y < fb.Height; y += 7)
            for (int x = 0; x < fb.Width; x += 5)
                if ((fb.Get(x, y) & 0xFFFFFF) != rgb) return false;
        return true;
    }

    static int FirstTunnelSlice(TrackRenderer r)
    {
        for (int i = 0; i < r.Slices.Length; i++)
        {
            byte b = r.Slices[i].Surroundings;
            if (b >> 4 == 2 || (b & 0xF) == 2) return i;
        }
        return -1;
    }

    [GameFact]
    public void Tunnel_Condition_UsesSliceMinus8_OrPlus8ByCarFlag()
    {
        var r = new TrackRenderer(6, GameData.Dir);
        int t = FirstTunnelSlice(r);
        Assert.True(t >= 0, "TR06 should have a tunnel");
        Assert.True(r.IsTunnelNear(t + 8, 0));
        Assert.True(r.IsTunnelNear(t - 8, 1));
        Assert.True(r.IsTunnelNear(t - 8 + r.Slices.Length, 1)); // wraps
        Assert.False(r.IsTunnelNear(SkyTests.OpenSlice(r), 0));
    }

    [GameFact]
    public void Tunnel_ReplacesTheSkyWithBlack_OnlyInTheRearMirror()
    {
        var r = new TrackRenderer(6, GameData.Dir);
        int t = FirstTunnelSlice(r);
        var cam = SkyCam(r);
        var open = Frame(r, cam, slice: SkyTests.OpenSlice(r));
        Assert.False(r.IsTunnelNear(SkyTests.OpenSlice(r), 0));
        Assert.False(AllColour(open, 0));                        // sky visible
        // CMP [view],1 -> only for view 1 [ref:3298]
        // the 640x70 dash mirror; the main view 0 keeps its sky (user report 2026-10-15: black sky leaving the TR06 tunnel).
        var main = Frame(r, cam, slice: t + 8);
        Assert.False(AllColour(main, 0), "main view keeps the sky near a tunnel");
        var mirror = Frame(r, cam, slice: t + 8, mirror: true);
        Assert.True(AllColour(mirror, 0), "mirror: black background instead of the sky");
        var reverse = Frame(r, cam, slice: t - 8, flag: 1, mirror: true);
        Assert.True(AllColour(reverse, 0));
    }

    /// <summary>Place from the user's dump (TR06, player car on slice 1146 just after the tunnel 1072..1145, flag 0):
    /// slice 1138 is tunnel, yet the main view must show the sky.</summary>
    [GameFact]
    public void Tr06TunnelExit_MainViewStillDrawsTheSky()
    {
        var r = new TrackRenderer(6, GameData.Dir);
        Assert.True(r.IsTunnelNear(1146, 0));
        Assert.False(AllColour(Frame(r, SkyCam(r), slice: 1146), 0));
        Assert.True(AllColour(Frame(r, SkyCam(r), slice: 1146, mirror: true), 0));
    }

    [GameTheory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void HorizonDisplayOff_FillsTheViewWithTheFlatSkyColour(int t)
    {
        var r = new TrackRenderer(t, GameData.Dir);
        var h = r.Hrz!;
        uint sky = (uint)(h.SkyRgb[0] << 16 | h.SkyRgb[1] << 8 | h.SkyRgb[2]);
        var cam = SkyCam(r);
        var on = Frame(r, cam, new RenderOptions { Objects = false });
        var off = Frame(r, cam, new RenderOptions { Objects = false, HorizonDisplay = false });
        Assert.False(AllColour(on, sky));
        Assert.True(AllColour(off, sky));
    }

    [GameFact]
    public void HorizonDisplayOff_StillBlackInATunnel_AndOnBlackHorizonTracks()
    {
        var r = new TrackRenderer(6, GameData.Dir);
        var o = new RenderOptions { Objects = false, HorizonDisplay = false };
        Assert.True(AllColour(Frame(r, SkyCam(r), o, slice: FirstTunnelSlice(r) + 8, mirror: true), 0)); // tunnel black = mirror only
        var r8 = new TrackRenderer(8, GameData.Dir);
        Assert.Equal(1, r8.Hrz!.BlackHorizon);
        Assert.True(AllColour(Frame(r8, SkyCam(r8), o), 0));
    }

    [Fact]
    public void FogCurve_BasisIsAPartitionOfUnity_AndEndPointsAreExact()
    {
        var (x, y) = FogCurve.Sample([0, 250, 500, 900], [0, 0, 10, 15]);
        Assert.Equal(0, x[0]); Assert.Equal(0, y[0]);
        Assert.Equal(900, x[^1]); Assert.Equal(15, y[^1]);
        for (int i = 1; i < x.Length; i++) Assert.True(x[i] > x[i - 1]);   // strictly increasing samples
        // uniform B-spline of constant control values reproduces the constant
        var (cx, cy) = FogCurve.Sample([0, 100, 200, 300], [7, 7, 7, 7]);
        Assert.All(cy, v => Assert.InRange(v, 6, 7));
    }

    [Fact]
    public void FogCurve_IsSmoothAndMonotonic_AndAnApproximationNotALine()
    {
        int[] xs = [0, 250, 500, 900], ls = [0, 0, 10, 15];
        var c = FogCurve.Evaluate(xs, ls);
        Assert.Equal(901, c.Length);
        Assert.Equal(0, FogCurve.Level(c, xs, ls, -5));
        Assert.Equal(0, FogCurve.Level(c, xs, ls, 0));
        Assert.Equal(15, FogCurve.Level(c, xs, ls, 900));
        Assert.Equal(15, FogCurve.Level(c, xs, ls, 1239));
        for (int b = 1; b < c.Length; b++) Assert.InRange(c[b] - c[b - 1], 0, 1);
        Assert.All(c, v => Assert.InRange(v, 0, 15));
        // the B-spline does not pass through the inner control points (the old linear version gave exactly 0 / 10)
        Assert.NotEqual(10, c[500]);
        Assert.InRange(c[250], 0, 2);
        Assert.InRange(c[500], 6, 9);
    }

    [Fact]
    public void FogCurve_DefaultsWithoutHrz_StartAtZeroAndReachFullFog()
    {
        int[] xs = [0, 80, 85, 300], ls = [0, 0, 12, 15];
        var c = FogCurve.Evaluate(xs, ls);
        Assert.Equal(0, c[0]);
        Assert.Equal(15, c[300]);
        Assert.True(c[200] > c[40]);
    }
}
