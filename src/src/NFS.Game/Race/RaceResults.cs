using NFS.Core.Constants;

namespace NFS.Game.Race;

/// <summary>Host-neutral snapshot of the authoritative lap fields, in RaceOrder order.</summary>
/// <param name="TopSpeedFx">The highest LapMaxSpeed value (16.16, m/s), the same one that [ref:2152]
/// shows on the results screen ("top speed"; in the original printed as %5.2f of a fixed-point value).</param>
public sealed record RacerResult(int Place, int CarIndex, string Model, bool Human, bool Estimated,
    int RaceTicks, IReadOnlyList<int> LapTicks, int? BestLapTicks, int RaceState = 2, string Driver = "",
    int TopSpeedFx = 0, int CarType = 0);

public sealed record RaceResults(IReadOnlyList<RacerResult> Racers)
{
    public static RaceResults Capture(RaceRunner runner)
    {
        var rows = runner.Laps.Ranking().Select(i =>
        {
            var sim = runner.Cars[i];
            var c = sim.Obj;
            var laps = Array.AsReadOnly(c.LapTimes.Take(Math.Clamp(c.LapsDone, 0, 8)).ToArray());
            return new RacerResult(c.RacePosition, i, sim.Race.Model,
                (c.Flags & NFS.Core.CarFlags.LocalHuman) != 0, c.LapsDone != runner.Laps.NumLaps,
                c.LapStartTick, laps, laps.Where(t => t > 0).Select(t => (int?)t).Min(), c.RaceState,
                // AI names follow config +0x30, not the grid slot. [ref:2153]
                sim.Race.Setup.CarClass == 2 && sim.Race.Setup.Personality is >= 0 and < 11
                    ? RaceConstants.AiDriverNames[sim.Race.Setup.Personality]
                    : RaceLoader.CarName(runner.Session.Setup, c.PlayerIndex),
                // takes the maximum over the whole array (+0x258, 8 entries; unfilled = 0). [ref:2154]
                c.LapMaxSpeed.Max(),
                sim.Race.Setup.CarType);
        }).ToArray();
        return new RaceResults(Array.AsReadOnly(rows));
    }
}

public static class RacePresentation
{
    /// <summary>Countdown stages 4/3/2/1/0. [ref:2155]
    /// displays cnt0..4 only for 0x120 &lt; tick &lt; 0x220. Text substitutes the original shapes.</summary>
    public static string CountdownText(int tick) => tick switch
    {
        <= 0x120 or >= 0x220 => "",
        < 0x140 => "READY",
        < 0x180 => "3",
        < 0x1C0 => "2",
        < 0x200 => "1",
        _ => "GO"
    };
}
