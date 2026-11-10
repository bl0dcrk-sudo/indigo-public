namespace NFS.Core.Constants;

/// <summary>
/// Physics and car tables of the original simulation (the original game), as constants. Values copied from the original game
/// (verified by tests). Fixed-point values are 16.16 (0x10000 = 1.0).
/// </summary>
public static class PhysicsConstants
{
    /// <summary>
    /// Table of the original game: 7 int, update rate per LOD level in Hz. [ref:0852]
    /// </summary>
    public static ReadOnlySpan<int> LodRateHz => [
        0, 1, 2, 4, 8, 16, 32,
    ];

    /// <summary>
    /// Table of the original game: 7 int, ticks between updates per LOD level. [ref:0853]
    /// </summary>
    public static ReadOnlySpan<int> LodTicks => [
        32, 32, 16, 8, 4, 2, 1,
    ];

    /// <summary>
    /// Table of the original game: 7 int, time step per LOD level in 16.16 {0, 1, 1/2, …, 1/32}. [ref:0854]
    /// </summary>
    public static ReadOnlySpan<int> LodDt => [
        0x0, 0x10000, 0x8000, 0x4000, 0x2000, 0x1000, 0x800,
    ];

    /// <summary>
    /// Table of the original game: 7 int, tick mask per LOD level. [ref:0855]
    /// </summary>
    public static ReadOnlySpan<int> LodMask => [
        0x3F, 0x1F, 0xF, 0x7, 0x3, 0x1, 0x0,
    ];

    /// <summary>
    /// Table of the original game: u8[4][20], LOD level per 16 m distance band; one row per line. [ref:0856]
    /// </summary>
    public static ReadOnlySpan<byte> LodRows => [
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        6, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        6, 6, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    ];

    /// <summary>
    /// Table of the original game (<c>[15]</c>): 16.16 m/s per car model. [ref:0857]
    /// </summary>
    public static ReadOnlySpan<int> ModelVmax => [
        0x674CCC, 0x5A4CCC, 0x51CCCC, 0x626666, 0x508000, 0x504CCC, 0x5E3333, 0x573333,
        0x4E3333, 0x564CCC, 0x50E666, 0x5ECCCC, 0x49B333, 0x5DE666, 0x7AE666,
    ];

    /// <summary>
    /// Table of the original game: int per surface type (+0x184, 0..0x3F); ≠0 [ref:0858]
    /// (surfaces 0, 3, 11, 12, 13).
    /// </summary>
    public static ReadOnlySpan<int> SurfSlide => [
        0x1, 0x0, 0x0, 0x1, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x1, 0x1, 0x1, 0x0, 0x0,
        0x0, 0x0, 0x10000, 0x18000, 0x9999, 0x13333, 0x0, 0x8000, 0x0, 0x4CCC, 0x8000, 0x10000, 0xCCCC, 0x13333, 0x0, 0x13333,
        0x0, 0x0, 0x7, 0x8, 0x0, 0x6, 0x0, 0x0, 0x0, 0x9, 0x0, 0x0, 0x0, 0x6, 0x0, 0x9,
        0x0, 0x0, 0x3, 0x9, 0x0, 0x3, 0x0, 0x0, 0x8, 0x8, 0x0, 0x0, 0x0, 0x3, 0x3, 0x8,
    ];

