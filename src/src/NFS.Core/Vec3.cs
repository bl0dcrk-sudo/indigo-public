namespace NFS.Core;

/// <summary>int[3] 16.16 vector (layout x, y, z as in carObj). Arithmetic wraps like the original.</summary>
public struct Vec3 : IEquatable<Vec3>
{
    public int X, Y, Z;

    public Vec3(int x, int y, int z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = default;

    public int this[int i]
    {
        readonly get => i switch { 0 => X, 1 => Y, 2 => Z, _ => throw new IndexOutOfRangeException() };
        set { switch (i) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; default: throw new IndexOutOfRangeException(); } }
    }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    /// <summary>Per-component FX(v, k).</summary>
    public static Vec3 Scale(Vec3 v, int k) => new(Fx.Mul(v.X, k), Fx.Mul(v.Y, k), Fx.Mul(v.Z, k));

    /// <summary>Sum of three rounded FX, order x, y, z.</summary>
    public static int Dot(Vec3 a, Vec3 b) => Fx.Mul(a.X, b.X) + Fx.Mul(a.Y, b.Y) + Fx.Mul(a.Z, b.Z);

    /// <summary>a × b (FX(ay,bz)−FX(az,by), FX(az,bx)−FX(ax,bz), FX(ax,by)−FX(ay,bx)). [ref:0686]
    /// Note: the original has a bug in its in-place variant; reproduce it at that call site, not here. [ref:0687]</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        Fx.Mul(a.Y, b.Z) - Fx.Mul(a.Z, b.Y),
        Fx.Mul(a.Z, b.X) - Fx.Mul(a.X, b.Z),
        Fx.Mul(a.X, b.Y) - Fx.Mul(a.Y, b.X));

    public readonly bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override readonly bool Equals(object? obj) => obj is Vec3 v && Equals(v);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);
    public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
    public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);
    public override readonly string ToString() => $"({X:X}, {Y:X}, {Z:X})";
}
