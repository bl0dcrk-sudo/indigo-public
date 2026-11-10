using System.Buffers.Binary;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;

namespace NFS.Desktop.Tests;

/// <summary>HUDCON.DAT outside the menu races: reads and writes for every race where the setting is != 1 [ref:2848]
/// and there is no replay, so a network race and a <c>--race</c> window race use the user copy like the menu race does. [ref:2849]</summary>
public class NetworkHudConTests
{
    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nfs2se-hudcon-net-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static byte[] UserFile(bool map, bool text, int cam, bool mirror)
    {
        var d = HudConfig.Compose([], map, text, cam, mirror);
        d[0x50] = 0x5A;
        return d;
    }

    [Fact]
    public void WindowOptions_RaceAndServerUseTheCopy_AutotestDoesNot()
    {
        Assert.True(DesktopOptions.Parse(["--race", "replay"]).UsesHudcon);
        Assert.True(DesktopOptions.Parse(["--server", "127.0.0.1", "--name", "A"]).UsesHudcon);
        Assert.True(DesktopOptions.Parse([]).UsesHudcon);
        Assert.False(DesktopOptions.Parse(["--race", "replay", "--autotest", "out"]).UsesHudcon);
        Assert.False(DesktopOptions.Parse(["--race", "replay"]).UsesUserSettings); // settings.json stays untouched by --race
    }

    [GameDataFact]
    public void RaceOption_ReadsTheUserCopy_AndNullDirectoryKeepsTheGameFile()
    {
        var dir = TempDir();
        try
        {
            HudConPersistence.Save(dir, UserFile(false, true, 2, false));
            var scene = new RaceScene("replay", null, dir);
            Assert.Equal((false, true), scene.InitialHudVisibility);
            Assert.Equal(2, scene.IntroReturnMode);
            Assert.Equal(HudConPersistence.Load(dir), scene.HudconBaseline);

            var plain = new RaceScene("replay");
            Assert.NotEqual(0x5A, plain.HudconBaseline.Length > 0x50 ? plain.HudconBaseline[0x50] : 0);
            Assert.Equal((false, true), new RaceScene("replay", null, dir).InitialHudVisibility); // same input, same result
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void GameLoopWithHudconDirOnly_SavesTheRaceWithoutTouchingSettings()
    {
        var dir = TempDir();
        try
        {
            var loop = new GameLoop(null, false, null, dir);
            loop.StartRace(new RaceScene("replay", null, loop.HudconDirForNewRace()));
            loop.Drive!.ToggleHud();
            var want = loop.Drive.HudconSnapshot();
            loop.Shutdown();
            Assert.Equal(want, HudConPersistence.Load(dir));
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public void WelcomeScene_ReadsTheUserCopy()
    {
        var dir = TempDir();
        try
        {
            HudConPersistence.Save(dir, UserFile(false, false, 3, true));
            var source = new RaceScene("replay");
            var scene = RemoteDesktopSession.LoadWelcomeScene(GameFiles.FindGameDir(null), source.Session.Setup.Block, source.PlayerCarId, dir);
            Assert.Equal((false, false), scene.InitialHudVisibility);
            Assert.Equal(3, scene.IntroReturnMode);
            var none = RemoteDesktopSession.LoadWelcomeScene(GameFiles.FindGameDir(null), source.Session.Setup.Block, source.PlayerCarId);
            Assert.NotEqual(scene.InitialHudVisibility, none.InitialHudVisibility);
        }
        finally { Directory.Delete(dir, true); }
    }

    [GameDataFact]
    public async Task NetworkRace_StartsFromTheCopy_AndStoresTheChoicesWhenLeft()
    {
        string dir = GameFiles.FindGameDir(null);
        var hud = TempDir();
        try
        {
            HudConPersistence.Save(hud, UserFile(false, true, 2, false));
            using var server = new UdpSessionServer(dir, new SessionServerOptions
            {
                CountdownMicros = 500_000, MaxRaceSteps = 256 + 3000, RaceLingerMicros = 2_000_000,
            });
            using var a = new MenuPlayer(dir, "Alice", server.Port);
            using var b = new MenuPlayer(dir, "Bob", server.Port);
            a.Host.HudconDir = hud;
            a.Connect();
            Assert.True(await MenuPlayers.Until(() => a.Host.Session?.Session.Phase == SessionClientPhase.Browsing, 10_000, a), MenuPlayers.Why(a));
            Assert.True(a.Host.Session!.Session.CreateGame(new NetGameCreate("RaceHud", 2)));
            Assert.True(await MenuPlayers.Until(() => a.Menu.Screen == NetScreen.Lobby, 10_000, a), MenuPlayers.Why(a));
            Assert.True(a.Host.Session.Session.SetOptions(0, 1, bots: 0));
            b.Connect();
            Assert.True(await MenuPlayers.Until(() => b.Host.Session?.Session.Games.Count == 1, 10_000, a, b), MenuPlayers.Why(b));
            Assert.True(b.Host.Session!.Session.JoinGame(b.Host.Session.Session.Games[0].GameId));
            Assert.True(await MenuPlayers.Until(() => b.Menu.Screen == NetScreen.Lobby && a.Menu.Lobby?.Members.Count == 2, 10_000, a, b), MenuPlayers.Why(a, b));
            Assert.True(a.Host.Session.Session.SetReady(true));
            Assert.True(b.Host.Session.Session.SetReady(true));
            Assert.True(await MenuPlayers.Until(() => a.Menu.Lobby!.Members.All(m => m.Ready), 10_000, a, b), MenuPlayers.Why(a, b));
            Assert.True(a.Host.Session.Session.StartRace());
            Assert.True(await MenuPlayers.Until(() => a.Host.Drive != null && b.Host.Drive != null, 30_000, a, b), MenuPlayers.Why(a, b));

            var drive = a.Host.Drive!;
            Assert.False(drive.ShowMap);                  // restored from the copy
            Assert.True(drive.ShowHud);
            Assert.Equal(2, drive.Cam.HudconCamMode);
            Assert.Null(b.Host.HudconDir);                // Bob has no copy: nothing of his is written
            drive.ToggleHud();
            var want = drive.HudconSnapshot();
            Assert.NotEqual(UserFile(false, true, 2, false), want);

            a.Host.Dispose();                             // leaving the network race [ref:2850]
            Assert.Equal(want, HudConPersistence.Load(hud));
            Assert.Equal(0x5A, HudConPersistence.Load(hud)![0x50]); // the other bytes travel along
        }
        finally { Directory.Delete(hud, true); }
    }
}
