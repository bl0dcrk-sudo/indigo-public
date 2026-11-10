using NFS.Net.Protocol;

namespace NFS.Net.Transport;

/// <summary>One direction of a simulated link: base one-way latency, uniform jitter, loss and duplication probability, optional
/// bandwidth (bytes/s, 0 = unlimited). Jitter reorders unreliable packets.</summary>
public sealed record LinkProfile(int LatencyMs = 0, int JitterMs = 0, double Loss = 0, double Duplicate = 0, int BandwidthBytesPerSecond = 0)
{
    public static LinkProfile Rtt(int rttMs, int jitterMs = 0, double loss = 0, double duplicate = 0) => new(rttMs / 2, jitterMs, loss, duplicate);
}

/// <summary>
/// Deterministic in-memory network with a fake clock (microseconds) for tests without sockets or windows. Channel semantics follow
/// LiteNetLib's DeliveryMethod: Unreliable may be lost, duplicated and reordered; Sequenced may be lost but never duplicated and an
/// older packet than the newest delivered one is dropped; ReliableOrdered/Unordered are never lost (a lost attempt costs a
/// retransmission delay) and ordered/unordered. Disconnects are delivered after the link latency; a cut link is detected after
/// <see cref="DisconnectTimeoutMicros"/> like a transport timeout.
/// </summary>
public sealed class SimNetwork
{
    public long DisconnectTimeoutMicros { get; set; } = 5_000_000;
    /// <summary>Unreliable/sequenced packets that would wait longer than this in a bandwidth queue are dropped (router queue).</summary>
    public long MaxQueueMicros { get; set; } = 1_000_000;

    readonly Random _rng;
    readonly PriorityQueue<Action, (long Time, long Order)> _queue = new();
    long _order;
    readonly Dictionary<int, SimClient> _byConnection = [];
    int _nextConnection = 1;

    public long Now { get; private set; }
    public INetServerHandler? Server { get; set; }
    public SimServerTransport ServerTransport { get; }
    public long Delivered { get; private set; }
    public long Lost { get; private set; }
    public long Duplicated { get; private set; }

    public SimNetwork(int seed)
    {
        _rng = new Random(seed);
        ServerTransport = new SimServerTransport(this);
    }

    public SimClient AddClient(INetClientHandler handler, LinkProfile up, LinkProfile down) => new(this, handler, up, down);

    /// <summary>Delivers everything due up to <paramref name="time"/> in time order, then sets the clock.</summary>
    public void AdvanceTo(long time)
    {
        while (_queue.TryPeek(out _, out var key) && key.Time <= time)
        {
            var action = _queue.Dequeue();
            Now = key.Time;
            action();
        }
        Now = Math.Max(Now, time);
    }

    void At(long time, Action action) => _queue.Enqueue(action, (Math.Max(time, Now), _order++));

    double Next() => _rng.NextDouble();

    long Delay(LinkProfile p) => (p.LatencyMs * 1000L) + (p.JitterMs > 0 ? (long)(Next() * p.JitterMs * 1000) : 0);

    /// <summary>Sends one packet over <paramref name="link"/>; <paramref name="deliver"/> runs at arrival.</summary>
    void Transmit(Direction dir, NetChannel channel, int size, Action deliver)
    {
        var p = dir.Profile;
        long start = Now;
        if (p.BandwidthBytesPerSecond > 0)
        {
            long serialize = size * 1_000_000L / p.BandwidthBytesPerSecond;
            start = Math.Max(Now, dir.BusyUntil);
            bool reliable = channel is NetChannel.ReliableOrdered or NetChannel.ReliableUnordered;
            if (!reliable && start - Now > MaxQueueMicros) { Lost++; return; }
            dir.BusyUntil = start + serialize;
            start += serialize;
        }
        switch (channel)
        {
            case NetChannel.Unreliable:
            case NetChannel.Sequenced:
            {
                if (Next() < p.Loss) { Lost++; return; }
                long seq = ++dir.SendSequence[(int)channel];
                int copies = channel == NetChannel.Unreliable && Next() < p.Duplicate ? 2 : 1;
                if (copies == 2) Duplicated++;
                for (int i = 0; i < copies; i++)
                    At(start + Delay(p), () =>
                    {
                        if (dir.Cut) return;
                        if (channel == NetChannel.Sequenced)
                        {
                            if (seq <= dir.LastSequenced) return;
                            dir.LastSequenced = seq;
                        }
                        Delivered++;
                        deliver();
                    });
                break;
            }
            default:
            {
                long t = start + Delay(p);
                int attempts = 0;
                while (Next() < p.Loss && attempts++ < 20) t += Math.Max(2 * p.LatencyMs * 1000L + p.JitterMs * 1000L, 30_000);
                if (channel == NetChannel.ReliableOrdered)
                {
                    t = Math.Max(t, dir.LastOrdered);
                    dir.LastOrdered = t;
                }
                dir.InFlight++;
                At(t, () =>
                {
                    dir.InFlight--;
                    if (dir.Cut) return;
                    Delivered++;
                    deliver();
                });
                break;
            }
        }
    }

