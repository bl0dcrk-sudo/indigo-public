namespace NFS.Core;

/// <summary>
/// Catch-up (rubber band) of the human cars, the tag 9 CATCHUPLOGIC branch in the original code (one player [ref:0470]
/// never has it: the front-end forces +0x0E to 0; split screen defaults to 1). The gap to the race leader (distance
/// <c>+0x220</c> of the leading racer minus the human's) picks the speed multiplier written to <c>car+0x218</c> (read as the car
/// time scale by): simtune[21..24] are the distances, simtune[25..28] the multipliers. [ref:0471]
/// Below the first distance the multiplier is 1.0, then {25, 26, 27} for the next three bands and {28} from the last distance up.
/// </summary>
public static class HumanCatchUp
{
    /// <summary>First simtune index of the four distances; the multipliers start 4 entries later. [ref:0472]</summary>
    public const int DistanceIndex = 21, MultiplierIndex = 25;

    public static int Multiplier(int gap, ReadOnlySpan<int> simTune)
    {
        if (gap < simTune[DistanceIndex]) return Fx.One;
        if (gap < simTune[DistanceIndex + 1]) return simTune[MultiplierIndex];
        if (gap < simTune[DistanceIndex + 2]) return simTune[MultiplierIndex + 1];
        if (gap < simTune[DistanceIndex + 3]) return simTune[MultiplierIndex + 2];
        return simTune[MultiplierIndex + 3];
    }
}
