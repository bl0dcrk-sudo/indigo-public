using NFS.Data;
using NFS.Desktop;

DesktopOptions opt;
try { opt = DesktopOptions.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); Console.Error.WriteLine(DesktopOptions.Usage); return 1; }
if (opt.Help) { Console.WriteLine(DesktopOptions.Usage); return 0; }
if (opt.PickData)
{
    // Folder picker only (same screen as the Android first run); drives are the start points. Saves port.json.
    var roots = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
    var settings = PortSettings.Load(PortSettings.DefaultPath);
    var screen = new DataDirScreen(settings, PortSettings.DefaultPath, roots);
    using var picker = new DataDirGame(screen, mouseAsTouch: true);
    picker.Finished += (r, dir) => Console.WriteLine(r == DataDirScreen.Outcome.Accepted ? $"data dir: {dir}" : "cancelled");
    picker.Run();
    return 0;
}
// GameTables.Default and the data readers resolve the game dir on their own: publish the resolved dir to all of them.
if (GameFiles.TryFindGameDir(opt.GameDir) is { } gameDir) Environment.SetEnvironmentVariable("NFS2SE_GAME_DIR", gameDir);
else
{
    HostLog.Error("game data not found (need GAMEDATA and FEDATA folders). Checked, in order:");
    foreach (var p in GameFiles.SearchedPaths(opt.GameDir)) HostLog.Error($"  {p}");
    HostLog.Error("Fix: pass --game-dir <dir>, set NFS2SE_GAME_DIR, or put the game next to the exe (NFS2SE\\ folder).");
    return 4;
}

// Mods: scanned once, frozen for the run; never stops the game.
NFS.Game.Mods.ModSession.Start(opt.ModsDir, AppContext.BaseDirectory, HostLog.Info);

try
{
    using var game = new Game1(opt);
    game.Run();
    return game.ExitCode;
}
catch (Exception e)
{
    HostLog.Error(e.ToString());
    return 2;
}
