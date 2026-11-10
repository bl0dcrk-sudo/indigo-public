using NFS.Game.Race;

namespace NFS.Desktop.Tests;

/// <summary>drive-race / drive-frames build the race from a PREFS file (DEFAULT.TXT: track 5, traffic car types 15 and 16 without carSpecs files).</summary>
public class DriveRaceTrackTests
{
    [GameDataTheory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AutopilotWithAiAndTrafficRunsOnTrack(int track)
    {
        var setup = RaceInfo.Setup("DEFAULT.TXT"); setup.Set(0x24, track);
        var drive = new RaceDrive(new RaceScene(RaceScene.LoadSession(setup, null)), withAi: true, automatic: true);
        drive.RunAutopilot(20);
        Assert.Equal(track, drive.Scene.Session.TrackNumber);
        Assert.Equal(drive.Scene.Session.Cars.Count, drive.Runner.Cars.Count);   // every configured car has a sim
    }

    [GameDataFact]
    public void StringSourceLoadsPrefsRaceAndRuns()
    {
        var drive = new RaceDrive(new RaceScene("DEFAULT.TXT"), withAi: true, automatic: true);
        drive.RunAutopilot(5);
        Assert.Equal(5, drive.Scene.Session.TrackNumber);
    }

    [GameDataFact]
    public void ControlsFileInPrefsFolderIsRejectedWithClearError()
    {
        // VPILOT.TXT holds joystick bindings, not a race: no player car
        var e = Assert.Throws<InvalidDataException>(() => new RaceScene("VPILOT.TXT"));
        Assert.Contains("no player car", e.Message);
    }
}
