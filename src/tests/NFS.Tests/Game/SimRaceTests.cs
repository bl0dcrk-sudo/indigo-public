using NFS.Core;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Game;

/// <summary>
/// Race in the SIMULATION driving style (CONFIG +0x0B = 0 → style 0 → calls the sim model [ref:3228]
/// ): the whole loop (input → force model → collisions) runs for hundreds of ticks [ref:3229]
/// on the original track/car data. Sanity of behaviour, not bit-exactness (no sim recording of the original exists).
/// </summary>
public class SimRaceTests(ITestOutputHelper output)
{
    private static RaceRunner Start(bool withAi = false)
    {
        var run = new RaceRunner(TrackSessions.FreshReplaySim(), 0, automatic: true, withAi: withAi);
        while (!run.RaceStarted) run.Step(default);
        return run;
    }

    private static double Ms(CarObj c) => c.Vel.Z == 0 && c.Vel.X == 0 && c.Vel.Y == 0 ? 0 : Math.Sqrt(
        Math.Pow(c.Vel.X / 65536.0, 2) + Math.Pow(c.Vel.Y / 65536.0, 2) + Math.Pow(c.Vel.Z / 65536.0, 2));

    [GameFact]
    public void StyleSimulation_SelectsSimModel_ReplayStyleSelectsArcade()
    {
        Assert.Equal(0, new RaceRunner(TrackSessions.FreshReplaySim(), 0).ArcadeMode);
        Assert.Equal(1, new RaceRunner(TrackSessions.FreshReplay(), 0).ArcadeMode);
    }

    [GameFact]
    public void FullThrottle_Accelerates_ShiftsUp_AndReachesSensibleTopSpeed()
    {
        var run = Start();
        var c = run.Car;
        int maxGear = 0, shifts = 0, lastGear = c.Gear;
        double prev5 = 0, vmax = 0;
        for (int i = 0; i < 640; i++)                                   // 20 s of 32 Hz sim ticks
        {
            run.Step(new PlayerInput(0, 255, 0, 0));
            if (c.Gear != lastGear) { shifts++; lastGear = c.Gear; }
            maxGear = Math.Max(maxGear, c.Gear);
            vmax = Math.Max(vmax, Ms(c));
            if (i % 64 == 63) { output.WriteLine($"t={(i + 1) / 32.0:F1}s v={Ms(c):F1} m/s gear={c.Gear} rpm={c.Rpm} slice={c.Trk.Slice} slip={c.SlipState}"); }
            if (i == 159) prev5 = Ms(c);
            Assert.True(Ms(c) < 120, $"speed {Ms(c)} at {i}");
        }
        Assert.True(prev5 > 15, $"5 s: {prev5:F1} m/s");                 // a sports car is well beyond 54 km/h after 5 s
        Assert.True(maxGear >= 4 && shifts >= 3, $"gears: max {maxGear}, shifts {shifts}");
        Assert.InRange(vmax, 35, 100);                                  // 126 … 360 km/h
        Assert.True(c.Vel.Y > -10 * Fx.One && c.Orient.M11 > 0x4000);   // still on its wheels
    }

    [GameFact]
    public void Steering_TurnsTheCarTowardsTheWheel_WithLateralForceBoundedByGrip()
    {
        double Yaw(sbyte steer, out double latAccel, out double vEnd)
        {
            var run = Start();
            var c = run.Car;
            for (int i = 0; i < 100; i++) run.Step(new PlayerInput(0, 255, 0, 0));      // ~22 m/s on the first straight
            double v0 = Ms(c);
            Assert.InRange(v0, 15, 40);
            for (int i = 0; i < 20; i++) run.Step(new PlayerInput(steer, 128, 0, 0));
            Assert.Equal(0, run.WallHits + run.EdgeHits);                              // a clean corner, no wall contact
            vEnd = Ms(c);
            double w = c.AngVel.Y / 65536.0 * 2 * Math.PI;                              // rad/s
            latAccel = Math.Abs(w * vEnd);
            return c.AngVel.Y;
        }
        int right = Math.Sign(Yaw(50, out double aR, out double vR));
        int left = Math.Sign(Yaw(-50, out double aL, out double vL));
        output.WriteLine($"steer +50: lateral {aR:F1} m/s^2 at {vR:F1} m/s; steer -50: {aL:F1} m/s^2 at {vL:F1} m/s");
        Assert.NotEqual(0, right);
        Assert.Equal(-right, left);                                                     // symmetric
        // carSpecs +0x138 (lateral grip multiplier, 3.0) lets a road car pull well beyond 1 g; the friction circle caps it near 3 g
        Assert.InRange(aR, 5, 35);
        Assert.InRange(aL, 5, 35);
        Assert.InRange(Math.Abs(aR - aL) / Math.Max(aR, aL), 0, 0.2);
        var straight = Start();
        for (int i = 0; i < 120; i++) straight.Step(new PlayerInput(0, 255, 0, 0));
        Assert.True(Math.Abs(straight.Car.AngVel.Y) < 0.05 * 65536, "no steering, no rotation");
    }

