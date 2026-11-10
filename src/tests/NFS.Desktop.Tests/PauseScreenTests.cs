using NFS.Core;
using NFS.Data;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Pause screen (Pause??.QFS) and menu logic; end race(1) → results. [ref:2856]</summary>
public class PauseScreenTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);
    static DesktopInput Idle => new(default, [], false);
    static DesktopInput Pause => new(default, [], true);
    static long[] State(RaceDrive d) => d.Runner.Cars.SelectMany(c => CarDump.Slots.Select(s => s.Get(c.Obj))).ToArray();

    /// <summary>Independent decode: indices into the HUD.FSH palette (master palette 0..25), 0xFF transparent.</summary>
    static (Fsh Art, Func<string, FshImage> Image) Art()
    {
        var fsh = Fsh.Load("GAMEDATA/PAUSE/PAUSEEN.QFS");
        var pal = Fsh.Load("GAMEDATA/DASHHUD/PC/HUD.FSH").GlobalPalette()!;
        return (fsh, n =>
        {
            var e = fsh.Find(n)!; var idx = fsh.Indices(e); var rgba = new byte[idx.Length * 4];
            for (int i = 0; i < idx.Length; i++)
                if (idx[i] != 0xFF) { pal.AsSpan(idx[i] * 4, 3).CopyTo(rgba.AsSpan(i * 4)); rgba[i * 4 + 3] = 255; }
            return new FshImage(n, e.Main.Width, e.Main.Height, rgba, 0, 0, e.Main.PosX, e.Main.PosY);
        });
    }

    /// <summary>Every opaque pixel of the shape appears at its own position in the frame (items are drawn last, over the backdrop).</summary>
    static bool Drawn(FrameBuffer fb, FshImage img, int ox, int oy)
    {
        int checkedPixels = 0;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                if (img.Rgba[o + 3] == 0) continue;
                uint rgb = (uint)(img.Rgba[o] << 16 | img.Rgba[o + 1] << 8 | img.Rgba[o + 2]);
                if ((fb.Get(ox + x, oy + y) & 0xFFFFFF) != rgb) return false;
                checkedPixels++;
            }
        return checkedPixels > 100;
    }

    [GameDataFact]
    public void ScreenDrawsOriginalShapesAtTheirPositions_SelectedItemUsesHighlightedVariant()
    {
        var (_, image) = Art();
        var menu = new PauseMenu();
        menu.Open(0);
        var fb = new FrameBuffer();
        menu.Render(fb);
        var ps01 = image("ps01"); var ps10 = image("ps10"); var ps50 = image("ps50");
        Assert.True(Drawn(fb, ps01, ps01.PosX, ps01.PosY), "selected RESUME = ps01");
        Assert.True(Drawn(fb, ps10, ps10.PosX, ps10.PosY), "RESTART not selected = ps10");
        Assert.True(Drawn(fb, ps50, ps50.PosX, ps50.PosY), "QUIT TO SYSTEM not selected = ps50");
        var logo = image("logo");
        Assert.True(Drawn(fb, logo, logo.PosX, logo.PosY));
        menu.Handle(MenuKey.Down);
        var fb2 = new FrameBuffer(); menu.Render(fb2);
        var ps11 = image("ps11"); var ps00 = image("ps00");
        Assert.True(Drawn(fb2, ps11, ps11.PosX, ps11.PosY)); Assert.True(Drawn(fb2, ps00, ps00.PosX, ps00.PosY));
        Assert.False(Drawn(fb, ps11, ps11.PosX, ps11.PosY));
        // Background 'back' is mirrored into four quadrants: pixel (x, y) = (639 - x, y) = (x, 479 - y).
        Assert.Equal(fb.Get(3, 5), fb.Get(636, 5)); Assert.Equal(fb.Get(3, 5), fb.Get(3, 474));
    }

    [GameDataFact]
    public void RestartIsGreyedAndSkippedOutsideSingleRace_ConfirmationCaptionFollowsRaceType()
    {
        var (_, image) = Art();
        var menu = new PauseMenu(); menu.Open(1); // tournament
        var fb = new FrameBuffer(); menu.Render(fb);
        var ps12 = image("ps12");
        Assert.True(Drawn(fb, ps12, ps12.PosX, ps12.PosY), "greyed restart");
        menu.Handle(MenuKey.Down); Assert.Equal(2, menu.Selected); // 0 → 2 [ref:2857]
        menu.Handle(MenuKey.Up); Assert.Equal(0, menu.Selected);   // 2 → 0 [ref:2858]
        menu.Handle(MenuKey.Up); Assert.Equal(5, menu.Selected);
        menu.Open(2); for (int i = 0; i < 3; i++) menu.Handle(MenuKey.Down);
        Assert.Equal(4, menu.Selected);
        menu.Handle(MenuKey.Enter);
        var ko = new FrameBuffer(); menu.Render(ko);
        var qtb1 = image("qtb1"); var noo1 = image("noo1"); var yes2 = image("yes2");
        Assert.True(Drawn(ko, qtb1, qtb1.PosX, qtb1.PosY), "QUIT KNOCKOUT?");
        Assert.True(Drawn(ko, noo1, noo1.PosX, noo1.PosY), "default NO highlighted");
        Assert.True(Drawn(ko, yes2, yes2.PosX, yes2.PosY));
        menu.Handle(MenuKey.Left);
        var yes = new FrameBuffer(); menu.Render(yes);
        var yes1 = image("yes1"); Assert.True(Drawn(yes, yes1, yes1.PosX, yes1.PosY));
        menu.Open(0); for (int i = 0; i < 4; i++) menu.Handle(MenuKey.Down);
        menu.Handle(MenuKey.Enter);
        var race = new FrameBuffer(); menu.Render(race);
        var qtbx = image("qtbx"); Assert.True(Drawn(race, qtbx, qtbx.PosX, qtbx.PosY), "QUIT RACE?");
    }

    [GameDataFact]
    public void SliderBarsFollowVolume()
    {
        var menu = new PauseMenu(); menu.Open(0); menu.SetVolumes(127, 0);
        var fb = new FrameBuffer(); menu.Render(fb);
        var (_, image) = Art(); var slid = image("slid");
        Assert.True(Drawn(fb, slid, 0x16C, 0x94), "music full: the whole yellow bar is visible");
        Assert.False(Drawn(fb, slid, 0x16C, 0xC0), "effects 0: the bar is covered");
        Assert.Equal(0x1F1F1Fu, fb.Get(0x16C + 80, 0xC0 + 6) & 0xFFFFFF); // palette index 9 cover
    }

    [GameDataFact]
    public void GlyphsHaveNoOpaqueBoxAndSelectedItemIsClearlyBrighter()
    {
        var (_, image) = Art();
        var menu = new PauseMenu(); menu.Open(0);
        var fb = new FrameBuffer(); menu.Render(fb);
        var ps01 = image("ps01");
        // Transparent shape pixels (index 0xFF) show the mirrored backdrop, never white.
        int transparent = 0;
        for (int y = 0; y < ps01.Height; y++)
            for (int x = 0; x < ps01.Width; x++)
                if (ps01.Rgba[(y * ps01.Width + x) * 4 + 3] == 0)
                {
                    transparent++;
                    uint c = fb.Get(ps01.PosX + x, ps01.PosY + y) & 0xFFFFFF;
                    Assert.True((c >> 16 & 0xFF) < 100 && (c >> 8 & 0xFF) < 100 && (c & 0xFF) < 100, $"box pixel {x},{y} = {c:X6}");
                }
        Assert.True(transparent > 500);
        // Selected RESUME (index 25 = white) vs unselected RESTART (index 19 = grey 138): mean brightness differs clearly.
        static double Mean(FrameBuffer f, FshImage img, string name)
        {
            double sum = 0; int n = 0;
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                    if (img.Rgba[(y * img.Width + x) * 4 + 3] != 0) { sum += f.Get(img.PosX + x, img.PosY + y) & 0xFF; n++; }
            return sum / n;
        }
        var sel = Mean(fb, image("ps01"), "ps01"); var uns = Mean(fb, image("ps10"), "ps10");
        Assert.True(sel - uns > 30, $"selected {sel:F0} vs unselected {uns:F0}");
    }

    [GameDataFact]
    public void OpeningNavigatingAndResumingPauseNeverChangesTheRace_VolumesPersist()
    {
        var a = new GameLoop(autopilot: true); a.StartRace(new RaceScene("replay"));
        var b = new GameLoop(autopilot: true); b.StartRace(new RaceScene("replay"));
        for (int i = 0; i < 320; i++) { a.Frame(1.0 / 32, Idle); b.Frame(1.0 / 32, Idle); }
        b.Frame(0, Pause); Assert.True(b.Drive!.Paused);
        int before = Math.Min((int)b.Menu.Controller.Config[0x5D], 127);
        int effects = Math.Min((int)b.Menu.Controller.Config[0x5E], 127);
        var fb = new FrameBuffer();
        foreach (var k in new[] { MenuKey.Down, MenuKey.Down, MenuKey.Left, MenuKey.Down, MenuKey.Right, MenuKey.Up })
        { b.Frame(5, Input(k)); b.Render(fb); }
        Assert.Equal(before - 3, b.Menu.Controller.Config[0x5D]); // music slider moved by one step
        Assert.Equal(Math.Min(effects + 3, 127), b.Menu.Controller.Config[0x5E]); // Right on the effects slider
        b.Frame(30, Pause); Assert.False(b.Drive.Paused);
        for (int i = 0; i < 160; i++) { a.Frame(1.0 / 32, Idle); b.Frame(1.0 / 32, Idle); }
        Assert.Equal(a.Drive!.Runner.Tick, b.Drive.Runner.Tick);
        Assert.Equal(State(a.Drive), State(b.Drive));
    }

    [GameDataFact]
    public void EndRaceFromPauseOpensResultsWithEstimatedTimesAndAbortedEntrants()
    {
        var loop = new GameLoop(autopilot: true); loop.StartRace(new RaceScene("replay"));
        for (int i = 0; i < 700; i++) loop.Frame(1.0 / 32, Idle);
        Assert.True(loop.Drive!.Runner.Tick > 0x200);
        loop.Frame(0, Pause);
        for (int i = 0; i < 4; i++) loop.Frame(0, Input(MenuKey.Down));
        loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Left)); loop.Frame(0, Input(MenuKey.Enter));
        var c = loop.Menu.Controller;
        Assert.Equal(MenuStatus.Results, c.Status);
        var rows = c.Results!.Racers;
        Assert.All(rows, r => Assert.Equal(1, r.RaceState));
        Assert.All(rows, r => Assert.True(r.Estimated));
        Assert.All(rows.Where(r => r.Human || r.RaceTicks > 0), r => Assert.True(r.RaceTicks > 0));
        var fb = new FrameBuffer(); loop.Render(fb);
    }
}
