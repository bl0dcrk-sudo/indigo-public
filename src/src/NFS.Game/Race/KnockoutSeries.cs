using NFS.Data.FrontEnd;

namespace NFS.Game.Race;

/// <summary>KO, RAM only. One human (identity 0) or split screen (humans = identities 0 and 1 [ref:2043]
/// AI from 2): the mask tells which humans still race; the series ends when none is left (case 1). [ref:2044]</summary>
public sealed class KnockoutSeries
{
    public KnockoutSeries(int humans = 1)
    {
        if (humans is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(humans));
        Humans = humans;
    }

    KnockoutSeries(int humans, bool network)
    {
        if (humans is < 1 or > MaxNetworkHumans) throw new ArgumentOutOfRangeException(nameof(humans));
        Humans = humans;
        Network = network;
    }

    /// <summary>Most humans of a network series (the ninth entrant is always an AI car).</summary>
    public const int MaxNetworkHumans = 8;

    /// <summary>Series of the dedicated server (not in the original): 1..8 humans with identities 0..Humans-1, AI from Humans.
    /// Differences from the local series: a DNF does not end the series (the disconnected humans pass as the last of the round and
    /// leave together with the last place; a straggler cut off by the time limit is only ranked behind the finishers),
    /// <see cref="Forfeit"/> removes a human that is gone between rounds, and grid slots follow the rank among the remaining
    /// entrants, so removed entrants never leave a gap.</summary>
    public static KnockoutSeries ForNetwork(int humans) => new(humans, true);

    /// <summary>The series of the dedicated server (see <see cref="ForNetwork"/>).</summary>
    public bool Network { get; }
    /// <summary>Number of human entrants (identities 0..Humans-1): 1 = one player, 2 = split screen (network: 1..8).</summary>
    public int Humans { get; }
    /// <summary>human <paramref name="human"/> is still in the series. [ref:2045]</summary>
    public bool HumanActive(int human) => human < Humans && _active[human];
    public bool AnyHumanActive => Enumerable.Range(0, Humans).Any(HumanActive);
    private bool[] _active = Enumerable.Repeat(true, 9).ToArray();
    private int[] _places = Enumerable.Range(1, 9).ToArray();
    private bool[]? _beforeActive;
    private int[]? _beforePlaces;
    private bool _rewardClosed;
    private readonly int[] _eliminatedRound = Enumerable.Repeat(-1, 9).ToArray();
    public int Stage { get; private set; }
    public bool ResultsApplied { get; private set; }
    public int? EliminatedIdentity { get; private set; }
    public bool Aborted { get; private set; }
    public bool Won { get; private set; }
    public bool CanContinue => ResultsApplied && !Aborted && AnyHumanActive && Stage < 7 && Entrants.Count > 1;
    /// <summary>Every entrant eliminated by the last <see cref="Complete(RaceResults, IReadOnlyCollection{int}?)"/> (network: the
    /// last place plus the disconnected humans; local: at most the last place).</summary>
    public IReadOnlyList<int> Eliminated { get; private set; } = [];
    /// <summary>Round (stage) in which the identity was eliminated, -1 while it is still in.</summary>
    public int EliminatedRound(int identity) => _eliminatedRound[identity];
    /// <summary>Place of the identity in the last completed round (its starting rank before the first round).</summary>
    public int Place(int identity) => _places[identity];
    /// <summary>The series is over: the results are applied and no further round follows.</summary>
    public bool Finished => ResultsApplied && !CanContinue;
    /// <summary>Network series that ended with one entrant left: its identity (-1 while running or when every human is out).</summary>
    public int Winner => Network && Finished && Entrants.Count == 1 ? Entrants[0] : -1;
    public IReadOnlyList<int> Entrants => Enumerable.Range(0, 9).Where(i => _active[i]).ToArray();
    // Compact race indices always follow persistent identities, with the human first. [ref:2046]
    public IReadOnlyList<int> RaceIdentities { get; private set; } = Enumerable.Range(0, 9).ToArray();
    public int GridSlot(int identity) => Stage == 0 ? identity : RaceIdentities.Count - Rank(identity);

