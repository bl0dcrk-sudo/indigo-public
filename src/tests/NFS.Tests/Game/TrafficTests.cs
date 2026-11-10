using NFS.Core;
using NFS.Core.Tables;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Traffic (class 4) of a single race: front-end picks, loader (ptrNN.dat, STRD.dat), the cycle [ref:3253]
/// park → spawn wait → spawn → place → relocate (<see cref="AiTraffic"/>) and its place in the runner. [ref:3254]</summary>
public class TrafficTests
{
    private static readonly Lazy<RaceTags> Tags = new(() => RaceTags.FromConstants());

    /// <summary>Single race, one opponent, traffic on; menu track 1..7 (default 2 = race track 4).</summary>
    internal static RaceSetup Setup(Action<FeConfig>? edit = null, byte sequence = 0)
    {
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.OpponentCar = 0; cfg.TrafficOff = 0; cfg.Track = 2; cfg.Style = 2;
        cfg.Backward = 0; cfg.Mirrored = 0;
        edit?.Invoke(cfg);
        var b = new FeRaceArgsBuilder() { SequenceIndex = sequence };
        return RaceArgsParser.Parse(Tags.Value, b.Build(cfg));
    }

    internal static RaceSession Session(Action<FeConfig>? edit = null, byte sequence = 0)
        => new RaceLoader(GameData.Dir).Load(Setup(edit, sequence));

    internal static RaceRunner Run(Action<FeConfig>? edit = null, byte sequence = 0)
        => new(Session(edit, sequence), 0, automatic: true, withAi: true);

    private static readonly int[][] Pools =
    [
        [19, 21, 22, 25, 27, 29, 30, 31], [19, 22, 25, 30, 31], [18, 23, 24, 26, 27, 29, 31, 32],
        [18, 19, 20, 21, 22, 23, 24, 25, 26, 28, 32], [18, 25, 26, 28, 29, 32], [19, 22, 25, 30, 33], [34, 35, 34, 35],
    ];

    private static int SliceDelta(int a, int b, int n)
    {
        int d = a - b;
        if (d > n / 2) d -= n; else if (d < -n / 2) d += n;
        return d;
    }

    private static NFS.Data.Track.TrackSlice Slice(byte lanes) =>
        new(0, 0, 0, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, lanes, 8, 8, 0, 0);

    [GameFact]
    public void FrontEnd_SameSequence_SameCars_AndOtherSequencesCanDiffer()
    {
        var a = Setup(sequence: 5).Cars.Select(c => (c.CarType, c.Colour)).ToArray();
        Assert.Equal(a, Setup(sequence: 5).Cars.Select(c => (c.CarType, c.Colour)).ToArray());
        Assert.Contains(Enumerable.Range(0, 40), s => !Setup(sequence: (byte)s).Cars.Select(c => (c.CarType, c.Colour)).SequenceEqual(a));
    }

    [GameFact]
    public void FrontEnd_NoTraffic_WhenOff_FullGrid_ProvingGrounds_OrNotASingleRace()
    {
        Assert.Equal(4, Setup().Cars.Count(c => c.CarClass == 4));
        Assert.DoesNotContain(Setup(c => c.TrafficOff = 1).Cars, c => c.CarClass == 4);
        Assert.DoesNotContain(Setup(c => c.OpponentCount = 2).Cars, c => c.CarClass == 4);   // full grid
        Assert.DoesNotContain(Setup(c => c.Track = 0).Cars, c => c.CarClass == 4);          // Proving Grounds
        Assert.DoesNotContain(Setup(c => c.RaceType = 1).Cars, c => c.CarClass == 4);       // tournament
        Assert.DoesNotContain(Setup(c => c.RaceType = 2).Cars, c => c.CarClass == 4);       // knockout
        // the byte sequence is consumed only when traffic is emitted
        var b = new FeRaceArgsBuilder();
        var cfg = FeConfig.Load(GameData.Dir); cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.TrafficOff = 1;
        b.Build(cfg);
        Assert.Equal(0, b.SequenceIndex);
    }

