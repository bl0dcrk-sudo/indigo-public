using NFS.Core;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

/// <summary>AI reversing, the lane narrowing row [ref:3042]
/// and the trafcfg glue factors. Expected values follow the original's code. [ref:3043]</summary>
public class AiReverseTests
{
    private static TrackSlice Slice(byte lanes, sbyte upY = 126) =>
        new(0, 0, 0, [0, upY, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0xFFFF, 0x500, 0x500, 0x11, lanes, 8, 8, 0, 0);

    [Fact]
    public void ReverseToggle_ForwardToReverseOnlyWhenSlow_AndAfterHalfTheDuration()
    {
        var c = new CarObj { Direction = 1, DirDuration = 0x60, DirChangeTick = 100, AiSpeed = 0x50000 };
        AiDriveModel.ReverseToggle(c, 0x40, 100 + 0x30);          // 0x60/2 = 0x30 is not yet "greater than"
        Assert.Equal(1, c.Direction);
        AiDriveModel.ReverseToggle(c, 0x40, 100 + 0x31);
        Assert.Equal((-1, 0x40, 100 + 0x31), (c.Direction, c.DirDuration, c.DirChangeTick));

        var fast = new CarObj { Direction = 1, DirDuration = 0, DirChangeTick = 0, AiSpeed = -0x140000 };
        AiDriveModel.ReverseToggle(fast, 0x40, 10);               // |v| >= 20 m/s never starts reversing
        Assert.Equal(1, fast.Direction);

        c.DirChangeTick = 200;                                    // reversing → forward after half the (new) duration
        AiDriveModel.ReverseToggle(c, 0x40, 200 + 0x20);
        Assert.Equal(-1, c.Direction);
        AiDriveModel.ReverseToggle(c, 0x40, 200 + 0x21);
        Assert.Equal((1, 200 + 0x21), (c.Direction, c.DirChangeTick));
    }

    [Fact]
    public void ReverseUpdate_OnlyWhileReversing_ClampsTargetAndZeroesLaneOffset()
    {
        var f = new CarObj { Direction = 1, AiTargetSpeed = 0x500000, SmoothedLaneOffset = 77 };
        AiDriveModel.ReverseUpdate(f, 500);
        Assert.Equal((0x500000, 77), (f.AiTargetSpeed, f.SmoothedLaneOffset));

        var c = new CarObj { Direction = -1, DirDuration = 0x40, DirChangeTick = 480, AiTargetSpeed = -0x500000, SmoothedLaneOffset = 77 };
        AiDriveModel.ReverseUpdate(c, 500);
        Assert.Equal((-1, -0x140000, 0), (c.Direction, c.AiTargetSpeed, c.SmoothedLaneOffset));
        c.AiTargetSpeed = 0x500000;
        AiDriveModel.ReverseUpdate(c, 500);
        Assert.Equal(0x140000, c.AiTargetSpeed);

        // reverse time elapsed → forward again, timer restarted
        var d = new CarObj { Direction = -1, DirDuration = 0x10, DirChangeTick = 480 };
        AiDriveModel.ReverseUpdate(d, 500);
        Assert.Equal((1, 500), (d.Direction, d.DirChangeTick));

        // far (> 10 m) on the wrong side of the lateral target speed: the timer is pushed back to tick − 0x18
        var e = new CarObj { Direction = -1, DirDuration = 0x40, DirChangeTick = 0, LateralOffsetCur = -0xB0000, LateralSpeedAhead = 5 };
        AiDriveModel.ReverseUpdate(e, 500);
        Assert.Equal((-1, 500 - 0x18), (e.Direction, e.DirChangeTick));
    }

    [Fact]
    public void OffroadCheck_CountsLodTicksOutsideRoadway_AndRespectsSteepness()
    {
        var sl = Slice(0x11);                                      // roadway lanes [6, 7]
        var c = new CarObj { Lane = 7, OffRoadTicks = 3, Rail = 1 };
        Assert.False(AiDriveModel.OffroadCheck(c, sl, 2));
        Assert.Equal(0, c.OffRoadTicks);                           // on the roadway: counter reset

        c.Lane = 4;                                                // shoulder, flat slice up.y·512 = 0xFC00 > 0xD999 → no back-up
        Assert.False(AiDriveModel.OffroadCheck(c, sl, 2));
        Assert.Equal(2, c.OffRoadTicks);
        Assert.True(AiDriveModel.OffroadCheck(c, Slice(0x11, 100), 2));   // 100·512 = 0xC800 ≤ 0xD999 → back up
        Assert.False(AiDriveModel.OffroadCheck(c, Slice(0x11, 100), 2));  // 6 ticks > 4 → off for good
        Assert.Equal(6, c.OffRoadTicks);
    }

    [Fact]
    public void ScoreNarrowing_PenalisesLanesThatEndAhead()
    {
        // 20 slices: roadway lanes [5..8] (0x21 → hi 2, lo 1: [7−2, 6+1]) up to slice 9, narrower [6..7] from slice 10 on
        var track = Enumerable.Range(0, 20).Select(i => Slice((byte)(i < 10 ? 0x21 : 0x11))).ToArray();
        var c = new CarObj { Lane = 6, AiSpeed = 0 };
        c.Trk.Slice = 5;                                           // look-ahead max(5, |v|·0.625) = 5 slices → slice 10
        var s = new int[3];
        AiSpeedPlanning.ScoreNarrowing(c, track, false, s);
        Assert.Equal([-40 * Fx.One, 0, 0], s);                     // lane 5 is a roadway lane here but not at slice 10

        c.AiFlags = 0x1000;                                        // oncoming car looks the other way (slice 0, wide road)
        c.Trk.Slice = 8;
        s = new int[3];
        AiSpeedPlanning.ScoreNarrowing(c, track, false, s);
        Assert.Equal([0, 0, 0], s);

        c.AiFlags = 0; c.Trk.Slice = 12;                           // already on the narrow part: lane 5 is outside here
        s = new int[3];
        AiSpeedPlanning.ScoreNarrowing(c, track, false, s);
        Assert.Equal([0, 0, 0], s);
    }

    [GameFact]
    public void GlueFactors_ShippedTrafCfg_SingleRace8Laps_Is0_95()
    {
        var t = NFS.Data.Ai.TrafCfg.Parse(GameData.ReadRaw("GAMEDATA/SIM/AI/TRAFCFG.DAT"));
        // single race (group 0), 8 laps, sim, no traffic, many opponents: 0.95 ("8 laps 0.95"); [ref:3044]
        var g = AiDriver.ReadGlueFactors(t.GlueGroup(0), 8, false, 1, false, true);
        Assert.Equal((62259, 68985), (g.Speed, g.SpeedInv));
        Assert.Equal(0x1_0000_0000L / g.Speed, g.SpeedInv);
        Assert.Equal(0x1_0000_0000L / g.Acc, g.AccInv);
    }
}
