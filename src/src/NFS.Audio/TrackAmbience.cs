using NFS.Data.Track;

namespace NFS.Audio;

/// <summary>Result of one ambient frame: voice 0xE loop (null = stop it) and an optional one-shot (voice 0xF). [ref:0043]</summary>
public readonly record struct AmbientFrame(AmbientVoice? Loop, AmbientVoice? Shot);

/// <summary>Control values for the EA sound API (sample in bank TRAMBnn, pitch always 0x40, volume 0..127, pan 0..127 before mirroring).</summary>
public readonly record struct AmbientVoice(int Sample, int Pitch, int Volume, int Pan);

/// <summary>
/// Track ambient sounds. Pure logic, no device, no RNG of the simulation. [ref:0044]
/// Per frame (only the main view): walk the neighbour list (XBID 4) of the listener's block in order; for each block with an XBID 17 source
/// compute distance/pan with <c>DistancePan</c> and volume = <c>min(0x10000 / q, 127)</c>, where q = <c>((d >> 12)^2) >> 10</c> and d = distance [ref:0045]
/// (127 when the divisor is 0; that is <c>262144 / m^2</c> with m in metres, so 0 beyond ~512 m). Type 1 = random one-shot on voice 0xF, fired
/// when <c>(frame &amp; 0x3F) >> 3 ==</c> the phase (initial value 1); then it is re-rolled with <c>&amp; 7</c>; [ref:0046]
/// any other type = looping voice 0xE, only the FIRST such source of the list plays (the accumulated flag is passed on). When no loop was
/// started voice 0xE is stopped. Options volume (master) is applied by the host. Hypotheses: <c>frame</c> = render frame counter
/// (incremented per presented frame; the port passes its 32 Hz sound frame); every block of the neighbour list is "loaded".
/// </summary>
public sealed class TrackAmbience
{
    public const int LoopVoice = 0xE, ShotVoice = 0xF, ShotPitch = 0x40;
    private readonly AmbientSource?[] _sources;
    private readonly ushort[][] _neighbors;
    private readonly Func<uint> _random;
    private int _shotPhase = 1;   // initial value 1, as in the original [ref:0047]

    public TrackAmbience(AmbientSource?[] sourcePerBlock, ushort[][] neighborsPerBlock, Func<uint> random)
    {
        _sources = sourcePerBlock; _neighbors = neighborsPerBlock; _random = random;
    }

    public static TrackAmbience ForTrack(Trk trk, Func<uint> random) =>
        new(AmbientSource.ForTrack(trk), AmbientSource.NeighborsForTrack(trk), random);

    /// <summary>Number of blocks with a source.</summary>
    public int SourceCount => _sources.Count(s => s != null);

    /// <summary>Volume 0..127 for a 16.16 distance, as in the original. [ref:0048]</summary>
    public static int VolumeForDistance(int distance)
    {
        int q = unchecked((distance >> 12) * (distance >> 12)) >> 10;
        if (q == 0) return 127;
        return Math.Min(0x10000 / q, 127);
    }

    /// <summary>Distance and pan. Pan = lateral component (dot with the right axis, normalised by the distance, +-1.0 in 16.16) / 1024 + 64. [ref:0049]</summary>
    public static (int Distance, int Pan) DistancePan((int X, int Y, int Z) listener, (int X, int Y, int Z) right, (int X, int Y, int Z) source)
    {
        int dx = source.X - listener.X, dy = source.Y - listener.Y, dz = source.Z - listener.Z;
        int distance = RaceSoundMath.Distance(dx, dy, dz);
        if (distance == 0) return (0, 64);
        long dot = MulRound(dx, right.X) + MulRound(dy, right.Y) + MulRound(dz, right.Z);
        int lateral = (int)Math.Clamp((dot << 16) / distance, -0x10000, 0x10000);
        return (distance, lateral / 1024 + 64);
    }

    private static long MulRound(int a, int b) => ((long)a * b + 0x8000) >> 16;

    /// <summary>One frame. <paramref name="block"/> = block of the listener, <paramref name="frame"/> = frame counter (see the class notes). [ref:0050]</summary>
    public AmbientFrame Update(int block, (int X, int Y, int Z) listener, (int X, int Y, int Z) right, int frame)
    {
        AmbientVoice? loop = null, shot = null;
        if ((uint)block >= (uint)_neighbors.Length) return default;
        foreach (ushort n in _neighbors[block])
        {
            if (n >= _sources.Length || _sources[n] is not { } src) continue;
            var (distance, pan) = DistancePan(listener, right, (src.X, src.Y, src.Z));
            int volume = VolumeForDistance(distance);
            if (src.IsRandomShot)
            {
                if (((frame & 0x3F) >> 3) == _shotPhase)
                {
                    shot = new AmbientVoice(src.Sample, ShotPitch, volume, pan);
                    _shotPhase = (int)(_random() & 7);
                }
            }
            else if (loop == null) loop = new AmbientVoice(src.Sample, ShotPitch, volume, pan);
        }
        return new AmbientFrame(loop, shot);
    }
}
