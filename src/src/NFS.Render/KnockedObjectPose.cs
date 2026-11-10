using NFS.Core;
using NFS.Data.Track;

namespace NFS.Render;

/// <summary>
/// Pose of a knocked-over XBID 11 object (type 4 reference): while its CAN animation [ref:2528]
/// runs, the matrix is <c>Ry(−a·(1−p) − h·p) · CAN · Ry(a)</c> [ref:2529]
/// (p = key (nKeys−1), a = hit angle byte, h = object heading byte) and the CAN position [ref:2530]
/// offset is added to the reference position; afterwards (state −2, also when the CAN file or slot is missing) the object
/// lies flat: <c>Ry(−h) · Rx(90°) · Ry(a)</c>. Row-vector convention [ref:2531]
/// (<c>v·M</c>), matrices 16.16 as in the original. [ref:2532]
/// </summary>
public static class KnockedObjectPose
{
    /// <summary>Round-to-nearest 16.16 product: (p &gt;&gt; 16) + bit 15 (the original's rounding pattern). [ref:2533]</summary>
    static int R16(long p) => (int)(p >> 16) + (int)((p >> 15) & 1);

    /// <summary>[c 0 −s; 0 1 0; s 0 c] (angle 0x10000 = 360°). [ref:2534]</summary>
    public static int[] RotY(int a16)
    {
        FxMath.SinCos(a16, out int s, out int c);
        return [c, 0, -s, 0, 0x10000, 0, s, 0, c];
    }

    /// <summary>[1 0 0; 0 c s; 0 −s c]. [ref:2535]</summary>
    public static int[] RotX(int a16)
    {
        FxMath.SinCos(a16, out int s, out int c);
        return [0x10000, 0, 0, 0, c, s, 0, -s, c];
    }

    /// <summary>out = A·B, every element the sum of three rounded 16.16 products. [ref:2536]</summary>
    public static int[] Mul(int[] a, int[] b)
    {
        var o = new int[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                o[i * 3 + j] = R16((long)a[i * 3] * b[j]) + R16((long)a[i * 3 + 1] * b[3 + j]) + R16((long)a[i * 3 + 2] * b[6 + j]);
        return o;
    }

    /// <summary>(s16 quaternion 1.14: x, y, z, w → 16.16 matrix). [ref:2537]</summary>
    public static int[] QuatToMatrix(int s0, int s1, int s2, int s3)
    {
        int y2 = s1 * 2, z2 = s2 * 2, zz = z2 * s2, yy = y2 * s1, xw = s0 * 2 * s3;
        int c = 0x10000000 - s0 * 2 * s0;
        return
        [
            (0x10000000 - yy - zz) >> 12, (s0 * y2 + s3 * z2) >> 12, (s0 * z2 - s3 * y2) >> 12,
            (s0 * y2 - s3 * z2) >> 12, (c - zz) >> 12, (xw + s1 * z2) >> 12,
            (s0 * z2 + s3 * y2) >> 12, (s1 * z2 - xw) >> 12, (c - yy) >> 12,
        ];
    }

    /// <summary>Key interpolation at <paramref name="frac"/> (16.16) → matrix [ref:2538]
    /// position offset (16.16).</summary>
    public static (int[] M, int X, int Y, int Z) Eval(CanKey k0, CanKey k1, int frac)
    {
        int inv = 0x10000 - frac;
        short L(short a, short b) => (short)(R16((long)a * inv) + R16((long)b * frac));
        var m = QuatToMatrix(L(k0.Qx, k1.Qx), L(k0.Qy, k1.Qy), L(k0.Qz, k1.Qz), L(k0.Qw, k1.Qw));
        int P(int a, int b) => R16((long)a * inv) + R16((long)b * frac);
        return (m, P(k0.X, k1.X), P(k0.Y, k1.Y), P(k0.Z, k1.Z));
    }

    /// <summary>byte angle of the edge vertex 0 → vertex 1 of the model (s16 units). [ref:2539]</summary>
    public static sbyte Heading(TrackStructure st)
    {
        var v = st.Vertices;
        if (v.Length < 6) return 0;
        return (sbyte)((FxMath.Atan2_16(v[3] - v[0], v[5] - v[2]) - 0x4000) >> 8);
    }

    /// <summary>Matrix and CAN position offset (16.16) of the knocked object <paramref name="ticksSinceHit"/> ticks after the
    /// hit. <paramref name="can"/> null (no file) or finished animation → lying flat, offset 0.</summary>
    public static (int[] M, int X, int Y, int Z, bool Lying) Pose(Can? can, int ticksSinceHit, sbyte angle, sbyte heading)
    {
        int a = angle << 8, h = heading << 8;
        if (can != null && can.Keys.Count > 1 && ticksSinceHit >= 0)
        {
            int tpk = can.TicksPerKey, seg = can.Keys.Count - 1, k = ticksSinceHit / tpk;
            if (k < seg)
            {
                var (cm, x, y, z) = Eval(can.Keys[k], can.Keys[k + 1], ((ticksSinceHit % tpk) << 16) / tpk);
                int p = (k << 16) / seg;
                int ang1 = R16((long)-a * (0x10000 - p)) + R16((long)-h * p);
                return (Mul(Mul(RotY(ang1), cm), RotY(a)), x, y, z, false);
            }
        }
        return (Mul(Mul(RotY(-h), RotX(0x4000)), RotY(a)), 0, 0, 0, true);
    }
}