    [GameFact]
    public void Menu_BuildSetup_KeepsTheSequenceAcrossRaces()
    {
        var menu = MenuController.Load(GameData.Dir);
        menu.Config.GameType = 0; menu.Config.RaceType = 0; menu.Config.OpponentCount = 1; menu.Config.TrafficOff = 0; menu.Config.Track = 2;
        var first = menu.BuildSetup().Cars.Where(c => c.CarClass == 4).Select(c => (c.CarType, c.Colour)).ToArray();
        var second = menu.BuildSetup().Cars.Where(c => c.CarClass == 4).Select(c => (c.CarType, c.Colour)).ToArray();
        Assert.Equal(4, first.Length);
        var viaBuilder = new FeRaceArgsBuilder();
        var s1 = RaceArgsParser.Parse(Tags.Value, viaBuilder.Build(menu.Config)).Cars.Where(c => c.CarClass == 4).Select(c => (c.CarType, c.Colour));
        var s2 = RaceArgsParser.Parse(Tags.Value, viaBuilder.Build(menu.Config)).Cars.Where(c => c.CarClass == 4).Select(c => (c.CarType, c.Colour));
        Assert.Equal(first, s1);
        Assert.Equal(second, s2);                         // the second race continues the sequence (8 steps on)
    }

    [GameFact]
    public void Menu_StartRace_WithTraffic_LoadsTheSession_WithFourTrafficCars()
    {
        var menu = MenuController.Load(GameData.Dir);
        menu.Config.GameType = 0; menu.Config.RaceType = 0; menu.Config.OpponentCount = 1; menu.Config.TrafficOff = 0;
        menu.Config.OpponentCar = 0; menu.Config.Track = 2; menu.Config.Style = 1;
        menu.Config[0x59] = 0;                                // saved copy of the option (restores it when the track leaves Proving Grounds) [ref:3255]
        menu.StartRace();
        Assert.NotNull(menu.Session);                         // no longer refused with "traffic simulation is incomplete"
        Assert.Equal(4, menu.Session!.Cars.Count(c => c.Setup.CarClass == 4));
        Assert.Equal(2, menu.Session.Cars.Count(c => c.Setup.CarClass is 1 or 2));
        var run = new RaceRunner(menu.Session, 0, automatic: true, withAi: true);
        Assert.Equal(4, run.TrafficCars.Count);
    }

    // ---- loader ----

    [Fact]
    public void SpecsAndModelFileNames_FollowCarLoadSpecs()
    {
        Assert.Equal("pMCF1.dat", RaceLoader.SpecsFile(0, "MCF1", 0));
        Assert.Equal("ptr19.dat", RaceLoader.SpecsFile(19, "COMC", 0));
        Assert.Equal("ptr36.dat", RaceLoader.SpecsFile(0x24, "limo", 0));
        Assert.Equal("pjagr.dat", RaceLoader.SpecsFile(19, "COMC", 4));   // bit 4 [ref:3256]
        Assert.Equal("pcala.dat", RaceLoader.SpecsFile(0x25, "x", 0));
        Assert.Equal("MCF1.dat", RaceLoader.AiModelFile(0, "MCF1"));
        Assert.Equal("STRD.dat", RaceLoader.AiModelFile(19, "COMC"));
    }

    [GameFact]
    public void Loader_TrafficCars_AreAiCarsOfClass4_WithTheirOwnSpecsAndBody()
    {
        var s = Session();
        var traffic = s.Cars.Where(c => c.Setup.CarClass == 4).ToArray();
        Assert.Equal(4, traffic.Length);
        foreach (var t in traffic)
        {
            Assert.Equal(CarFlags.NotLocal | CarFlags.Traffic, t.Obj.Flags);
            Assert.Equal(t.Index | 0x100, t.Obj.Id);           // every configured car idx|0x100 [ref:3257]
            Assert.NotNull(t.Specs);                           // PTRnn.DAT
            Assert.NotNull(t.AiModel);                         // STRD.dat
            Assert.NotNull(t.Geo);
            Assert.True(t.Obj.Mass > 0 && t.Obj.HalfWidth > 0 && t.Obj.HalfLength > 0 && t.Obj.Radius > 0);
        }
    }

    // ---- track parameters ----

    [Fact]
    public void TrackParameters_AreTheRecordsOfTheTrackParameterInit()
    {
        var sl = new[] { Slice(0x11), Slice(0x11) };
        (int Count, int Delay, bool OneWay, int Side)[] expected =
        [
            (0, 500, true, 1), (0, 0, false, 0), (4, 0x514, false, -1), (12, 400, false, 1), (8, 800, false, 1),
            (12, 400, false, 1), (3, 0x1194, true, 1), (3, 0x1194, false, 1), (3, 0x1194, true, 1),
        ];
        for (int t = 0; t < 9; t++)
        {
            var p = TrafficTrackParams.For(t, sl, 0);
            Assert.Equal(expected[t], (p.Count, p.SpawnDelay, p.OneWay, p.LaneSide));
        }
        Assert.Equal(0xF0A3, TrafficTrackParams.For(5, sl, 0).SimFactor);
        Assert.Equal(0xFAE1, TrafficTrackParams.For(5, sl, 0).ArcadeFactor);
        // game option 0x400: 15 cars, no delay (track 1 is never set)
        Assert.Equal((15, 0), (TrafficTrackParams.For(4, sl, 0x400).Count, TrafficTrackParams.For(4, sl, 0x400).SpawnDelay));
        Assert.Equal(0, TrafficTrackParams.For(1, sl, 0x400).Count);
        // tracks >= 9: defaults 12 / 500, one-way when slice 1 has no left lanes
        var d = TrafficTrackParams.For(9, sl, 0);
        Assert.Equal((12, 500, false), (d.Count, d.SpawnDelay, d.OneWay));
        Assert.True(TrafficTrackParams.For(9, [Slice(0x11), Slice(0x03)], 0).OneWay);
    }

