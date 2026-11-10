using NFS.Game.FrontEnd;

namespace NFS.Desktop;

internal enum NetworkRaceAction { None, Pause, Resume, Leave }

/// <summary>Headless tested input decisions shared by Game1's remote-race UI.</summary>
internal static class NetworkRaceInput
{
    public static NetworkRaceAction Apply(DesktopInput input, bool paused, bool results, ref int selection)
    {
        if (results && (input.Menu.Contains(MenuKey.Enter) || input.Menu.Contains(MenuKey.Escape) || input.Typed?.Contains((char)8) == true))
            return NetworkRaceAction.Leave;
        if (!paused) return input.Pause ? NetworkRaceAction.Pause : NetworkRaceAction.None;
        if (input.Pause || input.Menu.Contains(MenuKey.Escape)) return NetworkRaceAction.Resume;
        foreach (var key in input.Menu)
        {
            if (key is MenuKey.Up or MenuKey.Left) selection = 0;
            else if (key is MenuKey.Down or MenuKey.Right) selection = 1;
            else if (key == MenuKey.Enter) return selection == 0 ? NetworkRaceAction.Resume : NetworkRaceAction.Leave;
        }
        return NetworkRaceAction.None;
    }
}
