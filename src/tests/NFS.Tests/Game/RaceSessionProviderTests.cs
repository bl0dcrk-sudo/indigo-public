using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Stage 5a.2: immutable view frames, the event channel and the local provider over the embedded host.</summary>
public class RaceSessionProviderTests
{
    static LocalRaceProvider Provider(bool countdown = true, bool automatic = true) =>
        new(TrackSessions.FreshReplay(), 0, automatic: automatic, withAi: true, countdown: countdown);

    [GameFact]
    public void Frame_IsACopy_LaterHostChangesDoNotReachIt()
    {
        using var p = Provider(countdown: false);
        for (int i = 0; i < 128; i++) { p.SubmitInput(0, new PlayerInput(0, 255, 0, 0)); p.Step(); }
        var frame = p.ReadLatestState().Frame;
        var car = p.Host.Runner.Car;
        var view = frame.Car(p.Description.PlayerCarId);
        Assert.Equal(car.Pos, view.Pos);
        var (pos, laps, skids, skidAdded, knocked) = (view.Pos, view.LapTimes.ToArray(), frame.Skids, frame.Skids.Added, frame.Knocked);
        Assert.NotSame(p.Host.View.Skids!.Buffer, skids);
        Assert.NotSame(p.Host.Runner.DynObjects.Knocked, knocked);
        for (int i = 0; i < 24; i++) { p.SubmitInput(0, new PlayerInput(-60, 255, 0, PlayerInput.BtnHandbrake)); p.Step(); }
        Assert.True(p.Host.View.Skids.Buffer.Added > skidAdded, "the host kept producing tyre marks");
        car.Pos = new Vec3(car.Pos.X + 500 * Fx.One, car.Pos.Y, car.Pos.Z);   // direct host edits ...
        car.LapTimes[0] = 12345;
        p.Step();
        Assert.Equal(pos, frame.Car(p.Description.PlayerCarId).Pos);           // ... never reach a published frame
        Assert.Equal(laps, frame.Car(p.Description.PlayerCarId).LapTimes.ToArray());
        Assert.Equal(skidAdded, frame.Skids.Added);
        Assert.NotEqual(frame.SnapshotSequence, p.ReadLatestState().Frame.SnapshotSequence);
    }

    [GameFact]
    public void SubmitInput_DoesNotStep_LocalStepAppliesItOnceAndAcknowledgesIt()
    {
        using var p = Provider(countdown: false, automatic: false);
        Assert.Empty(p.DrainFrames());                                         // the initial frame is a state, not a published step
        long seq = p.SubmitInput(0, new PlayerInput(0, 255, 0, PlayerInput.BtnGearUp));
        int tick0 = p.Host.Runner.Tick;
        Assert.Equal(tick0, p.ReadLatestState().Frame.SimulationTick64);
        Assert.Empty(p.DrainFrames());
        Assert.Equal(0L, p.ReadLatestState().Frame.AppliedInput[0]);
        byte gear = p.Host.Runner.Car.Gear;
        Assert.True(p.Step());
        var frame = Assert.Single(p.DrainFrames());
        Assert.Equal((RaceViewFrameKind.Step, tick0 + 2, 1, seq), (frame.Kind, frame.SimulationTick64, frame.Steps, frame.AppliedInput[0]));
        Assert.Equal(gear + 1, frame.Car(p.Description.PlayerCarId).Gear);
        for (int i = 0; i < 8; i++) Assert.True(p.Step());                    // no new sample: neutral, the one-shot is not repeated
        Assert.Equal(gear + 1, p.ReadLatestState().Frame.Car(p.Description.PlayerCarId).Gear);
        Assert.Equal(seq, p.ReadLatestState().Frame.AppliedInput[0]);
        var frames = p.DrainFrames();
        Assert.Equal(Enumerable.Range(2, 8).Select(i => (long)i), frames.Select(f => f.SnapshotSequence));
    }

