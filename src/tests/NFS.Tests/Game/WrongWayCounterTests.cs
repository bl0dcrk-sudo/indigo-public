using NFS.Core;
using NFS.Data.Track;
using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>counts the ticks a car spends facing against the track direction (car + 0x288) [ref:3273]
/// turns that count into the HUD centre-band state (hud5 → hud6 "WRONG WAY!" → hud7).</summary>
public class WrongWayCounterTests
{
    /// <summary>Straight synthetic track along +Z (slice direction byte +0xF = 126), 4 m per slice.</summary>
    static TrackSlice[] Track(int n = 8) => Enumerable.Range(0, n).Select(i => new TrackSlice(
        0, 0, i * 4 * Fx.One, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, 0x22, 8, 8, 0, 0)).ToArray();

    /// <summary>Car whose orient forward row (+0xDC) is <paramref name="forward"/>.</summary>
    static CarObj Car(int x, int y, int z) =>
        new() { Orient = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, x, y, z) };

    static CarObj AlongTrack(int slice = 0) { var c = Car(0, 0, Fx.One); c.Trk.Slice = slice; return c; }
    static CarObj AgainstTrack() => Car(0, 0, -Fx.One);
    static void Face(CarObj c, bool along) => c.Orient = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0, along ? Fx.One : -Fx.One);
    static void Ticks(CarObj c, TrackSlice[] s, bool reverse, int n) { for (int i = 0; i < n; i++) WrongWayCounter.Advance(c, s, reverse); }

    [Fact]
    public void CounterOnTheCar_CountsUpAgainstTheTrackAndResetsWhenFacingForward()
    {
        var slices = Track();
        var car = AgainstTrack();
        Ticks(car, slices, false, 5);
        Assert.Equal(5, car.WrongWayTicks);
        Assert.Equal(0, WrongWayCounter.Band(car.WrongWayTicks));
        Face(car, true);                                    // facing forward again clears the count
        Ticks(car, slices, false, 1);
        Assert.Equal(0, car.WrongWayTicks);
        Ticks(car, slices, false, 200);
        Assert.Equal(0, car.WrongWayTicks);
    }

    [Fact]
    public void ReversedTrack_InvertsWhichWayIsWrong()
    {
        var slices = Track();
        var car = AlongTrack();
        Ticks(car, slices, true, 1);
        Assert.Equal(1, car.WrongWayTicks);                 // on a reversed track +Z is the wrong way
        Face(car, false);
        Ticks(car, slices, true, 1);
        Assert.Equal(0, car.WrongWayTicks);
    }

    [Fact]
    public void Band_ChangesAtSixtyFourAndHundredFortyEightTicks()
    {
        var slices = Track();
        var back = AgainstTrack();
        for (int i = 1; i <= 0x3F; i++) { Ticks(back, slices, false, 1); Assert.Equal(0, WrongWayCounter.Band(back.WrongWayTicks)); }
        Ticks(back, slices, false, 1);
        Assert.Equal((0x40, 1), (back.WrongWayTicks, WrongWayCounter.Band(back.WrongWayTicks)));   // hazard band
        while (back.WrongWayTicks < 0x94 - 1) Ticks(back, slices, false, 1);
        Assert.Equal(1, WrongWayCounter.Band(back.WrongWayTicks));
        Ticks(back, slices, false, 1);
        Assert.Equal((0x94, 2), (back.WrongWayTicks, WrongWayCounter.Band(back.WrongWayTicks))); // solid yellow
    }

    [Fact]
    public void AirborneCrashingAndFinishedCarsFollowTheOriginalGuards()
    {
        var slices = Track();
        var back = AgainstTrack();
        Ticks(back, slices, false, 10);
        back.AirTicks = 3;
        Ticks(back, slices, false, 1);
        Assert.Equal(10, back.WrongWayTicks);               // airborne: the count is held
        back.AirTicks = 0;
        back.Crashing = 1;
        Ticks(back, slices, false, 1);
        Assert.Equal(10, back.WrongWayTicks);               // crashing: held as well
        back.Crashing = 0;
        back.RaceState = 2;
        Ticks(back, slices, false, 1);
        Assert.Equal(0, back.WrongWayTicks);                // finished: cleared
    }

    [Fact]
    public void SliceOutsideTheTrackClearsTheCount()
    {
        var slices = Track(4);
        var back = AgainstTrack();
        Ticks(back, slices, false, 3);
        Assert.Equal(3, back.WrongWayTicks);
        back.Trk.Slice = 9;
        Ticks(back, slices, false, 1);
        Assert.Equal(0, back.WrongWayTicks);
    }
}
