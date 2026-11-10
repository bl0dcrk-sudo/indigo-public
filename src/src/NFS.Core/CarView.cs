namespace NFS.Core;

/// <summary>
/// Immutable copy of the <see cref="CarObj"/> fields that presentation reads (camera, HUD, cockpit, car animation, map, audio,
/// weather) – network stage 5a.2. Published by the race host after a step; it holds no reference to the live car, so a renderer
/// or a network client cannot observe or change the simulation through it. Field names follow <see cref="CarObj"/>
/// (<see cref="Slice"/> = <c>Trk.Slice</c>).
/// </summary>
public sealed record CarView
{
    public int Id { get; init; }
    public int PlayerIndex { get; init; }
    public CarFlags Flags { get; init; }
    public byte Active { get; init; }
    public byte Rail { get; init; }
    public Vec3 Pos { get; init; }
    public Mat3 Orient { get; init; }
    public Mat3 RoadFrame { get; init; }
    public Vec3 Vel { get; init; }
    public Vec3 LocalVel { get; init; }
    public Vec3 LocalForce { get; init; }
    public int Slice { get; init; }
    public int RaceState { get; init; }
    public int UpDotN { get; init; }
    public int WrongWayTicks { get; init; }
    public int AiSpeed { get; init; }
    public int LateralOffset { get; init; }
    public int CrashSlice { get; init; }
    public int ManualResetLock { get; init; }
    public int AutoResetCounter { get; init; }
    public int CollisionCooldown { get; init; }
    public ushort AirTicks { get; init; }
    public int HalfWidth { get; init; }
    public int HalfHeight { get; init; }
    public int HalfLength { get; init; }
    public int DistToPlayer { get; init; }
    public int TrafficSkin { get; init; }
    public int Steer { get; init; }
    public byte BrakeRamped { get; init; }
    public byte ThrottleRamped { get; init; }
    public int ShakeAmp0 { get; init; }
    public int ShakeAmp1 { get; init; }
    public int ShakePhase0 { get; init; }
    public int ShakePhase1 { get; init; }
    public int SlipFront { get; init; }
    public int SlipState { get; init; }
    public int SpeedApprox { get; init; }
    public int SurfaceType { get; init; }
    public int SlidingAxles { get; init; }
    public int HeightAboveRoad { get; init; }
    public int Rpm { get; init; }
    public byte Gear { get; init; }
    public byte Horn { get; init; }
    public int TurboReady { get; init; }
    public int TurboCounter { get; init; }
    public int Skid324 { get; init; }
    public int Skid328 { get; init; }
    public int LapStartTick { get; init; }
    public int LapsDone { get; init; }
    public IReadOnlyList<int> LapTimes { get; init; } = [];
    public int RacePosition { get; init; }
    public int LastProgressTick { get; init; }

    public Vec3 Right => Orient.Row0;
    public Vec3 Up => Orient.Row1;
    public Vec3 Forward => Orient.Row2;

    /// <summary>Copies the presentation fields of <paramref name="c"/> (arrays are copied, nothing aliases the car).</summary>
    public static CarView From(CarObj c) => new()
    {
        Id = c.Id, PlayerIndex = c.PlayerIndex, Flags = c.Flags, Active = c.Active, Rail = c.Rail,
        Pos = c.Pos, Orient = c.Orient, RoadFrame = c.RoadFrame, Vel = c.Vel, LocalVel = c.LocalVel, LocalForce = c.LocalForce,
        Slice = c.Trk.Slice, RaceState = c.RaceState, UpDotN = c.UpDotN, WrongWayTicks = c.WrongWayTicks, AiSpeed = c.AiSpeed,
        LateralOffset = c.LateralOffset, CrashSlice = c.CrashSlice, ManualResetLock = c.ManualResetLock,
        AutoResetCounter = c.AutoResetCounter, CollisionCooldown = c.CollisionCooldown, AirTicks = c.AirTicks,
        HalfWidth = c.HalfWidth, HalfHeight = c.HalfHeight, HalfLength = c.HalfLength, DistToPlayer = c.DistToPlayer,
        TrafficSkin = c.TrafficSkin, Steer = c.Steer, BrakeRamped = c.BrakeRamped, ThrottleRamped = c.ThrottleRamped,
        ShakeAmp0 = c.ShakeAmp0, ShakeAmp1 = c.ShakeAmp1, ShakePhase0 = c.ShakePhase0, ShakePhase1 = c.ShakePhase1,
        SlipFront = c.SlipFront, SlipState = c.SlipState, SpeedApprox = c.SpeedApprox, SurfaceType = c.SurfaceType,
        SlidingAxles = c.SlidingAxles, HeightAboveRoad = c.HeightAboveRoad, Rpm = c.Rpm, Gear = c.Gear, Horn = c.Horn,
        TurboReady = c.TurboReady, TurboCounter = c.TurboCounter, Skid324 = c.Skid324, Skid328 = c.Skid328,
        LapStartTick = c.LapStartTick, LapsDone = c.LapsDone, LapTimes = Array.AsReadOnly(c.LapTimes.ToArray()),
        RacePosition = c.RacePosition, LastProgressTick = c.LastProgressTick,
    };

    /// <summary>Test-fixture convenience: presentation unit tests build a <see cref="CarObj"/> and pass it where a view is expected
    /// (the conversion copies). Race presentation code receives its views from <c>RaceViewFrame</c>, never through this operator.</summary>
    public static implicit operator CarView(CarObj c) => From(c);
}
