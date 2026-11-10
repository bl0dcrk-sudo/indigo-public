using NFS.Data;
using NFS.Render;

namespace NFS.Desktop.Tests;

public sealed class ReplayStatusHudTests
{
    [Theory]
    [InlineData(0, "cqtr")]
    [InlineData(1, "chlf")]
    [InlineData(2, "cnml")]
    [InlineData(3, "cdbl")]
    public void SpeedIcon_MatchesTheOriginalReplaySpeedTable(int index, string shape)
        => Assert.Equal(shape, ReplayStatusHud.SpeedShape(index));

    [Theory]
    [InlineData(0, 36000, 0)]
    [InlineData(18000, 36000, 0x44)]
    [InlineData(36000, 36000, 0x88)]
    [InlineData(40000, 36000, 0x88)]
    [InlineData(-1, 36000, 0)]
    [InlineData(100, 0, 0)]
    public void ProgressOffset_UsesOriginal136PixelRange(int tick, int length, int expected)
        => Assert.Equal(expected, ReplayStatusHud.ProgressOffset(tick, length));

    static string[] Shapes(ReplayStripState state) => ReplayStatusHud.Plan(state).Select(i => i.Shape).ToArray();

    [Fact]
    public void Plan_UnselectedIconsAreThePlainSpeedAndCameraIcons()
    {
        var plan = ReplayStatusHud.Plan(new ReplayStripState(-1, false, false, 2, 4, 0, 1000));
        Assert.Equal(new[] { "repl", "cnml", "cca5", "indi" }, plan.Select(i => i.Shape));
        Assert.Equal(new[] { "repl", "play", "cqtr", "cca1", "indi" },
            Shapes(new ReplayStripState(-1, false, true, 0, 0, 0, 1000)));
    }

    [Theory]
    [InlineData(0, false, "hbut", -31, "hpau", 0)]
    [InlineData(0, true, "dbut", -31, "hpau", 1)]
    [InlineData(1, false, "hbut", 0, "hdbl", 0)]
    [InlineData(1, true, "dbut", 0, "hdbl", 1)]
    [InlineData(2, false, "hbut", 31, "heje", 0)]
    [InlineData(2, true, "dbut", 31, "heje", 1)]
    [InlineData(3, false, "hcam", 0, "hca3", 0)]
    [InlineData(3, true, "dcam", 0, "hca3", 1)]
    public void Plan_SelectedIconShowsItsHighlightedOrPressedButton(int slot, bool pressed, string button, int dx, string glyph, int glyphOffset)
    {
        var plan = ReplayStatusHud.Plan(new ReplayStripState(slot, pressed, false, 3, 2, 0, 1000));
        Assert.Contains(new ReplayIcon(button, dx, 0), plan);
        Assert.Contains(new ReplayIcon(glyph, glyphOffset, glyphOffset), plan);
        // the other slots keep their plain icons
        if (slot != 1) Assert.Contains(plan, i => i.Shape == "cdbl");
        else Assert.DoesNotContain(plan, i => i.Shape == "cdbl");
        if (slot != 3) Assert.Contains(plan, i => i.Shape == "cca3");
        else Assert.DoesNotContain(plan, i => i.Shape == "cca3");
    }

    [Fact]
    public void Plan_PauseIconShowsThePlayGlyphWhilePausedAndTheProgressMarkerMoves()
    {
        Assert.Contains("hply", Shapes(new ReplayStripState(0, false, true, 2, 0, 0, 1000)));
        Assert.Contains("hpau", Shapes(new ReplayStripState(0, false, false, 2, 0, 0, 1000)));
        Assert.Contains("play", Shapes(new ReplayStripState(3, false, true, 2, 0, 0, 1000)));
        Assert.Contains(new ReplayIcon("indi", 0x44, 0), ReplayStatusHud.Plan(new ReplayStripState(0, false, false, 2, 0, 500, 1000)));
        Assert.DoesNotContain("indi", Shapes(new ReplayStripState(0, false, false, 2, 0, 500, 0))); // == 0: no marker [ref:2894]
        Assert.Equal(new[] { "cca1", "cca2", "cca3", "cca4", "cca5", "cca6", "cca7", "cca8", "cca9" },
            Enumerable.Range(0, 9).Select(i => ReplayStatusHud.CameraShape(i, false)));
    }

