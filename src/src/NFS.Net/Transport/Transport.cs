using NFS.Net.Protocol;

namespace NFS.Net.Transport;

/// <summary>Server side of a transport (LiteNetLib, or the in-memory <see cref="SimNetwork"/> of the tests). Sending never blocks
/// the caller; a transport queues internally and reports its reliable backlog.</summary>
public interface INetServerTransport
{
    void Send(int connection, NetChannel channel, byte[] data);
    /// <summary>Closes a connection; <paramref name="reason"/> reaches the peer as disconnect data.</summary>
    void Disconnect(int connection, byte[] reason);
    /// <summary>Reliable packets queued for the connection and not yet acknowledged (backpressure metric).</summary>
    int PendingReliable(int connection);
}

/// <summary>Callbacks of the server logic. The transport calls them from the thread that owns the race (poll loop), never in
/// parallel with <c>RaceServer.Update</c>.</summary>
public interface INetServerHandler
{
    /// <summary>Validates a connection request; null = accept (then <see cref="OnConnected"/> follows at once), otherwise the reject data.</summary>
    byte[]? OnConnectRequest(ReadOnlySpan<byte> hello);
    void OnConnected(int connection, ReadOnlySpan<byte> hello);
    void OnReceive(int connection, NetChannel channel, ReadOnlySpan<byte> data);
    void OnDisconnected(int connection);
}

public interface INetClientTransport
{
    void Send(NetChannel channel, byte[] data);
    void Disconnect();
    /// <summary>Current round-trip estimate of the connection (microseconds).</summary>
    long RoundTripMicros { get; }
}

public interface INetClientHandler
{
    void OnConnected();
    void OnReceive(NetChannel channel, ReadOnlySpan<byte> data);
    /// <summary><paramref name="reason"/> = reject/disconnect data of the server (null when the transport lost the peer).</summary>
    void OnDisconnected(byte[]? reason);
}
