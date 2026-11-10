using System.Collections.ObjectModel;
using System.Security.Cryptography;
using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Client;

public enum RaceClientState { Connecting, Welcomed, Ready, Started, Finished, Closed, Rejected }

/// <summary>Counters of a client (diagnostics, tests).</summary>
public sealed class RaceClientStats
{
    public long SnapshotParts, SnapshotsComplete, SnapshotsIncomplete, StaleParts, WrongSession, Malformed, InputPackets, SamplesSent,
        DurableApplied, EventsReceived, FilledSamples, LeadIncreases, MaxSnapshotAgeMicros, BeforeBaseline, Resumes;
}

/// <summary>
/// Headless network client logic (stage 5a.3), independent of sockets and of the wall clock. It joins with a hello, receives the
/// frozen race description, reports ready, follows the server clock (estimated from snapshot arrival and RTT – the client never
/// sets the race time) and sends its controls as redundant samples targeted at future server steps, plus one-shot actions with
/// ids until the server acknowledges them. Received snapshots are assembled into immutable <see cref="RaceViewFrame"/>s with the
/// durable state (knocked objects, tyre marks) mirrored from the reliable stream. It does not simulate anything.
/// One transport connection can carry consecutive races: after the result of a round (<see cref="RaceClientState.Finished"/>),
/// a welcome with another race id and epoch starts a new clean round on the same connection (the name, fingerprint and learned
/// input lead stay; every other round state is discarded). Before the countdown the server may recompose the race for exactly
/// the connected humans and re-welcome the same connection; such a welcome is accepted in <see cref="RaceClientState.Welcomed"/>
/// / <see cref="RaceClientState.Ready"/> when both its race id and its epoch are new. Stale or duplicated welcomes are ignored.
/// </summary>
public sealed class RaceClient : INetClientHandler
{
    public const int MaxPendingSnapshots = 4, FrameQueueLimit = 64, ClockWindow = 64, MaxLeadExtra = 16;

    readonly INetClientTransport? _transport;
    readonly byte[] _fingerprint;
    readonly string _name;
    readonly int _carType;
    readonly Func<NetWelcome, bool>? _prepare;
    readonly Action<string>? _log;
    readonly Dictionary<long, (NetSnapshotPart?[] Parts, long FirstArrival)> _pending = [];
    readonly Queue<RaceViewFrame> _frames = new();
    readonly Queue<(NetSnapshotInfo Info, NetSeatAck Ack, CarFrame[] Cars, CarFrame[] Parked)> _durableWait = new();
    readonly SortedDictionary<long, NetDurable> _pendingDurable = new();
    readonly SortedDictionary<long, (long SnapshotSequence, IReadOnlyDictionary<ushort, KnockedObject> Knocked, SkidBuffer Skids)> _durableHistory = new();
    readonly List<NetInputSample> _sent = [];
    readonly Queue<(long Token, PlayerInput Input)> _queuedInputs = new();
    readonly List<NetAction> _actions = [];
    readonly Dictionary<ushort, KnockedObject> _knocked = [];
    readonly HashSet<int> _seenRaceIds = [];
    readonly HashSet<uint> _seenEpochs = [];
    SkidBuffer _skids = new();
    readonly long[] _clockSamples = new long[ClockWindow];
    int _clockCount, _clockNext;
    IReadOnlyDictionary<ushort, KnockedObject> _knockedView = ReadOnlyDictionary<ushort, KnockedObject>.Empty;
    SkidBuffer _skidsView = new();
    CarFrame[]? _parked;
    long _lastComplete = -1, _lastPublishedSnapshot = -1, _lastPublishedDurableSequence, _lastTarget, _sequence, _actionId, _lastLate;
    long? _latestProviderInputToken;
    long _startEstimate = long.MinValue;  // local time of host step 1 from the start message
    long _now;
    int _leadExtra;
    long _lastControlResendMicros = long.MinValue;
    bool _resumeDurable;

