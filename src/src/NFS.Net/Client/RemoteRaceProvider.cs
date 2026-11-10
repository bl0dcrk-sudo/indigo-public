using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Protocol;

namespace NFS.Net.Client;

/// <summary>IRaceSessionProvider adapter over a polled RaceClient. It preserves remote poses and epochs; presentation owns smoothing.</summary>
public sealed class RemoteRaceProvider : IRaceSessionProvider
{
    const int QueueLimit = RaceClient.FrameQueueLimit;
    const int InputTokenMapLimit = RaceHost.InputQueueLimit;
    readonly RaceClient _client;
    RaceSessionDescription? _description;
    readonly bool _realtimeInputs;
    readonly Queue<RaceViewFrame> _frames = new();
    readonly Queue<RaceEvent> _events = new();
    readonly SortedDictionary<long, long> _inputTokens = new();
    bool _left, _disposed, _resultAnnounced, _finishEventQueued;
    byte _previousActions;
    long _nextToken, _lastAckedToken;
    RaceViewFrame? _latest;
    RaceViewFrame? _terminalFrame;

    public RemoteRaceProvider(RaceClient client, bool realtimeInputs = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _description = client.Description;
        _realtimeInputs = realtimeInputs;
        _client.RealtimeControls = realtimeInputs;
        _client.InputSampleCreated += OnInputSampleCreated;
    }

    public RaceSessionDescription Description => _description ??= _client.Description
        ?? throw new InvalidOperationException("Race welcome has not arrived.");
    public long? StepOneLocalTime => _client.StepOneLocalTime;
    /// <summary>The round is only watched: <see cref="SubmitInput"/> accepts the presentation's controls and drops them.</summary>
    public bool Spectator => _client.Spectator;

