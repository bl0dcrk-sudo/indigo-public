namespace NFS.Core;

/// <summary>
/// Global EA generator: add-with-carry chain over 6 words (S0 … S5) [ref:0409]
/// plus a 96-bit counter S5..S1. Seeded with the sign-extended s16 seed from the race [ref:0410]
/// configuration (+0). Used by render, sound and weather; the stream is shared, so call counts matter for determinism. [ref:0411]
/// "RNG". [ref:0412]
/// </summary>
public sealed class EaRandom
{
    /// <summary>offsets: S[i] = seed + K[i] (cumulative ADDs in the original code). Seed 0 = the original's initial state. [ref:0413]</summary>
    public static readonly uint[] SeedOffsets = [0xF22D0E56, 0x883126E9, 0xC624DD2F, 0x0702C49C, 0x9E353F7D, 0x6FDF3B64];

    private readonly uint[] _s = new uint[6];

    public EaRandom(int seed = 0) => Seed(seed);

    /// <summary>The six state words S0..S5 (copy).</summary>
    public uint[] State => (uint[])_s.Clone();

    public void SetState(ReadOnlySpan<uint> s) => s[..6].CopyTo(_s);

    /// <summary>Seed value (the input of the original); the only caller passes <c>(short).seed</c>. [ref:0414]</summary>
    public void Seed(int seed)
    {
        for (int i = 0; i < 6; i++) _s[i] = (uint)seed + SeedOffsets[i];
    }

    /// <summary>Returns the full 32-bit result. [ref:0415]</summary>
    public uint Next()
    {
        ulong t = (ulong)_s[5] + _s[4];            // ADD
        _s[4] = (uint)t; uint c = (uint)(t >> 32);
        t = (ulong)_s[4] + _s[3] + c;              // adds the carry (uses the new S4)
        _s[3] = (uint)t; c = (uint)(t >> 32);
        t = (ulong)_s[3] + _s[2] + c;
        _s[2] = (uint)t; c = (uint)(t >> 32);
        t = (ulong)_s[2] + _s[1] + c;
        _s[1] = (uint)t; c = (uint)(t >> 32);
        uint r = (uint)((ulong)_s[1] + _s[0] + c); // final carry discarded
        // 96-bit counter S5 (low) .. S1; carry out of S1 increments the result
        if (++_s[5] == 0 && ++_s[4] == 0 && ++_s[3] == 0 && ++_s[2] == 0 && ++_s[1] == 0) r++;
        _s[0] = r;
        return r;
    }
}

/// <summary>
/// AI linear congruential generator [ref:0416]
/// inlined at 12 AI call sites. [ref:0417]
/// "LCG AI". [ref:0418]
/// </summary>
public sealed class AiRandom
{
    /// <summary>Values of the original before seeding.</summary>
    public const uint InitialState = 0xF874AF01, InitialMul = 1;

    public uint State { get; set; } = InitialState;
    public uint Last { get; set; }
    public uint Multiplier { get; set; } = InitialMul;

    public AiRandom() { }
    public AiRandom(int seed) => Seed(seed);

    /// <summary>mul = s|1 (s+1 if even), x = 3s (+1 if even); 7× x = x*mul &amp; 0xFFFF; [ref:0419]
    /// then x *= mul; last = x, state = x &amp; 0xFFFF.</summary>
    public void Seed(int s)
    {
        uint mul = (uint)s, x = (uint)(3 * s);
        if ((mul & 1) == 0) mul++;
        if ((x & 1) == 0) x++;
        for (int i = 0; i < 7; i++) x = (x * mul) & 0xFFFF;
        x *= mul;
        Last = x;
        State = x & 0xFFFF;
        Multiplier = mul;
    }

    /// <summary>One step as inlined: <c>t = mul*state; state = t &amp; 0xFFFF; last = t</c>; returns t.</summary>
    public uint Next()
    {
        uint t = Multiplier * State;
        State = t & 0xFFFF;
        Last = t;
        return t;
    }

    /// <summary>The usual 16-bit "middle" extraction <c>(t &amp; 0xFFFF00) &gt;&gt; 8</c> of a fresh step.</summary>
    public int NextMid16() => (int)((Next() & 0xFFFF00) >> 8);
}

/// <summary>
/// Watcom C <c>rand</c>: <c>s = s*0x41C64E6D + 0x3039; return (s&gt;&gt;16) &amp; 0x7FFF</c>; srand is never [ref:0420]
/// called by the game, so the start state is the runtime default 1 [probable]. [ref:0421] [ref:0422]
/// </summary>
public sealed class WatcomRandom
{
    public uint State { get; set; } = 1;

    public int Next()
    {
        State = State * 0x41C64E6D + 0x3039;
        return (int)((State >> 16) & 0x7FFF);
    }
}
