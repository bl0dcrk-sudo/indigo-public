using NFS.Core;
using NFS.Data;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Render;

namespace NFS.Desktop.Tests;

public sealed class RemoteRaceDriveTests
{
    [GameDataFact]
    public void InitialReadStateRendersWithoutAnotherQueuedSnapshot()
    {
        var scene = new RaceScene("replay");
        using var source = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var provider = new RemoteViewProvider(source);
        Assert.Empty(provider.DrainFrames());
        var drive = new RaceDrive(scene, provider: provider, monotonicMicros: () => 1_000_000);
        var camera = drive.Cam.Position;
        drive.Frame(1.0 / 60, default);
        Assert.True(System.Numerics.Vector3.Distance(camera, drive.Cam.Position) < .01f);
        Assert.Equal(source.ReadLatestState().Frame.Car(scene.PlayerCarId).Pos, drive.PlayerView.Pos);
    }

    [GameDataFact]
    public void NormalServerClosureAfterResultsDoesNotShowConnectionLost()
    {
        var scene = new RaceScene("replay");
        using var source = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var provider = new RemoteViewProvider(source) { Connected = false };
        var drive = new RaceDrive(scene, provider: provider, monotonicMicros: () => 1_000_000);
        Assert.True(drive.ConnectionLost);
        drive.ToggleRemotePause();
        provider.Publish(source.ReadLatestState().Frame with { Kind = RaceViewFrameKind.Resync, Results = new RaceResults([]) });
        drive.Frame(1.0 / 60, default);
        Assert.NotNull(drive.Results);
        Assert.False(drive.ConnectionLost);
        Assert.False(drive.RemotePaused);
    }

    [GameDataFact]
    public void DriverNameLabelsShowRivalsButNotTheLocalPlayer()
    {
        var local = new RaceDrive(new RaceScene("replay"), countdown: false);
        Assert.False(local.ShowDriverLabels); // the original single-player screen is untouched

        var scene = new RaceScene("replay");
        using var source = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(source);
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000)
        {
            DriverNames = new Dictionary<int, string> { [scene.PlayerCarId] = "ANNA" },
        };
        Assert.True(drive.ShowDriverLabels);

        // The player's own car is right in front of the chase camera, yet its name is never drawn:
        // naming it must produce exactly the same frame as naming nobody.
        var ownName = new FrameBuffer();
        drive.Render(ownName);
        drive.DriverNames = new Dictionary<int, string>();
        var unnamed = new FrameBuffer();
        drive.Render(unnamed);
        Assert.Equal(unnamed.Sha256(), ownName.Sha256());

