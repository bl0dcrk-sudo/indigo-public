using NFS.Core.Constants;
using NFS.Data.FrontEnd;

namespace NFS.Game.FrontEnd;

/// <summary>Cheat effects that last until the next entry into the front end. The original clears them [ref:1616]
/// at the start (every entry into the menu, also after a race) and at the end, when [ref:1617]
/// the game mode is not single/split or the race type is not single race.</summary>
public sealed class CheatEffects
{
    /// <summary>Forced model (car type) of the player (tag 0x7B) and of traffic cars; 0 = none. [ref:1618]</summary>
    public int ForcedModel { get; set; }
    /// <summary>Flag 4 in the traffic car table (from pjagr.dat) – codes PIONEER, MADLAND, SCHOOLZONE, VIP. [ref:1619]</summary>
    public bool Pioneer { get; set; }
    /// <summary>11/10/9 traffic cars instead of 4 (flag 0x400, and with model &gt; 0x25 also 0x1000). [ref:1620]</summary>
    public bool Crowd { get; set; }
    /// <summary>Flag 0x40 (weather). [ref:1621]</summary>
    public bool Rain { get; set; }
    /// <summary>Flag 0x100. [ref:1622]</summary>
    public bool Chase { get; set; }
    /// <summary>Flag 8. [ref:1623]</summary>
    public bool Slip { get; set; }
    /// <summary>Flag 0x800. [ref:1624]</summary>
    public bool RoadRage { get; set; }

    public void Clear()
    {
        ForcedModel = 0;
        Pioneer = Crowd = Rain = Chase = Slip = RoadRage = false;
    }

    public bool Any => ForcedModel != 0 || Pioneer || Crowd || Rain || Chase || Slip || RoadRage;

    /// <summary>Flags (tag 0x14) from the trailer; bits 0x10/0x20 = cfg +0x30 / +0x4B [ref:1625]
    /// (not for tournament and KO).</summary>
    public int Flags(FeConfig c)
    {
        int f = 0;
        if (Pioneer) f |= 4;
        if (Slip) f |= 8;
        if (c.RaceType is not (1 or 2))
        {
            if (c[0x30] != 0) f |= 0x10;
            if (c[0x4B] != 0) f |= 0x20;
        }
        if (Rain) f |= 0x40;
        if (Chase) f |= 0x100;
        if (Crowd && c.TrafficOff == 0) f |= 0x400;
        if (RoadRage) f |= 0x800;
        if (Crowd && c.TrafficOff == 0 && ForcedModel > 0x25) f |= 0x1000;
        if (c.GameType > 1 || c.RaceType is not (0 or 3)) f = 0;
        return f;
    }
}

/// <summary>Codes typed in the menu (called for each key press). A table of 17 strings [ref:1626]
/// (encrypted: byte = char + 9) + automaton "GO&lt;nn&gt;". They work only when the game is single-player/split (+0x09 in 0,1) [ref:1627]
/// and the chosen race type is single race (+0x0A == 0). Letter case does not matter. A wrong character resets the progress of that code [ref:1628]
/// without checking the character again as a possible start.</summary>
public sealed class CheatCodes
{
    public const int CodeCount = 17;

    public const int Fzr2000 = 0, Tombstone = 1, Bomber = 2, Hollywood = 3, Pioneer = 4, Slip = 5, Slot = 6, Mad = 7, Rain = 8,
        Chase = 9, KcJones = 10, RushHour = 11, RexHour = 12, MadLand = 13, SchoolZone = 14, Vip = 15, RoadRage = 16;

    private readonly string[] _codes = new string[CodeCount];
    private readonly int[] _progress = new int[CodeCount]; // one per code [ref:1629]
    private int _goState, _goValue;                        // [ref:1630]

    public CheatEffects Effects { get; } = new();
    /// <summary>Cheat code names read from the original executable (decrypted).</summary>
    public IReadOnlyList<string> Codes => _codes;
    /// <summary>Index of the last completed code (-1 = none, -2 = "GOnn").</summary>
    public int LastCompleted { get; private set; } = -1;

