using System.Collections.Concurrent;
using NFS.Data;
using NFS.Net.Protocol;
using NFS.Net.Server;

// NFS.Server – headless authoritative session server. With no arguments it listens immediately on 0.0.0.0:27100; game flow
// (track, laps, cars, readiness, starting) belongs to the clients and the games they create. server-config.json holds only
// administrative settings (bind, port, limits and timeouts); command line flags override the file. The console is passive:
// `status`, `games` and `quit`; end of input does not stop the server.
// Usage: NFS.Server [--game DIR] [--mods DIR] [--bind ADDR] [--port N] [--max-games N] [--max-connections N]
//                   [--lobby-timeout-seconds N] [--grace-seconds N] [--race-wait-seconds N] [--max-race-steps N]
//                   [--config FILE] [--log FILE] [--port-file FILE] [--max-run-seconds N] [--help]
// Exit codes: 0 = requested stop (quit/Ctrl+C or run limit), 1 = bad arguments, missing game data or transport failure.
if (!SessionServerCommandLine.TryParse(args, out var launch, out string argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine(SessionServerCommandLine.Usage);
    return 1;
}
if (launch.Help)
{
    Console.WriteLine(SessionServerCommandLine.Usage);
    return 0;
}

string? gameDir = GameFiles.TryFindGameDir(launch.GameDir);
if (gameDir == null)
{
    Console.Error.WriteLine("game data not found (--game DIR or NFS2SE_GAME_DIR)");
    return 1;
}
Environment.SetEnvironmentVariable(GameFiles.EnvVar, gameDir);

string configPath = launch.ConfigPath ?? SessionServerConfigStore.DefaultPath;
var config = new SessionServerConfig();
bool configFileUsed = false;
if (File.Exists(configPath))
{
    if (SessionServerConfigStore.TryLoad(configPath, out var loaded, out string loadError)) { config = loaded!; configFileUsed = true; }
    else if (launch.ConfigPath != null)
    {
        Console.Error.WriteLine($"cannot use {configPath}: {loadError}");
        return 1;
    }
    else Console.WriteLine($"ignoring saved configuration: {loadError}; using defaults");
}
else if (launch.ConfigPath != null)
{
    Console.Error.WriteLine($"no configuration at {configPath}");
    return 1;
}
config = launch.Apply(config);
if (!config.Validate(out string configError))
{
    Console.Error.WriteLine(configError);
    return 1;
}

StreamWriter? logFile = null;
if (launch.LogPath is { } logPath)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        logFile = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"cannot open log {logPath}: {e.Message}");
        return 1;
    }
}
var gate = new object();
void Log(string s)
{
    string line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
    lock (gate)
    {
        Console.WriteLine(line);
        logFile?.WriteLine(line);
    }
}

// Data mods (car mods) of this server: their cars are selectable in the lobby; a client must have the same set (hello).
// Default: mods/ next to the server exe (no directory = no mods, no error); --mods DIR overrides it. Never the repo fallback of the game.
NFS.Game.Mods.ModSession.Start(launch.ModsDir ?? Path.Combine(AppContext.BaseDirectory, "mods"), AppContext.BaseDirectory, Log);
foreach (string helpLine in SessionServerCommandLine.StartupHelp(config, configPath, configFileUsed)) Log(helpLine);
if (launch.Ignored.Count > 0)
    Log($"legacy flags ignored (game flow belongs to the clients): {string.Join(' ', launch.Ignored)}");

var lines = new ConcurrentQueue<string>();
var reader = new Thread(() =>
{
    while (Console.ReadLine() is { } line) lines.Enqueue(line);
    // EOF is not a stop request: a service started without an interactive console keeps listening.
}) { IsBackground = true, Name = "session-console" };
reader.Start();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    int code = SessionServerLoop.Serve(config, gameDir, launch.MaxRunSeconds, Log,
        port =>
        {
            if (launch.PortFile is not { } portFile) return;
            try { File.WriteAllText(portFile, port.ToString()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"cannot write port file {portFile}: {e.Message}");
            }
        }, cancel.Token, () => lines.TryDequeue(out var line) ? line : null);
    Log($"exit {code}");
    return code;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NetProtocolException or ArgumentException)
{
    Log($"error: {e.Message}");
    return 1;
}
finally
{
    logFile?.Dispose();
}
