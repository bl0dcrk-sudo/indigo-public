using System.Collections.ObjectModel;
using NFS.Core;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Game.Session;

/// <summary>
/// Host side of the view contract (stage 5a.2): copies the state of a <see cref="RaceRunner"/> into immutable
/// <see cref="RaceViewFrame"/>s and turns the runner's presentation hooks into <see cref="RaceEvent"/>s. The hooks that read
/// mid-step state stay here, next to the simulation: impacts (before they are cleared) and tyre marks [ref:2424]
/// (<see cref="SkidMarkTask"/>, after). Nothing here writes CarObj or a race RNG. [ref:2425]
/// </summary>
public sealed class RaceViewPublisher : IDisposable
{
    static int _nextRaceId;

    readonly RaceRunner _race;
    readonly Dictionary<CarObj, int> _carIds = new(ReferenceEqualityComparer.Instance);
    readonly int[] _simIds;
    readonly EpochTracker[] _epochs, _parkedEpochs;
    readonly List<RaceEvent> _stepEvents = [];
    readonly Func<long[]> _appliedInput;
    long _sequence, _eventId;
    int _knockedCount = -1, _skidAdded = -1, _skidCount = -1, _skidCurrent = -1;
    IReadOnlyDictionary<ushort, KnockedObject> _knocked = ReadOnlyDictionary<ushort, KnockedObject>.Empty;
    SkidBuffer _skids = new();
    int _particleVersion = -1;
    IReadOnlyList<Particle>? _particles;
    bool _finishedAnnounced, _disposed;

    public int RaceId { get; }
    public RaceSessionDescription Description { get; }
    /// <summary>Tyre-mark producer of this host (null when created without skids).</summary>
    public SkidMarkTask? Skids { get; }
    /// <summary>Effect-particle producer of this host (tyre smoke, sparks, dust; null when created without particles).</summary>
    public ParticleTask? Particles { get; }
    public RaceViewFrame Latest { get; private set; }
    /// <summary>Raised for every published frame with the events of that frame (oldest first).</summary>
    public event Action<RaceViewFrame, IReadOnlyList<RaceEvent>>? Published;

    /// <param name="raceId">Identifier of the race (a network server passes its own random id; default = process-wide counter).</param>
    public RaceViewPublisher(RaceRunner race, bool skids = true, bool playback = false, Func<long[]>? appliedInput = null, int? raceId = null, bool particles = false)
    {
        _race = race;
        RaceId = raceId ?? Interlocked.Increment(ref _nextRaceId);
        var cars = race.Session.Cars;
        for (int i = 0; i < cars.Count; i++) _carIds[cars[i].Obj] = i;
        _simIds = race.Cars.Select(c => _carIds[c.Obj]).ToArray();
        var redline = new int[cars.Count];
        foreach (var sim in race.Cars) redline[_carIds[sim.Obj]] = sim.Specs.Redline;
        // Seat order = RaceRunner.Humans (1: player, 2: split screen, 3+: list-seat race).
        var seats = race.Humans.Select(h => _carIds[h]).ToArray();
        Description = new RaceSessionDescription(RaceId, Array.AsReadOnly(seats), Array.AsReadOnly(_simIds), Array.AsReadOnly(redline),
            race.Countdown, playback);
        _appliedInput = appliedInput ?? (() => new long[seats.Length]);
        _epochs = cars.Select(_ => new EpochTracker()).ToArray();
        _parkedEpochs = (race.Session.Parked ?? []).Select(_ => new EpochTracker()).ToArray();
        if (skids) Skids = new SkidMarkTask(race, SkidMarkTask.SurfaceTable());
        if (particles) Particles = new ParticleTask(race);
        race.OnCountdown += OnCountdown;
        race.BeforeCarPost += OnBeforeCarPost;
        race.OnHornRequest += OnHornRequest;
        Latest = Capture(RaceViewFrameKind.Initial, 0, false, false, null);
    }

    public int CarId(CarObj car) => _carIds[car];

    /// <summary>Publishes the current state (<paramref name="steps"/> = whole host steps so far). <paramref name="playerReset"/>/<paramref name="secondReset"/> = the human of seat 0/1
    /// was reset in this step (a pose discontinuity). Step/Reset frames also advance the reset detectors. [ref:2426]</summary>
    public RaceViewFrame Publish(RaceViewFrameKind kind, int steps, bool playerReset = false, bool secondReset = false, RaceResults? results = null)
    {
        var frame = Capture(kind, steps, playerReset, secondReset, results);
        if (results != null && !_finishedAnnounced)
        {
            _finishedAnnounced = true;
            _stepEvents.Add(new RaceFinishedEvent(RaceId, ++_eventId, frame.SnapshotSequence, frame.SimulationTick64, results));
        }
        var events = _stepEvents.ToArray();
        _stepEvents.Clear();
        Latest = frame;
        Published?.Invoke(frame, events);
        return frame;
    }

