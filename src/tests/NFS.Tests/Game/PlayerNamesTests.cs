using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Player names 1 and 2: defaults, empty → defaults, character filter, editor, copy to [ref:3185]
/// the race, clearing in a series, saving in the settings and the menu flow before a split-screen start. [ref:3186]</summary>
public class PlayerNamesTests
{
    const string Player = "Player";   // TEXT 0x29D
    static FeConfig Empty() => new(new byte[FeConfig.Size]);

    // ---- names in config ----

    [Fact]
    public void DefaultNames_AreTheTextPlusOneOrTwo()
    {
        Assert.Equal("Player1", PlayerNames.DefaultName(Player, 0));
        Assert.Equal("Player2", PlayerNames.DefaultName(Player, 1));
        // text longer than 8 characters is cut to 8 (the field is 9 B with a zero), the digit does not fit
        Assert.Equal(8, PlayerNames.DefaultName("Spielerinnen", 0).Length);
    }

    [Fact]
    public void ConfigOffsets_AreTheTwoNinebyteFields()
    {
        var c = Empty();
        Assert.Equal((0x1AC, 0x1B5), (FeConfig.ProfileNameOffset(0), FeConfig.ProfileNameOffset(1)));
        Assert.Equal((0xE4, 0xED), (FeConfig.RaceNameOffset(0), FeConfig.RaceNameOffset(1)));
        c.SetName(FeConfig.ProfileNameOffset(0), "Tai Pan");
        c.SetName(FeConfig.ProfileNameOffset(1), "VeryLongName");
        Assert.Equal("Tai Pan", c.ProfileName(0));
        Assert.Equal("VeryLong", c.ProfileName(1));   // 8 characters and a zero, the second field untouched by the first
        c.SetName(FeConfig.ProfileNameOffset(0), "Ab");
        Assert.Equal("Ab", c.ProfileName(0));          // the rest of the field zeroed, no garbage left from a longer name
        Assert.Equal(0, c[0x1AC + 2]);
    }

    [Fact]
    public void EmptyOrBlankName_BecomesPlayer1_EvenForPlayerTwo()
    {
        Assert.Equal("Player1", PlayerNames.Fill("", Player));
        Assert.Equal("Player1", PlayerNames.Fill("   ", Player));   // always appends “1” [ref:3187]
        Assert.Equal("Bob", PlayerNames.Fill("Bob", Player));
        Assert.Equal(" a ", PlayerNames.Fill(" a ", Player));
    }

    [Fact]
    public void EnsureProfile_FillsOnlyEmptyNames()
    {
        var c = Empty();
        c.SetName(FeConfig.ProfileNameOffset(1), "Ola");
        PlayerNames.EnsureProfile(c, Player);
        Assert.Equal("Player1", c.ProfileName(0));
        Assert.Equal("Ola", c.ProfileName(1));
    }

    [Fact]
    public void CopyToRace_OnePlayerCopiesOnlyTheFirstName()
    {
        var c = Empty();
        c.SetName(FeConfig.ProfileNameOffset(0), "Anna");
        c.SetName(FeConfig.ProfileNameOffset(1), "Ben");
        PlayerNames.CopyToRace(c, split: false);
        Assert.Equal(("Anna", ""), (c.RaceName(0), c.RaceName(1)));
        PlayerNames.CopyToRace(c, split: true);
        Assert.Equal(("Anna", "Ben"), (c.RaceName(0), c.RaceName(1)));
    }

    [Fact]
    public void ClearRace_FollowsTheSeriesMask()
    {
        FeConfig Named()
        {
            var c = Empty();
            c.SetName(FeConfig.ProfileNameOffset(0), "Anna"); c.SetName(FeConfig.ProfileNameOffset(1), "Ben");
            PlayerNames.CopyToRace(c, split: true);
            return c;
        }
        var kept = Named();
        PlayerNames.ClearRace(kept, split: true, final: false, humanZeroActive: true);   // (0), human 0 is still driving [ref:3188]
        PlayerNames.ClearRace(kept, split: false, final: true, humanZeroActive: false);  // not split: nothing
        Assert.Equal(("Anna", "Ben"), (kept.RaceName(0), kept.RaceName(1)));
        var out0 = Named();
        PlayerNames.ClearRace(out0, split: true, final: false, humanZeroActive: false);  // human 0 dropped out
        Assert.Equal(("", ""), (out0.RaceName(0), out0.RaceName(1)));
        var final = Named();
        PlayerNames.ClearRace(final, split: true, final: true, humanZeroActive: true);   // (1) [ref:3189]
        Assert.Equal(("", ""), (final.RaceName(0), final.RaceName(1)));
        Assert.Equal(("Anna", "Ben"), (final.ProfileName(0), final.ProfileName(1)));     // profile unchanged
    }

