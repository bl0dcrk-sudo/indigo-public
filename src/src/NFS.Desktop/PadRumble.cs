using NFS.Core;

namespace NFS.Desktop;

/// <summary>Output of the pad vibration: motors 0..1 of player <c>player</c> (0 = pad 1, 1 = pad 2). The MonoGame implementation is
/// <see cref="GamePadRumbleSink"/>; tests supply a fake one, so the logic needs no device.</summary>
public interface IRumbleSink
{
    void Set(int player, float left, float right);
}

/// <summary>Real pad: <c>GamePad.SetVibration</c>. A device without a motor just ignores the call (MonoGame returns false).</summary>
public sealed class GamePadRumbleSink : IRumbleSink
{
    public void Set(int player, float left, float right) =>
        Microsoft.Xna.Framework.Input.GamePad.SetVibration((Microsoft.Xna.Framework.PlayerIndex)player, left, right);
}

/// <summary>Read-only snapshot of the car for the rumble. <c>Tick</c> = simulation tick (an impact is taken once per tick),
/// <c>SpeedMph</c> (|LocalVel.Z| * 0x23D70 &gt;&gt; 16 &gt;&gt; 16). [ref:1311]</summary>
public readonly record struct RumbleSample(long Tick, int Impact, int ImpactType, int SurfaceType, int SpeedMph, int Gear, int Rpm)
{
    /// <summary>Copies the fields; never writes to the car.</summary>
    public static RumbleSample From(CarObj car, long tick)
    {
        long mph = (long)Math.Abs((long)car.LocalVel.Z) * 0x23D70 >> 32;
        return new RumbleSample(tick, car.Impact, car.ImpactType, car.SurfaceType, (int)Math.Min(mph, int.MaxValue), car.Gear, car.Rpm);
    }
}

/// <summary>Pad vibration from the car state: a port of the original's force-feedback events onto the [ref:1312] [ref:3402]
/// two rumble motors (left = heavy, right = light). Pure logic: car snapshots + dt in, motor values out through <see cref="IRumbleSink"/>.
/// <list type="bullet">
/// <item>Impact : a jolt of magnitude 7000..10000 (of 10000) and 200..650 ms [ref:1313]
/// chosen by speed (wall) or by the impact strength (car-car); it fades out linearly. rate limit: no new jolt before [ref:1314]
/// duration/8 ms have passed since the last one.</item>
/// <item>Gear change : a short jolt, stronger with the rpm, forward gears only. [ref:1315]</item>
/// <item>Road : a continuous texture on the surface types of the original's switch, growing with the speed; none under 2 mph. [ref:1316]</item>
/// </list>
/// The steering spring has no rumble equivalent and is not ported. Motor scales and the car-car duration [ref:1317]
/// are port decisions (hypotheses in the RE).</summary>
public sealed class PadRumble
{
    public const int Players = 2;
    public const float Epsilon = 1f / 512;

    /// <summary>Road texture strength 0..10000 (0 = off): speed band (mph) magnitude of the original's table 2500..5000 plus the per-surface offset of the switch.</summary>
    public static int RoadMagnitude(int surfaceType, int mph)
    {
        if (mph < 2) return 0;
        int mag = mph switch
        {
            < 5 => 2500, < 10 => 3000, < 15 => 3500, < 20 => 4000, < 30 => 4500, < 40 => 5000, < 60 => 4500, < 80 => 4000, < 100 => 4000, _ => 3500,
        };
        // switch (surface 1..15): 6 and the non-listed types stop the effect; the listed ones shift the magnitude
        switch (surfaceType)
        {
            case 2: mag -= 500; break;
            case 3: mag -= 1000; break;
            case 4 or 0xB: mag += 1000; break;
            case 5 or 0xD: mag -= 1500; break;
            case 7 or 0xC: mag += 2000; break;
            default: return 0;
        }
        return Math.Clamp(mag, 0, 10000);
    }