    /// <summary>
    /// Per-car-type table: 0x34 rows × {int ?, mass 16.16} (cfg+0). [ref:0859]
    /// The car code copies the mass to +0xB4. Rows 0x28 are racer models; the rest are smackables [ref:0860]
    /// (CART 41, BEER 50, RUIN 51 of SMnn.DAT).
    /// </summary>
    public static ReadOnlySpan<int> CarInfo => [
        0x3, 0xB570A,
        0x3, 0xDF5C2,
        0x3, 0xDF5C2,
        0x3, 0xE8A3D,
        0x3, 0xA6E14,
        0x3, 0xE8A3D,
        0x3, 0xEB333,
        0x3, 0xA8000,
        0x3, 0xDCCCC,
        0x3, 0xD0000,
        0x3, 0xD0000,
        0x3, 0xECCCC,
        0x3, 0x280000,
        0x3, 0xD0000,
        0x3, 0xD0000,
        0x3, 0x8000,
        0x3, 0xB0000,
        0x3, 0xA0000,
        0x3, 0x60000,
        0x2, 0xF0000,
        0x1, 0x320000,
        0x1, 0x320000,
        0x3, 0xD0000,
        0x3, 0xF0000,
        0x3, 0xF0000,
        0x3, 0xDA147,
        0x3, 0xF0A3D,
        0x3, 0xB0000,
        0x3, 0x90000,
        0x3, 0x80000,
        0x3, 0xF0000,
        0x1, 0x320000,
        0x3, 0xC224D,
        0x1, 0x460000,
        0x3, 0x1E224D,
        0x3, 0x14224D,
        0x3, 0xA0000,
        0x3, 0x2224D,
        0x3, 0x2224D,
        0x3, 0x2224D,
        0x3, 0x8000,
        0x3, 0x8000,
        0x3, 0x30000,
        0x3, 0x640000,
        0x3, 0x70000,
        0x3, 0x30000,
        0x3, 0x30000,
        0x3, 0x30000,
        0x3, 0x20000,
        0x3, 0x30000,
        0x3, 0x1999,
        0x3, 0x140000,
    ];

    /// <summary>
    /// Tables of the original game: K0[2] ω scale, K1[2] about right, K2[2] about forward [ref:0861]
    /// K3[2] up speed; index 1 = arcade and not parked/static.
    /// </summary>
    public static ReadOnlySpan<int> LaunchK => [
        0x3333, 0x5999,
        0x10000, 0x3D999,
        0x10000, 0x1D999,
        0x10000, 0x3B333,
    ];

    /// <summary>
    /// Table of the original game: 15 car models × {int lean gain (+0x674), fx shake cap} [ref:0862]
    /// (used by the renderer's body lean). [ref:0863]
    /// </summary>
    public static ReadOnlySpan<int> ModelBody => [
        0x27, 0x9999,
        0x20, 0xB333,
        0x20, 0xB333,
        0x20, 0xB333,
        0x1A, 0x10000,
        0x20, 0xB333,
        0x2D, 0x3333,
        0x1A, 0xC000,
        0x2D, 0x1999,
        0x20, 0xB333,
        0x20, 0xB333,
        0x2D, 0x9999,
        0x41, 0xCCCC,
        0x41, 0xCCCC,
        0x20, 0x3333,
    ];

    /// <summary>
    /// Table of the original game: int per surface type (+0x184, 0..0x3F), body shake amplitude cap. [ref:0864]
    /// </summary>
    public static ReadOnlySpan<int> SurfShake => [
        0x0, 0x0, 0x10000, 0x18000, 0x9999, 0x13333, 0x0, 0x8000, 0x0, 0x4CCC, 0x8000, 0x10000, 0xCCCC, 0x13333, 0x0, 0x13333,
        0x0, 0x0, 0x7, 0x8, 0x0, 0x6, 0x0, 0x0, 0x0, 0x9, 0x0, 0x0, 0x0, 0x6, 0x0, 0x9,
        0x0, 0x0, 0x3, 0x9, 0x0, 0x3, 0x0, 0x0, 0x8, 0x8, 0x0, 0x0, 0x0, 0x3, 0x3, 0x8,
        0x0, 0x4, 0x4, 0x6, 0x4, 0x8, 0x0, 0x6, 0x4, 0x4, 0x4, 0xA, 0x4, 0xB, 0xA, 0xA,
    ];

    /// <summary>
    /// Table of the original game: damped sine, indexed by the shake phases +0x580/+0x588. [ref:0865]
    /// Phases start at 0x23, so the read runs on into the <c>LaunchK</c> table: 49 ints. [ref:0866]
    /// </summary>
    public static ReadOnlySpan<int> BodyShakeWave => [
        0, 0, -3, -6, -8, -9, -8,
        -6, -3, 0, 7, 12, 16, 18,
        19, 18, 16, 12, 7, 0, -8,
        -12, -18, -20, -21, -20, -18, -15,
        -11, -6, 0, 8, 16, 24, 32,
        42, 54, 0, 1, 0, 0, 0,
        1, 0, 1, 0, 0, 0, 0,
    ];
}
