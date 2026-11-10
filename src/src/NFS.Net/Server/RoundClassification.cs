using NFS.Game.Race;

namespace NFS.Net.Server;

/// <summary>Ranking of the cars that did not finish an aborted round (straggler limit, humans gone): finishers keep the order of
/// the host, then the unfinished cars follow by their progress on the track (distance covered, the more the better), and the
/// humans that were gone from the round unfinished come last of all. Pure presentation of the result: the simulation is not
/// touched, only the places and the order of the rows change.</summary>
public static class RoundClassification
{
    /// <param name="results">Result captured by an abort.</param>
    /// <param name="runner">The race the result belongs to (progress of the cars).</param>
    /// <param name="finishedCars">Race car indices that had crossed the finish before the abort (the abort itself marks every unfinished
    /// car, and humans even when finished, as state 1, so the set must be read before it); they are ranked by finish time.</param>
    /// <param name="goneCars">Race car indices of humans that left or lost the connection; when they did not finish they are last.</param>
    public static RaceResults Classify(RaceResults results, RaceRunner runner, IReadOnlyCollection<int> finishedCars,
        IReadOnlyCollection<int> goneCars)
    {
        var rows = results.Racers.Select(r => finishedCars.Contains(r.CarIndex) ? r with { RaceState = 2 } : r).ToArray();
        bool Finished(RacerResult r) => finishedCars.Contains(r.CarIndex);
        int Progress(RacerResult r) => runner.Cars[r.CarIndex].Obj.DistanceSlices;
        var ordered = rows.Where(Finished).OrderBy(r => r.RaceTicks).ThenBy(r => r.Place)
            .Concat(rows.Where(r => !Finished(r) && !goneCars.Contains(r.CarIndex)).OrderByDescending(Progress).ThenBy(r => r.Place))
            .Concat(rows.Where(r => !Finished(r) && goneCars.Contains(r.CarIndex)).OrderByDescending(Progress).ThenBy(r => r.Place))
            .Select((r, i) => r with { Place = i + 1 })
            .ToArray();
        return new RaceResults(Array.AsReadOnly(ordered));
    }
}
