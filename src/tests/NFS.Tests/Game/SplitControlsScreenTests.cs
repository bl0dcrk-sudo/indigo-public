using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Two-player controls screen (type 19, action 0xC): entered instead of the one at split screen [ref:3233]
/// player column switched with LEFT/RIGHT, capture within the column, evicting duplicates across both players, DONE, reset.</summary>
public class SplitControlsScreenTests
{
    static MenuController Menu(byte gameType)
    {
        var m = MenuController.Load(GameData.Dir);
        m.Config.GameType = gameType;
        return m;
    }

    /// <summary>BFS over position targets to the controls screen (as with one player); does not check which screen it ends on. [ref:3234]</summary>
    static void EnterControls(MenuController m)
    {
        var tree = MenuTables.Load(TextFile.Load("ENG", GameData.Dir));
        const string goal = "controls-setup";
        var prev = new Dictionary<string, (string From, int Index)>();
        var queue = new Queue<string>(["main"]);
        while (queue.Count > 0 && !prev.ContainsKey(goal))
        {
            var addr = queue.Dequeue();
            var items = tree[addr].Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i].Target is { } t && t != "main" && tree.Screens.Any(s => s.Id == t) && !prev.ContainsKey(t))
                { prev[t] = (addr, i); queue.Enqueue(t); }
        }
        var path = new List<(string From, int Index)>();
        for (string cur = goal; cur != "main"; cur = prev[cur].From) path.Insert(0, prev[cur]);
        foreach (var (from, index) in path)
        {
            Assert.Equal(from, m.CurrentScreen.Id);
            for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) m.HandleKey(MenuKey.Down);
            m.HandleKey(MenuKey.Enter);
        }
    }

    static void SelectRow(MenuController m, KeyAction action)
    {
        int offset = KeyBindings.SplitOffsetOf(0, action);
        int index = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.ConfigOffset == offset).i;
        for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) m.HandleKey(MenuKey.Down);
        Assert.Equal(index, m.SelectedIndex);
    }

    static MenuItem Done(MenuController m) => m.CurrentScreen.Items.Single(i => (i.Flags & 8) != 0);

    [GameFact]
    public void Screen_IsInTheTree_WithTenTwoColumnRows()
    {
        var tree = MenuTables.Load(TextFile.Load("ENG", GameData.Dir));
        var s = tree[MenuController.SplitControlsScreen];
        Assert.Equal(19, s.Type);   // [ref:3235]
        var rows = s.Items.Where(i => i.Action == 0xC).ToList();
        Assert.Equal(10, rows.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => KeyBindings.SplitOffsetOf(0, (KeyAction)i)).Order(), rows.Select(r => r.ConfigOffset!.Value).Order());
        Assert.Equal(10, s.Items.Take(10).Count(i => i.Action == 0xC));   // the rows are the first ten items (LEFT/RIGHT toggles the column only there)
        Assert.Single(s.Items, i => i.Action == 0x13);                    // reset
        Assert.Single(s.Items, i => (i.Flags & 8) != 0);                  // DONE
        Assert.Single(s.Items, i => (i.Flags & 0x10) != 0);               // CANCEL
    }

    [GameFact]
    public void ControlsEntry_GoesToTheTwoPlayerScreen_OnlyInSplitScreen()
    {
        var single = Menu(0);
        EnterControls(single);
        Assert.Equal("controls-setup", single.CurrentScreen.Id);
        var split = Menu(1);
        EnterControls(split);
        Assert.Equal(MenuController.SplitControlsScreen, split.CurrentScreen.Id);
        Assert.Equal(0, split.SelectedIndex);
    }

    [GameFact]
    public void LeftRight_SwitchTheEditedPlayer_OnlyOnTheKeyRows()
    {
        var m = Menu(1);
        EnterControls(m);
        Assert.Equal(0, m.SplitColumn);
        m.HandleKey(MenuKey.Right);
        Assert.Equal(1, m.SplitColumn);
        m.HandleKey(MenuKey.Left);
        Assert.Equal(0, m.SplitColumn);
        m.HandleKey(MenuKey.Left);
        Assert.Equal(1, m.SplitColumn);
        // rows 0..9 only: on the reset / DONE / CANCEL items LEFT and RIGHT do not touch the column
        int reset = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.Action == 0x13).i;
        while (m.SelectedIndex != reset) m.HandleKey(MenuKey.Down);
        m.HandleKey(MenuKey.Right);
        Assert.Equal(1, m.SplitColumn);
    }

    [GameFact]
    public void Capture_WritesTheSelectedPlayersColumn()
    {
        var m = Menu(1);
        EnterControls(m);
        SelectRow(m, KeyAction.SteerLeft);
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.Capturing);
        Assert.Equal(0, KeyBindings.GetSplit(m.Config, 0, KeyAction.SteerLeft));            // empty while waiting
        Assert.Equal(0x1F, KeyBindings.Scan(KeyBindings.GetSplit(m.Config, 1, KeyAction.SteerLeft)));   // the other column is untouched
        Assert.True(m.CaptureKey(0x2F, 'V'));
        Assert.Equal(KeyBindings.Word(0x2F, 'V'), KeyBindings.GetSplit(m.Config, 0, KeyAction.SteerLeft));
        m.HandleKey(MenuKey.Right);                                                         // player 2 column
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.CaptureKey(0x24, 'J'));
        Assert.Equal(KeyBindings.Word(0x24, 'J'), KeyBindings.GetSplit(m.Config, 1, KeyAction.SteerLeft));
        Assert.Equal(0x2F, KeyBindings.Scan(KeyBindings.GetSplit(m.Config, 0, KeyAction.SteerLeft)));
        // the one-player table is never touched
        Assert.Equal(0x4B, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.SteerLeft)));
    }

    [GameFact]
    public void Esc_DuringCapture_RestoresTheEntry()
    {
        var m = Menu(1);
        EnterControls(m);
        m.HandleKey(MenuKey.Right);
        SelectRow(m, KeyAction.Horn);
        int before = KeyBindings.GetSplit(m.Config, 1, KeyAction.Horn);
        m.HandleKey(MenuKey.Enter);
        Assert.Equal(0, KeyBindings.GetSplit(m.Config, 1, KeyAction.Horn));
        m.HandleKey(MenuKey.Escape);
        Assert.False(m.Capturing);
        Assert.Equal(before, KeyBindings.GetSplit(m.Config, 1, KeyAction.Horn));
    }

    [GameFact]
    public void SameKeyOfTheOtherPlayer_IsDisplaced_AndDoneStaysGreyedUntilEverythingIsAssigned()
    {
        var m = Menu(1);
        EnterControls(m);
        Assert.Equal(0, Done(m).Flags & 1);
        SelectRow(m, KeyAction.Horn);                                                       // player 1 horn := player 2's "S" (steer left)
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.CaptureKey(0x1F, 'S'));
        Assert.Equal(0, KeyBindings.GetSplit(m.Config, 1, KeyAction.SteerLeft));            // displaced
        Assert.NotEqual(0, Done(m).Flags & 1);                                              // DONE greyed out [ref:3236]
        SelectRow(m, KeyAction.SteerLeft);
        m.HandleKey(MenuKey.Right);
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.CaptureKey(0x2F, 'V'));
        Assert.Equal(0, Done(m).Flags & 1);
    }

    [GameFact]
    public void Reset_AsksFirst_AndRestoresBothPlayersDefaults()
    {
        var m = Menu(1);
        EnterControls(m);
        SelectRow(m, KeyAction.Horn);
        m.HandleKey(MenuKey.Enter); m.CaptureKey(0x2D, 'X');
        int reset = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.Action == 0x13).i;
        while (m.SelectedIndex != reset) m.HandleKey(MenuKey.Down);
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.ConfirmingReset);
        m.HandleKey(MenuKey.Escape);                                                        // "no"
        Assert.Equal(0x2D, KeyBindings.Scan(KeyBindings.GetSplit(m.Config, 0, KeyAction.Horn)));
        m.HandleKey(MenuKey.Enter);
        m.HandleKey(MenuKey.Left); m.HandleKey(MenuKey.Enter);                              // "yes" (the default selection is "no")
        Assert.Equal(KeyBindings.SplitBuiltin[0][(int)KeyAction.Horn], KeyBindings.Scan(KeyBindings.GetSplit(m.Config, 0, KeyAction.Horn)));
        Assert.True(KeyBindings.AllAssignedSplit(m.Config));
    }

    [GameFact]
    public void CancelOnTheScreen_RevertsKeysAndTheColumn()
    {
        var m = Menu(1);
        EnterControls(m);
        m.HandleKey(MenuKey.Right);
        SelectRow(m, KeyAction.Horn);
        m.HandleKey(MenuKey.Enter); m.CaptureKey(0x2D, 'X');
        m.HandleKey(MenuKey.Escape);                                                        // Esc = cancel: configuration backup restored
        Assert.Equal(0, m.SplitColumn);
        Assert.Equal(KeyBindings.SplitBuiltin[1][(int)KeyAction.Horn], KeyBindings.Scan(KeyBindings.GetSplit(m.Config, 1, KeyAction.Horn)));
    }
}
