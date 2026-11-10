using System.Numerics;
using NFS.Core.Tables;

namespace NFS.Core;

/// <summary>
/// fxmath of the original (EA math library + helpers), bit-exact. Table-driven functions use the original's tables [ref:0425]
/// <see cref="Tables"/> (defaults to <see cref="GameTables.Default"/>, i.e. the constants of <c>NFS.Core.Constants</c>).
/// Sources: "fxmath", "Arithmetic conventions", original assembly [ref:0426]
/// </summary>
public static class FxMath
{
    private static GameTables? _tables;

    /// <summary>Tables used by sin/atan/sqrt/orthonormalize. Set explicitly or taken from <see cref="GameTables.Default"/>.</summary>
    public static GameTables Tables
    {
        get => _tables ?? GameTables.Default;
        set => _tables = value;
    }

    // ---------------------------------------------------------------- sin / cos

    /// <summary>10-bit angle index (0x400 = 360°), exact table values, no interpolation. [ref:0427]</summary>
    public static void SinCosTab(int a10, out int sin, out int cos)
    {
        var t = Tables.SinQuarter;
        int q = (a10 >> 8) & 3, j = a10 & 0xFF;
        switch (q)
        {
            case 0: sin = t[j]; cos = t[256 - j]; break;
            case 1: sin = t[256 - j]; cos = -t[j]; break;
            case 2: sin = -t[j]; cos = -t[256 - j]; break;
            default: sin = -t[256 - j]; cos = t[j]; break;
        }
    }

    /// <summary>16-bit angle (0x10000 = 360°), table at a&gt;&gt;6 plus first-order interpolation: [ref:0428]
    /// <c>f=((a&amp;63)*0x6487E)&gt;&gt;9; sin = s + ((c&gt;&gt;2)*f &gt;&gt; 21); cos = c − ((s&gt;&gt;2)*f &gt;&gt; 21)</c> (32-bit).</summary>
    public static void SinCos(int a16, out int sin, out int cos)
    {
        SinCosTab(a16 >> 6, out int s, out int c);
        int f = ((a16 & 63) * 0x6487E) >> 9;
        sin = s + (((c >> 2) * f) >> 21);
        cos = c - (((s >> 2) * f) >> 21);
    }

    public static int Sin(int a16) { SinCos(a16, out int s, out _); return s; }
    public static int Cos(int a16) { SinCos(a16, out _, out int c); return c; }

    // ---------------------------------------------------------------- atan2

    /// <summary>
    /// heading of (x, z) measured from +z toward +x, 10-bit (0x400 = 360°), signed [ref:0429]
    /// −0x200..+0x200. (0,0) → 0x80; |x| == |z| → 0x80 through the jump table (e.g. (−1,1) → −0x80, (1,−1) → 0x180).
    /// Callers usually mask with 0x3FF themselves.
    /// </summary>
    public static int Atan2(int x, int z)
    {
        int fl = 0;
        int b = z, a = x;
        if (b < 0) { fl |= 8; b = -b; }
        if (a < 0) { fl |= 0x10; a = -a; }
        int t;
        if (a == b) t = 0x80;
        else
        {
            if (a > b) { (a, b) = (b, a); fl |= 4; }        // a = smaller (signed comparison, as in the original)
            uint q = (uint)(((ulong)(uint)a << 32) / (uint)b);  // 64-bit by 32-bit division
            t = Tables.Atan8[(int)((q >> 24) + ((q >> 23) & 1))]; // top 8 bits, rounded via bit 23
        }
        return fl switch                                  // jump table [ref:0430]
        {
            0x00 => t,
            0x04 => 0x100 - t,
            0x08 => 0x200 - t,
            0x0C => 0x100 + t,
            0x10 => -t,
            0x14 => t - 0x100,
            0x18 => t - 0x200,
            _ => -t - 0x100,                              // 0x1C
        };
    }

    /// <summary>
    /// angle of (x, y) from +y toward +x, 16-bit (0x10000 = 360°), signed −0x8000..+0x8000. [ref:0431]
    /// (0,0) and |x| == |y| → 0x2000 through the switch. Linear interpolation in the 257-entry table.
    /// </summary>
    public static int Atan2_16(int x, int y)
    {
        int fl = 0;
        if (y < 0) { fl = 2; y = -y; }
        if (x < 0) { fl |= 4; x = -x; }
        int t;
        if (x == y) t = 0x2000;
        else
        {
            if (y < x) { (x, y) = (y, x); fl |= 1; }
            uint q = (uint)(((ulong)(uint)x << 32) / (uint)y);  // __U8LS + u64 div [ref:0432]
            var tab = Tables.Atan16;
            int i = (int)(q >> 24);
            uint f = (q >> 8) & 0xFFFF;
            t = tab[i] + (int)((uint)((tab[i + 1] - tab[i]) * (int)f) >> 16);   // 32-bit product, shift right
        }
        return fl switch                                  // jump table [ref:0433]
        {
            0 => t,
            1 => 0x4000 - t,
            2 => 0x8000 - t,
            3 => 0x4000 + t,
            4 => -t,
            5 => t - 0x4000,
            6 => t - 0x8000,
            _ => -t - 0x4000,
        };
    }

