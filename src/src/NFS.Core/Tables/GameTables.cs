using NFS.Core.Constants;

namespace NFS.Core.Tables;

/// <summary>
/// Lookup tables of the deterministic simulation (the data of the original game). <see cref="Default"/> and
/// <see cref="FromConstants"/> build them from the hard-coded constants in <c>NFS.Core.Constants</c>; the simulation
/// needs no game executable. The verification tool is kept outside the port sources. [ref:3372]
/// </summary>
public sealed partial class GameTables
{
    public const int LodRowCount = 4, LodBands = 20;

    public required int[] SinQuarter { get; init; }
    public required uint[] SqrtMul { get; init; }
    public required uint[] SqrtAdd { get; init; }
    public required int[] OrthoCoeffs { get; init; }
    public required byte[] Atan8 { get; init; }
    public required int[] Atan16 { get; init; }
    public required int[] LodRateHz { get; init; }
    public required int[] LodTicks { get; init; }
    public required int[] LodDt { get; init; }
    public required int[] LodMask { get; init; }
    public required byte[] LodRows { get; init; }
    /// <summary>15 × {sim, arcade}, 16.16 multiplier of the AI acceleration curve [ref:0902]
    /// Used by the AI acceleration code. [ref:0903]</summary>
    public int[] AiAccelScale { get; init; } = [];
    /// <summary><c>[model][arcade ? 1 : 0]</c> (0x10000 = unscaled). [ref:0904]</summary>
    public int AiAccelScaleOf(int model, bool arcade) =>
        (uint)model < 15 ? AiAccelScale[model * 2 + (arcade ? 1 : 0)] : Fx.One;
    /// <summary><c>[15]</c> (16.16 m/s per car model). [ref:0905]</summary>
    public int[] ModelVmax { get; init; } = [];
    /// <summary>int per surface type (+0x184, 0..0x3F), ≠0 [ref:0906]
    /// (surfaces 0, 3, 11, 12, 13). [ref:0907]</summary>
    public int[] SurfSlide { get; init; } = [];
    /// <summary>40 × {int ?, int mass 16.16, fn ptr} per car type (cfg+0); [ref:0908]
    /// The car code copies the mass to +0xB4. [ref:0909]</summary>
    public int[] CarInfo { get; init; } = [];
    public const int CarInfoCount = 0x34, CarInfoStride = 2; // 0x28 racer models + smackables (CART 41, BEER 50, RUIN 51 of SMnn.DAT)
    /// <summary>Tables: K0[2] ω scale, K1[2] about right, K2[2] about forward [ref:0910]
    /// K3[2] up speed; index 1 = arcade and not parked/static.</summary>
    public int[] LaunchK { get; init; } = [];
    /// <summary>15 car models × {int lean gain (+0x674), fx shake cap} [ref:0911]
    /// Used by the renderer's body lean. [ref:0912]</summary>
    public int[] ModelBody { get; init; } = [];
    /// <summary>int per surface type (+0x184, 0..0x3F), body shake amplitude cap. [ref:0913]</summary>
    public int[] SurfShake { get; init; } = [];
    /// <summary>damped sine, indexed by the shake phases +0x580/+0x588. Phases start at 0x23, [ref:0914]
    /// so the read runs on into the <see cref="LaunchK"/> table: 49 ints. [ref:0915]</summary>
    public int[] BodyShakeWave { get; init; } = [];
    /// <summary>13 letters (a..m) × 2 skill levels, 16.16 speed factor [ref:0916]
    /// (indexed by personality letter and skill level). [ref:0917]</summary>
    public int[] AiSkillSpeed { get; init; } = [];
    /// <summary>13 personality letters × 2 skill levels. [ref:0918]</summary>
    public int[] AiSkillAcc { get; init; } = [];
    public int AiSkillAccOf(int letter, int level) =>
        (uint)letter < 13 && (uint)level < 2 ? AiSkillAcc[letter * 2 + level] : Fx.One;
    /// <summary>Skill factor of a personality letter (0 = 'a') at a skill level (0/1); 1.0 outside the table.</summary>
    public int AiSkillSpeedOf(int letter, int level) =>
        (uint)letter < 13 && (uint)level < 2 ? AiSkillSpeed[letter * 2 + level] : Fx.One;
    /// <summary>15 tracks × 2 skill levels, extra factor applied [ref:0919]
    /// while <c>prog &gt; 100</c>. [ref:0920]</summary>
    public int[] AiTrackSkill { get; init; } = [];
    /// <summary>Extra skill factor of a track at a skill level; 1.0 outside the table.</summary>
    public int AiTrackSkillOf(int track, int level) =>
        (uint)track < 15 && (uint)level < 2 ? AiTrackSkill[track * 2 + level] : Fx.One;
    /// <summary>4 rows × 12 letters; the letter of a car follows [ref:0921]
    /// from its grid slot: <c>slots[gameType][numRacers − 1 − gridSlot]</c> [ref:0922]
    /// PRSONAL.DAT overwrites this table with an identical copy. [ref:0923]</summary>
    public byte[] PersonalitySlots { get; init; } = [];

