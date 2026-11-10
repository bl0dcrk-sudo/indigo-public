using NFS.Game.Race;

namespace NFS.Game.FrontEnd;

/// <summary>Announcer commentary on the KO and tournament-final result screens – choice of clip (name in ENGLISH.VIV) from functions called once at
/// the first pass of the screen loop (KO / tournament flags): KO after a round, then; [ref:1968]
/// KO final, then; tournament final. Each one interrupts [ref:1969]
/// the previous clip, so the last one is heard – the functions return exactly that one. Clip names: see the tables in the code. [ref:1970]
/// Car fields: human = +0x1F4 &amp; 4 (<see cref="RacerResult.Human"/>), finished = +0x280 == 2 (<see cref="RacerResult.RaceState"/>),
/// place = +0x278. playerCars: 0 single machine, 1 split screen, ≥ 2 network – the port counts only 0/1 here (local player = car 0). [ref:1971]</summary>
public static class ResultSpeech
{
    /// <summary>(table [0,0,1,4,2,3,5,6,7]) == 6, i.e. track number 7 – KO has its own clips c34..c37 on it. [ref:1972]</summary>
    public const int SpecialTrackNumber = 7;

    /// <summary>KO results screen. <paramref name="final"/> = final (stage 7), otherwise a round. [ref:1973]
    /// <paramref name="splitScreen"/> = <c>1</c>, <paramref name="playerCars"/> is the number of player cars. [ref:1974]</summary>
    public static string? Knockout(IReadOnlyList<RacerResult> rows, bool final, bool splitScreen, int playerCars, int trackNumber)
    {
        if (rows.Count == 0) return null;
        string? clip = Eliminated(rows, splitScreen, playerCars);
        return (final ? FinalWinner(rows, splitScreen, playerCars) : Survivors(rows, splitScreen, trackNumber == SpecialTrackNumber)) ?? clip;
    }

    /// <summary>The last car in the results list (the eliminated one) is human and finished → c28/c29 (split, player 0/1), otherwise c27. [ref:1975]</summary>
    static string? Eliminated(IReadOnlyList<RacerResult> rows, bool splitScreen, int playerCars)
    {
        var last = rows.MaxBy(r => r.Place)!;
        if (last.RaceState != 2 || !last.Human) return null;
        if (splitScreen && playerCars > 1) return last.CarIndex switch { 0 => "c28", 1 => "c29", _ => null };
        return "c27";
    }

    /// <summary>(KO round): split – the number of humans other than the eliminated one who finished: ≥ 2 → c37/c33; ≥ 1, player 0 eliminated [ref:1976]
    /// and player 1 finished → c36/c32; ≥ 1 and player 0 finished → c35/c31. Single machine: someone other than player 0 was eliminated and player 0
    /// finished → c34/c30. The first clip of the pair – track <see cref="SpecialTrackNumber"/>.</summary>
    static string? Survivors(IReadOnlyList<RacerResult> rows, bool splitScreen, bool special)
    {
        int eliminated = rows.MaxBy(r => r.Place)!.CarIndex;
        bool Finished(int car) => rows.FirstOrDefault(r => r.CarIndex == car)?.RaceState == 2;
        if (splitScreen)
        {
            int survivors = rows.Count(r => r.Human && r.CarIndex != eliminated && r.RaceState == 2);
            if (survivors >= 2) return special ? "c37" : "c33";
            if (survivors >= 1 && eliminated == 0 && Finished(1)) return special ? "c36" : "c32";
            if (survivors >= 1 && Finished(0)) return special ? "c35" : "c31";
            return null;
        }
        return eliminated != 0 && Finished(0) ? (special ? "c34" : "c30") : null;
    }

    /// <summary>(KO final): the human in 1st place who finished, being the local player (car 0) → c25/c26 (split of two [ref:1977]
    /// players) or c24. In the original the last such human must be player 0, so a win by player 1 in split screen stays silent.</summary>
    static string? FinalWinner(IReadOnlyList<RacerResult> rows, bool splitScreen, int playerCars)
    {
        var winner = rows.LastOrDefault(r => r.Place == 1 && r.Human && r.RaceState == 2);
        if (winner is null || winner.CarIndex != 0) return null;
        return splitScreen && playerCars == 2 ? "c25" : "c24";
    }

    /// <summary>(tournament final): points table in ascending order, positions 7/6/5 = 1st/2nd/3rd place with 8 cars. [ref:1978]
    /// For each of them, when it is a human who finished the last race: split (<c>1</c>) – c13/c14, c16/c17, c19/c20 [ref:1979]
    /// for player 0/1; otherwise, when it is the local player – c12, c15, c18. The last one selected is played, i.e. the human's lowest place.</summary>
    public static string? TournamentFinal(IReadOnlyList<RacerResult> rows, IReadOnlyList<int> standings, bool splitScreen)
    {
        string?[][] clips = [["c13", "c14", "c12"], ["c16", "c17", "c15"], ["c19", "c20", "c18"]];
        string? clip = null;
        for (int place = 0; place < 3 && place < standings.Count; place++)
        {
            var row = rows.FirstOrDefault(r => r.CarIndex == standings[place]);
            if (row is not { Human: true, RaceState: 2 }) continue;
            if (splitScreen) clip = row.CarIndex is 0 or 1 ? clips[place][row.CarIndex] : clip;
            else if (row.CarIndex == 0) clip = clips[place][2];
        }
        return clip;
    }
}
