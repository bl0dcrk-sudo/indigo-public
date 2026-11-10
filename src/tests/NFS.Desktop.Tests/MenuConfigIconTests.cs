using NFS.Data;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Configuration icon bar of the menu: the right column of the main screen [ref:2786]
/// (type 1) and of the setup screens (type 2), drawn afterwards. [ref:2787]</summary>
public class MenuConfigIconTests
{
    const string Main = "main", Location = "location";

    static MenuController Open(params MenuKey[] keys)
    {
        var menu = MenuController.Load(null);
        foreach (var key in keys) menu.HandleKey(key);
        return menu;
    }

    static FrameBuffer Draw(MenuScene scene)
    {
        var fb = new FrameBuffer();
        scene.Render(fb);
        return fb;
    }

    /// <summary>Icon column: x &gt;= 496 (the icon furthest out to the left is at 0x1DF + 0x1C = 507), above the legend
    /// of the keys at y 465.</summary>
    const int IconsX0 = 496, IconsX1 = 640, IconsY1 = 460;

    /// <summary>Screen background (blk6/blk4 + side/gren/logo) and the expected icons in the original drawing order.</summary>
    static FrameBuffer Expected(string background, params (string Name, int X, int Y)[] icons)
        => Expected(background, MenuScene.IconSquashFull, icons);

    /// <summary>Reference frame: background + icons (the first two scaled by the squash factor - see
    /// <see cref="MenuScene.SquashFactor"/> and <see cref="BlitSquashed"/>).</summary>
    static FrameBuffer Expected(string background, int squash, params (string Name, int X, int Y)[] icons)
    {
        var art = Fsh.Load("FEDATA/PC/ART/MAIN.QFS");
        var fb = new FrameBuffer();
        fb.Clear(0, 0, 0);
        void Blit(string name)
        {
            var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
            OriginalHud.Blit(fb, image, image.PosX, image.PosY);
        }
        Blit(background);
        foreach (string name in new[] { "side", "gren", "logo" }) Blit(name);
        for (int i = 0; i < icons.Length; i++)
        {
            var (name, x, y) = icons[i];
            var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
            if (i < 2 && squash != MenuScene.IconSquashFull) BlitSquashed(fb, image, x, y, squash);
            else OriginalHud.Blit(fb, image, x, y);
        }
        return fb;
    }