    [GameDataFact]
    public void Draw_SelectedAndPressedIconsDifferFromThePlainStripAndArePngReviewed()
    {
        string dir = GameFiles.FindGameDir();
        var hud = new ReplayStatusHud(dir);
        FrameBuffer Frame(ReplayStripState state)
        {
            var fb = new FrameBuffer();
            fb.Clear(40, 60, 90);
            hud.Draw(fb, state);
            return fb;
        }
        var plain = Frame(new ReplayStripState(-1, false, false, 2, 5, 18000, 36000));
        var frames = new List<FrameBuffer> { plain };
        for (int slot = 0; slot < 4; slot++)
        {
            var idle = Frame(new ReplayStripState(slot, false, false, 2, 5, 18000, 36000));
            var pressed = Frame(new ReplayStripState(slot, true, false, 2, 5, 18000, 36000));
            Assert.NotEqual(plain.Rgba, idle.Rgba);
            Assert.NotEqual(idle.Rgba, pressed.Rgba);
            frames.Add(idle);
            frames.Add(pressed);
        }
        frames.Add(Frame(new ReplayStripState(0, false, true, 0, 8, 36000, 36000)));
        frames.Add(Frame(new ReplayStripState(1, true, false, 3, 0, 0, 36000)));
        SaveStripPng(frames.ToArray());
    }