    [GameFact]
    public void OneWayTracks_AreTheOnesWithoutLeftHalfWidth()
    {
        foreach (int t in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var col = NFS.Data.Track.Col.Load($"GAMEDATA/TRACKS/SE/TR{t:00}.COL", GameData.Dir);
            Assert.Equal(col.Slices[100].HalfWidthLeft == 0, TrafficTrackParams.For(t, col.Slices, 0).OneWay);
        }
    }

    // ---- cycle: reset / park ----

    [GameFact]
    public void Runner_StartsWithEveryTrafficCarParked_AfterOneLcgDrawEach()
    {
        var s = Session();
        var run = new RaceRunner(s, 0, automatic: true, withAi: true);
        var env = run.Traffic!;
        var traffic = run.TrafficCars;
        Assert.Equal(4, traffic.Count);
        Assert.Equal(4, env.Parked);                                   // after [ref:3258]
        var rng = new AiRandom(s.Setup.Random);
        foreach (var t in traffic)
        {
            var o = t.Obj;
            Assert.NotEqual(0, o.AiFlags & AiTraffic.FlagParked);
            Assert.Equal(0, o.Active);                                  // +0x8D = 0
            Assert.Same(o, o.Anchor);                                   // +0x3B0 = self [ref:3259]
            Assert.Equal(0, o.Trk.Slice);                               // (c, 0, {0, 100 m, 0}) [ref:3260]
            uint r = (uint)((rng.Next() & 0xFFFF00) >> 8);              // one draw [ref:3261]
            Assert.Equal((int)(r * (uint)env.SpawnDelayRange >> 16) + 1, o.SpawnDelay);
        }
        Assert.Equal(rng.State, run.AiRng.State);                       // nothing else drew before the first tick
        Assert.Equal(rng.Last, run.AiRng.Last);
    }

    [GameFact]
    public void Runner_WithoutTraffic_HasNoTrafficState_AndTheLcgIsUntouched()
    {
        var s = Session(c => c.TrafficOff = 1);
        var run = new RaceRunner(s, 0, automatic: true, withAi: true);
        Assert.Null(run.Traffic);
        Assert.Empty(run.TrafficCars);
        var fresh = new AiRandom(s.Setup.Random);
        Assert.Equal(fresh.State, run.AiRng.State);
        Assert.Equal(fresh.Last, run.AiRng.Last);
    }

    // ---- cycle: spawn / place ----

    private sealed record Spawn(int Tick, SimCar Car, int Slice, int PlayerSlice, int Lateral, bool Oncoming, int Anchor, int Active, CarObj? AnchorCar);

    /// <summary>Drives the player with the autopilot and records every parked → active transition.</summary>
    private static (List<Spawn> Spawns, int MaxActive) Drive(RaceRunner run, int ticks, Action<int>? each = null)
    {
        var spawns = new List<Spawn>();
        var parked = run.TrafficCars.ToDictionary(c => c, c => (c.Obj.AiFlags & AiTraffic.FlagParked) != 0);
        int maxActive = 0;
        for (int t = 0; t < ticks; t++)
        {
            run.Step(RaceRunnerAiTests.Autopilot(run));
            each?.Invoke(t);
            foreach (var c in run.TrafficCars)
            {
                bool now = (c.Obj.AiFlags & AiTraffic.FlagParked) != 0;
                if (parked[c] && !now)
                    spawns.Add(new Spawn(run.Tick, c, c.Obj.Trk.Slice, run.Car.Trk.Slice, c.Obj.LaneTargetOffset,
                        (c.Obj.AiFlags & AiTraffic.FlagOncoming) != 0, c.Obj.Anchor!.Trk.Slice, c.Obj.Active, c.Obj.Anchor));
                parked[c] = now;
            }
            maxActive = Math.Max(maxActive, run.TrafficCars.Count(c => (c.Obj.AiFlags & AiTraffic.FlagParked) == 0));
        }
        return (spawns, maxActive);
    }

