namespace NFS.Core;

/// <summary>
/// Ring buffer of 32 segments x 0x1A0 B. A segment holds an [ref:0578]
/// origin (+0, 16.16), a quad count (+0xC, at most 16), 16 quads x 4 vertices x 3 s16 relative to the origin >> 8 (+0x10) and one
/// texture index (skd0..3) per quad (+0x190). Presentation state only: it is filled from the car fields by <see cref="SkidMarks"/>
/// and never read by the simulation.
/// </summary>
public sealed class SkidBuffer
{
    public const int SegmentCount = 32, MaxQuads = 16;
    public const int HalfWidth = 0x4CCC;                      // 0x4CCC * sin/cos >> 17 = 0.15 m each side [ref:0579]

    public sealed class Segment
    {
        public int X, Y, Z;
        public int Quads;
        public readonly short[] Vertices = new short[MaxQuads * 12];
        public readonly byte[] Texture = new byte[MaxQuads];
    }

    public Segment[] Segments { get; } = Enumerable.Range(0, SegmentCount).Select(_ => new Segment()).ToArray();
    /// <summary>Segments in use (rendered 0..Count-1). [ref:0580]</summary>
    public int Count { get; private set; }
    /// <summary>Segment being filled. [ref:0581]</summary>
    public int Current { get; private set; }
    /// <summary>Total quads added since the start (diagnostics / tests).</summary>
    public int Added { get; private set; }

    /// <summary>stream for the texture choice (the original shares the global one; the call count only matters [ref:0582]
    /// for render/sound, so the port keeps an isolated stream like RaceAudio).</summary>
    public EaRandom Random { get; } = new();

    /// <summary>Deep copy of the segments for a published view frame (stage 5a.2); the copy shares nothing with this buffer
    /// and its <see cref="Random"/> stream is fresh (only <see cref="Add"/> draws from it).</summary>
    public SkidBuffer Clone()
    {
        var copy = new SkidBuffer { Count = Count, Current = Current, Added = Added };
        for (int i = 0; i < SegmentCount; i++)
        {
            Segment s = Segments[i], d = copy.Segments[i];
            d.X = s.X; d.Y = s.Y; d.Z = s.Z; d.Quads = s.Quads;
            s.Vertices.CopyTo(d.Vertices, 0);
            s.Texture.CopyTo(d.Texture, 0);
        }
        return copy;
    }

    /// <summary>Sets the ring cursor of a mirror rebuilt from received segments (network client, stage 5a.3); never used by the
    /// simulation side.</summary>
    public void SetCursor(int count, int current, int added)
    {
        if ((uint)count > SegmentCount || (uint)current >= SegmentCount || added < 0) throw new ArgumentOutOfRangeException(nameof(count));
        (Count, Current, Added) = (count, current, added);
    }

    /// <summary>Clears all segments and the ring cursor. [ref:0583]</summary>
    public void Reset()
    {
        foreach (var s in Segments) { s.X = s.Y = s.Z = 0; s.Quads = 0; }
        Count = Current = Added = 0;
    }

    /// <summary>Adds one quad from <paramref name="prev"/> to <paramref name="cur"/> (prev = pos_l, cur = pos_r; kind 2 = end piece). [ref:0584]</summary>
    public void Add(Vec3 prev, Vec3 cur, int kind)
    {
        if (kind == 0) return;
        var s = Segments[Current];
        bool fresh = s.Quads == MaxQuads
            || Math.Abs(s.X - cur.X) >= 0x400000 || Math.Abs(s.Y - cur.Y) >= 0x400000 || Math.Abs(s.Z - cur.Z) >= 0x400000;
        if (fresh)
        {
            if (Count < SegmentCount) { Current = Count; Count++; }
            else if (Current < SegmentCount - 1) Current++;
            else Current = 0;
            s = Segments[Current];
            s.Quads = 0;
        }
        if (s.Quads == 0) { s.X = cur.X; s.Y = cur.Y; s.Z = cur.Z; }
        MakeQuad(s.Vertices.AsSpan(s.Quads * 12, 12), s, prev, cur);
        s.Texture[s.Quads] = (byte)((Random.Next() & 1) + (kind == 2 ? 2 : 0));
        s.Quads++;
        Added++;
    }