        // The rivals still carry their [bot] tags in a network race.
        drive.ShowDriverLabels = false;
        var withoutLabels = new FrameBuffer();
        drive.Render(withoutLabels);
        Assert.NotEqual(unnamed.Sha256(), withoutLabels.Sha256());
    }

    sealed class RemoteViewProvider(LocalRaceProvider source) : IRaceSessionProvider
    {
        readonly Queue<RaceViewFrame> _frames = new();
        RaceViewFrame? _latest;
        public Queue<RaceEvent> Events { get; } = new();
        public RaceSessionDescription Description => source.Description;
        public bool Connected = true;
        public bool Left;
        public List<PlayerInput> Inputs { get; } = [];
        public long SubmitInput(int seat, PlayerInput input) { Assert.Equal(0, seat); Inputs.Add(input); return Inputs.Count; }
        public RaceViewState ReadLatestState()
        {
            var s = source.ReadLatestState();
            return s with { Frame = _latest ?? s.Frame, Connected = Connected };
        }
        public IReadOnlyList<RaceViewFrame> DrainFrames()
        {
            var frames = source.DrainFrames().Concat(_frames).ToArray();
            _frames.Clear();
            return frames;
        }
        public void Publish(RaceViewFrame frame) { _latest = frame; _frames.Enqueue(frame); }
        public IReadOnlyList<RaceEvent> DrainEvents()
        {
            var events = source.DrainEvents().Concat(Events).ToArray();
            Events.Clear();
            return events;
        }
        public bool RequestPause() => false;
        public void RequestResume() { }
        public void RequestLeave() => Left = true;
        public void Dispose() { }
    }

    static RaceViewFrame At(RaceViewFrame basis, int car, int step, float x, int epoch = 0,
        RaceViewFrameKind kind = RaceViewFrameKind.Step) => basis with
    {
        SnapshotSequence = step, Steps = step, SimulationTick64 = 2 * step, Kind = kind,
        Cars = basis.Cars.Select((c, i) => i == car
            ? c with { PoseEpoch = epoch, State = c.State with { Pos = c.State.Pos with { X = (int)(x * Fx.One) } } } : c).ToArray(),
    };

    [GameDataTheory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void InputKeeps32HzBelow32FpsAndBoundsCatchUpAfterAStall(int fps)
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(server);
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000);
        for (int i = 0; i < 3 * fps; i++) drive.Frame(1.0 / fps, default(DriveKeys) with { Up = true });
        Assert.Equal(96, remote.Inputs.Count);
        Assert.All(remote.Inputs, input => Assert.Equal(255, input.Gas));
        drive.Frame(5, default);
        Assert.Equal(104, remote.Inputs.Count); // bounded eight-sample catch-up, then discard the old interval
        drive.Frame(0, default);
        Assert.Equal(104, remote.Inputs.Count);
    }

    [GameDataFact]
    public void RemoteTransportPollCanSendTheCurrentFrameInputBeforePresentingReplies()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(server);
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000);
        bool pollSawCurrentInput = false;

        drive.Frame(1.0 / 32, new DriveKeys(true, false, false, true, false, false, false),
            afterRemoteInput: () => pollSawCurrentInput = remote.Inputs.Count == 1 && remote.Inputs[0].Gas == 255 && remote.Inputs[0].Steer == 127);

        Assert.True(pollSawCurrentInput);
    }

    [GameDataTheory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void LargeNormalGapAndDelayedNewerSnapshotNeverRewindThePresentedCar(int fps)
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(server);
        long now = 1_000_000;
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now);
        var basis = server.ReadLatestState().Frame;
        remote.Publish(At(basis, scene.PlayerCarId, 1, 0));
        drive.Frame(0, default);
        now += 10 * RemoteInterpolationClock.StepMicros;
        remote.Publish(At(basis, scene.PlayerCarId, 11, 15.625f)); // 50 m/s across ten lost steps (>8 m)
        drive.Frame(0, default);
        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 9.374f, 9.376f);
        float previous = scene.Cars[scene.PlayerInstance].Position.X;
        for (int i = 0; i < fps / 4; i++)
        {
            now += (long)Math.Round(1_000_000.0 / fps);
            drive.Frame(1.0 / fps, default);
            float x = scene.Cars[scene.PlayerInstance].Position.X;
            Assert.True(x >= previous, $"rewound from {previous} to {x}");
            Assert.InRange(x, 0, 15.625f);
            previous = x;
        }
        remote.Publish(At(basis, scene.PlayerCarId, 12, 17.1875f)); // a newer snapshot arrives very late
        drive.Frame(0, default);
        Assert.True(scene.Cars[scene.PlayerInstance].Position.X >= previous);
        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 17.186f, 17.189f);
        Assert.Same(drive.View, scene.Frame);
    }

    [GameDataFact]
    public void EpochAndSameSequenceResyncSnapTheSceneAndCameraAndTerminalFinishesExactlyOnce()
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(server);
        long now = 1_000_000;
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now);
        drive.Cam.Locator = null;
        var basis = server.ReadLatestState().Frame;
        remote.Publish(At(basis, scene.PlayerCarId, 1, 0));
        drive.Frame(0, default);
        now += 500_000;
        remote.Publish(At(basis, scene.PlayerCarId, 10, 100, 1));
        drive.Frame(.001, default);
        var camera = drive.Cam.Position;
        drive.Cam.Snap(RenderPose.From(drive.PlayerView.Pos, drive.PlayerView.Orient), drive.PlayerView, scene.PlayerAnimation);
        Assert.Equal(drive.Cam.Position, camera); // no smoothing across the epoch boundary
        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 99.999f, 100.001f);

        var resync = At(basis, scene.PlayerCarId, 10, 200, 1, RaceViewFrameKind.Resync);
        remote.Publish(resync); // same sequence, same epoch: explicit resync must still snap
        drive.Frame(0, default);
        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 199.999f, 200.001f);
        var terminal = resync with { Results = new RaceResults([]) };
        remote.Publish(terminal);
        int finishes = 0, pcmFrames = 0;
        drive.FramePresented += (f, events) =>
        {
            finishes += events.OfType<RaceFinishedEvent>().Count();
            if (f.Kind == RaceViewFrameKind.Step) pcmFrames++;
        };
        // Authoritative finish can be ahead of the retained pose, beyond the old presentation horizon.
        var finish = new RaceFinishedEvent(basis.RaceId, 900, 14, 28, terminal.Results!);
        remote.Events.Enqueue(finish);
        drive.Frame(.04, default);
        Assert.Same(terminal, drive.View);
        Assert.Same(terminal, scene.Frame);
        Assert.Equal(1, finishes);
        Assert.Equal(0, pcmFrames);
        remote.Events.Enqueue(finish);
        drive.Frame(.04, default);
        Assert.Equal(1, finishes);
        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 199.999f, 200.001f);
    }

    [GameDataTheory]
    [InlineData(60, 1)]
    [InlineData(30, 1)]
    [InlineData(60, 2)]
    [InlineData(30, 2)]
    public void AudioTimeAndAnimationsFollow32HzWithLossAndHoldAndLateEventsDoNotAddTicks(int fps, int snapshotStride)
    {
        var scene = new RaceScene("replay");
        using var server = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false);
        var remote = new RemoteViewProvider(server);
        long now = 1_000_000;
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => now);
        var basis = server.ReadLatestState().Frame;
        using var audio = new RaceAudio(scene.Session, remote.Description);
        int chunks = 0, samples = 0, shots = 0;
        audio.BufferReady += pcm => { chunks++; samples += pcm.Length; };
        audio.OneShot += _ => shots++;
        drive.FramePresented += audio.Present;
        var referenceAnim = NFS.Render.CarAnim.For(scene.Session.Cars[scene.PlayerCarId].Setup.CarType,
            NFS.Core.Tables.GameTables.Default, seed: scene.PlayerInstance);
        int published = 0;
        for (int i = 1; i <= 3 * fps; i++)
        {
            now = 1_000_000 + (long)Math.Round(i * 1_000_000.0 / fps);
            int step = (int)((now - 1_000_000) / 31_250);
            // Last second has no snapshots at all, while the audio holds the last state.
            if (i <= 2 * fps && step > published && step % snapshotStride == 0)
            {
                published = step;
                remote.Publish(basis with { Kind = RaceViewFrameKind.Step, SnapshotSequence = step, Steps = step, SimulationTick64 = step * 2 });
            }
            drive.Frame(1.0 / fps, default);
        }
        Assert.Equal(96, chunks);
        Assert.Equal(3 * audio.Mixer.OutputRate * 2, samples);
        for (int i = 0; i < 96; i++) referenceAnim.Tick(basis.Car(scene.PlayerCarId), NFS.Core.Tables.GameTables.Default);
        Assert.Equal(referenceAnim.WheelY, scene.PlayerAnimation!.WheelY);
        Assert.Equal(referenceAnim.Roll, scene.PlayerAnimation.Roll);
        var beep = new CountdownBeepEvent(basis.RaceId, 700, drive.View.SnapshotSequence, drive.View.SimulationTick64, 0);
        remote.Events.Enqueue(beep);
        drive.Frame(0, default); // late event for the same snapshot cannot create a second audio tick
        Assert.Equal(96, chunks);
        now += 31_250;
        drive.Frame(1.0 / 32, default);
        Assert.Equal(97, chunks);
        Assert.Equal(1, shots);
        remote.Events.Enqueue(beep);
        now += 31_250;
        drive.Frame(1.0 / 32, default);
        Assert.Equal(98, chunks);
        Assert.Equal(1, shots);
    }

    [GameDataFact]
    public void WelcomeLoadsStaticRaceDataForTheAssignedCarWithoutCreatingAClientSimulation()
    {
        var source = new RaceScene("replay");
        int assignedSeatCar = source.Session.Cars.Count - 1;
        var loaded = RemoteDesktopSession.LoadWelcomeScene(GameFiles.FindGameDir(null), source.Session.Setup.Block, assignedSeatCar);

        Assert.Equal(assignedSeatCar, loaded.PlayerCarId);
        Assert.Equal(source.Session.TrackNumber, loaded.Session.TrackNumber);
        Assert.Equal(source.Session.Cars.Count, loaded.Cars.Count);
    }

    [GameDataFact]
    public void InjectedRemoteProviderOwnsFramesInputsPauseDisconnectAndLeaveLifecycle()
    {
        var scene = new RaceScene("replay");
        using var serverSide = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false, withAi: true);
        var remote = new RemoteViewProvider(serverSide);
        long micros = 1_000_000;
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => micros);

        Assert.Throws<InvalidOperationException>(() => drive.Step(default));
        drive.Frame(.04, new DriveKeys(Up: true, Down: false, Left: false, Right: false, GearUp: false, GearDown: false, Reset: false));
        Assert.Contains(remote.Inputs, i => i.Gas == 255);
        drive.ToggleRemotePause();
        drive.Frame(.04, default);
        Assert.False(serverSide.ReadLatestState().Paused); // local pause never pauses the host
        Assert.Equal(default, remote.Inputs[^1]);

        for (int i = 0; i < 10; i++)
        {
            serverSide.Step(); // server clock runs independently while this client is locally paused
            micros += 31_250;
            drive.Frame(.03125, default);
        }
        Assert.Equal(0, drive.View.Steps); // local pause freezes this display while the host keeps stepping
        drive.ToggleRemotePause();
        drive.Frame(.001, default);
        Assert.Equal(6, drive.View.Steps); // on resume, the client presents four host steps behind
        micros += 125_000;
        drive.Frame(.016, default);
        Assert.Equal(10, drive.View.Steps);
        remote.Connected = false;
        drive.Frame(.016, default); // a disconnect freezes presentation; it is not a render-loop exception
        Assert.False(remote.ReadLatestState().Connected);
        remote.RequestLeave();
        Assert.True(remote.Left);
    }

    [GameDataTheory]
    [InlineData(PlayerInput.BtnReset)]
    [InlineData(PlayerInput.BtnGearUp)]
    [InlineData(PlayerInput.BtnGearDown)]
    public void RemotePauseDiscardsActionsAndStaysNeutralUntilResume(byte action)
    {
        var scene = new RaceScene("replay");
        using var serverSide = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false, withAi: true);
        var remote = new RemoteViewProvider(serverSide);
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => 1_000_000);
        var pressed = new DriveKeys(false, false, false, false,
            GearUp: action == PlayerInput.BtnGearUp, GearDown: action == PlayerInput.BtnGearDown,
            Reset: action == PlayerInput.BtnReset);

        drive.Frame(0, pressed); // an action pending before pause must also be discarded
        drive.ToggleRemotePause();
        drive.Frame(1.0 / 32, pressed);
        Assert.Single(remote.Inputs);
        Assert.Equal(default, remote.Inputs[^1]);

        drive.Frame(0, default);
        drive.Frame(.001, pressed); // short paused press entirely between input samples
        drive.Frame(.001, default);
        drive.Frame(1.0 / 32, default);
        Assert.Equal(2, remote.Inputs.Count);
        Assert.All(remote.Inputs, input => Assert.Equal(default, input));

        var held = pressed with { Up = true, Down = true, Right = true, Horn = true, Handbrake = true };
        for (int i = 0; i < 4; i++)
            drive.Frame(1.0 / 32, held); // consecutive samples stay neutral, including held controls
        Assert.Equal(6, remote.Inputs.Count);
        Assert.All(remote.Inputs, input => Assert.Equal(default, input));

        drive.ToggleRemotePause();
        drive.Frame(1.0 / 32, pressed); // holding R/A/Z across Resume must not synthesize a new press
        Assert.Equal(7, remote.Inputs.Count);
        Assert.All(remote.Inputs, input => Assert.Equal(default, input));
        drive.Frame(0, default);
        drive.Frame(1.0 / 32, default); // releasing after Resume must not flush a paused action
        Assert.Equal(default, remote.Inputs[^1]);
        drive.Frame(1.0 / 32, pressed); // a fresh press after Resume works exactly once
        Assert.Equal(action, remote.Inputs[^1].Buttons);
        drive.Frame(1.0 / 32, pressed);
        Assert.Equal(default, remote.Inputs[^1]);
    }

    [GameDataFact]
    public void ChangedPoseEpochSnapsAcrossTeleportAndLateResultNeedsNoFinalSnapshot()
    {
        var scene = new RaceScene("replay");
        using var serverSide = new LocalRaceProvider(scene.Session, scene.PlayerCarId, countdown: false, withAi: true);
        var remote = new RemoteViewProvider(serverSide);
        long micros = 1_000_000;
        var drive = new RaceDrive(scene, provider: remote, monotonicMicros: () => micros);
        var baseline = serverSide.ReadLatestState().Frame;
        drive.Frame(.001, default); // consume initial frame

        RaceViewFrame At(int step, long seq, int x, int epoch) => baseline with
        {
            SnapshotSequence = seq, Kind = RaceViewFrameKind.Step, Steps = step, SimulationTick64 = step * 2,
            Cars = baseline.Cars.Select((c, i) => i == scene.PlayerCarId
                ? c with { PoseEpoch = epoch, State = c.State with { Pos = c.State.Pos with { X = x } } } : c).ToArray(),
        };
        remote.Publish(At(1, 1, 50 * Fx.One, 1)); // server reset / teleport
        remote.Publish(At(2, 2, 51 * Fx.One, 1));
        remote.Publish(At(3, 3, 52 * Fx.One, 1));
        remote.Publish(At(4, 4, 53 * Fx.One, 1));
        remote.Publish(At(5, 5, 54 * Fx.One, 1));
        micros += 1_000;
        drive.Frame(.001, default); // four-step buffer selects the reset frame and its successor

        Assert.InRange(scene.Cars[scene.PlayerInstance].Position.X, 49.9f, 50.1f);
        var terminal = At(5, 5, 54 * Fx.One, 1) with { Kind = RaceViewFrameKind.Resync, Results = new RaceResults([]) };
        remote.Publish(terminal); // reliable result arrives without a newer unreliable pose snapshot
        drive.Frame(.001, default);
        Assert.NotNull(drive.Results);
        Assert.Equal(5, drive.View.Steps);
    }
}