    [GameFact]
    public void Traffic_SpawnsNearThePlayer_AfterTheScheduleOfTheLeadRacer_AndNeverMoreThanTheTrackLimit()
    {
        var run = Run();                                   // race track 4: limit 8, delay 800
        int n = run.Session.Slices.Length;
        var (spawns, maxActive) = Drive(run, 32 * 90);
        Assert.NotEmpty(spawns);
        Assert.True(maxActive <= run.Traffic!.Max);
        Assert.All(spawns, s =>
        {
            Assert.Equal(1, s.Active);                          // +0x8D = 1 at the spawn
            Assert.Same(run.Car, s.AnchorCar);                  // the lone local human
            Assert.InRange(Math.Abs(SliceDelta(s.Slice, s.Anchor, n)), 48, 62); // 50 + (mid16·11 >> 16) slices (± one tick of motion)
            Assert.True(s.Tick > 0x200);                        // never during the countdown
        });
    }

    [GameFact]
    public void Traffic_ActiveCarsMove_AndStayOnTheRoad()
    {
        var run = Run();
        var (spawns, _) = Drive(run, 32 * 60);
        Assert.NotEmpty(spawns);
        var active = run.TrafficCars.Where(x => (x.Obj.AiFlags & AiTraffic.FlagParked) == 0).ToArray();
        Assert.NotEmpty(active);
        Assert.Contains(active, x => Math.Abs(x.Obj.AiSpeed) > 2 * Fx.One);
        Assert.All(active, x => Assert.InRange(x.Obj.LateralOffsetCur, -25 * Fx.One, 25 * Fx.One));
    }

    [GameFact]
    public void Traffic_SameDirectionCarsUseTheirSide_OncomingCarsTheOther_OnATwoWayTrack()
    {
        // race track 4, lane side +1: a car in the race direction takes the right lanes (x > 0), an oncoming one the left lanes;
        // on a backward race the sides swap (xor) [ref:3262]
        foreach (bool back in new[] { false, true })
        {
            var run = Run(c => c.Backward = (byte)(back ? 1 : 0));
            var (spawns, _) = Drive(run, 32 * 130);
            Assert.NotEmpty(spawns);
            Assert.Contains(spawns, s => s.Oncoming);
            Assert.Contains(spawns, s => !s.Oncoming);
            foreach (var s in spawns)
            {
                bool right = s.Oncoming == back;
                if (right) Assert.True(s.Lateral > 0, $"back={back} oncoming={s.Oncoming} x={s.Lateral}");
                else Assert.True(s.Lateral < 0, $"back={back} oncoming={s.Oncoming} x={s.Lateral}");
            }
        }
    }

    [GameFact]
    public void Traffic_OncomingCarsFaceAgainstTheRaceDirection()
    {
        var run = Run();
        var (spawns, _) = Drive(run, 32 * 130, _ => { });
        var oncoming = spawns.Where(s => s.Oncoming).Select(s => s.Car).Distinct().ToArray();
        Assert.NotEmpty(oncoming);
        // place_on_slice with the reversed frame: the nose points against the track direction (slice forward · orientation < 0)
        foreach (var c in oncoming.Where(c => (c.Obj.AiFlags & AiTraffic.FlagParked) == 0 && (c.Obj.AiFlags & AiTraffic.FlagOncoming) != 0))
        {
            var sl = run.Session.Slices[c.Obj.Trk.Slice];
            long along = (long)c.Obj.Orient.M20 * sl.Forward[0] + (long)c.Obj.Orient.M22 * sl.Forward[2];
            Assert.True(along < 0, "an oncoming car points against the race direction");
        }
    }

    [GameFact]
    public void Traffic_OnAOneWayTrack_NeverComesOncoming()
    {
        var run = Run(c => c.Track = 5);                   // menu 5 → race track 6 (one-way, 3 cars)
        Assert.True(run.Traffic!.OneWay);
        var (spawns, _) = Drive(run, 32 * 140);
        Assert.NotEmpty(spawns);
        Assert.All(spawns, s => Assert.False(s.Oncoming));
        Assert.All(run.TrafficCars, c => Assert.Equal(0, c.Obj.AiFlags & AiTraffic.FlagOncoming));
    }

    // ---- cycle: relocate / park ----

