using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;

namespace NFS.Desktop.Tests;

/// <summary>network-game-sessions 5.4: GAME TYPE offers only one player, split screen and network; network leads straight to Connect.</summary>
public sealed class NetworkGameTypeMenuTests
{
    static MenuController Load() => MenuController.Load(GameFiles.FindGameDir(null));

    static List<string> CycleGameType(MenuController menu)
    {
        Assert.Equal("main", menu.CurrentScreen.Id);
        menu.HandleKey(MenuKey.Enter);                                  // GAME TYPE submenu
        Assert.Equal("game-setup", menu.CurrentScreen.Id);
        Assert.Equal(0x9, menu.CurrentScreen.Items[menu.SelectedIndex].ConfigOffset);
        var seen = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            seen.Add(menu.ItemText(menu.CurrentScreen.Items[menu.SelectedIndex]));
            menu.HandleKey(MenuKey.Right);
        }
        return seen;
    }

    [GameDataFact]
    public void GameTypeCycleHasNoModemSerialOrOtherLinkModes()
    {
        var seen = CycleGameType(Load());
        var values = seen.Select(s => s[(s.IndexOf(':') + 1)..].Trim().ToUpperInvariant()).ToList();
        Assert.Equal(3, values.Distinct().Count());
        Assert.Equal(values[0], values[3]);                             // wraps after three entries
        Assert.DoesNotContain(values, v => v.Contains("MODEM") || v.Contains("SERIAL") || v.Contains("IPX") || v.Contains("CABLE"));
        Assert.Contains(values, v => v.Contains("NETWORK"));
    }

    [GameDataFact]
    public void SelectingNetworkThenRaceOpensTheNetworkMenuOnTheConnectScreen()
    {
        var menu = Load();
        CycleGameType(menu);
        while (menu.Config.GameType != FeConfig.NetworkGameType) menu.HandleKey(MenuKey.Right);
        while ((menu.CurrentScreen.Items[menu.SelectedIndex].Flags & 8) == 0) menu.HandleKey(MenuKey.Down);
        menu.HandleKey(MenuKey.Enter);                                  // DONE keeps the choice (Esc would cancel it)
        Assert.Equal("main", menu.CurrentScreen.Id);
        while (menu.CurrentScreen.Items[menu.SelectedIndex].Target != "race") menu.HandleKey(MenuKey.Down);
        Assert.True(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [MenuKey.Enter], false), false, false));
        using var host = new NetworkMenuHost(null, () => -1, "Alice");
        Assert.Equal(NetScreen.Connect, host.Menu.Screen);
    }

    [GameDataFact]
    public void LocalGameTypesStillStartLocallyAndDoNotOpenTheNetworkMenu()
    {
        var menu = Load();
        foreach (byte type in new byte[] { 0, 1 })
        {
            menu.Config.GameType = type;
            while (menu.CurrentScreen.Items[menu.SelectedIndex].Target != "race") menu.HandleKey(MenuKey.Down);
            Assert.False(NetworkGameMenu.ShouldOpen(menu, new DesktopInput(default, [MenuKey.Enter], false), false, false));
        }
    }

    [Fact]
    public void LegacyModemAndSerialConfigValuesFallBackToOnePlayerWithoutThrowing()
    {
        Assert.Equal(0, FeConfig.SanitizeGameType(2 + 1));              // serial
        Assert.Equal(0, FeConfig.SanitizeGameType(4));                  // original network value
        Assert.Equal(0, FeConfig.SanitizeGameType(200));
        Assert.Equal(1, FeConfig.SanitizeGameType(1));
        Assert.Equal(FeConfig.NetworkGameType, FeConfig.SanitizeGameType(FeConfig.NetworkGameType));
    }
}
