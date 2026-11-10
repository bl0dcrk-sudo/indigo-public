using NFS.Data.Track;

namespace NFS.Core;

/// <summary>Per-track record (stride 0x1C), filled by the setup routine; the [ref:0195]
/// arguments in order: record, count, spawn delay, one-way, then the stack side, sim, arcade, glue-until.
/// Only tracks 0 and 2..8 are set (track 1 does not exist, its record stays zero in BSS). With the game option 0x400
/// (byte bit 4) every set record gets count 15 and delay 0. [ref:0196]</summary>
public readonly record struct TrafficTrackParams(int Count, int SpawnDelay, bool OneWay, int LaneSide,
    int SimFactor, int ArcadeFactor, int GlueUntil)
{
    private static readonly TrafficTrackParams[] Table =
    [
        new(0, 500, true, 1, 0x10000, 0x10000, 400),       // track 0 (Proving Grounds)
        new(0, 0, false, 0, 0, 0, 0),                      // track 1: never set
        new(4, 0x514, false, -1, 0x10000, 0x10000, 400),   // 2
        new(12, 400, false, 1, 0x10000, 0x10A3D, 400),     // 3
        new(8, 800, false, 1, 0x10000, 0x10000, 400),      // 4
        new(12, 400, false, 1, 0xF0A3, 0xFAE1, 400),       // 5
        new(3, 0x1194, true, 1, 0x10000, 0x10000, 400),    // 6
        new(3, 0x1194, false, 1, 0xF333, 0xF851, 400),     // 7
        new(3, 0x1194, true, 1, 0x10000, 0x10000, 400),    // 8
    ];

    /// <summary>Spawn delay and one-way flag, per track [ref:0197]
    /// (tracks ≥ 9: 12, 500, one-way when the high nibble of slice 1 +0x1F is 0, side 1).</summary>
    public static TrafficTrackParams For(int track, TrackSlice[] slices, int gameOptions)
    {
        if (track is < 0 or >= 9)
            return new(12, 500, slices.Length > 1 && (slices[1].Lanes >> 4) == 0, 1, 0x10000, 0x10000, 400);
        var p = Table[track];
        if ((gameOptions & 0x400) != 0 && track != 1) p = p with { Count = 15, SpawnDelay = 0 };
        return p;
    }
}

/// <summary>Context of the traffic cycle: the AI globals and the car lists the original reads. [ref:0198]</summary>
public sealed class TrafficEnv
{
    public required TrackSlice[] Slices { get; init; }
    public required bool Reverse { get; init; }
    public required AiRandom Rng { get; init; }
    public required TrafficTrackParams Track { get; init; }
    /// <summary>Base speed: FX(TRAFCFG [7] km/h, 0x471C). [ref:0199]</summary>
    public required int BaseSpeed { get; init; }
    /// <summary>Speed factors (TRAFCFG [3..6]). [ref:0200]</summary>
    public required int[] SpeedFactors { get; init; }
    /// <summary>Lane offsets (TRAFCFG [8..11]). [ref:0201]</summary>
    public required int[] LaneOffsets { get; init; }
    /// <summary>Game options (race config +0x20). [ref:0202]</summary>
    public int GameOptions { get; init; }
    /// <summary>Number of parked cars. [ref:0203]</summary>
    public int Parked { get; set; }
    /// <summary>Number of class-4 cars; the number of class-8 cars is subtracted from the limit. [ref:0204]</summary>
    public int TrafficCount { get; init; }
    public int CopCount { get; init; }
    /// <summary>Number of racing opponents (the spawn schedule is slower for more than 2). [ref:0205]</summary>
    public int Opponents { get; init; }
    /// <summary>Cars in order (unsorted). [ref:0206]</summary>
    public required IReadOnlyList<CarObj> Order { get; init; }
    /// <summary>Cars in neighbour order (sorted by slice every tick; ListPosition = index). [ref:0207]</summary>
    public required IReadOnlyList<CarObj> Sorted { get; init; }
    /// <summary>Human car entries. [ref:0208]</summary>
    public required IReadOnlyList<CarObj> Humans { get; init; }
    /// <summary>First car of the class-4 list. [ref:0209]</summary>
    public CarObj? FirstTraffic { get; init; }
    /// <summary>Track update callback, invoked as in the original. [ref:0210]</summary>
    public required Action<CarObj> TrackUpdate { get; init; }
    public int Tick { get; set; }
    /// <summary><c>true</c> for split screen: still draws its LCG step but never honks. [ref:0211]</summary>
    public bool SplitScreen { get; init; }
    /// <summary>LOD tick count from the AI drive task. [ref:0212]</summary>
    public int LodTicks { get; set; }
    public int Max => Track.Count;
    public int SpawnDelayRange => Track.SpawnDelay;
    public bool OneWay => Track.OneWay;
}

