using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Input blocking (<see cref="MenuController.InputBlocked"/>) in the name editor.</summary>
public class MenuNameBlockedTests
{
    [GameFact]
    public void NameKey_IgnoredWhileInputBlocked()
    {
        var menu = MenuController.Load(GameData.Dir);
        menu.Config.GameType = 1; menu.Config.RaceType = 0; menu.Config.Track = 1; menu.Config.OpponentCount = 0; menu.Config.TrafficOff = 1;
        int race = menu.CurrentScreen.Items.Select((it, i) => (it, i)).First(p => p.it.Target == "race").i;
        for (int guard = 0; menu.SelectedIndex != race && guard < 40; guard++) menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);
        Assert.NotNull(menu.NameEditing);
        string before = menu.NameEditing!.Text;
        bool blocked = true;
        menu.InputBlocked = () => blocked;
        menu.NameKey('X');
        Assert.Equal(before, menu.NameEditing!.Text);
        blocked = false;
        menu.NameKey('X');
        Assert.NotEqual(before, menu.NameEditing!.Text);
    }
}
