using System.Text.Json;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>User settings (task 1f): file, missing file, corrupt/unreadable file, schema, partial file,
/// preservation of unknown keys, atomic save and a path without write access. No game data needed – `FeConfig` is a 0x2AC-byte image.</summary>
public class UserSettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("nfs-settings-").FullName;
    string Path_ => System.IO.Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static FeConfig Config()
    {
        var c = new FeConfig(new byte[FeConfig.Size]);
        c.P1Car = 5; c.P1Transmission = 1; c.Style = 2; c.Skill = 1; c.CatchUp = 1;
        c.Track = 4; c.Laps = 8; c.LapsMenuIndex = 2; c.Backward = 1; c.Mirrored = 1;
        c.OpponentCar = 12; c.OpponentCount = 2; c.TrafficOff = 1; c.Raw[0x5A] = 1; c.Language = 3;   // 0x5A: Units is get-only
        c[0x5D] = 100; c[0x5E] = 60; c[0x1C] = 1; c[0x1D] = 0x07;
        for (int i = 0; i < 16; i++) c.Raw[0x21 + i] = (byte)(i + 1);
        return c;
    }

    [Fact]
    public void RoundTripsEveryFieldThroughTheFile()
    {
        UserSettings.From(Config()).Save(Path_);
        var target = new FeConfig(new byte[FeConfig.Size]);
        UserSettings.Load(Path_).ApplyTo(target);
        Assert.Equal(Config().ToBytes(), target.ToBytes());
    }

    [Fact]
    public void FileIsReadableJsonInTheUserFolder()
    {
        UserSettings.From(Config()).Save(Path_);
        var json = File.ReadAllText(Path_);
        Assert.Contains("\"schema\": 1", json);
        Assert.Contains("\"p1Car\": 5", json);
        Assert.Contains("\"volumeMusic\": 100", json);
        Assert.Contains("\"lapsMenuIndex\": 2", json);
        Assert.DoesNotContain("\r", json);                      // project files use LF line endings
        Assert.False(File.Exists(Path_ + ".tmp"), "atomic save leaves no temporary file");
        Assert.Contains("NFS2SE-port", UserSettings.DefaultPath);
        Assert.EndsWith(UserSettings.FileName, UserSettings.DefaultPath);
    }

    [Fact]
    public void MissingFileChangesNothing()
    {
        var settings = UserSettings.Load(Path_);
        Assert.False(settings.Loaded);
        Assert.Null(settings.LoadError);
        var c = Config();
        var before = c.ToBytes();
        settings.ApplyTo(c);
        Assert.Equal(before, c.ToBytes());
    }

    [Fact]
    public void CorruptOrUnreadableFileIsIgnoredNotFatal()
    {
        File.WriteAllText(Path_, "{ not json }");
        var settings = UserSettings.Load(Path_);
        Assert.False(settings.Loaded);
        Assert.NotNull(settings.LoadError);
        var c = Config(); var before = c.ToBytes();
        settings.ApplyTo(c);
        Assert.Equal(before, c.ToBytes());
    }

    [Fact]
    public void NewerSchemaIsIgnored()
    {
        File.WriteAllText(Path_, """{ "schema": 99, "p1Car": 3 }""");
        var settings = UserSettings.Load(Path_);
        Assert.False(settings.Loaded);
        Assert.Contains("99", settings.LoadError);
    }

    [Fact]
    public void UnreadableFileIsReportedNotFatal()
    {
        // File without read access / locked by another process: Load does not throw, returns the reason and overwrites nothing.
        File.WriteAllText(Path_, """{ "p1Car": 4 }""");
        using (var hold = new FileStream(Path_, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var settings = UserSettings.Load(Path_);
            Assert.False(settings.Loaded);
            Assert.NotNull(settings.LoadError);
            var c = Config(); var before = c.ToBytes();
            settings.ApplyTo(c);
            Assert.Equal(before, c.ToBytes());
        }
        Assert.Equal("""{ "p1Car": 4 }""", File.ReadAllText(Path_));
    }

    [Fact]
    public void SaveFailsWithACatchableExceptionForAPathThatCannotBeCreated()
    {
        // A directory without write access is hard to reproduce in a test, so we use a path that cannot be created:
        // a directory component is a file (the same way a profile blocked by a file fails). Saving throws an exception,
        // which the caller (`GameLoop.SaveSettings`) turns into a `SettingsError` – the game does not crash.
        var blocked = System.IO.Path.Combine(_dir, "profile");
        File.WriteAllText(blocked, "x");
        var ex = Record.Exception(() => UserSettings.From(Config()).Save(System.IO.Path.Combine(blocked, "settings.json")));
        Assert.NotNull(ex);
        Assert.True(ex is IOException or UnauthorizedAccessException, $"unexpected exception: {ex.GetType().Name}");
        Assert.Equal("x", File.ReadAllText(blocked));               // nothing was overwritten
    }

    [Fact]
    public void PartialFileTouchesOnlyItsKeys()
    {
        File.WriteAllText(Path_, """{ "volumeEffects": 40, "p1Car": 9 }""");
        var c = Config();
        UserSettings.Load(Path_).ApplyTo(c);
        Assert.Equal(40, c[0x5E]);
        Assert.Equal(9, c.P1Car);
        Assert.Equal(100, c[0x5D]);                             // was not in the file
        Assert.Equal(4, c.Track);
        Assert.Equal(8, c.Laps);
    }

    [Fact]
    public void UnknownKeysSurviveLoadAndSave()
    {
        File.WriteAllText(Path_, """{ "p1Car": 9, "futureThing": { "a": [1, 2] } }""");
        var settings = UserSettings.Load(Path_);
        Assert.True(settings.Loaded);
        settings.Save(Path_);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path_));
        Assert.Equal(9, doc.RootElement.GetProperty("p1Car").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("futureThing").GetProperty("a")[1].GetInt32());
    }

    [Fact]
    public void ValuesAreClampedLikeTheOriginals()
    {
        File.WriteAllText(Path_, """{ "volumeMusic": 999, "volumeEffects": -5, "skill": 300, "track": -1 }""");
        var c = Config();
        UserSettings.Load(Path_).ApplyTo(c);
        Assert.Equal(127, c[0x5D]);                             // 0..127, like the slider [ref:3270]
        Assert.Equal(0, c[0x5E]);
        Assert.Equal(255, c.Skill);                             // bajty przycinane do 0..255
        Assert.Equal(0, c.Track);
    }

    [Fact]
    public void SaveCreatesTheFolderAndOverwritesTheOldFile()
    {
        var nested = System.IO.Path.Combine(_dir, "a", "b", "settings.json");
        UserSettings.From(Config()).Save(nested);
        var other = UserSettings.From(new FeConfig(new byte[FeConfig.Size]));
        other.P1Car = 11;
        other.Save(nested);                                     // overwriting an existing file
        var target = new FeConfig(new byte[FeConfig.Size]);
        UserSettings.Load(nested).ApplyTo(target);
        Assert.Equal(11, target.P1Car);
        Assert.False(File.Exists(nested + ".tmp"));
    }

    string WriteConfigDat(byte[]? bytes)
    {
        string root = System.IO.Path.Combine(_dir, "game");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "FEDATA", "PC", "CONFIG"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "GAMEDATA"));   // GAMEDATA + FEDATA mark a game dir (GameFiles)
        if (bytes != null) File.WriteAllBytes(System.IO.Path.Combine(root, "FEDATA", "PC", "CONFIG", "CONFIG.DAT"), bytes);
        return root;
    }

    static FeConfig WithDefaultKeys()
    {
        var c = new FeConfig(new byte[FeConfig.Size]);
        for (int i = 0; i < KeyBindings.Count; i++) KeyBindings.Set(c, (KeyAction)i, KeyBindings.Word(KeyBindings.Builtin[i], 0));
        return c;
    }

    [Fact]
    public void FirstStartMigratesProfileFieldsFromConfigDat()
    {
        var dat = Config();
        dat.GameType = 3; dat.RaceType = 2;
        KeyBindings.Set(dat, KeyAction.SteerLeft, KeyBindings.Word(0x1E, 'A'));
        KeyBindings.Set(dat, KeyAction.SteerRight, 0x0101);   // joystick (device 1) – does not unbind the key
        KeyBindings.Set(dat, KeyAction.Brake, 0);             // no binding – also skipped
        var migrated = UserSettings.FromConfigDat(WriteConfigDat(dat.ToBytes()));
        Assert.NotNull(migrated);

        var target = WithDefaultKeys();
        migrated.ApplyTo(target);
        Assert.Equal(5, target.P1Car); Assert.Equal(4, target.Track); Assert.Equal(100, target[0x5D]);
        Assert.Equal(0, target.GameType); Assert.Equal(0, target.RaceType);
        Assert.Equal(KeyBindings.Word(0x1E, 'A'), KeyBindings.Get(target, KeyAction.SteerLeft));
        Assert.Equal(KeyBindings.Word(KeyBindings.Builtin[1], 0), KeyBindings.Get(target, KeyAction.SteerRight));
        Assert.Equal(KeyBindings.Word(KeyBindings.Builtin[3], 0), KeyBindings.Get(target, KeyAction.Brake));
    }

    [Fact]
    public void MissingOrForeignConfigDatIsNotMigrated()
    {
        Assert.Null(UserSettings.FromConfigDat(WriteConfigDat(null)));
        Assert.Null(UserSettings.FromConfigDat(WriteConfigDat([1, 2, 3])));
    }
}
