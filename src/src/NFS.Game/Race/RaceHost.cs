using NFS.Core;
using NFS.Game.Session;

namespace NFS.Game.Race;

/// <summary>
/// Owner of one race simulation (network stage 5a.1): <see cref="RaceRunner"/>, the applied-input recorder, the optional replay
/// input source, the pause flag and the final <see cref="RaceResults"/>. One <see cref="Step"/> is one whole
/// <see cref="RaceRunner.Step(PlayerInput, PlayerInput?)"/> = 32 Hz = two 64 Hz <see cref="RaceRunner.Tick"/> units.
/// The host knows no clock, camera, audio or renderer; the driver (local Desktop clock driver, later a server) decides when to step.
/// Stage 5a.2: after every step the host publishes an immutable <see cref="RaceViewFrame"/> (<see cref="View"/>); presentation reads
/// those frames, not <see cref="Runner"/>. Inputs can be queued per seat (<see cref="Submit"/> + <see cref="StepQueued"/>).
/// </summary>
public sealed class RaceHost : IDisposable
{
    /// <summary>Bound of each seat's input queue; older samples are dropped first (<see cref="DroppedInputs"/>).</summary>
    public const int InputQueueLimit = 64;

    public RaceRunner Runner { get; }
    public ReplayRecorder Recorder { get; }
    public ReplayInputSource? PlaybackSource { get; }
    public RaceResults? Results { get; private set; }
    public bool PlaybackEnded { get; private set; }
    /// <summary>Local pause (replay pause): <see cref="Step"/> does nothing while set. [ref:2101]</summary>
    public bool Paused { get; set; }
    /// <summary>Whole steps executed by this host.</summary>
    public int Steps { get; private set; }
    /// <summary>Frame publisher (view contract of stage 5a.2).</summary>
    public RaceViewPublisher View { get; }
    /// <summary>Number of human seats of this host: 1, 2 (split screen) or the count of the list-seat runner (3+).</summary>
    public int Seats => Runner.HumanSims.Count;
    public int DroppedInputs { get; private set; }

    readonly Queue<(long Sequence, PlayerInput Input)>[] _inputs;
    readonly long[] _applied;
    long _inputSequence;

    /// <param name="humanIndices">List-seat race (3+ humans): seat → index in the session cars; null = legacy
    /// <paramref name="playerIndex"/> / <paramref name="secondPlayerIndex"/> path.</param>
    public RaceHost(RaceSession session, int playerIndex = 0, bool? automatic = null, bool withAi = false, bool countdown = true,
        int? secondPlayerIndex = null, ReplayInputSource? playbackSource = null, int? raceId = null,
        IReadOnlyList<int>? humanIndices = null)
    {
        PlaybackSource = playbackSource;
        Runner = new RaceRunner(session, playerIndex, automatic: automatic, withAi: withAi, countdown: countdown,
            secondPlayerIndex: secondPlayerIndex, humanIndices: humanIndices);
        if (playbackSource != null && Runner.HumanSims.Count > 2)
            throw new NotSupportedException("replay playback supports at most two human seats");
        // == 2: the replay camera director runs and draws from the shared state, like the original [ref:2102]
        if (playbackSource != null) Runner.ReplayCam = new ReplayCamera(Runner.Rand);
        _inputs = Enumerable.Range(0, Seats).Select(_ => new Queue<(long, PlayerInput)>()).ToArray();
        _applied = new long[Seats];
        Recorder = new ReplayRecorder(session.Setup, Runner.Tick, session.Catalog);
        View = new RaceViewPublisher(Runner, playback: playbackSource != null, appliedInput: () => _applied.ToArray(), raceId: raceId, particles: true);
    }

    /// <summary>Queues one input sample of <paramref name="seat"/> for the next step and returns its InputSequence. Never steps;
    /// the frame field <see cref="RaceViewFrame.AppliedInput"/> acknowledges it once a step used it.</summary>
    public long Submit(int seat, PlayerInput input)
    {
        if ((uint)seat >= (uint)Seats) throw new ArgumentOutOfRangeException(nameof(seat));
        var q = _inputs[seat];
        if (q.Count >= InputQueueLimit) { q.Dequeue(); DroppedInputs++; }
        long sequence = ++_inputSequence;
        q.Enqueue((sequence, input));
        return sequence;
    }