    /// <summary>strip prev to cur, 0.15 m each side, vertices relative to the segment origin >> 8. [ref:0585]</summary>
    static void MakeQuad(Span<short> q, Segment o, Vec3 a, Vec3 b)
    {
        int th = FxMath.Atan2(b.X - a.X, b.Z - a.Z);
        FxMath.SinCosTab(th + 0x100, out int s1, out _);
        FxMath.SinCosTab(th, out int s0, out _);
        int ox = Half(-s1), oz = Half(s0);
        q[0] = S(a.X + ox - o.X); q[1] = S(a.Y - o.Y); q[2] = S(a.Z + oz - o.Z);
        q[3] = S(a.X - ox - o.X); q[4] = S(a.Y - o.Y); q[5] = S(a.Z - oz - o.Z);
        q[6] = S(b.X - ox - o.X); q[7] = S(b.Y - o.Y); q[8] = S(b.Z - oz - o.Z);
        q[9] = S(b.X + ox - o.X); q[10] = S(b.Y - o.Y); q[11] = S(b.Z + oz - o.Z);
        static short S(int v) => unchecked((short)(v >> 8));
        static int Half(int sin)
        {
            long p = (long)sin * HalfWidth;
            uint lo = (uint)p; int hi = (int)(p >> 32);
            return unchecked((int)((lo >> 16) + (uint)(hi << 16) + ((lo >> 15) & 1))) >> 1;
        }
    }

    /// <summary>World position of vertex <paramref name="v"/> (0..3) of quad <paramref name="quad"/> in a segment (16.16).</summary>
    public static Vec3 Vertex(Segment seg, int quad, int v) =>
        new(seg.X + (seg.Vertices[quad * 12 + v * 3] << 8), seg.Y + (seg.Vertices[quad * 12 + v * 3 + 1] << 8), seg.Z + (seg.Vertices[quad * 12 + v * 3 + 2] << 8));
}

/// <summary>
/// Presentation of tyre marks (task prio 0x32). [ref:0586]
/// Runs for cars within 90 m. [ref:0587]
/// <see cref="SkidBuffer.Add"/>. It only READS the car (+0x324/+0x328 skid levels, pos, orientation, +0x184 surface); the
/// original keeps the wheel state in car+0x32C..0x35C and in globals; here it lives outside CarObj.
/// Not ported: smoke/spark particles and the tyre sound events (RaceAudio).
/// </summary>
public sealed class SkidMarks(SkidBuffer buffer, Func<int, bool> surfaceMarks)
{
    public SkidBuffer Buffer { get; } = buffer;

    sealed class CarState
    {
        public int Flags;                                   // car+0x32C: 1 front tracks open, 2 rear tracks open
        public Vec3 FrontL, FrontR, RearL, RearR;           // car+0x330, 0x33C, 0x348, 0x354
    }
    readonly Dictionary<CarObj, CarState> _cars = [];

    // Per (car & 0xF, wheel): last tick, open flag and start point (globals in the original). [ref:0588]
    readonly int[] _lastTick = new int[64];
    readonly bool[] _open = new bool[64];
    readonly Vec3[] _start = new Vec3[64];

    /// <summary>Tyre marks for one active car (caller: not on rails, DistToPlayer &lt; 90 m). [ref:0589]</summary>
    public void CarSkidFx(CarObj c, int tick)
    {
        if (c.UpDotN < 0x3333) return;                      // car on its side/roof: sound + sparks only
        CarSkidMarks(c, tick);
    }

