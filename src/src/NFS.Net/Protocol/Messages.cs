using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;

namespace NFS.Net.Protocol;

/// <summary>Session header of every message after the handshake: the receiver drops messages of another race
/// (<see cref="RaceId"/>) or of another connection epoch (<see cref="Epoch"/>, issued per accepted connection).</summary>
public readonly record struct NetHeader(NetMessageType Type, int RaceId, uint Epoch)
{
    public const int Size = 9;

    public void Write(WireWriter w)
    {
        w.U8((byte)Type);
        w.I32(RaceId);
        w.U32(Epoch);
    }

    public static NetHeader Read(ref WireReader r)
    {
        byte t = r.U8();
        if (t is < (byte)NetMessageType.Welcome or > (byte)NetProtocol.LastMessageType) throw new NetProtocolException($"unknown message type {t}");
        return new NetHeader((NetMessageType)t, r.I32(), r.U32());
    }

    /// <summary>Message type of a datagram without decoding it (0 when empty).</summary>
    public static NetMessageType Peek(ReadOnlySpan<byte> data) => data.Length == 0 ? 0 : (NetMessageType)data[0];
}

/// <summary>Server → every connected player: current names by seat while waiting for the shared race.</summary>
public sealed record NetRoster(int RaceId, uint Epoch, IReadOnlyDictionary<int, string> Players, IReadOnlyList<int> ReadySeats)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size + 2 + NetProtocol.MaxSeats * (2 + NetProtocol.MaxNameLength + 1));
        new NetHeader(NetMessageType.Roster, RaceId, Epoch).Write(w);
        w.U8(checked((byte)Players.Count));
        foreach (var (seat, name) in Players.OrderBy(p => p.Key))
        { w.U8(checked((byte)seat)); w.Ascii(name, NetProtocol.MaxNameLength); }
        w.U8(checked((byte)ReadySeats.Count));
        foreach (int seat in ReadySeats.Order()) w.U8(checked((byte)seat));
        return w.ToArray();
    }

    public static NetRoster Decode(NetHeader h, ref WireReader r)
    {
        int count = r.U8();
        if (count > NetProtocol.MaxSeats) throw new NetProtocolException("roster too large");
        var players = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
        {
            int seat = r.U8(); string name = r.Ascii(NetProtocol.MaxNameLength);
            if (seat >= NetProtocol.MaxSeats || name.Length == 0 || !players.TryAdd(seat, name)) throw new NetProtocolException("invalid roster");
        }
        int readyCount = r.U8();
        if (readyCount > count) throw new NetProtocolException("invalid ready roster");
        var ready = new int[readyCount];
        for (int i = 0; i < readyCount; i++)
        { ready[i] = r.U8(); if (!players.ContainsKey(ready[i]) || ready.Take(i).Contains(ready[i])) throw new NetProtocolException("invalid ready seat"); }
        r.End();
        return new NetRoster(h.RaceId, h.Epoch, players, ready);
    }
}

/// <summary>Why a connection is opened: <see cref="Play"/> = a player of the session, <see cref="ModDownload"/> = only to download the mods of the server (the connection never becomes a player).</summary>
public enum NetHelloPurpose : byte { Play = 0, ModDownload = 1 }

/// <summary>Connection request payload: protocol/physics versions, the application version (assembly info, checked by the
/// server), the fingerprint of the local simulation data, the player name, the car model type selected in the menu
/// (<see cref="NetProtocol.NoCarType"/> = no explicit selection) and an optional 16-byte resume token for a former member of a
/// game (null = no resume attempt).</summary>
public sealed record NetHello
{
    public ushort Version { get; init; }
    public ushort PhysicsVersion { get; init; }
    public byte[] DataFingerprint { get; init; }
    public string Name { get; init; }
    public int CarType { get; init; }
    /// <summary>Four-component application version; defaults to <see cref="BuildInfo.AppVersion"/> when not supplied.</summary>
    public Version AppVersion { get; init; }
    /// <summary>Resume token issued by the server for a game seat; null when this is a fresh connection.</summary>
    public byte[]? ResumeToken { get => _resumeToken; init => _resumeToken = value is { Length: 0 } ? null : value; }
    readonly byte[]? _resumeToken;
    /// <summary>Data mods of the sender (D9); the wire carries their fingerprint (<see cref="NetModSet.Fingerprint"/>) and the list.</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;
    /// <summary>What the connection is for (v19): a player (default) or only a download of the mods of the server.</summary>
    public NetHelloPurpose Purpose { get; init; } = NetHelloPurpose.Play;

