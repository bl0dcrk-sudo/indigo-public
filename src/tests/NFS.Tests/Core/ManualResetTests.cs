using NFS.Core;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Core;

public class ManualResetTests
{
    static TrackSlice[] Track(byte surroundings = 0, byte left = 2, byte right = 6) =>
        Enumerable.Range(0, 32).Select(i => new TrackSlice(0, 0, i * 4 * Fx.One,
            [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, surroundings, 0x11, left, right, 0, 0)).ToArray();

    [GameTheory]
    [InlineData(false, 31, 1, 0, 0x10001)]
    [InlineData(true, 0, 30, 0x20, 0x8000)]
    [InlineData(false, 5, 7, 2, 0x8000)]
    public void Reset_WrapsInRaceDirection_RaisesCarAndClearsMotion(bool reverse, int start, int end, byte tunnel, int height)
    {
        var c = new CarObj { Flags = CarFlags.LocalHuman, Active = 1, Rail = 1,
            HalfHeight = 0, Vel = new(123, 456, 789), AngVel = new(10, 20, 30), Crashing = 1, Gear = 6, SlipState = 3 };
        c.Trk.Slice = start;
        var st = new int[29]; st[9] = 2; st[11] = 0x10001;
        var env = new CarPostEnv { SimTune = st, Slices = Track(tunnel), Reverse = reverse,
            TrackUpdate = car => car.Trk.RoadPoly = new object() };
        Assert.True(CarPost.ManualReset(c, env, true));
        Assert.Equal(end, c.Trk.Slice);
        Assert.Equal(height, c.Pos.Y);
        Assert.InRange(c.Pos.X, 65000, 66000); // (right 3 m − left 1 m)/2 = 1 m
        Assert.Equal(reverse ? -64512 : 64512, c.Orient.M22);
        Assert.Equal(Vec3.Zero, c.Vel);
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal((2, 2, 0, 0, 1, 48, 48), ((int)c.Gear, (int)c.RequestedGear, c.SlipState,
            c.Crashing, (int)c.AirTicks, c.ManualResetLock, c.CollisionCooldown));
        Assert.False(CarPost.ManualReset(c, env, true));
    }

    [GameTheory]
    [InlineData(1, 0, true, true, 0)]
    [InlineData(0, 2, true, true, 0)]
    [InlineData(0, 0, false, true, 0)]
    [InlineData(0, 0, true, false, 0)]
    [InlineData(0, 0, true, true, 4)]
    public void Guards_RejectWithoutTouchingCar(int locked, int state, bool started, bool human, int crashSlice)
    {
        var c = new CarObj { ManualResetLock = locked, RaceState = state, Flags = human ? CarFlags.LocalHuman : CarFlags.Opponent,
            CrashSlice = crashSlice, Pos = new(123, 456, 789), Crashing = 7, Gear = 5 };
        int calls = 0;
        var env = new CarPostEnv { SimTune = new int[29], Slices = [], TrackUpdate = _ => calls++ };
        Assert.False(CarPost.ManualReset(c, env, started));
        Assert.Equal(new Vec3(123, 456, 789), c.Pos);
        Assert.Equal(7, c.Crashing);
        Assert.Equal(5, c.Gear);
        Assert.Equal(0, calls);
    }

    [GameTheory]
    [InlineData(2, 6, 1)]
    [InlineData(6, 2, -1)]
    public void Search_TriesWiderSideThenFallsBackToCentre(byte left, byte right, int direction)
    {
        var c = new CarObj { Flags = CarFlags.LocalHuman, Active = 1, Rail = 1 };
        var positions = new List<int>();
        var env = new CarPostEnv { SimTune = new int[29], Slices = Track(0, left, right),
            TrackUpdate = car => positions.Add(car.Pos.X) }; // no road polygon, every placement rejected
        Assert.True(CarPost.ManualReset(c, env, true));
        Assert.Equal(23, positions.Count); // initial +21 attempts + centre fallback, last attempt not checked
        Assert.Equal(0, positions[^1]);
        Assert.Equal(direction, Math.Sign(positions[1] - positions[0]));
        Assert.Equal(0, c.Pos.X);
    }

    [GameFact]
    public void Runner_ResetButtonUsesOriginalGuardsAndLockout()
    {
        var r = new RaceRunner(TrackSessions.FreshReplay(), automatic: false);
        r.Step(new(0, 0, 0, PlayerInput.BtnReset));
        Assert.Equal(0, r.ManualResets);
        while (!r.RaceStarted) r.Step(default);
        r.Step(new(0, 0, 0, PlayerInput.BtnReset));
        Assert.Equal(1, r.ManualResets);
        Assert.Equal(47, r.Car.ManualResetLock); // post task decrements once after input task
        r.Step(new(0, 0, 0, PlayerInput.BtnReset));
        Assert.Equal(1, r.ManualResets);
        while (r.Car.ManualResetLock != 0) r.Step(default);
        r.Step(new(0, 0, 0, PlayerInput.BtnReset));
        Assert.Equal(2, r.ManualResets);
    }
}
