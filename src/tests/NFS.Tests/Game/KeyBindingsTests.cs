using System.Text;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Key bindings (task 1f / item 11): KEYBOARD.TXT → defaults, key names as in TEXT.ENG [ref:3143]
/// the control screen (capture, conflicts, DONE, reset), load/save in settings.json.</summary>
public class KeyBindingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("nfs-keys-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static MenuController Menu() => MenuController.Load(GameData.Dir);
    static FeConfig Empty() => new(new byte[FeConfig.Size]);

    // Previous hard-coded values from InputMapping: arrows, B, C, space, H, A, Z.
    static readonly int[] OldScans = [0x4B, 0x4D, 0x48, 0x50, 0x30, 0x2E, 0x39, 0x23, 0x1E, 0x2C];

    [GameFact]
    public void DefaultKeyboardTxt_GivesTheOldHardcodedMapping()
    {
        var (scans, hud) = KeyBindings.LoadDefaults(GameData.Dir);
        Assert.Equal(OldScans, scans);
        Assert.Equal(0x3F, hud);                       // C1_TOGGLEHUD = SC_F5
        var m = Menu();
        for (int i = 0; i < KeyBindings.Count; i++)
            Assert.Equal(OldScans[i], KeyBindings.Scan(KeyBindings.Get(m.Config, (KeyAction)i)));
    }

    [GameFact]
    public void DefaultsAreIdenticalToTheOriginalConfigDatWords()
    {
        var original = FeConfig.Load(GameData.Dir);
        var m = Menu();
        for (int i = 0; i < KeyBindings.Count; i++)
            Assert.Equal(KeyBindings.Get(original, (KeyAction)i), KeyBindings.Get(m.Config, (KeyAction)i));
    }

    [GameFact]
    public void ScancodeTableComesFromTheExe()
    {
        var names = KeyBindings.ScancodeNames();
        Assert.Equal(82, names.Count);
        Assert.Equal(0x4D, names["SC_RIGHT"]);
        Assert.Equal(0x39, names["sc_space"]);
        Assert.Equal(0x3F, names["SC_F5"]);
    }

    [GameFact]
    public void ParserReadsRowsAndFallsBackPerRow()
    {
        var names = KeyBindings.ScancodeNames();
        var text = "C1_GAS = key,SC_W\n# C1_BRAKE = key,SC_S\nC1_HORN = key,SC_NOSUCHKEY\nC1_SHIFTUP = joy,SC_Q\nC1_TOGGLEHUD\t= key,SC_F6\nC1_LEFT=key,SC_A";
        var (scans, hud) = KeyBindings.ParseFile(Encoding.ASCII.GetBytes(text), names);
        Assert.Equal(0x11, scans[(int)KeyAction.Accelerate]);
        Assert.Equal(0x1E, scans[(int)KeyAction.SteerLeft]);
        Assert.Equal(0x50, scans[(int)KeyAction.Brake]);       // commented-out row
        Assert.Equal(0x23, scans[(int)KeyAction.Horn]);        // unknown SC_ name
        Assert.Equal(0x1E, scans[(int)KeyAction.ShiftUp]);     // a device other than key
        Assert.Equal(0x40, hud);
    }

    [GameFact]
    public void MissingOrGarbageFile_GivesDefaults()
    {
        var names = KeyBindings.ScancodeNames();
        Assert.Equal(OldScans, KeyBindings.ParseFile(null, names).Scans);
        Assert.Equal(OldScans, KeyBindings.ParseFile([0, 1, 2, 255, 10, 61, 61], names).Scans);
        Assert.Equal(OldScans, KeyBindings.ParseFile([], names).Scans);
    }

    [GameFact]
    public void KeyNamesFollowTheOriginalTextTable()
    {
        var text = TextFile.Load("ENG", GameData.Dir);
        string Name(int scan, int ascii = 0) => KeyBindings.Display(KeyBindings.Word(scan, ascii), text).Name;
        Assert.Equal("space", Name(0x39));
        Assert.Equal("enter", Name(0x1C));
        Assert.Equal("insert", Name(0x52)); Assert.Equal("delete", Name(0x53));
        Assert.Equal("home", Name(0x47)); Assert.Equal("end", Name(0x4F));
        Assert.Equal("page up", Name(0x49)); Assert.Equal("page down", Name(0x51));
        Assert.Equal("up", Name(0x48)); Assert.Equal("down", Name(0x50));
        Assert.Equal("left", Name(0x4B)); Assert.Equal("right", Name(0x4D));
        Assert.Equal("backspace", Name(0x0E)); Assert.Equal("tab", Name(0x0F)); Assert.Equal("center", Name(0x4C));
        Assert.Equal("A", Name(0x1E, 'A'));                       // other keys: “%c” [ref:3144]
        Assert.Equal("", Name(0x57));                              // F11: no character
        Assert.Equal("keyboard", KeyBindings.Display(KeyBindings.Word(0x1E, 'A'), text).Device);
        Assert.Equal((null, "________"), KeyBindings.Display(0, text));   // no assignment, TEXT 0x133
    }

    [GameFact]
    public void ControlsScreenShowsDefaultsAsInTheOriginal()
    {
        var m = Menu();
        GoToControls(m);
        var texts = m.CurrentScreen.Items.Where(i => i.Action == 11).ToDictionary(i => i.Text!, i => m.KeyText(i));
        Assert.Equal("left", texts["steer left"].Name);
        Assert.Equal("right", texts["steer right"].Name);
        Assert.Equal("up", texts["accelerate"].Name);
        Assert.Equal("down", texts["brake"].Name);
        Assert.Equal("space", texts["handbrake"].Name);
        Assert.Equal("A", texts["shift up"].Name);
        Assert.Equal("Z", texts["shift down"].Name);
        Assert.Equal("H", texts["horn"].Name);
        Assert.Equal("C", texts["camera view"].Name);
        Assert.Equal("B", texts["look behind"].Name);
    }

    [GameFact]
    public void Capture_AssignsKey_EmptiesItWhileWaiting_AndEscRestores()
    {
        var m = Menu();
        GoToControls(m);
        SelectAction(m, KeyAction.Accelerate);
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.Capturing);
        Assert.Equal(0, KeyBindings.Get(m.Config, KeyAction.Accelerate));        // "________" in progress
        m.HandleKey(MenuKey.Down);                                                // nawigacja zablokowana
        Assert.True(m.Capturing);
        Assert.False(m.CaptureKey(0x3B, 0));                                      // F1 ignorowany
        Assert.False(m.CaptureKey(0x1C, 0x0D));                                   // Enter ignorowany
        Assert.True(m.Capturing);
        m.HandleKey(MenuKey.Escape);
        Assert.False(m.Capturing);
        Assert.Equal(0x48, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.Accelerate)));

        m.HandleKey(MenuKey.Enter);
        Assert.True(m.CaptureKey(0x11, 'W'));
        Assert.False(m.Capturing);
        Assert.Equal(KeyBindings.Word(0x11, 'W'), KeyBindings.Get(m.Config, KeyAction.Accelerate));
    }

    [GameFact]
    public void Capture_SameKeyOnAnotherAction_UnbindsTheOtherAndDoneIsDisabled()
    {
        var m = Menu();
        GoToControls(m);
        SelectAction(m, KeyAction.Brake);
        m.HandleKey(MenuKey.Enter);
        m.CaptureKey(0x39, ' ');                                                  // space = handbrake
        Assert.Equal(0x39, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.Brake)));
        Assert.Equal(0, KeyBindings.Get(m.Config, KeyAction.Handbrake));          // wyparty
        var done = m.CurrentScreen.Items.Single(i => (i.Flags & 8) != 0);
        Assert.NotEqual(0, done.Flags & 1);                                       // DONE greyed out until everything is assigned
        SelectAction(m, KeyAction.Handbrake);
        m.HandleKey(MenuKey.Enter);
        m.CaptureKey(0x2D, 'X');
        done = m.CurrentScreen.Items.Single(i => (i.Flags & 8) != 0);
        Assert.Equal(0, done.Flags & 1);
    }

    [GameFact]
    public void ResetAsksForConfirmationAndRestoresKeyboardTxtDefaults()
    {
        var m = Menu();
        GoToControls(m);
        SelectAction(m, KeyAction.Horn);
        m.HandleKey(MenuKey.Enter); m.CaptureKey(0x2D, 'X');
        var reset = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.Action == 0x13).i;
        while (m.SelectedIndex != reset) m.HandleKey(MenuKey.Down);
        m.HandleKey(MenuKey.Enter);
        Assert.True(m.ConfirmingReset);
        m.HandleKey(MenuKey.Escape);                                              // “no”
        Assert.Equal(0x2D, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.Horn)));
        m.HandleKey(MenuKey.Enter);
        m.HandleKey(MenuKey.Enter);                                               // defaults to “no”, Enter confirms it [ref:3145]
        Assert.False(m.ConfirmingReset);
        Assert.Equal(0x2D, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.Horn)));
        m.HandleKey(MenuKey.Enter);
        m.HandleKey(MenuKey.Left); m.HandleKey(MenuKey.Enter);                    // "yes"
        for (int i = 0; i < KeyBindings.Count; i++)
            Assert.Equal(OldScans[i], KeyBindings.Scan(KeyBindings.Get(m.Config, (KeyAction)i)));
    }

    [Fact]
    public void SettingsJson_RoundTripsKeys_AndKeepsUnknownActionsAndKeys()
    {
        var c = Empty();
        KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
        KeyBindings.Assign(c, KeyAction.Accelerate, 0x11, 'W');
        var s = UserSettings.From(c);
        s.KeyBindings!["futureAction"] = new KeyBindingEntry { Scan = 5, Char = 0 };
        s.Save(Path.Combine(_dir, "s.json"));
        var json = File.ReadAllText(Path.Combine(_dir, "s.json"));
        Assert.Contains("\"keyBindings\"", json);
        Assert.Contains("\"accelerate\"", json);
        var loaded = UserSettings.Load(Path.Combine(_dir, "s.json"));
        Assert.True(loaded.KeyBindings!.ContainsKey("futureAction"));
        var target = Empty();
        KeyBindings.ApplyDefaults(target, KeyBindings.Builtin);
        loaded.ApplyTo(target);
        Assert.Equal(c.Raw.AsSpan(KeyBindings.FirstOffset, 40).ToArray(), target.Raw.AsSpan(KeyBindings.FirstOffset, 40).ToArray());
    }

    [Fact]
    public void SettingsJson_MissingBrokenOrOutOfRangeEntries_KeepDefaults()
    {
        void Check(string json)
        {
            var path = Path.Combine(_dir, "x.json");
            File.WriteAllText(path, json);
            var c = Empty();
            KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
            var before = c.Raw.AsSpan(KeyBindings.FirstOffset, 40).ToArray();
            UserSettings.Load(path).ApplyTo(c);
            Assert.Equal(before, c.Raw.AsSpan(KeyBindings.FirstOffset, 40).ToArray());
        }
        Check("{ \"schema\": 1, \"p1Car\": 3 }");                                          // missing key
        Check("{ \"schema\": 1, \"keyBindings\": null }");
        Check("{ \"schema\": 1, \"keyBindings\": { \"accelerate\": null, \"brake\": { \"scan\": 999 } } }");
        Check("{ \"schema\": 1, \"keyBindings\": { \"horn\": { \"scan\": 59 }, \"shiftUp\": { \"scan\": 28 } } }");   // F1 i Enter
        Check("{ \"schema\": 1, \"keyBindings\": { \"horn\": { \"scan\": -3 } } }");
        Check("{ \"schema\": 1, \"keyBindings\": 5 }");                                    // wrong type = damaged file
        Check("{ \"schema\": 1, \"keyBindings\": { \"horn\": \"x\" } }");
        Check("not json at all");
    }

    [Fact]
    public void SettingsJson_ExplicitKeyDisplacesTheSameDefaultOfAnotherAction()
    {
        var path = Path.Combine(_dir, "d.json");
        File.WriteAllText(path, "{ \"schema\": 1, \"keyBindings\": { \"accelerate\": { \"scan\": 57, \"char\": 32 } } }");   // space
        var c = Empty();
        KeyBindings.ApplyDefaults(c, KeyBindings.Builtin);
        UserSettings.Load(path).ApplyTo(c);
        Assert.Equal(0x39, KeyBindings.Scan(KeyBindings.Get(c, KeyAction.Accelerate)));
        Assert.False(KeyBindings.IsKey(KeyBindings.Get(c, KeyAction.Handbrake)));        // space is not bound twice
    }

    // Control screen: BFS over the menu item targets, then Down/Enter.
    internal static void GoToControls(MenuController m)
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
            int guard = 0;
            while (m.SelectedIndex != index && guard++ < 80) m.HandleKey(MenuKey.Down);
            m.HandleKey(MenuKey.Enter);
        }
        Assert.Equal(goal, m.CurrentScreen.Id);
    }

    internal static void SelectAction(MenuController m, KeyAction action)
    {
        int offset = KeyBindings.OffsetOf(action);
        int index = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.ConfigOffset == offset).i;
        for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) m.HandleKey(MenuKey.Down);
        Assert.Equal(index, m.SelectedIndex);
    }
}
