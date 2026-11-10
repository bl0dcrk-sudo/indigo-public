using NFS.Audio;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;

namespace NFS.Tests.Audio;

public class RaceSoundMathTests
{
    [Theory]
    [InlineData(0, 7000, false, 0)]
    [InlineData(1000, 7000, false, 14)]
    [InlineData(6000, 7000, false, 84)]
    [InlineData(12000, 7000, false, 100)]
    [InlineData(6000, 7000, true, 74)]
    [InlineData(12000, 7000, true, 74)]
    public void RpmPitch_KnownAsmValues(int rpm, int redline, bool nearby, int expected) =>
        Assert.Equal(expected, RaceSoundMath.RpmPitch(rpm, redline, nearby: nearby));

    [Theory]
    [InlineData(0, 127)]
    [InlineData(5625, 0)]
    [InlineData(2500, 70)]
    public void Attenuation_KnownAsmValues(int distanceSquared, int expected) =>
        Assert.Equal(expected, RaceSoundMath.Attenuation(distanceSquared));

    [Fact]
    public void Spatial_AsymmetricApproximationAndDoppler()
    {
        Assert.Equal(10 * 65536, RaceSoundMath.Distance(8 * 65536, 4 * 65536, 4 * 65536));
        Assert.Equal(65536, RaceSoundMath.Doppler(10 * 65536, null));
        Assert.Equal(58770, RaceSoundMath.Doppler(11 * 65536, 10 * 65536));
        Assert.Equal(74061, RaceSoundMath.Doppler(9 * 65536, 10 * 65536));
    }

    [Theory]
    [InlineData(2, 0, 42)] [InlineData(2, 8, 43)] [InlineData(2, 3, 41)]
    [InlineData(1, 0, 40)] [InlineData(1, 8, 40)] [InlineData(1, 9, 44)]
    public void Tyres_SelectOriginalSurfaceSample(int kind, int material, int sample) =>
        Assert.Equal(sample, RaceSoundMath.TyreSample(kind, material));

    [GameFact]
    public void ListenerPan_AndMirrorUseOriginalEndpoints()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), countdown: false);
        using var audio = new RaceAudio(run, GameData.Dir);
        audio.Listener = () => (run.Car.Pos - new Vec3(10 * 65536, 0, 0), new Vec3(65536, 0, 0), new Vec3(0, 0, 65536));
        run.Step(default);
        Assert.Equal(127, audio.Voices.Single(v => v.Channel == 0).Pan);
        run.Session.Setup.Set(0x34, 1);
        run.Step(default);
        Assert.Equal(0, audio.Voices.Single(v => v.Channel == 0).Pan);
    }

    [Fact]
    public void Mixer_HighPitchWrapsRepeatedly_AndHeadroomPreventsClipping()
    {
        var mixer = new Mixer(22050) { OutputGain = 0.5f };
        var s = new Sound([30000, 30000], 1, 22050, 0, 2);
        mixer.Play(s, pitch: 8);
        mixer.Play(s, pitch: 8);
        Assert.All(mixer.Mix(100), x => Assert.Equal(30000, x));
        Assert.Equal(0, mixer.ClippedSamples);
    }

    [Fact]
    public void PlayerEngine_ThrottleCrossfadeAndRoadGain_KnownAsmValues()
    {
        var curve = NFS.Core.Constants.AudioConstants.Curve.ToArray();
        Assert.Equal(new EngineLevels(64, 55, 55, 64, 24, 127),
            RaceSoundMath.PlayerEngine(64, 255, 0, 20 * 65536, 1, false, 0, curve));
        Assert.Equal(224, RaceSoundMath.PlayerEngine(64, 0, 255, 0, 1, false, 0, curve).Throttle);
        Assert.Equal(0, RaceSoundMath.PlayerEngine(64, 255, 0, 20 * 65536, 1, false, 11250, curve).OnLoad);
        Assert.Equal(24, RaceSoundMath.PlayerEngine(64, 255, 0, 20 * 65536, 2, false, 0, curve).RoadPitch);
        Assert.Equal(94, RaceSoundMath.PlayerEngine(64, 255, 0, 20 * 65536, 1, true, 0, curve).RoadPitch);
    }
    [GameFact]
    public void BankPitch_UsesCentsRootNoteAndFloatFactors()
    {
        var b = BnkBank.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/SFX/PC/MACF1.BNK"));
        var h = b.Slots[0]!.Header;
        Assert.Equal(-1500, RaceSoundMath.PitchCents(h, 0));
        Assert.Equal(0, RaceSoundMath.PitchCents(h, 64));
        Assert.Equal(1476, RaceSoundMath.PitchCents(h, 127));
        var factors = NFS.Core.Constants.AudioConstants.PitchFactors.ToArray();
        Assert.Equal(1, RaceSoundMath.PitchMultiplier(0, factors));
        Assert.InRange(RaceSoundMath.PitchMultiplier(-1500, factors), 0.42044, 0.42046);
        Assert.InRange(RaceSoundMath.PitchMultiplier(1200, factors), 1.99999, 2.00001);
    }
}