    [GameFact]
    public void QueuedProviderInputs_StepExactlyLikeABareRunner()
    {
        var bare = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        using var p = Provider();
        for (int i = 0; i < 900; i++)
        {
            var input = new PlayerInput((sbyte)(Math.Sin(i / 11.0) * 100), 255, (byte)(i % 120 > 110 ? 200 : 0),
                i == 700 ? PlayerInput.BtnReset : (byte)0);
            bare.Step(input);
            p.SubmitInput(0, input);
            p.Step();
        }
        var frame = p.ReadLatestState().Frame;
        Assert.Equal(bare.Tick, frame.SimulationTick64);
        for (int i = 0; i < bare.Session.Cars.Count; i++)
        {
            var v = frame.Car(i);
            Assert.Equal((bare.Session.Cars[i].Obj.Pos, bare.Session.Cars[i].Obj.Vel, bare.Session.Cars[i].Obj.Orient), (v.Pos, v.Vel, v.Orient));
        }
    }

    [GameFact]
    public void PoseEpoch_ChangesOnlyForTheResetCar()
    {
        using var p = Provider(countdown: false);
        for (int i = 0; i < 1100; i++) { p.SubmitInput(0, new PlayerInput((sbyte)(Math.Sin(i / 9.0) * 90), 255, 0, 0)); p.Step(); }
        var before = p.ReadLatestState().Frame;
        p.SubmitInput(0, new PlayerInput(0, 0, 0, PlayerInput.BtnReset));
        p.Step();
        int resetAt = p.Host.Runner.ManualResets;
        Assert.Equal(1, resetAt);
        var after = p.ReadLatestState().Frame;
        int player = p.Description.PlayerCarId;
        Assert.NotEqual(before.Cars[player].PoseEpoch, after.Cars[player].PoseEpoch);
        foreach (var c in after.Cars.Where(c => c.CarId != player)) Assert.Equal(before.Cars[c.CarId].PoseEpoch, c.PoseEpoch);
        p.SubmitInput(0, default); p.Step();
        Assert.Equal(after.Cars[player].PoseEpoch, p.ReadLatestState().Frame.Cars[player].PoseEpoch);   // continuous again
    }

    [GameFact]
    public void Countdown_EventsArriveOnceInOrderWithTheirFrames()
    {
        using var p = Provider();
        var frames = new List<RaceViewFrame>();
        var events = new List<RaceEvent>();
        while (p.Host.Runner.Tick < 0x210) { p.Step(); frames.AddRange(p.DrainFrames()); events.AddRange(p.DrainEvents()); }
        var beeps = events.OfType<CountdownBeepEvent>().ToArray();
        Assert.Equal([(0x140, 0), (0x180, 1), (0x1C0, 2), (0x200, 3)], beeps.Select(b => (b.SimulationTick64, b.Sample)));
        Assert.True(events.Select(e => e.EventId).SequenceEqual(events.Select(e => e.EventId).Order().Distinct()));
        foreach (var b in beeps)
            Assert.Equal(b.SimulationTick64 + 2, frames.Single(f => f.SnapshotSequence == b.SnapshotSequence).SimulationTick64);
        Assert.All(events, e => Assert.Equal(p.Description.RaceId, e.RaceId));
    }

    [Fact]
    public void Inbox_DeduplicatesRejectsOtherRacesAndHandsOutPerFrameInOrder()
    {
        RaceEvent Beep(int race, long id, long seq) => new CountdownBeepEvent(race, id, seq, (int)seq * 2, (int)id);
        var inbox = new RaceEventInbox(7);
        inbox.Accept([Beep(7, 3, 2), Beep(7, 1, 1), Beep(9, 2, 1), Beep(7, 2, 1)]);   // shuffled, one foreign race
        inbox.Accept([Beep(7, 1, 1), Beep(7, 4, 3)]);                                    // a duplicate (resent)
        Assert.Equal([1L, 2L], inbox.TakeThrough(1).Select(e => e.EventId));
        Assert.Empty(inbox.TakeThrough(1));
        inbox.Accept([Beep(7, 2, 1)]);                                                   // late duplicate of a delivered event
        Assert.Equal([3L, 4L], inbox.TakeThrough(5).Select(e => e.EventId));
        Assert.Equal(3, inbox.Rejected);
    }

