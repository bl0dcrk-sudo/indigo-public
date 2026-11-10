using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;
using Xunit.Abstractions;

namespace NFS.Desktop.Tests;

/// <summary>Track polygon shading in the renderer ("Integration"): Enhanced option <see cref="RenderOptions.TrackShade"/>; [ref:2999]
/// Vanilla stays bit-identical.</summary>
public class TrackShadeRenderTests(ITestOutputHelper output)
{
    const int TrackNo = 6;

    static RenderOptions Enhanced(bool shade) =>
        new() { PerspectiveCorrect = true, Filter = TextureFilter.Trilinear, PixelFog = true, TrackShade = shade };

    static FrameBuffer Shot(TrackRenderer tr, int slice, RenderOptions opt, int k = 1)
    {
        var fb = new FrameBuffer(640 * k, 480 * k);
        var cam = Camera.FromSlice(tr.Slices[slice], 1.5f, 0f, fb.Width, fb.Height);
        tr.Render(fb, cam, opt);   // the view locator converges on the new slice over a frame: render twice, keep the second
        tr.Render(fb, cam, opt);
        return fb;
    }

    static (int Changed, double LumOff, double LumOn) Compare(FrameBuffer off, FrameBuffer on)
    {
        int changed = 0; double lo = 0, ln = 0;
        for (int i = 0; i < off.Rgba.Length; i += 4)
        {
            lo += off.Rgba[i] + off.Rgba[i + 1] + off.Rgba[i + 2]; ln += on.Rgba[i] + on.Rgba[i + 1] + on.Rgba[i + 2];
            if (off.Rgba[i] != on.Rgba[i] || off.Rgba[i + 1] != on.Rgba[i + 1] || off.Rgba[i + 2] != on.Rgba[i + 2]) changed++;
        }
        return (changed, lo, ln);
    }

    [GameDataFact]
    public void Vanilla_IgnoresTrackShade_ByteForByte()
    {
        var tr = new TrackRenderer(TrackNo);
        var g = new GraphicsSettings(GraphicsMode.Vanilla, 0) { TrackShade = true };
        var opt = new RenderOptions(); g.ApplyTo(opt);
        Assert.False(opt.TrackShade); Assert.False(opt.EnhancedFill);
        for (int slice = 0; slice < tr.Slices.Length; slice += 97)
            Assert.Equal(Shot(tr, slice, new RenderOptions()).Sha256(), Shot(tr, slice, opt).Sha256());
    }

    [GameDataFact]
    public void Enhanced_TrackShade_DarkensTheScene_AndOffEqualsTheOldEnhancedFrame()
    {
        // two renderers walk the same slices with the same calls: the view locator keeps state between frames, so one shared renderer would differ by history
        var tr = new TrackRenderer(TrackNo); var trOn = new TrackRenderer(TrackNo);
        int best = -1, bestChanged = 0; double ratio = 1;
        for (int slice = 0; slice < tr.Slices.Length; slice += 11)
        {
            var (changed, lo, ln) = Compare(Shot(tr, slice, Enhanced(false)), Shot(trOn, slice, Enhanced(true)));
            Assert.True(ln <= lo * 1.001, $"slice {slice}: shade must not brighten");
            if (changed > bestChanged) { bestChanged = changed; best = slice; ratio = ln / lo; }
        }
        output.WriteLine($"track {TrackNo} slice {best}: {bestChanged} px differ, luminance ratio {ratio:F3}");
        Assert.True(bestChanged > 640 * 480 / 100, "some slice must show visible shading");
        Assert.InRange(ratio, 0.3, 0.999);
        // switching the option off again gives back the plain Enhanced frame (no state leaks between frames)
        Shot(trOn, best, Enhanced(true));
        Assert.Equal(Shot(tr, best, Enhanced(false)).Sha256(), Shot(trOn, best, Enhanced(false)).Sha256());
    }

