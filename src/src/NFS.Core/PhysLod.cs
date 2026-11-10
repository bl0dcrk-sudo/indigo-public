using NFS.Core.Tables;

namespace NFS.Core;

/// <summary>
/// Level-of-detail of the per-car update (the name was formerly misnamed) [ref:0514]
/// by distance to the nearest player. Row 3 (newton): &lt;64 m → every 32 Hz tick, 64–128 m → 16 Hz (2 ticks),
/// beyond → 8 Hz (4 ticks). "LOD step". [ref:0515]
/// </summary>
public static class PhysLod
{
    public readonly record struct Step(int RateHz, int Ticks, int Dt);

    /// <summary>
    /// <c>b = min((dist/16)/65536, 19)</c> (both truncating), <c>k = rows[row*20 + b]</c>; outputs rate/ticks/dt[k];
    /// returns <c>((raceTick/2 + id*mul) &amp; mask) == mask</c> with mask = 0 while <paramref name="raceTick"/> == 0.
    /// </summary>
    public static bool Timestep(int mul, int row, int dist, int id, int raceTick, out Step step, GameTables? tables = null)
    {
        var t = tables ?? FxMath.Tables;
        int b = Fx.DivPow2(Fx.DivPow2(dist, 4), 16);
        if (b >= 20) b = 19;
        int k = t.LodRows[row * GameTables.LodBands + b];
        step = new Step(t.LodRateHz[k], t.LodTicks[k], t.LodDt[k]);
        int mask = raceTick == 0 ? 0 : t.LodMask[k];
        return ((raceTick / 2 + id * mul) & mask) == mask;
    }
}
