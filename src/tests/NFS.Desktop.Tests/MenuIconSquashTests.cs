using NFS.Data;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>"Squash" animation of the highlighted configuration icon: computes [ref:2802]
/// the width factor (0..0x40, 64/64 = full) from the animation phase 0..0x400 and the quarter-sine table
/// (0x101 entries: [0x100] = 0x10000), and draws the icon scaled from its [ref:2803]
/// centre; full width takes the normal path (<see cref="MenuScene.BlitLevel"/>). [ref:2804]
/// The bright and dark variants have the same geometry, so the icon frame does not depend on the chosen name.</summary>
public class MenuIconSquashTests
{
    const string Main = "main", Art = "FEDATA/PC/ART/MAIN.QFS";
    /// <summary>Icon columns (without the rest of the bar) in rows 0 and 1: sin5 from y 20 to 71, rac5 from 28 to 79.</summary>
    static readonly (int X0, int Y0, int X1, int Y1) Band = (496, 0, 640, 90);

    [GameDataFact]
    public void TheHighlightedIconIsSquashedAndCentredWhileTheAnimationRuns()
    {
        var menu = MenuController.Load(null);
        var scene = new MenuScene(menu);
        Assert.Equal(Main, menu.CurrentScreen.Id);                              // selection 0: rows 0 and 1
        var background = Background();
        var full = Drawn(Draw(scene), background);
        Assert.True(full.Count > 1000, $"obie pelne ikony w pasie: {full.Count}");
        Assert.Equal(ContentWidth(), Extent(full));   // full icons: drawn 1:1, so as much as the content in the images

        var drawn = new List<List<(int X, int Y)>>();
        for (int frameIndex = 1; frameIndex <= 17; frameIndex++)
        {
            scene.Advance(0.4);                             // 51 ticks at 128 Hz > 0x28: one animation step
            var frame = Draw(scene);
            if (frameIndex is 1 or 4 or 8) Review(frame, $"main_menu_icons_{phase(frameIndex):X4}.png");
            drawn.Add(Drawn(frame, background));
            int anim = phase(frameIndex), factor = MenuScene.SquashFactor(anim);
            Assert.True(factor != MenuScene.IconSquashFull, $"faza {anim:X} nadal animuje");
            var box = Boxes(factor);
            // The shape changes over time, but every icon pixel stays inside the rectangle given by the factor:
            // width = |factor| * icon width / 64, icon centre unchanged, height unchanged. [ref:2805]
            Assert.True(drawn[^1].Count > 0, $"faza {anim:X}: ikona zniknela");
            foreach (var (x, y) in drawn[^1])
                Assert.True(box.Any(b => x >= b.X0 && x < b.X1 && y >= b.Y0 && y < b.Y1), $"faza {anim:X}: piksel {x},{y} poza ikona");
            // The drawn width follows the factor (16.16 sampling does not reach the source edges, so
            // the narrowed icon is slightly narrower than the frame): phase 0x3C (59/64) wider than 0xF0 (6/64),
            // and after the reflection (0x21C, -63/64) wide again.
            Assert.True(Extent(drawn[^1]) <= Width(box) && Extent(drawn[^1]) > 0, $"phase {anim:X}: drawn width");
        }
        for (int i = 1; i <= 3; i++) Assert.True(Extent(drawn[i]) < Extent(drawn[i - 1]), $"frame {i + 1}: narrowing");
        Assert.True(Extent(drawn[8]) > Extent(drawn[3]), "the reflection stretches the icon again");
        for (int i = 1; i < drawn.Count; i++) Assert.False(drawn[i].SequenceEqual(drawn[i - 1]), $"frame {i}: unchanged");
    }

    /// <summary>Animation phase per frame (step 0x3C per frame, one step per 0x28 ticks at 128 Hz).</summary>
    static int phase(int frame) => frame * MenuScene.IconAnimStep;

    /// <summary>Frame dump for review (PNG) - only when <c>NFS_T7A2_REVIEW_DIR</c> is set.</summary>
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T7A2_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    /// <summary>Pixels differing from the background in the strip of rows 0 and 1 of the bar (nothing else is drawn there, so this is exactly
    /// the two highlighted icons).</summary>
    static List<(int X, int Y)> Drawn(FrameBuffer fb, FrameBuffer background)
    {
        var list = new List<(int, int)>();
        for (int y = Band.Y0; y < Band.Y1; y++)
            for (int x = Band.X0; x < Band.X1; x++)
                if (fb.Get(x, y) != background.Get(x, y)) list.Add((x, y));
        return list;
    }

    static int Extent(List<(int X, int Y)> pixels) => pixels.Max(p => p.X) - pixels.Min(p => p.X);

    /// <summary>Width of both frames together - for icons at (508, 20) and (553, 28) (FSH header position + 0x1C).</summary>
    static int Width((int X0, int X1, int Y0, int Y1)[] box) => Math.Max(box[0].X1, box[1].X1) - Math.Min(box[0].X0, box[1].X0);

    /// <summary>Width of the visible content of both icons (opaque pixels in their MAIN.QFS images):
    /// the images have transparent margins, so the full icon drawn 1:1 gives exactly that many pixels.</summary>
    static int ContentWidth()
    {
        var art = Fsh.Load(Art);
        int min = int.MaxValue, max = int.MinValue;
        foreach (var (name, x, _) in new[] { ("sin5", 508, 20), ("rac5", 553, 28) })
        {
            var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
            for (int sy = 0; sy < image.Height; sy++)
                for (int sx = 0; sx < image.Width; sx++)
                    if (image.Rgba[(sy * image.Width + sx) * 4 + 3] != 0)
                    {
                        min = Math.Min(min, x + sx);
                        max = Math.Max(max, x + sx);
                    }
        }
        return max - min;
    }

    /// <summary>Frames of both highlighted icons at squash factor: <c>w = factor * width / 64</c>
    /// (clamped to zero and to 16 bits), drawn from the icon centre (<c>x + w/2 - |w/2|</c>). [ref:2806]</summary>
    static (int X0, int X1, int Y0, int Y1)[] Boxes(int factor)
    {
        var art = Fsh.Load(Art);
        var result = new List<(int, int, int, int)>();
        foreach (var (name, x, y) in new[] { ("sin5", 508, 20), ("rac5", 553, 28) })
        {
            var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
            int w = (short)(factor * image.Width / 64), half = w / 2;
            if (half < 1) half = -half;
            result.Add((x + image.Width / 2 - half, x + image.Width / 2 - half + Math.Abs(w), y, y + image.Height));
        }
        return result.ToArray();
    }

    /// <summary>Main screen background (blk6 + side/gren/logo) without icons - the icon column is empty up to y 460.</summary>
    static FrameBuffer Background()
    {
        var art = Fsh.Load(Art);
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        foreach (string name in new[] { "blk6", "side", "gren", "logo" })
        {
            var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
            OriginalHud.Blit(fb, image, image.PosX, image.PosY);
        }
        return fb;
    }

    static FrameBuffer Draw(MenuScene scene) { var fb = new FrameBuffer(); scene.Render(fb); return fb; }
}