    internal sealed class Direction(LinkProfile profile)
    {
        public LinkProfile Profile = profile;
        public long BusyUntil, LastSequenced, LastOrdered;
        public readonly long[] SendSequence = new long[4];
        public int InFlight;
        public bool Cut;
    }

    /// <summary>One simulated client endpoint (its connection to the server).</summary>
    public sealed class SimClient : INetClientTransport
    {
        readonly SimNetwork _net;
        readonly INetClientHandler _handler;
        internal readonly Direction Up, Down;
        bool _open, _closed;

        public int Connection { get; private set; }
        public bool Connected => _open && !_closed;

        internal SimClient(SimNetwork net, INetClientHandler handler, LinkProfile up, LinkProfile down)
        {
            _net = net;
            _handler = handler;
            Up = new Direction(up);
            Down = new Direction(down);
        }

        public long RoundTripMicros => (Up.Profile.LatencyMs + Down.Profile.LatencyMs) * 1000L + (Up.Profile.JitterMs + Down.Profile.JitterMs) * 500L;

        /// <summary>Changes the link quality while connected (e.g. a client becoming slow).</summary>
        public void SetProfiles(LinkProfile up, LinkProfile down)
        {
            Up.Profile = up;
            Down.Profile = down;
        }

        public void Connect(byte[] hello)
        {
            _net.At(_net.Now + _net.Delay(Up.Profile), () =>
            {
                var server = _net.Server ?? throw new InvalidOperationException("no server");
                var reject = server.OnConnectRequest(hello);
                if (reject != null)
                {
                    _closed = true;
                    _net.At(_net.Now + _net.Delay(Down.Profile), () => _handler.OnDisconnected(reject));
                    return;
                }
                Connection = _net._nextConnection++;
                _net._byConnection[Connection] = this;
                _open = true;
                server.OnConnected(Connection, hello);
                _net.At(_net.Now + _net.Delay(Down.Profile), () => { if (!_closed) _handler.OnConnected(); });
            });
        }

        public void Send(NetChannel channel, byte[] data)
        {
            if (!Connected) return;
            var copy = (byte[])data.Clone();
            _net.Transmit(Up, channel, copy.Length, () =>
            {
                if (_open && !_closed) _net.Server!.OnReceive(Connection, channel, copy);
            });
        }

        internal void Receive(NetChannel channel, byte[] data)
        {
            var copy = (byte[])data.Clone();
            _net.Transmit(Down, channel, copy.Length, () => { if (!_closed) _handler.OnReceive(channel, copy); });
        }

        /// <summary>Client-initiated disconnect (graceful): the server learns it after the uplink latency.</summary>
        public void Disconnect()
        {
            if (_closed) return;
            _closed = true;
            if (!_open) return;
            _net.At(_net.Now + _net.Delay(Up.Profile), () => _net.Server!.OnDisconnected(Connection));
        }

        /// <summary>The link dies silently (crash, cable): both sides detect it only by the transport timeout.</summary>
        public void Cut()
        {
            if (_closed) return;
            Up.Cut = Down.Cut = true;
            _closed = true;
            _net.At(_net.Now + _net.DisconnectTimeoutMicros, () =>
            {
                if (_open) _net.Server!.OnDisconnected(Connection);
                _handler.OnDisconnected(null);
            });
        }

        internal void ServerDisconnect(byte[] reason)
        {
            if (_closed) return;
            _closed = true;
            var copy = (byte[])reason.Clone();
            _net.At(_net.Now + _net.Delay(Down.Profile), () => _handler.OnDisconnected(copy));
        }
    }

    public sealed class SimServerTransport(SimNetwork net) : INetServerTransport
    {
        public void Send(int connection, NetChannel channel, byte[] data)
        {
            if (net._byConnection.TryGetValue(connection, out var c) && c.Connected) c.Receive(channel, data);
        }

        public void Disconnect(int connection, byte[] reason)
        {
            if (net._byConnection.Remove(connection, out var c)) c.ServerDisconnect(reason);
        }

        public int PendingReliable(int connection) => net._byConnection.TryGetValue(connection, out var c) ? c.Down.InFlight : 0;
    }
}
