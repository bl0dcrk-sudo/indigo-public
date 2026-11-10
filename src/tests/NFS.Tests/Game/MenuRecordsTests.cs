using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Saving track records after a race in the menu (statchk): the announcer, the name editor and the STF file in the user directory. [ref:3158]</summary>
public class MenuRecordsTests
{
    static (MenuController Menu, List<string> Clips, string Dir) Race(Action<FeConfig>? setup = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nfs-records-" + Guid.NewGuid().ToString("N"));
        var m = MenuController.Load(GameData.Dir);
        m.StatsDirectory = dir;
        m.Config.GameType = 0; m.Config.RaceType = 0; m.Config.Track = 0;
        setup?.Invoke(m.Config);
        var clips = new List<string>();
        m.RecordSpeech += clips.Add;
        m.StartRace();
        return (m, clips, dir);
    }

    static RaceResults Results(int raceTicks, int[] laps, int carType = 0) => new([
        new RacerResult(1, 0, "", true, false, raceTicks, laps, laps.Min(), CarType: carType),
        new RacerResult(2, 1, "", false, false, raceTicks + 500, [400, 400], 400)]);

    [GameFact]
    public void FastRace_SavesTotalAndLapRecords()
    {
        var (m, clips, dir) = Race();
        try
        {
            int laps = m.Session!.Setup.NumLaps, row = TrackRecords.RowOffset(laps);
            m.CompleteRace(Results(1000, [.. Enumerable.Repeat(1000 / laps, laps)]));
            m.UpdateRecords();
            Assert.Null(m.NameEditing);                 // the victory screen first
            m.EndVictory();
            m.UpdateRecords();
            Assert.Equal(["c03"], clips);
            Assert.NotNull(m.NameEditing);
            m.HandleKey(MenuKey.Enter);
            Assert.Null(m.NameEditing);

            var saved = TrackRecords.Load(0, GameData.Dir, dir)!;
            Assert.Equal(1000, saved.Records[row].Time);
            Assert.Equal(m.Config.ProfileName(0), saved.Records[row].Name);
            Assert.Equal(m.Config.ProfileName(0), m.Config.RaceName(0));
            Assert.Equal(1000, m.RaceRecords!.Records[row].Time);

            m.HandleKey(MenuKey.Enter);                 // „lap times”
            Assert.Equal(MenuController.LapTimesScreen, m.CurrentScreen.Id);
            m.UpdateRecords();
            Assert.Equal(["c03", "c09"], clips);
            Assert.Null(m.NameEditing);                 // the total time is in the top 9 – the race name without asking
            saved = TrackRecords.Load(0, GameData.Dir, dir)!;
            Assert.Equal(1000 / laps, saved.Records[0].Time);
            Assert.Equal(m.Config.RaceName(0), saved.Records[0].Name);

            m.UpdateRecords();
            Assert.Equal(2, clips.Count);               // raz na wyniki
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [GameFact]
    public void SlowRaceOrBonusCar_NoRecord()
    {
        foreach (var (time, type) in new[] { (int.MaxValue / 2, 0), (1000, 12) })
        {
            var (m, clips, dir) = Race();
            int laps = m.Session!.Setup.NumLaps;
            m.CompleteRace(Results(time, [.. Enumerable.Repeat(time / laps, laps)], type));
            m.EndVictory();
            m.UpdateRecords();
            Assert.Empty(clips);
            Assert.Null(m.NameEditing);
            Assert.False(Directory.Exists(dir));
        }
    }

    [GameFact]
    public void RaceType3_NoRecord()
    {
        var (m, clips, dir) = Race(c => c[0x4B] = 1);
        m.CompleteRace(Results(1000, [500, 500]));
        m.EndVictory();
        m.UpdateRecords();
        Assert.Empty(clips);
        Assert.False(Directory.Exists(dir));
    }
}
