using Microsoft.Xna.Framework.Input;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;
using Xunit;

namespace NFS.Desktop.Tests;

/// <summary>Name editor for players 1 and 2 in the host (keys, game loop, settings, window) and the TEXT 0x134 hint on the controls screen
/// for two players. PNG (buffer only): <c>NFS_NAMES_PNG_DIR</c>. [ref:2846]</summary>
public class NameEditorSceneTests
{
    // ---- keys ----

    [Fact]
    public void TypedKeys_AreLowercaseUnlessShiftOrCapsLock_AndOnTheRisingEdgeOnly()
    {
        var mapper = new InputMapping();
        Assert.Equal(['a'], mapper.Read(new KeyboardState(Keys.A), default).Typed);
        Assert.Empty(mapper.Read(new KeyboardState(Keys.A), default).Typed!);                   // a held key does not repeat
        Assert.Equal(['B'], mapper.Read(new KeyboardState(Keys.LeftShift, Keys.B), default).Typed);
        Assert.Equal(['D'], mapper.Read(new KeyboardState([Keys.D], capsLock: true), default).Typed);
        Assert.Equal(['7', ' ', '-', '.'], mapper.Read(new KeyboardState(Keys.D7, Keys.Space, Keys.OemMinus, Keys.OemPeriod), default).Typed);
        Assert.Equal([(char)8], mapper.Read(new KeyboardState(Keys.Back), default).Typed);
        Assert.Empty(mapper.Read(new KeyboardState(Keys.Up, Keys.Enter), default).Typed!);      // menu keys are not characters
    }

    [Fact]
    public void TypedKeys_ShiftGivesUsLayoutSymbols_SoChatCanTypeEveryPrintableAscii()
    {
        // Chat report (task 26): Shift+0 produced '0' instead of ')', and the '/ ?' key produced nothing.
        var mapper = new InputMapping();
        string Typed(bool shift, params Keys[] keys)
        {
            var down = shift ? [Keys.LeftShift, .. keys] : keys;
            var t = new string(mapper.Read(new KeyboardState(down), default).Typed!.ToArray());
            mapper.Read(default, default);                                                      // release: the next press is a new edge
            return t;
        }
        Keys[] digits = [Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0];
        Keys[] oem = [Keys.OemMinus, Keys.OemPlus, Keys.OemOpenBrackets, Keys.OemCloseBrackets, Keys.OemPipe, Keys.OemSemicolon,
            Keys.OemQuotes, Keys.OemComma, Keys.OemPeriod, Keys.OemQuestion, Keys.OemTilde];
        var all = new HashSet<char>();
        foreach (var k in digits.Concat(oem))
        {
            all.UnionWith(Typed(false, k));
            all.UnionWith(Typed(true, k));
        }
        Assert.Equal(")", Typed(true, Keys.D0));
        Assert.Equal("?", Typed(true, Keys.OemQuestion));
        Assert.Equal("/", Typed(false, Keys.OemQuestion));
        for (char c = 'a'; c <= 'z'; c++) { all.Add(c); all.Add(char.ToUpperInvariant(c)); }
        all.Add(' ');
        var missing = Enumerable.Range(32, 95).Select(i => (char)i).Where(c => !all.Contains(c));
        Assert.Empty(missing);
        // CapsLock changes only letters, not digits or punctuation.
        Assert.Equal("1;", new string(mapper.Read(new KeyboardState([Keys.D1, Keys.OemSemicolon], capsLock: true), default).Typed!.ToArray()));
    }

    [Fact]
    public void ExistingCheatChars_AreUnchanged()
    {
        var mapper = new InputMapping();
        Assert.Equal(['F'], mapper.Read(new KeyboardState(Keys.F), default).Chars);
    }

    // ---- game loop ----