    [GameFact]
    public void Traffic_FarFromThePlayer_IsMovedNearThePlayerOrParkedAgain()
    {
        var run = Run();
        var (spawns, _) = Drive(run, 32 * 60);
        Assert.NotEmpty(spawns);
        int n = run.Session.Slices.Length;
        var player = run.Car;
        var active = run.TrafficCars.Where(c => (c.Obj.AiFlags & AiTraffic.FlagParked) == 0).ToArray();
        Assert.NotEmpty(active);
        // teleport the player 600 slices on: every active car is now far outside 244 m
        int target = (player.Trk.Slice + 600) % n;
        NewtonPlace.PlaceOnSlice(player, run.Session.Slices, target, new Vec3(0, 0x10000, 0), run.Session.Backward, run.Road.UpdateCar);
        var before = active.Select(c => c.Obj.Pos).ToArray();
        int parkedBefore = run.Traffic!.Parked;
        for (int t = 0; t < 32 * 4; t++) run.Step(default);
        int moved = 0, parked = 0;
        for (int i = 0; i < active.Length; i++)
        {
            var c = active[i].Obj;
            if ((c.AiFlags & AiTraffic.FlagParked) != 0) { Assert.Equal(0, c.Active); parked++; continue; }
            Assert.InRange(Math.Abs(SliceDelta(c.Trk.Slice, player.Trk.Slice, n)), 0, 75);   // placed again around the player
            Assert.NotEqual(before[i], c.Pos);
            moved++;
        }
        Assert.True(moved + parked == active.Length && run.Traffic!.Parked >= parkedBefore + parked);
    }

    [GameFact]
    public void Traffic_ParkedCarCountsDownItsDelay_WithoutMoving()
    {
        var run = Run();
        var car = run.TrafficCars[0];
        int delay = car.Obj.SpawnDelay;
        var pos = car.Obj.Pos;
        for (int t = 0; t < 40; t++) run.Step(default);
        Assert.NotEqual(0, car.Obj.AiFlags & AiTraffic.FlagParked);
        Assert.Equal(pos, car.Obj.Pos);
        Assert.True(car.Obj.SpawnDelay <= delay);
    }

    // ---- lap task, ranking, results ----

    [GameFact]
    public void Traffic_HasNoLapTask_IsNotRanked_AndIsNotInTheResults()
    {
        var run = Run();
        Drive(run, 32 * 80);
        Assert.All(run.TrafficCars, c =>
        {
            Assert.Equal(0, c.Obj.LapsDone);
            Assert.Equal(0, c.Obj.RaceState);
            Assert.Equal(0, c.Obj.RacePosition);
            Assert.Equal(CarFlags.None, c.Obj.Flags & CarFlags.Racer);
        });
        var ranking = run.Laps.Ranking();
        Assert.Equal(2, ranking.Length);                           // the player and the one opponent
        Assert.All(ranking, i => Assert.Equal(CarFlags.None, run.Cars[i].Obj.Flags & CarFlags.Traffic));
        var results = RaceResults.Capture(run);
        Assert.Equal(2, results.Racers.Count);
        Assert.DoesNotContain(results.Racers, r => run.Cars[r.CarIndex].Race.Setup.CarClass == 4);
    }

    [Fact]
    public void RaceEndMark_DoesNotEstimateOrFinishTraffic()
    {
        var human = new CarObj { Flags = CarFlags.Racer | CarFlags.LocalHuman, RaceState = 2, LastProgressTick = 0x300, DistanceSlices = 3000 };
        var opponent = new CarObj { Flags = CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent, DistanceSlices = 1000, LastProgressTick = 0x500 };
        var traffic = new CarObj { Flags = CarFlags.NotLocal | CarFlags.Traffic, DistanceSlices = 500, LastProgressTick = 0x500, LapStartTick = 0x200 };
        var p = new RaceProgress(1000, 3, [human, opponent, traffic], [true, false, false], [0, 1, 19]);
        p.CheckFinish(0x300 + RaceProgress.FinishDelay + 2);
        Assert.True(p.RaceOver);
        Assert.Equal(2, opponent.RaceState);        // the opponent's time is estimated
        Assert.Equal(0, traffic.RaceState);         // never touches class 4 [ref:3263]
        Assert.Equal(0x200, traffic.LapStartTick);
    }

    // ---- collision, determinism ----

