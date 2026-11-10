using NFS.Audio;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;

namespace NFS.Desktop.Tests;

/// <summary>Front-end voice-over triggers: which clip and when, after entering a screen. [ref:2839]</summary>
public class MenuSpeechTests
{
    const double Tick = 1.0 / MenuSpeech.TimerHz;

    static (MenuSpeech Speech, List<string> Clips) Make()
    {
        var s = new MenuSpeech();
        var clips = new List<string>();
        s.Requested += clips.Add;
        return (s, clips);
    }

    [Fact]
    public void Victory_SpeaksC00_ExactlyAt0x80Ticks_Once()
    {
        var (s, clips) = Make();
        var v = new MenuSpeechState(Victory: true);
        s.Advance(0x7F * Tick, v);                     // 0x7F < ticks – not yet [ref:2840]
        Assert.Empty(clips);
        s.Advance(1 * Tick, v);                        // tick 0x80
        Assert.Equal(["c00"], clips);
        s.Advance(2.0, v);                             // does not repeat during the screen (is cleared) [ref:3456]
        Assert.Single(clips);
        s.Advance(1.0, v with { Victory = false });    // end of the screen – the next victory speaks again
        s.Advance(0x80 * Tick, v);
        Assert.Equal(["c00", "c00"], clips);
    }

    [Fact]
    public void ShowcaseClip_NamesCarsAndEnglishImperialSpecialCase()
    {
        Assert.Equal("000", MenuSpeech.ShowcaseClip(0, 0, 0));
        Assert.Equal("110", MenuSpeech.ShowcaseClip(11, 3, 1));
        Assert.Equal("070", MenuSpeech.ShowcaseClip(7, 0, 0));    // units != 1 → plain number
        Assert.Equal("070", MenuSpeech.ShowcaseClip(7, 1, 1));    // language != 0 → plain number
        Assert.Equal("120", MenuSpeech.ShowcaseClip(7, 0, 1));    // language 0, units 1, car 7 → number + 5 [ref:2841]
        Assert.Equal("050", MenuSpeech.ShowcaseClip(5, 0, 1));
    }

    [Fact]
    public void Showcase_SpeaksAfterAnimationAndRestartsOnCarChange()
    {
        var (s, clips) = Make();
        var st = new MenuSpeechState(Showcase: true, ShowcaseCar: 3);
        s.Advance(MenuSpeech.ShowcaseTicks * Tick, st);
        Assert.Empty(clips);                           // frame 0x23 only just reached, speech in the next iteration
        s.Advance(2 * Tick, st);
        Assert.Equal(["030"], clips);
        s.Advance(5.0, st);
        Assert.Single(clips);                          // once per entry
        st = st with { ShowcaseCar = 4 };              // changing the car resets the counter and re-arms it
        s.Advance(1.0, st);
        Assert.Single(clips);
        s.Advance(2.0, st);
        Assert.Equal(["030", "040"], clips);
        s.Advance(1.0, new MenuSpeechState());         // leaving the screen
        s.Advance(4.0, st);
        Assert.Equal(["030", "040", "040"], clips);
    }

    [Fact]
    public void HistoryClip_UsesTheOriginalTable()
    {
        int[] expected = [0, 1, 1, 2, 2, 2, 3, 4, 4, 6, 6, 7];
        for (int car = 0; car < 12; car++) Assert.Equal($"h{expected[car]}0", MenuSpeech.HistoryClip(car));
    }

    [Fact]
    public void History_SpeaksAfter0xA0Ticks_AndAgainAfterCarChange()
    {
        var (s, clips) = Make();
        var st = new MenuSpeechState(History: true, HistoryCar: 9);
        s.Advance(MenuSpeech.HistoryTicks * Tick, st);
        Assert.Empty(clips);                           // 0xA0 < ticks, so equal to 0xA0 is not enough
        s.Advance(Tick, st);
        Assert.Equal(["h60"], clips);
        s.Advance(3.0, st);
        Assert.Single(clips);
        s.Advance(2.0, st with { HistoryCar = 11 });
        Assert.Equal(["h60", "h70"], clips);
    }

    [Fact]
    public void TrackInfoClip_UsesRaceTrackNumberOfMenuTrack()
    {
        // menu index → track number {0,2,4,5,3,6,7,8} [ref:2842]
        int[] numbers = [0, 2, 4, 5, 3, 6, 7, 8];
        for (int t = 0; t < 8; t++) Assert.Equal($"t{numbers[t]}0", MenuSpeech.TrackInfoClip(t));
        Assert.Equal("t00", MenuSpeech.TrackInfoClip(8));         // out of range → 0 [ref:2843]
    }

    [Fact]
    public void TrackInfo_SpeaksAfterIntroAndImmediatelyAfterTrackChange()
    {
        var (s, clips) = Make();
        var intro = new MenuSpeechState(TrackInfo: true, TrackIntro: true, Track: 2);
        s.Advance(10.0, intro);                        // entry (synchronous loop) does not count frames
        Assert.Empty(clips);
        var st = intro with { TrackIntro = false };
        s.Advance((MenuSpeech.TrackInfoTicks - 1) * Tick, st);
        Assert.Empty(clips);
        s.Advance(Tick, st);
        Assert.Equal(["t40"], clips);                  // menu 2 → track 4
        s.Advance(3.0, st);
        Assert.Single(clips);
        s.Advance(Tick, st with { Track = 3 });        // track changed with a key: full frame → speech immediately
        Assert.Equal(["t40", "t50"], clips);
        s.Advance(Tick, new MenuSpeechState());        // leaving, re-entry starts from zero
        s.Advance(3.0, intro with { TrackIntro = false });
        Assert.Equal(["t40", "t50", "t40"], clips);
    }