    [Fact]
    public void EndSeries_RestoresDefaultNames_AndCopiesThemToTheRaceFields()
    {
        var c = Empty();
        c.SetName(FeConfig.ProfileNameOffset(0), "Anna"); c.SetName(FeConfig.ProfileNameOffset(1), "Ben");
        PlayerNames.CopyToRace(c, split: true);
        PlayerNames.EndSeries(c, Player);   // [ref:3190]
        Assert.Equal(("Player1", "Player2"), (c.ProfileName(0), c.ProfileName(1)));
        Assert.Equal(("Player1", "Player2"), (c.RaceName(0), c.RaceName(1)));
    }

    // ---- character filter ---- [ref:3191]

    [Theory]
    [InlineData('A')] [InlineData('z')] [InlineData('0')] [InlineData(' ')] [InlineData('-')] [InlineData('.')] [InlineData('_')]
    [InlineData('!')] [InlineData('?')] [InlineData(';')] [InlineData('{')] [InlineData('}')] [InlineData('[')] [InlineData(']')]
    [InlineData('Ä')] [InlineData('ö')] [InlineData('ñ')] [InlineData('ü')] [InlineData('é')]
    public void Accepted_Characters(char ch) => Assert.True(PlayerNames.Accepts(ch));

    [Theory]
    [InlineData('#')] [InlineData('$')] [InlineData('%')] [InlineData('<')] [InlineData('=')] [InlineData('>')] [InlineData('@')]
    [InlineData('^')] [InlineData('~')] [InlineData('ż')] [InlineData('\t')] [InlineData('\u007F')]
    public void Rejected_Characters(char ch) => Assert.False(PlayerNames.Accepts(ch));

    [Fact]
    public void BackspaceAndDelete_PassTheFilter() => Assert.True(PlayerNames.Accepts(8) && PlayerNames.Accepts(0x5300));

    // ---- editor ---- [ref:3192]

    static NameEditor Type(NameEditor e, string s) { foreach (char ch in s) e.Key(ch); return e; }

    [Fact]
    public void DefaultName_IsReplacedByTheFirstKey()
    {
        var e = new NameEditor("Player1", Player);
        Type(e, "Bob");
        Assert.Equal("Bob", e.Text);
        Type(e, "by");
        Assert.Equal("Bobby", e.Text);   // still appends
    }

    [Fact]
    public void CustomName_IsEditedInPlace()
    {
        var e = new NameEditor("Tai Pan", Player);
        e.Key('X');
        Assert.Equal("Tai PanX", e.Text);                   // limit 8: fits exactly
        e.Key('Y');
        Assert.Equal("Tai PanX", e.Text);                   // ninth character rejected
        e.Key(8); e.Key(8);
        Assert.Equal("Tai Pa", e.Text);
    }

    [Fact]
    public void BackspaceOnADefaultName_ClearsItAll()
    {
        var e = new NameEditor("Player2", Player);
        e.Key(8);
        Assert.Equal("", e.Text);
        Type(e, "Z");
        Assert.Equal("Z", e.Text);       // the “replace” flag consumed
        e.Key(8);
        Assert.Equal("", e.Text);
        e.Key(8);                        // nothing happens on an empty field
        Assert.Equal("", e.Text);
    }

    [Fact]
    public void RejectedKeys_AreIgnored_AndLongInputStopsAtEight()
    {
        var e = new NameEditor("Tai Pan", Player);
        e.Key(8); e.Key(8); e.Key(8); e.Key(8); e.Key(8); e.Key(8); e.Key(8);
        Assert.Equal("", e.Text);
        Type(e, "a#b$c@d~");
        Assert.Equal("abcd", e.Text);
        Type(e, "efghijk");
        Assert.Equal("abcdefgh", e.Text);
    }

