using NFS.Core;
using NFS.Game.Race;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Replay camera director and its switching events: [ref:3218]
/// timing of the cycle, the shared draws and the waiting of modes 5/6 (values from the original's formulas and tables). [ref:3219]</summary>
public class ReplayCameraTests
{
    const int Slices = 1000;
    static CarObj Car(int slice, int speed = 0x100000) { var c = new CarObj { AiSpeed = speed }; c.Trk.Slice = slice; return c; }

    static int Expected(uint state, out uint next)
    {
        next = state * 0x41C64E6D + 0x3039;
        return (int)((next >> 16) & 0x7FFF);
    }

    /// <summary>Runs the camera over <paramref name="ticks"/> race ticks starting at the race start 0x200.</summary>
    static int Run(ReplayCamera cam, CarObj car, int ticks, int from = 0x200)
    {
        int first = -1;
        for (int t = from; t < from + ticks; t++)
        {
            int before = cam.Draws;
            cam.Cycle(t, true, car, Slices);
            cam.Update(t, true, car, Slices);
            if (first < 0 && cam.Draws != before) first = t;
        }
        return first;
    }

    [Fact]
    public void CameraTables_AreTheOnesOfTheOriginal()
    {
        Assert.Equal([4, 6, 7, 5, 6, 1], ReplayCamera.DefaultModes);                              // [ref:3220]
        Assert.Equal([0x40000, 0xA0000, 0x140000, 0x280000, 0x3C0000, 0x500000, 0x700000, 0x8C0000], ReplayCamera.TrackSpeeds); // [ref:3221]
        Assert.Equal([0x40, 0x20, 0x10, 8, 6, 4, 3, 2], ReplayCamera.TrackTicks);                  // [ref:3222]
    }

    [Fact]
    public void FirstChange_AfterThe140Ticks_DrawsOnceAndSelectsMode6()
    {
        var rand = new WatcomRandom();
        var cam = new ReplayCamera(rand);
        Assert.Equal(4, cam.Mode);
        int at = Run(cam, Car(100), 0x200);
        Assert.Equal(0x200 + 0x141, at);                    // 0x140 < (tick - 0x200) - 0
        Assert.Equal(6, cam.Mode);
        Assert.Equal(1, cam.Draws);
        int r = Expected(1, out uint st);
        Assert.Equal(st, rand.State);                      // exactly one step [ref:3223]
        Assert.Equal((r >> 11) < 4 ? ((r >> 11) < 2 ? 0x547A : -0x5486) : 0, cam.Field50);
    }

    [Fact]
    public void NoReplay_NoDraw_ButTheModeStillChanges()
    {
        var rand = new WatcomRandom();
        var cam = new ReplayCamera(rand) { Playing = false };
        Run(cam, Car(100), 0x200);
        Assert.Equal(6, cam.Mode);
        Assert.Equal(0, cam.Draws);
        Assert.Equal(1u, rand.State);
    }

    [Fact]
    public void NothingBeforeTheRaceStarts()
    {
        var cam = new ReplayCamera(new WatcomRandom());
        for (int t = 0; t < 0x800; t++) cam.Cycle(t, false, Car(0), Slices);
        Assert.Equal(0, cam.Draws);
        Assert.Equal(4, cam.Mode);
    }

    /// <summary>Car moving one slice per camera step (speed 0x100000: index 1, 0x20 ticks per slice, camera 5 ahead) keeps the camera
    /// from jumping; the switch after the 0x280 ticks of mode 6 then waits.</summary>
    [Fact]
    public void Mode6_AfterTheLongSpan_WaitsForTheCameraToJump()
    {
        var cam = new ReplayCamera(new WatcomRandom());
        var car = Car(100);
        int start = 0x200 + 0x141;
        for (int t = 0x200; t < start + 0x300; t++)
        {
            car.Trk.Slice = 100 + Math.Max(0, t - start) / 0x20;
            cam.Cycle(t, true, car, Slices);
            cam.Update(t, true, car, Slices);
        }
        Assert.Equal(6, cam.Mode);                           // index 2 was selected at +0x281 but mode 6 keeps running
        Assert.Equal(1, cam.Draws);
        // the car drives away fast: the camera falls >= 8 slices behind and jumps – only now the next mode is set (draw 2)
        for (int t = start + 0x300, s = car.Trk.Slice; t < start + 0x800 && cam.Draws == 1; t++, s += (t & 3) == 0 ? 1 : 0)
        {
            car.Trk.Slice = s;
            cam.Cycle(t, true, car, Slices);
            cam.Update(t, true, car, Slices);
        }
        Assert.Equal(2, cam.Draws);
        Assert.Equal(7, cam.Mode);                           // table index 2
    }

    [Fact]
    public void CarWithNonZeroField288_GetsMode2InsteadOf6()
    {
        var cam = new ReplayCamera(new WatcomRandom());
        var car = Car(100);
        car.WrongWayTicks = 5;                               // car+0x288 != 0 → table entries 1 and 4 become 2
        Run(cam, car, 0x141 + 4);
        Assert.Equal(2, cam.Mode);
        Assert.Equal(1, cam.Draws);
    }
}
