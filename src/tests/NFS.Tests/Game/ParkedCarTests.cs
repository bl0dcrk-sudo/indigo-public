using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Parked cars of SMnn.DAT (class 0x40): created after the racers, with their own model [ref:3178]
/// body, and placed from the track data. [ref:3179]</summary>
public class ParkedCarTests
{
    [GameFact]
    public void Loader_CreatesParkedCarsFromTheSmackableRecords_WithTheirOwnBody()
    {
        var s = TrackSessions.FreshReplay();
        Assert.NotNull(s.Smackables);
        var recs = s.Smackables!.Cars.Take(s.Setup.NumSmackables).ToArray();
        Assert.NotEmpty(recs);
        Assert.NotNull(s.Parked);
        Assert.Equal(recs.Length, s.Parked!.Count);
        var info = NFS.Core.Tables.GameTables.Default.CarInfo;
        for (int i = 0; i < recs.Length; i++)
        {
            var p = s.Parked[i];
            var r = recs[i];
            Assert.Equal(CarFlags.Static, p.Flags);
            Assert.Equal(s.Cars.Count + i, p.PlayerIndex);                       // index after the racers [ref:3180]
            Assert.Equal(1, p.Rail);                                             // +0x8C = 1 [ref:3181]
            Assert.Equal(new Vec3(r.X, r.Y, r.Z), p.Pos);
            Assert.Equal(r.Slice, p.Trk.Slice);
            Assert.Equal(FxMath.RotY(r.Heading), p.Orient);                      // (heading) [ref:3182]
            Assert.Equal(info[r.Model * NFS.Core.Tables.GameTables.CarInfoStride + 1], p.Mass);   // mass of the model [ref:3183]
            Assert.True(p.HalfWidth > 0 && p.HalfLength > 0 && p.Radius > 0, "box from the model's .geo");
        }
    }

    [GameFact]
    public void Runner_ParkedCarsJoinTheNeighbourScanAndThePhysicsQueues()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        for (int t = 0; t < 40; t++) run.Step(default);
        var parked = run.Session.Parked!;
        // far from the player they stay on rails, on the road plane (rail branch) [ref:3184]
        foreach (var p in parked)
        {
            Assert.Equal(CarFlags.Static, p.Flags);
            Assert.Equal(0, p.AirTicks);
            Assert.Equal(Vec3.Zero, p.Vel);
        }
    }
}
