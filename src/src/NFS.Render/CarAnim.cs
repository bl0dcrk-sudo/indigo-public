using NFS.Core;
using NFS.Core.Tables;

namespace NFS.Render;

/// <summary>
/// Render-only animation state of one car (the original's car node callbacks [ref:2442]
/// and body lean). [ref:2443]
/// Reads a <see cref="CarView"/> copy of the car, never the live CarObj: the original keeps this [ref:2444]
/// state in the car (+0x648, +0x664..+0x680) but nothing in the simulation reads it, and the jitter draws from the
/// shared, so the port keeps it here with its own <see cref="EaRandom"/> stream. [ref:2445]
/// Call <see cref="Tick"/> once per simulation tick; <see cref="CarInstance.Polys"/> applies the result.
/// </summary>
public sealed class CarAnim
{
    /// <summary>Car type (index, race config +0): 4 = Indigo (steers at the full angle from every view). [ref:2446]</summary>
    public int CarType { get; }
    /// <summary>Front wheels steer at the full angle from every view (original: car type 4, the Indigo; <c>CarTraits.FullAngleSteer</c>).</summary>
    public bool FullAngleSteer { get; }
    /// <summary>Wheel steering input <c>CarObj+0x2E4</c> (−127..127, after the ramp).</summary>
    public int Steer { get; set; }
    /// <summary>Roll of the body, 16-bit angle (<c>car+0x668</c>, rotation about Z). [ref:2447]</summary>
    public int Roll { get; set; }
    /// <summary>Pitch of the body, 16-bit angle (<c>car+0x664</c>, rotation about X). [ref:2448]</summary>
    public int Pitch { get; set; }
    /// <summary>Rim texture frame 0..3 (m000, m100, m200, m300) of the front wheels (parts 0xC/0xE).</summary>
    public int FrontRimFrame { get; set; }
    /// <summary>Rim texture frame of the rear wheels (parts 0x10/0x12).</summary>
    public int RearRimFrame { get; set; }
    /// <summary>Brake light on (frame 1 of the animated polygons of part 11).</summary>
    public bool BrakeLight { get; set; }
    /// <summary>Vertical offset of each wheel (front right, front left, rear right, rear left), 16.16 m.</summary>
    public int[] WheelY { get; } = new int[4];

    public const int RollLimit = 0x4CCC, PitchLimit = 0x4000, MaxSteerScale = 0x2A;

    readonly EaRandom _rand;
    readonly int _modelBodyLean;
    int _counter;                 // +0x648
    int _smoothFz, _smoothFx;     // +0x66C / +0x670
    int _rawRoll, _rawPitch;      // +0x668 / +0x664 before the render clamp

    /// <summary>Vanilla rule: only car type 4 steers at the full angle; single source for <c>CarTraits.FullAngleSteer</c>.</summary>
    public static bool VanillaFullAngleSteer(int carType) => carType == 4;

    public CarAnim(int carType, int seed = 0, bool? fullAngleSteer = null)
    {
        CarType = carType;
        FullAngleSteer = fullAngleSteer ?? VanillaFullAngleSteer(carType);
        _rand = new EaRandom(seed);
    }

    CarAnim(int carType, int seed, int lean, bool? fullAngleSteer) : this(carType, seed, fullAngleSteer) => _modelBodyLean = lean;

    /// <summary>Animation for a race car: lean gain (arcade model; 0 for types ≥ 0xF). [ref:2449]</summary>
    public static CarAnim For(int carType, GameTables t, int seed = 0, bool? fullAngleSteer = null) =>
        new(carType, seed, (uint)carType < 0xF && t.ModelBody.Length >= 2 * carType + 2 ? t.ModelBody[carType * 2] : 0, fullAngleSteer);

    /// <summary>One simulation tick after the whole step (the original runs the lean in the tick, and the frames [ref:2450]
    /// jitter in the render callbacks once per frame). [ref:2451]</summary>
    public void Tick(CarView c, GameTables t)
    {
        Steer = c.Steer;
        _counter++;
        BrakeLight = c.BrakeRamped != 0;
        if (c.Rail == 0 && c.Active != 0) Lean(c, t);
        (FrontRimFrame, RearRimFrame) = RimFrames(c, _counter);
        for (int i = 0; i < 4; i++) WheelY[i] = WheelJitter(c, t, front: i < 2);
        (Roll, Pitch) = ClampLean(_rawRoll, _rawPitch, c.RaceState == 2 && c.SpeedApprox < 0x20000);
    }