    [GameFact]
    public void Player_CollidesWithAnActiveTrafficCar()
    {
        var run = Run();
        var (spawns, _) = Drive(run, 32 * 60);
        Assert.NotEmpty(spawns);
        var car = run.TrafficCars.First(c => (c.Obj.AiFlags & AiTraffic.FlagParked) == 0);
        var p = run.Car;
        var o = car.Obj;
        // 3 m in front of the player's nose (the cars overlap after a tick or two of the player's speed)
        o.Pos = new Vec3(p.Pos.X + Fx.Mul(p.Orient.M20, 3 * Fx.One), p.Pos.Y, p.Pos.Z + Fx.Mul(p.Orient.M22, 3 * Fx.One));
        o.Vel = default; o.CollisionCooldown = 0;
        int before = run.CarHits;
        for (int t = 0; t < 8; t++) run.Step(RaceRunnerAiTests.Autopilot(run));
        Assert.True(run.CarHits > before, "the player–traffic contact is a car-pair collision");
    }

    [GameFact]
    public void Traffic_IsDeterministic()
    {
        string Trace()
        {
            var run = Run();
            var sb = new System.Text.StringBuilder();
            Drive(run, 32 * 70, t =>
            {
                if (t % 16 == 0) foreach (var c in run.Cars) sb.Append($"{c.Obj.Pos.X},{c.Obj.Pos.Z},{c.Obj.AiFlags};");
            });
            sb.Append($"{run.AiRng.State}/{run.AiRng.Last}/{run.Traffic!.Parked}");
            return sb.ToString();
        }
        Assert.Equal(Trace(), Trace());
    }

    // ---- unit level: AiTraffic / AiSpeedPlanning ----

    [Fact]
    public void SpeedFunctions_FollowTheAsm()
    {
        var c = new CarObj { TrafficSpeedFactor = 0xE666 };          // 0.9
        Assert.Equal(Fx.Mul(30 * Fx.One, 0xE666), AiTraffic.SpeedScale(c, 30 * Fx.One));
        Assert.Equal(Fx.Mul(-10 * Fx.One, 0xE666), AiTraffic.SpeedScale(c, -10 * Fx.One));   // v <= 0: plain product
        c.TrafficSpeedFactor = 0x3333;                               // 0.2: the product falls below v − 13.4 m/s
        Assert.Equal(40 * Fx.One - 0xD6666, AiTraffic.SpeedScale(c, 40 * Fx.One));
        Assert.Equal(0x8E666, AiTraffic.SpeedScale(c, 10 * Fx.One)); // never below 8.9 m/s
        var anchor = new CarObj();
        var t = new CarObj { Anchor = anchor };
        (int Speed, int Expected)[] bands =
            [(0, 0x136666), (0xDCCCC, 0xDE666), (0x1BCCCC, 0xB1C28), (0x29B333, 0x6E666), (0x378000, 0x428F5), (0x456666, 0x2C51E)];
        foreach (var (speed, expected) in bands) { anchor.AiSpeed = speed; Assert.Equal(expected, AiTraffic.OncomingSpeed(t, false)); }
        anchor.AiSpeed = -0x500000;
        Assert.Equal(0x2C51E, AiTraffic.OncomingSpeed(t, true));      // reverse track: the anchor speed is negated
    }

    [Fact]
    public void WithinDistances_UseMaxPlusQuarterMin()
    {
        var o = new CarObj { Pos = new Vec3(0, 0, 0) };
        Assert.True(AiTraffic.Within200(200 * Fx.One, 0, o));
        Assert.False(AiTraffic.Within200(201 * Fx.One, 0, o));
        Assert.True(AiTraffic.Within200(160 * Fx.One, 160 * Fx.One, o));   // 160 + 40
        Assert.False(AiTraffic.Within200(170 * Fx.One, 170 * Fx.One, o));
        Assert.True(AiTraffic.Within244(244 * Fx.One, 0, o));
        Assert.False(AiTraffic.Within244(245 * Fx.One, 0, o));
    }

    [GameFact]
    public void Honk_DrawsOneLcgStep_OnlyForAnOncomingTrafficCarNearAHuman()
    {
        var run = Run();
        var env = run.Traffic!;
        var car = run.TrafficCars[0].Obj;
        var scan = new AiSpeedPlanning.Scan();
        scan.Speed[1] = 5 * Fx.One;
        car.Pos = run.Car.Pos;                                         // within 200 m of the human
        var shadow = new AiRandom { State = env.Rng.State, Last = env.Rng.Last, Multiplier = env.Rng.Multiplier };
        car.AiFlags &= ~AiTraffic.FlagOncoming;
        AiTraffic.Honk(car, env, scan, null);
        Assert.Equal((shadow.State, shadow.Last), (env.Rng.State, env.Rng.Last));   // not oncoming: no draw
        car.AiFlags |= AiTraffic.FlagOncoming;
        scan.Speed[1] = -0x40000;                                      // below −3 m/s: no draw
        AiTraffic.Honk(car, env, scan, null);
        Assert.Equal((shadow.State, shadow.Last), (env.Rng.State, env.Rng.Last));
        scan.Speed[1] = 5 * Fx.One;
        AiTraffic.Honk(car, env, scan, null);
        shadow.Next();
        Assert.Equal((shadow.State, shadow.Last), (env.Rng.State, env.Rng.Last));   // exactly one draw
    }

