using NFS.Core;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Game.Session;

/// <summary>Why a frame was published: <see cref="Initial"/> = state at host creation (presentation snaps to it),
/// <see cref="Step"/> = after one whole host step (32 Hz, two 64 Hz ticks), <see cref="Reset"/> = an out-of-step change of the
/// cars (local outside <c>Step</c>): animations and pose history advance as after a step, but no sound frame; [ref:2419]
/// <see cref="Resync"/> = a fresh copy of the unchanged host state on request of a local tool/test that edited the host directly
/// (presentation snaps to it like to <see cref="Initial"/>).</summary>
public enum RaceViewFrameKind { Initial, Step, Reset, Resync }

/// <summary>One car of a frame. <paramref name="CarId"/> = index in <see cref="RaceSession.Cars"/> (parked cars: index in
/// <see cref="RaceSession.Parked"/>). <paramref name="PoseEpoch"/> changes when the car jumped (manual/auto reset, teleport):
/// presentation must not interpolate between poses of different epochs.</summary>
public sealed record CarFrame(int CarId, CarView State, int PoseEpoch);

/// <summary>
/// Immutable snapshot of a race published by the host after each step (network stage 5a.2). It is a copy: no field references a live
/// <see cref="CarObj"/>, <see cref="RaceRunner"/> or the host's mutable buffers, so the renderer/audio of a local or remote client read
/// it without touching the simulation. Durable effects (knocked-over objects, tyre marks, results) are part of every frame;
/// one-shot effects arrive as <see cref="RaceEvent"/>s on a separate channel.
/// </summary>
public sealed record RaceViewFrame
{
    public required int RaceId { get; init; }
    /// <summary>Publication counter of this host (0 = initial frame), strictly increasing.</summary>
    public required long SnapshotSequence { get; init; }
    public required RaceViewFrameKind Kind { get; init; }
    /// <summary>Simulation tick (64 Hz) after the step – the unit of HUD times and replays. [ref:2420]</summary>
    public required int SimulationTick64 { get; init; }
    /// <summary>Whole host steps executed so far.</summary>
    public required int Steps { get; init; }
    public required bool RaceStarted { get; init; }
    public required int NumLaps { get; init; }
    public required int RacerCount { get; init; }
    /// <summary>Cars in <see cref="RaceSession.Cars"/> order (index = CarId), including cars that the host does not simulate.</summary>
    public required IReadOnlyList<CarFrame> Cars { get; init; }
    /// <summary>Parked (SMnn.DAT) cars, index = CarId in <see cref="RaceSession.Parked"/>.</summary>
    public required IReadOnlyList<CarFrame> Parked { get; init; }
    /// <summary>Race order as CarIds, leader first. [ref:2421]</summary>
    public required IReadOnlyList<int> Ranking { get; init; }
    /// <summary>Per local seat: InputSequence of the last input the host applied (0 = none yet) – the ACK of
    /// <see cref="IRaceSessionProvider.SubmitInput"/>.</summary>
    public required IReadOnlyList<long> AppliedInput { get; init; }
    /// <summary>Knocked-over XBID 11 objects (durable state, bw_dynobj_state).</summary>
    public required IReadOnlyDictionary<ushort, KnockedObject> Knocked { get; init; }
    /// <summary>Tyre marks (copy of the host's skid ring buffer; never mutated after publication).</summary>
    public required SkidBuffer Skids { get; init; }
    /// <summary>Effect particles (tyre smoke, sparks, dust; copy of the host's pool, never mutated after publication). Null = a source without
    /// them (a network client); presentation then draws none.</summary>
    public IReadOnlyList<Particle>? Particles { get; init; }
    /// <summary>Final result, captured once by the host (also announced by <see cref="RaceFinishedEvent"/>).</summary>
    public RaceResults? Results { get; init; }
    /// <summary>Replay playback only: state of the replay camera director (null = live race).</summary>
    public ReplayCameraView? ReplayCam { get; init; }

    public CarView Car(int carId) => Cars[carId].State;
}

/// <summary>Replay camera director as presentation sees it: the mode chosen by the last change [ref:2422]
/// the look-at field +0x50 (16.16 x component of the up hint) and the change counter. [ref:2423]</summary>
public sealed record ReplayCameraView(int Mode, int Field50, int Changes);

/// <summary>Static description of a race session for presentation (no live state).</summary>
/// <param name="LocalSeats">Seat index → CarId of the humans driven from this machine (seat 0 = player, seat 1 = split-screen
/// second human). The list of views on this computer is decided by the presentation, not by the slot mapping.</param>
/// <param name="SimulatedCars">CarIds in host simulation order (RaceRunner.Cars), e.g. for stable nearest-car selection.</param>
/// <param name="Redline">Per CarId: derived redline of simulated cars (0 for cars the host does not simulate).</param>
public sealed record RaceSessionDescription(int RaceId, IReadOnlyList<int> LocalSeats, IReadOnlyList<int> SimulatedCars,
    IReadOnlyList<int> Redline, bool Countdown, bool Playback)
{
    public int PlayerCarId => LocalSeats[0];
    public int? SecondPlayerCarId => LocalSeats.Count > 1 ? LocalSeats[1] : null;
}

/// <summary>Non-blocking read of a provider: the newest frame plus provider status (no I/O).</summary>
public sealed record RaceViewState(RaceViewFrame Frame, bool Paused, bool PlaybackEnded, bool Connected = true);
