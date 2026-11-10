using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Split-screen keys: config table +0x94 (player 1) / +0x98 (player 2), defaults from 2PLAYER.TXT [ref:3237]
/// = CONFIG.DAT from the disc, tags in the stream. [ref:3238]</summary>
public class SplitKeysTests
{
    static FeConfig Empty() => new(new byte[FeConfig.Size]);

    // Default game keys: player 1 ← → ↑ ↓ PgDn PgUp Home End Ins Del, player 2 S F E D Z R W T Q A.
    static readonly int[][] Expected =
    [
        [0x4B, 0x4D, 0x48, 0x50, 0x51, 0x49, 0x47, 0x4F, 0x52, 0x53],
        [0x1F, 0x21, 0x12, 0x20, 0x2C, 0x13, 0x11, 0x14, 0x10, 0x1E],
    ];

    [Fact]
    public void ConfigOffsets_AreTheInterleavedTable()
    {
        Assert.Equal(0x94, KeyBindings.SplitOffsetOf(0, KeyAction.SteerLeft));
        Assert.Equal(0x98, KeyBindings.SplitOffsetOf(1, KeyAction.SteerLeft));
        Assert.Equal(0x9C, KeyBindings.SplitOffsetOf(0, KeyAction.SteerRight));
        Assert.Equal(0xE0, KeyBindings.SplitOffsetOf(1, KeyAction.ShiftDown));   // last of 20 words (0x94..0xE3)
        Assert.Equal((1, KeyAction.Horn), KeyBindings.SplitActionAt(0xD0));
        Assert.Null(KeyBindings.SplitActionAt(0x90));
        Assert.Null(KeyBindings.SplitActionAt(0xE4));
        var c = Empty();
        KeyBindings.SetSplit(c, 1, KeyAction.Brake, KeyBindings.Word(0x20, 'D'));
        Assert.Equal(c.SplitKey(1, 3), KeyBindings.GetSplit(c, 1, KeyAction.Brake));
        Assert.Equal(0, c.SplitKey(0, 3));
    }

