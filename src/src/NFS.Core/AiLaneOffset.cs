namespace NFS.Core;

/// <summary>
/// Random lateral slack of an AI car inside its lane (<c>+0x3A4</c> <see cref="CarObj.LaneRandomOffset"/>, read by
/// the lateral target). Both functions consume the AI random generator (<see cref="AiRandom"/>); [ref:0148]
/// in a race without traffic they are its ONLY consumers ("LCG"). [ref:3365]
/// </summary>
public static class AiLaneOffset
{
    /// <summary>
    /// Lane-offset change check (end of the update step), only when its LOD row 0 [ref:0149]
    /// passes and the car is neither parked (+0x1F4 &amp; 0x40) nor hidden (+0x3A8 &amp; 0x10)).
    /// Checkpoint slice s = slice + 2·index (reverse: slice − 2·index), wrapped once into [0, n).
    /// Humans never draw; opponents draw when s % 16 == 0, traffic when s is even, others always.
    /// Draw 1: <c>((last &amp; 0xFFFF00) &gt;&gt; 8) · prob &gt;&gt; 16 == 1</c> (prob = personality +0xD8 for
    /// opponents, car +0x564 otherwise) → <see cref="Pick"/> draws again. Returns true when a new offset was picked.
    /// </summary>
    public static bool MaybeChange(CarObj c, AiRandom rng, int numSlices, bool reverse,
        ReadOnlySpan<int> personalitySlack, int personalityProbability, ReadOnlySpan<int> trafficOffsets)
    {
        int d = reverse ? -2 * c.PlayerIndex : 2 * c.PlayerIndex;
        int s = c.Trk.Slice + d;
        if (d < 0) { if (s < 0) s += numSlices; }
        else if (s >= numSlices) s -= numSlices;

        if ((c.Flags & CarFlags.LocalHuman) != 0) return false;
        if ((c.Flags & CarFlags.Opponent) != 0 && (s >> 4) * 16 != s) return false;
        if ((c.Flags & CarFlags.Traffic) != 0 && (s >> 1) * 2 != s) return false;

        int prob = (c.Flags & CarFlags.Opponent) != 0 ? personalityProbability : c.OffsetChangeRate;
        uint last = rng.Next();
        if ((int)((((last & 0xFFFF00) >> 8) * (uint)prob) >> 16) != 1) return false;
        Pick(c, rng, personalitySlack, trafficOffsets);
        return true;
    }

    /// <summary>Index k = ((last &amp; 0xFFFF00) &gt;&gt; 8) &amp; 3 of a fresh draw; [ref:0150]
    /// opponent → personality LaneSlack[k] (+0xC8), otherwise → TRAFCFG [8..11][k]. [ref:0151]</summary>
    public static void Pick(CarObj c, AiRandom rng, ReadOnlySpan<int> personalitySlack, ReadOnlySpan<int> trafficOffsets)
    {
        int k = (int)((rng.Next() & 0xFFFF00) >> 8) & 3;
        var table = (c.Flags & CarFlags.Opponent) != 0 ? personalitySlack : trafficOffsets;
        c.LaneRandomOffset = k < table.Length ? table[k] : 0;
    }
}
