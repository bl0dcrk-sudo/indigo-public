namespace NFS.Render;

/// <summary>
/// Fog level per z-sort bucket, as in the original. The curve is a uniform cubic B-spline basis [ref:2514]
/// (20 samples per segment) through the 4 HRZ points, end points repeated [ref:2515]
/// with linear interpolation between samples (one value per bucket), and the band walk fills [ref:2516]
/// the level of each bucket b = curve value, for b in [x0, x3]. [ref:2517]
/// </summary>
public static class FogCurve
{
    const int Samples = 20;
    static readonly int[] B0 = new int[Samples], B1 = new int[Samples], B2 = new int[Samples], B3 = new int[Samples];

    static int MulRound(int a, int b)
    {
        long p = (long)a * b;
        return (int)((p >> 16) + ((p >> 15) & 1));
    }

    static FogCurve()
    {
        for (int i = 0; i < Samples; i++)
        {
            int t = (int)((long)i * 65536 / 19);
            int t2 = MulRound(t, t), t3 = MulRound(t2, t);
            int b3 = (int)((long)t3 * 65536 / 0x60000);
            B3[i] = b3;                         // [ref:2518]
            B2[i] = ((t2 - t3 + t) >> 1) + 0x2AAA; // [ref:2519]
            B1[i] = ((t3 >> 1) - t2) + 0xAAAA;     // [ref:2520]
            B0[i] = (((t2 - t) >> 1) - b3) + 0x2AAA; // [ref:2521]
        }
    }

    static int Blend(int i, int prev, int cur, int next, int next2) =>
        (int)(((long)B0[i] * prev + (long)B1[i] * cur + (long)B2[i] * next + (long)B3[i] * next2) >> 16);

    /// <summary>for 4 points: sample positions and values (end points exact). [ref:2522]</summary>
    public static (int[] X, int[] Y) Sample(int[] x, int[] y)
    {
        var ox = new List<int> { x[0] };
        var oy = new List<int> { y[0] };
        int last = x[0];
        for (int seg = 0; seg < 3; seg++) // the 3 segments of the 4 points: points seg, seg+1; neighbours clamped [ref:3438]
        {
            int p = Math.Max(seg - 1, 0), n2 = Math.Min(seg + 2, 3);
            for (int i = 0; i < Samples; i++)
            {
                int sx = Blend(i, x[p], x[seg], x[seg + 1], x[n2]);
                if (sx == last) continue;
                ox.Add(sx);
                oy.Add(Blend(i, y[p], y[seg], y[seg + 1], y[n2]));
                last = sx;
            }
        }
        if (last != x[3]) { ox.Add(x[3]); oy.Add(y[3]); }
        return (ox.ToArray(), oy.ToArray());
    }

    /// <summary>level for every bucket x[0]..x[3] (index = bucket − x[0]). [ref:2523]</summary>
    public static int[] Evaluate(int[] x, int[] y)
    {
        var (sx, sy) = Sample(x, y);
        var res = new int[Math.Max(0, x[3] - x[0] + 1)];
        int idx = 0;
        for (int v = x[0]; v <= x[3]; v++)
        {
            while (idx + 1 < sx.Length - 1 && sx[idx + 1] < v) idx++;
            int dx = sx[idx + 1] - sx[idx];
            res[v - x[0]] = dx == 0 ? sy[idx] : (v - sx[idx]) * (sy[idx + 1] - sy[idx]) / dx + sy[idx];
        }
        return res;
    }

    /// <summary>Level for a bucket: the curve inside [x0, x3], the end values outside.</summary>
    public static int Level(int[] curve, int[] x, int[] y, int bucket) =>
        bucket <= x[0] ? y[0] : bucket >= x[3] ? y[3] : curve[bucket - x[0]];
}
