using NFS.Core;
using NFS.Render;

namespace NFS.Game.Race;

/// <summary>Headless dash HUD values. [ref:2105]</summary>
public static class RaceHud
{
    /// <summary>The rival shown in the HUD text strip (index into the ranked list's car space). [ref:2106]
    /// Rank r = 1-based position of the viewed car in the race order; looking forward the car one place ahead
    /// (none when leading), looking behind (0x17) the car one place behind (none when last). [ref:2107]</summary>
    public static int? RivalIndex(IReadOnlyList<int> ranking, int self, bool lookBehind)
    {
        int r = -1;
        for (int i = 0; i < ranking.Count; i++) if (ranking[i] == self) r = i + 1;
        if (r < 0) return null;
        if (lookBehind) return r < ranking.Count ? ranking[r] : null;
        return r > 1 ? ranking[r - 2] : null;
    }

    /// <param name="band">wrong-way band state of the camera player's car (0/1/2). [ref:2108]</param>
    public static OriginalHudValues Read(CarView c, int tick, int laps, int racers, int redline,
        bool metric, int recordTicks = 0, string? rival = null, int band = 0)
    {
        int elapsed = tick - c.LapStartTick;
        int lapTime = tick < 0x200 ? 0 : c.LapsDone == 0 || (elapsed > 0x13F && c.RaceState != 2)
            ? elapsed : c.LapTimes[Math.Clamp(c.LapsDone - 1, 0, 7)];
        // loads the track record; updates it after completing a faster lap. [ref:2109]
        int best = recordTicks;
        foreach (int t in c.LapTimes.Take(Math.Clamp(c.LapsDone, 0, 8)))
            if (t > 0 && (best == 0 || t < best)) best = t;
        // rounded FX multiply, then truncating division by 65536. [ref:2110]
        int speed = Fx.Mul(Fx.Abs(c.LocalVel.Z), metric ? 0x39999 : 0x23CA5) / Fx.One;
        return new(speed, c.Gear, Math.Clamp(c.LapsDone + 1, 1, Math.Max(1, laps)), laps,
            c.RacePosition, racers, lapTime, best, c.Rpm, redline, tick,
            elapsed < 0x140 && laps == Math.Clamp(c.LapsDone + 1, 1, Math.Max(1, laps)), metric,
            c.LapsDone > 0 && (elapsed <= 0x13F || c.RaceState == 2) && lapTime == best
                && tick <= c.LastProgressTick + 0xC0, rival, band);
    }
}