    internal static void PressRace(GameLoop loop)
    {
        var menu = loop.Menu.Controller;
        int race = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == "race").i;
        for (int guard = 0; menu.SelectedIndex != race && guard < 40; guard++) loop.Frame(0, new DesktopInput(default, [MenuKey.Down], false));
        loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false));
    }

    [GameDataFact]
    public void GameLoop_EditsBothNames_SavesThemInTheSettings_AndStartsTheRace()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-names-").FullName;
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var loop = new GameLoop(null, false, path);
            var menu = loop.Menu.Controller;
            menu.Config.GameType = 1; menu.Config.RaceType = 0; menu.Config.Track = 1; menu.Config.OpponentCount = 0; menu.Config.TrafficOff = 1;
            PressRace(loop);
            Assert.NotNull(menu.NameEditing);
            Assert.Equal(MenuStatus.Menu, menu.Status);
            Assert.Equal("Tai Pan", menu.NameEditing!.Text);   // name from the installation's CONFIG.DAT (settings migration), not the default
            loop.Frame(1.0 / 60, new DesktopInput(default, [], false, Typed: [.. new string((char)8, 7), .. "Anna"]));
            Assert.Equal("Anna", menu.NameEditing!.Text);
            loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false));
            loop.Frame(1.0 / 60, new DesktopInput(default, [], false, Typed: [(char)8, (char)8, 'B', 'e', 'n']));
            Assert.Equal("Ben", menu.NameEditing!.Text);
            loop.Frame(0, new DesktopInput(default, [MenuKey.Enter], false));
            Assert.Equal(MenuStatus.Race, menu.Status);
            Assert.NotNull(loop.Drive);
            var saved = UserSettings.Load(path);
            Assert.Equal(("Anna", "Ben"), (saved.P1Name, saved.P2Name));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void GameLoop_CursorBlinks_OnTheLoopClock()
    {
        var loop = new GameLoop();
        var menu = loop.Menu.Controller;
        menu.Config.GameType = 1;
        PressRace(loop);
        var editor = menu.NameEditing!;
        Assert.False(editor.CursorVisible);
        loop.Frame(15.0 / 60, new DesktopInput(default, [], false));
        Assert.True(editor.CursorVisible);
        loop.Frame(15.0 / 60, new DesktopInput(default, [], false));
        Assert.False(editor.CursorVisible);
    }

    // ---- editor window ----

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

    static void Dump(string name, FrameBuffer fb)
    {
        // Default out/scratch/names (a directory outside git); NFS_NAMES_PNG_DIR changes the location.
        string? dir = Environment.GetEnvironmentVariable("NFS_NAMES_PNG_DIR");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); dir == null && d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NFS2SE.sln"))) dir = Path.Combine(d.FullName, "out", "scratch", "names");
        if (dir == null) return;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".png"), fb.EncodePng());
    }

    [GameDataFact]
    public void EditorWindow_DarkensTheCentre_ShowsTheTypedName_AndBlinksTheCursor()
    {
        var menu = MenuController.Load();
        menu.Config.GameType = 1;
        int race = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == "race").i;
        for (int guard = 0; menu.SelectedIndex != race && guard < 40; guard++) menu.HandleKey(MenuKey.Down);
        var before = Render(menu);
        menu.HandleKey(MenuKey.Enter);
        Assert.NotNull(menu.NameEditing);
        var open = Render(menu);
        Assert.True(Differences(before, open, 150, 200, 490, 280) > 2000);    // window with text in the middle of the screen
        Assert.Equal(0, Differences(before, open, 0, 0, 640, 100));           // rest of the screen unchanged
        Dump("name_editor_player1", open);

        foreach (char ch in "Anna") menu.NameKey(ch);
        var typed = Render(menu);
        Assert.True(Differences(open, typed, 150, 230, 490, 275) > 0);        // name in the field changed pixels
        Assert.Equal(0, Differences(open, typed, 150, 200, 490, 225));        // header unchanged
        menu.AdvanceNameEditor(15);
        var cursor = Render(menu);
        Assert.True(Differences(typed, cursor, 150, 230, 490, 275) > 0);      // cursor "_" appeared
        Dump("name_editor_typed", cursor);
        menu.AdvanceNameEditor(15);
        Assert.Equal(0, Differences(typed, Render(menu), 0, 0, 640, 480));    // i znika

        menu.HandleKey(MenuKey.Enter);
        var second = Render(menu);
        Assert.True(Differences(cursor, second, 150, 200, 490, 280) > 0);     // „Player 2: enter name”
        Dump("name_editor_player2", second);
    }

    // ---- two-player controls screen: TEXT 0x134 ----

    static MenuController SplitControls(byte language = 0)
    {
        var menu = MenuController.Load();
        menu.Config.GameType = 1;
        menu.Config.Language = language;
        foreach (var target in new[] { "options", "controls", "controls-setup" })
        {
            int index = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == target).i;
            for (int guard = 0; menu.SelectedIndex != index && guard < 80; guard++) menu.HandleKey(MenuKey.Down);
            menu.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(MenuController.SplitControlsScreen, menu.CurrentScreen.Id);
        return menu;
    }

    [GameDataFact]
    public void SplitControlsHint_MovesLeftInLanguageTwo()
    {
        var english = Render(SplitControls());
        var other = Render(SplitControls(language: 2));
        Assert.True(Differences(english, other, 290, 0x4D, 0x16B + 100, 0x4D + 18) > 0);
    }

    [GameDataFact]
    public void SplitControlsScreen_DrawsThePressEnterHint_InTheHighlightColour()
    {
        var menu = SplitControls();
        var text = TextFile.Load("ENG");
        var entry = text.Entries[0x134];
        Assert.Equal("press ENTER to re-map", text[0x134]);
        var fb = Render(menu);
        Dump("split_controls_hint", fb);
        uint c = NFS.Core.Constants.RenderConstants.FrontEndTextColours[entry.HighlightColour];
        uint full = (c >> 16 & 255) * 15 >> 4 << 16 | (c >> 8 & 255) * 15 >> 4 << 8 | (c & 255) * 15 >> 4;
        int width = new FrontEndFont().Font(entry.FontSize).Measure(text[0x134]);
        int n = 0;
        for (int y = 0x4D; y < 0x4D + entry.FontSize; y++)
            for (int x = 0x16B; x < 0x16B + width; x++) if (fb.Get(x, y) == full) n++;
        Assert.True(n > 50, $"hint pixels: {n}");
        // only the two-player screen has the hint: the one-player controls screen does not draw it [ref:2847]
        var single = MenuController.Load();
        foreach (var target in new[] { "options", "controls", "controls-setup" })
        {
            int index = single.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == target).i;
            for (int guard = 0; single.SelectedIndex != index && guard < 80; guard++) single.HandleKey(MenuKey.Down);
            single.HandleKey(MenuKey.Enter);
        }
        Assert.Equal("controls-setup", single.CurrentScreen.Id);
        var plain = Render(single);
        int m = 0;
        for (int y = 0x4D; y < 0x4D + entry.FontSize; y++)
            for (int x = 0x16B; x < 0x16B + width; x++) if (plain.Get(x, y) == full) m++;
        Assert.True(m < n / 2);
    }
}
