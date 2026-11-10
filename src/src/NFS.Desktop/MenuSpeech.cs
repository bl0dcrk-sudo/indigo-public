using NFS.Core.Constants;
using NFS.Game.FrontEnd;

namespace NFS.Desktop;

/// <summary>Menu state needed by the narration triggers in a given frame (<see cref="MenuSpeech.Advance"/>).</summary>
/// <param name="Victory">victory screen active [ref:1288]</param>
/// <param name="Showcase">car showcase screen, <paramref name="ShowcaseCar"/> = the car shown, 0..11 [ref:1289]</param>
/// <param name="History">car history screen, <paramref name="HistoryCar"/> = car 0..11 [ref:1290]</param>
/// <param name="TrackInfo">track description screen (<paramref name="TrackIntro"/> = entry still in progress), <paramref name="Track"/> = track index in the menu [ref:1291]</param>
/// <param name="Language">CONFIG +0x5B (0 = English)</param>
/// <param name="Units">CONFIG +0x5A</param>
/// <param name="ResultKey">results shown on the KO / tournament final screen (null outside them); a new object = first entry to the screen</param>
/// <param name="ResultClip">clip of this screen (<see cref="ResultSpeech"/>), computed only for a new <paramref name="ResultKey"/></param>
public readonly record struct MenuSpeechState(bool Victory = false, bool Showcase = false, int ShowcaseCar = 0, bool History = false,
    int HistoryCar = 0, bool TrackInfo = false, bool TrackIntro = false, int Track = 0, int Language = 0, int Units = 0,
    object? ResultKey = null, Func<string?>? ResultClip = null);

/// <summary>Front-end narration triggers – call sites in the menu screens (clip names in [ref:1292]
/// ENGLISH.VIV, see <see cref="NFS.Audio.FeSpeech"/>): victory "c00" after 0x80 ticks of the 128 Hz clock, car showcase "%02d0",
/// car history "h%d0" (table), track description "t%d0" (table). Time is counted by the 128 Hz clock; the class does not play [ref:1293] [ref:3401]
/// sound – it reports the clip name through the <see cref="Requested"/> event. KO and tournament final screens (c12…c37) – once per result
/// (flags); the clip is chosen by <see cref="ResultSpeech"/>. Track records (c03…c11, statchk) are reported [ref:1294]
/// by the menu (<see cref="Say"/>). There is no network mode (c01/c02).</summary>
public sealed class MenuSpeech
{
    public const double TimerHz = 128;
    /// <summary><c>0x7F &lt; ticks</c> since entering the victory screen. [ref:1295]</summary>
    public const string VictoryClip = "c00";
    public const int VictoryTicks = VictoryRules.SpeechTick;
    /// <summary>animation frame ≥ 0x23 (every 10 ticks) and &gt; 0x78 ticks since the start (0x78 &lt; 0x23·10, so the frame decides); [ref:1296]
    /// speech only starts in the next loop iteration after the frame advances, hence "&gt;" (an approximation of the iteration boundary).</summary>
    public const int ShowcaseTicks = 0x23 * 10;
    /// <summary><c>0xA0 &lt; ticks</c> (sum of ticks since entering / changing the car). [ref:1297]</summary>
    public const int HistoryTicks = 0xA0;
    /// <summary>frame &gt; 0x22 (i.e. ≥ 0x23), counted from the end of the entry; after a track change the frame stays full [ref:1298]
    /// so speech starts at once.</summary>
    public const int TrackInfoTicks = 0x23 * 10;
    /// <summary>Table (u16, 12 cars): history clip number for "h%d0". [ref:1299]</summary>
    public static IReadOnlyList<int> HistoryClipNumbers { get; } = [0, 1, 1, 2, 2, 2, 3, 4, 4, 6, 6, 7];

    public event Action<string>? Requested;

    double _victoryTicks; bool _victorySpoken;
    int _showcaseCar = -1; double _showcaseTicks; bool _showcaseSpoken;
    int _historyCar = -1; double _historyTicks; bool _historySpoken;
    int _trackSeen = -1; double _trackTicks; bool _trackSpoken;
    bool _wasShowcase, _wasHistory, _wasTrackInfo;
    object? _resultKey;

    /// <summary>"%02d0" with the car number; for English (CONFIG +0x5B = 0) with units == 1, car 7 says clip "120" (number + 5). [ref:1300]</summary>
    public static string ShowcaseClip(int car, int language, int units) =>
        $"{(language == 0 && units == 1 && car == 7 ? car + 5 : car):D2}0";

    /// <summary>"h%d0" with <see cref="HistoryClipNumbers"/>[car]. [ref:1301]</summary>
    public static string HistoryClip(int car) => $"h{HistoryClipNumbers[Math.Clamp(car, 0, HistoryClipNumbers.Count - 1)]}0";

    /// <summary>"t%d0" with the race track number (menu index → track number, outside 0..7 → 0). [ref:1302]</summary>
    public static string TrackInfoClip(int menuTrack) =>
        $"t{(menuTrack is >= 0 and < 8 ? FrontEndConstants.TrackMap[menuTrack] : 0)}0";

    /// <summary>Clip reported directly by the menu (track record c03…c11, statchk). [ref:1303]</summary>
    public void Say(string clip) => Requested?.Invoke(clip);

    /// <summary>Menu frame: <paramref name="seconds"/> of real time since the previous call.</summary>
    public void Advance(double seconds, in MenuSpeechState s)
    {
        double ticks = Math.Max(seconds, 0) * TimerHz;
        string? clip = null;

        if (s.Victory)
        {
            _victoryTicks += ticks;
            if (!_victorySpoken && _victoryTicks >= VictoryTicks) { _victorySpoken = true; clip = VictoryClip; }
        }
        else { _victoryTicks = 0; _victorySpoken = false; }

        if (s.Showcase)
        {
            if (!_wasShowcase || s.ShowcaseCar != _showcaseCar) { _showcaseCar = s.ShowcaseCar; _showcaseTicks = 0; _showcaseSpoken = false; }
            _showcaseTicks += ticks;
            if (!_showcaseSpoken && _showcaseTicks > ShowcaseTicks)
            {
                _showcaseSpoken = true;
                clip = ShowcaseClip(s.ShowcaseCar, s.Language, s.Units);
            }
        }
        _wasShowcase = s.Showcase;

        if (s.History)
        {
            if (!_wasHistory || s.HistoryCar != _historyCar) { _historyCar = s.HistoryCar; _historyTicks = 0; _historySpoken = false; }
            _historyTicks += ticks;
            if (!_historySpoken && _historyTicks > HistoryTicks) { _historySpoken = true; clip = HistoryClip(s.HistoryCar); }
        }
        _wasHistory = s.History;

        if (s.TrackInfo)
        {
            if (!_wasTrackInfo) { _trackTicks = 0; _trackSpoken = false; _trackSeen = s.Track; }
            else if (s.Track != _trackSeen) { _trackSeen = s.Track; _trackSpoken = false; }
            if (!s.TrackIntro) _trackTicks += ticks;
            if (!_trackSpoken && !s.TrackIntro && _trackTicks >= TrackInfoTicks) { _trackSpoken = true; clip = TrackInfoClip(s.Track); }
        }
        _wasTrackInfo = s.TrackInfo;

        if (s.ResultKey != null && !ReferenceEquals(s.ResultKey, _resultKey))
        {
            _resultKey = s.ResultKey;
            if (s.ResultClip?.Invoke() is { } result) clip = result;
        }

        if (clip != null) Requested?.Invoke(clip);
    }
}
