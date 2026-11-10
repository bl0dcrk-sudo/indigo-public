using System.Buffers.Binary;
using System.Numerics;
using NFS.Core;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

public class MirrorTests
{
    static readonly CockpitValues Values = new(0, 0, 7000, 0, 1);
    static FrameBuffer Strip(bool gradient = false)
    {
        var strip = new FrameBuffer(640, 70);
        strip.Clear(217, 31, 147);
        if (gradient) for (int y = 0; y < 70; y++) for (int x = 0; x < 640; x++)
            strip.Set(x, y, (uint)((x & 255) << 16 | y << 8 | ((x >> 8) + 1)));
        return strip;
    }
    static FrameBuffer Draw(Cockpit dash, FrameBuffer strip, bool hud = false)
    {
        var fb = new FrameBuffer(); fb.Clear(13, 31, 47);
        dash.Draw(fb, Values, mirror: strip, showHud: hud);
        return fb;
    }
    static RaceDrive Drive()
    {
        var setup = RaceInfo.Setup("replay"); setup.Set(0x24, 2); setup.Set(0x78, 2);
        var d = new RaceDrive(new RaceScene(new RaceLoader().Load(setup)), countdown: false);
        while (d.Cam.Mode != 0) d.Cam.Cycle(d.Scene.PresentPoses(1), d.Car);
        if (!d.Scene.Cockpit!.MirrorEnabled) d.Scene.Cockpit.ToggleMirror();
        return d;
    }

    [Fact]
    public void MirrorDefaultsAndHudconFieldMatchDhInit()
    {
        var hudcon = new byte[0x98];
        for (int type = -1; type <= 15; type++)
        {
            bool available = type is >= 0 and <= 11 && type != 4;
            Assert.Equal(available, Cockpit.InitialMirror(type, []));
            Assert.False(Cockpit.InitialMirror(type, hudcon));
            BinaryPrimitives.WriteInt32LittleEndian(hudcon.AsSpan(0x80), 1);
            Assert.Equal(available, Cockpit.InitialMirror(type, hudcon));
            BinaryPrimitives.WriteInt32LittleEndian(hudcon.AsSpan(0x80), 0);
        }
    }

    [GameDataFact]
    public void F355MaskReflectsBothGradientAxesAtOriginalFirstSpan()
    {
        var dash = new Cockpit(2);
        if (!dash.MirrorEnabled) dash.ToggleMirror();
        var strip = Strip(true);
        foreach (bool hud in new[] { false, true })
        {
            var fb = Draw(dash, strip, hud);
            // dsh02 mask20: src569,dst40260,len60; src+149-i => row1, x78-i.
            Assert.Equal(strip.Get(78, 1), fb.Get(580, 62));
            Assert.Equal(strip.Get(19, 1), fb.Get(639, 62));
        }
    }

    [GameDataFact]
    public void AllDashboardsComposeTheirMasksAndOnlyMirrorPixelsDependOnStrip()
    {
        for (int type = 0; type < 12; type++)
        {
            var dash = new Cockpit(type);
            if (type != 4 && !dash.MirrorEnabled) dash.ToggleMirror();
            foreach (bool hud in new[] { false, true })
            {
                var solid = Strip(); var other = Strip(); other.Clear(29, 213, 19);
                var a = Draw(dash, solid, hud); var b = Draw(dash, other, hud); var gradient = Strip(true);
                var c = Draw(dash, gradient, hud);
                int changes = 0, left = 0;
                for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
                    if (a.Get(x, y) != b.Get(x, y))
                    {
                        changes++; if (x < 320) left++;
                        Assert.Equal(0xD91F93u, a.Get(x, y));
                        Assert.Equal(0x1DD513u, b.Get(x, y));
                        Assert.NotEqual(a.Get(x, y), c.Get(x, y));
                    }
                    else Assert.Equal(a.Get(x, y), c.Get(x, y));
                if (type == 4) Assert.Equal(0, changes);
                else Assert.True(changes > 500);
                if (type == 0) Assert.True(left > 500); // separate mir1 / mask24.
            }
            bool previous = dash.MirrorEnabled;
            dash.ToggleMirror(); Assert.Equal(type == 4 ? previous : !previous, dash.MirrorEnabled);
            dash.ToggleMirror(); Assert.Equal(previous, dash.MirrorEnabled);
        }
    }

    [GameDataFact]
    public void ExternalViewsAndHeldBHideMirrorAndReleaseRestoresCockpit()
    {
        var d = Drive();
        d.ShowHud = true;
        var fb = new FrameBuffer(); d.Render(fb); var front = (byte[])fb.Rgba.Clone();
        Assert.True(d.ShowMirror);
        d.Frame(0, default(DriveKeys) with { LookBehind = true });
        Assert.False(d.ShowMirror); Assert.True(d.ShowHud);
        var sentinel = new FrameBuffer(); sentinel.Clear(1, 2, 3); string original = sentinel.Sha256();
        d.Scene.Cockpit!.Draw(sentinel, Values, 9, Strip()); Assert.Equal(original, sentinel.Sha256());
        d.Render(fb); Assert.False(front.SequenceEqual(fb.Rgba));
        d.Frame(0, default); d.Render(fb); Assert.Equal(front, fb.Rgba);
        foreach (int mode in new[] { 1, 2, 3 })
        {
            d.Cam.Cycle(d.Scene.PresentPoses(1), d.Car);
            Assert.Equal(mode, d.Cam.Mode); Assert.False(d.ShowMirror);
            d.Scene.Cockpit.Draw(sentinel, Values, mode, Strip()); Assert.Equal(original, sentinel.Sha256());
        }
    }

