using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class MenuTablesTests
{
    private static readonly Lazy<MenuTree> Tree = new(() =>
        MenuTables.Load(TextFile.Load("ENG", GameData.Dir)));

    [GameFact]
    public void Counts()
    {
        var t = Tree.Value;
        Assert.Equal(34, t.Screens.Count);
        Assert.Equal(239, t.Screens.Sum(s => s.Items.Count));
        Assert.Empty(t.NonScreenTargets);
        Assert.Equal(23, t.Handlers.Count);
        Assert.Equal("Generic", t.Handlers[0]);
        Assert.Equal("MainMenu", t.Handlers[1]);
        Assert.All(t.Screens, s => Assert.InRange(s.Type, 0, 22));
    }

    [GameFact]
    public void MainMenu()
    {
        var m = Tree.Value["main"];
        Assert.Equal(1, m.Type);
        Assert.Equal("main", m.Art);
        Assert.Equal("blk6", m.Background);
        Assert.Equal(["game setup", "location", "player one", "player two", "opponents", "RACE", "options", "exit"],
            m.Items.Select(i => i.Text));
        Assert.Equal(["game-setup", "location", "player-one", "player-two", "opponents", "race", "options", "back"],
            m.Items.Select(i => i.Target));
        Assert.All(m.Items, i => Assert.Equal(2, i.Kind));
    }

    [GameFact]
    public void GameSetup_OptionValues()
    {
        var s = Tree.Value["game-setup"];
        var raceType = s.Items.Single(i => i.Text == "race type");
        Assert.Equal(0x0A, raceType.ConfigOffset); // +0x0A [ref:3159]
        Assert.NotEmpty(raceType.ValueTexts!);
        var location = Tree.Value["location"];
        Assert.Equal(0x10, location.Items.Single(i => i.Text == "track").ConfigOffset);
        Assert.Equal(0x19, location.Items.Single(i => i.Text == "laps").ConfigOffset);
        var opp = Tree.Value["opponents"];
        Assert.Equal(0x55, opp.Items.Single(i => i.Text == "car").ConfigOffset);
        Assert.Equal(0x58, opp.Items.Single(i => i.Text == "traffic").ConfigOffset);
    }

    [GameFact]
    public void PostRaceChain_AndJson()
    {
        var t = Tree.Value;
        Assert.Equal("lap-times", t["race-results"].Items.Single(i => i.Text == "continue").Target);
        Assert.Equal("quit", t["knockout-standings"].Items.Single(i => i.Text == "quit").Target);
        var json = t.ToJson();
        Assert.Contains("\"id\": \"main\"", json);
        Assert.Contains("\"game setup\"", json);
    }
}
