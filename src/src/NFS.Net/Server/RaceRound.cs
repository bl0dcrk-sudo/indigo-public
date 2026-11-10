using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Server;

/// <summary>Transport endpoint of one round member. The round never sees a transport connection id: it sends, measures the
/// backlog and closes through the owner's endpoint, which maps the member to its current connection.</summary>
public interface IRaceEndpoint
{
    void Send(NetChannel channel, byte[] data);
    void Disconnect(byte[] reason);
    int PendingReliable { get; }
}

/// <summary>One participant of a <see cref="RaceRound"/>. The owner (session game or the compatibility <see cref="RaceServer"/>)
/// holds the member and routes incoming traffic to it; the member itself is only an identity inside the round.</summary>
public sealed class RaceMember
{
    internal RaceMember(int slot, IRaceEndpoint endpoint)
    {
        Slot = slot;
        Endpoint = endpoint;
    }

    /// <summary>Round-local id (never a transport connection id); used only inside <see cref="RaceRound"/>.</summary>
    internal int Slot { get; }
    internal IRaceEndpoint Endpoint { get; }
}

/// <summary>
/// Connection-independent race round: owns one <see cref="RaceHost"/>, composes the welcome/baseline, schedules the 32 Hz
/// steps and encodes snapshots/results exactly like the original race server. Members are added through
/// <see cref="TryAddMember"/> and every outgoing message goes to that member's <see cref="IRaceEndpoint"/>; the round has no
/// socket, no transport connection ids and no knowledge of the session layer. <see cref="RaceServer"/> is the legacy
/// INetServerHandler adapter over this class.
/// </summary>
public sealed class RaceRound : IDisposable
{
    readonly RaceServer _server;
    readonly MemberTransport _transport;
    readonly Dictionary<int, RaceMember> _members = [];
    int _nextSlot = 1;

    public RaceRound(RaceHost host, byte[] dataFingerprint, long now, RaceServerOptions? options = null,
        Action<string>? log = null, int seed = 0, Func<long>? clock = null)
    {
        _transport = new MemberTransport(this);
        _server = new RaceServer(host, dataFingerprint, _transport, now, options, log, seed) { Clock = clock };
    }

    /// <summary>Race host currently owned by the round (the caller must not step it).</summary>
    public RaceHost Host => _server.Host;
    public NetRacePhase Phase => _server.Phase;
    public int RaceId => _server.RaceId;
    public RaceServerStats Stats => _server.Stats;
    public NetFinishReason? FinishReason => _server.FinishReason;
    /// <summary>The result as the clients received it (null while the round runs); see <see cref="RaceServer.FinalResults"/>.</summary>
    public RaceResults? FinalResults => _server.FinalResults;
    /// <summary>Race car indices of humans ranked last because they were gone from the round unfinished.</summary>
    public IReadOnlyList<int> ForcedLastCars => _server.ForcedLastCars;
    /// <summary>Straggler countdown in milliseconds (0 = not running); see <see cref="RaceServer.CutoffRemainingMillis"/>.</summary>
    public int CutoffRemainingMillis => _server.CutoffRemainingMillis;
    public bool Stopping => _server.Stopping;
    /// <summary>The result exists: the round will not step again and can be replaced by the owner.</summary>
    public bool Finished => _server.FinishReason != null;
    public int MemberCount => _members.Count;
    /// <summary>When set (tests/diagnostics), every event the host published is kept here.</summary>
    public List<RaceEvent>? PublishedEvents
    {
        get => _server.PublishedEvents;
        set => _server.PublishedEvents = value;
    }

    /// <summary>Checks whether a hello would be accepted without adding a member (version/data/phase checks of the race).</summary>
    public bool CanAddMember(NetHello hello, out NetRejectReason reason)
    {
        byte[]? reject = _server.OnConnectRequest(hello.Encode());
        reason = reject == null ? NetRejectReason.None : (NetReject.TryDecode(reject)?.Reason ?? NetRejectReason.Malformed);
        return reject == null;
    }

