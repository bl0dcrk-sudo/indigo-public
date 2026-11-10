using System.Collections.ObjectModel;
using NFS.Core;
using NFS.Game.Session;

namespace NFS.Net.Protocol;

/// <summary>
/// Exact wire form of <see cref="CarView"/>: every field, as zig-zag varints (16.16 values stay bit-exact; small counters take one
/// byte). A typical car takes ~150–190 bytes. Nothing is quantised, so a decoded view equals the host's view field by field.
/// </summary>
public static class CarViewCodec
{
    public static void Write(WireWriter w, CarView v)
    {
        w.VarI(v.Id); w.VarI(v.PlayerIndex); w.U8((byte)v.Flags); w.U8(v.Active); w.U8(v.Rail);
        Vec(w, v.Pos); Mat(w, v.Orient); Mat(w, v.RoadFrame); Vec(w, v.Vel); Vec(w, v.LocalVel); Vec(w, v.LocalForce);
        w.VarI(v.Slice); w.VarI(v.RaceState); w.VarI(v.UpDotN); w.VarI(v.WrongWayTicks); w.VarI(v.AiSpeed); w.VarI(v.LateralOffset);
        w.VarI(v.CrashSlice); w.VarI(v.ManualResetLock); w.VarI(v.AutoResetCounter); w.VarI(v.CollisionCooldown); w.VarU((ulong)v.AirTicks);
        w.VarI(v.HalfWidth); w.VarI(v.HalfHeight); w.VarI(v.HalfLength); w.VarI(v.DistToPlayer); w.VarI(v.TrafficSkin); w.VarI(v.Steer);
        w.U8(v.BrakeRamped); w.U8(v.ThrottleRamped); w.VarI(v.ShakeAmp0); w.VarI(v.ShakeAmp1); w.VarI(v.ShakePhase0); w.VarI(v.ShakePhase1);
        w.VarI(v.SlipFront); w.VarI(v.SlipState); w.VarI(v.SpeedApprox); w.VarI(v.SurfaceType); w.VarI(v.SlidingAxles);
        w.VarI(v.HeightAboveRoad); w.VarI(v.Rpm); w.U8(v.Gear); w.U8(v.Horn); w.VarI(v.TurboReady); w.VarI(v.TurboCounter);
        w.VarI(v.Skid324); w.VarI(v.Skid328); w.VarI(v.LapStartTick); w.VarI(v.LapsDone);
        if (v.LapTimes.Count > NetProtocol.MaxLapTimes) throw new InvalidOperationException("lap times");
        w.U8(v.LapTimes.Count);
        foreach (var t in v.LapTimes) w.VarI(t);
        w.VarI(v.RacePosition); w.VarI(v.LastProgressTick);
    }

    public static CarView Read(ref WireReader r)
    {
        var v = new CarView
        {
            Id = r.VarI32(), PlayerIndex = r.VarI32(), Flags = (CarFlags)r.U8(), Active = r.U8(), Rail = r.U8(),
            Pos = Vec(ref r), Orient = Mat(ref r), RoadFrame = Mat(ref r), Vel = Vec(ref r), LocalVel = Vec(ref r), LocalForce = Vec(ref r),
            Slice = r.VarI32(), RaceState = r.VarI32(), UpDotN = r.VarI32(), WrongWayTicks = r.VarI32(), AiSpeed = r.VarI32(),
            LateralOffset = r.VarI32(), CrashSlice = r.VarI32(), ManualResetLock = r.VarI32(), AutoResetCounter = r.VarI32(),
            CollisionCooldown = r.VarI32(), AirTicks = (ushort)r.VarU(ushort.MaxValue),
            HalfWidth = r.VarI32(), HalfHeight = r.VarI32(), HalfLength = r.VarI32(), DistToPlayer = r.VarI32(), TrafficSkin = r.VarI32(),
            Steer = r.VarI32(), BrakeRamped = r.U8(), ThrottleRamped = r.U8(), ShakeAmp0 = r.VarI32(), ShakeAmp1 = r.VarI32(),
            ShakePhase0 = r.VarI32(), ShakePhase1 = r.VarI32(), SlipFront = r.VarI32(), SlipState = r.VarI32(), SpeedApprox = r.VarI32(),
            SurfaceType = r.VarI32(), SlidingAxles = r.VarI32(), HeightAboveRoad = r.VarI32(), Rpm = r.VarI32(), Gear = r.U8(), Horn = r.U8(),
            TurboReady = r.VarI32(), TurboCounter = r.VarI32(), Skid324 = r.VarI32(), Skid328 = r.VarI32(), LapStartTick = r.VarI32(),
            LapsDone = r.VarI32(),
        };
        int laps = r.U8();
        if (laps > NetProtocol.MaxLapTimes) throw new NetProtocolException("too many lap times");
        var times = new int[laps];
        for (int i = 0; i < laps; i++) times[i] = r.VarI32();
        return v with { LapTimes = Array.AsReadOnly(times), RacePosition = r.VarI32(), LastProgressTick = r.VarI32() };
    }

