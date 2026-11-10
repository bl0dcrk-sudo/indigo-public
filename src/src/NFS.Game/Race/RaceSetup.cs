using System.Buffers.Binary;
using NFS.Data.FrontEnd;

namespace NFS.Game.Race;

/// <summary>Race setup read from the 0x5B0-byte config block (same layout as the [ref:2289]
/// replay.rpy header, see <see cref="RaceConfig"/>) plus key bindings (tags 0x23..0x7A). [ref:2290]</summary>
public sealed class RaceSetup
{
    public const int MaxCars = 16;
    public const int KeyCount = 0x7B - 0x23;

    public byte[] Block { get; } = new byte[RaceConfig.Size];
    /// <summary>Indexed by tag − 0x23; value = device | code &lt;&lt; 8.</summary>
    public int[] KeyBindings { get; } = new int[KeyCount];

    public RaceConfig Config => new(Block);

    public int Get(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Block.AsSpan(offset));
    public void Set(int offset, int v) => BinaryPrimitives.WriteInt32LittleEndian(Block.AsSpan(offset), v);

    public int RaceType => Get(0x00);
    public int NumLaps => Get(0x04);
    public int Skill => Get(0x08);
    public int Style => Get(0x0C);
    public int CommMode => Get(0x10);
    public int LocalCar => Get(0x14);
    public int Track => Get(0x24);
    public int Random => Get(0x2C);
    public bool Backward => Get(0x30) != 0;
    public bool Mirrored => Get(0x34) != 0;
    /// <summary>(race config +0x38; front-end tag 0x21, written by [ref:2291]
    /// <see cref="FrontEnd.FeRaceArgsBuilder"/> from the style, except style 2 = arcade without aids): driving aids.
    /// The shipped REPLAY.RPY has it set (1). [ref:2292]</summary>
    public bool WildArcade => Get(0x38) != 0;
    public int NumCars => Get(0x68);
    public int NumPlayerCars => Get(0x6C);
    public int NumOpponentCars => Get(0x70);
    public int NumSmackables => Get(0x4F8);

    public CarSetup Car(int i)
    {
        int o = 0x78 + i * RaceConfig.CarRecordSize;
        // +0x10/+0x14/+0x18 = the three ramps (car+0x21C): steering ramp, slow throttle [ref:2293]
        // step 0x10 (else 0x18) and brake ramp. The shipped REPLAY.RPY has 0/1/1 for the human car and 0/0/0 for the AI.
        return new CarSetup(Get(o), Get(o + 4), Get(o + 8), Get(o + 0xC), Get(o + 0x30),
            Get(o + 0x10) != 0, Get(o + 0x14) != 0, Get(o + 0x18) != 0,
            Get(o + 0x1C), Get(o + 0x20), Get(o + 0x24), Get(o + 0x28), Get(o + 0x2C));
    }

    public IEnumerable<CarSetup> Cars => Enumerable.Range(0, Math.Clamp(NumCars, 0, MaxCars)).Select(Car);

    public int GridSlot(int car) => Get(0x478 + 4 * car);

    /// <summary>Key binding of tag (0x23..0x7A): (device index, code).</summary>
    public (int Device, int Code) Key(int tag) => (KeyBindings[tag - 0x23] & 0xFF, KeyBindings[tag - 0x23] >> 8);
}

/// <param name="CarClass">1 Human, 2 AI, 4 Traffic, 8 Cop</param>
/// <param name="Personality">0x30 (tag 0x87) AI personality</param>
/// <param name="SteerRamp">+0x10 → steering ramp (otherwise <c>Steer = SteerTarget</c>) [ref:2294]</param>
/// <param name="SlowThrottle">+0x14 → throttle step 0x10 per 32 Hz tick (else 0x18)</param>
/// <param name="BrakeRamp">+0x18 → brake ramp (else <c>BrakeRamped = Brake</c>)</param>
/// <param name="DownforceFront">+0x1C (tag 0x82) front downforce set-up, 16.16, 1.0 = stock (SIM model only)</param>
/// <param name="DownforceRear">+0x20 (tag 0x83) rear downforce set-up</param>
/// <param name="SteerScale">+0x24 (tag 0x84) steering sensitivity: multiplies carSpecs+0x12C in sim [ref:2295]</param>
/// <param name="BrakeBias">+0x28 (tag 0x85) brake balance correction added to carSpecs+0x100 (stock 0)</param>
/// <param name="FinalDrive">+0x2C (tag 0x86) final drive ratio: divides the gear ratios in sim (1.0 = stock) [ref:2296]</param>
public sealed record CarSetup(int CarType, int CarClass, int Transmission, int Colour, int Personality,
    bool SteerRamp = false, bool SlowThrottle = false, bool BrakeRamp = false,
    int DownforceFront = 0x10000, int DownforceRear = 0x10000, int SteerScale = 0x10000, int BrakeBias = 0, int FinalDrive = 0x10000);