    /// <summary>Jolt (magnitude 0..10000, duration ms) of a hit: wall by speed, car-car by the impact strength. [ref:1318]</summary>
    public static (int Magnitude, int Ms) ImpactJolt(int impactType, int impact, int mph)
    {
        if (impactType == 0x50001)
            return impact < 0x2000 ? (8000, 650) : impact < 0x5000 ? (8500, 500) : impact < 0x10000 ? (9000, 400) : impact < 0x100000 ? (9500, 350) : (10000, 350);
        return mph > 40 ? (10000, 200) : mph > 20 ? (9000, 300) : (7000, 400);
    }

    sealed class Channel
    {
        public long LastTick = long.MinValue;
        public int LastGear = -1;
        public double JoltLeft, JoltRight, JoltAge, JoltMs, Cooldown;
        public float SentLeft, SentRight;
        public bool Seen;
    }

    readonly IRumbleSink _sink;
    readonly Channel[] _ch = [new(), new()];

    public PadRumble(IRumbleSink sink) { _sink = sink; }

    /// <summary>Per-frame update. <paramref name="active"/> false (pause, menu, results, replay, lost focus) or <paramref name="enabled"/> false
    /// (port.json <c>rumble</c>) = motors 0 and the state cleared. A null sample = no car for this player (also 0).</summary>
    public void Update(double dt, bool enabled, bool active, RumbleSample? player1, RumbleSample? player2 = null)
    {
        bool on = enabled && active;
        Step(0, dt, on, player1);
        Step(1, dt, on, player2);
    }

    /// <summary>Immediately zero both pads (leaving the race, the network menu, exit).</summary>
    public void Stop() => Update(0, false, false, null);

    void Step(int player, double dt, bool on, RumbleSample? sample)
    {
        var c = _ch[player];
        if (!on || sample is not { } s)
        {
            c.JoltLeft = c.JoltRight = c.JoltMs = c.JoltAge = c.Cooldown = 0; c.Seen = false; c.LastGear = -1; c.LastTick = long.MinValue;
            Send(player, c, 0, 0);
            return;
        }
        c.Cooldown = Math.Max(0, c.Cooldown - dt * 1000);
        c.JoltAge += dt * 1000;
        if (!c.Seen) { c.Seen = true; c.LastTick = s.Tick; c.LastGear = s.Gear; }
        else if (s.Tick != c.LastTick)
        {
            c.LastTick = s.Tick;
            if (s.Impact > 0 && s.ImpactType != 0)
            {
                var (mag, ms) = ImpactJolt(s.ImpactType, s.Impact, s.SpeedMph);
                Jolt(c, mag / 10000.0, mag / 10000.0 * 0.7, ms);
            }
            if (s.Gear != c.LastGear)
            {
                c.LastGear = s.Gear;
                // only forward gears (>= 2) and not at standstill; strength and length from the rpm [ref:1319]
                if (s.Gear > 1 && s.SpeedMph > 0)
                {
                    var (mag, ms) = s.Rpm < 2000 ? (4200, 100) : s.Rpm < 3000 ? (5200, 150) : s.Rpm < 4000 ? (6200, 200) : (7200, 250);
                    Jolt(c, mag / 10000.0, mag / 10000.0 * 0.5, ms);
                }
            }
        }
        double fade = c.JoltMs > 0 ? Math.Max(0, 1 - c.JoltAge / c.JoltMs) : 0;
        double road = RoadMagnitude(s.SurfaceType, s.SpeedMph) / 10000.0;
        float left = (float)Math.Min(1, Math.Max(c.JoltLeft * fade, road * 0.5));
        float right = (float)Math.Min(1, Math.Max(c.JoltRight * fade, road * 0.3));
        Send(player, c, left, right);
    }

    static void Jolt(Channel c, double left, double right, int ms)
    {
        if (c.Cooldown > 0) return;   // [ref:1320]
        c.JoltLeft = left; c.JoltRight = right; c.JoltMs = ms; c.JoltAge = 0; c.Cooldown = ms / 8.0;
    }

    void Send(int player, Channel c, float left, float right)
    {
        bool zeroing = left == 0 && c.SentLeft != 0 || right == 0 && c.SentRight != 0;
        if (!zeroing && Math.Abs(left - c.SentLeft) < Epsilon && Math.Abs(right - c.SentRight) < Epsilon) return;
        c.SentLeft = left; c.SentRight = right;
        _sink.Set(player, left, right);
    }
}