    [GameFact]
    public void Pause_StopsLocalStepsAndDropsTheirInputs()
    {
        using var p = Provider(countdown: false, automatic: false);
        for (int i = 0; i < 40; i++) { p.SubmitInput(0, new PlayerInput(0, 255, 0, 0)); p.Step(); }
        p.DrainFrames();
        byte gear = p.Host.Runner.Car.Gear;
        Assert.True(p.RequestPause());
        Assert.True(p.ReadLatestState().Paused);
        p.SubmitInput(0, new PlayerInput(0, 255, 0, PlayerInput.BtnGearDown));
        Assert.False(p.Step());
        Assert.Empty(p.DrainFrames());
        p.RequestResume();
        Assert.True(p.Step());
        Assert.Equal(gear, p.ReadLatestState().Frame.Car(p.Description.PlayerCarId).Gear);   // the paused press is not applied later
    }

    [GameFact]
    public void Playback_FramesFollowTheRecordingAndIgnoreSubmittedInput()
    {
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        using var a = new LocalRaceProvider(TrackSessions.FreshReplay(), 0, withAi: true, playbackSource: new ReplayInputSource(rpy));
        using var b = new LocalRaceProvider(TrackSessions.FreshReplay(), 0, withAi: true, playbackSource: new ReplayInputSource(rpy));
        Assert.True(a.Description.Playback);
        for (int i = 0; i < 600; i++)
        {
            a.SubmitInput(0, new PlayerInput(127, 255, 255, 0xFF));
            a.Step(); b.Step();
        }
        var fa = a.ReadLatestState().Frame; var fb = b.ReadLatestState().Frame;
        Assert.Equal(fb.SimulationTick64, fa.SimulationTick64);
        for (int i = 0; i < fa.Cars.Count; i++) Assert.Equal(fb.Car(i).Pos, fa.Car(i).Pos);
        Assert.Null(fa.Results);
    }

    [GameFact]
    public void Finish_ResultIsInEveryLaterFrameAndAnnouncedOnce()
    {
        using var p = Provider(countdown: false);
        p.Host.Runner.SetLaps(2);
        var events = new List<RaceEvent>();
        int guard = 0;
        while (p.ReadLatestState().Frame.Results == null && guard++ < 6000)
        {
            if (guard == 100) p.Host.Runner.Car.Lap = 1;
            if (guard == 140) p.Host.Runner.Car.Lap = 2;
            p.Step();
            events.AddRange(p.DrainEvents());
        }
        var frame = p.ReadLatestState().Frame;
        Assert.NotNull(frame.Results);
        Assert.Same(p.Host.Results, frame.Results);
        Assert.Same(frame.Results, Assert.Single(events.OfType<RaceFinishedEvent>()).Results);
        p.DrainFrames();
        Assert.False(p.Step());                                                 // finished: no more steps, frames or events
        Assert.Empty(p.DrainFrames());
        Assert.Empty(p.DrainEvents());
        Assert.Same(frame, p.ReadLatestState().Frame);
    }

    [GameFact]
    public void UndrainedQueues_AreBounded()
    {
        using var p = Provider(countdown: false);
        for (int i = 0; i < LocalRaceProvider.QueueLimit + 10; i++) p.Step();
        var frames = p.DrainFrames();
        Assert.Equal(LocalRaceProvider.QueueLimit, frames.Count);
        Assert.Equal(10, p.DroppedFrames);
        Assert.Equal(p.ReadLatestState().Frame, frames[^1]);                   // the newest survive
    }

    [Fact]
    public void ProviderContract_HasNoStepping()
    {
        // A client must never drive the host's clock: stepping is a local-driver extra of LocalRaceProvider only.
        Assert.DoesNotContain(typeof(IRaceSessionProvider).GetMethods(), m => m.Name.Contains("Step", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(typeof(LocalRaceProvider).GetMethods(), m => m.Name == nameof(LocalRaceProvider.Step));
    }
}
