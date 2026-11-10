using NFS.Data.FrontEnd;
using NFS.Game.Race;

namespace NFS.Game.FrontEnd;

/// <summary>Saving track records (.STF) after a normal race – statchk: (top 10 by total time, first frame of the screen [ref:1982]
/// results, flag) and/(lap record = entry 0, lap-times screen, flag). [ref:1983]
/// The flags are set on entry after the race, and the KO/tournament screens clear them, so there are no records there. [ref:1984]
/// The class only computes what to insert and where; the name editor (→ window), the announcer and file saving are on the menu side. [ref:1985]</summary>
public static class TrackRecordUpdate
{
    /// <summary>Top 10 rows in the .STF for one number of laps.</summary>
    public const int Rows = 10;
    /// <summary>An empty slot (time 0) counts when sorting as (i + 1)·0x23280. [ref:1986]</summary>
    public const int EmptySlotStep = 0x23280;
    /// <summary>TEXT 0x2EF: error message for a failed write of the records file.</summary>
    public const int WriteErrorTextId = 0x2EF;
    /// <summary>Models ≥ 0xC + the unlock level (CONFIG +0x1D) are bonus cars – no record.</summary>
    public const int BonusModelBase = 0xC;

    /// <summary>Gates shared by both functions: the PIONEER/MADLAND/SCHOOLZONE/VIP code, race type 3, CONFIG +0x30 == 1 [ref:1987]
    /// and +0x4B == 1 disable saving.</summary>
    public static bool Allowed(FeConfig c, bool pioneerCheat) =>
        !pioneerCheat && c.RaceType != 3 && c[0x30] != 1 && c[0x4B] != 1;

    /// <summary>The car model is not a bonus model (<c>model &lt; CONFIG+0x1D + 0xC</c>); a modded car (number ≥ 0x35) is not a bonus one.</summary>
    public static bool ModelAllowed(FeConfig c, int carType) => carType >= NFS.Game.Cars.CarCatalog.VanillaCount || carType < c[0x1D] + BonusModelBase;

    /// <summary>Record difficulty level: → 1 (wild), → 2 (arcade), otherwise 0 (sim) = CONFIG +0x0B. [ref:1988]</summary>
    public static short Skill(FeConfig c) => (short)(c.Style is 1 or 2 ? c.Style : 0);

    static bool Finished(RacerResult r) => r.RaceState == 2;

    /// <summary>Order of cars it tries to insert: by place; with &lt; 2 (always in the port) only a human who [ref:1989]
    /// finished, and not a bonus car. The whole list requires that some human finished. [ref:1990]</summary>
    public static IReadOnlyList<RacerResult> TotalCandidates(FeConfig c, RaceResults results) =>
        [.. results.Racers.Where(r => r.Human && Finished(r) && ModelAllowed(c, r.CarType)).OrderBy(r => r.Place)];

    /// <summary>Whether the time enters the top 10 – <c>time &lt;= slot[row+9]</c> or the slot is empty (0) and time &gt; 0. [ref:1991]</summary>
    public static bool TotalQualifies(StatsFile file, int laps, int time)
    {
        int last = file.Records[TrackRecords.RowOffset(laps) + Rows - 1].Time;
        return time <= last || last == 0 && time > 0;
    }

    /// <summary>The record goes into slot row+9; the 10 times (empty ones (i+1)·0x23280) are sorted [ref:1992]
    /// stably in ascending order – on a tie the new one goes after the old ones. Returns the new file and the record's place 1..10.</summary>
    public static (StatsFile File, int Rank) InsertTotal(StatsFile file, int laps, StatRecord record)
    {
        int row = TrackRecords.RowOffset(laps);
        var recs = file.Records.ToArray();
        recs[row + Rows - 1] = record;
        int[] order = [.. Enumerable.Range(0, Rows).OrderBy(i => recs[row + i].Time != 0 ? recs[row + i].Time : (i + 1) * EmptySlotStep)];
        var sorted = order.Select(i => recs[row + i]).ToArray();
        sorted.CopyTo(recs, row);
        return (new StatsFile(recs), Array.IndexOf(order, Rows - 1) + 1);
    }

    /// <summary>Announcer clip (arrays): split with more than one player – separate for car 0/1 [ref:1993]
    /// otherwise a shared one. Place 1 → c04/c05/c03, the rest → c07/c08/c06.</summary>
    public static string? TotalClip(int rank, int car, bool splitScreen, int playerCars)
    {
        string[] clips = rank == 1 ? ["c04", "c05", "c03"] : ["c07", "c08", "c06"];
        if (splitScreen && playerCars > 1) return car is 0 or 1 ? clips[car] : null;
        return clips[2];
    }

    /// <summary>Announcer clip (array) played before the name editor: c10/c11 (split, car 0/1) or c09. [ref:1994]</summary>
    public static string? LapClip(int car, bool splitScreen) =>
        splitScreen ? car switch { 0 => "c10", 1 => "c11", _ => null } : "c09";

    /// <summary>Index of the best lap (stable sort – on a tie the first one); -1 without laps. [ref:1995]</summary>
    public static int BestLapIndex(RacerResult r)
    {
        int best = -1;
        for (int i = 0; i < r.LapTicks.Count; i++)
            if (best < 0 || r.LapTicks[i] < r.LapTicks[best]) best = i;
        return best;
    }

    /// <summary>The car that may hold the lap record (null = none). Split with more than one player: car 0 or 1 [ref:1996]
    /// must finish; when both do – the better lap (<c>best0 &lt; best1</c> → 0, otherwise 1). Otherwise the local player (car 0).
    /// There is a record when <c>best &lt;= entry 0</c> or entry 0 is empty and best &gt; 0.</summary>
    public static RacerResult? LapCandidate(FeConfig c, StatsFile file, RaceResults results, bool splitScreen, int playerCars)
    {
        RacerResult? Car(int i) => results.Racers.FirstOrDefault(r => r.CarIndex == i && Finished(r));
        int Best(RacerResult r) => r.LapTicks[BestLapIndex(r)];
        RacerResult? car;
        if (splitScreen && playerCars > 1)
        {
            var (a, b) = (Car(0), Car(1));
            car = a is null ? b : b is null ? a : Best(a) < Best(b) ? a : b;
        }
        else car = Car(0);
        if (car is null || BestLapIndex(car) < 0 || !ModelAllowed(c, car.CarType)) return null;
        int best = Best(car), rec = file.Records[0].Time;
        return best <= rec || rec == 0 && best > 0 ? car : null;
    }

    /// <summary>When the car's total time is in slots row..row+8 (only 9 – a quirk of the original), the name is taken from the race name [ref:1997]
    /// without asking.</summary>
    public static bool LapNameFromTotal(StatsFile file, int laps, RacerResult car)
    {
        int row = TrackRecords.RowOffset(laps);
        for (int i = 0; i < Rows - 1; i++)
            if (file.Records[row + i].Time == car.RaceTicks) return true;
        return false;
    }

    /// <summary>Entry 0 = name, model, best lap, difficulty level. [ref:1998]</summary>
    public static StatsFile SetLap(StatsFile file, StatRecord record)
    {
        var recs = file.Records.ToArray();
        recs[0] = record;
        return new StatsFile(recs);
    }
}
