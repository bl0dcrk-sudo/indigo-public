using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Drawing key names on the controls screen [ref:2734]
/// For action 0xB, in the value column (x = position + 0xD/0x13) the device label is drawn [ref:2735]
/// (TEXT 0x11D "keyboard"), and at a spacing of 0x42 (0x50 with font 18) the key name. Changing the binding changes
/// only those pixels; the position labels stay untouched.</summary>
public class ControlsScreenDrawTests
{
    /// <summary>Colour of a fully covered glyph pixel: the FFN ramp gives (colour * 15) >> 4 per channel. [ref:2736]</summary>
    static uint TextColour(int index)
    {
        uint c = NFS.Core.Constants.RenderConstants.FrontEndTextColours[index];
        return (c >> 16 & 255) * 15 >> 4 << 16 | (c >> 8 & 255) * 15 >> 4 << 8 | (c & 255) * 15 >> 4;
    }

    /// <summary>Controls screen: main → options → controls → setup. [ref:2737]</summary>
    static readonly MenuKey[] Path =
    [
        MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter,
        MenuKey.Down, MenuKey.Down, MenuKey.Enter, MenuKey.Enter
    ];

    static FrameBuffer Render(MenuController menu)
    {
        var fb = new FrameBuffer();
        new MenuScene(menu).Render(fb);
        return fb;
    }

    static int Differences(FrameBuffer a, FrameBuffer b, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }

    static int Count(FrameBuffer fb, int x0, int y0, int x1, int y1, uint rgb)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (fb.Get(x, y) == rgb) n++;
        return n;
    }

    [GameDataFact]
    public void ControlsScreenDrawsTheKeyNamesInTheValueColumn()
    {
        var menu = MenuController.Load();
        foreach (var key in Path) menu.HandleKey(key);
        Assert.Equal("controls-setup", menu.CurrentScreen.Id);
        Assert.Equal(0, menu.SelectedIndex);                                  // first item = steer left

        var text = TextFile.Load("ENG");
        var item = menu.CurrentScreen.Items[0];
        Assert.Equal(11, item.Action);                                        // akcja 0xB
        var e = text.Entries[item.TextId];
        int valueX = e.X + (e.FontSize < 18 ? 0xD : 0x13);
        int nameX = valueX + (e.FontSize == 18 ? 0x50 : 0x42);

        var assigned = Render(menu);
        var (device, name) = KeyBindings.Display(menu.Config.I32(item.ConfigOffset!.Value), text);
        Assert.Equal(text[0x11D], device);                                    // „keyboard”
        Assert.Equal(text[0x12B], name);                                      // steer left → TEXT 0x12B "left"

        // Pixels of the name in the value column, in the colour of the selected item.
        var font = new FrontEndFont().Font(e.FontSize);
        Assert.True(Count(assigned, nameX, e.Y, nameX + font.Measure(name), e.Y + e.FontSize, TextColour(e.HighlightColour)) > 0);
        Assert.Equal(0, Count(assigned, nameX, e.Y, nameX + font.Measure(name), e.Y + e.FontSize, TextColour(e.Colour)));

        // Without a binding the same column draws TEXT 0x133 "________", and the position labels and "keyboard" do not move.
        KeyBindings.Set(menu.Config, KeyAction.SteerLeft, 0);
        var unassigned = Render(menu);
        Assert.Equal(text[0x133], KeyBindings.Display(0, text).Name);
        Assert.True(Differences(assigned, unassigned, nameX, e.Y, nameX + font.Measure(name), e.Y + e.FontSize) > 0);
        // In this row only the key name changes; the position label and "keyboard" stay.
        // (An empty binding also greys out DONE, so we compare only the row band.) [ref:2738]
        // Without a binding the value column holds only TEXT 0x133 (no "keyboard"), and the key name disappears.
        Assert.True(Differences(assigned, unassigned, valueX, e.Y, nameX + font.Measure(name), e.Y + e.FontSize) > 0);
        Assert.Equal(0, Differences(assigned, unassigned, 0, e.Y, valueX, e.Y + e.FontSize));
    }

    /// <summary>Screen (action 0xC): two rows per item – player 1 in the value column, player 2 at x + 0x8A; on the selected [ref:2739]
    /// item the active player's column (config +0x08) has the highlight colour, the other column the normal colour.</summary>
    [GameDataFact]
    public void SplitControlsScreenDrawsTwoColumns_AndHighlightsTheActivePlayer()
    {
        var menu = MenuController.Load();
        menu.Config.GameType = 1;
        foreach (var target in new[] { "options", "controls", "controls-setup" })   // main → options → controls → setup [ref:2740]
        {
            int index = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == target).i;
            for (int guard = 0; menu.SelectedIndex != index && guard < 80; guard++) menu.HandleKey(MenuKey.Down);
            menu.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(MenuController.SplitControlsScreen, menu.CurrentScreen.Id);
        var text = TextFile.Load("ENG");
        var item = menu.CurrentScreen.Items[0];
        Assert.Equal(0xC, item.Action);
        var e = text.Entries[item.TextId];
        int valueX = e.X + (e.FontSize < 18 ? 0xD : 0x13);
        int nameOffset = e.FontSize == 18 ? 0x50 : 0x42;
        var font = new FrontEndFont().Font(e.FontSize);
        var (_, name1) = menu.KeyText(item, 0);
        var (_, name2) = menu.KeyText(item, 1);
        Assert.Equal("left", name1);   // player 1: ←
        Assert.Equal("S", name2);      // player 2: S
        int x1 = valueX + nameOffset, x2 = valueX + 0x8A + nameOffset;
        Assert.Equal(0, menu.SplitColumn);
        var first = Render(menu);
        Assert.True(Count(first, x1, e.Y, x1 + font.Measure(name1), e.Y + e.FontSize, TextColour(e.HighlightColour)) > 0);   // active column
        Assert.True(Count(first, x2, e.Y, x2 + font.Measure(name2), e.Y + e.FontSize, TextColour(e.Colour)) > 0);            // the other one
        Assert.Equal(0, Count(first, x2, e.Y, x2 + font.Measure(name2), e.Y + e.FontSize, TextColour(e.HighlightColour)));
        menu.HandleKey(MenuKey.Right);
        var second = Render(menu);
        Assert.True(Count(second, x2, e.Y, x2 + font.Measure(name2), e.Y + e.FontSize, TextColour(e.HighlightColour)) > 0);
        Assert.Equal(0, Count(second, x1, e.Y, x1 + font.Measure(name1), e.Y + e.FontSize, TextColour(e.HighlightColour)));
        // an empty entry in column 2 draws "________" (TEXT 0x133) and nothing else changes in column 1
        KeyBindings.SetSplit(menu.Config, 1, KeyAction.SteerLeft, 0);
        var empty = Render(menu);
        Assert.True(Differences(second, empty, valueX + 0x8A, e.Y, x2 + font.Measure(name2), e.Y + e.FontSize) > 0);
        Assert.Equal(0, Differences(second, empty, 0, e.Y, valueX + 0x8A, e.Y + e.FontSize));
        // visual review aid (headless): NFS_SPLIT_PNG_DIR set → the screen is dumped as PNG
        if (Environment.GetEnvironmentVariable("NFS_SPLIT_PNG_DIR") is { } dir)
        {
            Directory.CreateDirectory(dir);
            KeyBindings.SetSplit(menu.Config, 1, KeyAction.SteerLeft, KeyBindings.Word(0x1F, 'S'));
            File.WriteAllBytes(Path_(dir, "split_controls"), Render(menu).EncodePng());
        }
    }

    static string Path_(string dir, string name) => System.IO.Path.Combine(dir, name + ".png");
}