/// <summary>
/// Traffic cycle of class-4 cars (flag 0x10). [ref:0213]
/// Initial speed, then relocation. Every [ref:0215]
/// random number is a step of the AI LCG (<see cref="AiRandom"/>); the order of the draws is part of the compatibility.
/// AiFlags (+0x3A8): 0x10 parked/hidden, 0x800 spawn ahead of the anchor, 0x1000 oncoming (wrong way).
/// </summary>
public static class AiTraffic
{
    public const int FlagParked = 0x10, FlagAhead = 0x800, FlagOncoming = 0x1000;

    private static int Mid16(AiRandom r) => (int)((r.Next() & 0xFFFF00) >> 8);

    /// <summary>Clears the AI fields (every car); a traffic or police car is then parked and placed [ref:0216]
    /// on slice 0 100 m above the road (data = {0, 100 m, 0}). [ref:0217]</summary>
    public static void Reset(CarObj c, TrafficEnv env)
    {
        c.DirChangeTick = 0; c.DirDuration = 0; c.LaneScanCounter = 0;
        c.LaneTargetOffset = 0; c.AiTargetSpeed = 0; c.AiSpeed = 0; c.AiSpeedApprox = 0; c.LaneRandomOffset = 0;
        c.LateralOffsetCur = 0; c.AiFlags = 0; c.LaneHalfWidth = 0;
        c.ShiftCounter = 0; c.HornCounter = 0; c.SpawnDelay = 0; c.TrafficSpeedFactor = 0; c.RailLateralSpeed = 0;
        c.RelocateTries = 0;
        c.TrafficSkin = 0;   // skin variant (+0x634) [ref:0218]
        c.PsychoTicks = 0; c.PsychoHits = 0; c.PsychoTarget = null;
        c.Direction = 1;
        c.Anchor = c;
        if ((c.Flags & (CarFlags.Traffic | CarFlags.Cop)) == 0) return;
        Park(c, env);
        NewtonPlace.PlaceOnSlice(c, env.Slices, 0, new Vec3(0, 0x640000, 0), env.Reverse, env.TrackUpdate);
    }

    /// <summary>Park: one LCG draw sets +0x3C4 = ((mid16 · spawn_delay) &gt;&gt; 16) + 1; the car is hidden [ref:0219]
    /// (+0x8D = 0, +0x3A8 |= 0x10) above slice 5 at y += index·10 m + 25 m, velocities cleared, parked count incremented. [ref:0220]</summary>
    public static void Park(CarObj c, TrafficEnv env)
    {
        uint r = (uint)Mid16(env.Rng);
        c.Active = 0;
        c.SpawnDelay = (int)(r * (uint)env.SpawnDelayRange >> 16) + 1;
        var sl = env.Slices[5];
        c.Pos = new Vec3(sl.X, sl.Y + c.PlayerIndex * 0xA0000 + 0x1900000, sl.Z);
        NewtonPlace.TrkPosInit(c.Trk, 5);
        c.Progress = 5;
        c.Lap = 0; c.LapOffset = 0;
        c.Vel = default; c.AngVel = default;
        env.Parked++;
        c.AiFlags |= FlagParked;
    }

    /// <summary>The leading racer: the last car of the order list with flag 1 (an opponent when there is [ref:0221]
    /// one, otherwise the human); its progress +6 drives the spawn schedule.</summary>
    private static int LeadProgress(TrafficEnv env)
    {
        for (int i = env.Order.Count - 1; i >= 0; i--)
            if ((env.Order[i].Flags & CarFlags.Racer) != 0) return env.Order[i].Progress;
        return 0;
    }

