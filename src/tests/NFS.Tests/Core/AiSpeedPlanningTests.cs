using NFS.Core;
using NFS.Data.Track;
using Xunit;

namespace NFS.Tests.Core;

public class AiSpeedPlanningTests
{
    [Theory]
    [InlineData(7, false, false, 1, -65536, 0)]
    [InlineData(6, false, false, 1, -65536, 65536)]
    [InlineData(5, false, false, 1, -65536, 65536)]
    [InlineData(8, false, false, 1, 0, 0)]
    [InlineData(7, true, false, 1, 65536, -65536)]
    [InlineData(6, true, false, 1, 0, -65536)]
    [InlineData(5, true, false, 1, 0, 0)]
    [InlineData(8, true, false, 1, 65536, -65536)]
    [InlineData(7, false, true, 1, 65536, -65536)]
    [InlineData(6, true, true, 1, -65536, 65536)]
    [InlineData(7, false, false, -1, 65536, -65536)]
    [InlineData(6, true, false, -1, -65536, 65536)]
    [InlineData(7, false, false, 0, 65536, -65536)]
    public void LaneSideBias_RespectsDirectionAndLaneBoundary(int lane, bool reverse, bool oncoming,
        int side, int left, int right)
    {
        var c = new CarObj { Lane = lane, Progress = 1000, AiFlags = oncoming ? 0x1000 : 0 };
        int[] scores = [5 * Fx.One, 5 * Fx.One, 5 * Fx.One];
        AiSpeedPlanning.ScoreLaneSide(c, Straight(1)[0], 7, reverse, side, scores);
        Assert.Equal(new[] { 5 * Fx.One + left, 5 * Fx.One, 5 * Fx.One + right }, scores);
    }

    [Fact]
    public void LaneSideBias_StartPhaseBoundaryAndOriginalTie()
    {
        var sl = Straight(1)[0];
        var c = new CarObj { Lane = 7, ListPosition = 8, Progress = 262 };
        int[] scores = [5 * Fx.One, 5 * Fx.One, -5 * Fx.One];
        AiSpeedPlanning.ScoreLaneSide(c, sl, 7, true, 1, scores);
        Assert.Equal(new[] { 5 * Fx.One, 5 * Fx.One, -5 * Fx.One }, scores); // 8*14+262 == 374
        c.Progress++;
        AiSpeedPlanning.ScoreLaneSide(c, sl, 7, true, 1, scores);
        Assert.Equal(new[] { 6 * Fx.One, 5 * Fx.One, -6 * Fx.One }, scores);
        c.Progress = 299; // original car 7 @2072: previously tied left/current columns; now chooses lane 6
        Assert.Equal(6, AiSpeedPlanning.ChooseLane(c, sl, new(), true, 0, scores,
            maskScores: [5 * Fx.One, 5 * Fx.One, -5 * Fx.One], opponents: 7));
    }

