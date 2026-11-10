using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>The spectator of a network round (protocol v13) follows the leader of the ranking with its camera and HUD, Q/E (or the gear
/// keys) pick the previous / next car, W returns to the leader. A driver's drive is not affected by any of it.</summary>
public sealed class SpectatorCameraTests
{
    static readonly DriveKeys NoKeys = default;

    sealed class Provider(LocalRaceProvider source) : IRaceSessionProvider
    {
        readonly Queue<RaceViewFrame> _frames = new();
        RaceViewFrame? _latest;
        public RaceSessionDescription Description => source.Description;
        public long SubmitInput(int seat, PlayerInput input) => 1;
        public RaceViewState ReadLatestState() => source.ReadLatestState() with { Frame = _latest ?? source.ReadLatestState().Frame };
        public IReadOnlyList<RaceViewFrame> DrainFrames()
        {
            var frames = source.DrainFrames().Concat(_frames).ToArray();
            _frames.Clear();
            return frames;
        }
        public void Publish(RaceViewFrame frame) { _latest = frame; _frames.Enqueue(frame); }
        public IReadOnlyList<RaceEvent> DrainEvents() => source.DrainEvents();
        public bool RequestPause() => false;
        public void RequestResume() { }
        public void RequestLeave() { }
        public void Dispose() { }
    }

    /// <summary>A frame of step <paramref name="step"/> with the given ranking; car <paramref name="car"/> (when >= 0) moved to X metres (new pose epoch).</summary>
    static RaceViewFrame At(RaceViewFrame basis, int step, IReadOnlyList<int> ranking, int car = -1, float x = 0) => basis with
    {
        SnapshotSequence = step, Steps = step, SimulationTick64 = 2 * step, Kind = RaceViewFrameKind.Step, Ranking = ranking,
        Cars = basis.Cars.Select((c, i) => i == car
            ? c with { PoseEpoch = c.PoseEpoch + 1, State = c.State with { Pos = c.State.Pos with { X = (int)(x * Fx.One) } } } : c).ToArray(),
    };

    /// <summary>The same frame with the given cars across the finish line (RaceState 2): they stay active and in the ranking.</summary>
    static RaceViewFrame Finished(RaceViewFrame frame, params int[] ids) => frame with
    {
        Cars = frame.Cars.Select(c => ids.Contains(c.CarId)
            ? c with { State = c.State with { RaceState = 2 } } : c).ToArray(),
    };

    static List<int> Order(RaceViewFrame frame, int leader)
    {
        var ids = Enumerable.Range(0, frame.Ranking.Count).Select(i => frame.Ranking[i]).ToList();
        ids.Remove(leader);
        ids.Insert(0, leader);
        return ids;
    }

    static (RaceScene Scene, Provider Remote, RaceViewFrame Basis, List<int> Order) Setup(LocalRaceProvider server, RaceScene scene)
    {
        var remote = new Provider(server);
        var basis = server.ReadLatestState().Frame;
        // The first frame of a race may have no ranking yet: take the racers of the grid.
        var ranking = Enumerable.Range(0, basis.Cars.Count).Where(id => basis.Car(id).Active != 0).Take(6).ToArray();
        Assert.True(ranking.Length >= 3, $"cars {basis.Cars.Count}, active {ranking.Length}, ranking {basis.Ranking.Count}");
        basis = basis with { Ranking = ranking };
        int leader = ranking.First(id => id != scene.PlayerCarId);
        return (scene, remote, basis, Order(basis, leader));
    }

    [GameDataFact]
    public void ASpectatorFollowsTheLeaderAndQeCycleThroughThePlacesUntilWIsPressed()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000, spectator: true);
        Assert.True(drive.Spectator);
        Assert.Equal(order[0], drive.WatchedCarId);
        Assert.NotEqual(scene.PlayerCarId, drive.WatchedCarId);
        Assert.True(drive.SpectatorAuto);

        drive.SpectateStep(1);
        Assert.Equal(order[1], drive.WatchedCarId);
        Assert.False(drive.SpectatorAuto);
        drive.SpectateStep(-1);
        drive.SpectateStep(-1);                                    // before the leader: wraps around to the last place
        Assert.Equal(order[^1], drive.WatchedCarId);
        drive.SpectateStep(1);
        Assert.Equal(order[0], drive.WatchedCarId);
        for (int i = 0; i < order.Count; i++) drive.SpectateStep(1);   // a whole lap of the places
        Assert.Equal(order[0], drive.WatchedCarId);
        drive.SpectateStep(1);
        drive.SpectateAuto();
        Assert.True(drive.SpectatorAuto);
        Assert.Equal(order[0], drive.WatchedCarId);
    }

    [GameDataFact]
    public void TheAutomaticViewFollowsTheNewLeaderButAChosenCarStays()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        long now = 1_000_000;
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now, spectator: true);
        var overtaken = Order(basis, order[1]);                    // the second car takes the lead
        now += 500_000;
        remote.Publish(At(basis, 11, overtaken));
        drive.Frame(.001, NoKeys);
        Assert.Equal(order[1], drive.WatchedCarId);                // automatic: the new leader

        drive.SpectateStep(1);                                     // by hand: the car now second
        int chosen = drive.WatchedCarId;
        now += 500_000;
        remote.Publish(At(basis, 21, Order(basis, order[2])));     // the lead changes again
        drive.Frame(.001, NoKeys);
        Assert.NotEqual(order[2], chosen);
        Assert.Equal(chosen, drive.WatchedCarId);                  // the choice stays
        drive.Frame(.001, NoKeys with { SpectateAuto = true });
        Assert.Equal(order[2], drive.WatchedCarId);
    }

    [GameDataFact]
    public void TheKeysChangeTheCarOncePerPressAndTheGearKeysDoTheSame()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000, spectator: true);
        var next = NoKeys with { SpectateNext = true };
        drive.Frame(1.0 / 60, next);
        Assert.Equal(order[1], drive.WatchedCarId);
        drive.Frame(1.0 / 60, next);                               // held: no repeat
        Assert.Equal(order[1], drive.WatchedCarId);
        drive.Frame(1.0 / 60, default);
        drive.Frame(1.0 / 60, NoKeys with { GearUp = true });     // LB/RB on a pad, +/- on touch
        Assert.Equal(order[2], drive.WatchedCarId);
        drive.Frame(1.0 / 60, default);
        drive.Frame(1.0 / 60, NoKeys with { SpectatePrev = true });
        Assert.Equal(order[1], drive.WatchedCarId);
        drive.Frame(1.0 / 60, default);
        drive.Frame(1.0 / 60, NoKeys with { GearDown = true });
        Assert.Equal(order[0], drive.WatchedCarId);
        drive.Frame(1.0 / 60, default);
        drive.Frame(1.0 / 60, NoKeys with { SpectateAuto = true });
        Assert.True(drive.SpectatorAuto);
    }

    [GameDataFact]
    public void TheCameraAndTheHudFollowTheWatchedCarAndNeverShowTheDashboard()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        long now = 1_000_000;
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now, spectator: true);
        drive.Cam.Locator = null;
        drive.DriverNames = new Dictionary<int, string> { [order[1]] = "Łuk" };
        now += 500_000;
        remote.Publish(At(basis, 11, order, car: order[0], x: 500));   // the leader is far away from the grid
        drive.Frame(.001, NoKeys);
        Assert.InRange(drive.Cam.Position.X, 400, 600);
        Assert.Equal(drive.View.Car(order[0]).Pos, drive.WatchedView.Pos);
        Assert.NotEqual(drive.View.Car(scene.PlayerCarId).Pos, drive.WatchedView.Pos);
        Assert.False(string.IsNullOrWhiteSpace(drive.WatchedName));   // an AI car: its configured name
        drive.SpectateStep(1);
        Assert.Equal("LUK", drive.WatchedName);
        drive.Frame(.001, NoKeys);
        Assert.Equal(order[1], drive.WatchedCarId);
        Assert.False(drive.ShowMirror);
        Assert.False(drive.CockpitDrawn);

        // The cockpit view (it would draw the dashboard of the seat car) is left for the hood view at once.
        var view = drive.WatchedView;
        drive.Cam.SetReplayMode(0, RenderPose.From(view.Pos, view.Orient), view);
        drive.Frame(1.0 / 60, default);
        Assert.NotEqual(0, drive.Cam.Mode);
        drive.Render(new FrameBuffer());                           // the whole picture draws
    }

    [GameDataFact]
    public void ADriverIsNotAffectedByTheSpectatorKeysOrApi()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        long now = 1_000_000;
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now);
        Assert.False(drive.Spectator);
        Assert.Equal(scene.PlayerCarId, drive.WatchedCarId);
        drive.Cam.Locator = null;
        now += 500_000;
        remote.Publish(At(basis, 11, order, car: order[0], x: 500));
        drive.Frame(.001, NoKeys with { SpectateNext = true, SpectateAuto = true });
        drive.SpectateStep(1);
        drive.SpectateAuto();
        Assert.Equal(scene.PlayerCarId, drive.WatchedCarId);
        Assert.Equal(drive.PlayerView.Pos, drive.WatchedView.Pos);
        Assert.True(drive.Cam.Position.X < 100, "the camera stays with the player's own car");
    }

    [GameDataFact]
    public void TheAutomaticViewSkipsCarsThatFinishedAndTheManualCycleKeepsThemAtTheEnd()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        long now = 1_000_000;
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now, spectator: true);
        Assert.Equal(order[0], drive.WatchedCarId);

        now += 500_000;                                            // the leader crosses the line: the view moves on to the first still racing
        remote.Publish(Finished(At(basis, 11, order), order[0]));
        drive.Frame(.001, NoKeys);
        Assert.True(drive.SpectatorAuto);
        Assert.Equal(order[1], drive.WatchedCarId);

        drive.SpectateStep(-1);                                    // towards the leader: the finished car is last in the cycle
        Assert.Equal(order[0], drive.WatchedCarId);
        drive.SpectateStep(1);
        Assert.Equal(order[1], drive.WatchedCarId);
        drive.SpectateStep(1);
        Assert.Equal(order[2], drive.WatchedCarId);

        now += 500_000;                                            // two finished: the first of the rest is followed
        remote.Publish(Finished(At(basis, 21, order), order[0], order[1]));
        drive.Frame(.001, NoKeys with { SpectateAuto = true });
        Assert.Equal(order[2], drive.WatchedCarId);

        now += 500_000;                                            // everybody finished: the leader of the ranking again
        remote.Publish(Finished(At(basis, 31, order), order.ToArray()));
        drive.Frame(.001, NoKeys);
        Assert.Equal(order[0], drive.WatchedCarId);
    }

    [GameDataFact]
    public void ThePlacesListIsReusedAndDoesNotAllocateEveryFrame()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000, spectator: true);
        drive.SpectateAuto();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) drive.SpectateAuto();       // recomputes the candidates each time, the target never changes
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2000, $"allocated {allocated} B in 2000 refreshes");
    }

    [GameDataFact]
    public void ASpectatorSkipsTheCockpitInTheCycleAndKeepsTheRememberedModeWhileADriverRemembersEveryMode()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        remote.Publish(At(basis, 1, order));
        var cycle = NoKeys with { CycleCamera = true };

        var spectator = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000, spectator: true);
        Assert.True(spectator.Cam.SkipCockpit);
        int remembered = spectator.Cam.HudconCamMode;
        var view = spectator.WatchedView;
        spectator.Cam.SetReplayMode(3, RenderPose.From(view.Pos, view.Orient), view);
        spectator.Frame(.001, cycle);                              // 3 -> 0 would be the cockpit: straight to 1
        Assert.Equal(1, spectator.Cam.Mode);
        Assert.Equal(remembered, spectator.Cam.HudconCamMode);     // the key of a spectator does not write the driver's view

        var driver = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000);
        Assert.False(driver.Cam.SkipCockpit);
        var dview = driver.WatchedView;
        driver.Cam.SetReplayMode(3, RenderPose.From(dview.Pos, dview.Orient), dview);
        driver.Frame(.001, cycle);
        Assert.Equal(RaceCamera.NextMode(3, scene.Session.Cars[scene.PlayerCarId].Setup.CarType), driver.Cam.Mode);
        Assert.Equal(driver.Cam.Mode, driver.Cam.HudconCamMode);   // unchanged behaviour: the cockpit (or its successor) is remembered
    }

    [GameDataFact]
    public void TwoSpectatorKeysPressedInTheSameFrameBothAct()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var (_, remote, basis, order) = Setup(server, scene);
        remote.Publish(At(basis, 1, order));
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000, spectator: true);
        drive.Frame(1.0 / 60, NoKeys with { SpectatePrev = true, SpectateNext = true });   // one back and one forward: the leader, chosen by hand
        Assert.Equal(order[0], drive.WatchedCarId);
        Assert.False(drive.SpectatorAuto);
        drive.Frame(1.0 / 60, default);
        drive.Frame(1.0 / 60, NoKeys with { SpectateAuto = true, SpectateNext = true });   // W first, then E: the second car
        Assert.Equal(order[1], drive.WatchedCarId);
        Assert.False(drive.SpectatorAuto);
    }
}