    public NetHello(ushort Version, ushort PhysicsVersion, byte[] DataFingerprint, string Name,
        int CarType = NetProtocol.NoCarType, Version? AppVersion = null, byte[]? ResumeToken = null)
    {
        this.Version = Version;
        this.PhysicsVersion = PhysicsVersion;
        this.DataFingerprint = DataFingerprint;
        this.Name = Name;
        this.CarType = CarType;
        this.AppVersion = AppVersion ?? BuildInfo.AppVersion;
        this.ResumeToken = ResumeToken;
    }

    public byte[] Encode()
    {
        if (!NetProtocol.IsCarNumberOrNone(CarType))
            throw new ArgumentOutOfRangeException(nameof(CarType), $"car type must be {NetProtocol.NoCarType} (none) or 0..{NetProtocol.MaxCarNumber}");
        if (DataFingerprint.Length != 32) throw new ArgumentException("data fingerprint must be 32 bytes", nameof(DataFingerprint));
        if (ResumeToken is { } token && token.Length != NetProtocol.ResumeTokenLength)
            throw new ArgumentException($"resume token must be {NetProtocol.ResumeTokenLength} bytes or empty", nameof(ResumeToken));
        var w = new WireWriter(4 + 2 + 2 + 8 + 32 + 1 + NetProtocol.MaxNameLength + 1 + 1 + NetProtocol.ResumeTokenLength + 1 + NetModSet.MaxWireSize);
        w.U32(NetProtocol.Magic);
        w.U16(Version);
        w.U16(PhysicsVersion);
        BuildInfo.Write(w, AppVersion);
        w.Bytes(DataFingerprint);
        w.Ascii(Name, NetProtocol.MaxNameLength);
        w.I8(CarType);
        w.U8(ResumeToken?.Length ?? 0);
        if (ResumeToken is { } t) w.Bytes(t);
        w.U8((byte)Purpose);
        Mods.Write(w);
        return w.ToArray();
    }

    /// <summary>Reads only the magic and the protocol/physics versions, which every hello layout starts with, so a peer of
    /// another wire version can be told <see cref="NetRejectReason.ProtocolMismatch"/> instead of a decode failure.</summary>
    public static bool TryReadVersions(ReadOnlySpan<byte> data, out ushort version, out ushort physics)
    {
        version = physics = 0;
        if (data.Length < 8) return false;
        var r = new WireReader(data);
        if (r.U32() != NetProtocol.Magic) return false;
        version = r.U16();
        physics = r.U16();
        return true;
    }

    public static NetHello Decode(ReadOnlySpan<byte> data)
    {
        var r = new WireReader(data);
        if (r.U32() != NetProtocol.Magic) throw new NetProtocolException("bad magic");
        ushort version = r.U16(), physics = r.U16();
        var appVersion = BuildInfo.Read(ref r);
        var fingerprint = r.Bytes(32).ToArray();
        string name = r.Ascii(NetProtocol.MaxNameLength);
        int carType = r.I8();
        int tokenLength = r.U8();
        if (tokenLength is not 0 and not NetProtocol.ResumeTokenLength) throw new NetProtocolException("bad resume token length");
        var token = tokenLength == 0 ? null : r.Bytes(tokenLength).ToArray();
        byte purpose = r.U8();
        if (purpose > (byte)NetHelloPurpose.ModDownload) throw new NetProtocolException("bad hello purpose");
        var mods = NetModSet.Read(ref r);
        r.End();
        if (!NetProtocol.IsCarNumberOrNone(carType)) throw new NetProtocolException($"invalid car type {carType}");
        return new NetHello(version, physics, fingerprint, name, carType, appVersion, token) { Mods = mods, Purpose = (NetHelloPurpose)purpose };
    }
}

