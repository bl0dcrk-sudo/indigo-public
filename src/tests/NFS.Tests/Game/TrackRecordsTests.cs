using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Track records from FEDATA/PC/STATS (frontend/common): file name, lap entry [ref:3248]
///  and total, line format "%s     %s     %s     %s". [ref:3249]</summary>
public class TrackRecordsTests
{
    static readonly Lazy<TextFile> Text = new(() => TextFile.Load("ENG", GameData.Dir));

    [GameFact]
    public void TrackNameIndexAndFilePathFollowTheOriginalTable()
    {
        // id 0..7 → same name, 20..27 (mirror track) → id − 20, out of range → entry 0. [ref:3250]
        int[] expected = [0, 4, 7, 0, 4, 7, 0];
        Assert.Equal(expected, new[] { 0, 4, 7, 20, 24, 27, 9 }.Select(TrackRecords.NameIndex).ToArray());
        Assert.Equal("FEDATA/PC/STATS/OVAL.STF", TrackRecords.FilePath(0));
        Assert.Equal("FEDATA/PC/STATS/OVAL.STF", TrackRecords.FilePath(20));
        Assert.Equal("FEDATA/PC/STATS/PAC.STF", TrackRecords.FilePath(4));
        Assert.Equal(31, TrackRecords.Load(0, GameData.Dir)!.Records.Count);
    }

    [GameFact]
    public void LapAndTotalRecordsComeFromTheTrackStatsFile()
    {
        var lap = TrackRecords.Lap(0, GameData.Dir);
        Assert.NotNull(lap);
        Assert.Equal("Daniel", lap!.Value.Name.TrimEnd());        // OVAL.STF entry 0 (name padded with tabs)
        Assert.Equal(3168, lap.Value.Time);                       // ticki 1/64 s
        Assert.Equal(2, lap.Value.Skill);
        Assert.Equal(0, lap.Value.Car);

        // 2 laps → entry 1, 4 → 0xB, otherwise 0x15. [ref:3251]
        Assert.Equal(7471, TrackRecords.Total(0, 2, GameData.Dir)!.Value.Time);
        Assert.Equal(13084, TrackRecords.Total(0, 4, GameData.Dir)!.Value.Time);
        Assert.Equal(26818, TrackRecords.Total(0, 3, GameData.Dir)!.Value.Time);
        Assert.Equal(26818, TrackRecords.Total(0, 8, GameData.Dir)!.Value.Time);
    }

    [GameFact]
    public void RecordLineIsNameTimeSkillAndCar()
    {
        var lap = TrackRecords.Lap(0, GameData.Dir)!.Value;
        // The time value is a stand-in here, only to check the layout of the line. [ref:3252]
        Assert.Equal("Daniel     T3168     arc     MCF1",
            TrackRecords.Text(Text.Value, lap, t => $"T{t}"));
        Assert.Equal("wild", Text.Value[0x1B]);
        Assert.Equal("sim", Text.Value[0x1E]);
        Assert.Equal("arc", Text.Value[0x1F]);
    }
}