    static void Vec(WireWriter w, Vec3 v) { w.VarI(v.X); w.VarI(v.Y); w.VarI(v.Z); }
    static Vec3 Vec(ref WireReader r) => new(r.VarI32(), r.VarI32(), r.VarI32());

    static void Mat(WireWriter w, Mat3 m)
    {
        for (int i = 0; i < 9; i++) w.VarI(m[i]);
    }

    static Mat3 Mat(ref WireReader r) => new(r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32(), r.VarI32());
}

/// <summary>Per-connection fields of a snapshot: the ACK of this seat's inputs and actions and its input counters.</summary>
public readonly record struct NetSeatAck(long AppliedInput, long LastAction, long LateInputs, long FutureInputs, long HeldSteps, long NeutralSteps);

/// <summary>Race-wide part of a snapshot (in every part, so each part is decodable alone).</summary>
public sealed record NetSnapshotInfo(long SnapshotSequence, RaceViewFrameKind Kind, int SimulationTick64, int Steps, NetRacePhase Phase,
    bool RaceStarted, bool HasResults, long DurableSequence, int NumLaps, int RacerCount, IReadOnlyList<int> Ranking, int ConnectedSeats);

/// <summary>One decoded snapshot part: race info, the seat ACK and a subset of the cars (racers by CarId, parked by index).</summary>
public sealed record NetSnapshotPart(int RaceId, uint Epoch, int PartIndex, int PartCount, NetSnapshotInfo Info, NetSeatAck Ack,
    IReadOnlyList<CarFrame> Cars, IReadOnlyList<CarFrame> Parked);

/// <summary>
/// Snapshot encoder: a <see cref="RaceViewFrame"/> becomes one or more independent datagrams of at most
/// <see cref="NetProtocol.MaxDatagram"/> bytes. Cars are encoded once per step (<see cref="EncodeCars"/>) and packed greedily;
/// every part repeats the race info. The receiver publishes only complete snapshots: losing a part drops that snapshot,
/// but never invalidates the independently encoded next snapshot.
/// Parked cars are sent only once they differ from the baseline (durable dynamic objects); the baseline carries all of them.
/// </summary>
public static class SnapshotCodec
{
    /// <summary>Fixed bytes of a part without cars (worst case of the varints, ranking of <see cref="NetProtocol.MaxCars"/>).</summary>
    public const int MaxPartOverhead = NetHeader.Size + 2 + 10 * 8 + 6 * 10 + 4 + 2 + NetProtocol.MaxCars + 1;

    /// <summary>Encoded cars of one frame (shared by all connections of that step).</summary>
    public sealed record EncodedCars(IReadOnlyList<byte[]> Records);

    public static EncodedCars EncodeCars(RaceViewFrame frame, Func<int, bool> includeParked)
    {
        var list = new List<byte[]>();
        var w = new WireWriter(NetProtocol.MaxDatagram - MaxPartOverhead);
        foreach (var c in frame.Cars) list.Add(Record(w, 0, c));
        foreach (var c in frame.Parked)
            if (includeParked(c.CarId)) list.Add(Record(w, 1, c));
        return new EncodedCars(list);
    }

    static byte[] Record(WireWriter w, int kind, CarFrame c)
    {
        w.Reset();
        w.U8(kind);
        w.U8(c.CarId);
        w.VarU((ulong)(uint)c.PoseEpoch);
        CarViewCodec.Write(w, c.State);
        return w.ToArray();
    }

