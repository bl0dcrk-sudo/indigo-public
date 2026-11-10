using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Client;

/// <summary>
/// Client-side automatic return into a seat the server reserved for the reconnect grace window. The controller owns one
/// <see cref="RaceClient"/> at a time: the caller advances <see cref="Client"/> every frame and calls <see cref="Poll"/>. When
/// that client loses its connection (closed or rejected), the controller opens a fresh connection with the same resume token
/// and a fresh <see cref="RaceClient"/> (a new client never replays the old round's state, so no event or result is applied
/// twice). Retries are spaced by the retry interval and stop when the grace window runs out, when the connection ends with a
/// non-transient reason (a final server answer or a local <see cref="RaceClient.Leave"/>), or after <see cref="Stop"/>.
/// </summary>
public sealed class ReconnectController
{
    public const long DefaultRetryIntervalMicros = 250_000;

    readonly byte[] _fingerprint;
    readonly string _name;
    readonly byte[] _resumeToken;
    readonly long _graceMicros;
    readonly long _retryIntervalMicros;
    readonly Func<RaceClient, INetClientTransport> _open;
    readonly Func<long> _clock;
    readonly Action<string>? _log;
    long _lastAttempt = long.MinValue;

    /// <param name="open">Adds a client to the network, assigns <see cref="RaceClient.Transport"/> and starts the connection
    /// with <see cref="RaceClient.Hello"/> (which already carries the resume token); returns that transport.</param>
    /// <param name="clock">Monotonic microseconds, stored in the created clients (their message clock).</param>
    public ReconnectController(byte[] dataFingerprint, string name, byte[] resumeToken, long graceMicros,
        Func<RaceClient, INetClientTransport> open, Func<long> clock,
        long retryIntervalMicros = DefaultRetryIntervalMicros, Action<string>? log = null)
    {
        if (dataFingerprint.Length != 32) throw new ArgumentException("data fingerprint must be 32 bytes", nameof(dataFingerprint));
        if (resumeToken.Length != NetProtocol.ResumeTokenLength)
            throw new ArgumentException($"resume token must be {NetProtocol.ResumeTokenLength} bytes", nameof(resumeToken));
        if (retryIntervalMicros <= 0) throw new ArgumentOutOfRangeException(nameof(retryIntervalMicros), "retry interval must be positive");
        _fingerprint = dataFingerprint;
        _name = name;
        _resumeToken = (byte[])resumeToken.Clone();
        _graceMicros = Math.Max(0, graceMicros);
        _retryIntervalMicros = retryIntervalMicros;
        _open = open;
        _clock = clock;
        _log = log;
    }

    /// <summary>The client the caller must advance (the newest connection attempt).</summary>
    public RaceClient Client { get; private set; } = null!;
    /// <summary>Connections opened, including the first one.</summary>
    public int Attempts { get; private set; }
    /// <summary>The grace window ran out without a resume: no further attempt is made.</summary>
    public bool Expired { get; private set; }
    /// <summary>The controller stopped (final rejection or <see cref="Stop"/>); no further attempt is made.</summary>
    public bool Stopped { get; private set; }
    /// <summary>When the current loss was noticed; null while connected.</summary>
    public long? LostAtMicros { get; private set; }
    /// <summary>The current client accepted a resume welcome.</summary>
    public bool Resumed => Client?.Welcome?.Resume == true;

    /// <summary>Opens the first connection (with the resume token, if the seat is still reserved).</summary>
    public RaceClient Start()
    {
        if (Client != null) throw new InvalidOperationException("the controller was already started");
        Client = Connect();
        return Client;
    }

    /// <summary>Stops automatic reconnects (the caller leaves or closes the game).</summary>
    public void Stop() => Stopped = true;

    /// <summary>Watches <see cref="Client"/> and opens a new connection when it is closed or rejected. Returns the client to
    /// advance after this call (the same one, or the new attempt). The grace window runs from the first noticed loss until a
    /// client gets a welcome again; attempts still connecting do not restart it.</summary>
    public RaceClient Poll(long now)
    {
        if (Client == null) throw new InvalidOperationException("the controller was not started");
        if (Stopped || Expired) return Client;
        bool lost = Client.State is RaceClientState.Closed or RaceClientState.Rejected;
        if (!lost && (Client.Welcome != null || LostAtMicros == null))
        {
            LostAtMicros = null;
            return Client;
        }
        if (lost && !(Client.RejectReason is { } reject && Retryable(reject.Reason)))
        {
            // A local Leave/close (no reason) or a final server answer (bad/expired token, race over, shutdown, slow client):
            // retrying cannot or must not succeed.
            Stopped = true;
            Log($"connection ended ({Client.RejectReason?.Reason.ToString() ?? "local close"}); giving up");
            return Client;
        }
        LostAtMicros ??= now;
        if (now - LostAtMicros.Value >= _graceMicros)
        {
            Expired = true;
            if (!lost) Client.Transport?.Disconnect();
            Log($"grace window ({_graceMicros / 1000} ms) expired; giving up");
            return Client;
        }
        if (!lost) return Client;   // the current attempt is still connecting
        if (_lastAttempt != long.MinValue && now - _lastAttempt < _retryIntervalMicros) return Client;
        _lastAttempt = now;
        Client = Connect();
        return Client;
    }

    /// <summary>A loss detected by the transport (no server answer), a name still held by the old connection and an explicit
    /// leave of the old connection seen by the server are transient: another attempt inside the grace window may succeed.</summary>
    static bool Retryable(NetRejectReason reason) =>
        reason is NetRejectReason.NameTaken or NetRejectReason.Timeout or NetRejectReason.Left;

    /// <summary>Data mods sent in the hello (D9).</summary>
    public NetModSet Mods { get; init; } = NetModSet.Empty;

    RaceClient Connect()
    {
        Attempts++;
        var client = new RaceClient(null, _fingerprint, _name, log: _log)
        {
            ResumeToken = _resumeToken,
            Mods = Mods,
            Clock = _clock,
        };
        var transport = _open(client);
        client.Transport = transport;
        Log($"connection attempt {Attempts}");
        return client;
    }

    void Log(string message) => _log?.Invoke(message);
}
