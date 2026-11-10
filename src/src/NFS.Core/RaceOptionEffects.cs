namespace NFS.Core;

/// <summary>
/// Gameplay effects of race option flags that the front-end cheat codes set (race config +0x20): [ref:0530]
/// CHASE (0x100 = byte bit 0) and ROADRAGE (0x800 = bit 3) [ref:0531]
/// SLIP (8) is handled by <see cref="ArcadeModel.Step"/> (grip class table). [ref:0532]
/// </summary>
public static class RaceOptionEffects
{
    public const int OptBoost = 0x4, OptSlip = 0x8, OptChase = 0x100, OptRoadRage = 0x800;

    /// <summary>
    /// CHASE block (option 0x100, car flag 8 = racing opponent): the leading human [ref:0533]
    /// (max progress +0x06) within −1..+4 slices of this car becomes its psycho target: +0x53C = |Δprogress| (0 = inactive),
    /// +0x544 = the human. The psycho machinery (<see cref="AiReactions"/>) then makes the car chase and ram him.
    /// </summary>
    public static void Chase(CarObj c, CarObj? leadHuman, int gameOptions)
    {
        if ((gameOptions & OptChase) == 0 || (c.Flags & CarFlags.Opponent) == 0 || leadHuman == null) return;
        int d = leadHuman.Progress - c.Progress;
        if (d <= -2 || d >= 5) return;
        c.PsychoTicks = d < 1 ? -d : d;
        c.PsychoTarget = leadHuman;
    }

    /// <summary>the human with the largest progress; a strict [ref:0534]
    /// "greater than" starting from 0, so with progress 0 everywhere the previous value stays.</summary>
    public static CarObj? FindLeadHuman(IReadOnlyList<CarObj> humans, CarObj? previous)
    {
        var lead = previous;
        ushort best = 0;
        foreach (var h in humans)
            if (best < h.Progress) { lead = h; best = h.Progress; }
        return lead;
    }

    /// <summary>
    /// Road-rage horn launch. Runs with option 0x800 and the horn held (+0x2DD). For a local human [ref:0535]
    /// (+0x1F4 &amp; 4) not locked/finished/crashed, after the green light, it picks the nearest active settled car (+0x8D ≠ 0,
    /// +0x14C == 0) that is ahead (dot(forward +0xDC, normalized offset) &gt; 0xCCCC ≈ 0.8) and closer than 40 m (0x280000,
    /// approximate distance) and flings it with the road-rage horn. [ref:0536]
    /// </summary>
    public static CarObj? HornLaunch(CarObj c, IReadOnlyList<CarObj> cars, int gameOptions, bool raceStarted,
        int arcadeMode, int raceTick, NFS.Core.Tables.GameTables tables)
    {
        if ((gameOptions & OptRoadRage) == 0 || c.Horn == 0) return null;
        if (c.ManualResetLock != 0 || c.RaceState == 2 || !raceStarted || (c.Flags & CarFlags.LocalHuman) == 0
            || c.CrashSlice != 0) return null;
        CarObj? best = null;
        int bestDist = 0x280000;
        foreach (var o in cars)
        {
            if (o == c || o.Active == 0 || o.AirTicks != 0) continue;
            var d = new Vec3(o.Pos.X - c.Pos.X, o.Pos.Y - c.Pos.Y, o.Pos.Z - c.Pos.Z);
            FxMath.NormalizeBig(ref d);
            var f = c.Orient.Row2;
            int dot = Fx.Mul(f.X, d.X) + Fx.Mul(f.Y, d.Y) + Fx.Mul(f.Z, d.Z);
            if (dot <= 0xCCCC) continue;
            int dist = DistApprox3(o.Pos, c.Pos);
            if (dist > 0 && dist < bestDist) { best = o; bestDist = dist; }
        }
        if (best != null) CarPost.Launch(best, arcadeMode, raceTick, tables);
        return best;
    }

    /// <summary>3-D distance approximation (max + mid/4 + min/4 in the original's nesting); |x| as <c>x&lt;1 ? -x : x</c>. [ref:0537]</summary>
    public static int DistApprox3(Vec3 p, Vec3 q)
    {
        int a = Abs(p.X - q.X), b = Abs(p.Y - q.Y), c = Abs(p.Z - q.Z);
        int t = b < a ? (b >> 2) + a : (a >> 2) + b;
        return c < t ? t + (c >> 2) : (t >> 2) + c;
    }

    private static int Abs(int x) => x < 1 ? -x : x;
}