    /// <summary>Spawn check (parked car, every AI drive task that passes its LOD gate): the effective [ref:0222]
    /// progress p = lead racer's +6 (below 700 and after tick 0x5C0 replaced by ((tick − 0x200)/64·30)/4; +200 with option
    /// 0x400); spawn when p ≥ 700 or p ≥ (index − first traffic index + 1)·50 + 75 + (opponents &gt; 2), the delay
    /// +0x3C4 −= lod ticks reached 0 and fewer cars are active than the track maximum (count − cops − parked); otherwise the [ref:0223]
    /// delay is kept at 1. Returns true when the car was spawned.</summary>
    public static bool SpawnWait(CarObj c, TrafficEnv env)
    {
        int p = LeadProgress(env);
        if (p < 700 && env.Tick > 0x5C0)
            p = (((env.Tick - 0x200) / 64) * 30) / 4;
        if ((env.GameOptions & 0x400) != 0) p += 200;
        int first = env.FirstTraffic?.PlayerIndex ?? c.PlayerIndex;
        int threshold = (env.Opponents > 2 ? 1 : 0) + (c.PlayerIndex - first + 1) * 0x32 + 0x4B;
        if (!(p > 699 || threshold <= p)) return false;
        c.SpawnDelay -= env.LodTicks;
        if (c.SpawnDelay >= 1) return false;
        if (env.Max <= env.TrafficCount - env.CopCount - env.Parked) { c.SpawnDelay = 1; return false; }
        Spawn(c, env);
        return true;
    }

    /// <summary>Spawn: +0x4EC = 1, flags 0x810 cleared and 0x800 set, parked decremented; draw 1: bit 23 of the [ref:0224]
    /// LCG word set → oncoming (+0x3A9 bit 0x10), else cleared; draw 2 is discarded [ref:0225]
    /// (the skin cycle does not use it; the LCG is stepped inline); anchor = the last local human of the sorted list; +0x8D = 1; [ref:0226]</summary>
    public static void Spawn(CarObj c, TrafficEnv env)
    {
        c.RelocateTries = 1;
        c.AiFlags = (c.AiFlags & ~(FlagParked | FlagAhead)) | FlagAhead;
        env.Parked--;
        uint t = env.Rng.Next();
        if (((t & 0xFFFF00) >> 0x17) == 0) c.AiFlags &= ~FlagOncoming; else c.AiFlags |= FlagOncoming;
        env.Rng.Next();
        // skin variant: +0x634 = (+0x634 + 1) % 3 unless option bit 0x1000 is set [ref:0228]
        if ((env.GameOptions & 0x1000) == 0) c.TrafficSkin = (c.TrafficSkin + 1) % 3;
        for (int i = env.Sorted.Count - 1; i >= 0; i--)
            if ((env.Sorted[i].Flags & CarFlags.LocalHuman) != 0) { c.Anchor = env.Sorted[i]; break; }
        c.Active = 1;
        Place(c, env);
    }

    private static int Dir(bool aheadFlag, bool reverse) => aheadFlag == reverse ? -1 : 1;