    /// <summary>One step with the oldest queued sample of each seat; a seat without a sample drives neutral (no held gas, and a
    /// one-shot gear/reset button is never repeated). When the step cannot run (paused, finished, replay over) the oldest sample of
    /// each seat is discarded unapplied.</summary>
    public bool StepQueued()
    {
        if (Results != null || Paused || PlaybackEnded)
        {
            // A step that does not run drops its samples (like a direct Step call): nothing is applied later.
            foreach (var q in _inputs) q.TryDequeue(out _);
            return Seats > 2
                ? StepHumans(new PlayerInput[Seats])
                : Step(default, Seats > 1 ? default(PlayerInput) : null);
        }
        PlayerInput input = default;
        PlayerInput? input2 = Seats > 1 ? default(PlayerInput) : null;
        if (_inputs[0].TryDequeue(out var a)) { input = a.Input; _applied[0] = a.Sequence; }
        if (Seats > 1 && _inputs[1].TryDequeue(out var b)) { input2 = b.Input; _applied[1] = b.Sequence; }
        if (Seats <= 2) return Step(input, input2);
        // List-seat race: collect every seat and hand the whole list to the runner.
        var seats = new PlayerInput[Seats];
        seats[0] = input;
        if (Seats > 1) seats[1] = input2 ?? default;
        for (int seat = 2; seat < Seats; seat++)
            if (_inputs[seat].TryDequeue(out var c)) { seats[seat] = c.Input; _applied[seat] = c.Sequence; }
        return StepHumans(seats);
    }

    /// <summary>One step with inputs chosen by an external scheduler (network server, stage 5a.3): <paramref name="sequence"/> /
    /// <paramref name="sequence2"/> = InputSequence of the sample each seat applied in this step (0 = none, the previous ACK is kept).
    /// The sequences become <see cref="RaceViewFrame.AppliedInput"/> of the published frame.</summary>
    public bool StepScheduled(PlayerInput input, long sequence, PlayerInput? input2 = null, long sequence2 = 0)
    {
        if (Seats > 2) throw new InvalidOperationException("use the list-based scheduler for a race with more than two human seats");
        if (Results != null || Paused || PlaybackEnded || PlaybackSource != null) return false;
        if (sequence > 0) _applied[0] = sequence;
        if (Seats > 1 && sequence2 > 0) _applied[1] = sequence2;
        return Step(input, Seats > 1 ? input2 ?? default(PlayerInput) : null);
    }

