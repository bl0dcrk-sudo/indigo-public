namespace NFS.Core;

/// <summary>Flags <c>car+0x1F4</c> set from the race config class [ref:0305]
/// (cfg+4: human → 1|4, opponent → 1|2|8, traffic → 2|0x10, cop → 2|0x20, smackable/parked → 0x40).</summary>
[Flags]
public enum CarFlags : byte
{
    None = 0,
    Racer = 0x01,        // ranked racer (human or opponent) [ref:0306]
    NotLocal = 0x02,     // controlled by computer / network [ref:0307]
    LocalHuman = 0x04,   // local player (rot 64 Hz, full LOD) [ref:0308]
    Opponent = 0x08,     // racing opponent (AI racer or remote human in net games)
    Traffic = 0x10,      // AI-driven traffic
    Cop = 0x20,          // police (engine supports it, PC front-end never emits)
    Static = 0x40,       // parked / smackable (SMnn.DAT), ground contact [ref:0309]
}

/// <summary>
/// Car state <c>carObj</c> (0x684 B). Fields merged from several sources [ref:0310]
/// ; each carries its original offset. [ref:0311]
/// Values are 16.16 unless stated; speeds m/s, headings 10-bit (0x400 = 360°), ω in revolutions/s.
/// Pointers are object references (opaque until the corresponding types exist).
/// </summary>
public sealed class CarObj
{
    public const int Size = 0x684;

