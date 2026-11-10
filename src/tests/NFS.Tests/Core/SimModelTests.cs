using NFS.Core;
using NFS.Data.Car;
using NFS.Data.Track;
using NFS.Tests.Data;

namespace NFS.Tests.Core;

/// <summary>
/// The SIM driving model (<see cref="SimModel"/>) [ref:3097]
/// on F355 (sim derive, no [ref:3098]
/// set-up). Expected values come from an independent mechanical [ref:3482]
/// reference implementation of the original (the arcade-model tests use the same discipline); the behaviour tests need no reference.
/// There is NO recording of the original running the sim model yet, so bit-exactness against the original is
/// unconfirmed. [ref:3099] [ref:3483]
/// </summary>
public class SimModelTests
{
    private static DerivedCarSpecs F355() =>
        CarSpecsDerive.Derive(CarSpecs.Parse(GameData.ReadRaw("GAMEDATA/SIM/CARDATA/PF355.DAT")), new DeriveOptions(false, false));

    // road slices of the reference: 0, 1 flat; 2 crest (pitch +); 3 dip (pitch −); s8, 126 ≈ 1.0
    private static TrackSlice[] Slices()
    {
        static TrackSlice S(sbyte[] up, sbyte[] fw) => new(0, 0, 0, up, fw, [126, 0, 0], 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return [S([0, 126, 0], [0, 0, 126]), S([0, 126, 0], [0, 0, 126]), S([0, 125, -15], [0, 15, 125]), S([0, 126, 0], [0, -15, 125])];
    }

    private static void Put(CarObj c, int off, int v)
    {
        switch (off)
        {
            case 0xA8: c.Vel.X = v; break; case 0xAC: c.Vel.Y = v; break; case 0xB0: c.Vel.Z = v; break;
            case 0xE8: c.AngVel.X = v; break; case 0xEC: c.AngVel.Y = v; break; case 0xF0: c.AngVel.Z = v; break;
            case 0x14: c.Trk.Slice = v; break; case 0x14C: c.AirTicks = (ushort)v; break; case 0x150: c.F150 = v; break;
            case 0x184: c.SurfaceType = v; break; case 0x1F4: c.Flags = (CarFlags)v; break; case 0x94: c.TimeScale = v; break;
            case 0xE0: c.Orient.M21 = v; break;
            case 0x2B8: c.LocalVel.Z = v; break;
            case 0x2D7: c.ThrottleRamped = (byte)v; break; case 0x2D8: c.BrakeRamped = (byte)v; break;
            case 0x2D9: c.PrevGear = (byte)v; break; case 0x2DA: c.Gear = (byte)v; break; case 0x2DC: c.Handbrake = (byte)v; break;
            case 0x2E4: c.Steer = v; break; case 0x2E8: c.ManualResetLock = v; break; case 0x2F0: c.Rpm = v; break;
            case 0x2F4: c.SlipState = v; break; case 0x304: c.SignedSpeed = v; break; case 0x310: c.SlipAngle = v; break;
            case 0x320: c.LoadTransfer = v; break; case 0x200: c.GripClass = v; break;
            default: throw new ArgumentOutOfRangeException(nameof(off), off, null);
        }
    }

    private static int Get(CarObj c, int off) => off switch
    {
        0xA8 => c.Vel.X, 0xAC => c.Vel.Y, 0xB0 => c.Vel.Z, 0xE8 => c.AngVel.X, 0xEC => c.AngVel.Y, 0xF0 => c.AngVel.Z,
        0x28C => c.DeltaV.X, 0x290 => c.DeltaV.Y, 0x294 => c.DeltaV.Z, 0x298 => c.DeltaW.X, 0x29C => c.DeltaW.Y, 0x2A0 => c.DeltaW.Z,
        0x2A4 => c.LocalForce.X, 0x2AC => c.LocalForce.Z, 0x2F0 => c.Rpm, 0x2F4 => c.SlipState, 0x2FC => c.SlidingAxles,
        0x304 => c.SignedSpeed, 0x310 => c.SlipAngle, 0x320 => c.LoadTransfer, 0x324 => c.Skid324, 0x328 => c.Skid328,
        0x364 => c.FrontGrip, 0x368 => c.FrontGripDemand, 0x200 => c.GripClass,
        _ => throw new ArgumentOutOfRangeException(nameof(off), off, null),
    };

    private static readonly int[] Out = [0xA8, 0xAC, 0xB0, 0xE8, 0xEC, 0xF0, 0x28C, 0x290, 0x294, 0x298, 0x29C, 0x2A0, 0x2A4, 0x2AC,
        0x2F0, 0x2F4, 0x2FC, 0x304, 0x310, 0x320, 0x324, 0x328, 0x364, 0x368, 0x200];

    private static void AssertSeq(int[] expected, int[] got) =>
        Assert.True(expected.SequenceEqual(got), $"got {string.Join(", ", got)}, expected {string.Join(", ", expected)}");

    private static CarObj NewCar(int[] fields)
    {
        var c = new CarObj { DriveFrame = Mat3.Identity, Orient = Mat3.Identity };
        for (int i = 0; i < fields.Length; i += 2) Put(c, fields[i], fields[i + 1]);
        return c;
    }

    // name, car [offset, value, ...], env [model, race tick, slice, throttle, brake, steer abs], expected (Out order)
    public static TheoryData<string, int[], int[], int[]> StepCases => new()
    {
        { "launch", new[] { 0xB0, 65536, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 248, 0x2D9, 2, 0x2DA, 2, 0x2F0, 3500 }, new[] { 2, 0, 0, 65536, 0, 0 }, new[] { 0, 0, 75033, 0, 0, 0, 0, 0, 303924, 0, 0, 0, 0, 151962, 3495, 1, 0, 65536, 0, 161079, 0, 1933638, 924030, 0, 0 } },
        { "accel_3rd_60kmh", new[] { 0xB0, 1114112, 0x14, 1, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 248, 0x2D9, 2, 0x2DA, 4, 0x2F0, 4200 }, new[] { 2, 0, 1, 65536, 0, 0 }, new[] { 0, 0, 1120782, 0, 0, 0, 0, 0, 213446, 0, 0, 0, 0, 106723, 4000, 0, 0, 1114112, 0, 113126, 0, 0, 955453, 0, 0 } },
        { "corner_right_30ms", new[] { 0xB0, 1966080, 0x14, 1, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 192, 0x2D9, 2, 0x2DA, 5, 0x2E4, 60, 0x2F0, 4500 }, new[] { 2, 0, 1, 49152, 0, 15616 }, new[] { 28716, 0, 1967801, 0, 3731, 0, 918924, 0, 55095, 0, 119409, 0, 229731, 27547, 4645, 0, 0, 1966080, 0, 29200, 326532, 0, 979836, 1306368, 0 } },
        { "corner_left_oversteer", new[] { 0xA8, 262144, 0xB0, 2293760, 0xEC, -16384, 0x14, 1, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 248, 0x2D8, 0, 0x2D9, 2, 0x2DA, 5, 0x2E4, -90, 0x2F0, 5000 }, new[] { 2, 0, 1, 65536, 0, 23296 }, new[] { 201916, 0, 2295423, 0, -15617, 0, -1927325, 0, 53231, 0, 15844, 0, -481831, 26615, 5200, 2, 0, 2316828, 7489, 28212, 329685, 126426, 989297, -1318982, 0 } },
        { "brake_lock_non_abs", new[] { 0xB0, 2621440, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 0, 0x2D8, 248, 0x2D9, 2, 0x2DA, 5, 0x2F0, 4500 }, new[] { 2, 0, 0, 0, 65536, 0 }, new[] { 0, 0, 2601910, 0, 0, 0, 0, 0, -624990, 0, 0, 0, 0, -312495, 5028, 0, 1, 2621440, 0, -331244, 40982351, 0, 0, 0, 0 } },
        { "brake_abs_model5", new[] { 0xB0, 2621440, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 0, 0x2D8, 248, 0x2D9, 2, 0x2DA, 5, 0x2F0, 4500 }, new[] { 5, 8, 0, 0, 65536, 0 }, new[] { 0, 0, 2600436, 0, 0, 0, 0, 0, -672152, 0, 0, 0, 0, -336076, 5028, 0, 0, 2621440, 0, -356240, 0, 0, 998802, -960689, 0 } },
        { "handbrake_turn", new[] { 0xB0, 1703936, 0x14, 1, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 128, 0x2D9, 2, 0x2DA, 3, 0x2DC, 1, 0x2E4, 80, 0x2F0, 4500 }, new[] { 2, 0, 1, 32768, 0, 20480 }, new[] { 28015, 0, 1692416, 0, 3640, 0, 896488, 0, -368657, 0, 116493, 0, 224122, -184328, 4810, 0, 2, 1703936, 0, -195388, 324021, 26180825, 972301, 1296322, 0 } },
        { "reverse_gear", new[] { 0xB0, -131072, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 248, 0x2D9, 2, 0x2DA, 0, 0x2F0, 3000 }, new[] { 2, 0, 0, 65536, 0, 0 }, new[] { 27672, 0, -140569, 0, 3595, 0, 885531, 0, -303922, 0, 115069, 0, 221382, -151961, 2925, 1, 0, -131072, 0, -161078, 307935, 1726923, 924030, 1231965, 0 } },
        { "neutral_coast", new[] { 0xB0, 1048576, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 0, 0x2D9, 2, 0x2DA, 1, 0x2E4, 10, 0x2F0, 2000 }, new[] { 2, 0, 0, 0, 0, 0 }, new[] { 6890, 0, 1037958, 0, 895, 0, 222727, 0, -3946, 0, 28942, 0, 55681, -1973, 1800, 0, 0, 1048576, 0, -2091, 0, 0, 953590, 222863, 0 } },
        { "standstill_wheel_turned", new[] { 0xA8, 8192, 0xB0, 16384, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 0, 0x2D9, 2, 0x2DA, 2, 0x2E4, 100, 0x2F0, 1500 }, new[] { 2, 0, 0, 0, 0, 32768 }, new[] { 2930, 0, 25205, 0, 312, 0, -142204, 0, 334713, 0, 10004, 0, -35551, 167356, 0, 1, 0, 18735, 0, 177397, 0, 177641, 924030, -34654, 0 } },
        { "airborne", new[] { 0xB0, 1572864, 0x14C, 3, 0x150, 7340032, 0x1F4, 4, 0x94, 65536, 0x2D9, 2, 0x2DA, 3, 0x2F0, 4000 }, new[] { 2, 0, 0, 65536, 0, 0 }, new[] { 0, 0, 1572864, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4500, 0, 0, 1572864, 0, 0, 0, 0, 0, 0, 0 } },
        { "crest_high_speed", new[] { 0xB0, 3670016, 0x14, 2, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0x2D7, 248, 0x2D9, 2, 0x2DA, 6, 0x2F0, 6000 }, new[] { 2, 0, 2, 65536, 0, 0 }, new[] { 0, 0, 3674388, 0, 0, 0, 0, 0, 139910, 0, 0, 0, 0, 69955, 6200, 0, 0, 3670016, 0, 74152, 0, 0, 1852694, 0, 0 } },
        { "wet_surface_class5", new[] { 0xB0, 1179648, 0x14, 1, 0x150, 6553600, 0x184, 4, 0x1F4, 4, 0x94, 65536, 0x2D7, 192, 0x2D9, 2, 0x2DA, 3, 0x2E4, 40, 0x2F0, 4200 }, new[] { 2, 0, 1, 49152, 0, 10240 }, new[] { 26984, 0, 1186826, 0, 3506, 0, 863488, 0, 229713, 0, 112205, 0, 215872, 114856, 4400, 0, 0, 1179648, 0, 121748, 0, 0, 947735, 871789, 1 } },
        { "tilted_road_slope", new[] { 0xB0, 1179648, 0x14, 3, 0x150, 6553600, 0x1F4, 4, 0x94, 65536, 0xE0, 8192, 0x2D7, 192, 0x2D9, 2, 0x2DA, 3, 0x2F0, 4200 }, new[] { 2, 0, 3, 49152, 0, 0 }, new[] { 0, 0, 1181682, 0, 0, 0, 0, 0, 65089, 0, 0, 0, 0, 32544, 4400, 0, 0, 1179648, 0, 77915, 0, 327188, 420069, 0, 0 } },
    };

    [GameTheory]
    [MemberData(nameof(StepCases))]
    public void Step_MatchesReference(string name, int[] car, int[] env, int[] expected)
    {
        var c = NewCar(car);
        var e = new SimEnv { Model = env[0], RaceTick = env[1], Slices = Slices() };
        var sig = new DriveSignals(env[3], env[4], env[5], false);
        SimModel.Step(c, F355(), e, _ => sig);
        int[] got = Out.Select(o => Get(c, o)).ToArray();
        Assert.True(expected.SequenceEqual(got), $"{name}: got {string.Join(", ", got)}");
    }

    // x, z, angle (1.0 = 1/256 turn), expected ox, oz
    public static TheoryData<int[]> RotateCases => new()
    {
        new[] { 65536, 0, 4608, 65536, 113 },
        new[] { -327680, 196608, -9029, -327015, 197705 },
        new[] { 30583, -74565, 74565, 32650, -73682 },
        new[] { 0, 262144, 4194304, -262144, 0 },
    };

    [GameTheory]
    [MemberData(nameof(RotateCases))]
    public void Rotate2d_MatchesReference(int[] r)
    {
        _ = FxMath.Tables;
        SimModel.Rotate2d(r[0], r[1], r[2], out int ox, out int oz);
        Assert.Equal((r[3], r[4]), (ox, oz));
    }

    // vx, vz, load, expected Fy, Fz, skid
    public static TheoryData<int[]> TireCurveCases => new()
    {
        new[] { -3276800, -6553600, 589824, -217600, -435200, 6782976 },
        new[] { 100, -50, 1000, 39, -19, 0 },
        new[] { 8192, 4096, 131072, 101920, 50960, 0 },
        new[] { 524288, 65536, 3145728, 2668544, 333568, 0 },
    };

    [GameTheory]
    [MemberData(nameof(TireCurveCases))]
    public void TireCurve_MatchesReference(int[] r)
    {
        var t = new SimAxle { Vx = r[0], Vz = r[1], Load = r[2] };
        SimModel.TireCurve(ref t);
        Assert.Equal((r[3], r[4], r[5]), (t.Fy, t.Fz, t.Skid));
    }

    // [model, front, gear, throttle, slip state, grip class, vl.z, Fy, Fz, load, df front, df rear], expected [Fy, Fz, skid, +0x364, +0x368]
    public static TheoryData<int[], int[]> WheelSlipCases => new()
    {
        { new[] { 2, 1, 2, 248, 0, 0, 1048576, 196608, 131072, 589824, 65536, 65536 }, new[] { 196608, 131072, 0, 589824, 229376 } },
        { new[] { 2, 1, 2, 248, 0, 0, 1048576, 589824, -393216, 327680, 69632, 65536 }, new[] { 261990, -174660, 339968, 348160, 688128 } },
        { new[] { 2, 0, 2, 248, 0, 0, 1048576, 131072, 589824, 393216, 65536, 67584 }, new[] { 79656, 358452, 217088, 0, 0 } },
        { new[] { 2, 0, 2, 248, 0, 4, 1048576, 131072, 589824, 393216, 65536, 67584 }, new[] { 73942, 332739, 217088, 0, 0 } },
        { new[] { 2, 0, 3, 16, 1, 1, 196608, -589824, -131072, 262144, 65536, 65536 }, new[] { -217296, -48288, 360448, 0, 0 } },
        { new[] { 14, 1, 3, 16, 1, 1, 196608, -589824, -131072, 262144, 65536, 65536 }, new[] { -589824, -131072, 0, 0, 0 } },
    };

    [GameTheory]
    [MemberData(nameof(WheelSlipCases))]
    public void WheelSlip_MatchesReference(int[] i, int[] expected)
    {
        var c = new CarObj { Gear = (byte)i[2], ThrottleRamped = (byte)i[3], SlipState = i[4], GripClass = i[5] };
        c.LocalVel.Z = i[6];
        var t = new SimAxle { Front = i[1] != 0, Fy = i[7], Fz = i[8], Load = i[9] };
        SimModel.WheelSlip(c, F355(), new SimEnv { Model = i[0], DfFront = i[10], DfRear = i[11] }, ref t);
        AssertSeq(expected, [t.Fy, t.Fz, t.Skid, c.FrontGrip, c.FrontGripDemand]);
    }

    // [slice, vl.z, grip class, brake, model, downforce set-up front, rear], expected [mu, df front, df rear]
    public static TheoryData<int[], int[]> GripFactorCases => new()
    {
        { new[] { 0, 262144, 0, 0, 2, 65536, 65536 }, new[] { 65536, 65536, 65536 } },
        { new[] { 1, 2097152, 0, 0, 2, 65536, 65536 }, new[] { 65536, 67616, 68640 } },
        { new[] { 2, 3145728, 1, 248, 0, 73728, 57344 }, new[] { 115176, 69040, 71008 } },
        { new[] { 2, 3145728, 1, 248, 2, 73728, 57344 }, new[] { 115176, 69040, 69616 } },
        { new[] { 3, 5242880, 4, 0, 2, 65536, 65536 }, new[] { -39921, 70736, 73296 } },
        { new[] { 2, 1835008, 9, 64, 14, 65536, 65536 }, new[] { 68660, 67356, 68252 } },
    };

    [GameTheory]
    [MemberData(nameof(GripFactorCases))]
    public void GripFactors_MatchesReference(int[] i, int[] expected)
    {
        var c = new CarObj { GripClass = i[2], BrakeRamped = (byte)i[3] };
        c.Trk.Slice = i[0];
        c.LocalVel.Z = i[1];
        var e = new SimEnv { Model = i[4], DownforceFront = i[5], DownforceRear = i[6], Slices = Slices() };
        SimModel.GripFactors(c, F355(), e);
        AssertSeq(expected, [e.GripMu, e.DfFront, e.DfRear]);
    }

    // [front, model, drive, load, steer, vx, vz, throttle, brake, gear, handbrake, slip state, vl.z, race tick],
    // expected [Fy, Fz, drive, skid, +0x2FC, +0x324, +0x328]  (df 1.0+, (0x1234, -10, 0x500)) [ref:3100]
    public static TheoryData<int[], int[]> TiresCases => new()
    {
        { new[] { 1, 2, 131072, 589824, 0, -65536, -4194304, 248, 0, 2, 0, 0, 4194304, 0 }, new[] { -61236, 393216, 393216, 0, 0, 0, 0 } },
        { new[] { 1, 2, 131072, 589824, 6144, -196608, -4194304, 248, 0, 2, 0, 0, 4194304, 0 }, new[] { -177515, 393621, 393216, 0, 0, 0, 0 } },
        { new[] { 0, 2, 65536, 458752, 0, 131072, -4194304, 248, 0, 2, 0, 0, 4194304, 0 }, new[] { 131072, 196608, 196608, 0, 0, 0, 0 } },
        { new[] { 1, 2, -589824, 327680, 0, -8192, -2359296, 0, 248, 3, 0, 0, 2359296, 0 }, new[] { -992, -285696, -1769472, 2033664, 1, 2033664, 0 } },
        { new[] { 1, 2, -589824, 327680, 0, -8192, -2359296, 0, 248, 3, 1, 0, 2359296, 0 }, new[] { -992, -285696, -1769472, 2033664, 1, 2033664, 0 } },
        { new[] { 0, 5, -589824, 327680, 0, -8192, -2359296, 0, 248, 3, 0, 0, 2359296, 4 }, new[] { -8192, -327680, -327680, 196608, 0, 0, 196608 } },
        { new[] { 0, 7, -589824, 327680, 0, -8192, -2359296, 0, 248, 3, 0, 0, 2359296, 5 }, new[] { -8192, -327680, -327680, 196608, 0, 0, 196608 } },
        { new[] { 0, 2, 65536, 458752, 0, 8192, -81920, 248, 0, 3, 1, 0, 81920, 0 }, new[] { 39136, -391360, 196608, 0, 2, 0, 0 } },
        { new[] { 1, 2, 65536, 458752, 0, 8192, -81920, 248, 0, 3, 0, 0, 32768, 0 }, new[] { 8192, 196608, 196608, 0, 0, 0, 0 } },
        { new[] { 0, 2, 1572864, 458752, 0, 8192, 3145728, 248, 0, 0, 0, 0, 3145728, 0 }, new[] { 8192, 458752, 458752, 0, 0, 0, 0 } },
    };

    [GameTheory]
    [MemberData(nameof(TiresCases))]
    public void Tires_MatchesReference(int[] i, int[] expected)
    {
        var c = new CarObj { ThrottleRamped = (byte)i[7], BrakeRamped = (byte)i[8], Gear = (byte)i[9], Handbrake = (byte)i[10], SlipState = i[11] };
        c.LocalVel.Z = i[12];
        var e = new SimEnv { Model = i[1], RaceTick = i[13], DfFront = 0x10400, DfRear = 0x10800, GravLocal = new Vec3(0x1234, -0xA0000, 0x500) };
        var t = new SimAxle { Front = i[0] != 0, Drive = i[2], Load = i[3], Steer = i[4], Vx = i[5], Vz = i[6] };
        SimModel.Tires(c, F355(), e, ref t);
        AssertSeq(expected, [t.Fy, t.Fz, t.Drive, t.Skid, c.SlidingAxles, c.Skid324, c.Skid328]);
    }

    // ---- behaviour (no reference needed) ----

    [GameFact]
    public void TireCurve_OpposesTyreVelocity_AndIsZeroAtRest()
    {
        var rest = new SimAxle { Load = 0x90000 };
        SimModel.TireCurve(ref rest);
        Assert.Equal((0, 0, 0), (rest.Fy, rest.Fz, rest.Skid));
        var t = new SimAxle { Vx = 0x30000, Vz = -0x200000, Load = 0x90000 };   // tyre velocity = −v_local: the force acts the same way
        SimModel.TireCurve(ref t);
        Assert.True(t.Fy > 0 && t.Fz < 0);
        Assert.True(Math.Abs(t.Fz) < t.Load);                                   // at most 7/8 of the load
    }

    [GameFact]
    public void WheelSlip_Model0xE_LeavesForcesUntouched_AndOthersShrinkTowardsTheCircle()
    {
        var c = new CarObj { Gear = 3, GripClass = 0 };
        c.LocalVel.Z = 0x100000;
        var raw = new SimAxle { Front = true, Fy = 0x200000, Fz = 0x180000, Load = 0x90000 };
        var t = raw;
        SimModel.WheelSlip(c, F355(), new SimEnv { Model = 0xE }, ref t);
        Assert.Equal((raw.Fy, raw.Fz), (t.Fy, t.Fz));
        t = raw;
        SimModel.WheelSlip(c, F355(), new SimEnv { Model = 2 }, ref t);
        Assert.True(Math.Abs(t.Fy) < Math.Abs(raw.Fy) && Math.Abs(t.Fz) < Math.Abs(raw.Fz));
        Assert.Equal(0x90000, c.FrontGrip);
        Assert.True(c.FrontGripDemand > 0x90000);                                // demand beyond the available grip
    }

    [GameFact]
    public void GripFactors_NoAeroBelow5ms_AndGrowWithSpeed()
    {
        var c = new CarObj();
        c.LocalVel.Z = 0x50000;
        var e = new SimEnv { Model = 2 };
        SimModel.GripFactors(c, F355(), e);
        Assert.Equal((Fx.One, Fx.One), (e.DfFront, e.DfRear));
        Assert.Equal(Fx.One, e.GripMu);                                          // grip class 0, no slices
        c.LocalVel.Z = 0x400000;
        SimModel.GripFactors(c, F355(), e);
        Assert.True(e.DfFront > Fx.One && e.DfRear > e.DfFront);                 // the rear coefficient has the larger factor (x1.5)
    }

    [GameFact]
    public void Step_StraightLaunch_AcceleratesAlongForwardAxis_WithoutSideForce()
    {
        var c = NewCar([0x2DA, 2, 0x2D7, 0xF8, 0x2F0, 3500, 0x150, 0x640000, 0x1F4, 4, 0x94, Fx.One, 0xB0, Fx.One]);
        var sig = new DriveSignals(Fx.One, 0, 0, false);
        for (int i = 0; i < 30; i++) SimModel.Step(c, F355(), new SimEnv { Model = 2 }, _ => sig);
        Assert.True(c.Vel.Z > 4 * Fx.One, $"vz {c.Vel.Z / 65536.0}");
        Assert.Equal(0, c.Vel.X);
        Assert.Equal(0, c.AngVel.Y);
    }

    [GameFact]
    public void Step_AirborneOrLocked_DoesNotIntegrate()
    {
        var c = NewCar([0x14C, 3, 0x150, 0x700000, 0x2DA, 3, 0x2F0, 4000, 0xB0, 0x180000, 0x1F4, 4, 0x94, Fx.One]);
        c.Skid324 = c.Skid328 = 5;
        SimModel.Step(c, F355(), new SimEnv { Model = 2 }, _ => new DriveSignals(Fx.One, 0, 0, false));
        Assert.Equal(0x180000, c.Vel.Z);                                         // no force integrated in the air (F150 > S.14C)
        Assert.Equal((0, 0), (c.Skid324, c.Skid328));
        var locked = NewCar([0x2E8, 5, 0x2DA, 3, 0xB0, 0x180000, 0x1F4, 4, 0x94, Fx.One]);
        SimModel.Step(locked, F355(), new SimEnv { Model = 2 }, _ => new DriveSignals(Fx.One, 0, 0, false));
        Assert.Equal(0x180000, locked.Vel.Z);
    }
}
