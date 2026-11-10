using System.Numerics;
using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>Type 3 object references: inline CAN animation, looping on [ref:3275]
/// (mode 1). [ref:3276]</summary>
public class AnimatedObjectTests
{
    static byte[] Ref(int n, int tpk, Func<int, (int x, int y, int z)> pos)
    {
        var d = new byte[8 + 20 * n];
        BitConverter.GetBytes((ushort)n).CopyTo(d, 4);
        BitConverter.GetBytes((ushort)tpk).CopyTo(d, 6);
        for (int i = 0; i < n; i++)
        {
            var (x, y, z) = pos(i);
            BitConverter.GetBytes(x).CopyTo(d, 8 + 20 * i);
            BitConverter.GetBytes(y).CopyTo(d, 12 + 20 * i);
            BitConverter.GetBytes(z).CopyTo(d, 16 + 20 * i);
            BitConverter.GetBytes((short)0).CopyTo(d, 20 + 20 * i);  // identity quaternion: x y z = 0, w = 0x4000
            BitConverter.GetBytes((short)0).CopyTo(d, 22 + 20 * i);
            BitConverter.GetBytes((short)0).CopyTo(d, 24 + 20 * i);
            BitConverter.GetBytes((short)0x4000).CopyTo(d, 26 + 20 * i);
        }
        return d;
    }

    [Fact]
    public void Position_InterpolatesBetweenKeys_AndLoops()
    {
        var raw = Ref(4, 10, i => (i * 0x100000, -i * 0x20000, 0)); // keys 0,16 m / 0,-2 m
        var p0 = AnimatedObjectPose.Eval(raw, 0)!.Value;
        Assert.Equal((0, 0), (p0.X, p0.Y));
        var mid = AnimatedObjectPose.Eval(raw, 5)!.Value;           // half way between key 0 and 1
        Assert.Equal(0x80000, mid.X);
        Assert.Equal(-0x10000, mid.Y);
        var k2 = AnimatedObjectPose.Eval(raw, 20)!.Value;
        Assert.Equal(0x200000, k2.X);
        var wrapped = AnimatedObjectPose.Eval(raw, 30)!.Value;       // (nKeys-1)·ticks = 30 → back to key 0
        Assert.Equal(0, wrapped.X);
        Assert.Equal(AnimatedObjectPose.Eval(raw, 7)!.Value.X, AnimatedObjectPose.Eval(raw, 37)!.Value.X);
        Assert.Equal(1 << 16, mid.M[0]);                             // identity orientation
        Assert.Equal(1 << 16, mid.M[4]);
    }

    [Fact]
    public void TicksPerKey_ZeroOrAbove400_Means6_AndSingleKeyIsIgnored()
    {
        var a = Ref(3, 0, i => (i * 0x10000, 0, 0));
        Assert.Equal(0x10000 / 2, AnimatedObjectPose.Eval(a, 3)!.Value.X);   // 3 of 6 ticks
        var b = Ref(3, 401, i => (i * 0x10000, 0, 0));
        Assert.Equal(0x10000 / 2, AnimatedObjectPose.Eval(b, 3)!.Value.X);
        Assert.Null(AnimatedObjectPose.Eval(Ref(1, 10, _ => (0, 0, 0)), 5));
    }

    static (ObjectRef R, TrackStructure S) FirstMovingRef(TrackRenderer r)
    {
        foreach (var b in r.Trk.Blocks)
        {
            var structs = b.Xb(8)?.Structures() ?? [];
            foreach (var o in b.Xb(7)?.ObjectRefs() ?? [])
                if (o.Type == 3 && o.StructIndex < structs.Length && AnimatedObjectPose.Eval(o.Raw, 0) is { } a
                    && AnimatedObjectPose.Eval(o.Raw, 20) is { } c && (a.X != c.X || a.Y != c.Y || a.Z != c.Z))
                    return (o, structs[o.StructIndex]);
        }
        throw new InvalidOperationException("no animated object");
    }