    /// <summary>One scheduled input per human seat. The sequence for each seat advances only when that sample was applied.</summary>
    public bool StepScheduled(IReadOnlyList<PlayerInput> inputs, IReadOnlyList<long> sequences)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(sequences);
        if (inputs.Count != Seats || sequences.Count != Seats)
            throw new ArgumentException($"expected {Seats} inputs and sequences");
        if (Results != null || Paused || PlaybackEnded || PlaybackSource != null) return false;
        for (int seat = 0; seat < Seats; seat++)
            if (sequences[seat] > 0) _applied[seat] = sequences[seat];
        return StepHumans(inputs);
    }

    /// <summary>Replay playback: the recording ran out of samples as soon as the race had a result.</summary>
    public void EndPlaybackIfFinished()
    {
        if (PlaybackSource != null && Results != null) PlaybackEnded = true;
    }

    /// <summary>Replay playback: the header's race length has been reached.</summary>
    public void EndPlaybackIfOutOfInput()
    {
        if (PlaybackSource != null && Runner.Tick >= PlaybackSource.RaceTicks64) PlaybackEnded = true;
    }

    /// <summary>
    /// One simulation step. During playback the supplied inputs are ignored and read from <see cref="PlaybackSource"/> at the current
    /// tick; otherwise the applied inputs are recorded. <paramref name="afterStep"/> runs right after <see cref="RaceRunner.Step"/>
    /// (before the result capture) with the "player 1 / player 2 was manually reset" flags. Then one Step frame is published.
    /// Returns false when no step was executed.
    /// </summary>
    public bool Step(PlayerInput input, PlayerInput? input2 = null, Action<bool, bool>? afterStep = null)
    {
        if (Seats > 2) throw new InvalidOperationException("use StepHumans for a race with more than two human seats");
        return StepCore(input, input2, null, afterStep);
    }

    /// <summary>One step of a list-seat race (3+ humans): <paramref name="inputs"/> holds one input per seat; AI cars keep
    /// their drivers. Otherwise identical to <see cref="Step(PlayerInput, PlayerInput?, Action{bool, bool}?)"/>. The replay
    /// recorder keeps the original two-input format and records seats 0 and 1.</summary>
    public bool StepHumans(IReadOnlyList<PlayerInput> inputs, Action<bool, bool>? afterStep = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != Seats)
            throw new ArgumentException($"expected {Seats} seat inputs, got {inputs.Count}", nameof(inputs));
        return StepCore(default, null, inputs, afterStep);
    }

    private bool StepCore(PlayerInput input, PlayerInput? input2, IReadOnlyList<PlayerInput>? humanInputs, Action<bool, bool>? afterStep)
    {
        if (PlaybackSource != null && Results != null) { PlaybackEnded = true; return false; }
        if (Results != null || Paused || PlaybackEnded) return false;
        if (PlaybackSource != null)
        {
            if (Runner.Tick >= PlaybackSource.RaceTicks64) { PlaybackEnded = true; return false; }
            input = PlaybackSource.ForRaceTick(Runner.Tick);
            input2 = Runner.Player2 == null ? null : PlaybackSource.ForRaceTick(Runner.Tick, 1);
        }
        else if (humanInputs != null)
            Recorder.Record(humanInputs[0], humanInputs.Count > 1 ? humanInputs[1] : null);
        else Recorder.Record(input, input2);
        int resets = Runner.ManualResets, resets2 = Runner.ManualResets2;
        if (humanInputs != null) Runner.StepHumans(humanInputs);
        else Runner.Step(input, input2);
        bool reset1 = Runner.ManualResets != resets, reset2 = Runner.ManualResets2 != resets2;
        afterStep?.Invoke(reset1, reset2);
        Steps++;
        if (Runner.Laps.RaceOver)
        {
            if (PlaybackSource != null) PlaybackEnded = true;
            else Results = RaceResults.Capture(Runner);
        }
        View.Publish(RaceViewFrameKind.Step, Steps, reset1, reset2, Results);
        return true;
    }

    /// <summary>outside a step (original guards and lockout); publishes a Reset frame when accepted. [ref:2103]</summary>
    public bool ManualReset()
    {
        if (Paused || Results != null || !Runner.ManualReset()) return false;
        View.Publish(RaceViewFrameKind.Reset, Steps, playerReset: true);
        return true;
    }

    /// <summary>Publishes a Resync frame of the current state (local tools and tests that changed the host outside a step). [ref:3432]</summary>
    public RaceViewFrame Resync() => View.Publish(RaceViewFrameKind.Resync, Steps, results: Results);

    /// <summary>→(1): estimate unfinished times, abort the entrants and capture the result. [ref:2104]</summary>
    public RaceResults Abort(int raceType)
    {
        if (Results != null) return Results;
        Runner.Laps.AbortRace(raceType);
        Results = RaceResults.Capture(Runner);
        return Results;
    }

    /// <summary>The server re-ranks the unfinished cars of an aborted race (DNF behind the finishers); the simulation is not touched.
    /// Allowed only for the result already captured by <see cref="Abort"/>.</summary>
    public void OverrideResults(RaceResults results)
    {
        if (Results == null) throw new InvalidOperationException("the race has no result to replace");
        Results = results;
    }

    public void Dispose() => View.Dispose();
}