    /// <summary>Adds a human participant and sends its welcome, baseline and roster reliably. Returns null when the race
    /// itself refuses the hello (wrong version/data, race in progress or full); <paramref name="reason"/> then carries the
    /// same reason a reject message would.</summary>
    public RaceMember? TryAddMember(IRaceEndpoint endpoint, NetHello hello, out NetRejectReason reason)
    {
        if (!CanAddMember(hello, out reason)) return null;
        int slot;
        do slot = _nextSlot++; while (slot == 0 || _transport.Has(slot));
        var member = new RaceMember(slot, endpoint);
        _members[slot] = member;
        _transport.Bind(slot, endpoint);
        try { _server.OnConnected(slot, hello.Encode()); }
        catch
        {
            _members.Remove(slot);
            _transport.Unbind(slot);
            throw;
        }
        return member;
    }

    /// <summary>Adds a spectator (an eliminated knockout player): it gets the welcome (flagged spectator), baseline, roster and the whole
    /// stream of the round, but no seat, and nothing it does or loses changes the round. Allowed until the countdown starts; null when the
    /// round cannot take it (<paramref name="reason"/>).</summary>
    public RaceMember? TryAddSpectator(IRaceEndpoint endpoint, NetHello hello, out NetRejectReason reason)
    {
        if (!_server.CanAddSpectator(out reason)) return null;
        int slot;
        do slot = _nextSlot++; while (slot == 0 || _transport.Has(slot));
        var member = new RaceMember(slot, endpoint);
        _members[slot] = member;
        _transport.Bind(slot, endpoint);
        try { _server.OnConnectedSpectator(slot, hello.Encode()); }
        catch
        {
            _members.Remove(slot);
            _transport.Unbind(slot);
            throw;
        }
        return member;
    }

    /// <summary>Routes one message of <paramref name="member"/> to the race core (the core validates the race id and epoch).</summary>
    public void Receive(RaceMember member, NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (!_members.ContainsKey(member.Slot)) return;
        _server.OnReceive(member.Slot, channel, data);
    }

    /// <summary>Re-attaches a still bound member after a reconnect: the race core keeps the seat, issues a fresh epoch and
    /// sends a resume welcome, the current durable state and the fresh baseline. The member's endpoint already routes to the
    /// new connection.</summary>
    public bool TryResumeMember(RaceMember member, out NetRejectReason reason)
    {
        if (!_members.ContainsKey(member.Slot)) { reason = NetRejectReason.RaceClosed; return false; }
        return _server.TryResume(member.Slot, out reason);
    }

    /// <summary>The owner keeps the seat of a member whose connection was lost (<paramref name="away"/>) or hands it back.</summary>
    public void SetMemberAway(RaceMember member, bool away)
    {
        if (_members.ContainsKey(member.Slot)) _server.SetAway(member.Slot, away);
    }

    /// <summary>Reports that the member's connection ended. Before the countdown its seat is freed; afterwards the car is
    /// marked DNF and the race continues without waiting for it.</summary>
    public void RemoveMember(RaceMember member)
    {
        if (!_members.Remove(member.Slot)) return;
        _transport.Unbind(member.Slot);
        _server.OnDisconnected(member.Slot);
    }

    /// <summary>Advances the round by the monotonic server clock (starts when every member is ready, steps, finishes).</summary>
    public void Update(long now) => _server.Update(now);

    /// <summary>Graceful stop: an unfinished race gets the ServerStop result and a bounded drain follows in <see cref="Update"/>.</summary>
    public void Shutdown() => _server.Shutdown();

    public void Dispose()
    {
        _server.Dispose();
        _members.Clear();
        _transport.Clear();
    }

    sealed class MemberTransport(RaceRound round) : INetServerTransport
    {
        readonly Dictionary<int, IRaceEndpoint> _endpoints = [];

        public bool Has(int slot) => _endpoints.ContainsKey(slot);
        public void Bind(int slot, IRaceEndpoint endpoint) => _endpoints[slot] = endpoint;
        public void Unbind(int slot) => _endpoints.Remove(slot);
        public void Clear() => _endpoints.Clear();

        public void Send(int connection, NetChannel channel, byte[] data)
        {
            if (_endpoints.TryGetValue(connection, out var endpoint)) endpoint.Send(channel, data);
        }

        public void Disconnect(int connection, byte[] reason)
        {
            if (!_endpoints.Remove(connection, out var endpoint)) return;
            // The core has already forgotten the connection (Drop calls Leave first). Forget the member too, so a later
            // RemoveMember from the owner is a no-op; the endpoint then closes the real connection.
            round._members.Remove(connection);
            endpoint.Disconnect(reason);
        }

        public int PendingReliable(int connection) =>
            _endpoints.TryGetValue(connection, out var endpoint) ? endpoint.PendingReliable : 0;
    }
}
