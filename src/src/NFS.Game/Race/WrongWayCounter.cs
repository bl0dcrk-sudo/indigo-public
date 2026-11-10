using NFS.Core;
using NFS.Data.Track;

namespace NFS.Game.Race;

/// <summary>
/// Wrong-way counter <c>car + 0x288</c> (<see cref="CarObj.WrongWayTicks"/>; the same dword the physics reads: steer assist,
/// tunnel test, camera), written by the simulation: raised once per tick, [ref:2406]
/// for the human cars only, and turned into the centre-band state [ref:2407]
/// by the band mapping: 0 → hud5 (normal), 1 → hud6 (yellow hazard band with [ref:2408]
/// "WRONG WAY!"), 2 → hud7 (solid yellow). While the state is 1 the time/lap/place digits and the LAP/POS labels are
/// hidden, and the digit font switches to the yellow one. [ref:2409]
/// The counter lives in the car; <see cref="RaceRunner"/> calls <see cref="Advance"/> for each car. [ref:2410]
/// </summary>
public static class WrongWayCounter
{
    /// <summary>band 1 from this count, band 2 from <see cref="YellowFrom"/>. [ref:2411]</summary>
    public const int HazardFrom = 0x40, YellowFrom = 0x94;

    /// <summary>0 = hud5, 1 = hud6 + hidden digits/labels, 2 = hud7 + yellow digits.</summary>
    public static int Band(int ticks) => ticks < HazardFrom ? 0 : ticks < YellowFrom ? 1 : 2;

    /// <summary>Called for one car. The dot product is the fixed-point multiply of the car's forward [ref:2412]
    /// (orient row +0xDC) with the slice direction byte (+0xF), compared to 0 the other way round on reversed
    /// tracks; a finished car (+0x280 == 2) clears the counter, an airborne (+0x14C) [ref:2413]
    /// crashing (+0x58C) car keeps it, and facing forward again clears it.</summary>
    public static void Advance(CarObj c, TrackSlice[]? slices, bool trackReverse)
    {
        if (c.RaceState == 2) { c.WrongWayTicks = 0; return; }
        if (slices == null || (uint)c.Trk.Slice >= (uint)slices.Length) { c.WrongWayTicks = 0; return; }
        var fwd = slices[c.Trk.Slice].Forward;
        var f = c.Forward;
        // Three multiplies, each rounded back to 16.16. [ref:2414]
        int dot = Fx.Mul(f.X, fwd[0]) + Fx.Mul(f.Y, fwd[1]) + Fx.Mul(f.Z, fwd[2]);
        bool against = trackReverse ? dot > 0 : dot < 0;
        if (!against) { c.WrongWayTicks = 0; return; }
        if (c.AirTicks != 0 || c.Crashing != 0) return;
        c.WrongWayTicks++;
    }
}
