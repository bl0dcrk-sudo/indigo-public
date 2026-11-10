using NFS.Core;
using NFS.Data.Track;
using Xunit;

namespace NFS.Tests.Core;

public class FinishAiTests
{
    // Two lanes each side, each 2 m wide: roadway [-4, +4] m.
    static TrackSlice Road => new(0, 0, 0, [0, 127, 0], [0, 0, 127], [127, 0, 0],
        0, 0, 0xFFFF, 2048, 2048, 0, 0x22, 4, 4, 0, 0);

    [Theory]
    [InlineData(false, 60, 0, 720896)]
    [InlineData(false, 61, 0, 1)]
    [InlineData(true, 940, 0, 720896)]
    [InlineData(true, 939, 0, 1)]
    [InlineData(false, 10, 196608, 720896)] // body touches boundary at +4 m
    [InlineData(false, 10, 196609, 1)]
    [InlineData(false, 10, -196608, 720896)]
    [InlineData(false, 10, -196609, 1)]
    public void FinishedSpeedHonorsBodyEdgesAndSixtySliceBoundary(bool reverse, int slice, int offset, int expected)
    {
        var c = new CarObj { Flags = CarFlags.Racer | CarFlags.Opponent, RaceState = 2,
            HalfWidth = Fx.One, LateralOffsetCur = offset, Progress = 100 };
        c.Trk.Slice = slice;
        AiSpeedPlanning.Desired(c, 50, 80 * Fx.One, Fx.One, Fx.One, Fx.One, Fx.One,
            [], 1000, reverse, Road);
        Assert.Equal(expected * (reverse ? -1 : 1), c.AiTargetSpeed);
        Assert.Equal(Fx.One, c.TimeScale);
    }

    [Theory]
    [InlineData(0, CarFlags.Racer)]
    [InlineData(1, CarFlags.Racer)]
    [InlineData(2, CarFlags.Traffic)]
    public void FinishRestrictionsExcludeUnfinishedCarsAndTraffic(int state, CarFlags flags)
    {
        var c = new CarObj { Flags = flags, RaceState = state };
        Assert.Equal(123456, AiSpeedPlanning.FinishSpeed(c, Road, 1000, false, 123456));
        int[] scores = [1, 2, 3]; AiSpeedPlanning.ScoreFinished(c, Road, scores);
        Assert.Equal([1, 2, 3], scores);
        Assert.False(AiDriveModel.FinishStop(c));
    }

    [Theory]
    [InlineData(4, 1)] // already on left shoulder: stay
    [InlineData(9, 1)] // right shoulder: stay
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 2)]
    [InlineData(8, 2)]
    public void FinishedRacerFavorsNearestShoulder(int lane, int column)
    {
        var c = new CarObj { Flags = CarFlags.Racer, RaceState = 2, Lane = lane, Progress = 1000 };
        int[] scores = [7, 8, 9];
        AiSpeedPlanning.ScoreFinished(c, Road, scores);
        Assert.Equal((column == 0 ? 150 * Fx.One : 0) + 7, scores[0]);
        Assert.Equal((column == 1 ? 150 * Fx.One : 0) + 8, scores[1]);
        Assert.Equal((column == 2 ? 150 * Fx.One : 0) + 9, scores[2]);
        int picked = AiSpeedPlanning.ChooseLane(c, Road, new(), false, 0, scores);
        Assert.Equal(lane + column - 1, picked);
    }

    [Theory]
    [InlineData(-131072, false)]
    [InlineData(-131071, true)]
    [InlineData(0, true)]
    [InlineData(131071, true)]
    [InlineData(131072, false)]
    public void FinishedSlowCarStopsTurningAndDampsMotion(int speed, bool stopped)
    {
        var c = new CarObj { Flags = CarFlags.Racer, RaceState = 2, AiSpeed = speed,
            Vel = new(12345, 0, 23456), AngVel = new(100, 200, 300) };
        var expected = new CarObj { Vel = c.Vel, AngVel = default };
        ArcadeModel.AirDamping(expected);
        Assert.Equal(stopped, AiDriveModel.FinishStop(c));
        Assert.Equal(stopped ? expected.Vel : new Vec3(12345, 0, 23456), c.Vel);
        Assert.Equal(stopped ? default : new Vec3(100, 200, 300), c.AngVel);
    }
}
