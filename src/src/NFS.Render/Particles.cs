using NFS.Core;

namespace NFS.Render;

/// <summary>One effect particle (entry of the pool, 0x4C bytes in the original). Positions and velocities are 16.16 world values. [ref:2605]</summary>
public struct Particle
{
    /// <summary>+0: 1 tyre smoke, 2/3 crash smoke, 4/5 spark streaks, 6 dust, 7 grass (gr), 8 gravel (ga), 9 snow (sn); 0 = free.</summary>
    public byte Type;
    /// <summary>+1: pool slot at creation (it only picks the size variant of tyre smoke, <see cref="ParticleSprites"/>).</summary>
    public byte Slot;
    /// <summary>+2: 1 = also pushed by the slowly turning "wind" (smoke and dust only).</summary>
    public byte Wind;
    /// <summary>+4: remaining updates (one update = 4 race ticks); the particle dies at 0.</summary>
    public int Life;
    /// <summary>+8: sprite rotation, 10-bit (0x400 = 360°).</summary>
    public int Angle;
    /// <summary>+0xC: rotation per update (decays towards 0). [ref:2606]</summary>
    public int Spin;
    public Vec3 Pos, Vel;
}

/// <summary>
/// Pool of the car effects of NFS2SE (sfx: tyre smoke, crash smoke, dust, sparks, grass/gravel/snow spray), presentation only:
///   spawn rules (spawn), rate limits and the update step (every 4 ticks). [ref:2607]
///   particles (sfx). [ref:2608]
/// <para>Deviation: the original draws the random numbers (velocity, rotation) from the global random stream shared with sound [ref:2609]
/// body lean and the rain; the port gives the effects their own <see cref="EaRandom"/> stream (the same split as
/// <c>CarAnim</c>, <c>Weather</c> and the tyre-mark texture choice), so the simulation (CarObj, RaceRunner, WatchedRandom, AI LCG) is untouched.</para>
/// </summary>
public sealed class ParticleSystem
{
    /// <summary>0x3C entries (never exceeds it); a full pool recycles the entry with the least life left. [ref:2610]</summary>
    public const int Capacity = 60;
    /// <summary>The pool is updated when more than 3 ticks passed since the last update (<c>3 &lt; tick − last</c>). [ref:2611]</summary>
    public const int UpdateTicks = 4;

    readonly Particle[] _p = new Particle[Capacity];
    readonly int[] _lastSpawn = new int[64];   // [(car & 0xF) * 4 + wheel]: tick of the last throttled spawn [ref:2612]
    readonly EaRandom _rng;
    int _lastUpdate, _lastCrashSmoke, _windPhase;

    public ParticleSystem(int seed = 0) => _rng = new EaRandom(seed);

    public int Count { get; private set; }
    /// <summary>Incremented on every change (spawn, update with live particles); lets a publisher copy the pool only when it changed.</summary>
    public int Version { get; private set; }
    public ReadOnlySpan<Particle> Items => _p.AsSpan(0, Count);

    /// <summary>Empties the pool (race start). [ref:2613]</summary>
    public void Reset(int tick)
    {
        _lastUpdate = tick;
        _windPhase = 0;
        Count = 0;
        Version++;
    }

    /// <summary>Number of updates a particle of the given type lives: 1,6 → 16; 2,3 → 32; 4,5 → 8; 7,8 → 3; 9 → 4. [ref:2614]</summary>
    public static int LifetimeOf(int type) => type switch
    {
        1 or 6 => 0x10, 2 or 3 => 0x20, 4 or 5 => 8, 7 or 8 => 3, 9 => 4, _ => 0,
    };

    /// <summary>per (car &amp; 0xF, wheel) at most one spawn per 3 ticks for the types 7..9 and per 11 ticks [ref:2615]
    /// for the others. Returns whether a particle was created.</summary>
    public bool SpawnThrottled(int carId, int wheel, in Vec3 pos, int type, in Vec3 vel, int tick)
    {
        int k = (carId & 0xF) * 4 + (wheel & 3);
        int gap = type is 7 or 8 or 9 ? 2 : 10;
        if (tick - _lastSpawn[k] <= gap) return false;
        _lastSpawn[k] = tick;
        return Spawn(pos, type, vel) >= 0;
    }