    /// <summary>a − b for 10-bit headings wrapped to ±0x200 (only when signs differ). [ref:0434]</summary>
    public static int AngleDiff(int a, int b)
    {
        int d = a - b;
        if (a < 0)
        {
            if (b >= 0)
            {
                d = b - a;
                return d < 0x201 ? -d : 0x400 - d;
            }
        }
        else if (b < 0 && d > 0x200) return d - 0x400;
        return d;
    }

    // ---------------------------------------------------------------- sqrt / lengths

    /// <summary>Piecewise sqrt used by all vector lengths: <c>i = highest set bit of x; (u32)((u64)x*A[i] &gt;&gt; 16) + B[i]</c>
    /// (A, B); x == 0 → 0. Input is the u32 sum of FX squares, result 16.16. [ref:0435]</summary>
    public static int SqrtTab(uint x)
    {
        if (x == 0) return 0;                             // no bit set: result 0
        int i = BitOperations.Log2(x);
        var t = Tables;
        return (int)((uint)(((ulong)x * t.SqrtMul[i]) >> 16) + t.SqrtAdd[i]);
    }

    /// <summary>Squared length as the original: FX(x,x)+FX(y,y)+FX(z,z) (wrapping).</summary>
    public static int LengthSq(Vec3 v) => Fx.Mul(v.X, v.X) + Fx.Mul(v.Y, v.Y) + Fx.Mul(v.Z, v.Z);

    /// <summary>Length via <see cref="SqrtTab"/>, the inner part of the original's length function. [ref:0436]</summary>
    public static int Length(Vec3 v) => SqrtTab((uint)LengthSq(v));

    /// <summary>Normalizes in place: multiplies by FXDIV(1.0, len); len == 0 → unchanged. [ref:0437]</summary>
    public static void Normalize(ref Vec3 v)
    {
        int len = Length(v);
        if (len == 0) return;
        int inv = Fx.Div(Fx.One, len);
        v = Vec3.Scale(v, inv);
    }

    public static Vec3 Normalized(Vec3 v) { Normalize(ref v); return v; }

    /// <summary>Shared pre-scaling for <see cref="NormalizeBig"/> and <see cref="LengthBig"/>: halves (arithmetic shift) all components while any |c| &gt; 0x1000000 [ref:0438]
    /// then while (xx&gt;&gt;2)+(yy&gt;&gt;2)+(zz&gt;&gt;2) &gt; 0x40000000 (u32). Returns the u32 l2 and the halving count.</summary>
    private static uint ScaleBig(ref int x, ref int y, ref int z, out int halvings)
    {
        halvings = 0;
        while (Fx.Abs(x) > 0x1000000 || Fx.Abs(y) > 0x1000000 || Fx.Abs(z) > 0x1000000)
        {
            x >>= 1; y >>= 1; z >>= 1; halvings++;
        }
        while (true)
        {
            uint xx = Fx.MulU(x, x), yy = Fx.MulU(y, y), zz = Fx.MulU(z, z);
            if ((xx >> 2) + (yy >> 2) + (zz >> 2) <= 0x40000000u) return xx + yy + zz;
            x >>= 1; y >>= 1; z >>= 1; halvings++;
        }
    }

    /// <summary>Normalizes in place, overflow-safe: divides the (pre-scaled) components by [ref:0439]
    /// the length with FXDIV. len == 0 → unchanged.</summary>
    public static void NormalizeBig(ref Vec3 v)
    {
        int x = v.X, y = v.Y, z = v.Z;
        uint l2 = ScaleBig(ref x, ref y, ref z, out _);
        int len = SqrtTab(l2);
        if (len == 0) return;
        v = new Vec3(Fx.Div(x, len), Fx.Div(y, len), Fx.Div(z, len));
    }

    /// <summary>length of the pre-scaled vector doubled back once per halving. [ref:0440]</summary>
    public static int LengthBig(Vec3 v)
    {
        int x = v.X, y = v.Y, z = v.Z;
        uint l2 = ScaleBig(ref x, ref y, ref z, out int k);
        int len = SqrtTab(l2);
        for (; k > 0; k--) len += len;
        return len;
    }