    /// <summary>Advances input sampling and transfers client data. Each submit token maps to its network sample in frame ACKs.</summary>
    /// <remarks>When the final result has no matching snapshot, the frozen frame is marked <see cref="RaceViewFrameKind.Resync"/> and
    /// retains the last received pose and time. A later unreliable snapshot cannot replace that terminal view. Call from the owning update loop.</remarks>
    public void Poll(long monotonicMicros)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Update(monotonicMicros);
        foreach (var received in _client.DrainFrames())
        {
            if (_terminalFrame != null) continue;
            var frame = MapInputAck(received);
            _latest = frame;
            EnqueueFrame(frame);
        }
        if (_client.Results is { } result && !_resultAnnounced && _latest != null)
        {
            _resultAnnounced = true;
            var basis = _latest;
            var final = basis with { Kind = basis.Steps == result.Step && basis.SimulationTick64 == result.SimulationTick64
                    ? basis.Kind : RaceViewFrameKind.Resync, Results = result.Results };
            _terminalFrame = _latest = final;
            if (_frames.Count > 0 && _frames.Last().SnapshotSequence == final.SnapshotSequence)
            {
                var pending = _frames.ToArray();
                _frames.Clear();
                for (int i = 0; i < pending.Length; i++) _frames.Enqueue(i == pending.Length - 1 ? final : pending[i]);
            }
            else EnqueueFrame(final);
        }
        long through = _latest?.SnapshotSequence ?? -1;
        int tick = _latest?.SimulationTick64 ?? 0;
        foreach (var e in _client.Events?.TakeThrough(through, tick) ?? [])
            if (e is not RaceFinishedEvent && _events.Count < QueueLimit) _events.Enqueue(e);
        if (_client.Results is { } finished && _resultAnnounced && !_finishEventQueued)
        {
            _finishEventQueued = true;
            if (_events.Count == QueueLimit)
            {
                var queued = _events.ToArray();
                _events.Clear();
                int remove = Array.FindIndex(queued, e => e is not RaceFinishedEvent);
                for (int i = 0; i < queued.Length; i++) if (i != remove) _events.Enqueue(queued[i]);
            }
            _events.Enqueue(new RaceFinishedEvent(_latest!.RaceId, long.MaxValue, through, finished.SimulationTick64, finished.Results));
        }
    }

    void EnqueueFrame(RaceViewFrame frame)
    {
        if (_frames.Count == QueueLimit) _frames.Dequeue();
        _frames.Enqueue(frame);
    }

    public long SubmitInput(int localSeat, PlayerInput input)
    {
        if (_left || _disposed || _terminalFrame != null || _client.State is RaceClientState.Closed or RaceClientState.Rejected or RaceClientState.Finished)
            throw new InvalidOperationException("Remote race session is closed.");
        var desc = Description;
        if (localSeat != 0 || desc.LocalSeats.Count != 1)
            throw new ArgumentOutOfRangeException(nameof(localSeat), "Seat is not assigned to this connection.");
        // Real-time controls remain current even while reliable one-shot capacity is exhausted.
        if (_client.Spectator) return ++_nextToken; // nothing is sent for a spectator
        if (_realtimeInputs) _client.Controls = input with { Buttons = (byte)(input.Buttons & NetInputSample.HeldMask) };
        byte actions = (byte)(input.Buttons & (PlayerInput.BtnGearUp | PlayerInput.BtnGearDown | PlayerInput.BtnReset));
        byte pressed = (byte)(actions & ~_previousActions);
        var requested = new List<NetActionKind>(3);
        if ((pressed & PlayerInput.BtnGearUp) != 0) requested.Add(NetActionKind.GearUp);
        if ((pressed & PlayerInput.BtnGearDown) != 0) requested.Add(NetActionKind.GearDown);
        if ((pressed & PlayerInput.BtnReset) != 0) requested.Add(NetActionKind.Reset);
        if (_client.PendingActions + requested.Count > NetProtocol.MaxActionsPerPacket)
            throw new InvalidOperationException("Remote action queue is full; retry after an ACK.");
        if (!_realtimeInputs && _client.PendingInputs >= RaceHost.InputQueueLimit)
            throw new InvalidOperationException("Remote input queue is full.");
        foreach (var action in requested)
            if (!_client.QueueAction(action)) throw new InvalidOperationException("Remote action queue is full.");
        long token = ++_nextToken;
        if (!_client.QueueInput(token, input, _realtimeInputs)) throw new InvalidOperationException("Remote input queue is full.");
        _previousActions = actions;
        return token;
    }

    /// <summary>Refreshes held controls from the current render frame for the next realtime datagram.</summary>
    public void UpdateRealtimeControls(PlayerInput input)
    {
        if (_left || _disposed || !_realtimeInputs || _client.Spectator) return;
        _client.Controls = input with { Buttons = (byte)(input.Buttons & NetInputSample.HeldMask) };
    }

    RaceViewFrame MapInputAck(RaceViewFrame frame)
    {
        long wireAck = frame.AppliedInput.Count == 0 ? 0 : frame.AppliedInput[0];
        if (_inputTokens.TryGetValue(wireAck, out long token))
            _lastAckedToken = Math.Max(_lastAckedToken, token);
        foreach (var key in _inputTokens.Keys.Where(k => k <= wireAck).ToArray()) _inputTokens.Remove(key);
        return frame with { AppliedInput = Array.AsReadOnly(new[] { _lastAckedToken }) };
    }

    void OnInputSampleCreated(NetInputSample sample, long? token)
    {
        if (token is { } value) _inputTokens[sample.Sequence] = value;
        while (_inputTokens.Count > InputTokenMapLimit) _inputTokens.Remove(_inputTokens.Keys.First());
    }

    public RaceViewState ReadLatestState()
    {
        var frame = _latest ?? throw new InvalidOperationException("Initial race snapshot has not arrived.");
        return new RaceViewState(frame, false, false,
            !_left && !_client.Disconnected && _client.State is not (RaceClientState.Closed or RaceClientState.Rejected));
    }

    public IReadOnlyList<RaceViewFrame> DrainFrames()
    {
        var items = _frames.ToArray();
        _frames.Clear();
        return items;
    }

    public IReadOnlyList<RaceEvent> DrainEvents()
    {
        var items = _events.ToArray();
        _events.Clear();
        return items;
    }

    public bool RequestPause() => false;
    public void RequestResume() { }

    public void RequestLeave()
    {
        if (_left) return;
        _left = true;
        _client.Leave();
    }

    public void Dispose()
    {
        if (_disposed) return;
        RequestLeave();
        _client.InputSampleCreated -= OnInputSampleCreated;
        _disposed = true;
    }

    /// <summary>Drop presentation state after a result while keeping the client's transport seat for the next round.</summary>
    public void DetachForNextRound()
    {
        if (_disposed) return;
        _client.InputSampleCreated -= OnInputSampleCreated;
        _disposed = true;
    }
}
