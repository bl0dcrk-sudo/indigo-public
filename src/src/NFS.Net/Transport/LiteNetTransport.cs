using System.Net;
using LiteNetLib;
using NFS.Net.Protocol;

namespace NFS.Net.Transport;

/// <summary>Mapping of the protocol channels to LiteNetLib 2.1.4 delivery methods (LiteNetManager: one channel per method).</summary>
public static class LiteNetChannels
{
    public const int DisconnectTimeoutMs = 5000;

    public static DeliveryMethod ToDelivery(NetChannel c) => c switch
    {
        NetChannel.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        NetChannel.ReliableUnordered => DeliveryMethod.ReliableUnordered,
        NetChannel.Sequenced => DeliveryMethod.Sequenced,
        _ => DeliveryMethod.Unreliable,
    };

    public static NetChannel FromDelivery(DeliveryMethod d) => d switch
    {
        DeliveryMethod.ReliableOrdered or DeliveryMethod.ReliableSequenced => NetChannel.ReliableOrdered,
        DeliveryMethod.ReliableUnordered => NetChannel.ReliableUnordered,
        DeliveryMethod.Sequenced => NetChannel.Sequenced,
        _ => NetChannel.Unreliable,
    };

    internal static LiteNetManager Manager(EventBasedLiteNetListener listener) => new(listener, null)
    {
        AutoRecycle = true, UnsyncedEvents = false, IPv6Enabled = false, DisconnectTimeout = DisconnectTimeoutMs,
    };
}

/// <summary>
/// UDP server endpoint on LiteNetLib (<see cref="LiteNetManager"/>; events are delivered only by <see cref="Poll"/> on the caller's
/// thread, so the race keeps one mutation owner). Binds the given address only (smoke runs bind 127.0.0.1).
/// </summary>
public sealed class LiteNetServerHost : INetServerTransport, IDisposable
{
    readonly EventBasedLiteNetListener _listener = new();
    readonly LiteNetManager _manager;
    readonly Dictionary<int, LiteNetPeer> _peers = [];

    public INetServerHandler? Handler { get; set; }
    public int Port => _manager.LocalPort;
    public long SendErrors { get; private set; }

    public LiteNetServerHost()
    {
        _manager = LiteNetChannels.Manager(_listener);
        _listener.ConnectionRequestEvent += request =>
        {
            var handler = Handler ?? throw new InvalidOperationException("no handler");
            var hello = request.Data.GetRemainingBytes();
            var reject = handler.OnConnectRequest(hello);
            if (reject != null) { request.Reject(reject); return; }
            var peer = request.Accept();
            _peers[peer.Id] = peer;
            handler.OnConnected(peer.Id, hello);
        };
        _listener.NetworkReceiveEvent += (peer, reader, method) =>
        {
            if (_peers.ContainsKey(peer.Id)) Handler?.OnReceive(peer.Id, LiteNetChannels.FromDelivery(method), reader.GetRemainingBytesSpan());
        };
        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (_peers.Remove(peer.Id)) Handler?.OnDisconnected(peer.Id);
        };
    }

    public void Start(IPAddress bind, int port)
    {
        if (!_manager.Start(bind, IPAddress.IPv6Loopback, port)) throw new InvalidOperationException($"cannot bind {bind}:{port}");
    }

    public void Poll() => _manager.PollEvents();

    public void Send(int connection, NetChannel channel, byte[] data)
    {
        if (!_peers.TryGetValue(connection, out var peer)) return;
        try { peer.Send(data, LiteNetChannels.ToDelivery(channel)); }
        catch (TooBigPacketException) { SendErrors++; }
    }

    public void Disconnect(int connection, byte[] reason)
    {
        if (_peers.Remove(connection, out var peer)) _manager.DisconnectPeer(peer, reason);
    }

    public int PendingReliable(int connection) =>
        _peers.TryGetValue(connection, out var p) ? p.GetPacketsCountInReliableQueue(true) + p.GetPacketsCountInReliableQueue(false) : 0;

    /// <summary>Largest unfragmented payload of a delivery method on a connection (MTU check of the snapshot budget).</summary>
    public int MaxSinglePacket(int connection, NetChannel channel) =>
        _peers.TryGetValue(connection, out var p) ? p.GetMaxSinglePacketSize(LiteNetChannels.ToDelivery(channel)) : 0;

    public void Dispose() => _manager.Stop(true);
}

/// <summary>UDP client endpoint on LiteNetLib (events only from <see cref="Poll"/>).</summary>
public sealed class LiteNetClientHost : INetClientTransport, IDisposable
{
    readonly EventBasedLiteNetListener _listener = new();
    readonly LiteNetManager _manager;
    LiteNetPeer? _peer;

    public LiteNetClientHost(INetClientHandler handler)
    {
        _manager = LiteNetChannels.Manager(_listener);
        _listener.PeerConnectedEvent += _ => handler.OnConnected();
        _listener.NetworkReceiveEvent += (_, reader, method) => handler.OnReceive(LiteNetChannels.FromDelivery(method), reader.GetRemainingBytesSpan());
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            var data = info.AdditionalData?.GetRemainingBytes();
            _peer = null;
            handler.OnDisconnected(data is { Length: > 0 } ? data : null);
        };
    }

    /// <summary>Starts the client socket (loopback-only when the server is on loopback) and sends the hello as connection data.</summary>
    public void Connect(IPEndPoint server, byte[] hello)
    {
        var bind = IPAddress.IsLoopback(server.Address) ? IPAddress.Loopback : IPAddress.Any;
        if (!_manager.Start(bind, IPAddress.IPv6Loopback, 0)) throw new InvalidOperationException("cannot start the client socket");
        _peer = _manager.Connect(server, hello);
    }

    public void Poll() => _manager.PollEvents();

    public void Send(NetChannel channel, byte[] data) => _peer?.Send(data, LiteNetChannels.ToDelivery(channel));

    public void Disconnect()
    {
        if (_peer != null) _manager.DisconnectPeer(_peer);
    }

    public long RoundTripMicros => (_peer?.RoundTripTime ?? 0) * 1000L;
    public int Mtu => _peer?.Mtu ?? 0;
    public int MaxSinglePacket(NetChannel channel) => _peer?.GetMaxSinglePacketSize(LiteNetChannels.ToDelivery(channel)) ?? 0;

    public void Dispose() => _manager.Stop(true);
}