    [Fact]
    public void ParkedCarPenalty_CancelsLaneChangeFromLineAndSideBias()
    {
        var track = Straight(1863);
        var c = new CarObj { Lane = 7, Progress = 337, ListPosition = 8, AiSpeed = -3617815 };
        c.Trk.Slice = 1527;
        var parked = new CarObj { LateralOffset = -260568 }; parked.Trk.Slice = 1505;
        var scores = new int[3];
        int lane = AiSpeedPlanning.ChooseLane(c, track[1527], new(), true, 6, scores,
            maskScores: [5 * Fx.One, 5 * Fx.One, -7 * Fx.One], track: track, opponents: 7, parked: [parked]);
        Assert.Equal(7, lane);
        Assert.Equal(new[] { 8 * Fx.One - 571319, 5 * Fx.One, -8 * Fx.One }, scores);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, -1)]
    [InlineData(4, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 1)]
    public void TrackTrafficSide_MatchesOriginalInitialization(int track, int side)
        => Assert.Equal(side, AiSpeedPlanning.TrackLaneSide(track));

    [Fact]
    public void FreeLane_StartPhaseEndsAtOriginalProgressBoundary()
    {
        var s = Straight(1863);
        var c = new CarObj { Flags = CarFlags.Opponent, Lane = 7, ListPosition = 7, Progress = 276 };
        c.Trk.Slice = 1586;
        var other = new CarObj { Active = 1, Lane = 7 }; other.Trk.Slice = 1582;
        var scores = new int[3];
        AiSpeedPlanning.ScoreFree(c, [c, other], s, true, 7, scores, [0, 1 << 7]);
        Assert.Equal(new[] { 0, 0, 0 }, scores);
        c.Progress = 277; // car 6 @1988: 7*14+277=375 > 400-26; car 7 is four slices ahead in lane 7
        AiSpeedPlanning.ScoreFree(c, [c, other], s, true, 7, scores, [0, 1 << 7]);
        Assert.Equal(new[] { 5 * Fx.One, 0, 5 * Fx.One }, scores);
        Assert.Equal(6, AiSpeedPlanning.ChooseLane(c, s[1586], new(), true, 7, scores,
            cars: [c, other], slices: s.Length, track: s, opponents: 7, occupancy: [0, 1 << 7]));
    }

    [Theory]
    [InlineData(-1, -262144, 0)]
    [InlineData(-1, -262145, 327680)]
    [InlineData(3, 6946815, 0)]
    [InlineData(3, 6946816, 327680)]
    [InlineData(26, 0, 0)]
    [InlineData(27, 0, 327680)]
    public void FreeLane_UsesWorldDistanceNearCarAndFourMetresPerSliceFarAway(int delta, int worldDistance, int bonus)
    {
        var s = Straight(100);
        var c = new CarObj { Flags = CarFlags.Opponent, Lane = 7, Progress = 1000, RoadFrame = Mat3.Identity };
        c.Trk.Slice = 10;
        var other = new CarObj { Active = 1, Pos = new Vec3(0, 0, worldDistance) }; other.Trk.Slice = 10 + delta;
        var scores = new int[3];
        AiSpeedPlanning.ScoreFree(c, [c, other], s, false, 7, scores, [0, 1 << 7]);
        Assert.Equal(new[] { 5 * Fx.One, bonus, 5 * Fx.One }, scores);
    }

    [Fact]
    public void SideCar_PreventsMovingIntoPreferredOccupiedLane()
    {
        // Original car 6 before 1524: frame updated, car 7 in lane 7 is 357031 away (<369920). [ref:3045]
        var c = new CarObj { Flags = CarFlags.Opponent, Active = 1, Lane = 6, HalfLength = 147968,
            HalfWidth = 66560, Pos = new Vec3(-29024040, 0, -43234713), RoadFrame = Mat3.Identity };
        c.RoadFrame.M20 = -40580; c.RoadFrame.M22 = 51000; c.Trk.Slice = 1704;
        var other = new CarObj { Active = 1, Lane = 7, Pos = new Vec3(-28730229, 0, -43459724) };
        other.Trk.Slice = 1703;
        var scores = new int[3];
        var sl = Straight(1)[0] with { Lanes = 0x11, HalfWidthLeft = 7, HalfWidthRight = 7 };
        Assert.Equal(7, AiSpeedPlanning.ChooseLane(c, sl, new(), true, 7, scores));
        Assert.Equal(6, AiSpeedPlanning.ChooseLane(c, sl, new(), true, 7, scores, cars: [c, other], slices: 1863));
        Assert.Equal(-3 * Fx.One, scores[2]); // QAL +2, adjacent car -5
    }

    [Theory]
    [InlineData(2, 327679, true, -327680)]
    [InlineData(-2, -327679, false, -327680)]
    [InlineData(2, 327680, false, 0)]
    [InlineData(3, 0, true, 0)]
    [InlineData(-3, 0, false, 0)]
    public void SideCar_UsesStrictWorldDistanceAndInclusiveTwoSliceWindow(int delta, int distance, bool reverse, int expected)
    {
        var c = new CarObj { Flags = CarFlags.Opponent, Lane = 6, HalfLength = 2 * Fx.One, RoadFrame = Mat3.Identity };
        c.Trk.Slice = 0;
        var other = new CarObj { Active = 1, Lane = 5, Pos = new Vec3(0, 999 * Fx.One, distance) };
        other.Trk.Slice = (100 + delta) % 100;
        var scores = new int[3];
        AiSpeedPlanning.ScoreSideCars(c, [c, other], 100, reverse, scores);
        Assert.Equal(new[] { expected, 0, 0 }, scores);
    }

    [Fact]
    public void SideCar_ExcludesInactiveStaticSameLaneAndNonOpponents_AndAddsPenalties()
    {
        var c = new CarObj { Flags = CarFlags.Opponent, Active = 1, Lane = 6, HalfLength = Fx.One, RoadFrame = Mat3.Identity };
        var scores = new int[3];
        AiSpeedPlanning.ScoreSideCars(c, [c, new() { Lane = 5 },
            new() { Active = 1, Lane = 5, Flags = CarFlags.Static }, new() { Active = 1, Lane = 6 }], 100, false, scores);
        Assert.Equal(new[] { 0, 0, 0 }, scores);
        CarObj[] adjacent = [new() { Active = 1, Lane = 5 }, new() { Active = 1, Lane = 7 }, new() { Active = 1, Lane = 7 }];
        AiSpeedPlanning.ScoreSideCars(c, adjacent, 100, false, scores);
        Assert.Equal(new[] { -5 * Fx.One, 0, -10 * Fx.One }, scores);
        c.Flags = CarFlags.LocalHuman;
        Array.Clear(scores);
        AiSpeedPlanning.ScoreSideCars(c, adjacent, 100, false, scores);
        Assert.Equal(new[] { 0, 0, 0 }, scores);
    }

    private static TrackSlice[] Straight(int n) => Enumerable.Range(0, n).Select(i => new TrackSlice(
        0, 0, i * 4 * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0x3FFF, 4000, 4000,
        0, 0x22, 10, 10, 0, 0)).ToArray();

    [Fact]
    public void Desired_CapsBeforeFactors_ClampsBeforeStartBoost()
    {
        var c = new CarObj { Progress = 0 }; c.Trk.Slice = 17;
        var human = new CarObj(); human.Trk.Slice = 37;
        // Original car 4, trace tick 2: skill personality d, model MACH.
        AiSpeedPlanning.Desired(c, 100, 5262540, 59637, Fx.One, Fx.One, Fx.One, [human], 1863, true);
        Assert.Equal(119259, c.Glue);
        Assert.Equal(119259, c.TimeScale);
        Assert.Equal(-9576496, c.AiTargetSpeed);
        AiSpeedPlanning.Desired(c, 100, 5262540, 2 * Fx.One, Fx.One, Fx.One, Fx.One, [human], 1863, true);
        Assert.Equal(Fx.Mul(0x14CCC, 0x1FFF0), c.Glue);
    }

    [Theory]
    [InlineData(true, -2621440)]
    [InlineData(false, 2621440)]
    public void Follow_StationaryBlockerTenSlicesAhead_UsesGapMinusTwo(bool reverse, int expected)
    {
        var slices = Straight(100);
        var c = new CarObj { Active = 1, Lane = 7, Flags = CarFlags.Opponent, AiTargetSpeed = reverse ? -100 * Fx.One : 100 * Fx.One,
            HalfLength = 2 * Fx.One, RoadFrame = Mat3.Identity, LateralOffsetCur = 2 * Fx.One, LaneHalfWidth = Fx.One };
        var blocker = new CarObj { Active = 1, Lane = 7, HalfLength = 2 * Fx.One, LateralOffsetCur = 2 * Fx.One, LaneHalfWidth = Fx.One };
        c.Trk.Slice = 20; blocker.Trk.Slice = reverse ? 10 : 30;
        var scan = AiSpeedPlanning.ScanAhead(c, [c, blocker], slices, reverse, 30);
        Assert.Same(blocker, scan.Blocker[1]);
        Assert.Equal(expected, scan.Speed[1]);
        AiSpeedPlanning.Follow(c, scan, 7, reverse, null, 100);
        Assert.Equal(expected, c.AiTargetSpeed); // reverse upper cap must leave -40 alone
    }

    [Fact]
    public void Scan_UsesPersistentOccupancy_AndFirstCarInCyclicOrder()
    {
        var s = Straight(100);
        var c = new CarObj { Active = 1, Lane = 7, HalfLength = Fx.One, AiTargetSpeed = -100 * Fx.One };
        c.Trk.Slice = 40;
        var far = new CarObj { Active = 1 }; far.Trk.Slice = 25;
        var near = new CarObj { Active = 1 }; near.Trk.Slice = 35;
        var r = AiSpeedPlanning.ScanAhead(c, [c, far, near], s, true, 30, [0, 1 << 7, 1 << 7]);
        Assert.Same(far, r.Blocker[1]);
        Assert.Equal(-65 * Fx.One, r.Speed[1]);
        r = AiSpeedPlanning.ScanAhead(c, [c, far, near], s, true, 30, [0, 0, 0]);
        Assert.Null(r.Blocker[1]);
        Assert.Equal(c.AiTargetSpeed, r.Speed[1]);
    }

    [Fact]
    public void SpeedScoring_DoesNotPenalizeDesiredSpeedDuringStart()
    {
        var c = new CarObj { Lane = 7, AiTargetSpeed = -100 * Fx.One, HalfWidth = Fx.One };
        var scan = new AiSpeedPlanning.Scan(); scan.Blocker[1] = new CarObj();
        scan.Speed[1] = -40 * Fx.One; scan.SpeedNear[1] = -10 * Fx.One;
        var scores = new int[3];
        Assert.Equal(6, AiSpeedPlanning.ChooseLane(c, Straight(1)[0], scan, true, 6, scores));
        Assert.Equal(0, scores[1]);
        c.Progress = 150;
        AiSpeedPlanning.ChooseLane(c, Straight(1)[0], scan, true, 6, scores);
        Assert.True(scores[1] < 0);
    }

    [Fact]
    public void SmoothLane_SnapsAcrossActualOffsetBeforeAdvancing()
    {
        // trace car 4 before tick 560: the actual offset is between the old and new targets.
        var c = new CarObj { SmoothedLaneOffset = -163840, LaneTargetOffset = 163840, LateralOffsetCur = 150733 };
        AiSpeedPlanning.SmoothLane(c);
        Assert.Equal(163840, c.SmoothedLaneOffset);
        c.SmoothedLaneOffset = 0; c.LaneTargetOffset = -163840; c.LateralOffsetCur = 150733;
        AiSpeedPlanning.SmoothLane(c);
        Assert.Equal(-13107, c.SmoothedLaneOffset);
    }

    [Fact]
    public void Integrate_UphillGravityHasNoEighthDivision_ButArcadeQuarterReduction()
    {
        var c = new CarObj { Flags = CarFlags.Opponent, TimeScale = 119259, Orient = Mat3.Identity };
        c.Orient.M20 = -3106; c.Orient.M21 = 862; c.Orient.M22 = -65454;
        AiDriveModel.Integrate(c, new AiDriveModel.Forces { Car = new Vec3(0, 0, 605696) }, 0x800);
        Assert.Equal(new Vec3(-28520, 7915, -601022), c.DeltaV); // trace 530, independently recomputed from 528
        Assert.Equal(0, c.LocalForce.Z);
    }

    [Fact]
    public void Accel_BeginnerReductionStartsAfterProgress100()
    {
        var c = new CarObj { AiSpeed = -3753681, Progress = 100 };
        c.AccelCurve[58] = 1085; // original car 4 @1320: 277760 before ×0.75
        Assert.Equal(277760, AiDriveModel.Accel(c, skillLevel: 0));
        c.Progress = 101;
        Assert.Equal(208320, AiDriveModel.Accel(c, skillLevel: 0));
        Assert.Equal(277760, AiDriveModel.Accel(c, skillLevel: 1));
    }

    [Fact]
    public void NeighborSort_ExcludesLastEntryAndReversesAllEntries()
    {
        var cars = new[] { 10, 5, 7, 0 }.Select(s => { var c = new CarObj(); c.Trk.Slice = s; return c; }).ToArray();
        AiSpeedPlanning.SortNeighbors(cars, true);
        Assert.Equal(new[] { 0, 10, 7, 5 }, cars.Select(c => c.Trk.Slice));
        Assert.Equal(new[] { 0, 1, 2, 3 }, cars.Select(c => c.ListPosition));
        AiSpeedPlanning.SortNeighbors(cars, true);
        Assert.Equal(new[] { 5, 10, 7, 0 }, cars.Select(c => c.Trk.Slice));
    }

    [Fact]
    public void TargetOffset_UsesFreeGapBesideCloseBlocker()
    {
        // Original 1888: car 4 (lane 8) targets lane 7 around car 5, rather than its centre 114688.
        var s = Straight(20);
        s[10] = s[8] = s[10] with { Lanes = 0x11, HalfWidthLeft = 7, HalfWidthRight = 7 };
        var c = new CarObj { Lane = 8, HalfWidth = 63744, Progress = 237, ListPosition = 4 }; c.Trk.Slice = 10;
        var blocker = new CarObj { LateralOffsetCur = 219045, LaneHalfWidth = 99465 }; blocker.Trk.Slice = 8;
        var scan = new AiSpeedPlanning.Scan(); scan.Blocker[0] = blocker;
        Assert.Equal(59790, AiSpeedPlanning.TargetOffset(c, scan, 7, s, 7, true));
    }

    [Theory]
    [InlineData(-2, 0x8000)]
    [InlineData(0, 0x4000)]
    [InlineData(7, 0x80)]
    [InlineData(13, 2)]
    [InlineData(15, 1)]
    public void TrackMaskBits_AreReversedAndClamped(int lane, int expected) => Assert.Equal(expected, AiSpeedPlanning.LaneBit(lane));

    [Fact]
    public void AheadMask_PenalizesEndingLaneAndRequestsSideOffset()
    {
        var s = Straight(20).Select(sl => sl with { Mask = 0xFFFF }).ToArray();
        s[4] = s[4] with { Mask = (ushort)(0xFFFF ^ AiSpeedPlanning.LaneBit(8)) };
        var c = new CarObj { Lane = 7, AiSpeed = 4 * Fx.One };
        var scores = new int[3];
        Assert.Equal(1, AiSpeedPlanning.ScoreMask(c, s, false, 4, scores));
        Assert.Equal(new[] { 0, 0, -40 * Fx.One }, scores);
    }
}
