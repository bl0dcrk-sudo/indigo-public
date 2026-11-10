using NFS.Game.FrontEnd;
using NFS.Render;

namespace NFS.Desktop;

public static class LoopReview
{
    /// <summary>Headless acceptance path through the same controller used by Game1.</summary>
    public static GameLoop Run(string directory, string? gameDir = null)
    {
        Directory.CreateDirectory(directory);
        var loop = new GameLoop(gameDir, autopilot: true);
        var fb = new FrameBuffer();
        void Save(string name) { loop.Render(fb); fb.SavePng(Path.Combine(directory, name + ".png")); }
        void Key(MenuKey key) => loop.Frame(0, new DesktopInput(default, [key], false));
        Save("01-menu");
        // Main table: LOCATION, PLAYER, OPPONENTS, OPTIONS, RACE. [ref:1112]
        for (int i = 0; i < 4; i++) Key(MenuKey.Down);
        Key(MenuKey.Enter);
        if (loop.Drive == null) throw new InvalidOperationException("Menu failed to start race.");
        Save("02-grid");
        for (int i = 0; i < 12 * 32; i++) loop.Frame(1.0 / 32, new DesktopInput(default, [], false));
        Save("03-race");
        int tick = loop.Drive.View.SimulationTick64;
        loop.Frame(0, new DesktopInput(default, [], true));
        if (!loop.Drive.Paused) throw new InvalidOperationException("Pause failed.");
        for (int i = 0; i < 100; i++) loop.Frame(0.5, new DesktopInput(default, [], false));
        if (loop.Drive.View.SimulationTick64 != tick) throw new InvalidOperationException("Simulation advanced while paused.");
        Save("04-pause");
        for (int i = 0; i < 4; i++) Key(MenuKey.Down);
        Key(MenuKey.Enter); Save("05-confirm");
        Key(MenuKey.Left); Key(MenuKey.Enter);
        // End race (choice 3) goes to the results screen, not the menu. [ref:1113]
        if (loop.Menu.Controller.Status != MenuStatus.Results || loop.Drive != null)
            throw new InvalidOperationException("End race failed to open results.");
        Save("06-results");
        Key(MenuKey.Escape);
        if (loop.Menu.Controller.Status != MenuStatus.Menu)
            throw new InvalidOperationException("Results exit failed to return to menu.");
        Save("07-menu-return");
        return loop;
    }
}