    /// <summary>when the anchor is slower than 4 m/s (v &lt; 0x40000, reverse: [ref:0229]
    /// v &gt; −0x40000): +0x4EC = 0, 0x800 cleared (set again for an oncoming car). One-way track: never oncoming. Direction
    /// from 0x800 and the track direction; draw 1 picks the first candidate of the sorted list (a local human farther than
    /// 200 m from the target slice keeps being the anchor; a human nearer replaces it), draw 2: slice = anchor slice ±
    /// (50 + (mid16·11 &gt;&gt; 16)) (oncoming on a reverse track: opposite sign), wrapped once.</summary>
    public static void PickSpawnSlice(CarObj c, TrafficEnv env)
    {
        var anchor = c.Anchor!;
        int v = anchor.AiSpeed;
        if (v < 0x40000 || (env.Reverse && v > -0x40000))
        {
            c.RelocateTries = 0;
            c.AiFlags &= ~FlagAhead;
            if ((c.AiFlags & FlagOncoming) != 0) c.AiFlags |= FlagAhead;
        }
        if (env.OneWay) c.AiFlags &= ~FlagOncoming;
        int n = env.Slices.Length, cars = env.Sorted.Count;
        int dir = Dir((c.AiFlags & FlagAhead) != 0, env.Reverse);
        // note: 0x800 clear + forward track → −1 (behind); 0x800 set + forward → +1 (ahead); reverse inverts
        int start = (cars * Mid16(env.Rng) >> 16) + c.Anchor!.ListPosition + cars + dir;
        int idx = start;
        for (int k = 1; k < cars; k++)
        {
            var o = env.Sorted[idx % cars];
            if ((o.Flags & CarFlags.Static) == 0 && (o.Flags & CarFlags.LocalHuman) != 0)
            {
                int s = c.Anchor!.Trk.Slice + dir * 0x3D;
                if (dir < 0) { if (s < 0) s += n; } else if (s >= n) s -= n;
                if (!Within200(env.Slices[s].X, env.Slices[s].Z, o)) break;
                c.Anchor = o;
            }
            idx += dir;
        }
        int dist = dir;
        if (env.Reverse && (c.AiFlags & FlagOncoming) == 0) dist = -dist;
        dist = ((Mid16(env.Rng) * 0xB >> 16) + 0x32) * dist;
        int slice = c.Anchor!.Trk.Slice + dist;
        if (dist < 0) { if (slice < 0) slice += n; }
        else if (slice >= n) slice -= n;
        c.Trk.Slice = slice;
    }

    /// <summary>Places the car: picks the lane side (right side lanes when ((!oncoming) xor reverse) with [ref:0230]
    /// lane_side = 1, or (oncoming xor reverse) with lane_side = −1; else the left lanes) with one LCG draw choosing the
    /// lane k = (nibble · mid16 &gt;&gt; 16) + 1 (nibble = slice +0x1F low nibble for the right, high nibble for the left),
    /// x = ±k·w ∓ w/2 (w = half width byte · 0x8000); + the lane offset +0x3A4; speed factor (draw), lane offset (draw),
    /// initial speed (reverse = oncoming xor track reverse), then the speed factor again (draw) [ref:0231]
    /// v = |+0x39C| · forward (orientation row 2), ω = 0, AirTicks / skid / counters cleared.</summary>
    public static void Place(CarObj c, TrafficEnv env)
    {
        PickSpawnSlice(c, env);
        bool oncoming = (c.AiFlags & FlagOncoming) != 0;
        bool right = (((!oncoming) ^ env.Reverse) && env.Track.LaneSide == 1) || ((oncoming ^ env.Reverse) && env.Track.LaneSide == -1);
        var sl = env.Slices[c.Trk.Slice];
        int x;
        if (right)
        {
            int w = sl.HalfWidthRight << 15;
            int k = (int)((uint)(sl.Lanes & 0xF) * (uint)Mid16(env.Rng) >> 16) + 1;
            x = w * k - w / 2;
        }
        else
        {
            int w = sl.HalfWidthLeft << 15;
            int k = (int)((uint)(sl.Lanes >> 4) * (uint)Mid16(env.Rng) >> 16) + 1;
            x = -w * k + w / 2;
        }
        c.LaneTargetOffset = x + c.LaneRandomOffset;
        PickSpeedFactor(c, env);
        PickLaneOffset(c, env);
        InitialSpeed(c, env);
        NewtonPlace.PlaceOnSlice(c, env.Slices, c.Trk.Slice, new Vec3(c.LaneTargetOffset, 0x10000, 0),
            oncoming ^ env.Reverse, env.TrackUpdate);
        PickSpeedFactor(c, env);
        int speed = Math.Abs(c.AiSpeed);
        c.Vel = new Vec3(Fx.Mul(speed, c.Orient.M20), Fx.Mul(speed, c.Orient.M21), Fx.Mul(speed, c.Orient.M22));
        c.AngVel = default;
        c.AirTicks = 0;
        c.Skid324 = 0; c.Skid328 = 0;
        c.ShiftCounter = 0;
        c.HornCounter = c.ShiftCounter;
    }

    /// <summary>Speed factor: +0x3C8 = [mid16 &amp; 3]. [ref:0232]</summary>
    public static void PickSpeedFactor(CarObj c, TrafficEnv env) => c.TrafficSpeedFactor = env.SpeedFactors[Mid16(env.Rng) & 3];

