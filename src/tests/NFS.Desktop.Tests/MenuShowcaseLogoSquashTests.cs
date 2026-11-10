using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>"squash" animation of the brand logo on the showcase screen (TASKS 7a2 / 11): raises the phase [ref:2827]
/// by 0x1E on every screen draw, and draws the "car7" logo with a scaled blit [ref:2828]
/// ; the width factor uses the same arithmetic as the configuration icon [ref:2829]
/// (quadrant sine table). The factor multiplies the shape width and divides [ref:2830]
/// by 64, the drawing runs from the centre of the shape, and the scaled path takes no [ref:2831]
/// level argument, so during the logo phase it is opaque.</summary>
public class MenuShowcaseLogoSquashTests
{
    const string PlayerOne = "player-one", MacArt = "FEDATA/PC/ART/MAC.QFS";
    /// <summary>Pattern: |width factor| = quadrant sine (<c>MathConstants.SinQuarter</c>) with a triangle wave idx; the sign
    /// is negative in the middle half of the phase (0x100..0x300), and from 0x400 the full width.</summary>
    static int Expected(int phase)
    {
        if (phase >= MenuScene.IconAnimFull) return MenuScene.IconSquashFull;
        int t = (phase + 0x100) & 0x1FF, idx = 0x100 - Math.Abs(t - 0x100);
        int v = NFS.Core.Constants.MathConstants.SinQuarter[idx] / 1024;
        return phase is >= 0x100 and < 0x300 ? -v : v;
    }
    [GameDataFact]
    public void TheLogoFactorIsTheIconSineWaveSteppedByTheShowcasePhase()
    {
        // The showcase screen's phase step is 0x1E per frame, not 0x3C as for the icons. [ref:2832]
        Assert.Equal(0x1E, MenuScene.ShowcaseLogoStep);
        for (int step = 0; step <= MenuScene.IconAnimFull / MenuScene.ShowcaseLogoStep + 1; step++)
        {
            int phase = step * MenuScene.ShowcaseLogoStep;
            Assert.Equal(Expected(phase), MenuScene.SquashFactor(phase));
        }
        // Full width (64/64) at the start, narrowing, zero in phase 0x100, mirror reflection at 0x200
        // (full width with a negative sign), return, and 64/64 again from phase 0x400.
        Assert.Equal(Enumerable.Range(0, 10).Select(i => MenuScene.SquashFactor(i * MenuScene.ShowcaseLogoStep)),
            [64, 62, 59, 54, 47, 38, 28, 17, 6, -5]);
        Assert.Equal(MenuScene.IconSquashFull, MenuScene.SquashFactor(0));
        Assert.Equal(0, MenuScene.SquashFactor(0x100));
        Assert.Equal(-MenuScene.IconSquashFull, MenuScene.SquashFactor(0x200));
        Assert.Equal(MenuScene.IconSquashFull, MenuScene.SquashFactor(MenuScene.IconAnimFull));
    }

    [GameDataFact]
    public void TheShowcaseLogoSquashesWithThePhaseOfEveryDrawnFrame()
    {
        var menu = OpenShowcase();
        var scene = new MenuScene(menu) { AnimateChrome = true };
        var logo = Img(MacArt, "car7");
        var withoutLogo = Reference(menu, factor: 0);
        var widths = new List<int>();
        for (int frame = 0; frame < 12; frame++)
        {
            var fb = new FrameBuffer();
            scene.Render(fb);
            // The phase grows by one step on every screen draw: frame 0 (the first after entry) has phase 0x1E.
            int factor = MenuScene.SquashFactor((frame + 1) * MenuScene.ShowcaseLogoStep);
            Assert.Null(Diff(Reference(menu, factor), fb, 0, 0, 640, 460));
            if (frame is 0 or 3 or 8)
                Review(fb, $"showcase_logo_{(frame + 1) * MenuScene.ShowcaseLogoStep:X}.png");
            widths.Add(InkWidth(fb, withoutLogo, logo));
        }
        // The shape changes every frame (a different factor) and the drawing width follows it: the second frame
        // is narrower than the first, phase 0x100 (frame 7) is a vertical strip, and the reflected phase 0x1E0 (frame 10) stretches again.
        Assert.True(widths[1] < widths[0], $"0x1E: {widths[0]} → 0x3C: {widths[1]}");
        Assert.True(widths[7] < widths[1] / 4, $"phase 0x100: strip {widths[7]} vs 0x3C: {widths[1]}");
        Assert.True(widths[10] > widths[7] * 2, $"phase 0x1E0: {widths[10]} vs 0x100: {widths[7]}");
        // After 0x400 / 0x1E = 35 frames the phase ends and the logo returns to the path with the entry level; [ref:2833]
        // without elapsed time the level is zero, so the logo is not visible yet.
        for (int frame = 12; frame < 35; frame++) scene.Render(new FrameBuffer());
        var done = new FrameBuffer();
        scene.Render(done);
        Assert.Null(Diff(Reference(menu, MenuScene.IconSquashFull, level: 0), done, 0, 0, 640, 460));
        Assert.Equal(0, InkWidth(done, withoutLogo, logo));
    }

