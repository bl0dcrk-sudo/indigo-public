using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Menu look (stage 1). [ref:2815] [ref:3455]</summary>
public class MenuLookTests
{
    /// <summary>Full-coverage colour of a glyph pixel: the FFN ramp gives (colour * 15) >> 4 per channel. [ref:2816]</summary>
    static uint TextColour(int index)
    {
        uint c = NFS.Core.Constants.RenderConstants.FrontEndTextColours[index];
        return (c >> 16 & 255) * 15 >> 4 << 16 | (c >> 8 & 255) * 15 >> 4 << 8 | (c & 255) * 15 >> 4;
    }

    const string Main = "main", GameSetup = "game-setup", Location = "location", PlayerOne = "player-one",
        Opponents = "opponents", Options = "options", Controls = "controls", LoadSave = "load-save";

    static readonly Dictionary<string, MenuKey[]> Paths = new()
    {
        [Main] = [],
        [GameSetup] = [MenuKey.Enter],
        [Location] = [MenuKey.Down, MenuKey.Enter],
        [PlayerOne] = [MenuKey.Down, MenuKey.Down, MenuKey.Enter],
        [Opponents] = [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter],
        [Options] = [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter],
        [Controls] = [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter, MenuKey.Down, MenuKey.Down, MenuKey.Enter],
        [LoadSave] = [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter,
            MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter]
    };

    static MenuController Open(string screen, string? dir = null)
    {
        var menu = MenuController.Load(dir);
        foreach (var key in Paths[screen]) menu.HandleKey(key);
        Assert.Equal(screen, menu.CurrentScreen.Id);
        return menu;
    }

    static (Fsh Art, byte[]? Palette) MainArt()
    {
        var art = Fsh.Load("FEDATA/PC/ART/MAIN.QFS");
        return (art, art.GlobalPalette());
    }

    static FshImage Image(Fsh art, string name) => art.Decode(art.Find(name)!, art.GlobalPalette())!;

    /// <summary>Background shape plus the static chrome (side, gren, logo) as the original composes them.</summary>
    static FrameBuffer Backdrop(string background, bool chrome = true)
    {
        var (art, _) = MainArt();
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        var bg = Image(art, background);
        OriginalHud.Blit(fb, bg, bg.PosX, bg.PosY);
        if (chrome)
            foreach (string name in new[] { "side", "gren", "logo" })
            {
                var image = Image(art, name);
                OriginalHud.Blit(fb, image, image.PosX, image.PosY);
            }
        return fb;
    }

    /// <summary>Pixels of <paramref name="rect"/> that differ from the same area of <paramref name="backdrop"/>.</summary>
    static List<uint> Changed(FrameBuffer fb, FrameBuffer backdrop, int x0, int y0, int x1, int y1)
    {
        var list = new List<uint>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (fb.Get(x, y) != backdrop.Get(x, y)) list.Add(fb.Get(x, y));
        return list;
    }

    [GameDataTheory]
    [InlineData(Main, "blk6")]
    [InlineData(GameSetup, "blk4")]
    [InlineData(Location, "blk4")]
    [InlineData(PlayerOne, "blk4")]
    [InlineData(Opponents, "blk4")]
    [InlineData(Options, "blk4")]
    [InlineData(Controls, "blk4")]
    [InlineData(LoadSave, "blk4")]
    public void EveryScreenDrawsItsOriginalBackgroundFromMainQfs(string screen, string shape)
    {
        var menu = Open(screen);
        var scene = new MenuScene(menu);
        var fb = new FrameBuffer();
        scene.Render(fb);
        Assert.Equal("main/" + shape, scene.BackgroundSource);
        Assert.True(scene.UsesOriginalFonts);
        // Top-right corner: nothing else is drawn there (the icon strip starts at x = 507). [ref:2817]
        // MenuConfigIconTests), so the original bitmap shows through unchanged.
        var bg = Image(MainArt().Art, shape);
        for (int y = 24; y < 108; y += 7)
            for (int x = 482; x < 506; x += 5)
            {
                int o = (y * bg.Width + x) * 4;
                Assert.Equal((uint)(bg.Rgba[o] << 16 | bg.Rgba[o + 1] << 8 | bg.Rgba[o + 2]), fb.Get(x, y));
            }
    }