    /// <summary>Review picture of the strip states (2x) in out/gfx_check, written without a window.</summary>
    static void SaveStripPng(FrameBuffer[] frames)
    {
        string root = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, Path.Combine("src", "NFS2SE.sln")))) { root = d.FullName; break; }
        string dir = Path.Combine(root, "out", "gfx_check");
        Directory.CreateDirectory(dir);
        const int width = 209, height = 40, columns = 2, scale = 2;
        int rows = (frames.Length + columns - 1) / columns;
        var mosaic = new FrameBuffer(width * columns * scale, height * rows * scale);
        mosaic.Clear(10, 10, 10);
        for (int i = 0; i < frames.Length; i++)
        for (int y = 0; y < height * scale; y++)
        for (int x = 0; x < width * scale; x++)
            mosaic.Set((i % columns) * width * scale + x, (i / columns) * height * scale + y, frames[i].Get(216 + x / scale, 437 + y / scale));
        string path = Path.Combine(dir, "replay_strip_icons.png");
        mosaic.SavePng(path);
        Assert.True(new FileInfo(path).Length > 1000);
    }

    [GameDataFact]
    public void Draw_UsesOriginalReplayArtworkAndReflectsPauseSpeedAndProgress()
    {
        string dir = GameFiles.FindGameDir();
        var hud = new ReplayStatusHud(dir);
        var fsh = Fsh.Load("GAMEDATA/DASHHUD/PC/HUD.FSH", dir);
        string[] speeds = ["cqtr", "chlf", "cnml", "cdbl"];
        var cases = new (bool Paused, int Speed, int Tick)[]
        {
            (false, 2, 0), (true, 2, 0), (false, 0, 0), (false, 1, 0),
            (false, 2, 0), (false, 3, 0), (false, 2, 18000), (false, 2, 36000),
        };
        uint[][] backgrounds = [[1, 2, 3], [0x4F, 0x87, 0xB2], [0xAA, 0x15, 0x3C]];
        foreach (var state in cases)
        foreach (var bg in backgrounds)
        {
            var actual = new FrameBuffer();
            actual.Clear((byte)bg[0], (byte)bg[1], (byte)bg[2]);
            hud.Draw(actual, new ReplayStripState(-1, false, state.Paused, state.Speed, 0, state.Tick, 36000));
            var expected = ReferenceFrame(fsh, bg, state.Paused, speeds[state.Speed], state.Tick, 36000);
            Assert.Equal(expected.Rgba, actual.Rgba);
            Assert.Equal((bg[0] << 16) | (bg[1] << 8) | bg[2], actual.Get(216, 437)); // transparent repl corner preserves the background
            Assert.Equal((216, 437), (Signed12(fsh.Find("repl")!.Main.PosX), Signed12(fsh.Find("repl")!.Main.PosY)));
            Assert.Equal((251, 444), (Signed12(fsh.Find("play")!.Main.PosX), Signed12(fsh.Find("play")!.Main.PosY)));
            var speedEntry = fsh.Find(speeds[state.Speed])!;
            Assert.Equal((276, 437), (Signed12(speedEntry.Main.PosX), Signed12(speedEntry.Main.PosY)));
            Assert.Equal((251, 470), (Signed12(fsh.Find("indi")!.Main.PosX), Signed12(fsh.Find("indi")!.Main.PosY)));
        }

        SaveMosaic(cases.Select(c => DrawFrame(hud, c.Paused, c.Speed, c.Tick)).ToArray());
    }

    static FrameBuffer DrawFrame(ReplayStatusHud hud, bool paused, int speed, int tick)
    {
        var fb = new FrameBuffer(); fb.Clear(10, 20, 30);
        hud.Draw(fb, new ReplayStripState(-1, false, paused, speed, 0, tick, 36000));
        return fb;
    }

    static FrameBuffer ReferenceFrame(Fsh fsh, uint[] bg, bool paused, string speed, int tick, int length)
    {
        var fb = new FrameBuffer(); fb.Clear((byte)bg[0], (byte)bg[1], (byte)bg[2]);
        ReferenceBlit(fb, DecodeReference(fsh, "repl"));
        if (paused) ReferenceBlit(fb, DecodeReference(fsh, "play"));
        ReferenceBlit(fb, DecodeReference(fsh, speed));
        ReferenceBlit(fb, DecodeReference(fsh, "cca1")); // camera icon (slot 3) of the director
        var indi = DecodeReference(fsh, "indi");
        int progress = length <= 0 ? 0 : (int)Math.Clamp((long)Math.Max(0, tick) * 136 / length, 0, 136);
        ReferenceBlit(fb, indi, progress, 0);
        return fb;
    }

    static FshImage DecodeReference(Fsh fsh, string name)
    {
        var entry = fsh.Find(name) ?? throw new Xunit.Sdk.XunitException($"HUD.FSH missing '{name}'");
        var image = fsh.Decode(entry, fsh.GlobalPalette()) ?? throw new Xunit.Sdk.XunitException($"'{name}' is not a bitmap");
        if (entry.Main.Code == 0x7B)
        {
            var indices = fsh.Indices(entry);
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] == 255) image.Rgba[4 * i + 3] = 0;
        }
        return image;
    }

    static void ReferenceBlit(FrameBuffer fb, FshImage image, int dx = 0, int dy = 0)
    {
        int x0 = Signed12(image.PosX) + dx, y0 = Signed12(image.PosY) + dy;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            int px = x0 + x, py = y0 + y, o = (y * image.Width + x) * 4;
            if ((uint)px >= fb.Width || (uint)py >= fb.Height || image.Rgba[o + 3] == 0) continue;
            fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
        }
    }

    static int Signed12(int value) => (value << 20) >> 20;

    static void SaveMosaic(FrameBuffer[] frames)
    {
        if (Environment.GetEnvironmentVariable("NFS_REPLAY_REVIEW_DIR") is not { Length: > 0 } review) return;
        const int width = 209, height = 40, columns = 3;
        var mosaic = new FrameBuffer(width * columns, height * ((frames.Length + columns - 1) / columns));
        mosaic.Clear(10, 10, 10);
        for (int i = 0; i < frames.Length; i++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            mosaic.Set((i % columns) * width + x, (i / columns) * height + y, frames[i].Get(216 + x, 437 + y));

        Directory.CreateDirectory(review);
        mosaic.SavePng(Path.Combine(review, "replay_status_icons.png"));
    }
}