/// <summary>Reason sent with a rejected connection or a server-side disconnect, together with the server's protocol, physics
/// and application versions.</summary>
public sealed record NetReject
{
    public NetRejectReason Reason { get; init; }
    public ushort ServerVersion { get; init; }
    public ushort ServerPhysics { get; init; }
    public Version ServerAppVersion { get; init; }
    /// <summary>Mods of the server, sent with <see cref="NetRejectReason.ModsMismatch"/> so the client can name what differs.</summary>
    public NetModSet ServerMods { get; init; } = NetModSet.Empty;

    public NetReject(NetRejectReason Reason, ushort ServerVersion = NetProtocol.Version, ushort ServerPhysics = NetProtocol.PhysicsVersion,
        Version? ServerAppVersion = null)
    {
        this.Reason = Reason;
        this.ServerVersion = ServerVersion;
        this.ServerPhysics = ServerPhysics;
        this.ServerAppVersion = ServerAppVersion ?? BuildInfo.AppVersion;
    }

    /// <summary>Wire version that added the application version to the reject; older clients expect only
    /// magic + reason + protocol + physics and fail to parse anything longer.</summary>
    public const ushort AppVersionSinceProtocol = 8;

    /// <param name="clientVersion">Protocol version of the rejected client: below <see cref="AppVersionSinceProtocol"/> the
    /// reject is written in the old layout so that client still learns about the protocol mismatch.</param>
    public byte[] Encode(ushort clientVersion = NetProtocol.Version)
    {
        var w = new WireWriter(4 + 1 + 2 + 2 + 8 + NetModSet.MaxWireSize);
        w.U32(NetProtocol.Magic);
        w.U8((byte)Reason);
        w.U16(ServerVersion);
        w.U16(ServerPhysics);
        if (clientVersion >= AppVersionSinceProtocol) BuildInfo.Write(w, ServerAppVersion);
        if (Reason == NetRejectReason.ModsMismatch && clientVersion >= NetProtocol.Version) ServerMods.Write(w);
        return w.ToArray();
    }

    /// <summary>Null when the data is not a reject of this protocol (e.g. a transport timeout without data). The common prefix
    /// (magic, reason, versions) is all that is required: a reject from an older server has no application version
    /// (<see cref="ServerAppVersion"/> = 0.0.0.0) and fields a newer server appends are ignored, so a version mismatch is
    /// always reported as such.</summary>
    public static NetReject? TryDecode(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new WireReader(data);
            if (r.U32() != NetProtocol.Magic) return null;
            var reason = (NetRejectReason)r.U8();
            ushort version = r.U16(), physics = r.U16();
            var app = r.Remaining >= 8 ? BuildInfo.Read(ref r) : new Version(0, 0, 0, 0);
            var mods = NetModSet.Empty;
            if (reason == NetRejectReason.ModsMismatch && r.Remaining > 0) mods = NetModSet.Read(ref r);
            return new NetReject(reason, version, physics, app) { ServerMods = mods };
        }
        catch (NetProtocolException) { return null; }
    }
}

