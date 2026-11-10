using System.Diagnostics;
using System.Numerics;
using NFS.Core;
using NFS.Desktop;
using NFS.Game.Race;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Enhanced look, stage 3: Gouraud car light, soft car shadow, extended view range, CPU display filters [ref:3449]
/// (FSR-like EASU + RCAS, CRT). Vanilla stays bit-identical (golden hashes in <see cref="GraphicsModeTests"/>).</summary>
public class GraphicsLookTests(ITestOutputHelper output)
{
    // ================= car light (CarLighting + Gouraud fill) =================

    /// <summary>Half cylinder along z (radius 1, length 1, outward normals), faces every 15°, as quads with the outside on the viewer's side.</summary>
    static List<Vector3[]> HalfCylinder(int faces = 12)
    {
        var list = new List<Vector3[]>();
        for (int i = 0; i < faces; i++)
        {
            double a = Math.PI * i / faces, b = Math.PI * (i + 1) / faces;
            Vector3 P(double t, float z) => new((float)Math.Cos(t), (float)Math.Sin(t), z);
            list.Add([P(a, 0), P(a, 1), P(b, 1), P(b, 0)]);
        }
        return list;
    }

    static float[] Light(List<Vector3[]> quads, Vector3 viewer, float angle = 40)
    {
        var res = new float[quads.Count * 4];
        new CarLighting { SmoothAngleDegrees = angle }.Compute(quads, viewer, res);
        return res;
    }

    [Fact]
    public void CarLight_CurvedSurface_IsContinuousAcrossFaces_FlatShadingJumps()
    {
        var quads = HalfCylinder();
        var viewer = new Vector3(0, 10, 0.5f);
        var smooth = Light(quads, viewer);
        var flat = Light(quads, viewer, angle: 0.01f);
        // corners 2,3 of face i are the corners 1,0 of face i+1 (the same vertex)
        double maxSmooth = 0, maxFlat = 0;
        for (int i = 0; i + 1 < quads.Count; i++)
        {
            maxSmooth = Math.Max(maxSmooth, Math.Abs(smooth[i * 4 + 3] - smooth[(i + 1) * 4 + 0]));
            maxSmooth = Math.Max(maxSmooth, Math.Abs(smooth[i * 4 + 2] - smooth[(i + 1) * 4 + 1]));
            maxFlat = Math.Max(maxFlat, Math.Abs(flat[i * 4 + 3] - flat[(i + 1) * 4 + 0]));
        }
        Assert.True(maxSmooth < 1e-4, $"smooth jump {maxSmooth}");
        Assert.True(maxFlat > 0.01, $"flat jump {maxFlat}");
        // the surface really changes its brightness along the curve (a lit bulge, not a flat colour)
        Assert.True(smooth.Max() - smooth.Min() > 0.15f);
    }

    static List<Vector3[]> Cube()
    {
        Vector3[] c = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        int[][] f = [[4, 5, 6, 7], [1, 0, 3, 2], [5, 1, 2, 6], [0, 4, 7, 3], [7, 6, 2, 3], [0, 1, 5, 4]];   // +z, -z, +x, -x, +y, -y
        return f.Select(q => q.Select(i => c[i]).ToArray()).ToList();
    }

    [Fact]
    public void CarLight_Cube_KeepsSharpEdges_UnlessTheAngleLimitIsRaised()
    {
        var cube = Cube();
        var viewer = new Vector3(6, 6, 6);                 // sees +x, +y, +z, which meet in the corner (1,1,1)
        var sharp = Light(cube, viewer);
        int[] visible = [0, 2, 4];
        foreach (int fi in visible)                         // every visible face is lit evenly
            Assert.True(Enumerable.Range(0, 4).Select(c => sharp[fi * 4 + c]).Max() - Enumerable.Range(0, 4).Select(c => sharp[fi * 4 + c]).Min() < 1e-4f);
        float z = sharp[0], x = sharp[2 * 4], y = sharp[4 * 4];
        Assert.True(Math.Abs(z - x) > 0.04f && Math.Abs(x - y) > 0.04f && Math.Abs(z - y) > 0.04f, $"{z} {x} {y}");
        Assert.Equal(CarLighting.Intensity(Vector3.UnitY), y, 1e-4f);        // top face = a pure +y normal
        // a limit above 90° averages the three faces at the shared corner: the edge disappears
        var round = Light(cube, viewer, angle: 100);
        float cz = round[0 * 4 + 2], cx = round[2 * 4 + 3], cy = round[4 * 4 + 1];   // corner (1,1,1) of each face
        Assert.True(Math.Abs(cz - cx) < 1e-4f && Math.Abs(cx - cy) < 1e-4f, $"{cz} {cx} {cy}");
    }