    public RaceClientState State { get; private set; } = RaceClientState.Connecting;
    public bool Disconnected { get; private set; }
    public NetWelcome? Welcome { get; private set; }
    /// <summary>The round is only watched (an eliminated knockout player): the stream is the racers' stream, but the client owns no seat
    /// and sends no controls.</summary>
    public bool Spectator => Welcome?.Spectator == true;
    /// <summary>Race car index of the local player (<c>null</c> for a spectator or before the welcome); the result row with this
    /// <see cref="NFS.Game.Race.RacerResult.CarIndex"/> is the local one (the "local" flag of the original network result).</summary>
    public int? LocalCarIndex => Welcome is { Spectator: false } w ? w.CarId : null;
    /// <summary>Winner row for which the original shows the victory screen in a network game (the winner must be the local [ref:2429]
    /// player, besides the usual <see cref="NFS.Game.FrontEnd.VictoryRules.Winner"/> conditions); <c>null</c> otherwise or without result.</summary>
    public NFS.Game.Race.RacerResult? LocalWinner => Results is { } r && LocalCarIndex is { } me
        && NFS.Game.FrontEnd.VictoryRules.Winner(r.Results) is { } win && win.CarIndex == me ? win : null;
    public RaceSessionDescription? Description { get; private set; }
    public NetReject? RejectReason { get; private set; }
    public NetResults? Results { get; private set; }
    public RaceViewFrame? Baseline { get; private set; }
    public RaceViewFrame? Latest { get; private set; }
    public NetSnapshotInfo? LatestInfo { get; private set; }
    public NetSeatAck LatestAck { get; private set; }
    public long LatestDurableSequence { get; private set; }
    public RaceEventInbox? Events { get; private set; }
    public List<NetPlayerLeft> PlayersLeft { get; } = [];
    public IReadOnlyDictionary<int, string> PlayerNames => _playerNames;
    readonly Dictionary<int, string> _playerNames = [];
    public IReadOnlySet<int> ReadySeats => _readySeats;
    readonly HashSet<int> _readySeats = [];
    public RaceClientStats Stats { get; } = new();
    /// <summary>Continuous controls sampled for the next targets (steer, gas, brake; held bits handbrake/horn only).</summary>
    public PlayerInput Controls { get; set; }
    /// <summary>Extra steps of input lead on top of the RTT estimate (jitter margin).</summary>
    public int LeadSafetySteps { get; init; } = 2;
    /// <summary>Uses target-zero latest-wins controls, intended for live desktop input.</summary>
    public bool RealtimeControls { get; set; }
    public int PendingActions => _actions.Count;
    public int PendingInputs => _queuedInputs.Count;
    /// <summary>Durable state mirrored so far (may be ahead of or behind <see cref="Latest"/>; channels are independent).</summary>
    public IReadOnlyDictionary<ushort, KnockedObject> DurableKnocked => _knockedView;
    public SkidBuffer DurableSkids => _skidsView;
    /// <summary>Diagnostics hook: every input sample when it is created (tests compare it with the host's applied inputs).</summary>
    public Action<NetInputSample>? SampleCreated { get; init; }
    public long LastSentSequence => _sequence;
    public event Action<NetInputSample, long?>? InputSampleCreated;

    /// <param name="prepare">Loads/validates the race data for the welcome (5a.4 presentation hook); false = leave. Null = ready at once.</param>
    /// <param name="carType">Car model type selected in the menu (<see cref="NetProtocol.NoCarType"/> = none; the server keeps
    /// the frozen menu model). Sent in the hello; the server builds the human car with it.</param>
    public RaceClient(INetClientTransport? transport, byte[] dataFingerprint, string name, Func<NetWelcome, bool>? prepare = null,
        Action<string>? log = null, int carType = NetProtocol.NoCarType)
    {
        if (!NetProtocol.IsCarTypeOrNone(carType))
            throw new ArgumentOutOfRangeException(nameof(carType), $"car type must be {NetProtocol.NoCarType} (none) or 0..{NetProtocol.MaxCarType}");
        _transport = transport;
        _fingerprint = dataFingerprint;
        _name = name;
        _carType = carType;
        _prepare = prepare;
        _log = log;
    }

    /// <summary>Car model type sent in the hello (<see cref="NetProtocol.NoCarType"/> = no explicit selection).</summary>
    public int CarType => _carType;
    /// <summary>Resume token of a reserved game seat; sent by <see cref="Hello"/>. Set before connecting a reconnecting
    /// client (null = a fresh join).</summary>
    public byte[]? ResumeToken { get; set; }
    /// <summary>Data mods sent in the hello (D9).</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;

