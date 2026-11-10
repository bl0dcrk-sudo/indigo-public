namespace NFS.Audio;

/// <summary>EA-XA ADPCM (PT codec 7). Stereo: 30 B → 28 frames; [ref:0005]
/// mono: 15 B → 28 samples. Port of the Python reference decoder. [ref:0006]</summary>
public static class EaXaDecoder
{
    public const int SamplesPerBlock = 28;
    public const int StereoBlockBytes = 30;
    public const int MonoBlockBytes = 15;

    // Small constant tables (4 entries each), as in the original:
    static readonly int[] C1 = [0, 240, 460, 392];     // predictor coefficient 1 [ref:0007]
    static readonly int[] C2 = [0, 0, -208, -220];     // predictor coefficient 2 [ref:0008]

    static short Clip(int v) => (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);

    /// <summary>One sample: clamp16(((nibble&lt;&lt;28) &gt;&gt; shift) + C1*h1 + C2*h2 + 0x80 &gt;&gt; 8).</summary>
    static short Step(int nibble, int shift, int k, int h1, int h2) =>
        Clip(((nibble << 28) >> shift) + C1[k] * h1 + C2[k] * h2 + 0x80 >> 8);

    /// <summary>Encoded size in bytes.</summary>
    public static int StereoBytes(int frames) => (frames + 27) / 28 * StereoBlockBytes;
    public static int MonoBytes(int samples) => (samples + 27) / 28 * MonoBlockBytes;

    /// <summary>Decodes <paramref name="frames"/> stereo frames into interleaved L,R. [ref:0009]
    /// <paramref name="hist"/> = {L_old, L_new, R_old, R_new} (voice state +4,+6,+8,+A), updated in place.</summary>
    public static void DecodeStereo(ReadOnlySpan<byte> data, int frames, Span<short> hist, Span<short> output)
    {
        int lo = hist[0], ln = hist[1], ro = hist[2], rn = hist[3];
        int p = 0, o = 0;
        while (frames > 0)
        {
            int h0 = data[p], h1 = data[p + 1];
            p += 2;
            int kl = h0 >> 4, kr = h0 & 15, sl = (h1 >> 4) + 8, sr = (h1 & 15) + 8;
            for (int i = 0; i < SamplesPerBlock; i++)
            {
                int b = data[p + i];
                short l = Step(b >> 4, sl, kl, ln, lo);
                short r = Step(b & 15, sr, kr, rn, ro);
                lo = ln; ln = l; ro = rn; rn = r;
                if (i < frames) { output[o++] = l; output[o++] = r; }
            }
            p += SamplesPerBlock;
            frames -= SamplesPerBlock;
        }
        hist[0] = (short)lo; hist[1] = (short)ln; hist[2] = (short)ro; hist[3] = (short)rn;
    }

    /// <summary>Decodes one mono block (the original's MMX routine). Header byte: hi nibble = coef index, lo nibble = shift-8; then 14 B, high nibble first. [ref:0010]
    /// <paramref name="hist"/> = {old, new}, updated in place.</summary>
    public static void DecodeMono(ReadOnlySpan<byte> data, int samples, Span<short> hist, Span<short> output)
    {
        int old = hist[0], cur = hist[1];
        int p = 0, o = 0;
        while (samples > 0)
        {
            int h = data[p++];
            int k = h >> 4, s = (h & 15) + 8;
            for (int i = 0; i < SamplesPerBlock; i++)
            {
                int b = data[p + i / 2];
                int n = (i & 1) == 0 ? b >> 4 : b & 15;
                short v = Step(n, s, k, cur, old);   // the original's MMX table may differ by ±1 LSB [hypothesis]
                old = cur; cur = v;
                if (i < samples) output[o++] = v;
            }
            p += 14;
            samples -= SamplesPerBlock;
        }
        hist[0] = (short)old; hist[1] = (short)cur;
    }

    public static short[] DecodeStereo(ReadOnlySpan<byte> data, int frames, Span<short> hist)
    {
        var o = new short[frames * 2];
        DecodeStereo(data, frames, hist, o);
        return o;
    }

    /// <summary>BNK mono sample: blocks start at the data offset, history 0.</summary>
    public static short[] DecodeMono(ReadOnlySpan<byte> data, int samples)
    {
        var o = new short[samples];
        DecodeMono(data, samples, stackalloc short[2], o);
        return o;
    }
}