    [GameDataFact]
    public void MirrorCarLodRangeAndPlayerExclusionMatchOriginal()
    {
        var d = Drive(); var scene = d.Scene;
        var player = scene.Session.Cars.First(c => ReferenceEquals(c.Obj, d.Car));
        var ai = scene.Session.Cars.First(c => !ReferenceEquals(c, player));
        foreach (var c in scene.Session.Cars) if (!ReferenceEquals(c, player)) c.Obj.Pos = new(0, 0, -1000 * Fx.One);
        var camera = new Camera(); camera.LookAlong(-Vector3.UnitZ, Vector3.UnitY);
        foreach (var (distance, lod) in new[] { (20, 1), (30, 2), (151, -1) })
        {
            ai.Obj.Pos = new(0, 0, -distance * Fx.One); ai.Obj.UpDotN = Fx.One; ai.Obj.CrashSlice = 0;
            d.SnapToHost(); scene.PresentPoses(1);
            var cars = scene.MirrorCars(camera).ToArray();
            Assert.DoesNotContain(cars, c => c.Model == scene.Cars[scene.PlayerInstance].Model && c.Position == scene.Cars[scene.PlayerInstance].Position);
            var found = cars.Where(c => c.Position == new Vector3(0, 0, -distance)).ToArray();
            if (lod < 0) Assert.Empty(found); else Assert.Equal(lod, Assert.Single(found).Lod);
        }
    }

    [GameDataFact]
    public void CarBehindPlayerChangesOnlyMirrorPixelsInActualRaceRenderer()
    {
        var d = Drive(); var scene = d.Scene;
        if (d.ShowMap) d.ToggleHud();
        d.ShowHud = false;
        var pose = scene.PresentPoses(1);
        var ai = scene.Session.Cars.First(c => !ReferenceEquals(c.Obj, d.Car));
        static Vec3 Fixed(Vector3 p) => new((int)(p.X * Fx.One), (int)(p.Y * Fx.One), (int)(p.Z * Fx.One));
        foreach (var c in scene.Session.Cars) if (!ReferenceEquals(c.Obj, d.Car))
            c.Obj.Pos = Fixed(pose.Position - pose.Forward * 1000);
        d.SnapToHost(); scene.PresentPoses(1);
        var absent = new FrameBuffer(); d.Render(absent);
        ai.Obj.Pos = Fixed(pose.Position - pose.Forward * 15 + pose.Right);
        ai.Obj.Orient = d.Car.Orient; ai.Obj.UpDotN = Fx.One;
        d.SnapToHost(); scene.PresentPoses(1);
        var present = new FrameBuffer(); d.Render(present);
        var s1 = Strip(); var s2 = Strip(); s2.Clear(29, 213, 19);
        var mask1 = Draw(scene.Cockpit!, s1); var mask2 = Draw(scene.Cockpit!, s2);
        int changes = 0;
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
            if (absent.Get(x, y) != present.Get(x, y))
            {
                changes++;
                Assert.NotEqual(mask1.Get(x, y), mask2.Get(x, y));
            }
        Assert.True(changes > 5, $"rear car changed {changes} mirror pixels");
    }

    // ---- Enhanced: the strip rendered K times larger and pasted under the mask (task 08f) ----