    [GameFact]
    public void FullBrake_StopsTheCar_WithRoadCarDeceleration()
    {
        var run = Start();
        var c = run.Car;
        for (int i = 0; i < 100; i++) run.Step(new PlayerInput(0, 255, 0, 0));
        double v0 = Ms(c);
        Assert.True(v0 > 15, $"v0 {v0}");
        int stop = -1;
        for (int i = 0; i < 160; i++)                                                  // 5 s of full brake
        {
            run.Step(new PlayerInput(0, 0, 255, 0));
            Assert.True(Ms(c) < 120);
            if (stop < 0 && Ms(c) < 0.5) stop = i + 1;
        }
        double decel = v0 / (stop / 32.0);
        output.WriteLine($"brake: {v0:F1} m/s -> stop after {stop / 32.0:F2} s (mean {decel:F1} m/s^2)");
        Assert.True(stop > 0, "the car must stop within 5 s");
        Assert.InRange(decel, 4, 20);                                                  // 0.4 … 2 g with the brake ramp and tyre grip
        Assert.True(Ms(c) < 0.5 && Math.Abs(c.LocalVel.Z) < Fx.One);                   // stays stopped (brake at 0xFF, no creep)
    }

    [GameFact]
    public void CarSetup_SteeringSensitivityAndFinalDrive_ReachCarspecsDeriveInSim()
    {
        var normal = new RaceRunner(TrackSessions.FreshReplaySim(), 0).Specs;
        var tuned = new RaceRunner(TrackSessions.FreshReplaySim(b =>
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x78 + 0x24), 0x8000);    // steering sensitivity 0.5
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x78 + 0x2C), 0x20000);   // final drive 2.0
        }), 0).Specs;
        Assert.Equal(Fx.Mul(normal.MaxSteering, 0x8000), tuned.MaxSteering);                                   // S.12C *= cfg+0x24 [ref:3230]
        Assert.Equal(Fx.Div(normal.VelocityToRpm[2], 0x20000), tuned.VelocityToRpm[2]);                        // S.0C[g] /= cfg+0x2C
    }

    [GameFact]
    public void RoadEdgeHit_InSim_RunsDriveLandingReset_NotTheArcadeWallSlowdown()
    {
        var run = Start();
        var c = run.Car;
        for (int i = 0; i < 100; i++) run.Step(new PlayerInput(0, 255, 0, 0));
        int hits = run.EdgeHits;
        bool seen = false;
        for (int i = 0; i < 120 && !seen; i++)
        {
            run.Step(new PlayerInput(90, 128, 0, 0));                                   // into the wall of the first corner
            if (run.EdgeHits > hits)
            {
                seen = true;
                // local human, != 1: slip state, slip angle and skid cleared [ref:3231]
                Assert.Equal((0, 0, 0, 0), (c.SlipState, c.SlipAngle, c.Skid324, c.Skid328));
                var f = c.DriveFrame;                                                   // and +0x2B0/+0x2B8 = v · drive right / forward
                Assert.Equal(Fx.Mul(c.Vel.X, f.M00) + Fx.Mul(c.Vel.Y, f.M01) + Fx.Mul(c.Vel.Z, f.M02), c.LocalVel.X);
                Assert.Equal(Fx.Mul(c.Vel.X, f.M20) + Fx.Mul(c.Vel.Y, f.M21) + Fx.Mul(c.Vel.Z, f.M22), c.LocalVel.Z);
            }
        }
        Assert.True(seen, "the car must reach the road edge");
    }

    [GameFact]
    public void SimDiffersFromArcade_OnTheSameInputs()
    {
        double Run(RaceSession s)
        {
            var run = new RaceRunner(s, 0, automatic: true);
            while (!run.RaceStarted) run.Step(default);
            for (int i = 0; i < 160; i++) run.Step(new PlayerInput(0, 255, 0, 0));
            return Ms(run.Car);
        }
        double sim = Run(TrackSessions.FreshReplaySim()), arc = Run(TrackSessions.FreshReplay());
        output.WriteLine($"5 s full throttle: sim {sim:F1} m/s, arcade {arc:F1} m/s");
        Assert.NotEqual(sim, arc);
    }

    [GameFact]
    public void ManualGearbox_ShiftsOnlyOnRequest()
    {
        var run = new RaceRunner(TrackSessions.FreshReplaySim(), 0, automatic: false);
        while (!run.RaceStarted) run.Step(default);
        var c = run.Car;
        run.Step(new PlayerInput(0, 0, 0, PlayerInput.BtnGearUp));
        run.Step(default);
        for (int i = 0; i < 100; i++) run.Step(new PlayerInput(0, 255, 0, 0));
        int g = c.Gear;
        Assert.True(g >= 2, $"gear {g}");
        for (int i = 0; i < 100; i++) run.Step(new PlayerInput(0, 255, 0, 0));
        Assert.Equal(g, c.Gear);                                                   // a manual box never shifts by itself
    }

    [GameFact]
    public void FullRaceWithAi_Runs500Ticks_WithoutExceptions_AndKeepsAllCarsSane()
    {
        var run = Start(withAi: true);
        Assert.True(run.Cars.Count >= 2);
        for (int i = 0; i < 520; i++)
        {
            run.Step(new PlayerInput(0, 255, 0, 0));
            if (i % 130 == 129)
                foreach (var car in run.Cars)
                {
                    var o = car.Obj;
                    Assert.True(Ms(o) < 150 && Math.Abs(o.Pos.Y) < 20000 * 65536.0, $"car {o.Id:X} v {Ms(o):F1}");
                }
        }
        foreach (var car in run.Cars)
            output.WriteLine($"car {car.Obj.Id:X} ai={car.Ai != null} v={Ms(car.Obj):F1} gear={car.Obj.Gear} slice={car.Obj.Trk.Slice}");
        Assert.Contains(run.Cars.Where(x => x.Ai != null), x => Ms(x.Obj) > 5);   // the AI drives too (own model, sim parameter set)
    }
}
