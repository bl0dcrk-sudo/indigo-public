using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

public class TrackRecordUpdateTests
{
    static FeConfig Config() => new(new byte[FeConfig.Size]);
    static StatsFile Empty() => StatsFile.Parse(TrackRecords.ClearedBytes());

    static RacerResult Car(int index, int place, int raceTicks, int[]? laps = null, bool human = true, int state = 2, int type = 0) =>
        new(place, index, "", human, false, raceTicks, laps ?? [raceTicks / 2, raceTicks - raceTicks / 2], null, state, CarType: type);

    static StatsFile WithTotals(int laps, params int[] times)
    {
        var recs = Empty().Records.ToArray();
        for (int i = 0; i < times.Length; i++) recs[TrackRecords.RowOffset(laps) + i] = new($"old{i}", 1, times[i], 0, 0);
        return new StatsFile(recs);
    }

    [Fact]
    public void StatsFile_ToBytes_RoundTripsParse()
    {
        var file = new StatsFile([new StatRecord("Jacques", 3, 0x1234, 2, 0), new StatRecord("", -1, 0, 0, 7)]);
        var bytes = file.ToBytes();
        Assert.Equal(2 * StatsFile.RecordSize, bytes.Length);
        Assert.Equal(file.Records, StatsFile.Parse(bytes).Records);
        Assert.Equal(0, bytes[7]);                           // name terminated by zero
        Assert.Equal(TrackRecords.ClearedBytes(), Empty().ToBytes());
    }

    [Fact]
    public void Gates_CheatRaceType3AndConfigFlagsDisableRecords()
    {
        var c = Config();
        Assert.True(TrackRecordUpdate.Allowed(c, pioneerCheat: false));
        Assert.False(TrackRecordUpdate.Allowed(c, pioneerCheat: true));
        c.RaceType = 3; Assert.False(TrackRecordUpdate.Allowed(c, false)); c.RaceType = 0;
        c[0x30] = 1; Assert.False(TrackRecordUpdate.Allowed(c, false)); c[0x30] = 0;
        c[0x4B] = 1; Assert.False(TrackRecordUpdate.Allowed(c, false));
    }