/// <summary>
/// Frozen race description sent once to an accepted client (reliable): the assigned seat and car, the race configuration block
/// (RaceSetup, 0x5B0 bytes) with its hash, the server data fingerprint and the timing/input parameters of this race. The race is
/// frozen before the countdown: the server never changes it afterwards.
/// </summary>
public sealed record NetWelcome(
    int RaceId, uint Epoch, int Seat, IReadOnlyList<int> SeatCarIds, int CarCount, int ParkedCount,
    IReadOnlyList<int> SimulatedCars, IReadOnlyList<int> Redline, bool Countdown, int NumLaps,
    long StepMicros, int MaxFutureSteps, int HoldSteps, int ActionExpirySteps, int InputRedundancy, int MaxDatagram,
    byte[] SetupBlock, byte[] SetupHash, byte[] DataFingerprint,
    /// <summary>True when this welcome re-attaches an existing seat after a reconnect (same race, new epoch, fresh baseline);
    /// false for a normal join or a next round. Appended last so a welcome without resume keeps the old encoding prefix.</summary>
    bool Resume = false,
    /// <summary>True for a spectator (an eliminated knockout player): it follows the round like a racer but owns no seat and sends no
    /// controls. <see cref="Seat"/> then only names the human seat whose car the view starts on (the other seats keep their meaning
    /// for names and results). Appended after <see cref="Resume"/>.</summary>
    bool Spectator = false)
{
    public int CarId => SeatCarIds[Seat];

    /// <summary>The client's view of the session: only its own seat is local (no split screen from the slot mapping).</summary>
    public RaceSessionDescription ToDescription() => new(RaceId, Array.AsReadOnly(new[] { CarId }), SimulatedCars, Redline, Countdown, false);

    public byte[] Encode()
    {
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        new NetHeader(NetMessageType.Welcome, RaceId, Epoch).Write(w);
        w.U8(Seat);
        w.U8(SeatCarIds.Count);
        foreach (var c in SeatCarIds) w.U8(c);
        w.U8(CarCount);
        w.U8(ParkedCount);
        w.U8(SimulatedCars.Count);
        foreach (var c in SimulatedCars) w.U8(c);
        w.U8(Redline.Count);
        foreach (var r in Redline) w.VarI(r);
        w.Bool(Countdown);
        w.U8(NumLaps);
        w.VarU(StepMicros);
        w.U16(MaxFutureSteps);
        w.U16(HoldSteps);
        w.U16(ActionExpirySteps);
        w.U8(InputRedundancy);
        w.U16(MaxDatagram);
        w.Block(SetupBlock);
        w.Bytes(SetupHash);
        w.Bytes(DataFingerprint);
        w.Bool(Resume);
        w.Bool(Spectator);
        return w.ToArray();
    }

    public static NetWelcome Decode(ReadOnlySpan<byte> data)
    {
        var r = new WireReader(data);
        var h = NetHeader.Read(ref r);
        if (h.Type != NetMessageType.Welcome) throw new NetProtocolException("not a welcome");
        int seat = r.U8();
        int seats = r.U8();
        if (seats is 0 or > NetProtocol.MaxSeats || seat >= seats) throw new NetProtocolException("bad seat");
        var seatCars = new int[seats];
        for (int i = 0; i < seats; i++) seatCars[i] = r.U8();
        int carCount = r.U8(), parked = r.U8();
        if (carCount is 0 or > NetProtocol.MaxCars || parked > NetProtocol.MaxCars) throw new NetProtocolException("bad car count");
        var sim = new int[r.U8()];
        for (int i = 0; i < sim.Length; i++) sim[i] = r.U8();
        var redline = new int[r.U8()];
        for (int i = 0; i < redline.Length; i++) redline[i] = r.VarI32();
        bool countdown = r.Bool();
        int laps = r.U8();
        long step = r.VarU(10_000_000);
        int future = r.U16(), hold = r.U16(), expiry = r.U16(), redundancy = r.U8(), datagram = r.U16();
        var block = r.Block(NetProtocol.MaxSetupBlock);
        var hash = r.Bytes(32).ToArray();
        var fp = r.Bytes(32).ToArray();
        bool resume = r.Bool();
        bool spectator = r.Bool();
        r.End();
        if (seatCars.Any(c => c >= carCount) || sim.Any(c => c >= carCount)) throw new NetProtocolException("car id out of range");
        if (h.RaceId <= 0 || h.Epoch == 0 || seatCars.Distinct().Count() != seats || sim.Distinct().Count() != sim.Length ||
            redline.Length != carCount || block.Length != NFS.Data.FrontEnd.RaceConfig.Size || laps is < 1 or > 8 ||
            step != NetProtocol.StepMicros || future != NetProtocol.MaxFutureSteps || hold != NetProtocol.HoldSteps ||
            expiry != NetProtocol.ActionExpirySteps || redundancy != NetProtocol.InputRedundancy || datagram != NetProtocol.MaxDatagram)
            throw new NetProtocolException("invalid session description");
        return new NetWelcome(h.RaceId, h.Epoch, seat, seatCars, carCount, parked, sim, redline, countdown, laps, step, future, hold,
            expiry, redundancy, datagram, block, hash, fp, resume, spectator);
    }
}

