namespace NFS.Core;

/// <summary>
/// Rigid-body box vs plane collision: [ref:0516]
/// deepest chassis corner, half-penetration push-out, normal impulse (×0.4) with friction (0.8·j) and the
/// "in place" cross product bug of the original.
/// </summary>
public static class PlaneCollision
{
    /// <summary>1: push-out enabled. [ref:0517]</summary>
    public const bool PushOut = true;

    /// <summary>Clamps each ω component to ±1.8 (0x1CCCC). [ref:0518]</summary>
    public static void ClampAngVel(CarObj c)
    {
        const int L = 0x1CCCC;
        c.AngVel = new(Math.Clamp(c.AngVel.X, -L, L), Math.Clamp(c.AngVel.Y, -L, L), Math.Clamp(c.AngVel.Z, -L, L));
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(Fx.Mul(a.Y, b.Z) - Fx.Mul(a.Z, b.Y), Fx.Mul(a.Z, b.X) - Fx.Mul(a.X, b.Z), Fx.Mul(a.X, b.Y) - Fx.Mul(a.Y, b.X));

    private static Vec3 Scale(int k, Vec3 v) => new(Fx.Mul(k, v.X), Fx.Mul(k, v.Y), Fx.Mul(k, v.Z));

    /// <summary>|v|² with halving against overflow (components &gt; 0x1000000 or the [ref:0519]
    /// quarter sum &gt; 0x40000000), result shifted back by 2 bits per halving.</summary>
    public static int LenSqScaled(Vec3 v)
    {
        int x = v.X, y = v.Y, z = v.Z, k = 0;
        while (Math.Abs(x) > 0x1000000 || Math.Abs(y) > 0x1000000 || Math.Abs(z) > 0x1000000) { x >>= 1; y >>= 1; z >>= 1; k++; }
        uint a, b, c;
        while (true)
        {
            a = (uint)Fx.Mul(x, x); b = (uint)Fx.Mul(y, y); c = (uint)Fx.Mul(z, z);
            if ((c >> 2) + (b >> 2) + (a >> 2) <= 0x40000000) break;
            x >>= 1; y >>= 1; z >>= 1; k++;
        }
        int r = (int)(a + b + c);
        for (; k != 0; k--) r <<= 2;
        return r;
    }

    /// <summary>
    /// Plane collision of the car: ω ×2π first and ×1/2π (0x28BE) + clamp ±1.8 at the end (also without contact). [ref:0520]
    /// A zero n becomes (0,1,0) – written back to the caller's vector like the original.
    /// </summary>
    public static void CollPlane(CarObj c, ref Vec3 n, Vec3 p0)
    {
        c.AngVel = Scale(0x6487E, c.AngVel);
        if (n == Vec3.Zero) n.Y = Fx.One;
        Vec3 R = c.Orient.Row0, U = c.Orient.Row1, F = c.Orient.Row2;
        int ex = Fx.Mul(Vec3.Dot(n, R), c.HalfWidth), ey = Fx.Mul(Vec3.Dot(n, U), c.HalfHeight), ez = Fx.Mul(Vec3.Dot(n, F), c.HalfLength);
        int sx = ex < 0 ? 1 : -1, sy = ey < 0 ? 1 : -1, sz = ez < 0 ? 1 : -1;
        int dist = Vec3.Dot(n, c.Pos - p0) + sx * ex + sy * ey + sz * ez;
        if (dist < 0)
        {
            var C = new Vec3(
                c.Pos.X + sx * Fx.Mul(R.X, c.HalfWidth) + sy * Fx.Mul(U.X, c.HalfHeight) + sz * Fx.Mul(F.X, c.HalfLength),
                c.Pos.Y + sx * Fx.Mul(R.Y, c.HalfWidth) + sy * Fx.Mul(U.Y, c.HalfHeight) + sz * Fx.Mul(F.Y, c.HalfLength),
                c.Pos.Z + sx * Fx.Mul(R.Z, c.HalfWidth) + sy * Fx.Mul(U.Z, c.HalfHeight) + sz * Fx.Mul(F.Z, c.HalfLength));
            int pen = -dist;
            var push = Vec3.Zero;
            if (PushOut && pen != 0)
            {
                int h = pen / 2;
                push = Scale(h, n);
                c.Pos += push;
            }
            var r = C - c.Pos;
            var w = c.AngVel;
            var vp = c.Vel + new Vec3(Fx.Mul(w.Y, r.Z) - Fx.Mul(w.Z, r.Y), Fx.Mul(w.Z, r.X) - Fx.Mul(w.X, r.Z), Fx.Mul(w.X, r.Y) - Fx.Mul(w.Y, r.X));
            if (Vec3.Dot(vp, n) < 0) Impulse(c, C, vp, n);
            if (PushOut && pen != 0 && push.Y > 0)
            {
                int a = 2 * Fx.Mul(0x9CCCC, push.Y), b = Fx.Mul(c.Vel.Y, c.Vel.Y);
                if (a > b) c.Vel.Y = 0;
                else { int s = FxMath.SqrtTab((uint)(b - a)); c.Vel.Y = c.Vel.Y > 0 ? s : -s; }
            }
        }
        c.AngVel = new Vec3(Fx.Mul(0x28BE, c.AngVel.X), Fx.Mul(0x28BE, c.AngVel.Y), Fx.Mul(0x28BE, c.AngVel.Z));
        ClampAngVel(c);
    }

    /// <summary>Normal impulse ×0.4 and friction 0.8·j along −vt/|vt|. [ref:0521]</summary>
    public static void Impulse(CarObj c, Vec3 C, Vec3 vp, Vec3 n)
    {
        int vn = Vec3.Dot(n, c.Vel);
        var r = C - c.Pos;
        var cr = Cross(r, n);
        int wn = Vec3.Dot(c.AngVel, cr);
        int invM2 = c.InvMass / 2, invI2 = 2 * c.InvInertia;
        int den = invM2 / 2 + Fx.Mul(LenSqScaled(cr), invI2) / 2;
        int j = Fx.Mul(Fx.Div(-(vn + wn), den), 0x6666);
        c.Impact = n.Y < 0xB333 ? Math.Abs(vn) * 6 : Math.Abs(vn) * 4;
        c.ImpactOther = null;
        c.ImpactType = c.SurfaceType | 0x30000;
        c.LastHit = C;
        Vec3 t; int L;
        if (vp == Vec3.Zero) { t = Vec3.Zero; L = 0; }
        else
        {
            int d = Vec3.Dot(vp, n);
            var vt = vp - Scale(d, n);
            L = FxMath.SqrtTab((uint)(Fx.Mul(vt.X, vt.X) + Fx.Mul(vt.Y, vt.Y) + Fx.Mul(vt.Z, vt.Z)));
            int h = L / 2;
            t = h != 0 ? Scale(-Fx.Div(0x8000, h), vt) : Scale(-Fx.One, vt);
        }
        int f = Fx.Mul(0xCCCC, j);
        int fm = Fx.Mul(f, invM2);
        var jt = Scale(fm, t);
        var u = Cross(t, r);
        int wx = Fx.Mul(u.Y, r.Z) - Fx.Mul(u.Z, r.Y);
        int wy = Fx.Mul(u.Z, r.X) - Fx.Mul(wx, r.Z);   // aliasing bug of the original: wx instead of u.x
        int wz = Fx.Mul(wx, r.Y) - Fx.Mul(wy, r.X);    // and wx/wy instead of u.x/u.y
        int g = Fx.Mul(f, invI2);
        var dvt = jt + new Vec3(Fx.Mul(g, wx), Fx.Mul(g, wy), Fx.Mul(g, wz));
        int sdt = Vec3.Dot(dvt, t);
        int fe = sdt > L ? Fx.Mul(f, Fx.Div(L, sdt)) : f;
        var fr = Scale(fe, t);
        if (j <= 0) return;
        var J = Scale(j, n) + fr;
        c.Vel += Scale(invM2, J);
        var rj = Cross(r, J);
        c.AngVel += Scale(invI2, rj);
    }
}
