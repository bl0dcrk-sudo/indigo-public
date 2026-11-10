using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>Production decision for entering the network menu from the game menu, kept headless-testable.</summary>
internal static class NetworkGameMenu
{
    public static bool ShouldOpen(MenuController menu, DesktopInput input, bool shortcut, bool padChordRising)
    {
        if (menu.Status != MenuStatus.Menu || menu.CurrentScreen.Id != "main" || menu.NameEditing != null) return false;
        if (shortcut || padChordRising) return true;
        if (menu.Config.GameType <= 1 || !input.Menu.Contains(MenuKey.Enter)) return false;
        int selected = menu.SelectedIndex;
        return selected >= 0 && selected < menu.CurrentScreen.Items.Count && menu.CurrentScreen.Items[selected].Target == "race";
    }
}