    /// <summary>shift-sum approximation of sqrt(a²+b²) for a, b ≥ 0. [ref:0441]</summary>
    public static int Hypot2(int a, int b)
    {
        int lo = a, hi = b;
        if (b < a) { lo = b; hi = a; }
        if (lo < hi >> 2)
            return hi + (lo >> 4) + (lo >> 6) + (lo >> 7) + (lo >> 9) + (lo >> 14) + (lo >> 15) + (lo >> 16);
        if (lo < hi >> 1)
            return hi + (lo >> 3) + (lo >> 5) + (lo >> 6) + (lo >> 7) + (lo >> 8) + (lo >> 9) + (lo >> 12) + (lo >> 13)
                   + (lo >> 14) + (lo >> 16);
        if (lo >= (hi >> 1) + (hi >> 2))
            return hi + (lo >> 2) + (lo >> 4) + (lo >> 5) + (lo >> 6) + (lo >> 7) + (lo >> 8) + (lo >> 9) + (lo >> 11)
                   + (lo >> 12) + (lo >> 13) + (lo >> 14) + (lo >> 15);
        return hi + (lo >> 2) + (lo >> 5) + (lo >> 8) + (lo >> 10) + (lo >> 11) + (lo >> 12) + (lo >> 13);
    }

    /// <summary>max(|dx|,|dz|) + min/4 in the XZ plane. [ref:0442]</summary>
    public static int DistXzApprox(Vec3 p, Vec3 q)
    {
        int dx = Fx.Abs(p.X - q.X), dz = Fx.Abs(p.Z - q.Z);
        return dz < dx ? dx + (dz >> 2) : dz + (dx >> 2);
    }

    /// <summary>|v| ≈ max + min/4 of |vx|, |vz| (+0xBC, AI +0x3A0); |·| as <c>x&lt;=0 ? -x : x</c>. [ref:0443]</summary>
    public static int SpeedApproxXz(Vec3 v)
    {
        int ax = Fx.Abs(v.X), az = Fx.Abs(v.Z);
        return ax > az ? ax + (az >> 2) : az + (ax >> 2);
    }

    // ---------------------------------------------------------------- matrices

    /// <summary>[1,0,0; 0,c,s; 0,−s,c]. [ref:0444]</summary>
    public static Mat3 RotX(int a16)
    {
        SinCos(a16, out int s, out int c);
        return new Mat3(Fx.One, 0, 0, 0, c, s, 0, -s, c);
    }

    /// <summary>[c,0,−s; 0,1,0; s,0,c]. [ref:0445]</summary>
    public static Mat3 RotY(int a16)
    {
        SinCos(a16, out int s, out int c);
        return new Mat3(c, 0, -s, 0, Fx.One, 0, s, 0, c);
    }

    /// <summary>[c,s,0; −s,c,0; 0,0,1]. [ref:0446]</summary>
    public static Mat3 RotZ(int a16)
    {
        SinCos(a16, out int s, out int c);
        return new Mat3(c, s, 0, -s, c, 0, 0, 0, Fx.One);
    }

    /// <summary>
    /// Orthonormalizes in place: 4 iterations of [ref:0447]
    /// <c>E = MᵀM − I; A = I; S = I; for c in {−1/8, 3/32, −5/64}: A = A·E; S += FX(A, c); M = M·S</c>.
    /// Coefficients from the series; the original continues it, but only 3 terms are used. [ref:0448]
    /// </summary>
    public static void Orthonormalize(ref Mat3 m)
    {
        var coeffs = Tables.OrthoCoeffs;
        for (int it = 0; it < 4; it++)
        {
            var e = Mat3.Sub(Mat3.Mul(m.Transpose(), m), Mat3.Identity);
            var a = Mat3.Identity;
            var s = Mat3.Identity;
            for (int k = 0; k < 3; k++)
            {
                a = Mat3.Mul(a, e);
                s = Mat3.Add(s, Mat3.Scale(a, coeffs[k]));
            }
            m = Mat3.Mul(m, s);
        }
    }

    /// <summary>
    /// small rotation by per-step angles (16-bit) about X, Y, Z. [ref:0449]
    /// <c>accum += |ax|+|ay|+|az|; reortho = accum &gt; threshold; out = [RotX(ax)·]RotY(ay)[·RotZ(az)]</c>
    /// with RotX only when |ax| ≥ 0xE and RotZ only when |az| &gt; 0xD. Returns "changed" (|ay| &gt; 0x13 or X/Z
    /// applied); <paramref name="result"/> is always written (callers ignore it when false).
    /// </summary>
    public static bool RotateMatrix(out Mat3 result, int ax, int ay, int az, out bool reortho, int threshold, ref int accum)
    {
        int aax = ax < 1 ? -ax : ax, aay = ay < 1 ? -ay : ay, aaz = az < 1 ? -az : az;
        accum += aax + aay + aaz;
        reortho = threshold < accum;
        var y = RotY(ay);
        bool changed = aay > 0x13;
        Mat3 t;
        if (aax < 0xE) t = y;
        else { t = Mat3.Mul(RotX(ax), y); changed = true; }
        if (aaz > 0xD)
        {
            result = Mat3.Mul(t, RotZ(az));
            return true;
        }
        result = t;
        return changed;
    }
}
