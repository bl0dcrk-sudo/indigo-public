using Microsoft.Xna.Framework.Input;
using NFS.Desktop;
using NFS.Game.FrontEnd;

namespace NFS.Desktop.Tests;

/// <summary>Driving keys from the control screen: scancode ↔ key mapping, capture in the game loop, driving after changing a
/// key, saving and loading in settings.json, missing or corrupt entry = defaults.</summary>
public class KeyBindingInputTests
{
    static DesktopInput Frame(params MenuKey[] keys) => new(default, keys, false);
    static DesktopInput Pressed(Keys key, params MenuKey[] menu) => new(default, menu, false, null, KeyScan.Capture(key));

    static void OpenControls(GameLoop loop)
    {
        // BFS over the menu item targets (as in NFS.Tests): Main... → control screen. [ref:2755]
        var m = loop.Menu.Controller;
        var tree = NFS.Game.FrontEnd.MenuTables.Load(NFS.Data.FrontEnd.TextFile.Load("ENG"));
        const string goal = "controls-setup";
        var prev = new Dictionary<string, (string From, int Index)>();
        var queue = new Queue<string>(["main"]);
        while (queue.Count > 0 && !prev.ContainsKey(goal))
        {
            var addr = queue.Dequeue();
            for (int i = 0; i < tree[addr].Items.Count; i++)
                if (tree[addr].Items[i].Target is { } t && t != "main" && tree.Screens.Any(s => s.Id == t) && !prev.ContainsKey(t))
                { prev[t] = (addr, i); queue.Enqueue(t); }
        }
        var path = new List<(string From, int Index)>();
        for (string cur = goal; cur != "main"; cur = prev[cur].From) path.Insert(0, prev[cur]);
        foreach (var (_, index) in path)
        {
            for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) loop.Frame(0, Frame(MenuKey.Down));
            loop.Frame(0, Frame(MenuKey.Enter));
        }
        Assert.Equal(goal, m.CurrentScreen.Id);
    }

    static void Rebind(GameLoop loop, KeyAction action, Keys key)
    {
        var m = loop.Menu.Controller;
        int offset = KeyBindings.OffsetOf(action);
        int index = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.ConfigOffset == offset).i;
        for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) loop.Frame(0, Frame(MenuKey.Down));
        loop.Frame(0, Frame(MenuKey.Enter));
        Assert.True(m.Capturing);
        loop.Frame(0, Pressed(key));
        Assert.False(m.Capturing);
    }

    [Fact]
    public void DefaultMap_IsTheOldHardcodedBindings()
    {
        var map = DriveKeyMap.Default;
        Assert.Equal((Keys.Left, Keys.Right, Keys.Up, Keys.Down), (map.Left, map.Right, map.Gas, map.Brake));
        Assert.Equal((Keys.B, Keys.C, Keys.Space, Keys.H), (map.LookBehind, map.Camera, map.Handbrake, map.Horn));
        Assert.Equal((Keys.A, Keys.Z, Keys.F5), (map.ShiftUp, map.ShiftDown, map.ToggleHud));
        var drive = new InputMapping().Read(new KeyboardState(Keys.Up, Keys.Left, Keys.A, Keys.Space, Keys.B), default).Drive;
        Assert.True(drive.Up); Assert.True(drive.Left); Assert.True(drive.GearUp); Assert.True(drive.Handbrake); Assert.True(drive.LookBehind);
        Assert.False(drive.Down);
    }

    [Fact]
    public void HorizonKey_IsF9AndReachesTheDriveKeys()
    {
        // I_HORIZON (tag 0x49, command key 0xE) has no default key in the original [ref:2756]
        // (KEYBOARD.TXT lists only C1_*, exposes tags 0x3B..0x44) - the port uses F9. [ref:2757]
        Assert.Equal(Keys.F9, DriveKeyMap.Default.HorizonToggle);
        Assert.True(new InputMapping().Read(new KeyboardState(Keys.F9), default).Drive.HorizonToggle);
        Assert.False(new InputMapping().Read(default, default).Drive.HorizonToggle);
        Assert.False(new InputMapping().Read(new KeyboardState(Keys.F5), default).Drive.HorizonToggle); // the HUD is not the horizon
    }

    [Fact]
    public void ScancodesRoundTripThroughKeys()
    {
        Assert.Equal(Keys.Right, KeyScan.KeyOf(0x4D));
        Assert.Equal(Keys.F5, KeyScan.KeyOf(0x3F));
        Assert.Equal(Keys.None, KeyScan.KeyOf(0));
        Assert.Equal(new KeyCapture(0x11, 'W'), KeyScan.Capture(Keys.W));
        Assert.Equal(new KeyCapture(0x48, 0), KeyScan.Capture(Keys.Up));
        Assert.Null(KeyScan.FirstPressed(new KeyboardState(Keys.Escape), default));          // Esc cancels, it is not an assignment
        Assert.Equal(new KeyCapture(0x11, 'W'), KeyScan.FirstPressed(new KeyboardState(Keys.W), default));
        Assert.Null(KeyScan.FirstPressed(new KeyboardState(Keys.W), new KeyboardState(Keys.W)));   // a held key does not repeat
    }

    [GameDataFact]
    public void RebindingInTheMenu_DrivesTheCarWithTheNewKey_AndOnlyTheNewKey()
    {
        var loop = new GameLoop();
        OpenControls(loop);
        Rebind(loop, KeyAction.Accelerate, Keys.W);
        var input = new InputMapping { Bindings = DriveKeyMap.From(loop.Menu.Controller) };
        Assert.True(input.Read(new KeyboardState(Keys.W), default).Drive.Up);
        Assert.False(input.Read(new KeyboardState(Keys.Up), default).Drive.Up);
        Assert.True(input.Read(new KeyboardState(Keys.Down), default).Drive.Down);          // the rest unchanged
    }

    [GameDataFact]
    public void CapturedArrow_DoesNotAlsoNavigateTheMenu()
    {
        var loop = new GameLoop();
        OpenControls(loop);
        var m = loop.Menu.Controller;
        int offset = KeyBindings.OffsetOf(KeyAction.Horn);
        int index = m.CurrentScreen.Items.Select((it, i) => (it, i)).Single(p => p.it.ConfigOffset == offset).i;
        for (int guard = 0; m.SelectedIndex != index && guard < 80; guard++) loop.Frame(0, Frame(MenuKey.Down));
        loop.Frame(0, Frame(MenuKey.Enter));
        loop.Frame(0, Pressed(Keys.Down, MenuKey.Down));
        Assert.Equal(index, m.SelectedIndex);
        Assert.Equal(0x50, KeyBindings.Scan(KeyBindings.Get(m.Config, KeyAction.Horn)));
        Assert.Equal(0, KeyBindings.Get(m.Config, KeyAction.Brake));                          // "down" removed from brake
    }

    [GameDataFact]
    public void ChangedKey_IsSavedToSettingsJson_AndLoadedOnNextStart_AndBrokenEntryFallsBack()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-keys-loop-").FullName;
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var loop = new GameLoop(null, false, path);
            OpenControls(loop);
            Rebind(loop, KeyAction.ShiftUp, Keys.E);
            loop.SaveSettings();
            Assert.Contains("\"shiftUp\"", File.ReadAllText(path));

            var next = new GameLoop(null, false, path);
            Assert.Equal(0x12, KeyBindings.Scan(KeyBindings.Get(next.Menu.Controller.Config, KeyAction.ShiftUp)));
            Assert.Equal(Keys.E, DriveKeyMap.From(next.Menu.Controller).ShiftUp);
            Assert.Equal(Keys.Z, DriveKeyMap.From(next.Menu.Controller).ShiftDown);

            File.WriteAllText(path, """{ "schema": 1, "keyBindings": { "shiftUp": { "scan": 999 }, "horn": { "scan": 1000 }, "future": { "scan": 3 } } }""");
            var broken = new GameLoop(null, false, path);
            Assert.Equal(Keys.A, DriveKeyMap.From(broken.Menu.Controller).ShiftUp);
            Assert.Equal(Keys.H, DriveKeyMap.From(broken.Menu.Controller).Horn);
            broken.SaveSettings();
            Assert.Contains("\"future\"", File.ReadAllText(path));                           // unknown action kept
        }
        finally { Directory.Delete(dir, true); }
    }
}
