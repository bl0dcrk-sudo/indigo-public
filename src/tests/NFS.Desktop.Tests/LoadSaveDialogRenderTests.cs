using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Modal windows of the Load/Save screen: frame (question, message, name field) and list panel. [ref:2762]</summary>
public class LoadSaveDialogRenderTests
{
    static DesktopInput Input(MenuKey key) => new(default, [key], false);
    static void Type(GameLoop loop, string text) => loop.Frame(0, new DesktopInput(default, [], false, Typed: text.ToCharArray()));

    /// <summary>Screenshot for review (PNG) - only when <c>NFS_DIALOG_REVIEW_DIR</c> is set.</summary>
    static void Review(FrameBuffer fb, string name)
    {
        if (Environment.GetEnvironmentVariable("NFS_DIALOG_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir); fb.SavePng(Path.Combine(dir, name));
    }

    static GameLoop OpenLoadSave(string dir)
    {
        var loop = new GameLoop(null, false, Path.Combine(dir, "settings.json"));
        var m = loop.Menu.Controller;
        foreach (string item in new[] { "options", "load/save" })
        {
            while (m.CurrentScreen.Items[m.SelectedIndex].Text != item) loop.Frame(0, Input(MenuKey.Down));
            loop.Frame(0, Input(MenuKey.Enter));
        }
        return loop;
    }

    static void Select(GameLoop loop, string item)
    {
        var m = loop.Menu.Controller;
        while (m.CurrentScreen.Items[m.SelectedIndex].Text != item) loop.Frame(0, Input(MenuKey.Down));
    }

    static FrameBuffer Render(GameLoop loop)
    {
        var fb = new FrameBuffer();
        loop.Menu.Render(fb);
        return fb;
    }

    static List<(int X, int Y)> Changed(FrameBuffer a, FrameBuffer b, int x0, int y0, int x1, int y1)
    {
        var diff = new List<(int, int)>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (a.Get(x, y) != b.Get(x, y)) diff.Add((x, y));
        return diff;
    }

    [GameDataFact]
    public void LoadSaveItemsHaveNoSlotValueColumnLikeTheOriginal()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            var m = OpenLoadSave(dir).Menu.Controller;
            Assert.Equal("load-save", m.CurrentScreen.Id);
            foreach (var item in m.CurrentScreen.Items.Where(i => i.Kind == 2))
                Assert.Equal(item.Text, m.ItemText(item));
            Assert.Equal(1, m.SelectedIndex);                       // "save tournament" is disabled without a series → "load tournament" is selected
            Assert.NotEqual(0, m.CurrentScreen.Items[0].Flags & 1);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ConfirmDialogDrawsTheBarLogoQuestionAndHighlightsNoByDefault()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            var loop = OpenLoadSave(dir);
            var m = loop.Menu.Controller;
            Select(loop, "delete tournament");
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "saves")).FullName, "abc.tou"), []);
            var before = Render(loop);
            loop.Frame(0, Input(MenuKey.Enter)); loop.Frame(0, Input(MenuKey.Enter));   // list → choose "abc" → question
            Assert.Equal(0x2EB, m.Dialog!.TextId);
            var fb = Render(loop);
            Review(fb, "dialog-confirm.png");
            var text = TextFile.Load("ENG");
            var font = new FrontEndFont().Font(18);
            // frame: height 0x3C centred (y 210..270), width = max(header + 0x3C, content) + 0x1E
            int width = font.Measure(text[0x2EB]) + 0x3C + 0x1E, x0 = (640 - width) / 2, y0 = 210;
            Assert.NotEmpty(Changed(before, fb, x0, y0, x0 + width, y0 + 0x3C));
            Assert.Empty(Changed(before, fb, 0, 0, 640, y0 - 0x30));          // nothing changes above the logo (y0 − 39) or the bar
            Assert.Empty(Changed(before, fb, 0, y0 + 0x3C, 640, 480));        // nor below the frame
            // "no" (x 0x168) is highlighted, "yes" (x 0x118) is not: the colours differ in the text strip
            var yes = Changed(before, fb, 0x118 - 30, y0 + 0xF, 0x118 + 30, y0 + 0xF + 18);
            var no = Changed(before, fb, 0x168 - 30, y0 + 0xF, 0x168 + 30, y0 + 0xF + 18);
            Assert.NotEmpty(yes); Assert.NotEmpty(no);
            uint Brightest(List<(int X, int Y)> px) => px.Select(p => fb.Get(p.X, p.Y)).Max(c => (c >> 16 & 255) + (c >> 8 & 255) + (c & 255));
            Assert.True(Brightest(no) != Brightest(yes));
            loop.Frame(0, Input(MenuKey.Left));                               // "yes" selected
            var swapped = Render(loop);
            Assert.NotEmpty(Changed(fb, swapped, 0x118 - 30, y0 + 0xF, 0x168 + 30, y0 + 0xF + 18));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void TextEntryDialogShowsPromptTypedNameAndBlinkingCursor()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            var loop = OpenLoadSave(dir);
            var m = loop.Menu.Controller;
            m.Config.RaceType = 1;
            Assert.Null(m.Dialog);
            var before = Render(loop);
            m.Config.RaceType = 0;
            // "save replay" (REPLAY.RPY exists on the disc) → name field, TEXT header 0x2E2 [ref:2763]
            Select(loop, "save replay"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Text, m.Dialog!.Kind);
            var empty = Render(loop);
            Review(empty, "dialog-text.png");
            Type(loop, "SPEED");
            var typed = Render(loop);
            Assert.NotEmpty(Changed(empty, typed, 200, 225, 440, 265));      // the typed name changes the frame contents
            loop.Frame(0.2, new DesktopInput(default, [], false));            // 0.2 s · 128 Hz > 0xE: cursor "_" on
            Assert.True(m.Dialog!.CursorVisible);
            var cursor = Render(loop);
            Assert.NotEmpty(Changed(typed, cursor, 300, 225, 480, 265));
            Review(cursor, "dialog-text-cursor.png");
            Assert.NotEmpty(Changed(before, typed, 150, 170, 500, 280));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void MessageDialogDrawsItsTextAndClosesAfterItsTimeout()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            var loop = OpenLoadSave(dir);
            var m = loop.Menu.Controller;
            Select(loop, "save replay"); loop.Frame(0, Input(MenuKey.Enter)); Type(loop, "m1"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(0x2EE, m.Dialog!.TextId);
            var fb = Render(loop);
            Review(fb, "dialog-message.png");
            loop.Frame(2.0, new DesktopInput(default, [], false));            // 2 s < 350 / 128 s
            Assert.NotNull(m.Dialog);
            loop.Frame(0.8, new DesktopInput(default, [], false));            // total 2.8 s ≥ 2.73 s
            Assert.Null(m.Dialog);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ListDialogDrawsPanelRowsCancelTabAndScrollArrows()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            string saves = Directory.CreateDirectory(Path.Combine(dir, "saves")).FullName;
            for (int i = 0; i < 12; i++) File.WriteAllBytes(Path.Combine(saves, $"game{i:D2}.tou"), []);
            var loop = OpenLoadSave(dir);
            var m = loop.Menu.Controller;
            Select(loop, "load tournament");
            var before = Render(loop);
            loop.Frame(0, Input(MenuKey.Enter));
            var list = m.Dialog!;
            Assert.Equal(12, list.Items.Count);
            var top = Render(loop);
            Review(top, "dialog-list.png");
            // the "bsld" panel (438, 83) 202 × 230 and the "tsld" tab (596, 80) 44 × 224 cover the right side of the menu
            Assert.True(Changed(before, top, 438, 83, 640, 313).Count > 202 * 230 / 4);
            Assert.Empty(Changed(before, top, 0, 0, 430, 480));               // left part of the screen untouched
            // rows from x = 0x1E8, y = 0x62 + 0x16·i; the selected (first) one is white (colour 2), the others dark (colour 0)
            int White(FrameBuffer f, int row)
            {
                int n = 0;
                for (int y = 0x62 + row * 0x16; y < 0x62 + row * 0x16 + 18; y++)
                    for (int x = 0x1E8; x < 0x1E8 + 80; x++) if ((f.Get(x, y) & 0xE0E0E0) == 0xE0E0E0) n++;      // ramp: full coverage = 255·15/16 [ref:2764]
                return n;
            }
            int selectedWhite = White(top, 0), otherWhite = White(top, 1);
            Assert.True(selectedWhite > 2 * otherWhite, $"{selectedWhite} vs {otherWhite}");
            for (int i = 0; i < 9; i++) loop.Frame(0, Input(MenuKey.Down));   // scroll
            Assert.Equal(2, list.Scroll);                                      // selected row 9, visible 2..9
            var scrolled = Render(loop);
            Review(scrolled, "dialog-list-scrolled.png");
            Assert.NotEmpty(Changed(top, scrolled, 0x1E8, 0x62, 0x1E8 + 120, 0x62 + 8 * 0x16));
            loop.Frame(0, Input(MenuKey.Right));                              // "cancel" selected
            Assert.Equal(-1, list.Selected);
            var cancel = Render(loop);
            Review(cancel, "dialog-list-cancel.png");
            Assert.NotEmpty(Changed(scrolled, cancel, 0x26C, 0x76, 0x26C + 20, 0x76 + 6 * 18));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void DialogOverTheTournamentSummaryIsDrawnToo()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-dlg-").FullName;
        try
        {
            var loop = OpenLoadSave(dir);
            var m = loop.Menu.Controller;
            Select(loop, "save replay"); loop.Frame(0, Input(MenuKey.Enter));
            Assert.Equal(DialogKind.Text, m.Dialog!.Kind);
            Assert.True(Render(loop).Rgba.Length > 0);
        }
        finally { Directory.Delete(dir, true); }
    }
}