    /// <summary>Body lean (for cars nearer than 60 m; on the ground only). [ref:2452]</summary>
    void Lean(CarView c, GameTables t)
    {
        if (c.DistToPlayer >= 0x3C0000 || t.BodyShakeWave.Length == 0) { _rawRoll = _rawPitch = 0; return; }
        int d = c.LocalForce.Z - _smoothFz;                  // +0x2AC
        if (d > 0x8000) _smoothFz += 0x8000;
        else if (d < -0x8000) _smoothFz += d;                // as in the original: small steps leave the value unchanged
        d = c.LocalForce.X - _smoothFx;                      // +0x2A4
        _smoothFx += Math.Clamp(d, -0x18000, 0x18000);
        if (c.AirTicks != 0) { _rawRoll = _rawPitch = 0; return; }
        var wave = t.BodyShakeWave;
        int roll = _smoothFx + Fx.DivPow2(c.ShakeAmp1 * wave[(uint)c.ShakePhase1 < wave.Length ? c.ShakePhase1 : 0], 5);
        roll = Fx.Mul(roll, _modelBodyLean);
        int pitch = Fx.DivPow2(c.ShakeAmp0 * wave[(uint)c.ShakePhase0 < wave.Length ? c.ShakePhase0 : 0], 5) - _smoothFz;
        pitch = Fx.Mul(pitch, _modelBodyLean);
        int amp = ShakeAmplitude(c, t, CarType);
        int slipAmp = (c.SlipFront != 0 || c.SlipState != 0) ? Math.Max(amp, 0xE666) : amp;   // arcade
        _rawPitch = pitch + (int)(((long)(_rand.Next() >> 12) * slipAmp + 0x8000 >> 16) / 4096);
        _rawRoll = roll + (int)(((long)(_rand.Next() >> 12) * slipAmp + 0x8000 >> 16) / 4096);
    }

    /// <summary>Written to +0x67C. [ref:2453]</summary>
    public static int ShakeAmplitude(CarView c, GameTables t, int carType)
    {
        int cap = (uint)carType < 0xF && t.ModelBody.Length >= 2 * carType + 2 ? t.ModelBody[carType * 2 + 1] : 0;
        int v = Math.Min(c.SpeedApprox / 32, cap);
        int surf = (uint)c.SurfaceType < (uint)t.SurfShake.Length ? t.SurfShake[c.SurfaceType] : 0;
        int amp = v < surf ? v : surf;
        if (c.SpeedApprox > 0x430000) amp = Math.Max((c.SpeedApprox - 0x430000) / 128, amp);
        return amp;
    }

    /// <summary>Rim frames (front, rear) from |v| (+0x2B8) and the tick counter (+0x648). [ref:2454]</summary>
    public static (int Front, int Rear) RimFrames(CarView c, int counter)
    {
        int v = Math.Abs(c.LocalVel.Z);
        int f = v > 0x140000 ? 3
            : v > 0x70000 ? (counter & 1) + 1
            : v > 0x28000 ? ((counter & 2) >> 1) + 1
            : v >= 0x3334 ? ((counter & 4) >> 2) + 1 : 0;
        int front = f, rear = f;
        if (c.SlipState != 0) rear = 3;       // +0x2F4
        if (c.SlipFront != 0) front = 3;      // +0x2F8
        if (c.RaceState == 2 && c.SpeedApprox < 0x20000) { front = 0; rear = 0; }   // finished and stopped (+0x2FC = 3)
        else
        {
            if ((c.SlidingAxles & 1) != 0) front = 0;                               // +0x2FC bits: locked wheels do not blur
            if ((c.SlidingAxles & 2) != 0) rear = 0;
        }
        return (front, rear);
    }

    /// <summary>Clamps: roll ±0x4CCC, pitch ±0x4000; pitch 0 for a finished, stopped car. [ref:2455]</summary>
    public static (int Roll, int Pitch) ClampLean(int roll, int pitch, bool finishedStopped) =>
        (Math.Clamp(roll, -RollLimit, RollLimit), finishedStopped ? 0 : Math.Clamp(pitch, -PitchLimit, PitchLimit));

    /// <summary>Vertical wobble of a wheel node (node+0x84), 16.16 m. Amplitude from +0x67C and [ref:2456]
    /// sag from the height above the road +0x15C. [ref:2457]</summary>
    int WheelJitter(CarView c, GameTables t, bool front)
    {
        int amp = ShakeAmplitude(c, t, CarType);
        int sag = 0;
        if (amp > 0x28F) sag = Math.Min(Math.Abs(c.HeightAboveRoad) + 0x28F, 0xA3D);
        if ((front ? c.SlipFront : c.SlipState) != 0 && amp < 0xB334) amp = 0xB333;
        long Draw() => ((long)(_rand.Next() >> 8) * amp + 0x8000 >> 16) / 4096;
        return Draw() < 1 ? (int)Draw() - sag : -sag - (int)Draw();
    }

    /// <summary>Camera-dependent scale of the front wheel angle (camera mode ≠ 7): [ref:2458]
    /// 0x2A for the Indigo (type 4) or no steering, otherwise <c>|az|&gt;&gt;8</c> within ±0x31FF of the rear view,
    /// 0 around the sides, <c>(0x7F00−|az|)&gt;&gt;8</c> towards the front; capped at 0x2A. The wheel angle is scale × steer (16-bit).</summary>
    public static int SteerScale(int carType, int steer, int az) => SteerScale(VanillaFullAngleSteer(carType), steer, az);

    /// <summary><see cref="SteerScale(int,int,int)"/> with the trait <see cref="FullAngleSteer"/> instead of the car number.</summary>
    public static int SteerScale(bool fullAngleSteer, int steer, int az)
    {
        if (steer == 0 || fullAngleSteer) return MaxSteerScale;
        int v;
        if (az > -0x3200 && az < 0x3200) v = Math.Abs(az);
        else if (az > 0x4600) v = 0x7F00 - az;
        else v = az < -0x4600 ? az + 0x7F00 : 0;
        return Math.Min(v >> 8, MaxSteerScale);
    }
}
