using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NFS.Render;

/// <summary>
/// MMPX 2× pixel-art magnification (Morgan McGuire, Mara Gagiu: "MMPX Style-Preserving Pixel Art Magnification", JCGT 2021) of the 2D layer
/// in Enhanced (task 08j): each source pixel becomes 2×2 pixels copied from itself or a neighbour, so the one-bit menu fonts get rounded corners
/// and diagonals instead of k×k blocks, without new colours (no blur). A transparent pixel counts as the brightest one, so opaque glyphs grow into it.
/// No allocation; <paramref name="premultiply"/> stores colour × alpha (the layer over a 3D scene is drawn with premultiplied alpha and a linear filter).
/// </summary>
public static class PixelArtScaler
{
    /// <summary><paramref name="dst"/> must be 2·<paramref name="src"/> in both directions.</summary>
    public static void Mmpx2x(FrameBuffer src, FrameBuffer dst, bool premultiply = false)
    {
        if (dst.Width != src.Width * 2 || dst.Height != src.Height * 2) throw new ArgumentException("destination must be twice the source size");
        var s = MemoryMarshal.Cast<byte, uint>(src.Rgba.AsSpan());
        var d = MemoryMarshal.Cast<byte, uint>(dst.Rgba.AsSpan());
        Rows(s, d, src.Width, src.Height, 0, src.Height, premultiply);
    }

    static void Rows(ReadOnlySpan<uint> s, Span<uint> d, int w, int h, int y0, int y1, bool premultiply)
    {
        int dw = w * 2;
        for (int y = y0; y < y1; y++)
        {
            int ym1 = Math.Max(y - 1, 0) * w, yc = y * w, yp1 = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int xm1 = Math.Max(x - 1, 0), xp1 = Math.Min(x + 1, w - 1);
                uint A = s[ym1 + xm1], B = s[ym1 + x], C = s[ym1 + xp1];
                uint D = s[yc + xm1], E = s[yc + x], F = s[yc + xp1];
                uint G = s[yp1 + xm1], H = s[yp1 + x], I = s[yp1 + xp1];
                uint J = E, K = E, L = E, M = E;
                if (((A ^ E) | (B ^ E) | (C ^ E) | (D ^ E) | (F ^ E) | (G ^ E) | (H ^ E) | (I ^ E)) != 0)
                {
                    uint P = At(s, w, h, x, y - 2), S = At(s, w, h, x, y + 2), Q = At(s, w, h, x - 2, y), R = At(s, w, h, x + 2, y);
                    long Bl = Luma(B), Dl = Luma(D), El = Luma(E), Fl = Luma(F), Hl = Luma(H);

                    // 1:1 slope rules
                    if (D == B && D != H && D != F && (El >= Dl || E == A) && Any3(E, A, C, G) && (El < Dl || A != D || E != P || E != Q)) J = D;
                    if (B == F && B != D && B != H && (El >= Bl || E == C) && Any3(E, A, C, I) && (El < Bl || C != B || E != P || E != R)) K = B;
                    if (H == D && H != F && H != B && (El >= Hl || E == G) && Any3(E, A, G, I) && (El < Hl || G != H || E != S || E != Q)) L = H;
                    if (F == H && F != B && F != D && (El >= Fl || E == I) && Any3(E, C, G, I) && (El < Fl || I != H || E != R || E != S)) M = F;

                    // intersection rules
                    if (E != F && All5(E, C, I, D, Q) && All3(F, B, H) && F != At(s, w, h, x + 3, y)) K = M = F;
                    if (E != D && All5(E, A, G, F, R) && All3(D, B, H) && D != At(s, w, h, x - 3, y)) J = L = D;
                    if (E != H && All5(E, G, I, B, P) && All3(H, D, F) && H != At(s, w, h, x, y + 3)) L = M = H;
                    if (E != B && All5(E, A, C, H, S) && All3(B, D, F) && B != At(s, w, h, x, y - 3)) J = K = B;
                    if (Bl < El && All5(E, G, H, I, S) && !Any4(E, A, D, C, F)) J = K = B;
                    if (Hl < El && All5(E, A, B, C, P) && !Any4(E, G, D, I, F)) L = M = H;
                    if (Fl < El && All5(E, A, D, G, Q) && !Any4(E, C, B, I, H)) K = M = F;
                    if (Dl < El && All5(E, C, F, I, R) && !Any4(E, A, B, G, H)) J = L = D;

                    // 2:1 slope rules
                    if (H != B)
                    {
                        if (H != A && H != E && H != C)
                        {
                            if (All4(H, G, F, R) && H != D && H != At(s, w, h, x + 2, y - 1)) L = M;
                            if (All4(H, I, D, Q) && H != F && H != At(s, w, h, x - 2, y - 1)) M = L;
                        }
                        if (B != I && B != G && B != E)
                        {
                            if (All4(B, A, F, R) && B != D && B != At(s, w, h, x + 2, y + 1)) J = K;
                            if (All4(B, C, D, Q) && B != F && B != At(s, w, h, x - 2, y + 1)) K = J;
                        }
                    }
                    if (F != D)
                    {
                        if (D != I && D != E && D != C)
                        {
                            if (All4(D, A, H, S) && D != B && D != At(s, w, h, x + 1, y + 2)) J = L;
                            if (All4(D, G, B, P) && D != H && D != At(s, w, h, x + 1, y - 2)) L = J;
                        }
                        if (F != E && F != A && F != G)
                        {
                            if (All4(F, C, H, S) && F != B && F != At(s, w, h, x - 1, y + 2)) K = M;
                            if (All4(F, I, B, P) && F != H && F != At(s, w, h, x - 1, y - 2)) M = K;
                        }
                    }
                }
                int o = y * 2 * dw + x * 2;
                if (premultiply) { J = Premultiply(J); K = Premultiply(K); L = Premultiply(L); M = Premultiply(M); }
                d[o] = J; d[o + 1] = K; d[o + dw] = L; d[o + dw + 1] = M;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint At(ReadOnlySpan<uint> s, int w, int h, int x, int y) => s[Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)];

    /// <summary>Brightness for the "darker grows" rules: r+g+b+1, scaled up as the pixel gets more transparent (alpha 0 = the brightest).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long Luma(uint c) => ((c & 0xFF) + (c >> 8 & 0xFF) + (c >> 16 & 0xFF) + 1) * (256 - (c >> 24));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Any3(uint e, uint a, uint b, uint c) => e == a || e == b || e == c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Any4(uint e, uint a, uint b, uint c, uint d) => e == a || e == b || e == c || e == d;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool All3(uint a, uint b, uint c) => a == b && b == c;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool All4(uint a, uint b, uint c, uint d) => a == b && b == c && c == d;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool All5(uint a, uint b, uint c, uint d, uint e) => a == b && b == c && c == d && d == e;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Premultiply(uint c)
    {
        uint a = c >> 24;
        if (a == 255) return c;
        if (a == 0) return 0;
        uint r = ((c & 0xFF) * a + 127) / 255, g = ((c >> 8 & 0xFF) * a + 127) / 255, b = ((c >> 16 & 0xFF) * a + 127) / 255;
        return r | g << 8 | b << 16 | a << 24;
    }
}