    [Fact]
    public void ScanOncoming_SeesCarsAheadInItsOwnDirection_AndTrafficHalvesTheLaneSpeeds()
    {
        var slices = Enumerable.Range(0, 200).Select(_ => Slice(0x22)).ToArray();
        var me = new CarObj { Flags = CarFlags.NotLocal | CarFlags.Traffic, Active = 1, AiFlags = 0x1000, Lane = 7, AiTargetSpeed = 12 * Fx.One };
        me.Trk.Slice = 100;
        CarObj Other(int slice, int speed) { var o = new CarObj { Flags = CarFlags.NotLocal | CarFlags.Traffic, Active = 1, AiSpeed = speed }; o.Trk.Slice = slice; return o; }
        var ahead = Other(90, 5 * Fx.One);      // 10 slices against the nose of an oncoming car (forward race)
        var behind = Other(110, 5 * Fx.One);    // behind it
        var cars = new[] { me, behind, ahead };
        var occ = new[] { 1 << 7, 1 << 7, 1 << 7 };
        var r = AiSpeedPlanning.ScanAhead(me, cars, slices, false, 30, occ);
        Assert.Same(ahead, r.Blocker[1]);
        Assert.Null(r.Blocker[0]);
        Assert.Null(r.Blocker[2]);
        // delta −10 + 2 slices at 0x50000 (0x1B77C near); the blocker is farther than 3 slices: no halving
        Assert.Equal(5 * Fx.One + (-8) * 0x50000, r.Speed[1]);
        Assert.Equal(5 * Fx.One + (-8) * 0x1B77C, r.SpeedNear[1]);
        // a blocker 2 slices ahead (< 3): the traffic car halves lane speeds
        ahead.Trk.Slice = 98;
        r = AiSpeedPlanning.ScanAhead(me, cars, slices, false, 30, occ);
        Assert.Equal((5 * Fx.One + 0 * 0x50000) >> 1, r.Speed[1]);
        // the blocker is not faster than the car's target → every column halved
        Assert.Equal(me.AiTargetSpeed >> 1, r.Speed[0]);
        Assert.Equal(me.AiTargetSpeed >> 1, r.Speed[2]);
    }

    [Fact]
    public void ScoreKeepSide_NeedsTraffic_AndPenalisesTheHalfAgainstTheTrafficDirection()
    {
        var sl = Slice(0x11);
        var c = new CarObj { Flags = CarFlags.NotLocal | CarFlags.Opponent, Lane = 9 };
        c.Trk.Slice = 0;
        // far enough into the race that the start phase (ListPosition·14 + progress ≤ limit + 0xF) is over
        c.Progress = 5000;
        var scores = new int[3];
        AiSpeedPlanning.ScoreKeepSide(c, sl, 1, false, 1, 0, scores);
        Assert.Equal([0, 0, 0], scores);                      // no traffic cars: no effect
        AiSpeedPlanning.ScoreKeepSide(c, sl, 1, false, 1, 4, scores);
        Assert.Equal([0, 0, 0], scores);                      // lane side +1, forward: keep right; lane 9 is already right
        c.Lane = 3; scores = new int[3];
        AiSpeedPlanning.ScoreKeepSide(c, sl, 1, false, 1, 4, scores);
        Assert.Equal([-0x30000, -0x30000, 0], scores);        // on the left half: the columns still on the left lose 3.0
        c.Lane = 7; scores = new int[3];
        AiSpeedPlanning.ScoreKeepSide(c, sl, 1, false, 1, 4, scores);
        Assert.Equal([-0x30000, 0, 0], scores);
        c.Lane = 9; scores = new int[3];
        AiSpeedPlanning.ScoreKeepSide(c, sl, 1, true, 1, 4, scores);
        Assert.Equal([0, -0x30000, -0x30000], scores);        // backward: the sides swap
    }