    [Fact]
    public void EnterAcceptsTheText_EscKeepsTheOldName_BlankFallsBackToPlayer1()
    {
        var accepted = Type(new NameEditor("Player1", Player), "Max");
        accepted.Key(0xD);
        Assert.True(accepted.Finished && accepted.Accepted);
        Assert.Equal("Max", accepted.Result);
        var cancelled = Type(new NameEditor("Tai Pan", Player), "");
        cancelled.Key(8); cancelled.Key('Q');
        cancelled.Key(0x1B);
        Assert.True(cancelled.Finished && !cancelled.Accepted);
        Assert.Equal("Tai Pan", cancelled.Result);
        var blank = new NameEditor("Player2", Player);
        blank.Key(8); blank.Key(0xD);
        Assert.Equal("Player1", blank.Result);   // empty → “Player1” [ref:3193]
        accepted.Key('x');
        Assert.Equal("Max", accepted.Result);    // after finishing, keys change nothing
    }

    [Fact]
    public void Cursor_BlinksEveryFifteenTicks()
    {
        var e = new NameEditor("Bob", Player);
        Assert.False(e.CursorVisible);
        e.Advance(14);
        Assert.False(e.CursorVisible);
        e.Advance(1);
        Assert.True(e.CursorVisible);
        e.Advance(15);
        Assert.False(e.CursorVisible);
    }

    // ---- settings ----