    [Fact]
    public void BuiltinDefaults_AreTheTwoPlayerLayout()
    {
        Assert.Equal(Expected, KeyBindings.SplitBuiltin);
        var c = Empty();
        KeyBindings.EnsureSplitDefaults(c, KeyBindings.SplitBuiltin);
        Assert.True(KeyBindings.AllAssignedSplit(c));
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < 10; i++)
                Assert.Equal(Expected[p][i], KeyBindings.Scan(KeyBindings.GetSplit(c, p, (KeyAction)i)));
    }

    [Fact]
    public void EnsureDefaults_KeepsAnExistingAssignment()
    {
        var c = Empty();
        KeyBindings.SetSplit(c, 0, KeyAction.Horn, KeyBindings.Word(0x23, 'H'));
        KeyBindings.EnsureSplitDefaults(c, KeyBindings.SplitBuiltin);
        Assert.Equal(0, KeyBindings.GetSplit(c, 1, KeyAction.Horn));   // not replaced: one entry was already a key
    }

    [Fact]
    public void ParseFile_ReadsBothPlayersAndFallsBack()
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SC_LEFT"] = 0x4B, ["SC_J"] = 0x24, ["SC_K"] = 0x25 };
        var text = System.Text.Encoding.ASCII.GetBytes("C1_LEFT = key,SC_J\nC2_GAS = key,SC_K\nC2_BRAKE = key,SC_NOSUCH\n");
        var scans = KeyBindings.ParseSplitFile(text, names);
        Assert.Equal(0x24, scans[0][(int)KeyAction.SteerLeft]);
        Assert.Equal(0x25, scans[1][(int)KeyAction.Accelerate]);
        Assert.Equal(Expected[1][(int)KeyAction.Brake], scans[1][(int)KeyAction.Brake]);   // unknown key name → default
        Assert.Equal(Expected[0][(int)KeyAction.Horn], scans[0][(int)KeyAction.Horn]);
        Assert.Equal(Expected, KeyBindings.ParseSplitFile(null, names));
    }

    [Fact]
    public void Assign_DisplacesTheSameKeyOfEitherPlayer()
    {
        var c = Empty();
        KeyBindings.ApplySplitDefaults(c, KeyBindings.SplitBuiltin);
        // player 1 takes player 2's "S" (steer left): player 2 loses that action, the screen's DONE stays greyed out
        Assert.True(KeyBindings.AssignSplit(c, 0, KeyAction.Horn, 0x1F, 'S'));
        Assert.Equal(0, KeyBindings.GetSplit(c, 1, KeyAction.SteerLeft));
        Assert.Equal(0x1F, KeyBindings.Scan(KeyBindings.GetSplit(c, 0, KeyAction.Horn)));
        Assert.False(KeyBindings.AllAssignedSplit(c));
        Assert.False(KeyBindings.AssignSplit(c, 1, KeyAction.SteerLeft, 0x3B, 0));   // F1 is not assignable
        Assert.True(KeyBindings.AssignSplit(c, 1, KeyAction.SteerLeft, 0x2F, 'V'));
        Assert.True(KeyBindings.AllAssignedSplit(c));
    }

    [Fact]
    public void SettingsEntries_RoundTripAndOverrideDefaults()
    {
        var c = Empty();
        KeyBindings.ApplySplitDefaults(c, KeyBindings.SplitBuiltin);
        KeyBindings.AssignSplit(c, 1, KeyAction.Horn, 0x2F, 'V');
        var entries = KeyBindings.ToSplitEntries(c).ToDictionary(p => p.Key, p => (KeyBindingEntry?)p.Value);
        Assert.Equal(20, entries.Count);
        Assert.Equal(0x2F, entries["p2.horn"]!.Scan);
        var back = Empty();
        KeyBindings.ApplySplitDefaults(back, KeyBindings.SplitBuiltin);
        KeyBindings.ApplySplitEntries(back, entries);
        Assert.Equal(c.Raw.AsSpan(0x94, 0x50).ToArray(), back.Raw.AsSpan(0x94, 0x50).ToArray());
        // an explicit key displaces the same default of another action (like the one-player table)
        var changed = Empty();
        KeyBindings.ApplySplitDefaults(changed, KeyBindings.SplitBuiltin);
        KeyBindings.ApplySplitEntries(changed, new Dictionary<string, KeyBindingEntry?> { ["p1.horn"] = new() { Scan = 0x1F, Char = 'S' } });
        Assert.Equal(0x1F, KeyBindings.Scan(KeyBindings.GetSplit(changed, 0, KeyAction.Horn)));
        Assert.Equal(0, KeyBindings.GetSplit(changed, 1, KeyAction.SteerLeft));
    }

    [Fact]
    public void UserSettings_RoundTripPlayerTwoAndSplitKeys_AndBrokenEntriesFallBack()
    {
        var dir = Directory.CreateTempSubdirectory("nfs-split-settings-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var c = Empty();
            KeyBindings.ApplySplitDefaults(c, KeyBindings.SplitBuiltin);
            c.P2Car = 5; c.Raw[0x3B] = 1; c.Raw[0x3C + 5] = 9;
            KeyBindings.AssignSplit(c, 1, KeyAction.Horn, 0x2F, 'V');
            UserSettings.From(c).Save(path);
            var json = File.ReadAllText(path);
            Assert.Contains("\"splitKeyBindings\"", json);
            Assert.Contains("\"p2.horn\"", json);
            Assert.Contains("\"p2Car\": 5", json);
            var loaded = Empty();
            KeyBindings.ApplySplitDefaults(loaded, KeyBindings.SplitBuiltin);
            UserSettings.Load(path).ApplyTo(loaded);
            Assert.Equal((byte)5, loaded.P2Car);
            Assert.Equal((byte)1, loaded.P2Transmission);
            Assert.Equal((byte)9, loaded.P2Colours[5]);
            Assert.Equal(c.Raw.AsSpan(0x94, 0x50).ToArray(), loaded.Raw.AsSpan(0x94, 0x50).ToArray());
            // missing / broken entries keep the defaults; unknown ones survive a re-save
            File.WriteAllText(path, "{ \"schema\": 1, \"splitKeyBindings\": { \"p1.horn\": { \"scan\": 999 }, \"p9.horn\": { \"scan\": 3 } } }");
            var broken = Empty();
            KeyBindings.ApplySplitDefaults(broken, KeyBindings.SplitBuiltin);
            var settings = UserSettings.Load(path);
            settings.ApplyTo(broken);
            Assert.Equal(KeyBindings.SplitBuiltin[0][(int)KeyAction.Horn], KeyBindings.Scan(KeyBindings.GetSplit(broken, 0, KeyAction.Horn)));
            Assert.Equal((byte)0, broken.P2Car);   // not in the file: untouched
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameFact]
    public void DiscConfigDat_HasTheSameSplitDefaults()
    {
        var original = FeConfig.Load(GameData.Dir);
        var fromFile = KeyBindings.LoadSplitDefaults(GameData.Dir);
        Assert.Equal(Expected, fromFile);
        var c = Empty();
        KeyBindings.ApplySplitDefaults(c, fromFile);
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < 10; i++)
                Assert.Equal(original.SplitKey(p, i), c.SplitKey(p, i));
    }

    [GameFact]
    public void MenuController_StartsWithTheSplitDefaults_AndResetRestoresThem()
    {
        var m = MenuController.Load(GameData.Dir);
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < 10; i++)
                Assert.Equal(Expected[p][i], KeyBindings.Scan(KeyBindings.GetSplit(m.Config, p, (KeyAction)i)));
        KeyBindings.SetSplit(m.Config, 1, KeyAction.Horn, 0);
        Assert.False(KeyBindings.AllAssignedSplit(m.Config));
        m.ResetSplitControls();
        Assert.True(KeyBindings.AllAssignedSplit(m.Config));
    }

    static int[] Stream(Action<FeConfig> edit)
    {
        var cfg = FeConfig.Load(GameData.Dir);
        edit(cfg);
        return new FeRaceArgsBuilder().Build(cfg);
    }

    static RaceSetup Parse(int[] stream) => RaceArgsParser.Parse(RaceTags.FromConstants(), stream);

    [GameFact]
    public void SplitStream_CarriesTheKeysOfBothPlayers()
    {
        var r = Parse(Stream(c => { c.GameType = 1; c.RaceType = 0; c.OpponentCount = 2; c.TrafficOff = 1; c.Mirrored = 0; }));
        (int Tag, int Scan)[] keys =
        [
            (0x23, 0x4B), (0x24, 0x4D), (0x25, 0x48), (0x26, 0x50), (0x52, 0x51), (0x53, 0x49), (0x2B, 0x47), (0x2C, 0x4F), (0x2D, 0x52), (0x2E, 0x53),
            (0x2F, 0x1F), (0x30, 0x21), (0x31, 0x12), (0x32, 0x20), (0x54, 0x2C), (0x55, 0x13), (0x37, 0x11), (0x38, 0x14), (0x39, 0x10), (0x3A, 0x1E),
            (0x28, 0x57), (0x34, 0x58),   // reset-car buttons: F11 for player 1, F12 for player 2
        ];
        foreach (var (tag, scan) in keys)
        {
            var (device, code) = r.Key(tag);
            Assert.Equal(KeyBindings.DeviceKeyboard, device);
            Assert.Equal(scan, code & 0xFF);
        }
        Assert.Equal(2, r.NumPlayerCars);
        Assert.Equal(1, r.CommMode);
        Assert.Equal(8, r.NumCars);          // two humans + the six AI of "full grid"
        Assert.Equal(6, r.NumOpponentCars);
    }

    [GameFact]
    public void SplitStream_SwapsSteeringWhenMirrored_AndIgnoresTheOnePlayerTable()
    {
        var r = Parse(Stream(c =>
        {
            c.GameType = 1; c.RaceType = 0; c.OpponentCount = 0; c.TrafficOff = 1; c.Mirrored = 1;
            BitConverter.GetBytes(KeyBindings.Word(0x39, ' ')).CopyTo(c.Raw, 0x6C);   // one-player "steer left" = Space: must not leak
        }));
        Assert.Equal(0x4B, r.Key(0x24).Code & 0xFF);    // player 1 "left" key now feeds the RIGHT slot
        Assert.Equal(0x4D, r.Key(0x23).Code & 0xFF);
        Assert.Equal(0x1F, r.Key(0x30).Code & 0xFF);
        Assert.Equal(0x21, r.Key(0x2F).Code & 0xFF);
    }

    [GameFact]
    public void OnePlayerStream_HasNoSecondPlayerKeys()
    {
        var r = Parse(Stream(c => { c.GameType = 0; c.RaceType = 0; c.OpponentCount = 1; c.TrafficOff = 1; }));
        foreach (int tag in new[] { 0x2F, 0x30, 0x31, 0x32, 0x34, 0x37, 0x54, 0x55 })
            Assert.Equal(0, r.KeyBindings[tag - 0x23]);
        Assert.Equal(0x4B, r.Key(0x23).Code & 0xFF);   // one-player table (+0x6C): steer left = ←
    }
}
