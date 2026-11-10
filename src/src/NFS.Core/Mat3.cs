namespace NFS.Core;

/// <summary>
/// 3×3 16.16 matrix, row-major int[9] like the original. For a car orientation (+0xC4) the rows are
/// right (+0xC4), up (+0xD0), forward (+0xDC).
/// </summary>
public struct Mat3 : IEquatable<Mat3>
{
    public int M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public Mat3(int m00, int m01, int m02, int m10, int m11, int m12, int m20, int m21, int m22)
    {
        M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22;
    }

    /// <summary>Identity matrix constant. [ref:0473]</summary>
    public static readonly Mat3 Identity = new(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, Fx.One);

    public int this[int i]
    {
        readonly get => i switch
        {
            0 => M00, 1 => M01, 2 => M02, 3 => M10, 4 => M11, 5 => M12, 6 => M20, 7 => M21, 8 => M22,
            _ => throw new IndexOutOfRangeException(),
        };
        set
        {
            switch (i)
            {
                case 0: M00 = value; break; case 1: M01 = value; break; case 2: M02 = value; break;
                case 3: M10 = value; break; case 4: M11 = value; break; case 5: M12 = value; break;
                case 6: M20 = value; break; case 7: M21 = value; break; case 8: M22 = value; break;
                default: throw new IndexOutOfRangeException();
            }
        }
    }

    public int this[int r, int c] { readonly get => this[r * 3 + c]; set => this[r * 3 + c] = value; }

    public Vec3 Row0 { readonly get => new(M00, M01, M02); set { M00 = value.X; M01 = value.Y; M02 = value.Z; } }
    public Vec3 Row1 { readonly get => new(M10, M11, M12); set { M10 = value.X; M11 = value.Y; M12 = value.Z; } }
    public Vec3 Row2 { readonly get => new(M20, M21, M22); set { M20 = value.X; M21 = value.Y; M22 = value.Z; } }

    /// <summary>C[i][j] = FX(A[i][0],B[0][j]) + FX(A[i][1],B[1][j]) + FX(A[i][2],B[2][j]). [ref:0474]</summary>
    public static Mat3 Mul(in Mat3 a, in Mat3 b)
    {
        var c = new Mat3();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = Fx.Mul(a[i, 0], b[0, j]) + Fx.Mul(a[i, 1], b[1, j]) + Fx.Mul(a[i, 2], b[2, j]);
        return c;
    }

    /// <summary>Transposed matrix. [ref:0475]</summary>
    public readonly Mat3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    /// <summary>Element-wise sum. [ref:0476]</summary>
    public static Mat3 Add(in Mat3 a, in Mat3 b)
    {
        var c = new Mat3();
        for (int i = 0; i < 9; i++) c[i] = a[i] + b[i];
        return c;
    }

    /// <summary>Element-wise difference. [ref:0477]</summary>
    public static Mat3 Sub(in Mat3 a, in Mat3 b)
    {
        var c = new Mat3();
        for (int i = 0; i < 9; i++) c[i] = a[i] - b[i];
        return c;
    }

    /// <summary>FX(A[i], k) per element. [ref:0478]</summary>
    public static Mat3 Scale(in Mat3 a, int k)
    {
        var c = new Mat3();
        for (int i = 0; i < 9; i++) c[i] = Fx.Mul(a[i], k);
        return c;
    }

    /// <summary>Row vector times matrix: v·M (sum of rounded FX per column).</summary>
    public static Vec3 MulRow(Vec3 v, in Mat3 m) => new(
        Fx.Mul(v.X, m.M00) + Fx.Mul(v.Y, m.M10) + Fx.Mul(v.Z, m.M20),
        Fx.Mul(v.X, m.M01) + Fx.Mul(v.Y, m.M11) + Fx.Mul(v.Z, m.M21),
        Fx.Mul(v.X, m.M02) + Fx.Mul(v.Y, m.M12) + Fx.Mul(v.Z, m.M22));

    /// <summary>Matrix times column vector: M·v (dot of each row with v).</summary>
    public static Vec3 MulCol(in Mat3 m, Vec3 v) => new(Vec3.Dot(m.Row0, v), Vec3.Dot(m.Row1, v), Vec3.Dot(m.Row2, v));

    public readonly bool Equals(Mat3 o)
    {
        for (int i = 0; i < 9; i++) if (this[i] != o[i]) return false;
        return true;
    }

    public override readonly bool Equals(object? obj) => obj is Mat3 m && Equals(m);
    public override readonly int GetHashCode() => HashCode.Combine(M00, M01, M02, M10, M11, M12, M20, HashCode.Combine(M21, M22));
    public static bool operator ==(Mat3 a, Mat3 b) => a.Equals(b);
    public static bool operator !=(Mat3 a, Mat3 b) => !a.Equals(b);
    public override readonly string ToString() => $"[{M00:X} {M01:X} {M02:X}; {M10:X} {M11:X} {M12:X}; {M20:X} {M21:X} {M22:X}]";
}
