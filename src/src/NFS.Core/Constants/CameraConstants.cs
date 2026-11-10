namespace NFS.Core.Constants;

/// <summary>
/// Race camera tables of the original cam_* functions (the original game), as constants. Vectors are 16.16 int triples (x, y, z).
/// Values copied from the original game (verified by tests).
/// </summary>
public static class CameraConstants
{
    /// <summary>
    /// In the original game: target offset in the road frame (x, y, z; [ref:0766]
    /// z is negated when the flip started against the track direction).
    /// </summary>
    public static ReadOnlySpan<int> ChaseOffset => [
        0x0, 0x30000, -0xA0000,
    ];

    /// <summary>
    /// Cockpit view offset (in the original game) in the car frame, 16.16. [ref:0767]
    /// </summary>
    public static ReadOnlySpan<int> CockpitOffset => [
        0x0, 0x4CCC, 0x8000,
    ];

    /// <summary>
    /// Hood view offset (in the original game) in the car frame, 16.16. [ref:0768]
    /// </summary>
    public static ReadOnlySpan<int> HoodOffset => [
        0x0, 0x4CCC, 0xE666,
    ];

    /// <summary>
    /// Near chase view offset (in the original game) in the car frame, 16.16. [ref:0769]
    /// </summary>
    public static ReadOnlySpan<int> NearOffset => [
        0x0, 0x10CCC, -0x4D999,
    ];

    /// <summary>
    /// Far chase view offset (in the original game) in the car frame, 16.16. [ref:0770]
    /// </summary>
    public static ReadOnlySpan<int> FarOffset => [
        0x0, 0x20000, -0x60000,
    ];

    /// <summary>
    /// Mode 7 offset (in the original game) in the car axes, 16.16. [ref:0771]
    /// </summary>
    public static ReadOnlySpan<int> Mode7Offset => [
        0x30000, 0x14CCC, -0x3E666,
    ];

    /// <summary>
    /// Mode 8 offset (in the original game) in the world axes, 16.16. [ref:0772]
    /// </summary>
    public static ReadOnlySpan<int> Mode8Offset => [
        0x0, 0xF0000, -0x20000,
    ];

    /// <summary>
    /// entry speed per track-camera slice (table in the original game), 16.16 m/s. [ref:0773]
    /// </summary>
    public static ReadOnlySpan<int> TrackEntrySpeeds => [
        0x40000, 0xA0000, 0x140000, 0x280000, 0x3C0000, 0x500000, 0x700000, 0x8C0000,
    ];

    /// <summary>
    /// speed threshold per speed index (table in the original game), 16.16 m/s. [ref:0774]
    /// </summary>
    public static ReadOnlySpan<int> TrackSpeeds => [
        0x40000, 0xA0000, 0x140000, 0x280000, 0x3C0000, 0x500000, 0x700000, 0x8C0000,
    ];

    /// <summary>
    /// camera ticks per slice (table in the original game). [ref:0775]
    /// </summary>
    public static ReadOnlySpan<int> TrackTicks => [
        0x40, 0x20, 0x10, 0x8, 0x6, 0x4, 0x3, 0x2,
    ];

    /// <summary>
    /// Camera height per car type (in the original game), 16.16. Index = min(car type, 9): 10 entries. [ref:0776]
    /// </summary>
    public static ReadOnlySpan<int> CarHeights => [
        0x4000, 0x0, 0x0, 0xCCC, 0x0,
        0xCCC, 0x0, 0x4000, 0x0, 0x4CCC,
    ];

    /// <summary>
    /// successor of each mode (table in the original game; the runtime copy [ref:0777]
    /// is changed by RShift+C / RShift+B). Indexed by mode 0..0xC: 13 entries [ref:0778]
    /// The next table in the original starts right after this one. [ref:0779]
    /// </summary>
    public static ReadOnlySpan<byte> SuccessorDefault => [
        0x01, 0x02, 0x03, 0x00, 0x05, 0x06, 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06,
    ];

    /// <summary>
    /// successor of each mode for a split-screen player that has finished [ref:0780]
    /// (table in the original game). Indexed by mode 0..0xC: 13 entries. [ref:0781]
    /// </summary>
    public static ReadOnlySpan<byte> SuccessorFinished => [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x01, 0x04, 0x04, 0x04, 0x04, 0x04,
    ];
}
