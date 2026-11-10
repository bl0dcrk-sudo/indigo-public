using System.Net;
using System.Text;
using NFS.Net.Server;

namespace NFS.Tests.Net;

/// <summary>Persistence of the last approved interactive configuration (server-config.json beside the server): round trip,
/// missing/corrupt/invalid fallback, atomic write and dialog defaults. No game data, sockets or console needed.</summary>
public class ServerConfigStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "nfs2se-config-" + Guid.NewGuid().ToString("N"));
    string PathFor(string name) => Path.Combine(_dir, name);

    public ServerConfigStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // must not fail an unrelated test
    }

    static ServerConsoleConfig Sample => new("single", IPAddress.Parse("0.0.0.0"), 27100, 6, 4, 2, 2, 1, true, true);

    [Fact]
    public void SavedConfigurationRoundTripsEveryField()
    {
        string path = PathFor(ServerConfigStore.FileName);
        Assert.True(ServerConfigStore.TrySave(path, Sample, out string saveError), saveError);
        Assert.True(File.Exists(path));

        Assert.True(ServerConfigStore.TryLoad(path, out var loaded, out string loadError), loadError);
        Assert.Equal(Sample, loaded);
        string json = File.ReadAllText(path);
        Assert.Contains($"\"version\": {ServerConfigStore.CurrentVersion}", json);
        Assert.Contains("\"mode\": \"single\"", json);
        Assert.Contains("\"bind\": \"0.0.0.0\"", json);
        Assert.Contains("\"minPlayers\": 2", json);
        Assert.Contains("\"maxPlayers\": 2", json);
        Assert.Contains("\"bots\": 1", json);
        Assert.Contains("\"traffic\": true", json);
        Assert.Contains("\"backward\": true", json);
        // the atomic write leaves no temporary file behind
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void SavingAgainReplacesThePreviousFile()
    {
        string path = PathFor(ServerConfigStore.FileName);
        Assert.True(ServerConfigStore.TrySave(path, Sample, out _));
        var replacement = Sample with { Track = 3, Laps = 7, MinPlayers = 1, MaxPlayers = 8, Bots = 0, Traffic = false };
        Assert.True(ServerConfigStore.TrySave(path, replacement, out string error), error);
        Assert.True(ServerConfigStore.TryLoad(path, out var loaded, out error), error);
        Assert.Equal(replacement, loaded);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void MissingFileIsReportedWithoutThrowing()
    {
        Assert.False(ServerConfigStore.TryLoad(PathFor("absent.json"), out var loaded, out string error));
        Assert.Null(loaded);
        Assert.Contains("no saved configuration", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"mode\":\"single\",\"bind\":\"127.0.0.1\",\"port\":\"bad\"}")]
    public void CorruptFileFallsBackWithAnExplanation(string content)
    {
        string path = PathFor(ServerConfigStore.FileName);
        File.WriteAllText(path, content);
        Assert.False(ServerConfigStore.TryLoad(path, out var loaded, out string error));
        Assert.Null(loaded);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("knockout", "127.0.0.1", 0, 1, 2, 1, 8, 0, false, "mode")]     // unsupported mode
    [InlineData("single", "not-an-ip", 0, 1, 2, 1, 8, 0, false, "IP address")] // unparsable bind
    [InlineData("single", "127.0.0.1", 70000, 1, 2, 1, 8, 0, false, "port")]   // port
    [InlineData("single", "127.0.0.1", 0, 8, 2, 1, 8, 0, false, "track")]     // track
    [InlineData("single", "127.0.0.1", 0, 1, 9, 1, 8, 0, false, "laps")]      // laps
    [InlineData("single", "127.0.0.1", 0, 1, 2, 0, 8, 0, false, "min players")] // minimum
    [InlineData("single", "127.0.0.1", 0, 1, 2, 1, 9, 0, false, "max players")] // maximum
    [InlineData("single", "127.0.0.1", 0, 1, 2, 5, 3, 0, false, "below min")] // inverted range
    [InlineData("single", "127.0.0.1", 0, 1, 2, 1, 8, 3, false, "bots")]      // bots
    public void OutOfRangeValuesAreRejected(string mode, string bind, int port, int track, int laps, int min, int max,
        int bots, bool traffic, string expected)
    {
        string path = PathFor(ServerConfigStore.FileName);
        File.WriteAllText(path,
            $$"""
            {"mode":"{{mode}}","bind":"{{bind}}","port":{{port}},"track":{{track}},"laps":{{laps}},
             "minPlayers":{{min}},"maxPlayers":{{max}},"bots":{{bots}},"traffic":{{(traffic ? "true" : "false")}}}
            """);
        Assert.False(ServerConfigStore.TryLoad(path, out var loaded, out string error));
        Assert.Null(loaded);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void UnwritablePathFailsGracefullyAndLeavesNoTemporaryFile()
    {
        string path = PathFor("missing-parent");
        Directory.CreateDirectory(path); // File.Move onto a directory fails
        Assert.False(ServerConfigStore.TrySave(path, Sample, out string error));
        Assert.Contains("cannot write", error);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void SaveCreatesAMissingDirectory()
    {
        string path = Path.Combine(_dir, "nested", "deeper", ServerConfigStore.FileName);
        Assert.True(ServerConfigStore.TrySave(path, Sample, out string error), error);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DialogOffersTheSavedConfigurationAsDefaults()
    {
        string path = PathFor(ServerConfigStore.FileName);
        var saved = new ServerConsoleConfig("single", IPAddress.Parse("0.0.0.0"), 27100, 5, 3, 2, 2, 1, true);
        Assert.True(ServerConfigStore.TrySave(path, saved, out string error), error);
        Assert.True(ServerConfigStore.TryLoad(path, out var defaults, out error), error);

        // Eleven empty answers: mode, bind, port, track, direction, laps, min, max, opponents, traffic, confirmation.
        var output = new StringBuilder();
        var queue = new Queue<string>(["", "", "", "", "", "", "", "", "", "", "yes"]);
        Assert.True(ServerConsole.TryReadConfig(() => queue.Count > 0 ? queue.Dequeue() : null, s => output.Append(s),
            out var cfg, out error, defaults), error);

        Assert.Equal(saved, cfg);
        string text = output.ToString();
        Assert.Contains("mode [single]", text);
        Assert.Contains("bind address [0.0.0.0]", text);
        Assert.Contains("UDP port (0 = ephemeral) [27100]", text);
        Assert.Contains("track 0..7 [5]", text);
        Assert.Contains("direction forward/backward [forward]", text);
        Assert.Contains("laps 1..8 [3]", text);
        Assert.Contains("min players 1..8 [2]", text);
        Assert.Contains("max players 2..8 [2]", text);
        Assert.Contains("bots 0..7 or auto (-1 = fill the grid) [1]", text);
        Assert.Contains("traffic yes/no [yes]", text);
    }

    [Theory]
    [InlineData(2, 2, ServerRaceConfig.AutoBots)] // old full split-screen lineup (six opponents) becomes auto
    [InlineData(2, 0, 0)]                         // old "no opponents" becomes an explicit zero
    [InlineData(2, 1, 0)]                         // any non-2 opponent option meant no AI in the old menu
    [InlineData(8, 0, ServerRaceConfig.AutoBots)] // other capacities stored an ignored 0 -> auto (full grid)
    [InlineData(3, 0, ServerRaceConfig.AutoBots)]
    public void LegacyConfigurationMigratesItsBotFieldSensibly(int maxPlayers, int oldBots, int expected)
    {
        string path = PathFor(ServerConfigStore.FileName);
        File.WriteAllText(path,
            $$"""
            {"mode":"single","bind":"127.0.0.1","port":0,"track":1,"laps":2,
             "minPlayers":1,"maxPlayers":{{maxPlayers}},"bots":{{oldBots}},"traffic":false}
            """);
        Assert.True(ServerConfigStore.TryLoad(path, out var loaded, out string error), error);
        Assert.NotNull(loaded);
        Assert.Equal(expected, loaded.Bots);
        Assert.Equal(expected, loaded.ToRaceConfig().BotSetting);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(8)]
    [InlineData(99)]
    public void VersionOneRejectsBotSettingsOutsideAutoAndZeroToSeven(int bots)
    {
        string path = PathFor(ServerConfigStore.FileName);
        File.WriteAllText(path,
            $$"""
            {"version":1,"mode":"single","bind":"127.0.0.1","port":0,"track":1,"laps":2,
             "minPlayers":1,"maxPlayers":8,"bots":{{bots}},"traffic":false}
            """);
        Assert.False(ServerConfigStore.TryLoad(path, out var loaded, out string error));
        Assert.Null(loaded);
        Assert.Contains("bots", error);
    }

    [Fact]
    public void InvalidSavedFileDoesNotChangeTheDialogDefaults()
    {
        string path = PathFor(ServerConfigStore.FileName);
        File.WriteAllText(path, "{ broken");
        Assert.False(ServerConfigStore.TryLoad(path, out var defaults, out _));
        Assert.Null(defaults);

        var output = new StringBuilder();
        var queue = new Queue<string>(["", "", "", "", "", "", "", "", "", "yes"]); // max 8: bots question, no traffic
        Assert.True(ServerConsole.TryReadConfig(() => queue.Count > 0 ? queue.Dequeue() : null, s => output.Append(s),
            out var cfg, out string error, defaults), error);
        Assert.Equal(ServerConsole.DefaultConfig, cfg);
        Assert.Contains("track 0..7 [1]", output.ToString());
        Assert.Contains("max players 1..8 [8]", output.ToString());
    }
}