    /// <summary>Datagrams of one snapshot for one connection, each at most <paramref name="budget"/> bytes.</summary>
    public static List<byte[]> EncodeParts(int raceId, uint epoch, NetSnapshotInfo info, NetSeatAck ack, EncodedCars cars, int budget = NetProtocol.MaxDatagram)
    {
        var groups = new List<List<byte[]>>();
        var header = new WireWriter(budget);
        WriteHead(header, raceId, epoch, info, ack, 0, 1);
        int room = budget - header.Length - 1 - 2; // car count byte, and slack for part index/count of larger values
        var current = new List<byte[]>();
        int used = 0;
        foreach (var rec in cars.Records)
        {
            if (rec.Length > room) throw new InvalidOperationException($"car record of {rec.Length} bytes does not fit a part");
            if (used + rec.Length > room)
            {
                groups.Add(current);
                current = [];
                used = 0;
            }
            current.Add(rec);
            used += rec.Length;
        }
        groups.Add(current);
        if (groups.Count > 255) throw new InvalidOperationException("too many parts");
        var parts = new List<byte[]>(groups.Count);
        var w = new WireWriter(budget);
        for (int i = 0; i < groups.Count; i++)
        {
            w.Reset();
            WriteHead(w, raceId, epoch, info, ack, i, groups.Count);
            w.U8(groups[i].Count);
            foreach (var rec in groups[i]) w.Bytes(rec);
            parts.Add(w.ToArray());
        }
        return parts;
    }

    static void WriteHead(WireWriter w, int raceId, uint epoch, NetSnapshotInfo info, NetSeatAck ack, int part, int count)
    {
        new NetHeader(NetMessageType.Snapshot, raceId, epoch).Write(w);
        w.U8(part);
        w.U8(count);
        w.VarU(info.SnapshotSequence);
        w.U8((byte)info.Kind);
        w.VarI(info.SimulationTick64);
        w.VarU(info.Steps);
        w.U8((byte)info.Phase);
        w.U8((info.RaceStarted ? 1 : 0) | (info.HasResults ? 2 : 0));
        w.VarU(info.DurableSequence);
        w.U8(info.NumLaps);
        w.U8(info.RacerCount);
        w.U8(info.ConnectedSeats);
        w.U8(info.Ranking.Count);
        foreach (var id in info.Ranking) w.U8(id);
        w.VarU(ack.AppliedInput);
        w.VarU(ack.LastAction);
        w.VarU(ack.LateInputs);
        w.VarU(ack.FutureInputs);
        w.VarU(ack.HeldSteps);
        w.VarU(ack.NeutralSteps);
    }

    public static NetSnapshotPart Decode(NetHeader h, ref WireReader r)
    {
        int part = r.U8(), count = r.U8();
        if (count == 0 || part >= count) throw new NetProtocolException("bad part index");
        long seq = r.VarU(int.MaxValue);
        byte kind = r.U8();
        if (kind > (byte)RaceViewFrameKind.Resync) throw new NetProtocolException("bad frame kind");
        int tick = r.VarI32();
        int steps = (int)r.VarU(int.MaxValue);
        byte phase = r.U8();
        if (phase > (byte)NetRacePhase.Aborted) throw new NetProtocolException("bad phase");
        byte flags = r.U8();
        long durable = r.VarU(int.MaxValue);
        int laps = r.U8(), racers = r.U8(), connected = r.U8();
        int rankN = r.U8();
        if (rankN > NetProtocol.MaxCars) throw new NetProtocolException("ranking");
        var ranking = new int[rankN];
        for (int i = 0; i < rankN; i++) ranking[i] = r.U8();
        var ack = new NetSeatAck(r.VarU(long.MaxValue / 2), r.VarU(long.MaxValue / 2), r.VarU(int.MaxValue), r.VarU(int.MaxValue),
            r.VarU(int.MaxValue), r.VarU(int.MaxValue));
        int n = r.U8();
        var cars = new List<CarFrame>();
        var parked = new List<CarFrame>();
        for (int i = 0; i < n; i++)
        {
            byte k = r.U8();
            int id = r.U8();
            int epoch = (int)(uint)r.VarU(uint.MaxValue);
            if (k > 1 || id >= NetProtocol.MaxCars) throw new NetProtocolException("bad car record");
            var frame = new CarFrame(id, CarViewCodec.Read(ref r), epoch);
            (k == 0 ? cars : parked).Add(frame);
        }
        r.End();
        var info = new NetSnapshotInfo(seq, (RaceViewFrameKind)kind, tick, steps, (NetRacePhase)phase, (flags & 1) != 0, (flags & 2) != 0,
            durable, laps, racers, Array.AsReadOnly(ranking), connected);
        return new NetSnapshotPart(h.RaceId, h.Epoch, part, count, info, ack, cars, parked);
    }
}