/// <summary>Client → server: the race data is loaded and matches <see cref="SetupHash"/>; the seat may start.</summary>
public sealed record NetReady(int RaceId, uint Epoch, byte[] SetupHash)
{
    public byte[] Encode()
    {
        var w = new WireWriter(64);
        new NetHeader(NetMessageType.Ready, RaceId, Epoch).Write(w);
        w.Bytes(SetupHash);
        return w.ToArray();
    }

    public static NetReady Decode(NetHeader h, ref WireReader r)
    {
        var x = new NetReady(h.RaceId, h.Epoch, r.Bytes(32).ToArray());
        r.End();
        return x;
    }
}

/// <summary>Server → client (reliable): common start. Host step <see cref="FirstStep"/> runs <see cref="LeadMicros"/> after the
/// server sent this message; afterwards the server clock alone advances the race.</summary>
public sealed record NetStart(int RaceId, uint Epoch, long FirstStep, long LeadMicros)
{
    public byte[] Encode()
    {
        var w = new WireWriter(32);
        new NetHeader(NetMessageType.Start, RaceId, Epoch).Write(w);
        w.VarU(FirstStep);
        w.VarU(LeadMicros);
        return w.ToArray();
    }

    public static NetStart Decode(NetHeader h, ref WireReader r)
    {
        var x = new NetStart(h.RaceId, h.Epoch, r.VarU(int.MaxValue), r.VarU(60_000_000));
        r.End();
        return x;
    }
}

/// <summary>Continuous controls (steer, gas, brake, handbrake/horn). TargetStep 0 means the server applies the newest
/// sample at its next step; a positive target selects that host step. Gear and reset travel as <see cref="NetAction"/>s.</summary>
public readonly record struct NetInputSample(long Sequence, long TargetStep, sbyte Steer, byte Gas, byte Brake, byte Held)
{
    /// <summary>Button bits a sample may hold (handbrake, horn); everything else is an action.</summary>
    public const byte HeldMask = PlayerInput.BtnHandbrake | PlayerInput.BtnHorn;

    public PlayerInput ToInput(byte actionButtons = 0) => new(Steer, Gas, Brake, (byte)(Held | actionButtons));
}

public readonly record struct NetAction(long Id, NetActionKind Kind, long TargetStep)
{
    public byte Button => Kind switch
    {
        NetActionKind.GearUp => PlayerInput.BtnGearUp,
        NetActionKind.GearDown => PlayerInput.BtnGearDown,
        _ => PlayerInput.BtnReset,
    };
}

/// <summary>Client → server (unreliable): either the newest target-step samples (consecutive sequences, increasing targets)
/// or a single target-zero latest-wins continuous-controls sample, plus unacknowledged one-shot actions.</summary>
public sealed record NetInputPacket(int RaceId, uint Epoch, long AckSnapshot, IReadOnlyList<NetInputSample> Samples, IReadOnlyList<NetAction> Actions)
{
    /// <summary>A single sample with TargetStep=0 is a realtime latest-wins controls update.</summary>
    public bool IsRealtimeControls => Samples.Count == 1 && Samples[0].TargetStep == 0;