    [Fact]
    public void CarLight_SunSideIsBrighterThanTheShadedSide_AndNormalsFaceTheViewer()
    {
        var upFace = new List<Vector3[]> { new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0) } };   // a floor tile
        float fromAbove = Light(upFace, new Vector3(0.5f, 5, 0.5f))[0], fromBelow = Light(upFace, new Vector3(0.5f, -5, 0.5f))[0];
        Assert.True(fromAbove > fromBelow + 0.3f, $"{fromAbove} vs {fromBelow}");   // the same polygon seen from the other side lit as an underside
        Assert.True(CarLighting.Intensity(CarLighting.Sun) > CarLighting.Intensity(-CarLighting.Sun) + 0.4f);
        Assert.Equal(1f, Light([[Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero]], Vector3.One)[0]);   // degenerate polygon: unlit
    }

    [Fact]
    public void GouraudFill_InterpolatesTheLight_AndIsIgnoredWithoutShade()
    {
        ScreenVertex V(double x, double y, double l) => new(x, y, 0, 0) { L = l };
        ReadOnlySpan<ScreenVertex> quad = [V(0, 0, 0.5), V(100, 0, 1.5), V(100, 10, 1.5), V(0, 10, 0.5)];
        var shaded = new FrameBuffer(100, 10); shaded.Clear(0, 0, 0);
        EnhancedRasterizer.Fill(shaded, quad, null, 0x808080, false, 0, 0, new FillContext { Shade = true });
        var plain = new FrameBuffer(100, 10); plain.Clear(0, 0, 0);
        EnhancedRasterizer.Fill(plain, quad, null, 0x808080, false, 0, 0, new FillContext());
        int prev = -1;
        for (int x = 0; x < 100; x++)
        {
            int v = shaded.Rgba[(5 * 100 + x) * 4];
            Assert.True(v >= prev, $"x={x}: {v} < {prev}");
            prev = v;
            Assert.Equal(0x80, plain.Rgba[(5 * 100 + x) * 4]);
        }
        Assert.InRange((int)shaded.Rgba[(5 * 100 + 1) * 4], 62, 72);       // ≈ 0.5·128
        Assert.InRange((int)shaded.Rgba[(5 * 100 + 98) * 4], 183, 195);    // ≈ 1.5·128
        Assert.InRange((int)shaded.Rgba[(5 * 100 + 50) * 4], 124, 132);    // the middle keeps the colour
    }

    [Fact]
    public void GouraudFill_TexturedAndFogged_MultipliesTheTexelAndClamps()
    {
        ScreenVertex V(double x, double y, double l) => new(x, y, 0.5, 0.5) { L = l };
        var tex = new Texture(1, 1, [0xC0A060u], [false]);
        ReadOnlySpan<ScreenVertex> quad = [V(0, 0, 2), V(20, 0, 2), V(20, 20, 2), V(0, 20, 2)];
        var fb = new FrameBuffer(20, 20); fb.Clear(0, 0, 0);
        EnhancedRasterizer.Fill(fb, quad, tex, 0, false, 0, 0, new FillContext { Shade = true });
        int o = (10 * 20 + 10) * 4;
        Assert.Equal((255, 255, 0xC0), (fb.Rgba[o], fb.Rgba[o + 1], fb.Rgba[o + 2]));   // 0xC0·2 and 0xA0·2 clamp, 0x60·2 = 0xC0
    }

    // ================= display filters =================

    static FrameBuffer Solid(int w, int h, byte r, byte g, byte b) { var f = new FrameBuffer(w, h); f.Clear(r, g, b); return f; }

    static FrameBuffer Step(int w, int h, int edgeX)
    {
        var f = new FrameBuffer(w, h); f.Clear(0, 0, 0);
        for (int y = 0; y < h; y++) for (int x = edgeX; x < w; x++) f.Set(x, y, 0xFFFFFF);
        return f;
    }

    static double Luma(FrameBuffer fb)
    {
        double s = 0;
        for (int i = 0; i < fb.Rgba.Length; i += 4) s += 0.299 * fb.Rgba[i] + 0.587 * fb.Rgba[i + 1] + 0.114 * fb.Rgba[i + 2];
        return s / (fb.Rgba.Length / 4);
    }

    static int MidTones(FrameBuffer fb, int y) { int n = 0; for (int x = 0; x < fb.Width; x++) { int v = fb.Rgba[(y * fb.Width + x) * 4]; if (v is > 12 and < 243) n++; } return n; }

    [Fact]
    public void Easu_ConstantImage_StaysConstant_AndHasTheRequestedSize()
    {
        var o = DisplayFilters.Easu(Solid(16, 12, 40, 120, 200), 70, 50);
        Assert.Equal((70, 50), (o.Width, o.Height));
        for (int i = 0; i < o.Rgba.Length; i += 4) Assert.Equal((40, 120, 200, 255), (o.Rgba[i], o.Rgba[i + 1], o.Rgba[i + 2], o.Rgba[i + 3]));
    }

    [Fact]
    public void Easu_Edge_IsMonotone_NoOvershoot_AndSharperThanBilinear()
    {
        var src = Step(16, 8, 8);
        var easu = DisplayFilters.Easu(src, 64, 32); var bil = DisplayFilters.Bilinear(src, 64, 32);
        for (int y = 0; y < 32; y++)
        {
            int prev = 0;
            for (int x = 0; x < 64; x++)
            {
                int v = easu.Rgba[(y * 64 + x) * 4];
                Assert.True(v >= prev, $"row {y} x {x}: {v} after {prev}");   // monotone: no ringing
                prev = v;
            }
        }
        Assert.True(MidTones(easu, 16) < MidTones(bil, 16), $"easu {MidTones(easu, 16)} bilinear {MidTones(bil, 16)}");
        Assert.Equal(0, easu.Rgba[(16 * 64 + 5) * 4]); Assert.Equal(255, easu.Rgba[(16 * 64 + 60) * 4]);
    }

    [Fact]
    public void Easu_DiagonalEdge_HasFewerBlendedPixelsThanBilinear_AndKeepsTheMeanLevel()
    {
        var src = new FrameBuffer(24, 24); src.Clear(0, 0, 0);
        for (int y = 0; y < 24; y++) for (int x = 0; x < 24; x++) if (x > y) src.Set(x, y, 0xFFFFFF);
        var easu = DisplayFilters.Easu(src, 96, 96); var bil = DisplayFilters.Bilinear(src, 96, 96);
        int Blend(FrameBuffer f) { int n = 0; for (int i = 0; i < f.Rgba.Length; i += 4) if (f.Rgba[i] is > 20 and < 235) n++; return n; }
        Assert.True(Blend(easu) < Blend(bil) * 0.85, $"easu {Blend(easu)} bilinear {Blend(bil)}");
        Assert.InRange(Luma(easu) - Luma(bil), -3, 3);
    }

    [Fact]
    public void Rcas_ConstantUnchanged_EdgeSteeper_StaysInRange()
    {
        var c = DisplayFilters.Rcas(Solid(10, 10, 90, 90, 90));
        Assert.All(Enumerable.Range(0, 100), i => Assert.Equal(90, c.Rgba[i * 4]));
        // a soft ramp 40 / 100 / 160 / 220 across the edge
        var f = new FrameBuffer(8, 3);
        byte[] ramp = [40, 40, 40, 100, 160, 220, 220, 220];
        for (int y = 0; y < 3; y++) for (int x = 0; x < 8; x++) f.Set(x, y, (uint)(ramp[x] << 16 | ramp[x] << 8 | ramp[x]));
        var o = DisplayFilters.Rcas(f, sharpness: 0f);
        int At(int x) => o.Rgba[(1 * 8 + x) * 4];
        // the kinks at both ends of the ramp are steepened (dark side darker, light side lighter), the straight part stays
        Assert.True(At(2) < ramp[2] - 4 && At(5) > ramp[5] + 4, $"{At(2)} {At(5)}");
        Assert.InRange(At(3), ramp[3] - 2, ramp[3] + 2); Assert.InRange(At(4), ramp[4] - 2, ramp[4] + 2);
        Assert.True(At(5) - At(2) > ramp[5] - ramp[2] + 8, "edge contrast grows");
        // sharpness in stops: higher = weaker
        var weak = DisplayFilters.Rcas(f, sharpness: 2f);
        Assert.True(weak.Rgba[(1 * 8 + 2) * 4] > At(2) && weak.Rgba[(1 * 8 + 2) * 4] <= ramp[2]);
    }

    [Fact]
    public void Rcas_NeverLeavesTheByteRange_OnHardEdges()
    {
        var o = DisplayFilters.Rcas(Step(12, 6, 6), 0f);
        Assert.Equal(0, o.Rgba[(3 * 12 + 2) * 4]); Assert.Equal(255, o.Rgba[(3 * 12 + 9) * 4]);
        Assert.Equal(255, o.Rgba[3]);
    }

    [Fact]
    public void Crt_MakesScanlines_KeepsTheMeanLevel_AndZeroStrengthIsAPlainScale()
    {
        var src = Solid(32, 16, 128, 128, 128);
        var o = DisplayFilters.Crt(src, 96, 64, scanline: 0.5f, mask: 0f);
        // output rows 4 per source row: the middle rows of a source row are brighter than the gap rows between two source rows
        int Row(int y) => o.Rgba[(y * 96 + 10) * 4];
        Assert.True(Row(1) > Row(0) + 20 && Row(2) > Row(3) + 20, $"{Row(0)} {Row(1)} {Row(2)} {Row(3)}");
        Assert.Equal(Row(0), Row(4)); Assert.Equal(Row(1), Row(5));            // periodic with the source row
        Assert.InRange(Luma(o) / Luma(src), 0.93, 1.07);                          // brightness compensated
        var plain = DisplayFilters.Crt(src, 96, 64, scanline: 0f, mask: 0f);
        Assert.All(Enumerable.Range(0, 96 * 64), i => Assert.InRange((int)plain.Rgba[i * 4], 127, 129));
        // the aperture grille tints neighbouring columns differently
        var g = DisplayFilters.Crt(src, 96, 64, scanline: 0f, mask: 0.4f);
        Assert.True(g.Rgba[(10 * 96 + 0) * 4] > g.Rgba[(10 * 96 + 0) * 4 + 1] && g.Rgba[(10 * 96 + 1) * 4 + 1] > g.Rgba[(10 * 96 + 1) * 4]);
    }

    [Fact]
    public void DisplayFilterApply_DispatchesAndParses()
    {
        var src = Step(16, 8, 8);
        foreach (var kind in new[] { DisplayFilterKind.Sharp, DisplayFilterKind.Fsr, DisplayFilterKind.Crt })
        {
            var o = DisplayFilters.Apply(kind, src, 64, 48);
            Assert.Equal((64, 48), (o.Width, o.Height));
            Assert.Equal(kind, DisplayFilterKinds.Parse(kind.Name()));
        }
        Assert.Null(DisplayFilterKinds.Parse("hqx"));
        Assert.Equal(DisplayFilterKind.Sharp, DisplayFilterKind.Crt.Next());
        Assert.NotEqual(DisplayFilters.Apply(DisplayFilterKind.Fsr, src, 64, 48).Sha256(), DisplayFilters.Apply(DisplayFilterKind.Sharp, src, 64, 48).Sha256());
    }

    // ================= z-sort range =================

    [Fact]
    public void ZSort_Extended_KeepsPolygonsBeyondTheOriginalLimit_AndCountsTheDropped()
    {
        var normal = new ZSort<int>(); var wide = new ZSort<int>(ZSort<int>.ExtendedBuckets);
        foreach (double z in new[] { 100.0, 500, 615, 700, 900, 1200 })
        {
            Assert.Equal(z < 610, normal.Insert(z, 1));   // bucket = 2·(z + 10 m) < 1240
            Assert.True(wide.Insert(z, 1), $"z {z}");
        }
        Assert.Equal(4, normal.FarDropped); Assert.Equal(0, wide.FarDropped);
        Assert.False(wide.Insert(1300, 1)); Assert.Equal(1, wide.FarDropped);
        Assert.Equal(6, wide.FarToNear().Count());
        Assert.True(wide.FarToNear().Select(t => t.Bucket).SequenceEqual(wide.FarToNear().Select(t => t.Bucket).OrderByDescending(b => b)));   // far → near
        normal.Clear(); Assert.Equal(0, normal.FarDropped);
        Assert.Equal(ZSort<int>.BucketFor(100), ZSort<int>.BucketFor(100, 0, ZSort<int>.ExtendedBuckets));   // the same scale of 0.5 m
    }

    [Fact]
    public void ViewRange_Extended_ScalesObjectsBlocksAndCars_NotTheLodThresholds()
    {
        var m = ViewRange.Main; var e = m.Extended();
        Assert.Equal(1000f, e.ObjectRangeMetres); Assert.Equal(1000f, e.BlockRangeMetres); Assert.Equal(400f, e.CarDepthMetres);
        Assert.Equal(m.LodNear, e.LodNear); Assert.Equal(m.CullLimit, e.CullLimit);
        Assert.True(e.ObjectRangeMetres < ZSort<int>.ExtendedBuckets / 2f);         // fits into the wider sort
    }

    // ================= settings =================

    [Fact]
    public void LookSettings_Defaults_CommandLine_PortJson_AndNotice()
    {
        var d = GraphicsSettings.Resolve(DesktopOptions.Parse([]), null);
        Assert.True(d.CarLight); Assert.True(d.CarShadow); Assert.False(d.ExtendedRange); Assert.Equal(DisplayFilterKind.Sharp, d.Display);
        var o = DesktopOptions.Parse(["--gfx-light", "off", "--gfx-shadow", "off", "--gfx-range", "extended", "--gfx-display", "crt"]);
        var s = GraphicsSettings.Resolve(o, null);
        Assert.False(s.CarLight); Assert.False(s.CarShadow); Assert.True(s.ExtendedRange); Assert.Equal(DisplayFilterKind.Crt, s.Display);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-display", "hqx"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-range", "huge"]));
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-light", "maybe"]));
        string path = Path.Combine(Path.GetTempPath(), $"port_{Guid.NewGuid():N}.json");
        try
        {
            var port = new PortSettings(); s.WriteTo(port); Assert.Null(port.Save(path));
            Assert.Equal(s, GraphicsSettings.Resolve(DesktopOptions.Parse([]), PortSettings.Load(path)));
            Assert.Equal(DisplayFilterKind.Fsr, GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-display", "fsr"]), PortSettings.Load(path)).Display);   // CLI wins
        }
        finally { File.Delete(path); }

        var r = new RenderOptions();
        d.ApplyTo(r); Assert.True(r.CarLight && r.CarShadow && !r.ExtendedRange && r.EnhancedFill);
        s.ApplyTo(r); Assert.False(r.CarLight); Assert.False(r.CarShadow); Assert.True(r.ExtendedRange);
        (d with { Mode = GraphicsMode.Vanilla, ExtendedRange = true }).ApplyTo(r);
        Assert.False(r.CarLight || r.CarShadow || r.ExtendedRange || r.EnhancedFill);   // Vanilla = the original renderer

        // the F8 notice lists the new items
        var all = d with { ExtendedRange = true, Display = DisplayFilterKind.Fsr };
        string text = GfxNotice.ForMode(all, 1).Detail!;
        Assert.Contains("CAR LIGHT", text); Assert.Contains("SHADOW", text); Assert.Contains("RANGE X2", text); Assert.Contains("DISPLAY FSR", text);
        Assert.DoesNotContain("SHADOW", GfxNotice.ForMode(d with { CarShadow = false }, 1).Detail!);
        Assert.Equal("ORIGINAL RENDERER", GfxNotice.ForMode(all.Toggled(), 1).Detail);
        Assert.Equal("GFX = VANILLA", GfxNotice.ForMode(all.Toggled(), 1).Title);
        Assert.Equal("GFX DISPLAY = FSR", GfxNotice.ForDisplay(all, 1).Title);
        Assert.Equal(DisplayFilterKind.Crt, all.NextDisplay().Display);
        Assert.Contains("VANILLA", GfxNotice.ForDisplay(all.Toggled(), 1).Detail);
    }

    // ================= with game data: the real cars, shadow, range =================

    sealed record CarShot(RaceScene Scene, CarInstance Player);

    static CarShot PrepareCarScene()
    {
        var scene = new RaceScene("replay");
        var d = new RaceDrive(scene, countdown: false, automatic: true);
        var gas = new DriveKeys(true, false, false, false, false, false, false);
        for (int i = 0; i < 40; i++) d.Frame(1.0 / 32, gas);
        scene.PresentPoses(1);
        return new CarShot(scene, scene.RenderCars.First());
    }

    /// <summary>Camera at (right, up, back) from the car's centre, looking at it.</summary>
    static Camera Around(CarInstance car, float right, float up, float back, int w, int h)
    {
        var cam = new Camera(w, h);
        cam.Position = car.Position + car.Right * right + car.Up * up - car.Forward * back;
        cam.LookAlong(car.Position + car.Up * 0.5f - cam.Position, Vector3.UnitY);
        return cam;
    }

    static FrameBuffer Shot(CarShot s, RenderOptions opt, float right, float up, float back, int k = 2, bool playerOnly = false)
    {
        var fb = new FrameBuffer(640 * k, 480 * k);
        s.Scene.Track.Render(fb, Around(s.Player, right, up, back, fb.Width, fb.Height), opt, playerOnly ? [s.Player] : s.Scene.RenderCars);
        return fb;
    }

    static RenderOptions Look(bool light = false, bool shadow = false, bool range = false) =>
        new() { PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, PixelFog = true, CarLight = light, CarShadow = shadow, ExtendedRange = range };

    [GameDataFact]
    public void CarLight_ChangesOnlyTheCarPixels_AddsVariation_AndKeepsTheOutline()
    {
        var s = PrepareCarScene();
        var off = Shot(s, Look(), 3.2f, 1.2f, 3f); var on = Shot(s, Look(light: true), 3.2f, 1.2f, 3f);
        int changed = 0, total = off.Width * off.Height, maxDiff = 0;
        double lumOff = 0, lumOn = 0;
        for (int i = 0; i < off.Rgba.Length; i += 4)
        {
            int d = Math.Abs(off.Rgba[i] - on.Rgba[i]) + Math.Abs(off.Rgba[i + 1] - on.Rgba[i + 1]) + Math.Abs(off.Rgba[i + 2] - on.Rgba[i + 2]);
            if (d > 0) { changed++; lumOff += off.Rgba[i + 1]; lumOn += on.Rgba[i + 1]; }
            maxDiff = Math.Max(maxDiff, d);
        }
        output.WriteLine($"changed {changed} of {total} px ({100.0 * changed / total:F1} %), max channel-sum diff {maxDiff}");
        Assert.InRange(changed, total / 200, total / 3);                            // the car, not the world
        Assert.True(maxDiff > 40);                                                  // a visible change
        Assert.InRange(lumOn / lumOff, 0.7, 1.25);                                  // the light keeps the overall brightness of the paint
        // the road far from the car is bit-identical
        Assert.Equal(off.Get(5, 5), on.Get(5, 5)); Assert.Equal(off.Get(off.Width - 5, off.Height / 3), on.Get(off.Width - 5, off.Height / 3));
    }

    [GameDataFact]
    public void CarShadow_OnlyDarkens_NearTheCar_CentreDarkerThanTheEdge_AndFadesWithFog()
    {
        var s = PrepareCarScene();
        var off = Shot(s, Look(), 0, 3.5f, 5.5f, 2, true); var on = Shot(s, Look(shadow: true), 0, 3.5f, 5.5f, 2, true);
        int darker = 0, brighter = 0, minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < off.Height; y++)
            for (int x = 0; x < off.Width; x++)
            {
                int o = (y * off.Width + x) * 4, a = off.Rgba[o] + off.Rgba[o + 1] + off.Rgba[o + 2], b = on.Rgba[o] + on.Rgba[o + 1] + on.Rgba[o + 2];
                if (b > a) brighter++;
                if (b < a) { darker++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            }
        output.WriteLine($"darker {darker}, brighter {brighter}, bbox {minX}..{maxX} x {minY}..{maxY}");
        Assert.Equal(0, brighter);
        Assert.True(darker > 800, $"{darker}");
        Assert.True(maxX - minX < off.Width / 2 && maxY - minY < off.Height / 2, "the shadow is local");
        // darkest at the middle of the blob: compare the strip through the centre of the darkened area with its border
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        double Ratio(int x, int y)
        {
            int o = (y * off.Width + x) * 4;
            return (on.Rgba[o] + on.Rgba[o + 1] + on.Rgba[o + 2] + 1.0) / (off.Rgba[o] + off.Rgba[o + 1] + off.Rgba[o + 2] + 1.0);
        }
        // the car covers the middle of the blob, so sample along the blob's horizontal extent at the lower edge of the car (in front of it)
        var ratios = new List<double>();
        for (int x = minX; x <= maxX; x++) ratios.Add(Ratio(x, Math.Min(maxY, cy + (maxY - cy) * 3 / 4)));
        output.WriteLine($"ratio profile at y={cy}: min {ratios.Min():F2}, ends {ratios.First():F2}/{ratios.Last():F2}");
        Assert.True(ratios.Min() < 0.88, $"darkest ratio {ratios.Min():F2}");
        Assert.True(ratios.First() > ratios.Min() + 0.04 && ratios.Last() > ratios.Min() + 0.04, "the shadow is softer towards its edge");
        // strength 0 / full fog: nothing is drawn
        var none = new FrameBuffer(64, 64); none.Clear(100, 100, 100);
        Assert.Equal(0, CarShadow.Draw(none, new Camera(64, 64), s.Player, fog: 16));
        Assert.All(none.Rgba.Where((_, i) => i % 4 == 0), v => Assert.Equal(100, v));
    }

    [GameDataFact]
    public void CarShadow_LiesOnTheGround_UnderTheWheels()
    {
        var s = PrepareCarScene();
        Assert.True(CarShadow.Footprint(s.Player, out var centre, out float hx, out float hz));
        var bounds = s.Player.Model.LocalBounds(s.Player.Lod)!.Value;
        // the plane of the tyres: the lowest point of the model, along the car's up axis, a couple of centimetres under the centre of the blob
        float below = Vector3.Dot(centre - s.Player.Position, s.Player.Up);
        Assert.InRange(below, bounds.Min.Y - 0.05f, bounds.Min.Y + 0.1f);
        Assert.InRange(hx, 0.6f, 2.0f); Assert.InRange(hz, 1.5f, 3.5f); Assert.True(hz > hx);   // longer than wide
    }

    /// <summary>The car lifted by <paramref name="lift"/> m along the road normal and tilted (degrees) about its own right / forward axis, keeping the road plane.</summary>
    static CarInstance Lifted(CarInstance car, float lift, float pitchDeg = 0, float rollDeg = 0)
    {
        var n = car.Ground!.Value.Normal;
        var q = Quaternion.CreateFromAxisAngle(car.Right, pitchDeg * MathF.PI / 180) * Quaternion.CreateFromAxisAngle(car.Forward, rollDeg * MathF.PI / 180);
        return new CarInstance(car.Model, car.Position + n * lift, Vector3.Transform(car.Right, q), Vector3.Transform(car.Up, q), Vector3.Transform(car.Forward, q)) { Ground = car.Ground };
    }

    static float OnRoad(CarInstance car, Vector3 p) => Vector3.Dot(p - car.Ground!.Value.Point, car.Ground!.Value.Normal);

    [GameDataFact]
    public void CarShadow_OnTheRoad_LooksLikeTheTyrePlaneShadow()
    {
        var s = PrepareCarScene();
        Assert.NotNull(s.Player.Ground);
        Assert.True(CarShadow.TryShape(s.Player, out var onRoad));
        Assert.True(CarShadow.TryShape(s.Player with { Ground = null }, out var inCarFrame));
        output.WriteLine($"centre shift {Vector3.Distance(onRoad.Centre, inCarFrame.Centre):F3} m, fade {onRoad.Fade:F3}");
        Assert.True(Vector3.Distance(onRoad.Centre, inCarFrame.Centre) < 0.15f);
        Assert.InRange(onRoad.Fade, 0.95f, 1f);
        Assert.InRange(Math.Abs(onRoad.HalfX / inCarFrame.HalfX - 1), 0, 0.03f);
        Assert.True(Vector3.Dot(onRoad.Forward, inCarFrame.Forward) > 0.99f);
    }

    [GameDataFact]
    public void CarShadow_CarInTheAir_StaysOnTheRoad_WeakerAndWider_AndVanishesHigh()
    {
        var s = PrepareCarScene();
        Assert.True(CarShadow.TryShape(s.Player, out var ground));
        float prevFade = ground.Fade, prevHalfX = ground.HalfX;
        foreach (float lift in new[] { 0.5f, 1.5f, 3f })
        {
            var air = Lifted(s.Player, lift);
            Assert.True(CarShadow.TryShape(air, out var sh));
            float onPlane = OnRoad(air, sh.Centre);
            // centre on the road plane (a couple of cm for the road's unevenness) and not carried up with the car; same spot along the road
            Assert.InRange(onPlane, 0f, 0.05f);
            Assert.True(Vector3.Distance(sh.Centre - s.Player.Ground!.Value.Normal * onPlane, ground.Centre - s.Player.Ground!.Value.Normal * OnRoad(s.Player, ground.Centre)) < 0.02f);
            Assert.True(sh.Fade < prevFade, $"lift {lift}: fade {sh.Fade} >= {prevFade}");
            Assert.True(sh.HalfX > prevHalfX);
            prevFade = sh.Fade; prevHalfX = sh.HalfX;
        }
        // at / above the fade height nothing is drawn
        var high = Lifted(s.Player, CarShadow.FadeHeight + 0.1f);
        Assert.False(CarShadow.TryShape(high, out _));
        var fb = new FrameBuffer(64, 64); fb.Clear(100, 100, 100);
        Assert.Equal(0, CarShadow.Draw(fb, new Camera(64, 64), high, fog: 0));
        Assert.All(fb.Rgba.Where((_, i) => i % 4 == 0), v => Assert.Equal(100, v));
    }

    [GameDataFact]
    public void CarShadow_PitchAndRollInTheAir_DoNotTiltTheShadow()
    {
        var s = PrepareCarScene();
        var flat = Lifted(s.Player, 1f);
        Assert.True(CarShadow.TryShape(flat, out var a));
        foreach (var (pitch, roll) in new[] { (35f, 0f), (0f, 40f), (-25f, 30f) })
        {
            var tilted = Lifted(s.Player, 1f, pitch, roll);
            Assert.True(CarShadow.TryShape(tilted, out var b));
            var n = s.Player.Ground!.Value.Normal;
            output.WriteLine($"pitch {pitch} roll {roll}: axes.n {Vector3.Dot(b.Right, n):F4} {Vector3.Dot(b.Forward, n):F4}, centre on road {OnRoad(tilted, b.Centre):F3}");
            Assert.InRange(Math.Abs(Vector3.Dot(b.Right, n)), 0, 1e-3f);          // the shadow lies in the road plane
            Assert.InRange(Math.Abs(Vector3.Dot(b.Forward, n)), 0, 1e-3f);
            Assert.InRange(OnRoad(tilted, b.Centre), 0f, 0.05f);
            Assert.InRange(Math.Abs(Vector3.Dot(b.Forward, a.Forward)), 0.999f, 1.001f);   // heading kept (roll/pitch about the car's own axes keep its projection)
        }
        // yaw does turn it: the same footprint along the new heading
        var yawed = new CarInstance(flat.Model, flat.Position, -flat.Right, flat.Up, -flat.Forward) { Ground = flat.Ground };
        Assert.True(CarShadow.TryShape(yawed, out var c));
        Assert.True(Math.Abs(Vector3.Dot(c.Forward, a.Forward)) > 0.999f);
        var side = new CarInstance(flat.Model, flat.Position, flat.Forward, flat.Up, -flat.Right) { Ground = flat.Ground };
        Assert.True(CarShadow.TryShape(side, out var d));
        Assert.True(Math.Abs(Vector3.Dot(d.Forward, a.Forward)) < 0.01f);
    }

    [GameDataFact]
    public void CarShadow_Drawn_StaysAtTheSpotOnTheRoad_WhenTheCarJumps_AndGetsWeaker()
    {
        var s = PrepareCarScene();
        var cam = Around(s.Player, 0, 6f, 7f, 320, 240);
        (double cx, double cy, double dark) Blob(CarInstance car)
        {
            var fb = new FrameBuffer(320, 240); fb.Clear(200, 200, 200);
            CarShadow.Draw(fb, cam, car, fog: 0);
            double n = 0, sx = 0, sy = 0, sum = 0;
            for (int y = 0; y < fb.Height; y++)
                for (int x = 0; x < fb.Width; x++)
                {
                    double d = 200 - fb.Rgba[(y * fb.Width + x) * 4];
                    n += d > 0 ? 1 : 0; sx += d * x; sy += d * y; sum += d;
                }
            return sum > 0 ? (sx / sum, sy / sum, sum) : (0, 0, 0);
        }
        var g = Blob(s.Player); var a = Blob(Lifted(s.Player, 1.5f, 20, 15));
        output.WriteLine($"ground ({g.cx:F1},{g.cy:F1}) dark {g.dark:F0}; air ({a.cx:F1},{a.cy:F1}) dark {a.dark:F0}");
        Assert.True(g.dark > 1000);
        Assert.True(a.dark < g.dark * 0.9 && a.dark > 0);
        // the car tilts about the box centre (0.59 m above the road): the tyre plane, hence the blob, moves a little sideways
        Assert.InRange(Math.Abs(a.cx - g.cx), 0, 6); Assert.InRange(Math.Abs(a.cy - g.cy), 0, 6);
    }

    [GameDataFact]
    public void RaceScene_GroundOf_IsTheBottomOfTheBoxOfTheSim_AndNoneOnRails()
    {
        // flat road, the bottom of the box 2 m above it, rising at 8 m/s; box half height 0.5 m
        var v = new CarView { Pos = new Vec3(10 * Fx.One, 3 * Fx.One, 4 * Fx.One), Vel = new Vec3(0, 8 * Fx.One, 0), RoadFrame = Mat3.Identity,
            HeightAboveRoad = 2 * Fx.One, HalfHeight = Fx.One / 2 };
        var g = RaceScene.GroundOf(v)!.Value;
        Assert.Equal(new Vector3(0, 1, 0), g.Normal);
        // start of the step (before the position was integrated): y = 3 - 8/32 = 2.75; the plane is HalfHeight + HeightAboveRoad = 2.5 m under it
        Assert.Equal(2.75f - 0.5f - 2f, g.Point.Y, 3);
        Assert.Null(RaceScene.GroundOf(v with { Rail = 1 }));
        Assert.Null(RaceScene.GroundOf(v with { RoadFrame = new Mat3() }));
        // on the wheels: the plane lies half a box height under the centre (Newton.PlaneDist)
        var ground = RaceScene.GroundOf(v with { HeightAboveRoad = 0, Vel = new Vec3() })!.Value;
        Assert.Equal(3f - 0.5f, ground.Point.Y, 3);
        // a tilted road: the normal comes from the road frame, not from the car or the world up
        var slope = new Mat3(Fx.One, 0, 0, 0, Fx.One * 8 / 10, Fx.One * 6 / 10, 0, -Fx.One * 6 / 10, Fx.One * 8 / 10);
        var gs = RaceScene.GroundOf(v with { RoadFrame = slope, HeightAboveRoad = 0, Vel = new Vec3() })!.Value;
        Assert.Equal(0.6f, gs.Normal.Z, 2); Assert.Equal(0.8f, gs.Normal.Y, 2);
    }

    /// <summary>The original draws the model around CarObj.Pos, the centre of the collision box (body node Y = 0, wheel nodes Y − parts[1].Y; RE): [ref:2748]
    /// the lowest drawn vertex lies at the road plane of the sim, Pos − n·(HalfHeight + HeightAboveRoad), not half a box height above it.</summary>
    [GameDataFact]
    public void Car_IsDrawnOnTheRoadPlaneOfTheSim_NotHangingAboveIt()
    {
        var s = PrepareCarScene();
        var view = (CarView)typeof(RaceScene).GetMethod("InstanceView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(s.Scene, [s.Scene.Cars.ToList().IndexOf(s.Player)])!;
        var n = RaceScene.ToWorld(view.RoadFrame.Row1); n = Vector3.Normalize(n);
        var plane = s.Player.Position - n * ((view.HalfHeight + view.HeightAboveRoad) / (float)Fx.One);
        float lowest = float.MaxValue;
        foreach (var pl in s.Player.Polys()) foreach (var v in pl.P) lowest = Math.Min(lowest, Vector3.Dot(v - plane, n));
        output.WriteLine($"lowest drawn vertex above the sim road plane: {lowest:F4} m (HalfHeight {view.HalfHeight / (float)Fx.One:F3})");
        Assert.InRange(lowest, -0.05f, 0.05f);
        // the shadow plane is that same plane
        Assert.True(Vector3.Dot(s.Player.Ground!.Value.Point - plane, n) is > -0.02f and < 0.02f, "the shadow plane is the sim road plane");
    }

    [GameDataFact]
    public void ExtendedRange_NothingVanishesBeyondTheOriginalLimit_AndMoreIsDrawn()
    {
        var tr = new TrackRenderer(2);
        int more = 0, probed = 0;
        for (int slice = 0; slice < tr.Slices.Length; slice += 37)
        {
            var cam = Camera.FromSlice(tr.Slices[slice], 1.5f, 0f, 640, 480);
            var fb = new FrameBuffer(640, 480);
            tr.Render(fb, cam, new RenderOptions { Fog = false });
            int e0 = tr.LastEmitted; int d0 = tr.LastDrawn;
            var ext = new FrameBuffer(640, 480);
            tr.Render(ext, cam, new RenderOptions { Fog = false, ExtendedRange = true });
            Assert.Equal(0, tr.LastFarDropped);
            Assert.True(tr.LastEmitted >= e0, $"slice {slice}: {tr.LastEmitted} < {e0}");
            if (tr.LastEmitted > e0) more++;
            probed++;
        }
        output.WriteLine($"{more} of {probed} cameras emit more polygons in the Extended range");
        Assert.True(more > probed / 4, $"{more}/{probed}");
    }

    [GameDataFact]
    public void ExtendedRange_DrawsMoreOfTheHorizon_WithoutFog_AndTheMirrorKeepsItsRange()
    {
        var tr = new TrackRenderer(2);
        // pick the camera where the extended range adds the most polygons
        int best = 0, gain = -1;
        for (int slice = 0; slice < tr.Slices.Length; slice += 23)
        {
            var cam = Camera.FromSlice(tr.Slices[slice], 1.5f, 0f);
            tr.Render(new FrameBuffer(640, 480), cam, new RenderOptions { Fog = false }); int a = tr.LastEmitted;
            tr.Render(new FrameBuffer(640, 480), cam, new RenderOptions { Fog = false, ExtendedRange = true });
            if (tr.LastEmitted - a > gain) { gain = tr.LastEmitted - a; best = slice; }
        }
        Assert.True(gain > 0);
        var camBest = Camera.FromSlice(tr.Slices[best], 1.5f, 0f);
        var plain = new FrameBuffer(640, 480); tr.Render(plain, camBest, new RenderOptions { Fog = false });
        var wide = new FrameBuffer(640, 480); tr.Render(wide, camBest, new RenderOptions { Fog = false, ExtendedRange = true });
        int differing = 0;
        for (int i = 0; i < plain.Rgba.Length; i += 4) if (plain.Rgba[i] != wide.Rgba[i] || plain.Rgba[i + 1] != wide.Rgba[i + 1] || plain.Rgba[i + 2] != wide.Rgba[i + 2]) differing++;
        output.WriteLine($"slice {best}: +{gain} polygons, {differing} pixels differ");
        Assert.True(differing > 0);
        // the rear mirror is never extended: the same picture
        var m0 = new FrameBuffer(640, 70); var m1 = new FrameBuffer(640, 70);
        var mc = new Camera(640, 70);
        mc.Position = camBest.Position; mc.LookAlong(-camBest.Forward, Vector3.UnitY);
        tr.Render(m0, mc, new RenderOptions { Fog = false }, rearMirror: true, range: ViewRange.Mirror);
        tr.Render(m1, mc, new RenderOptions { Fog = false, ExtendedRange = true }, rearMirror: true, range: ViewRange.Mirror);
        Assert.Equal(m0.Sha256(), m1.Sha256());
    }

    [GameDataFact]
    public void Vanilla_IgnoresTheNewItems_ByteForByte()
    {
        // a Vanilla run never enables the new items (GraphicsSettings.ApplyTo), so the renderer's output equals the plain one
        var v = new GraphicsSettings(GraphicsMode.Vanilla, 0) { CarLight = true, CarShadow = true, ExtendedRange = true, Display = DisplayFilterKind.Crt };
        var opt = new RenderOptions(); v.ApplyTo(opt);
        var s = PrepareCarScene();
        var a = new FrameBuffer(640, 480); var b = new FrameBuffer(640, 480);
        s.Scene.Track.Render(a, Around(s.Player, 3, 1.2f, 3, 640, 480), opt, s.Scene.RenderCars);
        s.Scene.Track.Render(b, Around(s.Player, 3, 1.2f, 3, 640, 480), new RenderOptions(), s.Scene.RenderCars);
        Assert.Equal(b.Sha256(), a.Sha256());
    }

    // ================= previews and timings (only with NFS_GFX_CHECK_DIR) =================

    static FrameBuffer Side(FrameBuffer left, FrameBuffer right)
    {
        int h = Math.Max(left.Height, right.Height);
        var pair = new FrameBuffer(left.Width + right.Width + 8, h); pair.Clear(255, 0, 255);
        for (int y = 0; y < left.Height; y++) Buffer.BlockCopy(left.Rgba, y * left.Width * 4, pair.Rgba, y * pair.Width * 4, left.Width * 4);
        for (int y = 0; y < right.Height; y++) Buffer.BlockCopy(right.Rgba, y * right.Width * 4, pair.Rgba, (y * pair.Width + left.Width + 8) * 4, right.Width * 4);
        return pair;
    }

    [GameDataFact]
    public void Report_WritesLookPairs_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var s = PrepareCarScene();
        var vanilla = new RenderOptions();
        var shots = new (string Name, float R, float U, float B)[] { ("car_close", 2.6f, 1.1f, 2.4f), ("car_rear", 0.6f, 1.5f, 5f), ("car_front34", 3.4f, 1.3f, -3.5f), ("car_shadow", 0, 3.5f, 5.5f) };
        foreach (var (name, r, u, b) in shots)
        {
            var van = Compositor.UpscaleNearest(Shot(s, vanilla, r, u, b, 1), 2);
            var light = Shot(s, Look(light: true), r, u, b);
            var shadow = Shot(s, Look(shadow: true), r, u, b);
            var both = Shot(s, Look(light: true, shadow: true), r, u, b);
            var enhancedNo = Shot(s, Look(), r, u, b);
            Side(van, both).SavePng(Path.Combine(dir, $"pair3_{name}_vanilla_vs_enhanced.png"));
            Side(enhancedNo, light).SavePng(Path.Combine(dir, $"pair3_{name}_light_off_on.png"));
            Side(enhancedNo, shadow).SavePng(Path.Combine(dir, $"pair3_{name}_shadow_off_on.png"));
        }
        // far horizon: normal vs extended, fog off (the fog hides the difference on most tracks)
        foreach (int trackNo in new[] { 2, 4 })
        {
            var tr = new TrackRenderer(trackNo);
            int best = 0, gain = -1;
            for (int slice = 0; slice < tr.Slices.Length; slice += 11)
            {
                var cam = Camera.FromSlice(tr.Slices[slice], 1.5f, 0f);
                var f0 = new FrameBuffer(640, 480); var g1 = new FrameBuffer(640, 480);
                tr.Render(f0, cam, new RenderOptions { Fog = false }); int a = tr.LastEmitted;
                tr.Render(g1, cam, new RenderOptions { Fog = false, ExtendedRange = true });
                int diff = 0; for (int i = 0; i < f0.Rgba.Length; i += 4) if (f0.Rgba[i + 1] != g1.Rgba[i + 1]) diff++;
                if (diff > gain) { gain = diff; best = slice; }
            }
            foreach (bool fog in new[] { false, true })
            {
                var cam = Camera.FromSlice(tr.Slices[best], 1.5f, 0f, 1280, 960);
                var a = new FrameBuffer(1280, 960); var e = new FrameBuffer(1280, 960);
                var o = Look(); o.Fog = fog; var oe = Look(range: true); oe.Fog = fog;
                tr.Render(a, cam, o); tr.Render(e, cam, oe);
                Side(a, e).SavePng(Path.Combine(dir, $"pair3_range_tr{trackNo:00}_slice{best}_fog{(fog ? "on" : "off")}.png"));
                output.WriteLine($"track {trackNo} slice {best}: {gain} px differ, far dropped {tr.LastFarDropped}");
            }
        }
        // display filters on a track frame: source 1280×960 -> 1920×1440 window area
        var t = new FrameTargets(); t.Configure(GraphicsMode.Enhanced, 2, 1);
        var opt = Look(light: true, shadow: true);
        GfxScenes.Render("chase", t, opt);
        var frame = t.ComposeCpu();
        foreach (var kind in new[] { DisplayFilterKind.Sharp, DisplayFilterKind.Fsr, DisplayFilterKind.Crt })
            DisplayFilters.Apply(kind, frame, 1920, 1440).SavePng(Path.Combine(dir, $"display_chase_{kind.Name()}.png"));
        var t1 = new FrameTargets(); t1.Configure(GraphicsMode.Enhanced, 1, 1);
        GfxScenes.Render("chase", t1, new RenderOptions());
        var f1 = t1.ComposeCpu();
        foreach (var kind in new[] { DisplayFilterKind.Sharp, DisplayFilterKind.Fsr, DisplayFilterKind.Crt })
            DisplayFilters.Apply(kind, f1, 1920, 1440).SavePng(Path.Combine(dir, $"display_chase_k1_{kind.Name()}.png"));
    }

    [GameDataFact]
    public void Timing_LookItems_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_GFX_CHECK_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        var s = PrepareCarScene();
        var configs = new (string Name, RenderOptions Opt)[]
        {
            ("stage2 (all quality)", Look()), ("+light", Look(light: true)), ("+shadow", Look(shadow: true)), ("+range", Look(range: true)),
            ("+light+shadow (new default)", Look(light: true, shadow: true)), ("everything", Look(light: true, shadow: true, range: true)),
        };
        foreach (int k in new[] { 1, 2 })
            foreach (var c in configs)
            {
                var fb = new FrameBuffer(640 * k, 480 * k);
                var cam = Around(s.Player, 2.6f, 1.1f, 2.4f, fb.Width, fb.Height);
                var times = new List<double>();
                for (int i = 0; i < 18; i++)
                {
                    var sw = Stopwatch.StartNew();
                    s.Scene.Track.Render(fb, cam, c.Opt, s.Scene.RenderCars);
                    if (i >= 3) times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                lines.Add($"car close k{k} {c.Name,-28}: median {times[times.Count / 2]:F1} ms");
            }
        var frame = new FrameBuffer(1280, 960); frame.Clear(90, 120, 60);
        foreach (var kind in new[] { DisplayFilterKind.Fsr, DisplayFilterKind.Crt })
            foreach (var (w, h) in new[] { (1280, 960), (1920, 1440) })
            {
                var times = new List<double>();
                for (int i = 0; i < 6; i++)
                {
                    var sw = Stopwatch.StartNew();
                    DisplayFilters.Apply(kind, frame, w, h);
                    if (i >= 1) times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                lines.Add($"display {kind.Name()} 1280x960 -> {w}x{h}: median {times[times.Count / 2]:F1} ms");
            }
        File.WriteAllLines(Path.Combine(dir, "timing_look.txt"), lines);
        foreach (var l in lines) output.WriteLine(l);
    }
}