    /// <summary>Reference frame of the showcase screen (as ShowcaseTests.Reference): "back" from the car's QFS, the page
    /// (<see cref="MenuScene.ShowcasePageShape"/>), the "car7" logo drawn along with the position texts [ref:2834]
    /// <paramref name="factor"/> = 0 means no logo; 0x40 is normal drawing with <paramref name="level"/> [ref:2835]
    /// otherwise a scaled blit from the centre of the shape (<c>w = factor * width / 64</c>, 16 bit). [ref:2836]</summary>
    static FrameBuffer Reference(MenuController menu, int factor, int level = MenuScene.ShowcaseFullLevel)
    {
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        foreach (string name in new[] { "back", MenuScene.ShowcasePageShape(menu.ShowcasePage) })
        {
            var image = Img(MacArt, name);
            OriginalHud.Blit(fb, image, image.PosX, image.PosY);
        }
        if (factor != 0)
        {
            var logo = Img(MacArt, "car7");
            int x = logo.PosX, y = logo.PosY + MenuScene.ShowcaseLogoDy;
            if (factor == MenuScene.IconSquashFull) MenuScene.BlitLevel(fb, logo, x, y, level);
            else
            {
                int w = (short)(factor * logo.Width / 64), half = w / 2;
                if (half < 1) half = -half;
                MenuScene.BlitScaled(fb, logo, x + logo.Width / 2 - half, y, w, logo.Height, MenuScene.ShowcaseFullLevel);
            }
        }
        DrawItems(fb, menu);
        return fb;
    }

    /// <summary>Position texts of the showcase screen (the ID is substituted). [ref:2837]</summary>
    static void DrawItems(FrameBuffer fb, MenuController menu)
    {
        var font = new FrontEndFont();
        var text = TextFile.Load("ENG");
        var items = menu.CurrentScreen.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Kind is not (0 or 2) || item.TextId >= text.Entries.Count) continue;
            var e = text.Entries[item.TextId];
            int colour = i == menu.SelectedIndex ? e.HighlightColour : e.Colour;
            font.Draw(fb, e.Text, e.X, e.Y, e.FontSize, e.Align, colour);
            if ((item.Flags & 2) != 0)
                font.Draw(fb, "...", e.X + (e.FontSize < 18 ? 0xD : 0x13), e.Y, e.FontSize, e.Align, colour);
        }
    }

    /// <summary>Width of the logo drawing in a frame: the columns of the "car7" shape rectangle that differ from the frame
    /// without the logo (the position texts are in both, so they are not counted in the drawing).</summary>
    static int InkWidth(FrameBuffer fb, FrameBuffer withoutLogo, FshImage logo)
    {
        int min = int.MaxValue, max = int.MinValue;
        for (int x = logo.PosX; x < logo.PosX + logo.Width; x++)
        {
            bool ink = false;
            for (int y = logo.PosY + MenuScene.ShowcaseLogoDy; y < logo.PosY + MenuScene.ShowcaseLogoDy + logo.Height && !ink; y++)
                ink = fb.Get(x, y) != withoutLogo.Get(x, y);
            if (!ink) continue;
            min = Math.Min(min, x);
            max = Math.Max(max, x);
        }
        return max < min ? 0 : max - min + 1;
    }

    /// <summary>First pixel that differs between the reference and the frame (null = identical).</summary>
    static string? Diff(FrameBuffer expected, FrameBuffer fb, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < Math.Min(y1, fb.Height); y++)
            for (int x = x0; x < Math.Min(x1, fb.Width); x++)
                if (fb.Get(x, y) != expected.Get(x, y)) return $"piksel {x},{y}: {fb.Get(x, y):X6} != {expected.Get(x, y):X6}";
        return null;
    }

    static FshImage Img(string art, string name)
    {
        var fsh = Fsh.Load(art);
        return fsh.Decode(fsh.Find(name)!, fsh.GlobalPalette())!;
    }

    /// <summary>Dumps a frame for review (PNG), only when <c>NFS_T7A2_LOGO_REVIEW_DIR</c> is set.</summary>
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_T7A2_LOGO_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    /// <summary>Menu → PLAYER ONE → "showcase"; the first car from the config (see ShowcaseTests). [ref:2838]</summary>
    static MenuController OpenShowcase(int car = 0)
    {
        var menu = MenuController.Load();
        menu.Config.P1Car = (byte)car;
        foreach (var key in new[] { MenuKey.Down, MenuKey.Down, MenuKey.Enter }) menu.HandleKey(key);
        Assert.Equal(PlayerOne, menu.CurrentScreen.Id);
        int showcase = menu.CurrentScreen.Items.ToList().FindIndex(i => i.Target == MenuController.ShowcaseScreen);
        for (int i = 0; menu.SelectedIndex != showcase; i++)
        {
            Assert.True(i < 10, "the showcase position is not reachable");
            menu.HandleKey(MenuKey.Down);
        }
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuController.ShowcaseScreen, menu.CurrentScreen.Id);
        return menu;
    }
}