    public byte[] Encode()
    {
        if (Samples.Count is 0 or > NetProtocol.InputRedundancy || Actions.Count > NetProtocol.MaxActionsPerPacket)
            throw new ArgumentException("sample/action count");
        if (Samples.Any(s => s.TargetStep == 0) && !IsRealtimeControls)
            throw new ArgumentException("realtime controls must be a single target-zero sample");
        var w = new WireWriter(NetProtocol.MaxDatagram);
        new NetHeader(NetMessageType.Input, RaceId, Epoch).Write(w);
        w.VarU(AckSnapshot);
        w.U8(Samples.Count);
        w.VarU(Samples[0].Sequence);
        w.VarU(Samples[0].TargetStep);
        for (int i = 0; i < Samples.Count; i++)
        {
            var s = Samples[i];
            if (i > 0)
            {
                if (s.Sequence != Samples[0].Sequence + i || s.TargetStep <= Samples[i - 1].TargetStep) throw new ArgumentException("samples not consecutive");
                w.VarU(s.TargetStep - Samples[i - 1].TargetStep);
            }
            w.I8(s.Steer);
            w.U8(s.Gas);
            w.U8(s.Brake);
            w.U8(s.Held);
        }
        w.U8(Actions.Count);
        foreach (var a in Actions)
        {
            w.VarU(a.Id);
            w.U8((byte)a.Kind);
            w.VarU(a.TargetStep);
        }
        return w.ToArray();
    }

    public static NetInputPacket Decode(NetHeader h, ref WireReader r)
    {
        long ack = r.VarU(int.MaxValue);
        int n = r.U8();
        if (n is 0 or > NetProtocol.InputRedundancy) throw new NetProtocolException("bad sample count");
        long seq = r.VarU(long.MaxValue / 2), target = r.VarU(int.MaxValue);
        var samples = new NetInputSample[n];
        if (target == 0 && n != 1) throw new NetProtocolException("realtime sample must be alone");
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                long d = r.VarU(NetProtocol.MaxFutureSteps * 4L);
                if (d == 0) throw new NetProtocolException("non-increasing target");
                target += d;
            }
            sbyte steer = r.I8();
            byte gas = r.U8(), brake = r.U8(), held = r.U8();
            if ((held & ~NetInputSample.HeldMask) != 0) throw new NetProtocolException("one-shot button in a held sample");
            samples[i] = new NetInputSample(seq + i, target, steer, gas, brake, held);
        }
        int m = r.U8();
        if (m > NetProtocol.MaxActionsPerPacket) throw new NetProtocolException("too many actions");
        var actions = new NetAction[m];
        for (int i = 0; i < m; i++)
        {
            long id = r.VarU(long.MaxValue / 2);
            byte kind = r.U8();
            if (kind is < (byte)NetActionKind.GearUp or > (byte)NetActionKind.Reset) throw new NetProtocolException("bad action");
            actions[i] = new NetAction(id, (NetActionKind)kind, r.VarU(int.MaxValue));
        }
        r.End();
        return new NetInputPacket(h.RaceId, h.Epoch, ack, samples, actions);
    }
}

/// <summary>Server → client (reliable): a seat left the race; after the start its car is DNF (neutral input, aborted result).</summary>
public sealed record NetPlayerLeft(int RaceId, uint Epoch, int Seat, NetRejectReason Reason, long Step)
{
    public byte[] Encode()
    {
        var w = new WireWriter(32);
        new NetHeader(NetMessageType.PlayerLeft, RaceId, Epoch).Write(w);
        w.U8(Seat);
        w.U8((byte)Reason);
        w.VarU(Step);
        return w.ToArray();
    }

    public static NetPlayerLeft Decode(NetHeader h, ref WireReader r)
    {
        var x = new NetPlayerLeft(h.RaceId, h.Epoch, r.U8(), (NetRejectReason)r.U8(), r.VarU(int.MaxValue));
        r.End();
        return x;
    }
}

