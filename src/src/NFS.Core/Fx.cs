namespace NFS.Core;

/// <summary>
/// 16.16 fixed-point arithmetic exactly as compiled in the original (values are plain <see cref="int"/>, wrapping
/// like 32-bit registers). See "Arithmetic conventions". [ref:0423]
/// <list type="bullet">
/// <item><see cref="Mul"/> – 64-bit product, shifted right by 16, rounded half up via bit 15.</item>
/// <item><see cref="Div"/> – a 64-bit by 32-bit division, truncating toward zero.</item>
/// <item><see cref="DivPow2"/> – C <c>x / 2^n</c> (sign-corrected arithmetic shift), truncation toward zero.</item>
/// <item><see cref="Shr"/> – arithmetic shift right (<c>&gt;&gt;</c>), floor. The original uses both – keep them apart!</item>
/// </list>
/// Angles: 16-bit (0x10000 = 360°) for matrices/sin, 10-bit (0x400 = 360°) for headings (<see cref="FxMath.Atan2"/>).
/// </summary>
public static class Fx
{
    public const int One = 0x10000;
    public const int Half = 0x8000;
    public const int Angle16Full = 0x10000;
    public const int Angle10Full = 0x400;

    /// <summary>FX(a,b) = ((long)a*b + 0x8000) &gt;&gt; 16, low 32 bits.</summary>
    public static int Mul(int a, int b) => (int)(((long)a * b + 0x8000) >> 16);

    /// <summary>FX as unsigned (keeps squares as u32). [ref:0424]</summary>
    public static uint MulU(int a, int b) => (uint)Mul(a, b);

    /// <summary>FXDIV(a,b): <c>q=a/b; r=a%b; (q&lt;&lt;16) + ((long)r&lt;&lt;16)/b</c> (two divisions, both truncating);
    /// equal to <c>(int)(((long)a&lt;&lt;16)/b)</c> whenever the quotient fits.</summary>
    public static int Div(int a, int b)
    {
        int q = a / b, r = a % b;
        return (q << 16) + (int)(((long)r << 16) / b);
    }

    /// <summary>C integer division by 2^n (truncates toward zero): <c>-3/2 = -1</c>.</summary>
    public static int DivPow2(int x, int n) => (x + ((x >> 31) & ((1 << n) - 1))) >> n;

    /// <summary>Arithmetic shift (floor): <c>-3 &gt;&gt; 1 = -2</c>.</summary>
    public static int Shr(int x, int n) => x >> n;

    /// <summary>|x| as compiled (<c>x &lt;= 0 ? -x : x</c>; int.MinValue stays negative).</summary>
    public static int Abs(int x) => x <= 0 ? -x : x;

    /// <summary>Dot product = sum of three separately rounded FX (order x, y, z).</summary>
    public static int Dot(int ax, int ay, int az, int bx, int by, int bz) => Mul(ax, bx) + Mul(ay, by) + Mul(az, bz);

    /// <summary>Converts a real constant from the docs (e.g. 14.7) – truncated, as the compiler stored constants.
    /// For documentation/tests only; the simulation uses exact integers from the original's formulas.</summary>
    public static int FromDouble(double v) => (int)(v * One);

    public static double ToDouble(int v) => v / (double)One;
}
