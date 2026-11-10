using NFS.Desktop;
using NFS.Game.Cars;
using NFS.Game.FrontEnd;
using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>openspec modding-api-cars 4.4: the car panels and the showcase screen of the menu draw a mod car (3D preview, class label, description,
/// colours) and leave the original cars' screens exactly as they were. Uses the repo mod <c>mods/indigo.duster</c>.</summary>
public class ModMenuRenderTests
{
    static string RepoMods()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NFS2SE.sln"))) return Path.GetFullPath(Path.Combine(dir.FullName, "..", "mods"));
        throw new DirectoryNotFoundException("NFS2SE.sln");
    }

    static MenuController Open(bool mods, params MenuKey[] keys)
    {
        var menu = MenuController.Load(null);
        if (mods)
        {
            var diag = new ModDiagnostics();
            var loaded = ModLoader.Load(RepoMods(), diag);
            menu.Cars = CarCatalog.Build([VanillaCarSource.FromGameDir(null), new ModCarSource(loaded, diag)]).Catalog;
        }
        foreach (var key in keys) menu.HandleKey(key);
        return menu;
    }

    static FrameBuffer Draw(MenuController menu)
    {
        var fb = new FrameBuffer();
        new MenuScene(menu, null).Render(fb);
        return fb;
    }

    // PLAYER ONE (Down Down Enter), the car item selected; Left = the mod car when mods are loaded.
    static readonly MenuKey[] PlayerOne = [MenuKey.Down, MenuKey.Down, MenuKey.Enter];
    static readonly MenuKey[] Opponents = [MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter];

    /// <summary>The panel (0xD3, 0x13C, 0x118 x 0x80) of the car screens.</summary>
    const int PanelX = 0xD3, PanelY = 0x13C, PanelW = 0x118, PanelH = 0x80;

    static int Count(FrameBuffer fb, int x0, int y0, int w, int h, Func<uint, bool> test)
    {
        int n = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++) if (test(fb.Get(x, y))) n++;
        return n;
    }

    /// <summary>Orange paint of the first Duster colour (#C8691E): red clearly above blue, green in between.</summary>
    static bool Orange(uint rgb)
    {
        int r = (int)(rgb >> 16 & 255), g = (int)(rgb >> 8 & 255), b = (int)(rgb & 255);
        return r > 120 && r > g + 30 && g > b + 15;
    }

    [GameDataFact]
    public void PlayerPanel_ModCarShowsThePaintedPreviewAndFollowsTheColourChoice()
    {
        var menu = Open(true, [.. PlayerOne, MenuKey.Left]);
        var orange = Draw(menu);
        Assert.True(Count(orange, PanelX, PanelY, PanelW, PanelH, Orange) > 400, "the orange Duster is visible in the panel");

        // colour item: Right x 4 = the red colour of car.json; the panel follows
        foreach (var key in new[] { MenuKey.Down, MenuKey.Down, MenuKey.Right, MenuKey.Right, MenuKey.Right, MenuKey.Right })
            menu.HandleKey(key);
        var red = Draw(menu);
        Assert.True(Count(red, PanelX, PanelY, PanelW, PanelH, Orange) < Count(orange, PanelX, PanelY, PanelW, PanelH, Orange) / 4);
    }

    [GameDataFact]
    public void OriginalCarScreens_AreUnchangedByLoadedMods()
    {
        // the same original car with and without a mod in the catalog: identical pixels (car select, opponents, showcase)
        foreach (var keys in new MenuKey[][]
        {
            [.. PlayerOne],
            [.. PlayerOne, MenuKey.Right],
            [.. Opponents],
            [.. Opponents, MenuKey.Right],
            [.. PlayerOne, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter],
        })
            Assert.Equal(Draw(Open(false, keys)).Sha256(), Draw(Open(true, keys)).Sha256());
    }

    [GameDataFact]
    public void Showcase_ModCarDrawsPreviewAndTexts()
    {
        var menu = Open(true, [.. PlayerOne, MenuKey.Left, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter]);
        Assert.True(menu.InShowcase);
        Assert.NotNull(menu.ShowcaseModCar);
        var fb = Draw(menu);
        Assert.True(Count(fb, MenuScene.ModShowcaseX, MenuScene.ModShowcaseY, MenuScene.ModShowcaseWidth, MenuScene.ModShowcaseHeight, Orange) > 600);
        // text rows below the preview (name, maker, description) have bright pixels; the original car stats rows are not drawn
        Assert.True(Count(fb, 243, 180, 360, 100, rgb => (rgb >> 16 & 255) > 200 && (rgb >> 8 & 255) > 150) > 150);
        // moving on to an original car draws the original screen again (its photo background is not the dark gradient)
        menu.HandleKey(MenuKey.Right);
        Assert.Null(menu.ShowcaseModCar);
        Assert.NotEqual(fb.Sha256(), Draw(menu).Sha256());
    }

    [GameDataFact]
    public void OpponentPanel_ModCarShowsThePreview()
    {
        var menu = Open(true, [.. Opponents, MenuKey.Left]);
        Assert.Equal("car: Duster", menu.ItemText(menu.CurrentScreen.Items[menu.SelectedIndex]));
        Assert.True(Count(Draw(menu), PanelX, PanelY, PanelW, PanelH, Orange) > 400);
    }

    [GameDataFact]
    public void OpponentPanel_ClassGShowsTheDusterPreviewAndItsName()
    {
        var menu = Open(true, [.. Opponents, MenuKey.Right, MenuKey.Right, MenuKey.Right]);   // 12 -> 13 -> 14 -> class G (the only low class with a car)
        Assert.Equal("car: class G", menu.ItemText(menu.CurrentScreen.Items[menu.SelectedIndex]));
        var fb = Draw(menu);
        Assert.True(Count(fb, PanelX, PanelY, PanelW, PanelH, Orange) > 400);
        // the value row (x 330..460, y 148..170) shows the label "class G": different from the row of "no opponents" (next value) and of class C
        static uint[] Row(FrameBuffer f) => [.. Enumerable.Range(0, 130 * 22).Select(i => f.Get(330 + i % 130, 148 + i / 130))];
        var rowG = Row(fb);
        Assert.Contains(rowG, p => (p >> 16 & 255) > 200 && (p >> 8 & 255) > 150);   // bright text pixels
        menu.HandleKey(MenuKey.Right);
        Assert.NotEqual(rowG, Row(Draw(menu)));
        menu.HandleKey(MenuKey.Left); menu.HandleKey(MenuKey.Left);
        Assert.NotEqual(rowG, Row(Draw(menu)));
        menu.HandleKey(MenuKey.Right);
        // without a mod the class C screen of the original (collage) is a different picture
        Assert.NotEqual(Draw(Open(false, [.. Opponents, MenuKey.Right, MenuKey.Right])).Sha256(), fb.Sha256());
    }
}