public class RaceAudioTests
{
    [GameFact]
    public void ReplayPcm_CountdownAndEngines_NonSilentUnclippedExactDuration()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), withAi: true);
        using var audio = new RaceAudio(run, GameData.Dir) { MasterVolume = 127 };
        var input = new ReplayInputSource(Replay.Load(gameDir: GameData.Dir));
        List<short> pcm = [];
        List<(int Tick, int Sample)> countdown = [];
        audio.BufferReady += b => pcm.AddRange(b);
        // Stage 5a.2: the beep of a step is played when that step's frame is consumed (its sound frame, run.Tick = start + 2).
        audio.OneShot += s => { if (s.Bank == "CNTENG") countdown.Add((run.Tick - 2, s.Sample)); };
        for (int i = 0; i < 16 * 32; i++) run.Step(input.ForRaceTick(run.Tick));
        Assert.Equal(new[] { (320, 0), (384, 1), (448, 2), (512, 3) }, countdown); // "3", "2", "1", "GO" [ref:3013]
        Assert.Equal(16 * 22050 * 2, pcm.Count);
        Assert.Contains(pcm, x => x != 0);
        Assert.DoesNotContain(pcm, x => x is short.MinValue or short.MaxValue);
        Assert.Equal(0, audio.Mixer.ClippedSamples);
        Assert.Contains(audio.Voices, v => v.Channel == 0 && v.Sample == 0 && v.Volume > 0);
        Assert.Contains(audio.Voices, v => v.Channel == 2 && v.Sample == 1);
    }

    [GameFact]
    public void AudioEnabled_AllCarStateAndSimulationRandomRemainUnchanged()
    {
        var a = new RaceRunner(TrackSessions.FreshReplay(), withAi: true);
        var b = new RaceRunner(TrackSessions.FreshReplay(), withAi: true);
        using var audio = new RaceAudio(a, GameData.Dir);
        List<RaceAudio.SoundVoice> shots = [];
        audio.OneShot += shots.Add;
        var input = new ReplayInputSource(Replay.Load(gameDir: GameData.Dir));
        for (int i = 0; i < 35 * 32; i++)
        {
            var controls = input.ForRaceTick(a.Tick);
            a.Step(controls); b.Step(controls);
            for (int car = 0; car < a.Cars.Count; car++)
                foreach (var slot in CarDump.Slots) Assert.Equal(slot.Get(b.Cars[car].Obj), slot.Get(a.Cars[car].Obj));
        }
        Assert.Equal(a.AiRng.State, b.AiRng.State);
        Assert.Equal(a.Rand.Next(), b.Rand.Next());
        Assert.Contains(shots, s => s.Bank == "GEN" && s.Sample >= 29 && s.Volume > 0);
    }

    [GameTheory]
    [InlineData(1, 0, 16, 29)] [InlineData(1, 0, 17, 32)]
    [InlineData(1, 0, 23, 34)] [InlineData(1, 0, 81, 33)]
    [InlineData(2, 0, 100, 31)] [InlineData(1, 10, 100, 30)]
    [InlineData(1, 5, 100, 29)] [InlineData(1, 1, 25, 30)]
    [InlineData(1, 1, 26, 34)] [InlineData(1, 1, 51, 37)]
    [InlineData(1, 1, 71, 32)] [InlineData(1, 1, 91, 33)]
    public void ImpactSelection_KnownAsmThresholds(int kind, int material, int strength, int sample)
    {
        using var audio = new RaceAudio(new RaceRunner(TrackSessions.FreshReplay()), GameData.Dir);
        Assert.Equal(sample, audio.ImpactSample(kind, material, strength));
    }

    [GameFact]
    public void HornStopsOnRelease_MuteAndDisposeStopAudioOnly()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), countdown: false);
        var audio = new RaceAudio(run, GameData.Dir);
        run.Step(new PlayerInput(0, 0, 0, PlayerInput.BtnHorn));
        Assert.Contains(audio.Voices, v => v.Channel == 12 && v.Sample == 3);
        run.Step(default);
        Assert.DoesNotContain(audio.Voices, v => v.Channel == 12);
        audio.MasterVolume = 0;
        short[] pcm = [];
        audio.BufferReady += b => pcm = b;
        run.Step(default);
        Assert.True(pcm.All(x => x == 0), "master volume zero produces silent PCM");
        audio.Dispose();
        int tick = run.Tick;
        run.Step(default);
        Assert.Equal(tick + 2, run.Tick);
        Assert.Empty(audio.Voices);
    }

}