    void CarSkidMarks(CarObj c, int tick)
    {
        if (!_cars.TryGetValue(c, out var st)) _cars[c] = st = new CarState();
        bool traffic = (c.Flags & CarFlags.Traffic) != 0;
        int surf = c.SurfaceType;
        int front, frontEmit, rear;
        if (c.DistToPlayer < 0x3C0000 && c.HeightAboveRoad < 0x6666 && c.CrashSlice == 0)
        {
            front = c.Skid324 - 0x4CCC;
            frontEmit = front - Math.Abs(c.LocalVel.Z / 8);
            rear = c.Skid328 / 4 - 0x4CCC;
        }
        else front = frontEmit = rear = 0;
        if ((surf == 0xD || surf == 5) && c.SpeedApprox > 0x2C9999) rear = 0x10000;
        front = Math.Max(front, 0); frontEmit = Math.Max(frontEmit, 0); rear = Math.Max(rear, 0);

        Vec3 side = default, fwdF = default, fwdR = default;
        if (front != 0 || rear != 0 || (st.Flags & 3) != 0)
        {
            side = Mul8(c.Orient.Row0, c.HalfWidth * 3 / 4 / 256);
            fwdR = Mul8(c.Orient.Row2, c.HalfLength * 3 / 4 / 256);        // 0.75 * half length (rear axle, behind) [ref:3370]
            fwdF = Mul8(c.Orient.Row2, c.HalfLength * 2 / 4 / 256);        // 0.5 * half length (front axle, ahead) [ref:3371]
        }
        var pos = new Vec3(c.Pos.X, c.PrevRoadHeight, c.Pos.Z);            // y = car+0x154 (road height)

        if (front == 0)
        {
            if ((st.Flags & 1) != 0)
            {
                st.Flags -= 1;
                if (!traffic) { Track(c, 0, st.FrontL, 1, tick); Track(c, 1, st.FrontR, 1, tick); }
            }
        }
        else if (frontEmit != 0)
            Emit(c, st, false, pos + side + fwdF, pos - side + fwdF, surf, traffic, tick);

        if (rear == 0)
        {
            if ((st.Flags & 2) != 0)
            {
                if (!traffic) { Track(c, 2, st.RearL, 1, tick); Track(c, 3, st.RearR, 1, tick); }
                st.Flags -= 2;
            }
        }
        else
            Emit(c, st, true, pos + side - fwdR, pos - side - fwdR, surf, traffic, tick);
    }

    static Vec3 Mul8(Vec3 v, int k) => new(unchecked(v.X * k) / 256, unchecked(v.Y * k) / 256, unchecked(v.Z * k) / 256);

    /// <summary>Emits marks for one axle (wheel pair: front 0/1, rear 2/3). [ref:0590]</summary>
    void Emit(CarObj c, CarState st, bool rear, Vec3 left, Vec3 right, int surf, bool traffic, int tick)
    {
        int w = rear ? 2 : 0, bit = rear ? 2 : 1;
        if (surfaceMarks(surf))
        {
            if (!traffic) { Track(c, w, left, 0, tick); Track(c, w + 1, right, 0, tick); }
            st.Flags |= bit;
            if (rear) { st.RearL = left; st.RearR = right; } else { st.FrontL = left; st.FrontR = right; }
        }
        else if ((st.Flags & bit) != 0)
        {
            if (!traffic)
            {
                Track(c, w, rear ? st.RearL : st.FrontL, 1, tick);
                Track(c, w + 1, rear ? st.RearR : st.FrontR, 1, tick);
            }
            st.Flags -= bit;
        }
    }

    /// <summary>joins the wheel points into pieces of at least 0.5 m; <paramref name="end"/> closes the track. [ref:0591]</summary>
    void Track(CarObj c, int wheel, Vec3 pos, int end, int tick)
    {
        int k = (c.Id & 0xF) * 4 + wheel;
        if (end == 0 && !(tick != _lastTick[k] && tick - _lastTick[k] >= 0)) return;
        _lastTick[k] = tick;
        if (!_open[k])
        {
            if (end == 0) { _open[k] = true; _start[k] = pos; }
            return;
        }
        if (end != 0)
        {
            if (!Within(_start[k], pos, 0x3333)) Segment(_start[k], pos, 2, 0);
            _open[k] = false;
            return;
        }
        if (!Within(_start[k], pos, 0x8000))
        {
            Segment(_start[k], pos, 1, 0);
            _open[k] = true;
            _start[k] = pos;
        }
    }

    /// <summary>Pieces of 14 m or more (teleports) are split; the original's midpoint is [ref:0592]
    /// <c>(a+a)/2</c> (it doubles the first operand instead of adding both ends), so the "split" re-adds a..b - reproduced as is.</summary>
    void Segment(Vec3 a, Vec3 b, int kind, int depth)
    {
        if (Within(a, b, 0xE0000) || depth >= 3) { Buffer.Add(a, b, kind); return; }
        var mid = a;
        Segment(mid, b, 1, depth + 1);
        Segment(a, mid, kind, depth + 1);
    }

    /// <summary>max + min/4 &lt; limit. [ref:0593]</summary>
    public static bool Within(Vec3 a, Vec3 b, int limit)
    {
        int dx = Math.Abs(a.X - b.X), dz = Math.Abs(a.Z - b.Z);
        return (dz < dx ? dx + (dz >> 2) : dz + (dx >> 2)) < limit;
    }
}