/// <summary>
/// Durable presentation state that must never be lost (reliable ordered stream, one message per step that changed it):
/// knocked-over objects (add-only) and tyre-mark segments (changed quads of the 32-segment ring + ring cursor). Applying the
/// stream in order rebuilds the host's <see cref="SkidBuffer"/> and knocked set exactly.
/// </summary>
public sealed record NetDurable(int RaceId, uint Epoch, long DurableSequence, long SnapshotSequence,
    IReadOnlyList<KeyValuePair<ushort, KnockedObject>> Knocked, NetSkidDelta? Skids)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        new NetHeader(NetMessageType.Durable, RaceId, Epoch).Write(w);
        w.VarU(DurableSequence);
        w.VarU(SnapshotSequence);
        w.VarU((ulong)Knocked.Count);
        foreach (var (id, k) in Knocked)
        {
            w.U16(id);
            w.VarI(k.HitTick);
            w.VarI(k.CanIndex);
            w.I8(k.Angle);
        }
        w.Bool(Skids != null);
        Skids?.Write(w);
        return w.ToArray();
    }

    public static NetDurable Decode(NetHeader h, ref WireReader r)
    {
        long seq = r.VarU(int.MaxValue), snap = r.VarU(int.MaxValue);
        int n = (int)r.VarU(4096);
        var knocked = new KeyValuePair<ushort, KnockedObject>[n];
        for (int i = 0; i < n; i++) knocked[i] = new(r.U16(), new KnockedObject(r.VarI32(), r.VarI32(), r.I8()));
        var skids = r.Bool() ? NetSkidDelta.Read(ref r) : null;
        r.End();
        return new NetDurable(h.RaceId, h.Epoch, seq, snap, knocked, skids);
    }
}

/// <summary>Changed skid quads: per segment a quad range (with the origin when it starts at quad 0) plus the ring cursor.</summary>
public sealed record NetSkidDelta(int Count, int Current, int Added, IReadOnlyList<NetSkidDelta.Op> Ops)
{
    public sealed record Op(int Segment, int FromQuad, int X, int Y, int Z, int Quads, short[] Vertices, byte[] Texture);

    static readonly SkidBuffer Empty = new();

    /// <summary>Changes from <paramref name="old"/> (null = empty ring) to <paramref name="now"/>; null when nothing changed.
    /// Only the used quads of a segment (0..Quads-1) and its origin are state; memory past Quads is never rendered.</summary>
    public static NetSkidDelta? Diff(SkidBuffer? old, SkidBuffer now)
    {
        old ??= Empty;
        var ops = new List<Op>();
        for (int i = 0; i < SkidBuffer.SegmentCount; i++)
        {
            SkidBuffer.Segment s = now.Segments[i], o = old.Segments[i];
            bool sameOrigin = o.X == s.X && o.Y == s.Y && o.Z == s.Z;
            int from = 0;
            if (sameOrigin && s.Quads >= o.Quads)
            {
                from = o.Quads;
                for (int q = 0; q < o.Quads; q++)
                    if (o.Texture[q] != s.Texture[q] || !o.Vertices.AsSpan(q * 12, 12).SequenceEqual(s.Vertices.AsSpan(q * 12, 12))) { from = q; break; }
            }
            if (sameOrigin && from == s.Quads && s.Quads == o.Quads) continue;
            ops.Add(new Op(i, from, s.X, s.Y, s.Z, s.Quads,
                s.Vertices.AsSpan(from * 12, (s.Quads - from) * 12).ToArray(), s.Texture.AsSpan(from, s.Quads - from).ToArray()));
        }
        if (ops.Count == 0 && old.Count == now.Count && old.Current == now.Current && old.Added == now.Added) return null;
        return new NetSkidDelta(now.Count, now.Current, now.Added, ops);
    }

    /// <summary>Applies the delta to a client mirror.</summary>
    public void Apply(SkidBuffer mirror)
    {
        foreach (var op in Ops)
        {
            var s = mirror.Segments[op.Segment];
            if (op.FromQuad == 0) { s.X = op.X; s.Y = op.Y; s.Z = op.Z; }
            op.Vertices.CopyTo(s.Vertices, op.FromQuad * 12);
            op.Texture.CopyTo(s.Texture, op.FromQuad);
            s.Quads = op.Quads;
        }
        mirror.SetCursor(Count, Current, Added);
    }

    public void Write(WireWriter w)
    {
        w.U8(Count);
        w.U8(Current);
        w.VarU((ulong)Added);
        w.U8(Ops.Count);
        foreach (var op in Ops)
        {
            w.U8(op.Segment);
            w.U8(op.FromQuad);
            w.U8(op.Quads);
            if (op.FromQuad == 0) { w.VarI(op.X); w.VarI(op.Y); w.VarI(op.Z); }
            foreach (var v in op.Vertices) w.VarI(v);
            w.Bytes(op.Texture);
        }
    }

