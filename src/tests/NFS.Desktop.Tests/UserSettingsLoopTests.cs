using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;

namespace NFS.Desktop.Tests;

/// <summary>User settings in the game loop (task 1f): no file = migration of the current menu config into
/// the profile, a file loaded at startup goes into the menu config (and the pause sliders), and quitting the game saves
/// the current config back. A directory without write rights only reports `SettingsError` (no exception).</summary>
public class UserSettingsLoopTests
{
    static string TempDir() => Directory.CreateTempSubdirectory("nfs-loop-settings-").FullName;

    [GameDataFact]
    public void SavedSettingsReachTheMenuAndAreWrittenBackOnExit()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "p1Car": 7, "volumeMusic": 40, "skill": 2 }""");
            var loop = new GameLoop(null, false, path);
            Assert.Equal(7, loop.Menu.Controller.Config.P1Car);
            Assert.Equal(2, loop.Menu.Controller.Config.Skill);
            Assert.Equal(40, loop.Menu.Controller.Config[0x5D]);
            Assert.Equal(40, loop.PauseMenu.MusicVolume);           // the pause slider takes the same value

            loop.Menu.Controller.Config.P1Transmission = 1;         // „change in the menu”
            loop.Menu.Controller.Config[0x5E] = 55;
            loop.SaveSettings();                                    // quit the game

            var saved = new FeConfig(new byte[FeConfig.Size]);
            UserSettings.Load(path).ApplyTo(saved);
            Assert.Equal(1, saved.P1Transmission);
            Assert.Equal(55, saved[0x5E]);
            Assert.Equal(7, saved.P1Car);                           // fields outside the change preserved
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void FirstStartMigratesTheMenuConfigIntoTheFile()
    {
        // Migration (task 1f): without a file the profile is created at once from the current menu config, so from then on
        // the source of truth is the file, not the installation config.
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var loop = new GameLoop(null, false, path);
            Assert.Null(loop.SettingsError);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"), "migration write is atomic");
            // The file carries the same settings as the menu config at startup (the fields the profile covers).
            var fromMenu = new FeConfig(new byte[FeConfig.Size]);
            UserSettings.From(loop.Menu.Controller.Config).ApplyTo(fromMenu);
            var fromFile = new FeConfig(new byte[FeConfig.Size]);
            Assert.True(UserSettings.Load(path).Loaded);
            UserSettings.Load(path).ApplyTo(fromFile);
            Assert.Equal(fromMenu.ToBytes(), fromFile.ToBytes());
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void LaterStartsKeepTheFileValuesAndDoNotReMigrate()
    {
        // „another installation config” on the second start: values from the file win over the port config,
        // and the file is not overwritten or re-filled at startup (migration only when there is no file).
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            int defaultCar = MenuController.Load().Config.P1Car;
            var loop1 = new GameLoop(null, false, path);            // migration
            loop1.Menu.Controller.Config.P1Car = 5;                 // the player changed the setting in the menu
            loop1.Menu.Controller.Config[0x5D] = 33;
            loop1.SaveSettings();
            var file = File.ReadAllBytes(path);
            Assert.NotEqual(5, defaultCar);         // otherwise „file wins” would be indistinguishable from the defaults

            var loop2 = new GameLoop(null, false, path);
            Assert.Null(loop2.SettingsError);
            Assert.Equal(5, loop2.Menu.Controller.Config.P1Car);    // the file, not the port defaults
            Assert.Equal(33, loop2.Menu.Controller.Config[0x5D]);
            Assert.Equal(file, File.ReadAllBytes(path));            // file untouched at startup
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void BrokenFileIsReportedButNotOverwrittenAtStartup()
    {
        // A corrupted file is not overwritten at startup (it can still be recovered manually):
        // the game starts on the defaults, and the reason goes to SettingsError.
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            const string broken = "{ not json }";
            File.WriteAllText(path, broken);
            var loop = new GameLoop(null, false, path);
            Assert.NotNull(loop.SettingsError);
            Assert.Equal(MenuController.Load().Config.ToBytes(), loop.Menu.Controller.Config.ToBytes());
            Assert.Equal(broken, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"), "startup did not try to write");

            const string newer = """{ "schema": 99, "p1Car": 2 }""";
            File.WriteAllText(path, newer);
            var loop2 = new GameLoop(null, false, path);
            Assert.NotNull(loop2.SettingsError);
            Assert.Equal(newer, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void ReadOnlySettingsFileIsLoadedButASaveReportsAnErrorInsteadOfThrowing()
    {
        // Profile on read-only media / without rights: reading works, saving does not throw, the reason is in SettingsError.
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "p1Car": 7 }""");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var loop = new GameLoop(null, false, path);
            Assert.Null(loop.SettingsError);                        // loading succeeds
            Assert.Equal(7, loop.Menu.Controller.Config.P1Car);
            loop.Menu.Controller.Config.P1Car = 6;
            loop.SaveSettings();                                    // does not throw (File.Move onto a ReadOnly file)
            Assert.NotNull(loop.SettingsError);
            var reloaded = new FeConfig(new byte[FeConfig.Size]);
            UserSettings.Load(path).ApplyTo(reloaded);
            Assert.Equal(7, reloaded.P1Car);                        // old file intact
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);         // otherwise Directory.Delete would not remove the file
            Directory.Delete(dir, true);
        }
    }

    [GameDataFact]
    public void UnreadableFileIsReportedAndUntouchedAndUncreatablePathDoesNotThrow()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            // File locked by another process (or without read rights): Load does not throw, file untouched.
            File.WriteAllText(path, """{ "p1Car": 4 }""");
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var loop = new GameLoop(null, false, path);
                Assert.NotNull(loop.SettingsError);
                Assert.Equal(MenuController.Load().Config.ToBytes(), loop.Menu.Controller.Config.ToBytes());
            }
            Assert.Equal("""{ "p1Car": 4 }""", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"), "an unreadable file is not overwritten at startup");

            // A path that cannot be created (the profile directory is a file): the migration write does not throw.
            var blocked = Path.Combine(dir, "profile");
            File.WriteAllText(blocked, "x");
            var loop2 = new GameLoop(null, false, Path.Combine(blocked, "settings.json"));
            Assert.NotNull(loop2.SettingsError);
            Assert.Equal("x", File.ReadAllText(blocked));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void LoopWithoutSettingsPathNeitherReadsNorWrites()
    {
        // Desktop passes the path only outside autotest; without it the loop is deterministic (tests, headless tools).
        var loop = new GameLoop();
        loop.SaveSettings();                                   // without a path it does nothing
        Assert.Equal(MenuController.Load().Config.ToBytes(), loop.Menu.Controller.Config.ToBytes());
    }

    [GameDataFact]
    public void BrokenFileLeavesTheDefaultsAndReportsWhy()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, "{ not json }");
            var loop = new GameLoop(null, false, path);
            Assert.NotNull(loop.SettingsError);                 // the host logs the reason, the game starts on the defaults
            Assert.Equal(MenuController.Load().Config.ToBytes(), loop.Menu.Controller.Config.ToBytes());
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void SaveKeepsUnknownKeysAndLeavesNewerSchemaFileAlone()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(path, """{ "p1Car": 3, "futureOption": { "a": 1 } }""");
            var loop = new GameLoop(null, false, path);
            loop.Menu.Controller.Config.P1Car = 5;
            loop.SaveSettings();
            Assert.Null(loop.SettingsError);
            var saved = UserSettings.Load(path);
            Assert.Equal(5, saved.P1Car);
            Assert.True(saved.Extra.ContainsKey("futureOption"));   // a key from a newer build is not lost

            const string newer = """{ "schema": 99, "p1Car": 2 }""";
            File.WriteAllText(path, newer);
            var loop2 = new GameLoop(null, false, path);
            loop2.SaveSettings();
            Assert.NotNull(loop2.SettingsError);
            Assert.Equal(newer, File.ReadAllText(path));            // newer-schema file untouched
        }
        finally { Directory.Delete(dir, true); }
    }
}