    // The best of the previous round starts at the front. After a plain elimination the places of the entrants are exactly
    // 1..n; a network forfeit leaves gaps, so the rank counts the entrants that finished ahead.
    private int Rank(int identity) => 1 + RaceIdentities.Count(other => _places[other] < _places[identity]);

    /// <summary>Network: a human that is gone between rounds leaves the series (it never raced the next round). A forfeit before
    /// the first round is not allowed (the series would simply start without it).</summary>
    public void Forfeit(int identity)
    {
        if (!Network) throw new InvalidOperationException("only a network series has forfeits");
        if ((uint)identity >= (uint)Humans) throw new ArgumentOutOfRangeException(nameof(identity));
        if (!_active[identity]) return;
        _active[identity] = false;
        _eliminatedRound[identity] = ResultsApplied ? Stage + 1 : Stage;
        if (!ResultsApplied && Stage > 0) RaceIdentities = Entrants; // the round before its start: race without it
    }

    /// <param name="disconnectedCars">Network only: race car indices of humans that were gone from the round (DNF, ranked last);
    /// they are eliminated together with the last place.</param>
    public void Complete(RaceResults results, IReadOnlyCollection<int>? disconnectedCars = null)
    {
        if (ResultsApplied) return; // [ref:2047]
        var rows = results.Racers;
        int n = RaceIdentities.Count;
        if (rows.Count != n || !rows.Select(r => r.CarIndex).Order().SequenceEqual(Enumerable.Range(0, n)) ||
            !rows.Select(r => r.Place).Order().SequenceEqual(Enumerable.Range(1, n)))
            throw new ArgumentException("KO results must contain each entrant and rank exactly once.", nameof(results));
        _beforeActive = (bool[])_active.Clone(); _beforePlaces = (int[])_places.Clone();
        foreach (var row in rows) _places[RaceIdentities[row.CarIndex]] = row.Place;
        if (Network)
        {
            var gone = new List<int> { RaceIdentities[rows.Single(r => r.Place == n).CarIndex] };
            foreach (int car in disconnectedCars ?? [])
                if ((uint)car < (uint)n && RaceIdentities[car] < Humans && !gone.Contains(RaceIdentities[car])) gone.Add(RaceIdentities[car]);
            foreach (int id in gone) { _active[id] = false; _eliminatedRound[id] = Stage; }
            Eliminated = gone;
            EliminatedIdentity = gone[0];
            ResultsApplied = true;
            return;
        }
        Aborted = rows.Any(r => r.RaceState == 1);
        if (Aborted) Array.Fill(_active, false); // Single player DNF ends the series, not just that car.
        else
        {
            EliminatedIdentity = RaceIdentities[rows.Single(r => r.Place == n).CarIndex];
            _active[EliminatedIdentity.Value] = false;
            _eliminatedRound[EliminatedIdentity.Value] = Stage;
            Eliminated = [EliminatedIdentity.Value];
        }
        Won = Stage == 7 && !Aborted && rows.Single(r => r.CarIndex == 0) is { Place: 1, RaceState: 2 };
        ResultsApplied = true;
    }

    public void NextRace()
    {
        if (!CanContinue) throw new InvalidOperationException("KO has no next race.");
        Stage++; RaceIdentities = Entrants;
        ResultsApplied = false; EliminatedIdentity = null; Eliminated = [];
        _beforeActive = null; _beforePlaces = null;
    }

    public void RestartStage()
    {
        if (!ResultsApplied) return;
        _active = _beforeActive!; _places = _beforePlaces!;
        ResultsApplied = false; EliminatedIdentity = null; Eliminated = []; Aborted = Won = false;
        for (int i = 0; i < 9; i++) if (_eliminatedRound[i] == Stage) _eliminatedRound[i] = -1;
    }

    /// <summary>Grants the reward when the final screen is closed, once; other unlock bits are preserved. [ref:2048]</summary>
    public bool CloseFinal(FeConfig config)
    {
        if (_rewardClosed || !ResultsApplied || Stage != 7) return false;
        _rewardClosed = true;
        if (!Won) return false;
        bool changed = (config[0x1C] & 1) == 0;
        config[0x1C] |= 1;
        return changed;
    }
}