    [Fact]
    public void BonusCarsDependOnUnlockLevel()
    {
        var c = Config();
        Assert.True(TrackRecordUpdate.ModelAllowed(c, 11));
        Assert.False(TrackRecordUpdate.ModelAllowed(c, 12));
        c[0x1D] = 2;
        Assert.True(TrackRecordUpdate.ModelAllowed(c, 13));
        Assert.False(TrackRecordUpdate.ModelAllowed(c, 14));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Skill_FollowsStyle(byte style, short skill)
    {
        var c = Config(); c.Style = style;
        Assert.Equal(skill, TrackRecordUpdate.Skill(c));
    }

    [Fact]
    public void TotalCandidates_HumansWhoFinishedInPlaceOrder()
    {
        var c = Config();
        var results = new RaceResults([Car(3, 1, 900, human: false), Car(1, 2, 1000), Car(0, 3, 1100),
            Car(2, 4, 1200, state: 1), Car(4, 5, 1300, type: 12)]);
        Assert.Equal([1, 0], TrackRecordUpdate.TotalCandidates(c, results).Select(r => r.CarIndex));
    }

    [Fact]
    public void TotalQualifies_AgainstTenthSlotOrEmptySlot()
    {
        Assert.True(TrackRecordUpdate.TotalQualifies(Empty(), 4, 5000));
        Assert.True(TrackRecordUpdate.TotalQualifies(Empty(), 4, 0));       // 0 <= 0 – the original does not reject it (the car must finish anyway)
        var full = WithTotals(4, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100);
        Assert.True(TrackRecordUpdate.TotalQualifies(full, 4, 100));    // remis z 10. miejscem wchodzi
        Assert.False(TrackRecordUpdate.TotalQualifies(full, 4, 101));
        Assert.True(TrackRecordUpdate.TotalQualifies(full, 2, 101));    // other rows (2 laps) are empty
    }

    [Fact]
    public void InsertTotal_StableSortNewRecordBehindEqualTimes()
    {
        var file = WithTotals(2, 100, 200, 300);
        var (updated, rank) = TrackRecordUpdate.InsertTotal(file, 2, new StatRecord("new", 0, 200, 1, 0));
        Assert.Equal(3, rank);
        var rows = updated.Records.Skip(TrackRecords.RowOffset(2)).Take(10).ToArray();
        Assert.Equal(["old0", "old1", "new", "old2"], rows.Take(4).Select(r => r.Name));
        Assert.All(rows.Skip(4), r => Assert.Equal(0, r.Time));
        Assert.Equal(file.Records[0], updated.Records[0]);               // lap record untouched
    }

    [Fact]
    public void InsertTotal_ReplacesTenthAndEmptySlotsCountAsLongTimes()
    {
        var (full, rank) = TrackRecordUpdate.InsertTotal(WithTotals(8, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100), 8, new StatRecord("new", 0, 50, 0, 0));
        Assert.Equal(10, rank);
        Assert.DoesNotContain(full.Records, r => r.Name == "old9");
        Assert.Equal(1, TrackRecordUpdate.InsertTotal(Empty(), 8, new StatRecord("x", 0, 5000, 0, 0)).Rank);
        // empty slot 0 counts as 0x23280 – the longer time lands after it
        Assert.Equal(2, TrackRecordUpdate.InsertTotal(Empty(), 8, new StatRecord("x", 0, TrackRecordUpdate.EmptySlotStep + 1, 0, 0)).Rank);
    }

    [Theory]
    [InlineData(1, 0, true, 2, "c04")]
    [InlineData(1, 1, true, 2, "c05")]
    [InlineData(1, 0, true, 1, "c03")]
    [InlineData(1, 0, false, 1, "c03")]
    [InlineData(4, 0, true, 2, "c07")]
    [InlineData(4, 1, true, 2, "c08")]
    [InlineData(10, 0, false, 1, "c06")]
    public void TotalClip_Table(int rank, int car, bool split, int players, string clip) =>
        Assert.Equal(clip, TrackRecordUpdate.TotalClip(rank, car, split, players));

    [Fact]
    public void LapClip_Table()
    {
        Assert.Equal("c10", TrackRecordUpdate.LapClip(0, true));
        Assert.Equal("c11", TrackRecordUpdate.LapClip(1, true));
        Assert.Equal("c09", TrackRecordUpdate.LapClip(0, false));
    }

    [Fact]
    public void LapCandidate_SinglePlayerUsesCarZeroAndRecordTime()
    {
        var c = Config();
        var results = new RaceResults([Car(0, 1, 1000, [520, 480]), Car(1, 2, 1100, [500, 600], human: false)]);
        Assert.Equal(0, TrackRecordUpdate.LapCandidate(c, Empty(), results, false, 1)!.CarIndex);
        var recs = Empty().Records.ToArray(); recs[0] = new StatRecord("rec", 0, 480, 0, 0);
        Assert.NotNull(TrackRecordUpdate.LapCandidate(c, new StatsFile(recs), results, false, 1));   // remis wystarcza
        recs[0] = recs[0] with { Time = 479 };
        Assert.Null(TrackRecordUpdate.LapCandidate(c, new StatsFile(recs), results, false, 1));
        Assert.Null(TrackRecordUpdate.LapCandidate(c, Empty(), new RaceResults([Car(0, 1, 1000, state: 1)]), false, 1));
        Assert.Null(TrackRecordUpdate.LapCandidate(c, Empty(), new RaceResults([Car(0, 1, 1000, type: 12)]), false, 1));
    }

    [Fact]
    public void LapCandidate_SplitPicksBetterLapAndCarOneOnTie()
    {
        var c = Config();
        RaceResults Split(int[] a, int[] b, int stateB = 2) => new([Car(0, 1, 1000, a), Car(1, 2, 1100, b, state: stateB)]);
        Assert.Equal(0, TrackRecordUpdate.LapCandidate(c, Empty(), Split([500, 400], [450, 450]), true, 2)!.CarIndex);
        Assert.Equal(1, TrackRecordUpdate.LapCandidate(c, Empty(), Split([500, 450], [450, 460]), true, 2)!.CarIndex);
        Assert.Equal(0, TrackRecordUpdate.LapCandidate(c, Empty(), Split([500, 500], [100, 100], stateB: 1), true, 2)!.CarIndex);
    }

    [Fact]
    public void BestLapIndex_FirstOfEqualMinimum()
    {
        Assert.Equal(1, TrackRecordUpdate.BestLapIndex(Car(0, 1, 0, [500, 400, 400])));
        Assert.Equal(-1, TrackRecordUpdate.BestLapIndex(Car(0, 1, 0, [])));
    }

    [Fact]
    public void LapNameFromTotal_ChecksOnlyNineSlots()
    {
        var file = WithTotals(4, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Assert.True(TrackRecordUpdate.LapNameFromTotal(file, 4, Car(0, 1, 9)));
        Assert.False(TrackRecordUpdate.LapNameFromTotal(file, 4, Car(0, 1, 10)));
        Assert.False(TrackRecordUpdate.LapNameFromTotal(file, 2, Car(0, 1, 9)));
    }
}
