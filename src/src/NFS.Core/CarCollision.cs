using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// Car–car collisions: [ref:0291]
/// point-in-box contact tests (not SAT), up to 32 separation steps [ref:0292]
/// impulse ×0.55. Flags/F0/F4/F8 = 0, = 1 as in the original. Not ported: [ref:0293]
/// (after 8 re-collisions; its K tables are still to be read from the original), ffb, horn/traffic rule. [ref:0294]
/// </summary>
public static class CarCollision
{
    private static Vec3 Axis(CarObj c, int i) => i == 0 ? c.Orient.Row0 : i == 1 ? c.Orient.Row1 : c.Orient.Row2;
    private static int Half(CarObj c, int i) => i == 0 ? c.HalfWidth : i == 1 ? c.HalfHeight : c.HalfLength;
    private static int Max3Abs(Vec3 v) => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z)));

    private static readonly int[][] Faces = [[1, 0, 0], [0, 1, 0], [0, 0, 1], [-1, 0, 0], [0, -1, 0], [0, 0, -1]];
    private static readonly int[][] Corners =
        [[1, 1, 1], [1, 1, -1], [1, -1, 1], [1, -1, -1], [-1, 1, 1], [-1, 1, -1], [-1, -1, 1], [-1, -1, -1]];

    /// <summary>Per-pair tables of (hb, ha, dA/dB). [ref:0295]</summary>
    private sealed class Pair
    {
        public readonly CarObj A, B;
        public readonly int[,] Hb = new int[3, 3], Ha = new int[3, 3];
        public readonly int[] DA = new int[3], DB = new int[3];

        public Pair(CarObj a, CarObj b)
        {
            A = a; B = b;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    int m = Vec3.Dot(Axis(a, i), Axis(b, j));
                    Hb[i, j] = Fx.Mul(m, Half(b, j));
                    Ha[i, j] = Fx.Mul(m, Half(a, i));
                }
        }
    }

    /// <summary>Tail part: bounding test on the radii (+0x114), ω ×2π [ref:0296]
    /// ω ×1/2π and for both cars. [ref:0297]</summary>
    public static bool Collide(CarObj c, CarObj o, TrackSlice[] slices, ReadOnlySpan<int> st, bool arcade)
    {
        int r = c.Radius + o.Radius;
        if (Math.Abs(c.Pos.Z - o.Pos.Z) >= r || Math.Abs(c.Pos.X - o.Pos.X) >= r || Math.Abs(c.Pos.Y - o.Pos.Y) >= r) return false;
        Spin(c, 0x6487E); Spin(o, 0x6487E);
        bool hit = PairCollide(c, o, slices, st, arcade);
        Spin(c, 0x28BE); Spin(o, 0x28BE);
        PlaneCollision.ClampAngVel(c); PlaneCollision.ClampAngVel(o);
        return hit;
    }

    private static void Spin(CarObj c, int k) => c.AngVel = new Vec3(Fx.Mul(k, c.AngVel.X), Fx.Mul(k, c.AngVel.Y), Fx.Mul(k, c.AngVel.Z));

    /// <summary>Collision test for a pair of cars. [ref:0298]</summary>
    public static bool PairCollide(CarObj a, CarObj b, TrackSlice[] slices, ReadOnlySpan<int> st, bool arcade)
    {
        a.Impact = b.Impact = 0;
        var p = new Pair(a, b);
        if (!Separate(p, out var P, out var N)) return false;
        if (!Impulse(a, b, P, N)) return false;
        WallCollision.RoadEdgeCheck(a, slices, st, arcade);
        WallCollision.RoadEdgeCheck(b, slices, st, arcade);
        int n = 8;
        while (Separate(p, out P, out N) && n >= 1)
        {
            bool r = Impulse(a, b, P, N);
            n--;
            if (!r) return true;
            WallCollision.RoadEdgeCheck(a, slices, st, arcade);
            WallCollision.RoadEdgeCheck(b, slices, st, arcade);
            // n == 0: (A),(B) – not ported [ref:0299]
        }
        return true;
    }

    /// <summary>up to 32 contact tests, each followed by a fixed push of both cars [ref:0300]
    /// (computed on the first contact: along the velocities when they close fast head-on, else along the centre line
    /// by mass). P/N of the last contact found.</summary>
    private static bool Separate(Pair p, out Vec3 P, out Vec3 N)
    {
        CarObj A = p.A, B = p.B;
        bool res = false;
        P = N = Vec3.Zero;
        Vec3 sA = Vec3.Zero, sB = Vec3.Zero;
        for (int it = 0; it < 32; it++)
        {
            var d = B.Pos - A.Pos;
            for (int i = 0; i < 3; i++) { p.DA[i] = Vec3.Dot(Axis(A, i), d); p.DB[i] = Vec3.Dot(Axis(B, i), d); }
            if (!ObbContact(p, out var cp, out var cn)) return res;
            P = cp; N = cn;
            if (!res)
            {
                var dv = A.Vel - B.Vel;
                bool useVel = false;
                if (Max3Abs(dv) > 0x140000)
                {
                    if (Max3Abs(d) < 0x3333) useVel = true;
                    else
                    {
                        var u = dv; FxMath.NormalizeBig(ref u);
                        var w = d; FxMath.Normalize(ref w);
                        if (Vec3.Dot(u, w) > 0xD999) useVel = true;
                    }
                }
                if (useVel)
                {
                    int L = FxMath.LengthBig(dv) / 2;
                    int k = L != 0 ? Fx.Div(0x8000, L) : 0x10000;
                    int s = -k / 256;
                    sA = new Vec3(unchecked(A.Vel.X * s) / 256 >> 5, unchecked(A.Vel.Y * s) / 256 >> 5, unchecked(A.Vel.Z * s) / 256 >> 5);
                    sB = new Vec3(unchecked(B.Vel.X * s) / 256 >> 5, unchecked(B.Vel.Y * s) / 256 >> 5, unchecked(B.Vel.Z * s) / 256 >> 5);
                }
                else
                {
                    var n = d; FxMath.Normalize(ref n);
                    int fa = Fx.Div(A.Mass, A.Mass + B.Mass), fb = Fx.Div(B.Mass, A.Mass + B.Mass) / 4, fa4 = fa / 4;
                    sA = new Vec3(-(unchecked(n.X * fb) / 16384) >> 5, -(unchecked(n.Y * fb) / 16384) >> 5, -(unchecked(n.Z * fb) / 16384) >> 5);
                    sB = new Vec3(unchecked(n.X * fa4) / 16384 >> 5, unchecked(n.Y * fa4) / 16384 >> 5, unchecked(n.Z * fa4) / 16384 >> 5);
                }
                res = true;
            }
            A.Pos += sA;
            B.Pos += sB;
        }
        return res;
    }

    /// <summary>B faces in A, A faces in B, B corners in A, A corners in B (first hit [ref:0301]
    /// wins). N points from B to A (a box axis).</summary>
    private static bool ObbContact(Pair p, out Vec3 P, out Vec3 N)
    {
        CarObj A = p.A, B = p.B;
        var q = new int[3];
        foreach (var s in Faces) if (BInA(p, s, q, out P)) { N = Sum(B, s); return true; }
        foreach (var s in Faces) if (AInB(p, s, q, out P)) { N = -Sum(A, s); return true; }
        foreach (var s in Corners)
        {
            if (!BInA(p, s, q, out P)) continue;
            int pick;
            if (Max3Abs(B.Vel - A.Vel) > 0xF0000)
            {
                var e = new int[3];
                for (int i = 0; i < 3; i++) e[i] = Math.Abs(Vec3.Dot(q[i] > 0 ? -Axis(A, i) : Axis(A, i), B.Vel));
                pick = e[0] > e[1] && e[0] > e[2] ? 0 : e[1] > e[2] ? 1 : 2;
            }
            else
            {
                int px = A.HalfWidth - Math.Abs(q[0]), py = A.HalfHeight - Math.Abs(q[1]), pz = A.HalfLength - Math.Abs(q[2]);
                pick = px < py && px < pz ? 0 : py < pz ? 1 : 2;
            }
            N = q[pick] > 0 ? -Axis(A, pick) : Axis(A, pick);
            return true;
        }
        foreach (var s in Corners)
        {
            if (!AInB(p, s, q, out P)) continue;
            int pick;
            if (Max3Abs(A.Vel - B.Vel) > 0xF0000)
            {
                var e = new int[3];
                for (int j = 0; j < 3; j++) e[j] = Math.Abs(Vec3.Dot(q[j] < 0 ? -Axis(B, j) : Axis(B, j), A.Vel));
                pick = e[0] > e[1] && e[0] > e[2] ? 0 : e[1] > e[2] ? 1 : 2;
            }
            else
            {
                int px = B.HalfWidth - Math.Abs(q[0]), py = B.HalfHeight - Math.Abs(q[1]), pz = B.HalfLength - Math.Abs(q[2]);
                pick = px < py && px < pz ? 0 : py < pz ? 1 : 2;
            }
            N = q[pick] < 0 ? -Axis(B, pick) : Axis(B, pick);
            return true;
        }
        P = N = Vec3.Zero;
        return false;
    }

    private static Vec3 Sum(CarObj c, int[] s) => new(
        s[0] * c.Orient.M00 + s[1] * c.Orient.M10 + s[2] * c.Orient.M20,
        s[0] * c.Orient.M01 + s[1] * c.Orient.M11 + s[2] * c.Orient.M21,
        s[0] * c.Orient.M02 + s[1] * c.Orient.M12 + s[2] * c.Orient.M22);

    private static Vec3 Point(CarObj c, int[] s)
    {
        var r = c.Pos;
        for (int j = 0; j < 3; j++)
        {
            var ax = Axis(c, j); int h = Half(c, j);
            r += new Vec3(s[j] * Fx.Mul(ax.X, h), s[j] * Fx.Mul(ax.Y, h), s[j] * Fx.Mul(ax.Z, h));
        }
        return r;
    }

    /// <summary>q_i = Σ_j s_j·hb[i][j] + dA[i] within ±A.half_i. [ref:0302]</summary>
    private static bool BInA(Pair p, int[] s, int[] q, out Vec3 P)
    {
        P = Vec3.Zero;
        for (int i = 0; i < 3; i++) q[i] = s[0] * p.Hb[i, 0] + s[1] * p.Hb[i, 1] + s[2] * p.Hb[i, 2] + p.DA[i];
        for (int i = 0; i < 3; i++) { int h = Half(p.A, i); if (q[i] < -h || q[i] > h) return false; }
        P = Point(p.B, s);
        return true;
    }

    /// <summary>q_j = Σ_i s_i·ha[i][j] − dB[j] within ±B.half_j. [ref:0303]</summary>
    private static bool AInB(Pair p, int[] s, int[] q, out Vec3 P)
    {
        P = Vec3.Zero;
        for (int j = 0; j < 3; j++) q[j] = s[0] * p.Ha[0, j] + s[1] * p.Ha[1, j] + s[2] * p.Ha[2, j] - p.DB[j];
        for (int j = 0; j < 3; j++) { int h = Half(p.B, j); if (q[j] < -h || q[j] > h) return false; }
        P = Point(p.A, s);
        return true;
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(Fx.Mul(a.Y, b.Z) - Fx.Mul(a.Z, b.Y), Fx.Mul(a.Z, b.X) - Fx.Mul(a.X, b.Z), Fx.Mul(a.X, b.Y) - Fx.Mul(a.Y, b.X));

    private static int SqSum(Vec3 v) => Fx.Mul(v.X, v.X) + Fx.Mul(v.Y, v.Y) + Fx.Mul(v.Z, v.Z);

    /// <summary>j = FXDIV(num, den) (≥ 0 or no response), ×0.55, opposite impulses, impact [ref:0304]
    /// +0x160 = |FX(sev, invM)|, +0x168 = 0x50001, +0x164 = other car, air ticks ++, contact = midpoint.</summary>
    public static bool Impulse(CarObj a, CarObj b, Vec3 P, Vec3 N)
    {
        var rA = P - a.Pos; var rB = P - b.Pos;
        var cA = Cross(rA, N); var cB = Cross(rB, N);
        int num = Vec3.Dot(b.Vel, N) - Vec3.Dot(a.Vel, N) - Vec3.Dot(a.AngVel, cA) + Vec3.Dot(b.AngVel, cB);
        int den = a.InvMass / 2 + b.InvMass / 2 + (Fx.Mul(SqSum(cA), a.InvInertia) >> 1) + (Fx.Mul(SqSum(cB), b.InvInertia) >> 1);
        int j = Fx.Div(num, den);
        int sev = Fx.Mul(Vec3.Dot(a.Vel, N) - Vec3.Dot(b.Vel, N), a.Mass + b.Mass);
        if (j < 0) return false;
        j = Fx.Mul(0x8CCC, j);
        var J = new Vec3(Fx.Mul(j, N.X), Fx.Mul(j, N.Y), Fx.Mul(j, N.Z));
        a.Vel += new Vec3(Fx.Mul(a.InvMass, J.X), Fx.Mul(a.InvMass, J.Y), Fx.Mul(a.InvMass, J.Z));
        a.Impact = Math.Abs(Fx.Mul(sev, a.InvMass)); a.ImpactType = 0x50001; a.ImpactOther = b;
        b.Vel -= new Vec3(Fx.Mul(b.InvMass, J.X), Fx.Mul(b.InvMass, J.Y), Fx.Mul(b.InvMass, J.Z));
        b.Impact = Math.Abs(Fx.Mul(sev, b.InvMass)); b.ImpactType = 0x50001; b.ImpactOther = a;
        var ja = Cross(rA, J); var jb = Cross(rB, J);
        a.AngVel += new Vec3(Fx.Mul(a.InvInertia, ja.X), Fx.Mul(a.InvInertia, ja.Y), Fx.Mul(a.InvInertia, ja.Z));
        b.AngVel -= new Vec3(Fx.Mul(b.InvInertia, jb.X), Fx.Mul(b.InvInertia, jb.Y), Fx.Mul(b.InvInertia, jb.Z));
        a.AirTicks++; b.AirTicks++;
        var mid = new Vec3((a.Pos.X + b.Pos.X) / 2, (a.Pos.Y + b.Pos.Y) / 2, (a.Pos.Z + b.Pos.Z) / 2);
        a.LastHit = mid; b.LastHit = mid;
        return true;
    }
}
