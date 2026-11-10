namespace NFS.Audio;

/// <summary>Integer control values sent to the EA sound API, before PCM resampling.</summary>
public readonly record struct EngineLevels(int Pitch, int OnLoad, int OffLoad, int RoadPitch, int RoadVolume, int Throttle);

public static class RaceSoundMath
{
    // max(x,z)+min(x,z)/4, then combine y in the same way (16.16). [ref:0035]
    public static int Distance(int x, int y, int z)
    {
        x = Math.Abs(x); y = Math.Abs(y); z = Math.Abs(z);
        int d = Math.Max(x, z) + (Math.Min(x, z) >> 2);
        return Math.Max(d, y) + (Math.Min(d, y) >> 2);
    }

    // speed of sound 278 m/s; distance sampled at 32 Hz. [ref:0036]
    public static int Doppler(int distance, int? previous) => previous is { } p
        ? (int)(((long)0x1160000 << 16) / (0x1160000L + (distance - p) * 32L)) : 0x10000;

    public static int Attenuation(int squaredMeters, int limit = 0x15F9) =>
        squaredMeters < limit ? (limit - squaredMeters) * 127 / limit : 0;

    // Division and fixed-point conversion truncate toward zero. [ref:0037]
    public static int RpmPitch(int rpm, int redline, int doppler = 0x10000, bool nearby = false) =>
        Math.Clamp((int)((long)doppler * ((nearby ? Math.Min(rpm, 6000) : rpm) * 127 / (redline + 2000)) / 65536)
            - (nearby ? 10 : 0), 0, nearby ? 127 : 100);

    // Table has 129 entries, including both endpoints. [ref:0038]
    public static EngineLevels PlayerEngine(int pitch, int throttle, int previousThrottle, int speed,
        int surface, bool tunnel, int squaredMeters, ReadOnlySpan<byte> curve, int master = 127)
    {
        int t = previousThrottle + (throttle - previousThrottle) / (throttle < previousThrottle ? 8 : 2);
        int road = Math.Abs(speed / 65536) * 2;
        int roadPitch = tunnel ? 94 : 64;
        if (tunnel) road += 20;
        if (surface is 2 or 10 or 13 or 11 or 12) { road += road / 4; roadPitch = 24; }
        if (squaredMeters > 4) road = road * 6 / 10;
        road = Math.Min(road, 127);
        int shift = squaredMeters < 4 ? 2 : squaredMeters < 35 ? 3 : 6;
        int gain = Math.Min((pitch + 40) * Attenuation(squaredMeters, 0x2BF2) / 128, 127);
        int on = curve[Math.Min(t, 255) >> 1] * gain >> 7;
        int off = curve[128 - (Math.Min(t, 255) >> 1)] * gain >> 7;
        int wind = gain * road >> 7;
        int Scale(int v) { v = v * master / 127; return (v >> shift) + (v >> 1); }
        return new(pitch, Scale(on), Scale(off), roadPitch, Scale(wind), t);
    }

    // Bank GEN holds the persistent tyre/scrape loops. [ref:0039]
    public static int TyreSample(int kind, int material) => (kind, material) switch
    {
        (2, 0) => 42, (2, 8) => 43, (2, 3) => 41,
        (1, 0 or 3 or 8) => 40, _ => 44
    };

    // front tyres track strength; rear pitch is latched until the sample changes. [ref:0040]
    public static int StrengthPitch(int strength) => Math.Min(strength * 127 / 0xA0000, 127);

    // PT tag 6 cents, tag 7 root note (default 60), tag 10 range in semitones. [ref:0041]
    public static int PitchCents(PtHeader h, int control) =>
        h.Get(6, 0) + (60 - h.Get(7, 60)) * 100 + ((control - 64) * h.Get(10, 0) * 100 >> 6);

    // rounds to float after each multiplication/division; factors[i] applies to bit i of |cents|. [ref:0042]
    public static double PitchMultiplier(int cents, ReadOnlySpan<float> factors)
    {
        float v = 1;
        int bits = Math.Abs(cents);
        for (int i = 0; bits != 0; i++, bits >>= 1)
            if ((bits & 1) != 0) v = cents >= 0 ? v * factors[i] : v / factors[i];
        return v;
    }
}