    static FrameBuffer View(TrackRenderer r, ObjectRef o, int tick)
    {
        var a = AnimatedObjectPose.Eval(o.Raw, tick)!.Value;
        var p = new Vector3(a.X, a.Y, a.Z) / 65536f;
        var cam = new Camera { Position = p + new Vector3(0, 12, 0) };
        cam.LookAlong(new Vector3(0, -1, 0.0001f), Vector3.UnitZ);
        var fb = new FrameBuffer();
        r.Render(fb, cam, new RenderOptions { Sky = false }, tick: tick);
        return fb;
    }

    [GameFact]
    public void Tr00_AnimatedObject_IsDrawn_MovesWithTheTick_AndRepeatsAfterOneCycle()
    {
        var r = new TrackRenderer(0, GameData.Dir);
        var (o, _) = FirstMovingRef(r);
        int n = o.Raw[4] | o.Raw[5] << 8, tpkRaw = o.Raw[6] | o.Raw[7] << 8, tpk = tpkRaw is 0 or > 400 ? 6 : tpkRaw;
        int period = (n - 1) * tpk;
        var f0 = View(r, o, 0);
        var fCycle = View(r, o, period);
        Assert.Equal(f0.Sha256(), fCycle.Sha256());                  // loop
        // a camera that follows the object sees it at the same screen place, but the world behind it changes:
        // the same fixed camera at tick 0 and at a later tick differs because the object moved
        var a0 = AnimatedObjectPose.Eval(o.Raw, 0)!.Value;
        var cam = new Camera { Position = new Vector3(a0.X, a0.Y, a0.Z) / 65536f + new Vector3(0, 40, 0) };
        cam.LookAlong(new Vector3(0, -1, 0.0001f), Vector3.UnitZ);
        var s0 = new FrameBuffer(); var s1 = new FrameBuffer();
        r.Render(s0, cam, new RenderOptions { Sky = false }, tick: 0);
        r.Render(s1, cam, new RenderOptions { Sky = false }, tick: 20);
        Assert.NotEqual(s0.Sha256(), s1.Sha256());
        var noObj = new FrameBuffer();
        r.Render(noObj, cam, new RenderOptions { Sky = false, Objects = false }, tick: 0);
        Assert.NotEqual(s0.Sha256(), noObj.Sha256());
        f0.SavePng(Path.Combine(GameData.OutDir(), "t8_anim_tr00.png"));
    }
}

/// <summary>Animated track textures (XBID 2 flag 0x04): frame (period) % nFrames, shapes ShapeIndex + frame. [ref:3277]</summary>
public class AnimatedTextureTests
{
    static IEnumerable<(TrackRenderer R, int Index)> Animated()
    {
        foreach (int t in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var r = new TrackRenderer(t, GameData.Dir);
            for (int i = 0; i < r.Col.Textures.Length; i++)
                if ((r.Col.Textures[i].Unknown_0x02 & 4) != 0 && r.Col.Textures[i].Frames > 1) yield return (r, i);
        }
    }

    [GameFact]
    public void AnimatedTextures_CycleThroughTheirFrames_AndLoop()
    {
        int n = 0, changed = 0;
        foreach (var (r, i) in Animated())
        {
            var rec = r.Col.Textures[i];
            int period = rec.Unknown_0x08 & 0xFF;
            if (period == 0) continue;
            n++;
            Assert.Same(r.TextureFor(i, 0), r.TextureFor(i, period - 1));         // frame holds for `period` ticks
            Assert.Same(r.TextureFor(i, 0), r.TextureFor(i, period * rec.Frames)); // loops after one cycle
            if (!ReferenceEquals(r.TextureFor(i, 0), r.TextureFor(i, period))) changed++;
        }
        Assert.True(n > 0, "the game should have animated textures");
        Assert.True(changed > 0, "frame changes after one period");
    }

    [GameFact]
    public void StaticTextures_DoNotDependOnTheTick()
    {
        var r = new TrackRenderer(0, GameData.Dir);
        for (int i = 0; i < r.Col.Textures.Length; i++)
            if ((r.Col.Textures[i].Unknown_0x02 & 4) == 0)
                Assert.Same(r.TextureFor(i), r.TextureFor(i, 12345));
    }
}
