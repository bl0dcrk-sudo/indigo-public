using NFS.Audio;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Audio;

/// <summary>Split-screen sound: with == 1 – one listener per human ((p)) [ref:3014]
/// only the car's own engine, channels 0/2/10 and 1/3/11, fixed panning (player 1 right 0x7F, player 2 left 0), horns 12/13 [ref:3015]
/// no nearby cars (channels 4/5) and no AI horn; the "s" banks are used [ref:3016]</summary>
public class SplitAudioTests
{
    static RaceRunner Runner(int opponents = 2, Action<FeConfig>? edit = null)
    {
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 1; cfg.RaceType = 0; cfg.OpponentCount = (byte)opponents; cfg.TrafficOff = 1; cfg.Track = 1;
        edit?.Invoke(cfg);
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        var session = new RaceLoader(GameData.Dir).Load(setup);
        return new RaceRunner(session, 0, automatic: true, withAi: true, countdown: false, secondPlayerIndex: 1, automatic2: true);
    }

    static (Vec3, Vec3, Vec3) Listen(CarObj car, int dx = 0) => (car.Pos + new Vec3(dx, 0, 0), new Vec3(65536, 0, 0), new Vec3(0, 0, 65536));

    [Fact]
    public void FixedPan_IsRightForTheFirstAndLeftForTheSecondHuman()
    {
        Assert.Equal(127, RaceAudio.SplitPan(0));
        Assert.Equal(0, RaceAudio.SplitPan(1));
    }

    [GameFact]
    public void ListenerOfEachHuman_SetsItsOwnDistanceAttenuation()
    {
        var run = Runner();
        using var audio = new RaceAudio(run, GameData.Dir);
        audio.Listener = () => Listen(run.Player.Obj, dx: 200 * 65536);    // player 1's camera is 200 m away from its car
        audio.Listener2 = () => Listen(run.Player2!.Obj);
        for (int i = 0; i < 40; i++) run.Step(new PlayerInput(0, 255, 0, 0), new PlayerInput(0, 255, 0, 0));
        var v = audio.Voices;
        Assert.Equal(0, v.Single(x => x.Channel == 0).Volume);              // (11250 − d²) < 0: silent
        Assert.True(v.Single(x => x.Channel == 1).Volume > 0);              // the other one is close to its listener
    }

    [GameFact]
    public void MirroredTrack_DoesNotSwapTheEngineSides_ButSwapsTyresAndHorn()
    {
        var run = Runner(edit: c => c.Mirrored = 1);
        Assert.True(run.Session.Setup.Mirrored);
        using var audio = new RaceAudio(run, GameData.Dir);
        for (int i = 0; i < 4; i++) run.Step(new PlayerInput(0, 255, 0, 0), new PlayerInput(0, 255, 0, 0));
        Assert.Equal(127, audio.Voices.Single(x => x.Channel == 0).Pan);    // sets the engine pan directly [ref:3017]
        Assert.Equal(0, audio.Voices.Single(x => x.Channel == 1).Pan);
    }

    [GameFact]
    public void SecondHumansHorn_UsesChannel13_FirstUses12()
    {
        var run = Runner();
        using var audio = new RaceAudio(run, GameData.Dir);
        run.Step(default, new PlayerInput(0, 0, 0, PlayerInput.BtnHorn));
        Assert.Contains(audio.Voices, v => v.Channel == 13 && v.Sample == 3 && v.Pan == 0);
        Assert.DoesNotContain(audio.Voices, v => v.Channel == 12);
        run.Step(new PlayerInput(0, 0, 0, PlayerInput.BtnHorn), default);
        Assert.Contains(audio.Voices, v => v.Channel == 12 && v.Sample == 3 && v.Pan == 127);
        Assert.DoesNotContain(audio.Voices, v => v.Channel == 13);
    }

    [GameFact]
    public void Pcm_IsExactLength_NonSilent_AndTheSimulationIsUntouched()
    {
        var a = Runner(); var b = Runner();
        using var audio = new RaceAudio(a, GameData.Dir);
        List<short> pcm = [];
        audio.BufferReady += x => pcm.AddRange(x);
        for (int i = 0; i < 6 * 32; i++)
        {
            var p1 = new PlayerInput((sbyte)(i % 64 < 32 ? 40 : -40), 255, 0, 0); var p2 = new PlayerInput(0, 200, 0, 0);
            a.Step(p1, p2); b.Step(p1, p2);
            for (int car = 0; car < a.Cars.Count; car++)
                foreach (var slot in CarDump.Slots) Assert.Equal(slot.Get(b.Cars[car].Obj), slot.Get(a.Cars[car].Obj));
        }
        Assert.Equal(a.AiRng.State, b.AiRng.State);
        Assert.Equal(6 * 22050 * 2, pcm.Count);
        Assert.Contains(pcm, x => x != 0);
        Assert.Equal(0, audio.Mixer.ClippedSamples);
        // left and right channels both carry sound (player 1 right, player 2 left)
        long left = 0, right = 0;
        for (int i = 0; i + 1 < pcm.Count; i += 2) { left += Math.Abs(pcm[i]); right += Math.Abs(pcm[i + 1]); }
        Assert.True(left > 0 && right > 0, $"left {left}, right {right}");
    }

    [GameFact]
    public void OnePlayerRace_KeepsTheSingleLayout()
    {
        var run = new RaceRunner(TrackSessions.FreshReplay(), countdown: false);
        using var audio = new RaceAudio(run, GameData.Dir);
        Assert.False(audio.IsSplit);
        run.Step(default);
        Assert.Contains(audio.Voices, v => v.Channel == 0 && v.Bank != "GEN" && !v.Bank.StartsWith("S", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(audio.Voices, v => v.Channel is 1 or 3 or 11 or 13);
    }

    [GameFact]
    public void EachHuman_HasItsOwnEngineChannels_BanksAndPan_AndNoOpponentVoices()
    {
        var run = Runner();
        using var audio = new RaceAudio(run, GameData.Dir);
        Assert.True(audio.IsSplit);
        audio.Listener = () => Listen(run.Player.Obj);
        audio.Listener2 = () => Listen(run.Player2!.Obj);
        for (int i = 0; i < 40; i++) run.Step(new PlayerInput(0, 255, 0, 0), new PlayerInput(0, 255, 0, 0));
        var v = audio.Voices;
        string Bank(int model) => model < 15 ? NFS.Core.Constants.AudioConstants.SplitBanks[model] : "";
        foreach (int p in new[] { 0, 1 })
        {
            var car = p == 0 ? run.Player : run.Player2!;
            var on = v.Single(x => x.Channel == p);
            var off = v.Single(x => x.Channel == 2 + p);
            var road = v.Single(x => x.Channel == 10 + p);
            Assert.Equal(0, on.Sample); Assert.Equal(1, off.Sample); Assert.Equal("GEN", road.Bank);
            Assert.Equal(Bank(car.Setup.CarType), on.Bank, ignoreCase: true);   // sMacF1 … (s-bank of that car) [ref:3018]
            Assert.Equal(RaceAudio.SplitPan(p), on.Pan);
            Assert.Equal(RaceAudio.SplitPan(p), off.Pan);
            Assert.Equal(RaceAudio.SplitPan(p), road.Pan);
            Assert.True(on.Volume > 0 || off.Volume > 0);
        }
        Assert.DoesNotContain(v, x => x.Channel is 4 or 5 or 16 or 17 or 19);   // AI horns are not played in split [ref:3019]
    }
}