    public CheatCodes()
    {
        for (int i = 0; i < CodeCount; i++)
        {
            string raw = TextConstants.CheatCodes[i];
            _codes[i] = string.Concat(raw.Select(ch => (char)(ch - 9)));
        }
    }

    /// <param name="Accepted">code/GOnn completed</param>
    /// <param name="ConfigChanged">persistent config fields changed (+0x1C/+0x1D/+0x1E/+0x1F/track) – needs saving</param>
    /// <param name="Dialog">PIONEER code: text dialog 0x34A</param>
    public readonly record struct Result(bool Accepted, bool ConfigChanged, bool Dialog);

    private static int Upper(int k) => k is > 0x60 and < 0x7B ? k - 0x20 : k;

    /// <summary>One key (ASCII code; 0 = none). <paramref name="normalize"/> is used after HOLLYWOOD. [ref:1631]</summary>
    public Result Type(int key, FeConfig c, Action normalize)
    {
        bool accepted = false, changed = false, dialog = false;
        if (key == 0 || (c.GameType != 0 && c.GameType != 1) || c.RaceType != 0) return default;
        int k = Upper(key);
        switch (_goState)
        {
            case 0: if (k == 'G') _goState = 1; break;
            case 1: _goState = k == 'O' ? 2 : 0; break;
            case 2:
                if (k < '0' || k > '9') _goState = 0;
                else { _goState = 3; _goValue = (k - '0') * 10; }
                break;
            case 3:
                if (k is >= '0' and <= '9')
                {
                    _goValue += k - '0';
                    bool ok = true;
                    if (_goValue is 0x16 or 0x18 or 0x19 or 0x1D or 0x1E or 0x20 or 0x12) _goValue = 0x14;
                    else if (_goValue < 0x12 || _goValue > 0x33) ok = false;
                    if (ok)
                    {
                        Effects.ForcedModel = _goValue;
                        accepted = true;
                        LastCompleted = -2;
                    }
                }
                _goState = 0;
                break;
        }
        for (int i = 0; i < CodeCount; i++)
        {
            string s = _codes[i];
            // the next character after a complete code is compared with byte 0 (never matches) and resets the progress [ref:1632]
            if (_progress[i] >= s.Length || k != s[_progress[i]]) { _progress[i] = 0; continue; }
            if (++_progress[i] < s.Length) continue;
            switch (i)
            {
                case Fzr2000: if (c[0x1D] < 3) { c[0x1D] = 3; c.P1Car = 0xE; changed = true; } break;
                case Tombstone: if (c[0x1D] < 2) { c.P1Car = 0xD; c[0x1D] = 2; changed = true; } break;
                case Bomber: if (c[0x1D] == 0) { c[0x1D] = 1; c.P1Car = 0xC; changed = true; } break;
                case Hollywood: c.Track = 7; c[0x1C] |= 1; normalize(); changed = true; break;
                case Pioneer: Effects.Pioneer = true; dialog = true; break;
                case Slip: Effects.Slip = true; break;
                case Slot: c[0x1E] = 1; changed = true; break;
                case Rain: Effects.Rain = true; break;
                case Chase: Effects.Chase = true; break;
                case RushHour: Effects.Crowd = true; break;
                case RexHour: Effects.ForcedModel = 0x2B; Effects.Crowd = true; break;
                case MadLand: Effects.Pioneer = Effects.Crowd = true; Effects.ForcedModel = 0x2A; break;
                case SchoolZone: Effects.Pioneer = Effects.Crowd = true; Effects.ForcedModel = 0x14; break;
                case Vip: Effects.Pioneer = Effects.Crowd = true; Effects.ForcedModel = 0x23; break;
                case RoadRage: Effects.RoadRage = true; break;
                default: continue; // MAD (7), KCJONES (10): no case in the switch – recognized, no effect [ref:1633]
            }
            accepted = true;
            LastCompleted = i;
        }
        return new Result(accepted, changed, dialog);
    }
}