    /// <summary>For traffic: +0x3A4 = [mid16 &amp; 3]. [ref:0233]</summary>
    public static void PickLaneOffset(CarObj c, TrafficEnv env) => c.LaneRandomOffset = env.LaneOffsets[Mid16(env.Rng) & 3];

    /// <summary>v &gt; 0: max(FX(v, +0x3C8), v − 13.4 m/s, 8.9 m/s); v ≤ 0: FX(v, +0x3C8). [ref:0234]</summary>
    public static int SpeedScale(CarObj c, int v)
    {
        int s = Fx.Mul(v, c.TrafficSpeedFactor);
        if (v <= 0) return s;
        if (s < v - 0xD6666) s = v - 0xD6666;
        return s < 0x8E666 ? 0x8E666 : s;
    }

    /// <summary>from the anchor's +0x39C (negated on a reverse track): below 13.8 m/s [ref:0235]
    /// 19.4 m/s, 27.8 → 13.9, 41.7 → 11.1, 55 → 6.9, 69.4 → 4.1, else 2.77 m/s.</summary>
    public static int OncomingSpeed(CarObj c, bool reverse)
    {
        int v = c.Anchor!.AiSpeed;
        if (reverse) v = -v;
        if (v < 0xDCCCC) return 0x136666;
        if (v < 0x1BCCCC) return 0xDE666;
        if (v < 0x29B333) return 0xB1C28;
        if (v < 0x378000) return 0x6E666;
        if (v < 0x456666) return 0x428F5;
        return 0x2C51E;
    }

    /// <summary>Same direction: +0x398 = scale(base/2 + anchor +0x3A0/2), +0x39C [ref:0236]
    /// = +0x398 (an opponent 0); oncoming: +0x39C = +0x398 = −oncoming speed; negated on a reverse track.</summary>
    public static void InitialSpeed(CarObj c, TrafficEnv env)
    {
        if ((c.AiFlags & FlagOncoming) == 0)
        {
            c.AiTargetSpeed = SpeedScale(c, (env.BaseSpeed >> 1) + (c.Anchor!.AiSpeedApprox >> 1));
            c.AiSpeed = (c.Flags & CarFlags.Opponent) == 0 ? c.AiTargetSpeed : 0;
        }
        else
        {
            c.AiSpeed = -OncomingSpeed(c, env.Reverse);
            c.AiTargetSpeed = c.AiSpeed;
        }
        if (env.Reverse) { c.AiTargetSpeed = -c.AiTargetSpeed; c.AiSpeed = -c.AiSpeed; }
    }

    /// <summary>Distance check: |dx|, |dz| → max + min/4 ≤ 200 m (0xC80000) / 244 m (0xF40000). [ref:0237]</summary>
    private static bool Within(int x, int z, CarObj o, int limit)
    {
        int dz = Math.Abs(z - o.Pos.Z), dx = Math.Abs(x - o.Pos.X);
        return (dz < dx ? dx + (dz >> 2) : (dx >> 2) + dz) <= limit;
    }
    public static bool Within200(int x, int z, CarObj o) => Within(x, z, o, 0xC80000);
    public static bool Within244(int x, int z, CarObj o) => Within(x, z, o, 0xF40000);

    /// <summary>Any human within 200 m of the car. [ref:0238]</summary>
    public static bool NearAnyHuman(CarObj c, TrafficEnv env)
    {
        foreach (var h in env.Humans) if (Within200(c.Pos.X, c.Pos.Z, h)) return true;
        return false;
    }

    private static int SliceDelta(int a, int b, int n)
    {
        int d = a - b;
        if (d > 0 && d > n / 2) return d - n;
        if (d < 0 && d < -(n / 2)) return d + n;
        return d;
    }