    [GameDataFact]
    public void Report_WritesTrackShadePairs_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("NFS_TRACKSHADE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        // fixed road-level views (the "best" slice of a sweep is often a tunnel or an odd camera); two renderers see the same history
        foreach (var (trackNo, slice) in new[] { (2, 120), (2, 400), (4, 300), (6, 300), (6, 600), (6, 900) })
        {
            var tr = new TrackRenderer(trackNo); var trOn = new TrackRenderer(trackNo);
            var off = Shot(tr, slice, Enhanced(false)); var on = Shot(trOn, slice, Enhanced(true));
            var (changed, lo, ln) = Compare(off, on);
            var pair = new FrameBuffer(off.Width * 2 + 8, off.Height); pair.Clear(255, 0, 255);
            for (int y = 0; y < off.Height; y++)
            {
                Buffer.BlockCopy(off.Rgba, y * off.Width * 4, pair.Rgba, y * pair.Width * 4, off.Width * 4);
                Buffer.BlockCopy(on.Rgba, y * on.Width * 4, pair.Rgba, (y * pair.Width + off.Width + 8) * 4, on.Width * 4);
            }
            pair.SavePng(Path.Combine(dir, $"trackshade_tr{trackNo:00}_slice{slice}_off_on.png"));
            output.WriteLine($"track {trackNo} slice {slice}: {changed} px differ, luminance ratio {ln / lo:F3}");
        }
    }

    [Fact]
    public void Settings_TrackShade_DefaultCliPortJson_AndNotice()
    {
        Assert.True(GraphicsSettings.Resolve(DesktopOptions.Parse([]), null).TrackShade);
        Assert.False(GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-trackshade", "off"]), null).TrackShade);
        Assert.True(GraphicsSettings.Resolve(DesktopOptions.Parse(["--gfx-trackshade", "on"]), new PortSettings { GfxTrackShade = false }).TrackShade);   // CLI wins
        Assert.False(GraphicsSettings.Resolve(DesktopOptions.Parse([]), new PortSettings { GfxTrackShade = false }).TrackShade);
        Assert.Throws<ArgumentException>(() => DesktopOptions.Parse(["--gfx-trackshade", "maybe"]));
        var port = new PortSettings(); (GraphicsSettings.Resolve(DesktopOptions.Parse([]), null) with { TrackShade = false }).WriteTo(port);
        Assert.False(port.GfxTrackShade);

        var on = new GraphicsSettings(GraphicsMode.Enhanced, 0) { TrackShade = true };
        var r = new RenderOptions(); on.ApplyTo(r);
        Assert.True(r.TrackShade && r.EnhancedFill);
        (on with { TrackShade = false }).ApplyTo(r);
        Assert.False(r.TrackShade);
        Assert.Contains("TRACK SHADE", on.QualityText(1));
        Assert.DoesNotContain("TRACK SHADE", (on with { TrackShade = false }).QualityText(1));
    }

    [Fact]
    public void PortOptionsRow_TrackShade_TogglesOnlyItsOwnValue()
    {
        var s = new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Enhanced, 0));
        Assert.Equal("TRACK SHADE", PortOptionsScreen.Label(PortOption.TrackShade));
        while (s.SelectedOption != PortOption.TrackShade) s.Handle(MenuKey.Down);
        Assert.Equal("ON", s.Value(PortOption.TrackShade));
        var before = s.Settings;
        Assert.True(s.Handle(MenuKey.Right).Changed);
        Assert.Equal("OFF", s.Value(PortOption.TrackShade));
        Assert.Equal(before with { TrackShade = false }, s.Settings);
        s.Handle(MenuKey.Left);
        Assert.Equal(before, s.Settings);
        Assert.True(s.IsActive(PortOption.TrackShade));
        Assert.False(new PortOptionsScreen(new GraphicsSettings(GraphicsMode.Vanilla, 0)).IsActive(PortOption.TrackShade));
    }
}
