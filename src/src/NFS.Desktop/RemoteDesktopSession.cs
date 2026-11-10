using System.Diagnostics;
using System.Net;
using NFS.Data;
using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Desktop;

/// <summary>Owns the Desktop UDP client of a network menu session: one <see cref="SessionClient"/> per connection and, for
/// every round (and every resume), a fresh <see cref="RemoteRaceProvider"/> over that round's <see cref="RaceClient"/>.
/// <para>It makes no decisions about games: connecting, creating, joining, readiness and starting are done by the user through
/// the network menu (<see cref="NetworkMenuController"/>, hosted by <see cref="NetworkMenuHost"/>) on <see cref="Session"/>. This
/// class only loads each round's scene on its welcome, builds the provider once the baseline arrived and drops it on
/// <see cref="ReleaseRound"/>. The session server is always an external process (a dedicated server or NFS.Server).</para></summary>
internal sealed class RemoteDesktopSession : IDisposable
{
    public const int DefaultRemotePresentationDelaySteps = 4;
    public const int LanPresentationDelaySteps = 2;
    public const int LoopbackPresentationDelaySteps = 2;
    public const long DefaultHandshakeTimeoutMicros = 20_000_000;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly IPEndPoint _endpoint;
    readonly SessionClient _session;
    readonly long _connectDeadline;
    LiteNetClientHost? _transport;
    RaceScene? _preparedScene;
    RaceClient? _providerClient, _releasedClient;
    bool _disposed;

    public SessionClient Session => _session;
    public RaceScene? Scene { get; private set; }
    public RemoteRaceProvider? Provider { get; private set; }
    /// <summary>RaceClient of the current round (null before the first welcome and after leaving the game).</summary>
    public RaceClient? Client => _session.Race;
    public string? Error { get; private set; }
    /// <summary>Acknowledges <see cref="Error"/> (the host showed it and carries on).</summary>
    public void ClearError() => Error = null;
    public bool Connected => Scene != null && Provider != null;
    /// <summary>The server has a round (or a resumed seat) whose client the presentation does not use yet.</summary>
    public bool HasNewRound => Provider != null && _session.Race is { } race && !ReferenceEquals(race, _providerClient);
    public long NowMicros => _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
    public int PresentationDelaySteps { get; }

    /// <summary>Names of the players of the current game (lobby members; the race roster while the lobby is unknown).</summary>
    public IReadOnlyList<string> PlayerNames => _session.Lobby is { } lobby
        ? lobby.Members.OrderBy(m => m.Slot).Select(m => m.Name).ToArray()
        : Client?.PlayerNames.OrderBy(p => p.Key).Select(p => p.Value).ToArray() ?? [];

    RemoteDesktopSession(string gameDir, string name, IPEndPoint endpoint, long handshakeTimeoutMicros, string? hudconDir)
    {
        PresentationDelaySteps = PresentationDelayFor(endpoint);
        _endpoint = endpoint;
        _session = new SessionClient(SimDataFingerprint.Compute(gameDir), name, Open, () => NowMicros,
            new SessionClientOptions { Mods = NetModSet.FromMods(NFS.Game.Mods.ModSession.Current.Mods), Cars = NFS.Net.Server.ServerLoop.ModCars(gameDir), PrepareRace = welcome => Prepare(gameDir, welcome, hudconDir) }, HostLog.Info);
        try { _session.Connect(); }
        catch { _transport?.Dispose(); throw; }
        _connectDeadline = NowMicros + handshakeTimeoutMicros;
    }

    /// <summary>Every connection attempt (the first and each reconnect) gets its own UDP socket and handler.</summary>
    INetClientTransport Open(INetClientHandler handler, byte[] hello)
    {
        _transport?.Dispose();
        var transport = new LiteNetClientHost(handler);
        _transport = transport;
        transport.Connect(_endpoint, hello);
        return transport;
    }

    bool Prepare(string gameDir, NetWelcome welcome, string? hudconDir)
    {
        try
        {
            var scene = LoadWelcomeScene(gameDir, welcome.SetupBlock, welcome.CarId, hudconDir);
            if (scene.Session.Cars.Count != welcome.CarCount || (scene.Session.Parked?.Count ?? 0) != welcome.ParkedCount)
                throw new InvalidDataException("Welcome car roster does not match the locally loaded frozen race setup.");
            _preparedScene = scene;
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Error = $"Remote race data could not be loaded: {e.Message}";
            HostLog.Error(Error);
            return false;
        }
    }