    RaceViewFrame Capture(RaceViewFrameKind kind, int steps, bool playerReset, bool secondReset, RaceResults? results)
    {
        bool advance = kind is RaceViewFrameKind.Step or RaceViewFrameKind.Reset;
        var cars = _race.Session.Cars;
        var seats = Description.LocalSeats;
        var carFrames = new CarFrame[cars.Count];
        for (int i = 0; i < cars.Count; i++)
        {
            var obj = cars[i].Obj;
            bool flagged = i == seats[0] && playerReset || seats.Count > 1 && i == seats[1] && secondReset;
            int epoch = advance ? _epochs[i].Observe(obj, flagged) : _epochs[i].Epoch;
            carFrames[i] = new CarFrame(i, CarView.From(obj), epoch);
        }
        var parked = _race.Session.Parked ?? [];
        var parkedFrames = new CarFrame[parked.Count];
        for (int i = 0; i < parked.Count; i++)
            parkedFrames[i] = new CarFrame(i, CarView.From(parked[i]), advance ? _parkedEpochs[i].Observe(parked[i], false) : _parkedEpochs[i].Epoch);
        var dyn = _race.DynObjects.Knocked;
        if (dyn.Count != _knockedCount)
        {
            _knockedCount = dyn.Count;
            _knocked = new ReadOnlyDictionary<ushort, KnockedObject>(dyn.ToDictionary(k => k.Key, k => k.Value));
        }
        if (Skids?.Buffer is { } sb && (sb.Added != _skidAdded || sb.Count != _skidCount || sb.Current != _skidCurrent))
        {
            (_skidAdded, _skidCount, _skidCurrent) = (sb.Added, sb.Count, sb.Current);
            _skids = sb.Clone();
        }
        if (Particles?.Particles is { } ps && ps.Version != _particleVersion)
        {
            _particleVersion = ps.Version;
            _particles = ps.Snapshot();
        }
        var ranking = _race.Laps.Ranking().Select(r => _simIds[r]).ToArray();
        return new RaceViewFrame
        {
            RaceId = RaceId, SnapshotSequence = kind == RaceViewFrameKind.Initial && _sequence == 0 ? 0 : ++_sequence, Kind = kind,
            SimulationTick64 = _race.Tick, Steps = steps, RaceStarted = _race.RaceStarted,
            NumLaps = _race.Laps.NumLaps, RacerCount = _race.RacerCount,
            Cars = Array.AsReadOnly(carFrames), Parked = Array.AsReadOnly(parkedFrames), Ranking = Array.AsReadOnly(ranking),
            AppliedInput = Array.AsReadOnly(_appliedInput()), Knocked = _knocked, Skids = _skids, Particles = _particles, Results = results,
            ReplayCam = _race.ReplayCam is { } rc ? new ReplayCameraView(rc.Mode, rc.Field50, rc.Changes) : null,
        };
    }

    long NextSequence => _sequence + 1;

    void OnCountdown(int sample) =>
        _stepEvents.Add(new CountdownBeepEvent(RaceId, ++_eventId, NextSequence, _race.Tick, sample));

    void OnBeforeCarPost(SimCar car)
    {
        var c = car.Obj;
        // queued before Impact/ImpactType are cleared, only while shake is idle (RaceAudio guard). [ref:2427]
        if (c.Rail != 0 || c.Active == 0 || c.Impact <= 0xA0000 || Math.Max(c.ShakePhase0, c.ShakePhase1) >= 31) return;
        _stepEvents.Add(new CarImpactEvent(RaceId, ++_eventId, NextSequence, _race.Tick, _carIds[c], c.ImpactType, c.Impact, c.UpDotN, c.LastHit));
    }

    void OnHornRequest(CarObj car, int count, int length, int gap)
    {
        if (_carIds.TryGetValue(car, out int id))
            _stepEvents.Add(new HornRequestEvent(RaceId, ++_eventId, NextSequence, _race.Tick, id, count, length, gap));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _race.OnCountdown -= OnCountdown;
        _race.BeforeCarPost -= OnBeforeCarPost;
        _race.OnHornRequest -= OnHornRequest;
        Skids?.Dispose();
        Particles?.Dispose();
    }

    /// <summary>Reset/teleport detector of one car (formerly PoseHistory.Capture in the Desktop scene): a rise of +0x16C manual-reset
    /// lock, or the end of the auto-reset timer with a fresh collision cooldown while on the ground.</summary>
    sealed class EpochTracker
    {
        int _manualLock, _resetTimer, _cooldown;
        public int Epoch { get; private set; }

        public int Observe(CarObj car, bool discontinuity)
        {
            bool reset = car.ManualResetLock > _manualLock ||
                (_resetTimer > 0 && car.AutoResetCounter == 0 && car.CollisionCooldown > _cooldown && car.AirTicks <= 1);
            _manualLock = car.ManualResetLock;
            _resetTimer = car.AutoResetCounter;
            _cooldown = car.CollisionCooldown;
            if (reset || discontinuity) Epoch++;
            return Epoch;
        }
    }
}
