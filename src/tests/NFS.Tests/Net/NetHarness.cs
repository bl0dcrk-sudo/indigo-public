using NFS.Core;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Server + clients over <see cref="SimNetwork"/> with a fake clock advanced in 1 ms ticks (no sockets, no wall clock).</summary>
sealed class NetHarness : IDisposable
{
    static readonly Lazy<byte[]> LazyFingerprint = new(() => SimDataFingerprint.Compute(GameData.Dir));
    public static byte[] Fingerprint => LazyFingerprint.Value;

    public SimNetwork Net { get; }
    public RaceHost Host { get; }
    public RaceServer Server { get; }
    public List<(RaceClient Client, SimNetwork.SimClient Link)> Clients { get; } = [];
    readonly Dictionary<RaceClient, RemoteRaceProvider> _providers = [];
    public RemoteRaceProvider UseProvider(RaceClient client, bool realtimeInputs = false)
    {
        var provider = new RemoteRaceProvider(client, realtimeInputs);
        _providers.Add(client, provider);
        return provider;
    }
    /// <summary>Every frame the host published, by SnapshotSequence (the reference the clients must reproduce).</summary>
    public Dictionary<long, RaceViewFrame> Frames { get; } = [];
    public List<string> Log { get; } = [];
    /// <summary>Per client: controls as a function of the client's newest frame.</summary>
    public Func<int, RaceViewFrame?, PlayerInput>? Controls { get; set; }

    /// <param name="hostFactory">Creates a host for (race id, human car count); null = a single host of the configured capacity.
    /// The initial (capacity/lobby) host is created with the same factory.</param>
    /// <param name="hostFactoryBots">Creates a host for (race id, human car count, real AI car count); required for operator bot
    /// changes. The initial (capacity/lobby) host still uses <paramref name="hostFactory"/> or the configured capacity host.</param>
    /// <param name="hostFactoryTypes">Creates a host for (race id, human car count, real AI car count, car model types the
    /// humans selected). Only passed when a test needs the menu selections to compose a race; without it the server keeps
    /// the frozen host models (legacy behaviour, including the refusal to start with another model).</param>
    /// <param name="hostFactoryTrack">Creates a host for (race id, human car count, real AI car count, car model types,
    /// track, backward); required for operator track/direction changes and preferred over the other factories when present.</param>
    public NetHarness(int seed, RaceServerOptions? options = null, ServerRaceConfig? race = null,
        Func<int, int, RaceHost>? hostFactory = null, Func<int, int, int, RaceHost>? hostFactoryBots = null,
        Func<int, int, int, IReadOnlyList<int>, RaceHost>? hostFactoryTypes = null,
        Func<int, int, int, IReadOnlyList<int>?, int, bool, RaceHost>? hostFactoryTrack = null)
    {
        Net = new SimNetwork(seed);
        var config = race ?? new ServerRaceConfig();
        int raceId = 0x5EED0000 + seed;
        Host = hostFactory?.Invoke(raceId, config.EffectivePlayers) ?? config.CreateHost(GameData.Dir, raceId);
        Host.View.Published += (f, _) => Frames[f.SnapshotSequence] = f;
        Frames[0] = Host.View.Latest;
        Server = new RaceServer(Host, Fingerprint, Net.ServerTransport, 0, options, s => Log.Add($"{Net.Now / 1000} server {s}"), seed + 1,
            hostFactory, hostFactoryBots, hostFactoryTypes, hostFactoryTrack) { Clock = () => Net.Now, PublishedEvents = [] };
        Net.Server = Server;
    }

    /// <summary>Per client: every sample created, every completed frame and every event handed out by its inbox.</summary>
    public List<List<NetInputSample>> Samples { get; } = [];
    public List<List<RaceViewFrame>> Received { get; } = [];
    public List<List<RaceEvent>> Taken { get; } = [];

    /// <param name="carType">Car model selected in the menu, sent in the hello (<see cref="NetProtocol.NoCarType"/> = none).</param>
    public RaceClient Join(string name, LinkProfile up, LinkProfile down, byte[]? hello = null, Func<NetWelcome, bool>? prepare = null,
        int carType = NetProtocol.NoCarType)
    {
        var samples = new List<NetInputSample>();
        Samples.Add(samples);
        Received.Add([]);
        Taken.Add([]);
        var client = new RaceClient(null, Fingerprint, name, prepare, log: s => Log.Add($"{Net.Now / 1000} {name} {s}"), carType)
        {
            Clock = () => Net.Now, SampleCreated = samples.Add,
        };
        var link = Net.AddClient(client, up, down);
        client.Transport = link;
        link.Connect(hello ?? client.Hello());
        Clients.Add((client, link));
        return client;
    }