    /// <summary>Transport set after construction (the simulated network creates the endpoint with this handler).</summary>
    public INetClientTransport? Transport { get; set; }
    INetClientTransport? T => Transport ?? _transport;

    public byte[] Hello() => new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint, _name, _carType,
        BuildInfo.AppVersion, ResumeToken) { Mods = Mods }.Encode();

    void Log(string s) => _log?.Invoke(s);

    public void OnConnected() => Log("connected");

    public void OnDisconnected(byte[]? reason)
    {
        Disconnected = true;
        RejectReason = reason == null ? new NetReject(NetRejectReason.Timeout) : NetReject.TryDecode(reason) ?? new NetReject(NetRejectReason.Malformed);
        if (State != RaceClientState.Finished) State = State == RaceClientState.Connecting ? RaceClientState.Rejected : RaceClientState.Closed;
        Log($"disconnected: {RejectReason.Reason}");
    }

    /// <summary>Leaves the race (DNF after the start).</summary>
    public void Leave()
    {
        if (Welcome is { } w) T?.Send(NetChannel.ReliableOrdered, NetLeave.Encode(w.RaceId, w.Epoch));
        State = RaceClientState.Closed;
    }

    public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (Clock != null) _now = Clock();
        try
        {
            if (data.Length > NetProtocol.MaxReliableMessage) throw new NetProtocolException("oversized server message");
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            if (h.Type == NetMessageType.Welcome)
            {
                OnWelcome(NetWelcome.Decode(data));
                return;
            }
            if (Welcome == null || h.RaceId != Welcome.RaceId || h.Epoch != Welcome.Epoch) { Stats.WrongSession++; return; }
            switch (h.Type)
            {
                case NetMessageType.Start: OnStart(NetStart.Decode(h, ref r)); break;
                case NetMessageType.Snapshot: OnPart(SnapshotCodec.Decode(h, ref r)); break;
                case NetMessageType.Durable: OnDurable(NetDurable.Decode(h, ref r)); break;
                case NetMessageType.Events:
                {
                    var ev = NetEvents.Decode(h, ref r);
                    Stats.EventsReceived += ev.Events.Count;
                    Events!.Accept(ev.Events);
                    break;
                }
                case NetMessageType.Results:
                {
                    var result = NetResults.Decode(h, ref r);
                    if (Results == null)
                    {
                        Results = result;
                        State = RaceClientState.Finished;
                        AttachResults();
                        // A graceful server stop waits for this acknowledgement before it closes the connection, so the
                        // result is never lost to the disconnect.
                        T?.Send(NetChannel.ReliableOrdered, NetResultsAck.Encode(h.RaceId, h.Epoch));
                    }
                    break;
                }
                case NetMessageType.PlayerLeft: PlayersLeft.Add(NetPlayerLeft.Decode(h, ref r)); break;
                case NetMessageType.Roster:
                {
                    var roster = NetRoster.Decode(h, ref r);
                    _playerNames.Clear();
                    foreach (var player in roster.Players) _playerNames.Add(player.Key, player.Value);
                    _readySeats.Clear();
                    foreach (int seat in roster.ReadySeats) _readySeats.Add(seat);
                    break;
                }
                default: Stats.Malformed++; break;
            }
        }
        catch (NetProtocolException e)
        {
            Stats.Malformed++;
            Log($"malformed message: {e.Message}");
        }
    }

    void OnWelcome(NetWelcome w)
    {
        bool reconfiguration = false, resume = false;
        if (Welcome != null)
        {
            // A welcome may only replace a round that has not started (the server composed a host for exactly the connected
            // humans before the countdown) or that has finished (consecutive race). It must be a different race: a
            // replayed/duplicated welcome of the current round, or a reused id/epoch, never resets live state.
            reconfiguration = State is RaceClientState.Welcomed or RaceClientState.Ready;
            // A resume welcome re-attaches the live round after a lost connection: same race, fresh epoch, Started state.
            resume = State == RaceClientState.Started && w.Resume && w.RaceId == Welcome.RaceId &&
                w.Epoch != Welcome.Epoch && !_seenEpochs.Contains(w.Epoch);
            if ((!reconfiguration && State != RaceClientState.Finished && !resume) || Disconnected)
            {
                Stats.WrongSession++;
                Log($"ignoring welcome in {State}: race {w.RaceId:X8} epoch {w.Epoch:X8}");
                return;
            }
            if (!resume && (_seenRaceIds.Contains(w.RaceId) || _seenEpochs.Contains(w.Epoch)))
            {
                Stats.WrongSession++;
                Log($"ignoring welcome with a reused race id or epoch: race {w.RaceId:X8} epoch {w.Epoch:X8}");
                return;
            }
        }
        if (!SHA256.HashData(w.SetupBlock).AsSpan().SequenceEqual(w.SetupHash) || !w.DataFingerprint.AsSpan().SequenceEqual(_fingerprint))
        {
            Log("welcome does not match the local data");
            T?.Disconnect();
            State = RaceClientState.Closed;
            return;
        }
        if (Welcome != null)
        {
            Log($"{(resume ? "resuming the round" : reconfiguration ? "race reconfigured before the start" : "next round")}: " +
                $"race {Welcome.RaceId:X8} -> {w.RaceId:X8}, epoch {Welcome.Epoch:X8} -> {w.Epoch:X8}");
            ResetRound();
        }
        _seenRaceIds.Add(w.RaceId);
        _seenEpochs.Add(w.Epoch);
        Welcome = w;
        Description = w.ToDescription();
        Events = new RaceEventInbox(w.RaceId, RaceEventInbox.DefaultMaxTracked, NetProtocol.EventExpiryTicks64);
        _durableHistory[0] = (0, _knockedView, _skids.Clone());
        if (w.Resume) Stats.Resumes++;
        State = w.Resume ? RaceClientState.Started : RaceClientState.Welcomed;
        Log($"{(w.Resume ? "resume welcome" : "welcome")}{(w.Spectator ? " (spectator)" : "")}: race {w.RaceId:X8} seat {w.Seat} car {w.CarId}, {w.CarCount} cars, " +
            $"{w.ParkedCount} parked");
        if (_prepare != null && !_prepare(w)) { Leave(); return; }
        T?.Send(NetChannel.ReliableOrdered, new NetReady(w.RaceId, w.Epoch, w.SetupHash).Encode());
        if (w.Resume) _resumeDurable = true;
        else State = RaceClientState.Ready;
    }

    /// <summary>Forgets everything that belongs to the finished round, so the next welcome starts from a clean round on the same
    /// connection. Transport identity, name, fingerprint and configuration stay; <see cref="Stats"/> are connection diagnostics.</summary>
    void ResetRound()
    {
        _pending.Clear();
        _frames.Clear();
        _durableWait.Clear();
        _pendingDurable.Clear();
        _durableHistory.Clear();
        _sent.Clear();
        _queuedInputs.Clear();
        _actions.Clear();
        _knocked.Clear();
        _skids = new SkidBuffer();
        _knockedView = ReadOnlyDictionary<ushort, KnockedObject>.Empty;
        _skidsView = new SkidBuffer();
        _parked = null;
        Latest = null;
        LatestInfo = null;
        LatestAck = default;
        LatestDurableSequence = 0;
        Baseline = null;
        Results = null;
        PlayersLeft.Clear();
        _playerNames.Clear();
        _readySeats.Clear();
        _lastComplete = -1;
        _lastPublishedSnapshot = -1;
        _lastPublishedDurableSequence = 0;
        _lastTarget = 0;
        _sequence = 0;
        _actionId = 0;
        _lastLate = 0;
        _latestProviderInputToken = null;
        _startEstimate = long.MinValue;
        _lastControlResendMicros = long.MinValue;
        _clockCount = 0;
        _clockNext = 0;
        Array.Clear(_clockSamples);
        Controls = default;
        _resumeDurable = false;
    }

    void OnStart(NetStart s)
    {
        long owd = (T?.RoundTripMicros ?? 0) / 2;
        _startEstimate = _now - owd + s.LeadMicros;
        if (State is RaceClientState.Ready or RaceClientState.Welcomed) State = RaceClientState.Started;
        Log($"start: first step at local {_startEstimate}");
    }

    // ---------------- snapshots ----------------

    void OnPart(NetSnapshotPart p)
    {
        Stats.SnapshotParts++;
        long seq = p.Info.SnapshotSequence;
        if (seq <= _lastComplete) { Stats.StaleParts++; return; }
        // parked cars of later snapshots are deltas against the baseline (reliable, but on another channel): wait for it
        if (seq > 0 && Baseline == null) { Stats.BeforeBaseline++; return; }
        if (seq > 0) ClockSample(p.Info.Steps);
        if (!_pending.TryGetValue(seq, out var set))
        {
            if (p.PartCount > 255) throw new NetProtocolException("parts");
            set = (new NetSnapshotPart?[p.PartCount], _now);
            _pending[seq] = set;
            while (_pending.Count > MaxPendingSnapshots)
            {
                _pending.Remove(_pending.Keys.Min());
                Stats.SnapshotsIncomplete++;
            }
        }
        if (set.Parts.Length != p.PartCount) { Stats.Malformed++; return; }
        set.Parts[p.PartIndex] ??= p;
        if (set.Parts.Any(x => x == null)) return;
        _pending.Remove(seq);
        foreach (var old in _pending.Keys.Where(k => k < seq).ToArray()) { _pending.Remove(old); Stats.SnapshotsIncomplete++; }
        Complete(set.Parts!, set.FirstArrival);
    }

    void Complete(NetSnapshotPart?[] parts, long arrival)
    {
        var w = Welcome!;
        var info = parts[0]!.Info;
        var cars = new CarFrame?[w.CarCount];
        _parked ??= new CarFrame[w.ParkedCount];
        var parked = (CarFrame[])_parked.Clone();
        foreach (var p in parts)
        {
            foreach (var c in p!.Cars)
            {
                if (c.CarId >= cars.Length || cars[c.CarId] != null) throw new NetProtocolException("car id");
                cars[c.CarId] = c;
            }
            foreach (var c in p.Parked)
            {
                if (c.CarId >= parked.Length) throw new NetProtocolException("parked id");
                parked[c.CarId] = c;
            }
        }
        if (cars.Any(c => c == null)) throw new NetProtocolException("snapshot without all cars");
        if (info.SnapshotSequence == 0 && parked.Any(c => c == null)) throw new NetProtocolException("baseline without parked cars");
        _parked = parked;
        _lastComplete = info.SnapshotSequence;
        Stats.SnapshotsComplete++;
        Stats.MaxSnapshotAgeMicros = Math.Max(Stats.MaxSnapshotAgeMicros, _now - arrival);
        var ack = parts[0]!.Ack;
        LatestInfo = info;
        if (info.SnapshotSequence > 0) OnAck(ack);
        var completeCars = cars.Select(c => c!).ToArray();
        if (info.DurableSequence > LatestDurableSequence)
        {
            if (_durableWait.Count == FrameQueueLimit)
            {
                _durableWait.Dequeue();
                Stats.SnapshotsIncomplete++;
            }
            _durableWait.Enqueue((info, ack, completeCars, parked));
            return;
        }
        if (!PublishFrame(info, ack, completeCars, parked)) Stats.SnapshotsIncomplete++;
    }

    bool PublishFrame(NetSnapshotInfo info, NetSeatAck ack, CarFrame[] cars, CarFrame[] parked)
    {
        if (info.SnapshotSequence <= _lastPublishedSnapshot || info.DurableSequence < _lastPublishedDurableSequence) return false;
        if (!_durableHistory.TryGetValue(info.DurableSequence, out var durable) || durable.SnapshotSequence > info.SnapshotSequence)
            return false;
        var frame = new RaceViewFrame
        {
            RaceId = Welcome!.RaceId, SnapshotSequence = info.SnapshotSequence, Kind = info.Kind, SimulationTick64 = info.SimulationTick64,
            Steps = info.Steps, RaceStarted = info.RaceStarted, NumLaps = info.NumLaps, RacerCount = info.RacerCount,
            Cars = Array.AsReadOnly(cars), Parked = Array.AsReadOnly(parked), Ranking = info.Ranking,
            AppliedInput = Array.AsReadOnly(new[] { ack.AppliedInput }), Knocked = durable.Knocked, Skids = durable.Skids.Clone(),
            Results = info.HasResults ? Results?.Results : null,
        };
        if (info.SnapshotSequence == 0) Baseline = frame;
        _lastPublishedSnapshot = info.SnapshotSequence;
        _lastPublishedDurableSequence = info.DurableSequence;
        Latest = frame;
        if (_frames.Count >= FrameQueueLimit) _frames.Dequeue();
        _frames.Enqueue(frame);
        return true;
    }

    void AttachResults()
    {
        if (Results is not { } result) return;
        if (Latest?.Steps == result.Step) Latest = Latest with { Results = result.Results };
        if (_frames.Count > 0)
        {
            var frames = _frames.Select(f => f.Steps == result.Step ? f with { Results = result.Results } : f).ToArray();
            _frames.Clear();
            foreach (var frame in frames) _frames.Enqueue(frame);
        }
    }

    void OnAck(NetSeatAck ack)
    {
        LatestAck = ack;
        _actions.RemoveAll(a => a.Id <= ack.LastAction);
        // late samples mean the lead is too short for this connection: lead one step more (bounded)
        if (ack.LateInputs > _lastLate && _leadExtra < MaxLeadExtra) { _leadExtra++; Stats.LeadIncreases++; }
        _lastLate = ack.LateInputs;
    }

    void OnDurable(NetDurable d)
    {
        if (_resumeDurable)
        {
            // First durable of a resumed round. The old round's stream reached some sequence S; the server continues after
            // it (the full state, then incremental deltas). Rebase so the fresh baseline, whose info carries S, can publish.
            _resumeDurable = false;
            _pendingDurable.Clear();
            if (d.DurableSequence <= LatestDurableSequence) return;
            LatestDurableSequence = d.DurableSequence - 1;
        }
        if (d.DurableSequence <= LatestDurableSequence || _pendingDurable.ContainsKey(d.DurableSequence)) return;
        if (_pendingDurable.Count >= FrameQueueLimit) throw new NetProtocolException("durable reorder window exceeded");
        _pendingDurable.Add(d.DurableSequence, d);
        while (_pendingDurable.Remove(LatestDurableSequence + 1, out var next)) ApplyDurable(next);
    }

    void ApplyDurable(NetDurable d)
    {
        LatestDurableSequence = d.DurableSequence;
        if (d.Knocked.Count > 0)
        {
            foreach (var (id, k) in d.Knocked) _knocked[id] = k;
            _knockedView = new ReadOnlyDictionary<ushort, KnockedObject>(new Dictionary<ushort, KnockedObject>(_knocked));
        }
        if (d.Skids != null)
        {
            d.Skids.Apply(_skids);
            _skidsView = _skids.Clone();
        }
        Stats.DurableApplied++;
        _durableHistory[d.DurableSequence] = (d.SnapshotSequence, _knockedView, _skidsView.Clone());
        while (_durableWait.TryPeek(out var waiting) && waiting.Info.DurableSequence <= LatestDurableSequence)
        {
            _durableWait.Dequeue();
            if (!PublishFrame(waiting.Info, waiting.Ack, waiting.Cars, waiting.Parked)) Stats.SnapshotsIncomplete++;
        }
        while (_durableHistory.Count > 256) _durableHistory.Remove(_durableHistory.Keys.First());
        AttachResults();
    }

    /// <summary>Frames completed since the previous call, oldest first (bounded).</summary>
    public IReadOnlyList<RaceViewFrame> DrainFrames()
    {
        var list = _frames.ToArray();
        _frames.Clear();
        return list;
    }

    // ---------------- server clock and input ----------------

    void ClockSample(long steps)
    {
        // arrival − (steps − 1)·step = local time of host step 1 + one-way delay (+ jitter); the window minimum removes jitter
        _clockSamples[_clockNext] = _now - (steps - 1) * NetProtocol.StepMicros;
        _clockNext = (_clockNext + 1) % ClockWindow;
        _clockCount = Math.Min(_clockCount + 1, ClockWindow);
    }

    /// <summary>Estimated local time at which host step 1 ran (null before the start).</summary>
    public long? StepOneLocalTime
    {
        get
        {
            long owd = (T?.RoundTripMicros ?? 0) / 2;
            if (_clockCount > 0)
            {
                long min = long.MaxValue;
                for (int i = 0; i < _clockCount; i++) min = Math.Min(min, _clockSamples[i]);
                return min - owd;
            }
            return _startEstimate == long.MinValue ? null : _startEstimate;
        }
    }

    /// <summary>Server step expected to run when a packet sent now arrives, plus the safety lead.</summary>
    public long? TargetStep(long now)
    {
        if (StepOneLocalTime is not { } t1) return null;
        long owd = (T?.RoundTripMicros ?? 0) / 2;
        long arrive = now + owd - t1;
        long step = arrive < 0 ? 1 : arrive / NetProtocol.StepMicros + 1;
        return step + LeadSafetySteps + _leadExtra;
    }

    /// <summary>Queues a gear/reset action; false when <see cref="NetProtocol.MaxActionsPerPacket"/> actions are still unacknowledged.</summary>
    public bool QueueAction(NetActionKind kind)
    {
        if (_actions.Count >= NetProtocol.MaxActionsPerPacket) return false;
        _actions.Add(new NetAction(++_actionId, kind, RealtimeControls ? 0 : Math.Max(_lastTarget + 1, TargetStep(_now) ?? 1)));
        return true;
    }

    /// <summary>Queues one provider input token for a future network sample. False means the bounded pending queue is full.</summary>
    public bool QueueInput(long token, PlayerInput input, bool realtime = false)
    {
        if (!realtime && _queuedInputs.Count >= RaceHost.InputQueueLimit) return false;
        if (realtime) _queuedInputs.Clear();
        else _queuedInputs.Enqueue((token, input with { Buttons = (byte)(input.Buttons & NetInputSample.HeldMask) }));
        Controls = input with { Buttons = (byte)(input.Buttons & NetInputSample.HeldMask) };
        _latestProviderInputToken = token;
        return true;
    }

    /// <summary>Advances the client to <paramref name="now"/>: produces the samples of newly due targets (at most a few to fill a
    /// hiccup) and sends one input datagram with the newest samples and the unacknowledged actions.</summary>
    public void Update(long now)
    {
        _now = now;
        if (State != RaceClientState.Started || Welcome is not { } w || w.Spectator) return;
        if (RealtimeControls)
        {
            const long realtimeSendIntervalMicros = 12_500; // 80 Hz, below the 128 datagram/s transport limit.
            var controlsNow = Controls with { Buttons = (byte)(Controls.Buttons & NetInputSample.HeldMask) };
            if (_lastControlResendMicros != long.MinValue && now - _lastControlResendMicros < realtimeSendIntervalMicros)
                return; // Coalesce quick changes to the newest controls; never exceed 80 packets/s.

            var realtime = new NetInputSample(++_sequence, 0, controlsNow.Steer, controlsNow.Gas,
                controlsNow.Brake, controlsNow.Buttons);
            _lastControlResendMicros = now;
            SampleCreated?.Invoke(realtime);
            InputSampleCreated?.Invoke(realtime, _latestProviderInputToken);
            Stats.SamplesSent++;
            T?.Send(NetChannel.Unreliable, new NetInputPacket(w.RaceId, w.Epoch,
                _lastComplete < 0 ? 0 : _lastComplete, [realtime], _actions.ToArray()).Encode());
            Stats.InputPackets++;
            return;
        }
        if (TargetStep(now) is not { } target) return;
        if (target <= _lastTarget) return;
        long from = Math.Max(_lastTarget + 1, target - 3);
        // after a long stall only the newest targets are filled; older ones are never sent (the server holds, then drives neutral)
        for (long t = from; t <= target; t++)
        {
            long? token = _latestProviderInputToken;
            var c = Controls;
            if (_queuedInputs.TryDequeue(out var queued)) { token = queued.Token; c = queued.Input; }
            var sample = new NetInputSample(++_sequence, t, c.Steer, c.Gas, c.Brake, (byte)(c.Buttons & NetInputSample.HeldMask));
            _sent.Add(sample);
            SampleCreated?.Invoke(sample);
            InputSampleCreated?.Invoke(sample, token);
            if (t < target) Stats.FilledSamples++;
            Stats.SamplesSent++;
        }
        _lastTarget = target;
        if (_sent.Count > w.InputRedundancy) _sent.RemoveRange(0, _sent.Count - w.InputRedundancy);
        var packet = new NetInputPacket(w.RaceId, w.Epoch, _lastComplete < 0 ? 0 : _lastComplete, _sent.ToArray(), _actions.ToArray());
        T?.Send(NetChannel.Unreliable, packet.Encode());
        Stats.InputPackets++;
    }

    /// <summary>Monotonic clock (microseconds) read when data arrives; without it the time of the last <see cref="Update"/> is used.</summary>
    public Func<long>? Clock { get; init; }
}
