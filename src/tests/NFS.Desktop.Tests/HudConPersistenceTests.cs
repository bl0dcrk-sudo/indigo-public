using System.Buffers.Binary;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;

namespace NFS.Desktop.Tests;

/// <summary>HUDCON.DAT : HUD, camera and mirror choices carry over between races [ref:2751]
/// through a user copy; no directory = no persistence (tests, replays).</summary>
public class HudConPersistenceTests
{
    static int Dw(byte[] d, int off) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off));

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nfs2se-hudcon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static byte[] UserFile(bool map, bool text, int cam, bool mirror)
    {
        var d = HudConfig.Compose([], map, text, cam, mirror);
        d[0x50] = 0x5A; // transient bytes of the dump travel along
        return d;
    }

    [Fact]
    public void Defaults_MatchDhInitWithoutFile()
    {
        var d = HudConfig.Defaults();
        Assert.Equal(HudConfig.Size, d.Length);
        Assert.Equal((true, true), NFS.Render.OriginalHud.Visibility(d));
        Assert.Equal(1, Dw(d, HudConfig.MirrorOffset));
        Assert.Equal(0, Dw(d, HudConfig.CameraOffset));
        Assert.Equal(1, Dw(d, HudConfig.Text2Offset));
        Assert.Equal(0, Dw(d, 0)); // [ref:2752]
    }

    [Fact]
    public void Compose_ReplacesOnlyTheFiveFieldsAndKeepsTheRest()
    {
        var baseline = new byte[HudConfig.Size];
        for (int i = 0; i < baseline.Length; i++) baseline[i] = (byte)(i * 7 + 1);
        var d = HudConfig.Compose(baseline, true, false, 3, false);
        var expected = (byte[])baseline.Clone();
        foreach (var off in new[] { 0, HudConfig.MapOffset, HudConfig.CameraOffset, HudConfig.TextOffset, HudConfig.MirrorOffset })
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(off), 0);
        expected[HudConfig.MapOffset] = 1; expected[HudConfig.CameraOffset] = 3;
        Assert.Equal(expected, d);
        Assert.Equal(0, Dw(HudConfig.Compose(baseline, true, true, 9, true), HudConfig.CameraOffset)); // view outside 0..3 = 0
    }

    [Fact]
    public void SaveThenLoad_RoundTripsInTheGivenDirectory_AndRejectsBadFiles()
    {
        var dir = TempDir();
        try
        {
            Assert.Null(HudConPersistence.Load(dir));
            var data = UserFile(false, true, 2, false);
            Assert.True(HudConPersistence.Save(dir, data));
            Assert.Equal(data, HudConPersistence.Load(dir));
            File.WriteAllBytes(Path.Combine(dir, HudConPersistence.FileName), new byte[10]);
            Assert.Null(HudConPersistence.Load(dir)); // wrong size = ignored, as in the original [ref:2753]
            Assert.False(HudConPersistence.Save(dir, new byte[10]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NullDirectory_NeverReadsOrWrites()
    {
        Assert.Null(HudConPersistence.Load(null));
        Assert.False(HudConPersistence.Save(null, HudConfig.Defaults()));
    }

    sealed class Fixture
    {
        public readonly string Dir = TempDir();
        public readonly RaceScene Scene;
        public Fixture()
        {
            HudConPersistence.Save(Dir, UserFile(false, true, 2, false));
            var setup = RaceInfo.Setup("replay");
            Scene = new RaceScene(new RaceLoader().Load(setup), hudconDir: Dir);
        }
    }
    static readonly Lazy<Fixture> Shared = new(() => new Fixture());

    [GameDataFact]
    public void UserCopy_RestoresMapTextCameraAndMirrorAtRaceStart()
    {
        var scene = Shared.Value.Scene;
        Assert.Equal((false, true), scene.InitialHudVisibility);
        Assert.Equal(2, scene.IntroReturnMode);
        if (scene.Cockpit != null) Assert.False(scene.Cockpit.MirrorEnabled);
        var drive = new RaceDrive(scene, withAi: true);
        Assert.False(drive.ShowMap);
        Assert.True(drive.ShowHud);
        Assert.Equal(2, drive.Cam.HudconCamMode);
    }

    [GameDataFact]
    public void Snapshot_FollowsTheLiveChoices_AndKeepsTheUserFileBytes()
    {
        var scene = Shared.Value.Scene;
        var drive = new RaceDrive(scene, withAi: true);
        drive.ToggleHud(); // text on, map off -> text on, map on
        var d = drive.HudconSnapshot();
        Assert.Equal(HudConfig.Size, d.Length);
        Assert.Equal((drive.ShowMap, drive.ShowHud), NFS.Render.OriginalHud.Visibility(d));
        Assert.Equal(0x5A, d[0x50]);
        Assert.Equal(0, Dw(d, 0));
        Assert.Equal(drive.Cam.HudconCamMode, Dw(d, HudConfig.CameraOffset));
        // the next race starting from this snapshot sees the same choices
        var again = HudConfig.Compose(d, drive.ShowMap, drive.ShowHud, drive.Cam.HudconCamMode, scene.Cockpit?.MirrorEnabled == true);
        Assert.Equal(d, again);
    }

    [GameDataFact]
    public void GameLoopWithUserSettings_SavesOnShutdown_AndWithoutSettingsWritesNothing()
    {
        var scene = Shared.Value.Scene;
        var dir = TempDir();
        try
        {
            var plain = new GameLoop();
            plain.StartRace(new RaceScene(new RaceLoader().Load(RaceInfo.Setup("replay")), hudconDir: dir));
            plain.Shutdown();
            Assert.Null(HudConPersistence.Load(dir)); // no user settings path: persistence off

            var loop = new GameLoop(null, false, Path.Combine(dir, "settings.json"));
            loop.StartRace(scene);
            loop.Drive!.ToggleHud();
            var want = loop.Drive.HudconSnapshot();
            loop.Shutdown();
            Assert.Equal(want, HudConPersistence.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