    /// <summary>at most once per 9 ticks (global, not per car): type 1 (15 of 16 times) and type 3 [ref:2616]
    /// both with +0xF5C added to the upward velocity.</summary>
    public void CrashSmoke(in Vec3 pos, int tick)
    {
        if (tick - _lastCrashSmoke < 9) return;
        _lastCrashSmoke = tick;
        if ((_rng.Next() & 0xF) != 0) Bump(Spawn(pos, 1, default));
        Bump(Spawn(pos, 3, default));
        void Bump(int i) { if (i >= 0) _p[i].Vel.Y += 0xF5C; }
    }

    /// <summary>creates a particle of <paramref name="type"/> (1..9; anything else is ignored) at <paramref name="pos"/>. [ref:2617]
    /// <paramref name="vel"/> (the car velocity) only matters for the types 4, 5, 7, 8, 9: velocity = vel &gt;&gt; 5 per update.
    /// Returns the pool index, −1 when ignored.</summary>
    public int Spawn(in Vec3 pos, int type, in Vec3 vel)
    {
        if (type < 1 || type > 9) return -1;
        int i = Count;
        if (Count == Capacity)
        {
            i = 0;
            int least = _p[0].Life;
            for (int j = 1; j < Capacity; j++)
                if (_p[j].Life < least) { least = _p[j].Life; i = j; }
        }
        else Count++;
        ref var p = ref _p[i];
        p = default;
        p.Pos = pos;
        p.Type = (byte)type;
        p.Slot = (byte)i;
        if (type is 1 or 2 or 3 or 6)
        {
            // x, z in 0..0x3FF, y (up) 0x7FF..0xBFE per update [ref:2618]
            p.Vel.X = (int)(_rng.Next() & 0x3FF);
            p.Vel.Z = (int)(_rng.Next() & 0x3FF);
            p.Vel.Y = (int)(_rng.Next() & 0x3FF) + 0x7FF;
            p.Wind = 1;
        }
        else p.Vel = new Vec3(vel.X >> 5, vel.Y >> 5, vel.Z >> 5);
        p.Angle = (int)_rng.Next();
        p.Spin = (int)(_rng.Next() & 0x7F) - 0x3F;
        p.Life = LifetimeOf(type);
        Version++;
        return i;
    }

    /// <summary>every 4th tick moves all particles (velocity, plus the wind for smoke and dust), turns them, ages them [ref:2620]
    /// and drops the dead ones (the last entry fills the hole).</summary>
    public void Update(int tick)
    {
        if (tick - _lastUpdate <= UpdateTicks - 1) return;
        _lastUpdate = tick;
        if (Count == 0) return;
        _windPhase += 2;
        FxMath.SinCosTab(_windPhase, out int s, out int c);
        int windX = Fx.Mul(c, 0xCCC), windZ = Fx.Mul(s, 0xCCC);
        for (int i = 0; i < Count; i++)
        {
            ref var p = ref _p[i];
            p.Pos.X += p.Vel.X; p.Pos.Y += p.Vel.Y; p.Pos.Z += p.Vel.Z;
            if (p.Wind != 0) { p.Pos.X += windX; p.Pos.Z += windZ; }
            p.Angle += p.Spin;                      // [ref:2621]
            if (p.Spin < -3) p.Spin++;
            else if (p.Spin >= 4) p.Spin--;
            if (--p.Life == 0) p.Type = 0;
        }
        for (int i = 0; i < Count; i++)
            if (_p[i].Type == 0) { _p[i] = _p[--Count]; i--; }
        Version++;
    }

    /// <summary>Copy of the live particles (an immutable snapshot for a published frame).</summary>
    public Particle[] Snapshot() => _p.AsSpan(0, Count).ToArray();
}
