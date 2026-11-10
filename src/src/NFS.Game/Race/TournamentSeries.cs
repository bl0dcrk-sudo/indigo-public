using NFS.Data.FrontEnd;

namespace NFS.Game.Race;

/// <summary>Tournament in RAM. One human (identity 0) or split screen (identities 0 and 1 are [ref:2394]
/// humans; a tournament has no track mask: both race every round). [ref:2395]</summary>
public sealed class TournamentSeries
{
    // s16[8] points (+0x2B0 in the save file); menu bits from the original layout [ref:2396]
    private static readonly int[] Awards = [30, 25, 20, 16, 12, 8, 4, 0];
    private static readonly int[] TrackBits = [1, 2, 16, 4, 8, 32, 64];
    private int[] _points = new int[8], _places = Enumerable.Range(0, 8).ToArray();
    private int[]? _beforePoints, _beforePlaces;
    private int _beforeMask;
    private bool _rewardClosed, _humanFinished;
    /// <summary>Human entrants (identities 0..Humans-1): 1 = one player, 2 = split screen.</summary>
    public int Humans { get; private set; } = 1;
    public int Track { get; private set; }
    public int CompletedMask { get; private set; }
    public int CompletedTracks => System.Numerics.BitOperations.PopCount((uint)CompletedMask);
    public bool ResultsApplied { get; private set; }
    public bool Finished => CompletedMask == 0x7F;
    public IReadOnlyList<int> Points => Array.AsReadOnly(_points);
    public IReadOnlyList<int> AvailableTracks => Enumerable.Range(0, 7).Where(t => (CompletedMask & TrackBits[t]) == 0).ToArray();
    // stable ascending sort, human tie correction (the human goes up; with two humans tied, the better live rank [ref:2397]
    // = last race place goes up), display backwards.
    public IReadOnlyList<int> Standings
    {
        get
        {
            var order = Enumerable.Range(0, 8).OrderBy(i => _points[i]).ToArray();
            // Preserve the original six passes (outer < count-1), including the all-zero initial case.
            for (int outer = 1; outer < 7; outer++)
                for (int j = 7; j >= outer; j--)
                    if (_points[order[j - 1]] == _points[order[j]] && order[j - 1] < Humans
                        && (order[j] >= Humans || _places[order[j - 1]] < _places[order[j]]))
                        (order[j - 1], order[j]) = (order[j], order[j - 1]);
            return order.Reverse().ToArray();
        }
    }
    public int GridSlot(int identity) => _places[identity];

    public TournamentSeries(int track = 0, int humans = 1)
    {
        if (humans is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(humans));
        Humans = humans; SelectTrack(track);
    }
    private TournamentSeries() { }

    /// <summary>Save to `.tou`: copy of the config + points + mask. The starting LINEUP slots are not stored in the file. [ref:2398]</summary>
    public TouFile ToTou(FeConfig config)
    {
        var f = new TouFile { TrackCount = (ushort)CompletedTracks, Mask = CompletedMask };
        config.Raw.CopyTo(f.Config, 0);
        for (int i = 0; i < 8; i++) f.Points[i] = (short)_points[i];
        return f;
    }

    /// <summary>Load of `.tou`: points and mask from the file; LINEUP rebuilt from the points by an ascending sort [ref:2399]
    /// the next track is <paramref name="preferredTrack"/>, or the first track not yet completed. [ref:2400]</summary>
    public static TournamentSeries Restore(TouFile file, int preferredTrack, int humans = 1)
    {
        int mask = file.Mask & 0x7F;
        if (mask == 0x7F) throw new InvalidDataException("Series is already finished.");
        var s = new TournamentSeries { CompletedMask = mask, Humans = humans };
        for (int i = 0; i < 8; i++) s._points[i] = file.Points[i];
        var order = Enumerable.Range(0, 8).OrderBy(i => s._points[i]).ToArray();
        for (int pos = 0; pos < 8; pos++) s._places[order[pos]] = pos;
        var free = s.AvailableTracks;
        s.Track = free.Contains(preferredTrack) ? preferredTrack : free[0];
        return s;
    }

    public void SelectTrack(int track)
    {
        if (track is < 0 or > 6 || (CompletedMask & TrackBits[track]) != 0)
            throw new ArgumentOutOfRangeException(nameof(track), "Choose an uncompleted tournament track.");
        Track = track;
    }

    public void Complete(RaceResults results)
    {
        if (ResultsApplied) return; // results are applied once [ref:2401]
        if (Finished) throw new InvalidOperationException("Tournament is finished.");
        var rows = results.Racers.OrderBy(r => r.CarIndex).ToArray();
        if (rows.Length != 8 || !rows.Select(r => r.CarIndex).SequenceEqual(Enumerable.Range(0, 8)) ||
            !rows.Select(r => r.Place).Order().SequenceEqual(Enumerable.Range(1, 8)) ||
            rows.Take(Humans).Any(r => !r.Human) || rows.Skip(Humans).Any(r => r.Human))
            throw new ArgumentException("Tournament needs the human racers first and the rest AI racers.", nameof(results));
        _beforePoints = (int[])_points.Clone(); _beforePlaces = (int[])_places.Clone(); _beforeMask = CompletedMask;
        // signed race times, two stable sorts; DNF humans get no points. [ref:2402]
        var times = rows.Select(r => r.RaceTicks).ToArray();
        for (int i = 0; i < 8; i++)
            if (rows[i].Human && rows[i].RaceState != 2)
                times[i] = times[i] < 0 ? unchecked(-times[i] + 0x1C200 + i * 0xE4C) : unchecked(times[i] + 0x1C200);
        int last = Enumerable.Range(0, 8).OrderBy(i => times[i]).Last();
        for (int i = 0; i < 8; i++)
            if (rows[i].Human && rows[i].RaceState != 2)
                times[i] = times[i] < 0 ? unchecked(-times[i] + i * 0xE4C + times[last]) : unchecked(times[i] + times[last]);
        var order = Enumerable.Range(0, 8).OrderBy(i => times[i]).ToArray();
        for (int p = 0; p < 8; p++)
        {
            int i = order[p];
            if (!rows[i].Human || rows[i].RaceState == 2) _points[i] += Awards[p];
            // the live 1-based rank (+0x278) is copied into LINEUP, while the first [ref:2403]
            // race and the .tou load use 0-based slots; this does not normalise, so [ref:2404]
            // slot 8 runs past the grid (reverse: slice -3). Deviation: store rank-1, keeping slots 0..7.
            _places[i] = rows[i].Place - 1;
        }
        _humanFinished = rows[0].RaceState == 2;
        CompletedMask |= TrackBits[Track]; ResultsApplied = true;
    }

    public void NextRace(int track)
    {
        if (!ResultsApplied || Finished) throw new InvalidOperationException("Tournament has no next race.");
        SelectTrack(track); ResultsApplied = false;
        _beforePoints = _beforePlaces = null;
    }

    public void RestartStage()
    {
        if (!ResultsApplied) return;
        _points = _beforePoints!; _places = _beforePlaces!; CompletedMask = _beforeMask;
        ResultsApplied = false; _humanFinished = false;
    }

    /// <summary>The podium raises CONFIG+0x1D only when closing a finished final. [ref:2405]</summary>
    public bool CloseFinal(FeConfig config)
    {
        if (_rewardClosed || !Finished || !ResultsApplied) return false;
        _rewardClosed = true;
        if (!_humanFinished) return false;
        int level = Math.Max(0, 3 - Standings.ToList().IndexOf(0));
        if (config[0x1D] >= level) return false;
        config[0x1D] = (byte)level; return true;
    }
}