    /// <summary>Personality letter index (0 = 'a') of a grid slot [ref:0924]
    /// (<c>(numRacers − 1 − gridSlot)</c> clamped to 0..0xB, in the row selected by the game type. [ref:0925]
    /// 0 for out-of-range slots.</summary>
    public int PersonalityLetterFor(int gridSlot, int numRacers, int gameType)
    {
        int col = numRacers - 1 - gridSlot;
        if (PersonalitySlots.Length < 48 || gameType is < 0 or > 3 || col is < 0 or > 11) return 0;
        int c = PersonalitySlots[gameType * 12 + col] - 'a';
        return c is >= 0 and < 13 ? c : 0;
    }

    /// <summary>row of <see cref="PersonalitySlots"/> for the current race type [ref:0926]
    /// (<paramref name="raceType"/>, <paramref name="numAi"/> = number of AI cars on the grid). [ref:0927]</summary>
    public static int AiGameType(int raceType, int numAi) => raceType != 0 ? (raceType < 2 ? 1 : 0) : (numAi > 1 ? 3 : 2);

    /// <summary>Tables built from the constants in <c>NFS.Core.Constants</c> (copies; no external data needed).</summary>
    public static GameTables FromConstants() => new()
    {
        SinQuarter = MathConstants.SinQuarter.ToArray(),
        SqrtMul = MathConstants.SqrtMul.ToArray(),
        SqrtAdd = MathConstants.SqrtAdd.ToArray(),
        OrthoCoeffs = MathConstants.OrthoCoeffs.ToArray(),
        Atan8 = MathConstants.GenerateAtan8(),
        Atan16 = MathConstants.GenerateAtan16(),
        LodRateHz = PhysicsConstants.LodRateHz.ToArray(),
        LodTicks = PhysicsConstants.LodTicks.ToArray(),
        LodDt = PhysicsConstants.LodDt.ToArray(),
        LodMask = PhysicsConstants.LodMask.ToArray(),
        LodRows = PhysicsConstants.LodRows.ToArray(),
        AiAccelScale = AiConstants.AiAccelScale.ToArray(),
        ModelVmax = PhysicsConstants.ModelVmax.ToArray(),
        SurfSlide = PhysicsConstants.SurfSlide.ToArray(),
        CarInfo = PhysicsConstants.CarInfo.ToArray(),
        LaunchK = PhysicsConstants.LaunchK.ToArray(),
        ModelBody = PhysicsConstants.ModelBody.ToArray(),
        SurfShake = PhysicsConstants.SurfShake.ToArray(),
        BodyShakeWave = PhysicsConstants.BodyShakeWave.ToArray(),
        AiSkillSpeed = AiConstants.AiSkillSpeed.ToArray(),
        AiSkillAcc = AiConstants.AiSkillAcc.ToArray(),
        AiTrackSkill = AiConstants.AiTrackSkill.ToArray(),
        PersonalitySlots = AiConstants.PersonalitySlots.ToArray(),
    };

    private static GameTables? _default;
    private static readonly object Gate = new();

    /// <summary>Tables from <see cref="FromConstants"/> (built once, no external data). <see cref="UseDefault"/> injects others.</summary>
    public static GameTables Default
    {
        get
        {
            if (_default != null) return _default;
            lock (Gate) return _default ??= FromConstants();
        }
    }

    public static void UseDefault(GameTables tables) { lock (Gate) _default = tables; }

    /// <summary>Always true: the default tables are the built-in constants.</summary>
    public static bool DefaultAvailable => true;
}