    [GameDataTheory]
    [InlineData(0, 2, 640)]
    [InlineData(2, 2, 640)]
    [InlineData(5, 3, 640)]
    [InlineData(9, 2, 856)]    // panorama: the 2D layer is centred in the wider scene
    [InlineData(2, 4, 1120)]
    public void ScaledStripIsTheExactKTimesVersionOfTheOriginalPaste(int type, int k, int viewWidth)
    {
        var dash = new Cockpit(type);
        if (!dash.MirrorEnabled) dash.ToggleMirror();
        var strip = Strip(true);
        var original = new FrameBuffer(); original.Clear(13, 31, 47);
        dash.Draw(original, Values, mirror: strip, showHud: true);
        var overlay = new FrameBuffer(); overlay.ClearTransparent();
        var scene = new FrameBuffer(viewWidth * k, 480 * k);
        var backdrop = new FrameBuffer(viewWidth * k, 480 * k); backdrop.Clear(13, 31, 47);
        backdrop.Rgba.CopyTo(scene.Rgba, 0);
        dash.Draw(overlay, Values, mirror: Compositor.UpscaleNearest(strip, k), showHud: true, scene: scene);
        // composed (centred layer) vs the original frame scaled K times: identical, so the K-times strip is only a finer version of the same picture
        var expected = Compositor.UpscaleNearest(original, k);
        int x0 = (viewWidth - 640) / 2 * k, bad = 0, transparent = 0;
        var wide = new FrameBuffer(viewWidth, 480); wide.ClearTransparent();
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
            Array.Copy(overlay.Rgba, (y * 640 + x) * 4, wide.Rgba, (y * viewWidth + x + (viewWidth - 640) / 2) * 4, 4);
        var composed = Compositor.Compose(scene, wide);
        for (int y = 0; y < 480 * k; y++) for (int x = 0; x < 640 * k; x++)
            if (composed.Get(x0 + x, y) != expected.Get(x, y) && !(viewWidth > 640 && (x < k || x >= 639 * k))) bad++;   // panorama: edge columns are repeated into the margins (1x sample)
        for (int i = 3; i < overlay.Rgba.Length; i += 4) if (overlay.Rgba[i] == 0) transparent++;
        Assert.Equal(0, bad);
        Assert.True(transparent > 500, "mirror pixels of the 2D layer must be transparent");
        // panorama: where the mirror reaches the layer edge, the edge pixel stays transparent (repeated into the margin), not the last picture column
        if (viewWidth > 640)
            for (int y = 0; y < 480; y++)
                if (overlay.Rgba[(y * 640 + 638) * 4 + 3] == 0 && original.Get(638, y) != (13u << 16 | 31u << 8 | 47u)) Assert.Equal(0, overlay.Rgba[(y * 640 + 639) * 4 + 3]);
    }

    [GameDataFact]
    public void ScaledStripKeepsWindowOpenAndRejectsWrongSizes()
    {
        var dash = new Cockpit(2);
        if (!dash.MirrorEnabled) dash.ToggleMirror();
        var overlay = new FrameBuffer(); overlay.ClearTransparent();
        Assert.Throws<ArgumentException>(() => dash.Draw(overlay, Values, mirror: new FrameBuffer(1280, 140), scene: new FrameBuffer(1280, 480)));
        Assert.Throws<ArgumentException>(() => dash.Draw(overlay, Values, mirror: new FrameBuffer(1280, 139), scene: new FrameBuffer(1280, 960)));
    }

    [GameDataFact]
    public void EnhancedMirrorIsRenderedAtSceneResolution_VanillaAndK1Unchanged()
    {
        FrameBuffer Frame(int k, int ssaa)
        {
            var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, k, ssaa);
            GfxScenes.Render("cockpit", t);
            return t.ComposeCpu();
        }
        var f1 = Frame(1, 1); var f2 = Frame(2, 1);
        // the mirror area at k=2 carries real detail: count 2x2 blocks (aligned to the original pixel grid) that are not uniform inside the mask
        int detail = 0, total = 0;
        var d = Drive(); var mask = d.Scene.Cockpit!;
        var marker = Strip(); marker.Clear(255, 0, 255);
        var a = Draw(mask, Strip()); var b = Draw(mask, marker);
        for (int y = 0; y < 480; y++) for (int x = 0; x < 640; x++)
        {
            if (a.Get(x, y) == b.Get(x, y)) continue;
            total++;
            uint p = f2.Get(2 * x, 2 * y);
            if (f2.Get(2 * x + 1, 2 * y) != p || f2.Get(2 * x, 2 * y + 1) != p || f2.Get(2 * x + 1, 2 * y + 1) != p) detail++;
        }
        Assert.True(total > 500);
        Assert.True(detail > total / 10, $"only {detail}/{total} mirror blocks show sub-pixel detail");
        // k=1 without SSAA keeps the mirror in the 2D layer: the frame is the plain 640x480 original
        Assert.Equal(640, f1.Width);
    }

    [GameDataFact]
    public void ReviewPictures()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, k, ssaa, width) in new[] { ("mirror_k1", 1, 1, 640), ("mirror_k2", 2, 1, 640), ("mirror_k3", 3, 1, 640), ("mirror_k2_wide", 2, 1, 856), ("mirror_k1_ssaa2", 1, 2, 640) })
        {
            var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, k, ssaa, width);
            GfxScenes.Render("cockpit", t);
            t.ExtendEdges = true;
            var frame = t.ComposeCpu();
            frame.SavePng(Path.Combine(dir, name + ".png"));
            // zoom: the mirror (x 480..640, y 40..140 of the 640x480 layer, F355) at the frame's own resolution, enlarged to 1280 px wide
            int kk = frame.Height / 480, cx = ((width - 640) / 2 + 480) * kk, cy = 40 * kk, cw = 160 * kk, ch = 100 * kk;
            var crop = new FrameBuffer(cw, ch);
            for (int y = 0; y < ch; y++) Array.Copy(frame.Rgba, ((cy + y) * frame.Width + cx) * 4, crop.Rgba, y * cw * 4, cw * 4);
            Compositor.UpscaleNearest(crop, Math.Max(1, 8 / kk)).SavePng(Path.Combine(dir, name + "_crop.png"));
        }
    }
}