/// <summary>Server → client (reliable): final result, sent exactly once per connection. <see cref="DnfSeats"/> = bit per seat
/// that left the race before the result.</summary>
public sealed record NetResults(int RaceId, uint Epoch, NetFinishReason Reason, long Step, int SimulationTick64, int DnfSeats, RaceResults Results)
{
    const int MaxText = 24;

    public byte[] Encode()
    {
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        new NetHeader(NetMessageType.Results, RaceId, Epoch).Write(w);
        w.U8((byte)Reason);
        w.VarU(Step);
        w.VarI(SimulationTick64);
        w.U8(DnfSeats);
        w.U8(Results.Racers.Count);
        foreach (var x in Results.Racers)
        {
            w.VarI(x.Place);
            w.U8(x.CarIndex);
            w.Ascii(x.Model, MaxText);
            w.Bool(x.Human);
            w.Bool(x.Estimated);
            w.VarI(x.RaceTicks);
            w.U8(x.LapTicks.Count);
            foreach (var t in x.LapTicks) w.VarI(t);
            w.Bool(x.BestLapTicks.HasValue);
            if (x.BestLapTicks is { } b) w.VarI(b);
            w.VarI(x.RaceState);
            w.Ascii(x.Driver, MaxText);
            w.VarI(x.TopSpeedFx);
            w.U8(Math.Clamp(x.CarType, 0, NetProtocol.MaxCarNumber));
        }
        return w.ToArray();
    }

    static int CarType(ref WireReader r)
    {
        int t = r.U8();
        if (t > NetProtocol.MaxCarNumber) throw new NetProtocolException("bad car type");
        return t;
    }

    public static NetResults Decode(NetHeader h, ref WireReader r)
    {
        byte reason = r.U8();
        if (reason is < (byte)NetFinishReason.Finished or > (byte)NetFinishReason.StragglerLimit) throw new NetProtocolException("bad finish reason");
        long step = r.VarU(int.MaxValue);
        int tick = r.VarI32();
        int dnf = r.U8();
        int n = r.U8();
        if (n > NetProtocol.MaxCars) throw new NetProtocolException("too many racers");
        var rows = new RacerResult[n];
        for (int i = 0; i < n; i++)
        {
            int place = r.VarI32(), car = r.U8();
            string model = r.Ascii(MaxText);
            bool human = r.Bool(), est = r.Bool();
            int ticks = r.VarI32();
            int laps = r.U8();
            if (laps > NetProtocol.MaxLapTimes) throw new NetProtocolException("too many laps");
            var lapTicks = new int[laps];
            for (int j = 0; j < laps; j++) lapTicks[j] = r.VarI32();
            int? best = r.Bool() ? r.VarI32() : null;
            int state = r.VarI32();
            string driver = r.Ascii(MaxText);
            rows[i] = new RacerResult(place, car, model, human, est, ticks, Array.AsReadOnly(lapTicks), best, state, driver, r.VarI32(), CarType(ref r));
        }
        r.End();
        return new NetResults(h.RaceId, h.Epoch, (NetFinishReason)reason, step, tick, dnf, new RaceResults(Array.AsReadOnly(rows)));
    }
}

/// <summary>Client → server (reliable): the player leaves (counts as a disconnect: DNF after the start).</summary>
public static class NetLeave
{
    public static byte[] Encode(int raceId, uint epoch)
    {
        var w = new WireWriter(NetHeader.Size);
        new NetHeader(NetMessageType.Leave, raceId, epoch).Write(w);
        return w.ToArray();
    }
}

/// <summary>Client → server (reliable): the result of this round was received. A graceful stop waits for every connected
/// client to acknowledge before it disconnects, so the <see cref="NetResults"/> message is never dropped by the close.</summary>
public static class NetResultsAck
{
    public static byte[] Encode(int raceId, uint epoch)
    {
        var w = new WireWriter(NetHeader.Size);
        new NetHeader(NetMessageType.ResultsAck, raceId, epoch).Write(w);
        return w.ToArray();
    }
}