    public RaceClient Join(string name, LinkProfile both) => Join(name, both, both);

    public void Run(long micros) => RunUntil(() => false, micros);

    /// <summary>Advances in 1 ms ticks until <paramref name="done"/> or the time budget; returns whether <paramref name="done"/> held.</summary>
    public bool RunUntil(Func<bool> done, long maxMicros)
    {
        long end = Net.Now + maxMicros;
        while (Net.Now < end)
        {
            if (done()) return true;
            long t = Net.Now + 1000;
            Net.AdvanceTo(t);
            Server.Update(t);
            for (int i = 0; i < Clients.Count; i++)
            {
                var c = Clients[i].Client;
                if (_providers.TryGetValue(c, out var provider))
                {
                    provider.Poll(t);
                    Taken[i].AddRange(provider.DrainEvents()); // same order as presentation: events before frames
                    Received[i].AddRange(provider.DrainFrames());
                    if (Controls != null) c.Controls = Controls(i, c.Latest);
                    continue;
                }
                foreach (var f in c.DrainFrames())
                {
                    Received[i].Add(f);
                    if (c.Events != null) Taken[i].AddRange(c.Events.TakeThrough(f.SnapshotSequence, f.SimulationTick64));
                }
                if (Controls != null) c.Controls = Controls(i, c.Latest);
                c.Update(t);
            }
        }
        return done();
    }

    public void Dispose()
    {
        Server.Dispose();  // releases the current host (the initial one is already released after a round change)
        Host.Dispose();    // idempotent; the harness created it
    }

    // ---------- comparisons ----------

    static readonly IReadOnlyList<int> NoLaps = [];

    public static bool SameCar(CarView a, CarView b) => a with { LapTimes = NoLaps } == b with { LapTimes = NoLaps } && a.LapTimes.SequenceEqual(b.LapTimes);

    /// <summary>The client frame reproduces the host frame of the same sequence: every car (exact CarView, pose epoch), parked
    /// cars, ranking, ticks and race flags.</summary>
    public static void AssertSameFrame(RaceViewFrame host, RaceViewFrame client)
    {
        Assert.Equal((host.RaceId, host.SnapshotSequence, host.Kind, host.SimulationTick64, host.Steps, host.RaceStarted, host.NumLaps, host.RacerCount),
            (client.RaceId, client.SnapshotSequence, client.Kind, client.SimulationTick64, client.Steps, client.RaceStarted, client.NumLaps, client.RacerCount));
        Assert.Equal(host.Ranking, client.Ranking);
        Assert.Equal(host.Cars.Count, client.Cars.Count);
        for (int i = 0; i < host.Cars.Count; i++)
        {
            Assert.Equal((host.Cars[i].CarId, host.Cars[i].PoseEpoch), (client.Cars[i].CarId, client.Cars[i].PoseEpoch));
            Assert.True(SameCar(host.Cars[i].State, client.Cars[i].State), $"car {i} differs at snapshot {host.SnapshotSequence}");
        }
        Assert.Equal(host.Parked.Count, client.Parked.Count);
        for (int i = 0; i < host.Parked.Count; i++)
            Assert.True(SameCar(host.Parked[i].State, client.Parked[i].State), $"parked {i} differs at snapshot {host.SnapshotSequence}");
    }

    /// <summary>Scripted driving: neutral until the green light, then gas with a weaving steer and short handbrake slides
    /// (tyre marks) – different per client.</summary>
    public static PlayerInput Weave(int client, RaceViewFrame? f)
    {
        if (f is not { RaceStarted: true }) return default;
        int s = f.Steps + client * 13;
        byte buttons = s % 64 is >= 40 and < 48 ? PlayerInput.BtnHandbrake : (byte)0;
        // values already on the replay/PlayerDriver grid (steer /4, gas /8), so recorded inputs compare 1:1 with the samples
        return new PlayerInput((sbyte)((int)(Math.Sin(s / 11.0) * 25) * 4), 248, 0, buttons);
    }
}

/// <summary>Records what a raw client endpoint receives (handshake tests without <see cref="RaceClient"/>).</summary>
sealed class RecordingHandler : INetClientHandler
{
    public bool Connected;
    public byte[]? DisconnectData;
    public bool Disconnected;
    public List<byte[]> Received { get; } = [];
    public void OnConnected() => Connected = true;
    public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data) => Received.Add(data.ToArray());
    public void OnDisconnected(byte[]? reason) { Disconnected = true; DisconnectData = reason; }
}