    internal static int PresentationDelayFor(IPEndPoint endpoint) => IPAddress.IsLoopback(endpoint.Address)
        ? LoopbackPresentationDelaySteps : IsPrivateNetworkAddress(endpoint.Address) ? LanPresentationDelaySteps : DefaultRemotePresentationDelaySteps;

    static bool IsPrivateNetworkAddress(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }

    /// <param name="hudconDir">User folder of the HUDCON.DAT copy (reads it for every net_mode != 1 race, the network included); null = none. [ref:1488]</param>
    internal static RaceScene LoadWelcomeScene(string gameDir, byte[] setupBlock, int carId, string? hudconDir = null)
    {
        var setup = new RaceSetup();
        setupBlock.CopyTo(setup.Block, 0);
        var session = new RaceLoader(gameDir).Load(setup);
        return new RaceScene(session, gameDir, carId, hudconDir);
    }

    /// <summary>Opens the connection to the session server at <paramref name="endpoint"/> (IP:port). Throws
    /// <see cref="ArgumentException"/> for a bad name or address, or the socket error of the first attempt.</summary>
    public static RemoteDesktopSession Connect(string gameDir, string endpoint, string name,
        long handshakeTimeoutMicros = DefaultHandshakeTimeoutMicros, string? hudconDir = null)
    {
        if (!IPEndPoint.TryParse(endpoint, out var parsed)) throw new ArgumentException("Address must be an IP:port endpoint.");
        return Connect(gameDir, parsed, name, handshakeTimeoutMicros, hudconDir);
    }

    public static RemoteDesktopSession Connect(string gameDir, IPEndPoint endpoint, string name,
        long handshakeTimeoutMicros = DefaultHandshakeTimeoutMicros, string? hudconDir = null) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Player name must not be empty.")
        : new RemoteDesktopSession(gameDir, name, endpoint, handshakeTimeoutMicros, hudconDir);

    public void Poll()
    {
        if (_disposed) return;
        long now = NowMicros;
        try
        {
            _transport?.Poll();
            _session.Update(now);
        }
        catch (InvalidOperationException e)
        {
            Error ??= $"Connection failed: {e.Message}";
        }
        CheckFailure(now);
        // A new round can arrive while the results screen is still visible. Game1 releases the old drive on the next
        // update; do not feed the next round's frames into its provider in the meantime.
        if (HasNewRound) return;
        var race = _session.Race;
        if (race != null && Provider == null && !ReferenceEquals(race, _releasedClient))
        {
            if (race.Baseline != null && _preparedScene != null)
            {
                Scene = _preparedScene;
                _providerClient = race;
                Provider = new RemoteRaceProvider(race, realtimeInputs: true);
            }
            else race.Update(now);
        }
        Provider?.Poll(now);
    }

    /// <summary>Only what the session client cannot report itself: a handshake that never gets an answer. A refused or closed
    /// session is described by <see cref="SessionClient.Phase"/>/<see cref="SessionClient.LastReject"/> for the menu.</summary>
    void CheckFailure(long now)
    {
        if (_session.Phase == SessionClientPhase.Connecting && now >= _connectDeadline)
            Error ??= "Timed out waiting for the session server (20 s).";
    }

    /// <summary>Drops the presentation of the current round; the next provider belongs to the next round client (a later
    /// welcome or a resume). The session and its seat stay connected.</summary>
    public void ReleaseRound()
    {
        if (_providerClient != null) _releasedClient = _providerClient;
        Provider?.DetachForNextRound();
        Provider = null;
        _providerClient = null;
    }

    /// <summary>Cuts the UDP link without telling the session client (tests and diagnostics): the client sees a lost link and
    /// reconnects like after a network drop, and the server reserves the seat of a running race.</summary>
    internal void DropLink() => _transport?.Disconnect();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Provider?.RequestLeave();   // DNF notice of the running round while the socket is still alive
        _session.LeaveGame();
        _transport?.Poll();
        _session.Disconnect();
        _transport?.Poll();
        _transport?.Dispose();
    }
}
