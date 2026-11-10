using NFS.Data.Track;

namespace NFS.Render;

/// <summary>
/// Type 3 object reference: the reference itself is an inline CAN animation [ref:2436]
/// (+4 u16 nKeys, +6 u16 ticksPerKey, 20-byte keys from +8; struct index at +3). Every frame <c>(ref, 1) [ref:2437]</c>
/// (mode 1 = looping) gives the world position and matrix; the model vertices are projected as [ref:2438]
/// <c>pos + v·M</c> with no other transform. [ref:2439]
/// </summary>
public static class AnimatedObjectPose
{
    public const int KeysOffset = 8;

    /// <summary>Position (16.16) and matrix of the looping animation at <paramref name="tick"/>; null when the reference has
    /// fewer than 2 keys (the original would divide by zero).</summary>
    public static (int[] M, int X, int Y, int Z)? Eval(byte[] raw, int tick)
    {
        int n = raw[4] | raw[5] << 8, tpkRaw = raw[6] | raw[7] << 8;
        if (n < 2 || raw.Length < KeysOffset + 20 * n || tick < 0) return null;
        int tpk = tpkRaw is 0 or > 400 ? 6 : tpkRaw;
        int k = tick / tpk % (n - 1);
        return KnockedObjectPose.Eval(Key(raw, k), Key(raw, k + 1), ((tick % tpk) << 16) / tpk);
    }

    static CanKey Key(byte[] d, int i)
    {
        int o = KeysOffset + 20 * i;
        return new CanKey(BitConverter.ToInt32(d, o), BitConverter.ToInt32(d, o + 4), BitConverter.ToInt32(d, o + 8),
            BitConverter.ToInt16(d, o + 12), BitConverter.ToInt16(d, o + 14), BitConverter.ToInt16(d, o + 16), BitConverter.ToInt16(d, o + 18));
    }
}
