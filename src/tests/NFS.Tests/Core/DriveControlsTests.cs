using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>(hand-computed from the original's formulas). [ref:3062]</summary>
public class DriveControlsTests
{
    private static DriveControlSpecs Specs() => new()
    {
        NumGears = 8, GearShiftDelay = 6, Redline = 7000, TurnInRamp = 10, TurnOutRamp = 20,
        BrakeIncreasing = [5, 6, 7, 8, 9, 10, 11, 12], BrakeDecreasing = [50, 51, 52, 53, 54, 55, 56, 57],
        // speed at redline = (f * (7000 << 16)) >> 16 = f * 7000 (raw 16.16): gear 2 ≈ 10.04 m/s, 3 ≈ 20.1, 4 ≈ 30.0 …
        GearSpeedFactor = [0, 0, 94, 188, 281, 375, 468, 562, 0, 0],
    };

    private static readonly DriveOptions Ramps = new(Automatic: true, SteerRamp: true, SlowThrottle: false, BrakeRamp: true);

    [Fact]
    public void ThrottleRamp_StepsBy24Or16()
    {
        var c = new CarObj { Throttle = 248, Gear = 2, RequestedGear = 2 };
        var sig = DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0x18, c.ThrottleRamped);
        Assert.Equal(25 * 0x10000 / 0xF8, sig.Throttle);
        DriveControls.Filter(c, Specs(), Ramps with { SlowThrottle = true }, true, 0x400);
        Assert.Equal(0x28, c.ThrottleRamped);
        c.Throttle = 0x20;
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0x20, c.ThrottleRamped);                 // |diff| < step → exact
        c.ThrottleRamped = 247;
        c.Throttle = 248;
        Assert.Equal(0x10000, DriveControls.Filter(c, Specs(), Ramps, true, 0x400).Throttle);   // (248+1)/248 → clamp
    }

    [Fact]
    public void BrakeRamp_UsesSpecTablesByBrakeLevel()
    {
        var c = new CarObj { Brake = 200, BrakeRamped = 0x40, Gear = 2, RequestedGear = 2 };
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0x40 + 7, c.BrakeRamped);                // table index 0x40 >> 5 = 2
        c.Brake = 0;
        c.BrakeRamped = 0xA0;
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0xA0 - 55, c.BrakeRamped);               // decreasing[5]
        c.Brake = 0x30;
        DriveControls.Filter(c, Specs(), Ramps with { BrakeRamp = false }, true, 0x400);
        Assert.Equal(0x30, c.BrakeRamped);
        DriveControls.Filter(c, Specs(), Ramps, false, 0x400);
        Assert.Equal(0xFF, c.BrakeRamped);                    // race not started → full brake
    }

    [Fact]
    public void SteerRamp_TurnInAndReturnToCentre()
    {
        var c = new CarObj { SteerTarget = 100, Steer = 0, Gear = 2, RequestedGear = 2 };
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(10, c.Steer);
        c.Steer = 95;
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(100, c.Steer);
        c.SteerTarget = -40;                                  // opposite sign → back toward 0 by TurnOut
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(80, c.Steer);
        c.Steer = 15;
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0, c.Steer);
        var sig = DriveControls.Filter(c, Specs(), Ramps, true, 0x400);   // now 0 → toward -40 by TurnIn
        Assert.Equal(-10, c.Steer);
        Assert.Equal(9 * 0x10000 / 128, sig.SteerAbs);       // |steer + 1|
        c.SteerTarget = 0; c.Steer = -5;
        DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal(0, c.Steer);
        c.SteerTarget = 77;
        DriveControls.Filter(c, Specs(), Ramps with { SteerRamp = false }, true, 0x400);
        Assert.Equal(77, c.Steer);
    }

    [Fact]
    public void Finished_CutsThrottleAndCentresSteering()
    {
        var c = new CarObj { RaceState = 2, Throttle = 200, ThrottleRamped = 100, Steer = 50, GearChangeCounter = 3 };
        var sig = DriveControls.Filter(c, Specs(), Ramps, true, 0x400);
        Assert.Equal((0, 0xFF, 0, 3), (c.ThrottleRamped, c.BrakeRamped, c.Steer, c.GearChangeCounter));
        Assert.Equal((0x10000 / 0xF8, 0x10000), (sig.Throttle, sig.Brake));
    }

    [Fact]
    public void Automatic_ForcesDriveBeforeStartAndPicksGearBySpeed()
    {
        var c = new CarObj { Gear = 1, RequestedGear = 1 };
        c.LocalVel.Z = 25 * Fx.One;                           // above gear 2 (≈10) and 3 (≈20) redline speeds
        var sig = DriveControls.Filter(c, Specs(), Ramps, true, 0x100);
        Assert.True(sig.GearChanged);
        Assert.Equal((3, 1, 6), (c.Gear, c.PrevGear, c.GearChangeCounter));
        // D → D+ requests are ignored by the filter (handles them) [ref:3063]
        c.RequestedGear = 4;
        Assert.False(DriveControls.Filter(c, Specs(), Ramps, true, 0x400).GearChanged);
        Assert.Equal(3, c.Gear);
        c.RequestedGear = 1;                                  // D → N
        Assert.True(DriveControls.Filter(c, Specs(), Ramps, true, 0x400).GearChanged);
        Assert.Equal((1, 3), (c.Gear, c.PrevGear));
    }

    [Fact]
    public void Manual_TakesRequestedGearAndCountsDown()
    {
        var c = new CarObj { Gear = 2, RequestedGear = 3 };
        var o = Ramps with { Automatic = false };
        Assert.True(DriveControls.Filter(c, Specs(), o, true, 0x100).GearChanged);
        Assert.Equal((3, 2, 6), (c.Gear, c.PrevGear, c.GearChangeCounter));
        Assert.False(DriveControls.Filter(c, Specs(), o, true, 0x100).GearChanged);
        Assert.Equal(5, c.GearChangeCounter);
        c.RequestedGear = 5;
        Assert.False(DriveControls.Filter(c, Specs(), o, false, 0x100).GearChanged);   // not started: no change
        Assert.Equal(3, c.Gear);
    }

    [Fact]
    public void EdgeHit_CutsThrottle()
    {
        var c = new CarObj { EdgeHit = 2, Throttle = 200, Gear = 2, RequestedGear = 2 };
        Assert.Equal(0x10000 / 0xF8, DriveControls.Filter(c, Specs(), Ramps, true, 0x400).Throttle);
        Assert.Equal(0, c.ThrottleRamped);
    }

    // ------------------------------------------------------------ [ref:3064]
    // speed 1.0 → r(g) = 7000 * f / 65536: 61793 → 6600, 0x8000 → 3500, 0x4000 → 1750, 0x12000 → 7875

    [Theory]
    [InlineData(3, 0x10000, new[] { 0, 0, 0, 61793, 0, 0, 0, 0 }, 4)]               // full throttle, r > 6500 → up
    [InlineData(3, 0x8000, new[] { 0, 0, 0, 0x12000, 0, 0, 0, 0 }, 4)]              // part throttle but over-rev → up
    [InlineData(4, 0x8000, new[] { 0, 0, 0, 0x8000, 0x8000, 0, 0, 0 }, 3)]          // part: dn rpm 3500 < 5500 → down
    [InlineData(4, 0x8000, new[] { 0, 0, 0, 61793, 0x8000, 0, 0, 0 }, 4)]           // part: dn rpm 6600 → stay
    [InlineData(5, 0x10000, new[] { 0, 0, 0, 0x4000, 0x4000, 0x8000, 0, 0 }, 3)]    // full: dn2 first
    [InlineData(5, 0x10000, new[] { 0, 0, 0, 61793, 0x4000, 0x8000, 0, 0 }, 4)]     // full: dn2 too high → dn
    [InlineData(7, 0x10000, new[] { 0, 0, 0, 0, 0, 61793, 61793, 61793 }, 7)]       // top gear: no up, downs too high
    [InlineData(2, 0x10000, new[] { 0, 0, 0x8000, 0, 0, 0, 0, 0 }, 2)]              // 1st gear: dn = dn2 = gear
    [InlineData(1, 0x10000, new[] { 0, 0x12000, 0, 0, 0, 0, 0, 0 }, 1)]             // N: nothing
    public void AutoShift(int gear, int throttle, int[] f, int expected)
    {
        var s = Specs();
        s.GearRpmFactor = [.. f, 0, 0];
        var c = new CarObj { Gear = (byte)gear, SignedSpeed = Fx.One };
        bool shifted = DriveControls.AutoShift(c, s, throttle);
        Assert.Equal(expected, c.Gear);
        Assert.Equal(expected != gear, shifted);
        if (shifted) Assert.Equal((gear, 6), (c.PrevGear, c.GearChangeCounter));
    }
}