    [GameDataFact]
    public void EveryClipRequestedByTheMenuExistsInEnglishArchive()
    {
        var viv = FeSpeech.TryOpen(0)!;
        var names = new List<string> { "c00" };
        for (int car = 0; car < 12; car++) { names.Add(MenuSpeech.HistoryClip(car)); names.Add(MenuSpeech.ShowcaseClip(car, 0, 1)); names.Add(MenuSpeech.ShowcaseClip(car, 0, 0)); }
        for (int t = 0; t < 8; t++) names.Add(MenuSpeech.TrackInfoClip(t));
        for (int c = 12; c <= 37; c++) if (c is not (21 or 22 or 23)) names.Add($"c{c}");   // knockout / tournament (ResultSpeech)
        Assert.All(names, n => Assert.True(viv.Contains(n), n));
    }

    // ---- Knockout / tournament-final screens (ResultSpeech) ----

    /// <summary>Result rows: <paramref name="order"/> = cars from 1st place; humans = cars &lt; <paramref name="humans"/>.</summary>
    static RacerResult[] Rows(int humans, params int[] order) =>
        order.Select((car, p) => new RacerResult(p + 1, car, $"CAR{car}", car < humans, false, 1000 + p, [], null)).ToArray();

    [Fact]
    public void Knockout_SinglePlayer_SurvivedRound_SaysC30_OrC34OnTrack7()
    {
        var rows = Rows(1, 0, 1, 2, 3);
        Assert.Equal("c30", ResultSpeech.Knockout(rows, final: false, splitScreen: false, playerCars: 1, trackNumber: 2));
        Assert.Equal("c34", ResultSpeech.Knockout(rows, final: false, splitScreen: false, playerCars: 1, trackNumber: 7));
    }

    [Fact]
    public void Knockout_SinglePlayer_Eliminated_SaysC27_DnfSilent()
    {
        Assert.Equal("c27", ResultSpeech.Knockout(Rows(1, 1, 2, 0), false, false, 1, 2));
        var dnf = Rows(1, 1, 2, 0); dnf[2] = dnf[2] with { RaceState = 1 };
        Assert.Null(ResultSpeech.Knockout(dnf, false, false, 1, 2));
    }

    [Fact]
    public void Knockout_SplitScreen_ChoosesBySurvivors()
    {
        Assert.Equal("c33", ResultSpeech.Knockout(Rows(2, 0, 1, 2, 3), false, true, 2, 2));
        Assert.Equal("c37", ResultSpeech.Knockout(Rows(2, 0, 1, 2, 3), false, true, 2, 7));
        // Player 0 was knocked out (last place): says c28, but c32 overrides it (player 1 finished). [ref:2844]
        Assert.Equal("c32", ResultSpeech.Knockout(Rows(2, 1, 2, 0), false, true, 2, 2));
        Assert.Equal("c31", ResultSpeech.Knockout(Rows(2, 0, 2, 1), false, true, 2, 2));
    }

    [Fact]
    public void Knockout_Final_WinnerC24_SplitC25_LoserC27()
    {
        Assert.Equal("c24", ResultSpeech.Knockout(Rows(1, 0, 1), final: true, false, 1, 2));
        Assert.Equal("c27", ResultSpeech.Knockout(Rows(1, 1, 0), final: true, false, 1, 2));
        Assert.Equal("c25", ResultSpeech.Knockout(Rows(2, 0, 1), final: true, true, 2, 2));
        Assert.Equal("c28", ResultSpeech.Knockout(Rows(2, 1, 0), final: true, true, 2, 2));   // silent for player 1 [ref:2845]
    }

    [Fact]
    public void TournamentFinal_SpeaksHumanPlace_LowestWins()
    {
        int[] standings = [3, 0, 1, 2, 4, 5, 6, 7];
        Assert.Equal("c15", ResultSpeech.TournamentFinal(Rows(1, 0, 1, 2, 3, 4, 5, 6, 7), standings, splitScreen: false));
        Assert.Equal("c12", ResultSpeech.TournamentFinal(Rows(1, 0, 1, 2, 3, 4, 5, 6, 7), [0, 1, 2, 3, 4, 5, 6, 7], false));
        Assert.Null(ResultSpeech.TournamentFinal(Rows(1, 0, 1, 2, 3, 4, 5, 6, 7), [7, 6, 5, 4, 3, 2, 1, 0], false));
        // Split: player 0 in 2nd (c16), player 1 in 3rd (c20) – the last one is heard.
        Assert.Equal("c20", ResultSpeech.TournamentFinal(Rows(2, 0, 1, 2, 3, 4, 5, 6, 7), standings, splitScreen: true));
    }

    [Fact]
    public void ResultClip_SpokenOncePerResults()
    {
        var (s, clips) = Make();
        var key = new object();
        int calls = 0;
        Func<string?> clip = () => { calls++; return "c30"; };
        for (int i = 0; i < 5; i++) s.Advance(Tick, new MenuSpeechState(ResultKey: key, ResultClip: clip));
        s.Advance(Tick, new MenuSpeechState());
        s.Advance(Tick, new MenuSpeechState(ResultKey: key, ResultClip: clip));   // back on the same screen – the flag is already cleared
        s.Advance(Tick, new MenuSpeechState(ResultKey: new object(), ResultClip: clip));
        Assert.Equal(["c30", "c30"], clips);
        Assert.Equal(2, calls);
    }
}
