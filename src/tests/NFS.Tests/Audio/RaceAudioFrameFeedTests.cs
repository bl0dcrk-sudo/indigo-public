using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Audio;

/// <summary>Stage 5a.2: audio fed by provider frames/events (the Desktop path) sounds exactly like the bare-runner adapter.</summary>
public class RaceAudioFrameFeedTests
{
    static void Collect(RaceAudio audio, List<short> pcm, List<RaceAudio.SoundVoice> shots)
    {
        audio.BufferReady += b => pcm.AddRange(b);
        audio.OneShot += shots.Add;
    }

    [GameFact]
    public void ProviderFeed_MatchesBareRunnerPcmAndOneShots()
    {
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var input = new ReplayInputSource(rpy);
        const int steps = 32 * 30;   // countdown, start, the first corners with AI contact

        var run = new RaceRunner(TrackSessions.FreshReplay(), 0, withAi: true);
        List<short> pcmA = []; List<RaceAudio.SoundVoice> shotsA = [];
        using (var legacy = new RaceAudio(run, GameData.Dir) { MasterVolume = 127 })
        {
            Collect(legacy, pcmA, shotsA);
            for (int i = 0; i < steps; i++) run.Step(input.ForRaceTick(run.Tick));
        }

        using var provider = new LocalRaceProvider(TrackSessions.FreshReplay(), 0, withAi: true);
        List<short> pcmB = []; List<RaceAudio.SoundVoice> shotsB = [];
        var inbox = new RaceEventInbox(provider.Description.RaceId);
        using (var fed = new RaceAudio(provider.Host.Runner.Session, provider.Description, GameData.Dir) { MasterVolume = 127 })
        {
            Collect(fed, pcmB, shotsB);
            for (int i = 0; i < steps; i++)
            {
                provider.SubmitInput(0, input.ForRaceTick(provider.ReadLatestState().Frame.SimulationTick64));
                provider.Step();
                inbox.Accept(provider.DrainEvents());
                foreach (var f in provider.DrainFrames()) fed.Present(f, inbox.TakeThrough(f.SnapshotSequence));
            }
        }
        Assert.Equal(steps, provider.Host.Steps);
        Assert.Equal(pcmA.Count, pcmB.Count);
        Assert.Equal(pcmA, pcmB);
        Assert.Equal(shotsA, shotsB);
        Assert.Contains(shotsB, s => s.Bank.StartsWith("CNT"));
        Assert.Contains(pcmB, x => x != 0);
    }
}
