using NFS.Core;
using NFS.Data;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;

namespace NFS.Desktop.Tests;

/// <summary>Desktop presentation (RemoteRaceProvider + RaceDrive) over the session architecture: a real SessionServer and
/// SessionClients on the simulated network; the provider is created per round client, like RemoteDesktopSession does.</summary>
public sealed class RemoteDesktopNetworkTests
{
    sealed class Peer
    {
        public required SessionClient Client;
        public SimNetwork.SimClient Link = null!;
        public RaceClient Race => Client.Race!;
        public RemoteRaceProvider? Provider;
        public RaceClient? ProviderClient;
        public RaceScene? Scene;
        public RaceDrive? Drive;
        public DriveKeys Keys;
        public long NextFrame, LastFrame;
        public int Finishes;
        public List<NetInputSample> Samples { get; } = [];
    }

    sealed class Session(int fps, int maxSteps = 1000) : IDisposable
    {
        readonly string _dir = GameFiles.FindGameDir(null);
        readonly byte[] _fingerprint = SimDataFingerprint.Compute(GameFiles.FindGameDir(null));
        public SimNetwork Net { get; } = new(986);
        SessionServer _server = null!;
        public List<Peer> Peers { get; } = [];
        public RaceHost Host => _server.Games[0].Round!.Host;

        public void Start()
        {
            _server = new SessionServer(Net.ServerTransport, _dir, _fingerprint, 0,
                new SessionServerOptions { MaxRaceSteps = maxSteps, RaceLingerMicros = 3_000_000, CountdownMicros = 500_000 }, null, 987)
            { Clock = () => Net.Now };
            Net.Server = _server;
        }

        /// <summary>Connects a client; the first one creates the game, the others join it. Everybody readies up.</summary>
        public Peer Join()
        {
            var peer = new Peer { Client = null! };
            string name = $"driver{Peers.Count}";
            RaceScene? prepared = null;
            peer.Client = new SessionClient(_fingerprint, name, (handler, hello) =>
            {
                peer.Link = Net.AddClient(handler, LinkProfile.Rtt(10), LinkProfile.Rtt(10));
                peer.Link.Connect(hello);
                return peer.Link;
            }, () => Net.Now, new SessionClientOptions
            {
                PrepareRace = w => { prepared = RemoteDesktopSession.LoadWelcomeScene(_dir, w.SetupBlock, w.CarId); peer.Scene = prepared; return true; },
            });
            peer.Client.Connect();
            Peers.Add(peer);
            Assert.True(Until(() => peer.Client.Phase == SessionClientPhase.Browsing, 2_000_000));
            if (Peers.Count == 1) peer.Client.CreateGame("game", 2);
            else peer.Client.JoinGame(Peers[0].Client.GameId);
            Assert.True(Until(() => peer.Client.Phase == SessionClientPhase.InGame, 2_000_000));
            if (Peers.Count == 1) peer.Client.SetOptions(1, 3);   // the creator sets track and laps in the lobby
            peer.Client.SetReady(true);
            return peer;
        }

        public void StartRace()
        {
            Assert.True(Until(() => Peers.All(p => p.Client.Lobby?.Members.All(m => m.Ready) == true && p.Client.Lobby.Members.Count == Peers.Count), 2_000_000));
            Assert.True(Peers[0].Client.StartRace());
        }

        public void Run(long micros) => Until(() => false, micros);
        public bool Until(Func<bool> done, long budget)
        {
            long end = Net.Now + budget;
            while (!done() && Net.Now < end)
            {
                Net.AdvanceTo(Net.Now + 1000);
                _server.Update(Net.Now);
                foreach (var p in Peers)
                {
                    p.Client.Update(Net.Now);
                    var race = p.Client.Race;
                    if (race == null) continue;
                    if (!ReferenceEquals(race, p.ProviderClient))
                    {
                        if (race.Baseline == null) { race.Update(Net.Now); continue; }
                        // One provider and one drive per round client: the old ones are dropped, not reconfigured.
                        p.Provider?.DetachForNextRound();
                        p.ProviderClient = race;
                        p.Provider = new RemoteRaceProvider(race, realtimeInputs: true);
                        race.InputSampleCreated += (sample, _) => p.Samples.Add(sample);
                        p.Drive = null;
                    }
                    p.Provider!.Poll(Net.Now);
                    if (p.Drive == null)
                    {
                        p.Drive = new RaceDrive(p.Scene!, provider: p.Provider, monotonicMicros: () => Net.Now);
                        p.Drive.FramePresented += (_, events) => p.Finishes += events.OfType<RaceFinishedEvent>().Count();
                        p.LastFrame = Net.Now;
                        p.NextFrame = Net.Now + (long)Math.Round(1_000_000.0 / fps);
                    }
                    if (Net.Now < p.NextFrame) continue;
                    p.Drive.Frame((Net.Now - p.LastFrame) / 1_000_000.0, p.Keys);
                    p.LastFrame = Net.Now;
                    p.NextFrame += (long)Math.Round(1_000_000.0 / fps);
                }
            }
            return done();
        }
        public void Dispose()
        {
            foreach (var p in Peers) p.Provider?.Dispose();
            _server.Dispose();
        }
    }