    [GameFact]
    public void SkinVariant_StartsAtZero_AndAdvancesAtEverySpawn()
    {
        var r = Run();
        var car = r.TrafficCars[0].Obj;
        Assert.Equal(0, car.TrafficSkin);                        // +0x634 = 0 [ref:3264]
        var seen = new List<int>();
        for (int i = 0; i < 4; i++) { AiTraffic.Spawn(car, r.Traffic!); seen.Add(car.TrafficSkin); }
        Assert.Equal([1, 2, 0, 1], seen);                        // (+0x634 + 1) % 3 [ref:3265]
        AiTraffic.Reset(car, r.Traffic!);
        Assert.Equal(0, car.TrafficSkin);
    }

    /// <summary>The "stuck" traffic car of the autopilot runs (e.g. on its roof after a hard hit, or wedged against the player):
    /// only calls for cars without flag 0x10 (traffic), and the only recovery is [ref:3266]
    /// – a distance (&gt; 244 m from the anchor), not a time or speed rule. The original behaves [ref:3267]
    /// the same, so the port keeps the car where it is while a human is within 244 m.</summary>
    [GameFact]
    public void FlippedTrafficCar_IsNotCrashReset_AndIsRelocatedOnlyWhenFarFromItsAnchor()
    {
        var r = Run();
        var car = r.TrafficCars[0].Obj;
        AiTraffic.Spawn(car, r.Traffic!);
        var env = new CarPostEnv { SimTune = new int[29], Slices = [], TrackUpdate = _ => { }, Tables = NFS.Core.Tables.GameTables.FromConstants() };
        car.UpDotN = -Fx.One; car.AutoResetCounter = 1;
        var before = (car.Pos, car.Orient);
        CarPost.ImpactProcess(car, env, 0);
        Assert.Equal(before, (car.Pos, car.Orient));             // no: it stays where (and how) it is [ref:3268]
        Assert.Equal((0, 0), (car.AutoResetCounter, car.Crashing));
        var human = r.Traffic!.Humans[0];
        Assert.True(AiTraffic.Within244(car.Pos.X, car.Pos.Z, human));
        Assert.False(AiTraffic.Relocate(car, r.Traffic!));       // near its anchor: nothing happens, however long it lies there
        Assert.Equal(before.Pos, car.Pos);
        car.Pos = new Vec3(car.Pos.X + 300 * Fx.One, car.Pos.Y, car.Pos.Z);
        Assert.False(AiTraffic.Within244(car.Pos.X, car.Pos.Z, human));
        Assert.True(AiTraffic.Relocate(car, r.Traffic!));        // far away: placed again near a human, or parked
        Assert.True(AiTraffic.Within244(car.Pos.X, car.Pos.Z, human) || (car.AiFlags & AiTraffic.FlagParked) != 0);
    }

    [GameTheory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void FrontEnd_FourTrafficCars_ModelsFromTheTrackPool_EachPoolSlotOnce(int menuTrack)
    {
        foreach (byte seq in new byte[] { 0, 1, 77, 200 })
        {
            var setup = Setup(c => c.Track = (ushort)menuTrack, seq);
            var traffic = setup.Cars.Where(c => c.CarClass == 4).ToArray();
            Assert.Equal(4, traffic.Length);
            var pool = Pools[menuTrack - 1];
            Assert.All(traffic, c => Assert.Contains(c.CarType, pool));
            // skips a used slot, so the four picks are four different slots (pool 7 lists its two models twice) [ref:3269]
            Assert.All(traffic.GroupBy(c => c.CarType), g => Assert.True(g.Count() <= pool.Count(m => m == g.Key)));
            var colours = NFS.Core.Constants.FrontEndConstants.TrafficColours.ToArray();
            Assert.All(traffic, c => Assert.Contains((byte)c.Colour, colours));
        }
    }

    [GameFact]
    public void FrontEnd_Picks_FollowTheByteTableOfTheExe_AndAdvanceTheSequenceByTwoPerCar()
    {
        var b = new FeRaceArgsBuilder() { SequenceIndex = 10 };
        var table = NFS.Core.Constants.FrontEndConstants.SequenceTable.ToArray();
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.TrafficOff = 0; cfg.Track = 2;
        var setup = RaceArgsParser.Parse(Tags.Value, b.Build(cfg));
        Assert.Equal(18, b.SequenceIndex);
        var colours = NFS.Core.Constants.FrontEndConstants.TrafficColours.ToArray();
        var pool = Pools[1];
        var used = new bool[pool.Length];
        int seq = 10;
        foreach (var car in setup.Cars.Where(c => c.CarClass == 4))
        {
            int i = table[seq++] % pool.Length;
            while (used[i]) i = (i + 1) % pool.Length;
            used[i] = true;
            Assert.Equal(pool[i], car.CarType);
            Assert.Equal(colours[table[seq++] % 16], car.Colour);
        }
    }
}