    [GameDataFact]
    public void MainMenuItemsUseTextTablePositionsFontAndColours()
    {
        var menu = Open(Main);
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        // Reference: backdrop plus every item drawn by the original FFN routine at its TEXT.ENG record. [ref:2818]
        var expected = Backdrop("blk6");
        var text = TextFile.Load("ENG");
        var font = new FrontEndFont();
        var items = menu.CurrentScreen.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var e = text.Entries[items[i].TextId];
            bool disabled = (items[i].Flags & 1) != 0;
            font.Draw(expected, e.Text, e.X, e.Y, e.FontSize, e.Align, disabled ? 6 : i == menu.SelectedIndex ? e.HighlightColour : e.Colour);
        }
        // Compare everywhere except the port's key legend at the bottom and the icon strip in the [ref:2819]
        // right column (x >= 496; covered by MenuConfigIconTests). Items sit right-aligned at x 168.
        for (int y = 0; y < 460; y++)
            for (int x = 0; x < 496; x++)
                Assert.True(expected.Get(x, y) == fb.Get(x, y), $"pixel {x},{y}");
        // "RACE" is the 36-pixel heading, the other entries the 18-pixel font.
        Assert.Equal(36, text.Entries[menu.CurrentScreen.Items[5].TextId].FontSize);
    }

    [GameDataFact]
    public void SelectedItemIsHighlightedAndDisabledItemIsGrey()
    {
        var menu = Open(Main);
        var scene = new MenuScene(menu);
        var text = TextFile.Load("ENG");
        var font = new FrontEndFont();
        var items = menu.CurrentScreen.Items;
        bool Has(FrameBuffer fb, int item, uint colour)
        {
            var e = text.Entries[items[item].TextId];
            int w = font.Font(e.FontSize).Measure(e.Text);
            for (int y = e.Y - 4; y < e.Y + e.FontSize + 8; y++)
                for (int x = e.X - w - 2; x <= e.X + 2; x++)
                    if (fb.Get(x, y) == colour) return true;
            return false;
        }
        var first = new FrameBuffer();
        scene.Render(first);
        var e0 = text.Entries[items[0].TextId];
        Assert.True(Has(first, 0, TextColour(e0.HighlightColour)));
        Assert.False(Has(first, 0, TextColour(e0.Colour)));
        Assert.True(Has(first, 1, TextColour(text.Entries[items[1].TextId].Colour)));
        Assert.False(Has(first, 1, TextColour(text.Entries[items[1].TextId].HighlightColour)));
        Assert.NotEqual(TextColour(e0.HighlightColour), TextColour(e0.Colour));
        // "player two" is disabled in single-player: colour index 6. [ref:2820]
        Assert.True((items[3].Flags & 1) != 0);
        Assert.True(Has(first, 3, TextColour(6)));
        menu.HandleKey(MenuKey.Down);
        var second = new FrameBuffer();
        scene.Render(second);
        Assert.True(Has(second, 1, TextColour(text.Entries[items[1].TextId].HighlightColour)));
        Assert.False(Has(second, 0, TextColour(e0.HighlightColour)));
        Assert.True(Has(second, 0, TextColour(e0.Colour)));
    }

    [GameDataFact]
    public void OptionValuesAreDrawnRightOfTheLabelWithTheirOwnColours()
    {
        var menu = Open(GameSetup);
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        var expected = Backdrop("blk4");
        var text = TextFile.Load("ENG");
        var font = new FrontEndFont();
        var items = menu.CurrentScreen.Items;
        var item = items[0]; // game type: label with ':' marker and the current value at x + 0x13
        var e = text.Entries[item.TextId];
        var v = text.Entries[item.ValueTextIds[menu.Config[item.ConfigOffset!.Value]]];
        font.Draw(expected, e.Text, e.X, e.Y, 18, e.Align, e.HighlightColour);
        font.Draw(expected, ":", e.X + 0xC, e.Y, 18, e.Align, e.HighlightColour);
        font.Draw(expected, v.Text, e.X + 0x13, e.Y, 18, 3, v.HighlightColour);
        // Comparison only up to x 496: the configuration icon strip starts further on (MenuConfigIconTests). [ref:2821]
        var changed = Changed(fb, expected, 0, e.Y - 2, 496, e.Y + 24);
        // Everything in the first row matches the reference except where later items would overlap (none): zero diffs
        // apart from the cursor-independent rows is enough to prove label, ':' and value positions.
        Assert.Empty(changed);
        Assert.Contains(TextColour(v.HighlightColour), Enumerable.Range(e.X + 0x13, 120)
            .SelectMany(x => Enumerable.Range(e.Y, 20).Select(y => fb.Get(x, y))));
    }

    static bool Differs(FrameBuffer a, FrameBuffer b, int x0, int y0, int x1, int y1) => Changed(a, b, x0, y0, x1, y1).Count > 0;

    [GameDataFact]
    public void CarGraphModeDrawsGraphBackdropAndStatLabels()
    {
        var menu = Open(PlayerOne);
        menu.Config[0x31] = 1;
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        var grph = Image(MainArt().Art, "grph");
        // lower-left of the panel is plain "grph" art
        int o = (100 * grph.Width + 10) * 4;
        if (grph.Rgba[o + 3] != 0)
            Assert.Equal((uint)(grph.Rgba[o] << 16 | grph.Rgba[o + 1] << 8 | grph.Rgba[o + 2]), fb.Get(0xD3 + 10, 0x13C + 100));
        // the four captions at TEXT 0x55..0x58 are drawn (colour from the table, left of the panel)
        var text = TextFile.Load("ENG");
        var backdrop = Backdrop("blk4");
        var e = text.Entries[0x55];
        Assert.True(Differs(fb, backdrop, e.X - 100, e.Y, e.X + 2, e.Y + 18));
    }

    [GameDataFact]
    public void OpponentClassScreenListsTheClassDescription()
    {
        var menu = Open(Opponents);
        menu.Config.OpponentCar = 13; // class B
        menu.Config.OpponentCount = 1;
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        var backdrop = Backdrop("blk4");
        var text = TextFile.Load("ENG");
        for (int k = 0; k < 4; k++)
        {
            int y = text.Entries[0x55 + k].Y;
            Assert.True(Differs(fb, backdrop, 60, y, 172, y + 18), $"line {k}");
        }
    }

    [GameDataFact]
    public void LoadSaveScreenShowsItsEntriesInTheOriginalFont()
    {
        var menu = Open(LoadSave);
        var fb = new FrameBuffer();
        var scene = new MenuScene(menu);
        scene.Render(fb);
        var text = TextFile.Load("ENG");
        var backdrop = Backdrop("blk4");
        foreach (var item in menu.CurrentScreen.Items.Where(i => i.Kind is 0 or 2))
        {
            var e = text.Entries[item.TextId];
            int w = new FrontEndFont().Font(e.FontSize).Measure(e.Text);
            int x0 = e.Align % 3 == 1 ? e.X - w : e.Align % 3 == 2 ? e.X - w / 2 : e.X;
            Assert.True(Differs(fb, backdrop, x0, e.Y, x0 + w, e.Y + e.FontSize), e.Text);
        }
    }

    [GameDataTheory]
    [InlineData(Location, 0xD3, 0x13C)]
    [InlineData(PlayerOne, 0xD3, 0x13C)]
    public void PreviewPanelUsesTableShapeAndKeepsTransparency(string screen, int x, int y)
    {
        var menu = Open(screen);
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        var (art, _) = MainArt();
        string shape = screen == Location
            ? NFS.Core.Constants.MenuArtConstants.TrackShapeNames[menu.Config.Track]
            : NFS.Core.Constants.MenuArtConstants.PlayerCarShapeNames[menu.Config.P1Car];
        var image = Image(art, shape);
        var backdrop = Backdrop("blk4");
        int opaque = 0, transparent = 0;
        for (int sy = 0; sy < image.Height; sy++)
            for (int sx = 0; sx < image.Width; sx++)
            {
                int px = x + sx, py = y + sy, o = (sy * image.Width + sx) * 4;
                bool covered = image.Rgba[o + 3] != 0;
                if (covered) opaque++; else transparent++;
                if (py >= 460 || px >= 640) continue;
                // The configuration icon strip follows the preview and enters its right edge [ref:2822]
                // from x 507 (MenuConfigIconTests checks this column separately).
                if (px >= 507) continue;
                if (!covered)
                {
                    // no white (or any) rectangle around the picture: transparent pixels keep the background
                    Assert.Equal(backdrop.Get(px, py), fb.Get(px, py));
                }
                else
                {
                    Assert.Equal((uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]), fb.Get(px, py));
                }
            }
        Assert.True(opaque > 10000);
        Assert.True(transparent > 100, "preview shapes carry a transparent border");
    }

    [GameDataFact]
    public void ChangingTheCarChangesThePreviewShape()
    {
        var menu = Open(PlayerOne);
        var scene = new MenuScene(menu);
        var a = new FrameBuffer();
        scene.Render(a);
        menu.HandleKey(MenuKey.Right);
        var b = new FrameBuffer();
        scene.Render(b);
        Assert.True(Differs(a, b, 0xD3 + 20, 0x13C + 20, 0xD3 + 280, 0x13C + 120));
        string expectedShape = NFS.Core.Constants.MenuArtConstants.PlayerCarShapeNames[menu.Config.P1Car];
        Assert.Equal("car9", expectedShape);
        var image = Image(MainArt().Art, expectedShape);
        int o = (60 * image.Width + 150) * 4;
        Assert.Equal((uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]), b.Get(0xD3 + 150, 0x13C + 60));
    }

    [GameDataFact]
    public void MenuWithoutFrontEndDataFallsBackWithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nfs2se-nodata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, GameFiles.DataDirName));   // a game dir without front-end files
        Directory.CreateDirectory(Path.Combine(dir, GameFiles.FeDirName));
        try
        {
            var menu = MenuController.Load();
            var scene = new MenuScene(menu, dir);
            var fb = new FrameBuffer();
            foreach (var screen in new[] { Main, GameSetup, Location, PlayerOne })
            {
                menu = MenuController.Load();
                scene = new MenuScene(menu, dir);
                foreach (var key in Paths[screen]) menu.HandleKey(key);
                scene.Render(fb);
                Assert.Null(scene.BackgroundSource);
                Assert.False(scene.UsesOriginalFonts);
                Assert.All(fb.Rgba.Where((_, n) => n % 4 == 3), a => Assert.Equal(255, a));
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class GameDataTheoryAttribute : TheoryAttribute
{
    public GameDataTheoryAttribute() { if (NFS.Data.GameFiles.TryFindGameDir() is null) Skip = "game data (game_src/NFS2SE) not found"; }

}
