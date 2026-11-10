using System.Numerics;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>View draw distances: [ref:3312]
/// split screen: objects 400 m, blocks 440 m, cars up to 150 m (full screen 500 / 500 / 200 m).</summary>
public class ViewRangeTests
{
    [Fact]
    public void Views_HaveTheOriginalRanges()
    {
        var main = ViewRange.Main; var split = ViewRange.Split; var mirror = ViewRange.Mirror;
        Assert.Equal((500f, 500f, 200f), (main.ObjectRangeMetres, main.BlockRangeMetres, main.CarDepthMetres));
        Assert.Equal((400f, 440f, 150f), (split.ObjectRangeMetres, split.BlockRangeMetres, split.CarDepthMetres));
        Assert.Equal((150f, 100f, 150f), (mirror.ObjectRangeMetres, mirror.BlockRangeMetres, mirror.CarDepthMetres));
        Assert.Equal((110 << 16, 150 << 16, 2000, 42), (main.LodNear, main.LodMid, main.CullLimit, main.MaxNeighbours));
        Assert.Equal((60 << 16, 92 << 16, 700, 24), (split.LodNear, split.LodMid, split.CullLimit, split.MaxNeighbours));
        Assert.Equal((40 << 16, 80 << 16, 250, 6), (mirror.LodNear, mirror.LodMid, mirror.CullLimit, mirror.MaxNeighbours));
    }

    [Fact]
    public void DefaultRenderOptions_UseFullScreenColObjectRange_WhileSplitKeepsItsOwnLimit()
    {
        Assert.Equal(500f, new RenderOptions().ObjectRange);
        Assert.Equal(400f, ViewRange.Split.ObjectRangeMetres);
        Assert.Equal(150f, ViewRange.Mirror.ObjectRangeMetres);
    }

    [Fact]
    public void DrawDistanceLevel_ScalesBlocksAndLod_NotTheObjectRange()
    {
        var split = ViewRange.Split;
        Assert.Equal(split, split.Scaled(0));
        foreach (int level in new[] { 1, 2 })
        {
            var s = split.Scaled(level);
            long k = ViewRange.DrawDistScale[level];
            Assert.Equal((int)(k * split.BlockRange >> 16), s.BlockRange);
            Assert.Equal((int)(k * split.LodNear >> 16), s.LodNear);
            Assert.Equal((int)(k * split.LodMid >> 16), s.LodMid);
            Assert.Equal(split.ObjectRange, s.ObjectRange);
            Assert.Equal(split.CarDepth, s.CarDepth);
        }
        Assert.Equal(new[] { 0x10000, 0x8CCC, 0x6666 }, ViewRange.DrawDistScale);   // 1.0 / 0.55 / 0.4 [ref:3313]
        Assert.Equal(440f * 0.55f, split.Scaled(1).BlockRangeMetres, 0.05f);
    }

    static readonly Lazy<TrackRenderer> Tr00 = new(() => new TrackRenderer(0, GameData.Dir));

    [GameFact]
    public void BlockRange_CutsFarBlocks()
    {
        var r = Tr00.Value;
        var cam = r.CameraAtSlice(4, height: 1.6f);
        var o = new RenderOptions { Fog = false, Objects = false };
        r.Render(new FrameBuffer(), cam, o);
        int all = r.LastEmitted;
        r.Render(new FrameBuffer(), cam, o, range: ViewRange.Split);
        Assert.InRange(r.LastEmitted, 1, all);
        r.Render(new FrameBuffer(), cam, o, range: ViewRange.Split with { BlockRange = 0x400000 });   // 64 m
        Assert.InRange(r.LastEmitted, 1, all - 1);
    }

    [GameFact]
    public void CarsBeyondTheViewDepth_AreNotDrawn()
    {
        var r = Tr00.Value;
        var car = new CarModel("F355", GameData.Dir);
        var cam = r.CameraAtSlice(4, height: 1.6f);
        var o = new RenderOptions { Fog = false, Objects = false };
        var fw = Vector3.Normalize(new Vector3(cam.Forward.X, 0, cam.Forward.Z));
        CarInstance At(float metres)
        {
            var p = cam.Position + cam.Forward * metres;
            var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fw));
            return new CarInstance(car, p, right, Vector3.UnitY, fw);
        }
        int Emitted(float metres, ViewRange? range)
        {
            r.Render(new FrameBuffer(), cam, o, [At(metres)], range: range);
            return r.LastEmitted;
        }
        int none = Emitted(1000, null);
        Assert.True(Emitted(140, null) > none);                           // 200 m (full screen)
        Assert.True(Emitted(170, null) > none);
        int noneSplit = Emitted(1000, ViewRange.Split);
        Assert.True(Emitted(140, ViewRange.Split) > noneSplit);           // split: 150 m
        Assert.Equal(noneSplit, Emitted(170, ViewRange.Split));
        Assert.True(Emitted(170, ViewRange.Main) > Emitted(1000, ViewRange.Main));
    }
}