    [Fact]
    public void Settings_RoundTripTheNames()
    {
        var c = Empty();
        c.SetName(FeConfig.ProfileNameOffset(0), "Anna"); c.SetName(FeConfig.ProfileNameOffset(1), "Ben");
        var path = Path.Combine(Path.GetTempPath(), $"nfs_names_{Guid.NewGuid():N}", "settings.json");
        try
        {
            UserSettings.From(c).Save(path);
            var loaded = UserSettings.Load(path);
            Assert.Equal(("Anna", "Ben"), (loaded.P1Name, loaded.P2Name));
            var target = Empty();
            loaded.ApplyTo(target);
            Assert.Equal(("Anna", "Ben"), (target.ProfileName(0), target.ProfileName(1)));
            Assert.Contains("\"p1Name\": \"Anna\"", File.ReadAllText(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Settings_WithoutNames_LeaveTheConfigNames()
    {
        var c = Empty();
        c.SetName(FeConfig.ProfileNameOffset(0), "Anna");
        new UserSettings { P1Car = 3 }.ApplyTo(c);   // not loaded from the file: nothing is overwritten
        var loaded = UserSettings.From(Empty());
        loaded.P1Name = null; loaded.P2Name = "";
        loaded.ApplyTo(c);
        Assert.Equal("Anna", c.ProfileName(0));
        Assert.Equal("", c.ProfileName(1));          // an empty string in the file does not overwrite either
    }

    // ---- menu: before starting split screen ----

    static MenuController SplitMenu()
    {
        var menu = MenuController.Load(GameData.Dir);
        menu.Config.GameType = 1; menu.Config.RaceType = 0; menu.Config.Track = 1; menu.Config.OpponentCount = 0; menu.Config.TrafficOff = 1;
        return menu;
    }

    static void PressRace(MenuController menu)
    {
        int race = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == "race").i;
        for (int guard = 0; menu.SelectedIndex != race && guard < 40; guard++) menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);
    }

    static void TypeName(MenuController menu, string s) { foreach (char ch in s) menu.NameKey(ch); }

    [GameFact]
    public void ProfileNamesAreFilledAtStart()
    {
        var menu = MenuController.Load(GameData.Dir);
        Assert.NotEqual("", menu.Config.ProfileName(0));
        Assert.NotEqual("", menu.Config.ProfileName(1));
    }

    [GameFact]
    public void SplitRace_AsksForBothNames_ThenStarts()
    {
        var menu = SplitMenu();
        PressRace(menu);
        Assert.Equal(MenuStatus.Menu, menu.Status);
        Assert.NotNull(menu.NameEditing);
        Assert.Equal(0, menu.NameEditPlayer);
        Assert.Equal("Player 1: enter name", menu.NamePrompt);
        TypeName(menu, "Anna");
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(1, menu.NameEditPlayer);
        Assert.Equal("Player 2: enter name", menu.NamePrompt);
        Assert.Equal("Player2", menu.NameEditing!.Text);
        Assert.Equal(MenuStatus.Menu, menu.Status);
        TypeName(menu, "Ben");
        menu.HandleKey(MenuKey.Enter);
        Assert.Null(menu.NameEditing);
        Assert.Equal(MenuStatus.Race, menu.Status);
        Assert.Equal(("Anna", "Ben"), (menu.Config.ProfileName(0), menu.Config.ProfileName(1)));
        Assert.Equal(("Anna", "Ben"), (menu.Config.RaceName(0), menu.Config.RaceName(1)));   // [ref:3194]
    }

    [GameFact]
    public void Escape_CancelsTheStart_AndKeepsConfirmedNames()
    {
        var menu = SplitMenu();
        var changed = 0;
        menu.PersistRequested += () => changed++;
        PressRace(menu);
        TypeName(menu, "Anna");
        menu.HandleKey(MenuKey.Enter);
        TypeName(menu, "Zzz");
        menu.HandleKey(MenuKey.Escape);
        Assert.Null(menu.NameEditing);
        Assert.Equal(MenuStatus.Menu, menu.Status);
        Assert.Equal("Anna", menu.Config.ProfileName(0));
        Assert.Equal("Player2", menu.Config.ProfileName(1));
        Assert.True(changed > 0);   // names go to settings.json
    }

    [GameFact]
    public void SinglePlayerRace_DoesNotAsk()
    {
        var menu = SplitMenu();
        menu.Config.GameType = 0;
        PressRace(menu);
        Assert.Null(menu.NameEditing);
        Assert.Equal(MenuStatus.Race, menu.Status);
    }

    [GameFact]
    public void MenuKeys_AreSwallowedWhileEditing_AndCheatCharsAreIgnored()
    {
        var menu = SplitMenu();
        PressRace(menu);
        int selected = menu.SelectedIndex;
        menu.HandleKey(MenuKey.Down);   // does not move the selection
        menu.HandleKey(MenuKey.Left);
        menu.TypeChar('G');             // cheat codes do not see the editor's characters
        Assert.Equal(selected, menu.SelectedIndex);
        Assert.NotNull(menu.NameEditing);
        menu.NameKey('X');
        Assert.Equal("X", menu.NameEditing!.Text);
    }

    [GameFact]
    public void HumanRacers_GetTheirNamesInTheResults()
    {
        var menu = SplitMenu();
        PressRace(menu);
        TypeName(menu, "Anna"); menu.HandleKey(MenuKey.Enter);
        TypeName(menu, "Ben"); menu.HandleKey(MenuKey.Enter);
        var results = new RaceResults(
        [
            new RacerResult(1, 2, "MCF1", false, false, 4000, [], null, Driver: "Slick"),
            new RacerResult(2, 1, "FF50", true, false, 4100, [], null),
            new RacerResult(3, 0, "FF50", true, false, 4200, [], null),
        ]);
        menu.CompleteRace(results);
        Assert.Equal(["Slick", "Ben", "Anna"], menu.Results!.Racers.Select(r => r.Driver));
    }

    [GameFact]
    public void SingleRace_KeepsTheNamesOfThePlayers_ButSeriesEndRestoresDefaults()
    {
        var menu = SplitMenu();
        PressRace(menu);
        TypeName(menu, "Anna"); menu.HandleKey(MenuKey.Enter);
        TypeName(menu, "Ben"); menu.HandleKey(MenuKey.Enter);
        menu.CompleteRace(new RaceResults([new RacerResult(1, 0, "FF50", true, false, 4000, [], null), new RacerResult(2, 1, "FF50", true, false, 4100, [], null)]));
        menu.ReturnToMenu();   // a plain race, no series: the names stay
        Assert.Equal(("Anna", "Ben"), (menu.Config.ProfileName(0), menu.Config.ProfileName(1)));
        // tournament for two: return to the menu [ref:3195]
        menu.Config.RaceType = 1;
        PressRace(menu);
        TypeName(menu, "Cora"); menu.HandleKey(MenuKey.Enter);
        menu.HandleKey(MenuKey.Enter);
        Assert.Equal(MenuStatus.Race, menu.Status);
        Assert.NotNull(menu.Tournament);
        menu.ReturnToMenu();
        Assert.Equal(("Player1", "Player2"), (menu.Config.ProfileName(0), menu.Config.ProfileName(1)));
    }
}