    /// <summary>Icon of width <c>factor * w / 64</c> (clamped to zero and to [ref:2788]
    /// 16 bits), drawn from its own centre (<c>x + w/2 - |w/2|</c>); source step 16.16, first sample is
    /// half a step; with a negative factor the step is negative, and the start is shifted by the whole source (mirror).</summary>
    static void BlitSquashed(FrameBuffer fb, FshImage image, int x, int y, int squash)
    {
        int w = (short)(squash * image.Width / 64), half = w / 2;
        if (half < 1) half = -half;
        int stepX = (image.Width << 16) / w, startX = stepX / 2 + (w < 0 ? image.Width << 16 : 0);
        for (int dy = 0; dy < image.Height; dy++)
            for (int dx = 0; dx < Math.Abs(w); dx++)
            {
                int srcX = (startX + dx * stepX) >> 16;
                if ((uint)srcX >= (uint)image.Width) continue;
                int px = x + image.Width / 2 - half + dx, py = y + dy, o = (dy * image.Width + srcX) * 4;
                if ((uint)px >= (uint)fb.Width || (uint)py >= (uint)fb.Height || image.Rgba[o + 3] == 0) continue;
                fb.Set(px, py, (uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]));
            }
    }

    /// <summary>Every pixel of the icon column (x 496..639, y 0..459) must match exactly the background + these icons -
    /// this checks both the variant choice and the position. Positions: x = position from the FSH header + 0x1C (rows 0/1)
    /// or 0x19 (the rest), y = position + table [-9, -9, 0x45, 0xB0, 0x10D, 0x16B]. Works only on the main screen [ref:2789]
    /// (blk6), where nothing else is drawn in this column.</summary>
    static void AssertIcons(FrameBuffer fb, string background, params (string Name, int X, int Y)[] icons)
        => AssertIcons(fb, background, MenuScene.IconSquashFull, icons);

    /// <summary>As above, but the highlighted rows (the first two list icons: 0 and 1) are scaled
    /// by the animation factor (0x40 = full width). [ref:2790]</summary>
    static void AssertIcons(FrameBuffer fb, string background, int squash, params (string Name, int X, int Y)[] icons)
    {
        var expected = Expected(background, squash, icons);
        for (int y = 0; y < IconsY1; y++)
            for (int x = IconsX0; x < IconsX1; x++)
                Assert.True(expected.Get(x, y) == fb.Get(x, y), $"pixel {x},{y} (squash {squash})");
    }

    /// <summary>Opaque pixels of one icon. On the setup screens (type 2) the right column also holds the text
    /// of item values (up to x 517, y 111) and the track preview (up to x 517, y 316+), so we compare only the icon's own
    /// pixels - it is drawn over them anyway. [ref:2791]</summary>
    static void AssertIcon(FrameBuffer fb, string name, int x, int y)
    {
        var art = Fsh.Load("FEDATA/PC/ART/MAIN.QFS");
        var image = art.Decode(art.Find(name)!, art.GlobalPalette())!;
        int opaque = 0;
        for (int sy = 0; sy < image.Height; sy++)
            for (int sx = 0; sx < image.Width; sx++)
            {
                int px = x + sx, py = y + sy, o = (sy * image.Width + sx) * 4;
                if (py >= IconsY1 || px >= IconsX1 || image.Rgba[o + 3] == 0) continue;
                opaque++;
                Assert.True((uint)(image.Rgba[o] << 16 | image.Rgba[o + 1] << 8 | image.Rgba[o + 2]) == fb.Get(px, py),
                    $"{name} pixel {px},{py}");
            }
        Assert.True(opaque > 200, name);
    }

    // Default config (MenuController.Load): game type 0 (sin), race 0 (rac), track 0 (cir), car 0 (mac),
    // opponent 12 → class A, 2 opponents. Highlighted rows: 0 (menu item 0) and 1 (rule row==1
    // when hrow==0) - bright "…5", the others dark "…d".
    static readonly (string Name, int X, int Y)[] DefaultStrip =
        [("sin5", 508, 20), ("rac5", 553, 28), ("cird", 529, 92), ("macd", 518, 232), ("Ad  ", 512, 406)];

    [GameDataFact]
    public void MainMenuDrawsTheIconStripOfTheCurrentConfiguration()
    {
        var menu = Open();
        Assert.Equal(Main, menu.CurrentScreen.Id);
        // Rows 0..5 (game type, race, track, player 1 car, skipped two-player game, opponent) - exactly
        // these icons and only these; row 4 is not drawn in the one-player game.
        AssertIcons(Draw(new MenuScene(menu)), "blk6", DefaultStrip);
    }

    [GameDataFact]
    public void IconsFollowTheTrackAndCarOfTheConfiguration()
    {
        var menu = Open();
        var scene = new MenuScene(menu);
        AssertIcons(Draw(scene), "blk6", DefaultStrip);
        menu.Config.Track = 1;                     // track 1 = out, car 2 = 355
        menu.Config.P1Car = 2;
        AssertIcons(Draw(scene), "blk6",
            ("sin5", 508, 20), ("rac5", 553, 28), ("outd", 513, 97), ("355d", 512, 212), ("Ad  ", 512, 406));
        menu.Config.RaceType = 2;                  // knockout: car table index 0xF = "tko", track shown as 0 [ref:2792]
        AssertIcons(Draw(scene), "blk6",
            ("sin5", 508, 20), ("nok5", 553, 28), ("cird", 529, 92), ("355d", 512, 212), ("tkod", 532, 387));
    }

    [GameDataFact]
    public void HighlightingAMainMenuItemLightsItsIconAndKeepsItOnTheSubScreens()
    {
        var menu = Open();
        var scene = new MenuScene(menu);
        menu.HandleKey(MenuKey.Down);              // item 1 "location" → row 2 (track) bright
        AssertIcons(Draw(scene), "blk6",
            ("sind", 508, 20), ("racd", 553, 28), ("cir5", 529, 92), ("macd", 518, 232), ("Ad  ", 512, 406));
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(Location, menu.CurrentScreen.Id);
        // The setup screen also draws the value text and the track preview in this column - compare single icons.
        var location = Draw(scene);
        AssertIcon(location, "sind", 508, 20);
        AssertIcon(location, "racd", 553, 28);
        AssertIcon(location, "cir5", 529, 92);
        AssertIcon(location, "macd", 518, 232);
        AssertIcon(location, "Ad  ", 512, 406);
        menu.HandleKey(MenuKey.Right);             // track change on the track screen: new icon, still bright
        var changedTrack = Draw(scene);
        AssertIcon(changedTrack, "out5", 513, 97);
        AssertIcon(changedTrack, "sind", 508, 20);
        AssertIcon(changedTrack, "racd", 553, 28);
        AssertIcon(changedTrack, "macd", 518, 232);
        AssertIcon(changedTrack, "Ad  ", 512, 406);
    }

    [GameDataFact]
    public void SelectedIconPulsesBetweenBrightAndDarkVariantsAfterTheChangeDelay()
    {
        var menu = Open();
        var scene = new MenuScene(menu);
        AssertIcons(Draw(scene), "blk6", DefaultStrip);
        scene.Advance(0.4);                        // 51 ticks at 128 Hz > 0x28 since the value changed
        // Frames 2..5: anim 0x3C, 0x78, 0xB4, 0xF0 - all <= 0x100, so still bright, but the icon is
        // narrowed by the squash factor (0x40 = full width). [ref:2793]
        for (int i = 1; i <= 4; i++)
            AssertIcons(Draw(scene), "blk6", MenuScene.SquashFactor(i * MenuScene.IconAnimStep), DefaultStrip);
        // Frame 6: anim 0x12C - the highlighted rows 0 and 1 switch to the dark variants, negative factor
        // (icon mirrored).
        AssertIcons(Draw(scene), "blk6", MenuScene.SquashFactor(5 * MenuScene.IconAnimStep),
            ("sind", 508, 20), ("racd", 553, 28), ("cird", 529, 92), ("macd", 518, 232), ("Ad  ", 512, 406));
    }
}