    public static NetSkidDelta Read(ref WireReader r)
    {
        int count = r.U8(), current = r.U8();
        int added = (int)r.VarU(int.MaxValue);
        if (count > SkidBuffer.SegmentCount || current >= SkidBuffer.SegmentCount) throw new NetProtocolException("skid cursor");
        int n = r.U8();
        if (n > SkidBuffer.SegmentCount) throw new NetProtocolException("skid ops");
        var ops = new Op[n];
        for (int i = 0; i < n; i++)
        {
            int seg = r.U8(), from = r.U8(), quads = r.U8();
            if (seg >= SkidBuffer.SegmentCount || quads > SkidBuffer.MaxQuads || from > quads) throw new NetProtocolException("skid op");
            int x = 0, y = 0, z = 0;
            if (from == 0) { x = r.VarI32(); y = r.VarI32(); z = r.VarI32(); }
            var verts = new short[(quads - from) * 12];
            for (int j = 0; j < verts.Length; j++)
            {
                int v = r.VarI32();
                if (v is < short.MinValue or > short.MaxValue) throw new NetProtocolException("skid vertex");
                verts[j] = (short)v;
            }
            ops[i] = new Op(seg, from, x, y, z, quads, verts, r.Bytes(quads - from).ToArray());
        }
        return new NetSkidDelta(count, current, added, ops);
    }
}

/// <summary>One-shot events of one or more steps (reliable unordered; the client deduplicates by EventId and expires sound).</summary>
public sealed record NetEvents(int RaceId, uint Epoch, IReadOnlyList<RaceEvent> Events)
{
    public const int MaxEventSize = 64;

    public byte[] Encode()
    {
        var w = new WireWriter(NetProtocol.MaxDatagram);
        new NetHeader(NetMessageType.Events, RaceId, Epoch).Write(w);
        w.U8(Events.Count);
        foreach (var e in Events)
        {
            switch (e)
            {
                case CountdownBeepEvent c: w.U8(1); Common(w, e); w.U8(c.Sample); break;
                case CarImpactEvent c:
                    w.U8(2); Common(w, e); w.U8(c.CarId); w.VarI(c.ImpactType); w.VarI(c.Impact); w.VarI(c.UpDotN);
                    w.VarI(c.Position.X); w.VarI(c.Position.Y); w.VarI(c.Position.Z);
                    break;
                case HornRequestEvent c: w.U8(3); Common(w, e); w.U8(c.CarId); w.VarI(c.Count); w.VarI(c.Length); w.VarI(c.Gap); break;
                default: throw new InvalidOperationException($"{e.GetType().Name} is not a network event"); // results travel as NetResults
            }
        }
        return w.ToArray();
    }

    static void Common(WireWriter w, RaceEvent e)
    {
        w.VarU(e.EventId);
        w.VarU(e.SnapshotSequence);
        w.VarI(e.SimulationTick64);
    }

    public static NetEvents Decode(NetHeader h, ref WireReader r)
    {
        int n = r.U8();
        var list = new RaceEvent[n];
        for (int i = 0; i < n; i++)
        {
            byte kind = r.U8();
            long id = r.VarU(long.MaxValue / 2), seq = r.VarU(int.MaxValue);
            int tick = r.VarI32();
            list[i] = kind switch
            {
                1 => new CountdownBeepEvent(h.RaceId, id, seq, tick, r.U8()),
                2 => new CarImpactEvent(h.RaceId, id, seq, tick, r.U8(), r.VarI32(), r.VarI32(), r.VarI32(), new Vec3(r.VarI32(), r.VarI32(), r.VarI32())),
                3 => new HornRequestEvent(h.RaceId, id, seq, tick, r.U8(), r.VarI32(), r.VarI32(), r.VarI32()),
                _ => throw new NetProtocolException("unknown event"),
            };
        }
        r.End();
        return new NetEvents(h.RaceId, h.Epoch, list);
    }

    /// <summary>Splits events into messages of at most <see cref="NetProtocol.MaxDatagram"/> bytes.</summary>
    public static IEnumerable<NetEvents> Batches(int raceId, uint epoch, IReadOnlyList<RaceEvent> events)
    {
        int per = (NetProtocol.MaxDatagram - NetHeader.Size - 1) / MaxEventSize;
        for (int i = 0; i < events.Count; i += per)
            yield return new NetEvents(raceId, epoch, events.Skip(i).Take(per).ToArray());
    }
}

/// <summary>Builds immutable <see cref="RaceViewFrame"/> collections for decoded data (no aliasing of decoder buffers).</summary>
static class FrameCollections
{
    public static IReadOnlyDictionary<ushort, KnockedObject> Copy(Dictionary<ushort, KnockedObject> d) =>
        new ReadOnlyDictionary<ushort, KnockedObject>(new Dictionary<ushort, KnockedObject>(d));
}