    /// <summary>Relocation (active traffic car, from the AI drive task): a car farther than 244 m from its [ref:0239]
    /// anchor searches the sorted list, starting after the anchor (anchor earlier in the list than the car) or before it,
    /// for the first non-static local human; it only counts as far enough when more than 100 slices away in that
    /// direction. No human found → park. The human found becomes the anchor; when no human is within 200 m of the car and
    /// (the new anchor is outside 200 m of the old one, or it was far enough, or there is a single human) +0x4EC is
    /// decremented: below 0 → park, otherwise place again. Returns true when the car was parked or placed.</summary>
    public static bool Relocate(CarObj c, TrafficEnv env)
    {
        var anchor = c.Anchor!;
        if (Within244(c.Pos.X, c.Pos.Z, anchor)) return false;
        int cars = env.Sorted.Count, n = env.Slices.Length;
        CarObj? found = null;
        bool far = false;
        bool anchorBefore = anchor.ListPosition < c.ListPosition;
        c.AiFlags &= ~FlagAhead;                                   // byte +0x3A9 &= 0xF7
        if (!anchorBefore) c.AiFlags |= FlagAhead;                 // the other branch sets 0x08 again
        int step = anchorBefore ? 1 : -1, i = anchor.ListPosition + step + cars;
        for (int k = 0; k < cars; k++, i += step)
        {
            var o = env.Sorted[i % cars];
            if ((o.Flags & CarFlags.Static) != 0 || (o.Flags & CarFlags.LocalHuman) == 0) continue;
            found = o;
            int d = SliceDelta(c.Trk.Slice, o.Trk.Slice, n);
            far = anchorBefore ? d > 100 : d < -100;
            break;
        }
        if (found == null) { Park(c, env); return true; }
        c.Anchor = found;
        if (!NearAnyHuman(c, env) && (!Within200(found.Pos.X, found.Pos.Z, anchor) || far || env.Humans.Count == 1))
        {
            if (--c.RelocateTries < 0) { Park(c, env); return true; }
            Place(c, env);
            return true;
        }
        return false;
    }

    /// <summary>Honk (only with the AI line step, i.e. not on rails): with the game option 0x400 a [ref:0240]
    /// traffic car with a blocker in its lane honks (2, 0x20, 8); an oncoming non-opponent car with a human within 200 m and
    /// lane speed above −3 m/s draws one LCG step and honks when (mid16 &amp; 0xFF) &lt; 10 and it moves.</summary>
    public static void Honk(CarObj c, TrafficEnv env, AiSpeedPlanning.Scan scan, Action<CarObj, int, int, int>? horn)
    {
        bool near = NearAnyHuman(c, env);
        if ((c.Flags & CarFlags.Traffic) != 0 && (env.GameOptions & 0x400) != 0 && scan.Blocker[1] != null) horn?.Invoke(c, 2, 0x20, 8);
        if ((c.Flags & (CarFlags.Opponent | CarFlags.Cop)) == 0 && (c.AiFlags & FlagOncoming) != 0 && near && -0x30000 < scan.Speed[1])
        {
            uint t = env.Rng.Next();
            if (!env.SplitScreen && ((t & 0xFFFF00) >> 8 & 0xFF) < 10 && c.AiSpeed != 0) horn?.Invoke(c, 2, 0x20, 8);
        }
    }

    /// <summary>Traffic branch (flag 8 clear): same direction → +0x398 [ref:0241]
    /// scale((base + lead human +0x39C)/2), min with scale(FX(speed byte of the slice, 0.4)), · +0x560, at least 10 m/s;
    /// oncoming → scale(−oncoming speed) · +0x560; negated on a reverse track.</summary>
    public static void DesiredSpeed(CarObj c, TrafficEnv env, CarObj leadHuman, int speedByteAtSlice)
    {
        int v;
        if ((c.AiFlags & FlagOncoming) == 0)
        {
            v = SpeedScale(c, (env.BaseSpeed >> 1) + (leadHuman.AiSpeed >> 1));
            int cap = SpeedScale(c, (int)(((long)(speedByteAtSlice << 16) * 0x6666) >> 16)); // truncating product (0x425D..)
            if (cap < v) v = cap;
            v = Fx.Mul(v, c.Unk560);
            if (v < 0xA0001) v = 0xA0000;
        }
        else
        {
            v = Fx.Mul(SpeedScale(c, -OncomingSpeed(c, env.Reverse)), c.Unk560);
        }
        c.AiTargetSpeed = env.Reverse ? -v : v;
    }
}