    [Orig(0x000, 4)] public int Id;                  // idx|0x100 racer, idx|0x200 traffic
    [Orig(0x004, 2)] public ushort PrevSlice;        // previous slice (lap counting); = slice on start/reset
    [Orig(0x006, 2)] public ushort Progress;         // progress in slices from start ("start slice") [ref:3367]
    [Orig(0x008, 0x80)] public TrackPos Trk = new(); // locator; car+0x14 slice, +0x18 poly, +0x20 VROAD, +0x24 XBID6,
                                                     // +0x28 corners, +0x76 Unk6E, +0x80 slot
    [Orig(0x088, 4)] public int DistToPlayer;        // to nearest player (LOD), 0 for players
    [Orig(0x08C, 1)] public byte Rail;               // simplified "on rails" mode (far from players)
    [Orig(0x08D, 1)] public byte Active;             // physics active
    [Orig(0x08E, 1)] public byte TookOff;            // just left the ground (1 step)
    [Orig(0x08F, 1)] public byte OrthoCount;         // steps until orthonormalisation (16 at 64 Hz / 32 at 32 Hz)
    [Orig(0x090, 1)] public byte LandingSmoke;       // smoke counter after landing (=5)
    [Orig(0x094, 4)] public int TimeScale;           // car time scale (copy of +0x218), multiplies gravity
    [Orig(0x098, 4)] public int RotAccum;            // accumulator [ref:0312]
    [Orig(0x09C, 12)] public Vec3 Pos;               // world position
    [Orig(0x0A8, 12)] public Vec3 Vel;               // world velocity
    [Orig(0x0B4, 4)] public int Mass;                // 10.0 (all cars)
    [Orig(0x0B8, 4)] public int InvMass;             // 1/10
    [Orig(0x0BC, 4)] public int SpeedApprox;         // max(|vx|,|vz|) + min/4
    [Orig(0x0C0, 4)] public int LateralOffset;       // lateral offset from slice centre
    [Orig(0x0C4, 36)] public Mat3 Orient;            // rows right +0xC4, up +0xD0, forward +0xDC
    [Orig(0x0E8, 12)] public Vec3 AngVel;            // ω world, rev/s
    [Orig(0x0F4, 4)] public int Inertia;             // 15.0
    [Orig(0x0F8, 4)] public int InvInertia;
    [Orig(0x0FC, 4)] public int RightDotN;           // right·road normal
    [Orig(0x100, 4)] public int UpDotN;              // up·road normal (cos of tilt; < 0.1 → no drive)
    [Orig(0x104, 4)] public int FwdDotN;             // fwd·road normal
    [Orig(0x108, 4)] public int HalfWidth;           // from the model
    [Orig(0x10C, 4)] public int HalfHeight;
    [Orig(0x110, 4)] public int HalfLength;
    [Orig(0x114, 4)] public int Radius;
    [Orig(0x118, 36)] public Mat3 RoadFrame;         // road right +0x118, up/normal +0x124, forward +0x130
    [Orig(0x13C, 12)] public Vec3 RoadPoint;         // slice centre or quad corner
    [Orig(0x148, 4)] public int RoadHeading;         // 0..0x3FF [ref:0313]
    [Orig(0x14C, 2)] public ushort AirTicks;         // ticks airborne/unsettled (0 = on wheels); ++ on car impulses
    [Orig(0x14E, 2)] public ushort Unk14E;           // AI think requires 0 [unknown]
    [Orig(0x150, 4)] public int F150;                // 100.0 each newton step; w.y while spinning on wheels
    [Orig(0x154, 4)] public int PrevRoadHeight;      // road height of the previous step
    [Orig(0x158, 4)] public int RoadVySmoothed;      // smoothed vertical road speed
    [Orig(0x15C, 4)] public int HeightAboveRoad;
    [Orig(0x160, 4)] public int Impact;              // impact strength of this step (fx/sound/ffb)
    [Orig(0x164, 4)] public CarObj? ImpactOther;     // other car of a car–car hit
    [Orig(0x168, 4)] public int ImpactType;          // 0x50001 car, 0x60005 object, surf|0x30000 plane, 0x40004 edge
    [Orig(0x16C, 4)] public int CollisionCooldown;   // ticks without car/object tests
    [Orig(0x170, 12)] public Vec3 LastHit;           // impact point
    [Orig(0x17C, 4)] public int CrashSlice;          // ≠0: fell off the track, reset to this slice
    [Orig(0x180, 1)] public byte SurfaceByte;        // XBID5 byte: 0x40 test objects, 0x80 test walls
    [Orig(0x184, 4)] public int SurfaceType;         // &0x3F (0 = off road, 0xE)
    [Orig(0x188, 36)] public Mat3 DriveFrame;        // right +0x188, up (ground normal) +0x194, fwd +0x1A0
    [Orig(0x1AC, 48)] public Vec3[] Corners = new Vec3[4]; // chassis corners (walls, shadow)
    [Orig(0x1DC, 12)] public string ModelName = "";  // e.g. "f355" → <name>.dat
    [Orig(0x1E8, 4)] public int PlayerIndex;         // index; == 1 → 2nd human in split screen [ref:0314]
    [Orig(0x1EC, 4)] public int HumanIndex = -1;     // index among humans
    [Orig(0x1F0, 4)] public int ListPosition;        // position in list (neighbour scans) [ref:0315]
    [Orig(0x1F4, 1)] public CarFlags Flags;
    [Orig(0x1F8, 4)] public int Lap;                 // current lap (from location)
    [Orig(0x1FC, 4)] public int LapOffset;           // 1 when the track is reversed
    [Orig(0x200, 4)] public int GripClass;           // surface grip class 0..9
    [Orig(0x204, 4)] public int Heading;             // (fwd.x, fwd.z) of the drive frame (arcade zeroes it) [ref:0316]
    [Orig(0x214, 4)] public int LaneHalfWidth;       // half width for lane occupancy
    [Orig(0x218, 4)] public int CatchUp = Fx.One;    // catch-up multiplier (glue) / time scale [ref:0317]
    [Orig(0x21C, 4)] public object? Config;          // (0x40 B) / traffic cfg (0x54 B); [+0] = model [ref:0318]
    [Orig(0x220, 4)] public int DistanceSlices;      // (lap−offset)·nSlices + slice
    [Orig(0x224, 4)] public int LastProgressTick;
    [Orig(0x228, 4)] public int LastSlice;
    [Orig(0x230, 4)] public int LapsDone;
    [Orig(0x234, 4)] public int LapStartTick;        // after finish: race time
    [Orig(0x238, 32)] public int[] LapTimes = new int[8];    // 64 Hz ticks
    [Orig(0x258, 32)] public int[] LapMaxSpeed = new int[8];
    [Orig(0x278, 4)] public int RacePosition;
    [Orig(0x27C, 4)] public int CrashCount;
    [Orig(0x280, 4)] public int RaceState;           // 0 racing, 1 DNF, 2 finished (brake 0xFF, throttle 0)
    [Orig(0x284, 4)] public int EdgeHit;             // road-edge hit flag (2 → throttle 0)
    [Orig(0x288, 4)] public int WrongWayTicks;           // ticks driven against the track (from, human cars); ≠0 → no steer assist/turbo, camera/tunnel tests [ref:0319]
    [Orig(0x28C, 12)] public Vec3 DeltaV;            // Δv of this step (world)
    [Orig(0x298, 12)] public Vec3 DeltaW;            // Δω of this step
    [Orig(0x2A4, 12)] public Vec3 LocalForce;        // after step: F.x/4, –, F.z/2 (telemetry)
    [Orig(0x2B0, 12)] public Vec3 LocalVel;          // v in drive frame (x lateral, z forward); +0x2B8 = speed
    [Orig(0x2BC, 12)] public Vec3 LocalAngVel;
    [Orig(0x2D4, 1)] public byte Throttle;           // input 0..255
    [Orig(0x2D5, 1)] public byte Brake;
    [Orig(0x2D6, 1)] public byte RequestedGear;
    [Orig(0x2D7, 1)] public byte ThrottleRamped;
    [Orig(0x2D8, 1)] public byte BrakeRamped;        // brake light when ≠0
    [Orig(0x2D9, 1)] public byte PrevGear;
    [Orig(0x2DA, 1)] public byte Gear;               // 0 = R, 1 = N, 2.. = 1st..
    [Orig(0x2DB, 1)] public byte GearChangeCounter;
    [Orig(0x2DC, 1)] public byte Handbrake;          // button 8 / rear lock
    [Orig(0x2DD, 1)] public byte Horn;
    [Orig(0x2E0, 4)] public int SteerTarget;         // −127..127
    [Orig(0x2E4, 4)] public int Steer;               // after ramp
    [Orig(0x2E8, 4)] public int ManualResetLock;     // ticks (0x30); ≠0 also blocks drive [ref:3368]
    [Orig(0x2EC, 4)] public object? Specs;           // carSpecs (0x1DC B, p<car>.dat)
    [Orig(0x2F0, 4)] public int Rpm;                 // int rpm
    [Orig(0x2F4, 4)] public int SlipState;           // 0 none, ±1 wheelspin, ±2 oversteer, ±3 drift/landing, ±4, 5 push
    [Orig(0x2F8, 4)] public int SlipFront;           // wheelspin with option 4 / front slip (brake light frame)
    [Orig(0x2FC, 4)] public int SlidingAxles;        // sim: front +1, rear +2; arcade: brake lock level 0..3
    [Orig(0x304, 4)] public int SignedSpeed;         // |v| signed / arcade target longitudinal speed
    [Orig(0x30C, 4)] public int YawTarget;           // sim: wheel angle; arcade: target yaw rate
    [Orig(0x310, 4)] public int SlipAngle;           // sim: vl.x/vl.z; arcade: target lateral speed
    [Orig(0x314, 4)] public int BodyYaw;             // extra render rotY (arcade/crash)
    [Orig(0x318, 4)] public int TurboReady;          // arcade turbo ready (u8; also "player assist 0x10/0x20") [ref:3369]
    [Orig(0x31C, 4)] public int TurboCounter;        // 1..9 active, >0x80 ready
    [Orig(0x320, 4)] public int LoadTransfer;
    [Orig(0x324, 4)] public int Skid324;             // tyre skid/squeal intensity (sound, skidmarks) 0..40
    [Orig(0x328, 4)] public int Skid328;
    [Orig(0x364, 4)] public int FrontGrip;           // sim: available front grip; arcade: |steer| helper
    [Orig(0x368, 4)] public int FrontGripDemand;     // sim: demand; arcade: copy of +0x30C
    [Orig(0x36C, 4)] public Action<object?>? TaskTrack;      // sim 6 [ref:0320]
    [Orig(0x370, 4)] public Action<object?>? InputHook;      // called by the driver task (replay record/play)
    [Orig(0x374, 4)] public Action<object?>? TaskDriver;     // sim 0x15
    [Orig(0x378, 4)] public Action<object?>? TaskLap;        // frame 0x19 (flag 1)
    [Orig(0x37C, 4)] public Action<object?>? TaskDrive;      // sim 0x1E
    [Orig(0x380, 4)] public Action<object?>? TaskPhysics;    // sim 0x1E [ref:0321]
    [Orig(0x384, 4)] public Action<object?>? TaskIntegratePos; // frame 0x1E
    [Orig(0x388, 4)] public Action<object?>? TaskIntegrateRot; // frame (human) / sim
    [Orig(0x38C, 4)] public Action<object?>? TaskCollisions; // sim 0x28
    [Orig(0x390, 4)] public Action<object?>? TaskPost;       // sim 0x32
    [Orig(0x394, 4)] public int LaneTargetOffset;    // AI target lateral offset (arcade assist: target offset)
    [Orig(0x398, 4)] public int AiTargetSpeed;       // may be negative [ref:0322]
    [Orig(0x39C, 4)] public int AiSpeed;             // AI longitudinal speed (signed)
    [Orig(0x3A0, 4)] public int AiSpeedApprox;
    [Orig(0x3A4, 4)] public int LaneRandomOffset;
    [Orig(0x3A8, 4)] public int AiFlags;             // 0x10 parked/hidden, 0x800 spawn side, 0x1000 wrong way
    [Orig(0x3AC, 4)] public int LateralOffsetCur;    // current lateral offset from slice centre
    [Orig(0x3B0, 4)] public CarObj? Anchor;          // human the traffic spawns around
    [Orig(0x3B4, 4)] public int ShiftTime;           // from <model>.dat (524 B block +0x3B4..+0x560)
    [Orig(0x3B8, 4)] public int ShiftCounter;
    [Orig(0x3BC, 4)] public int MaxRpm;
    [Orig(0x3C0, 4)] public int HornCounter;
    [Orig(0x3C4, 4)] public int SpawnDelay;
    [Orig(0x3C8, 4)] public int TrafficSpeedFactor;
    [Orig(0x3CC, 4)] public int RailLateralSpeed;
    [Orig(0x3D0, 28)] public int[] GearSpeeds = new int[7];
    [Orig(0x3EC, 28)] public int[] GearSpeedsInv = new int[7];   // FXDIV(1, …)
    [Orig(0x408, 224)] public short[] AccelCurve = new short[112]; // a(v) per 1 m/s, 8.8
    [Orig(0x4E8, 4)] public int Unk4E8;
    [Orig(0x4EC, 4)] public int RelocateTries;
    /// <summary>+0x634: palette variant of a traffic car (0..2), advanced at every spawn; render only. [ref:0323]</summary>
    [Orig(0x634, 4)] public int TrafficSkin;
    [Orig(0x4F0, 4)] public int CornerMaxSteer;
    [Orig(0x4F4, 4)] public int CornerMinSteer;
    [Orig(0x4F8, 4)] public int CornerSlope;
    [Orig(0x4FC, 4)] public int LaneScanCounter;
    [Orig(0x500, 4)] public int Lane;                // 0..13
    [Orig(0x504, 12)] public Vec3 ToTarget;
    [Orig(0x510, 4)] public int HeadingError;        // ±0x200
    [Orig(0x520, 4)] public int DirDuration;
    [Orig(0x524, 4)] public int Direction = 1;       // +1 forward, −1 reversing
    [Orig(0x528, 4)] public int DirChangeTick;
    [Orig(0x52C, 4)] public int AiHeading;
    [Orig(0x530, 4)] public int TargetHeading;
    [Orig(0x538, 4)] public int LateralSpeedAhead;
    [Orig(0x53C, 4)] public int PsychoTicks;
    [Orig(0x540, 4)] public int PsychoHits;
    [Orig(0x544, 4)] public CarObj? PsychoTarget;
    [Orig(0x548, 4)] public int SmoothedLaneOffset;  // ±0x3333 per step
    [Orig(0x54C, 4)] public int OffRoadTicks;
    [Orig(0x550, 4)] public int MaxLatAccel;
    [Orig(0x554, 4)] public int MaxYawAccel;
    [Orig(0x558, 4)] public int Glue;                // AI catch-up speed multiplier (≥1.0 copied to +0x94)
    [Orig(0x55C, 4)] public int SlipDamping;
    [Orig(0x560, 4)] public int Unk560;
    [Orig(0x564, 4)] public int OffsetChangeRate;
    [Orig(0x578, 4)] public int AutoResetCounter;    // auto reset after crash
    [Orig(0x57C, 4)] public int ShakeAmp0;           // body shake channel 0
    [Orig(0x580, 4)] public int ShakePhase0;
    [Orig(0x584, 4)] public int ShakeAmp1;
    [Orig(0x588, 4)] public int ShakePhase1;
    [Orig(0x58C, 4)] public int Crashing;            // sparks
    [Orig(0x590, 4)] public int SoundEventCount;
    [Orig(0x594, 24)] public int[] SoundEvents = new int[6];
    [Orig(0x660, 4)] public int Unk660;              // zeroed with the body roll block on reset
    [Orig(0x664, 4)] public int BodyRoll;            // render
    [Orig(0x668, 4)] public int BodyPitch;
    [Orig(0x66C, 4)] public int SmoothFz;
    [Orig(0x670, 4)] public int SmoothFx;
    [Orig(0x674, 4)] public int RollGain;            // from table[model] (×0.65 in sim) [ref:0324]
    [Orig(0x67C, 4)] public int ShakeAmplitude;

    // Convenience views of matrix rows (no extra state).
    public Vec3 Right => Orient.Row0;
    public Vec3 Up => Orient.Row1;
    public Vec3 Forward => Orient.Row2;
    public Vec3 RoadUp => RoadFrame.Row1;
    public int Slice => Trk.Slice;
}