    [GameDataTheory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void ReadyWaitAndStartLeadCannotDelayCurrentThrottleOrLoseTerminalFinish(int fps)
    {
        using var s = new Session(fps, maxSteps: 400);
        s.Start();
        var first = s.Join();
        var second = s.Join();
        // Both members are ready but the operator has not started: no round exists, so nothing can be sampled or sent.
        s.Run(3_000_000);
        Assert.Null(first.Client.Race);
        Assert.Empty(first.Samples);
        s.StartRace();
        // The round (welcome, baseline) arrives after the countdown; the presentation exists before the race starts.
        Assert.True(s.Until(() => first.Drive != null && second.Drive != null, 5_000_000));
        Assert.NotEqual(RaceClientState.Started, first.Race.State);
        Assert.Equal(0, first.Race.PendingInputs);
        Assert.Empty(first.Samples);
        Assert.True(s.Until(() => first.Race.State == RaceClientState.Started && second.Drive != null, 1_000_000));
        s.Run(200_000); // inside the 500 ms start lead
        Assert.Equal(0, first.Race.PendingInputs);
        Assert.True(s.Until(() => s.Host.Steps > 0, 1_000_000));
        first.Keys = new DriveKeys(true, false, false, false, false, false, false);
        long changedAt = s.Net.Now;
        int oldSamples = first.Samples.Count;
        Assert.True(s.Until(() => first.Samples.Skip(oldSamples).Any(sample => sample.Gas == 255), 150_000));
        Assert.True(s.Net.Now - changedAt < 150_000);
        Assert.Equal(0, first.Race.PendingInputs);
        Assert.True(s.Until(() => s.Host.View.Latest.AppliedInput[first.Race.Welcome!.Seat] > 0
            && new ReplayInputSource(s.Host.Recorder.Build(s.Host.Runner.Tick))
                .At(s.Host.Steps - 1, first.Race.Welcome.Seat).Gas > 0, 1_000_000));
        Assert.True(s.Until(() => s.Host.Steps >= 396, 15_000_000));
        // Lose all four final unreliable snapshots for client 0. Reliable result/events still arrive after retries.
        first.Link.SetProfiles(LinkProfile.Rtt(10), new LinkProfile(5, Loss: 1));
        Assert.True(s.Until(() => s.Peers.All(p => p.Drive?.Results != null), 15_000_000));
        s.Run(200_000);
        foreach (var peer in s.Peers)
        {
            Assert.Equal(1, peer.Finishes);
            Assert.Same(peer.Drive!.View, peer.Scene!.Frame);
            Assert.Equal(0, peer.Race.PendingInputs);
        }
        Assert.True(first.Drive!.View.Steps < 400);
        Assert.Equal(RaceViewFrameKind.Resync, first.Drive.View.Kind);
    }

    [GameDataTheory]
    [InlineData(PlayerInput.BtnReset)]
    [InlineData(PlayerInput.BtnGearUp)]
    [InlineData(PlayerInput.BtnGearDown)]
    public void FullActionQueueKeepsCurrentControlsAndRetriesActionOnceAfterFinalAck(byte action)
    {
        using var s = new Session(20);
        s.Start();
        var first = s.Join();
        s.Join();
        s.StartRace();
        Assert.True(s.Until(() => first.Client.Race?.Latest?.RaceStarted == true, 12_000_000));
        for (int i = 0; i < NetProtocol.MaxActionsPerPacket; i++)
            Assert.True(first.Race.QueueAction(action == PlayerInput.BtnGearDown ? NetActionKind.GearUp : NetActionKind.GearDown));
        first.Keys = new DriveKeys(true, false, false, false,
            action == PlayerInput.BtnGearUp, action == PlayerInput.BtnGearDown, action == PlayerInput.BtnReset);
        // Render immediately, before network polling can ACK the saturated queue.
        first.Drive!.Frame(1.0 / 32, first.Keys);
        Assert.Equal(NetProtocol.MaxActionsPerPacket, first.Race.PendingActions);
        Assert.Equal(255, first.Race.Controls.Gas);
        first.Keys = first.Keys with { Reset = false, GearUp = false, GearDown = false }; // short tap survives rejection
        Assert.True(s.Until(() => first.Race.LatestAck.LastAction == NetProtocol.MaxActionsPerPacket + 1
            && first.Race.PendingActions == 0, 3_000_000));
        s.Run(300_000);
        if (action == PlayerInput.BtnReset) Assert.Equal(1, s.Host.Runner.ManualResets);
        var replay = new ReplayInputSource(s.Host.Recorder.Build(s.Host.Runner.Tick));
        Assert.Equal(1, Enumerable.Range(0, s.Host.Steps).Count(step =>
            (replay.At(step, first.Race.Welcome!.Seat).Buttons & action) != 0));
        Assert.True(first.Provider!.ReadLatestState().Frame.AppliedInput[0] > 0);
        Assert.Equal(0, first.Race.PendingInputs);
    }
}
