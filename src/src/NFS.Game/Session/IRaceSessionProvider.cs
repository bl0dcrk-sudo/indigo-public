using NFS.Core;
using NFS.Game.Race;

namespace NFS.Game.Session;

/// <summary>
/// Client side of a race session (stage 5a.2): what presentation may do with a race it does not own. All calls are non-blocking
/// and none of them advances the simulation – there is deliberately no <c>Step</c>/<c>StepServer(dt)</c> here. A local host is
/// stepped by its own clock driver, a remote one by the server's scheduler.
/// </summary>
public interface IRaceSessionProvider : IDisposable
{
    RaceSessionDescription Description { get; }
    /// <summary>Queues one input sample of <paramref name="localSeat"/> and returns its InputSequence; it does not wait for the host
    /// and does not return car state. <see cref="RaceViewFrame.AppliedInput"/> acknowledges application.</summary>
    long SubmitInput(int localSeat, PlayerInput input);
    /// <summary>Newest frame and provider status, without I/O.</summary>
    RaceViewState ReadLatestState();
    /// <summary>Frames published since the previous call, oldest first (bounded; a remote provider may skip frames).</summary>
    IReadOnlyList<RaceViewFrame> DrainFrames();
    /// <summary>Events published since the previous call (bounded). Not ordered relative to <see cref="DrainFrames"/>;
    /// join them with <see cref="RaceEventInbox"/>.</summary>
    IReadOnlyList<RaceEvent> DrainEvents();
    /// <summary>Asks to pause the race; a local host stops stepping, a network race ignores it. Returns whether the race is paused.</summary>
    bool RequestPause();
    void RequestResume();
    /// <summary>Leaves the session (lifecycle only; does not reset physics).</summary>
    void RequestLeave();
}

/// <summary>
/// In-memory provider over a <see cref="RaceHost"/> embedded in this process: no sockets, no serialization, but the same copy
/// semantics – frames and events are immutable values published by the host. Local-only extras (stepping by
/// <see cref="Step"/> for the local clock driver, replay controls, manual reset, aborting with a result, the recording) live on
/// this class, not on <see cref="IRaceSessionProvider"/>.
/// </summary>
public sealed class LocalRaceProvider : IRaceSessionProvider
{
    /// <summary>Bound of the undrained frame / event queues; the oldest entries are dropped first (<see cref="DroppedFrames"/>).</summary>
    public const int QueueLimit = 256;

    readonly Queue<RaceViewFrame> _frames = new();
    readonly Queue<RaceEvent> _events = new();
    bool _left;

    public RaceHost Host { get; }
    public RaceSessionDescription Description => Host.View.Description;
    public int DroppedFrames { get; private set; }
    public int DroppedEvents { get; private set; }
    public bool Left => _left;

    public LocalRaceProvider(RaceHost host)
    {
        Host = host;
        Host.View.Published += OnPublished;
    }

    public LocalRaceProvider(RaceSession session, int playerIndex = 0, bool? automatic = null, bool withAi = false, bool countdown = true,
        int? secondPlayerIndex = null, ReplayInputSource? playbackSource = null)
        : this(new RaceHost(session, playerIndex, automatic, withAi, countdown, secondPlayerIndex, playbackSource)) { }

    void OnPublished(RaceViewFrame frame, IReadOnlyList<RaceEvent> events)
    {
        if (_frames.Count >= QueueLimit) { _frames.Dequeue(); DroppedFrames++; }
        _frames.Enqueue(frame);
        foreach (var e in events)
        {
            if (_events.Count >= QueueLimit) { _events.Dequeue(); DroppedEvents++; }
            _events.Enqueue(e);
        }
    }

    public long SubmitInput(int localSeat, PlayerInput input) => Host.Submit(localSeat, input);

    public RaceViewState ReadLatestState() => new(Host.View.Latest, Host.Paused, Host.PlaybackEnded, !_left);

    public IReadOnlyList<RaceViewFrame> DrainFrames()
    {
        if (_frames.Count == 0) return [];
        var list = _frames.ToArray();
        _frames.Clear();
        return list;
    }

    public IReadOnlyList<RaceEvent> DrainEvents()
    {
        if (_events.Count == 0) return [];
        var list = _events.ToArray();
        _events.Clear();
        return list;
    }

    public bool RequestPause() { Host.Paused = true; return true; }
    public void RequestResume() => Host.Paused = false;
    public void RequestLeave() => _left = true;

    /// <summary>Local clock driver only: one host step with the queued inputs (publishes one Step frame when it ran).</summary>
    public bool Step() => !_left && Host.StepQueued();

    public bool ManualReset() => !_left && Host.ManualReset();
    public RaceViewFrame Resync() => Host.Resync();
    public RaceResults AbortRace(int raceType) => Host.Abort(raceType);
    public ReplayInputSource? PlaybackSource => Host.PlaybackSource;
    public bool PlaybackEnded => Host.PlaybackEnded;
    public void EndPlaybackIfFinished() => Host.EndPlaybackIfFinished();
    public void EndPlaybackIfOutOfInput() => Host.EndPlaybackIfOutOfInput();
    /// <summary>The applied inputs of this race as a replay (only the local host records).</summary>
    public NFS.Data.FrontEnd.Replay BuildRecording() => Host.Recorder.Build(Host.Runner.Tick);

    public void Dispose()
    {
        Host.View.Published -= OnPublished;
        Host.Dispose();
    }
}
