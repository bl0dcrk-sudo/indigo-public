using System.Security.Cryptography;
using System.Text;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Game;

public class ReplayInputSourceTests(ITestOutputHelper output)
{
    private static Replay Rpy() => Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));

    [GameFact]
    public void Inputs_HaveSaneRanges_AndLengthMatchesHeader()
    {
        var src = new ReplayInputSource(Rpy());
        Assert.Equal(1, src.NumPlayers);
        Assert.Equal(0x8D34, src.RaceTicks64);
        int len = src.Length();
        output.WriteLine($"samples {len}, header ticks32 {src.RaceTicks32}, packets {src.Replay.Packets.Count}");
        // 32 samples per packet from race tick 0; only full packets are stored, so the last partial one (≤31 ticks of
        // the header length 0x8D34/2 = 18074) is missing: 564 packets = 18048 samples
        Assert.Equal(src.Replay.Packets.Count * 32, len);
        Assert.InRange(src.RaceTicks32 - len, 0, 31);
        int gasOn = 0, steerOn = 0;
        for (int t = 0; t < len; t++)
        {
            var i = src.At(t);
            Assert.Equal(0, i.Steer % 4);           // (s8)(b<<2) [ref:3224]
            Assert.Equal(0, i.Gas % 8);             // b<<3
            Assert.Equal(0, i.Brake % 8);
            Assert.InRange((int)i.Gas, 0, 248);
            if (i.Gas > 0) gasOn++;
            if (i.Steer != 0) steerOn++;
        }
        Assert.True(gasOn > len / 2, $"gas held only {gasOn}/{len}");
        Assert.True(steerOn > 0);
        Assert.Equal(default, src.At(len));         // past the end = no input
        Assert.Equal(src.At(300), src.ForRaceTick(601));
    }

    [GameFact]
    public void FirstPacket_MatchesManualDecode()
    {
        var raw = GameData.ReadRaw("REPLAY.RPY");
        var src = new ReplayInputSource(Replay.Parse(raw));
        // manual RLE decode of the first packet's 4 sub-packets at 0x5B4
        int p = Replay.StreamOffset;
        var subs = new List<byte>[4];
        for (int s = 0; s < 4; s++)
        {
            int len = raw[p];
            subs[s] = [];
            for (int i = p + 1; i < p + len;)
                if (raw[i] == 0xFF) { for (int k = 0; k < raw[i + 1]; k++) subs[s].Add(raw[i + 2]); i += 3; }
                else subs[s].Add(raw[i++]);
            p += len;
        }
        for (int t = 0; t < 32; t++)
        {
            var i = src.At(t);
            Assert.Equal((sbyte)(subs[0][t] << 2), i.Steer);
            Assert.Equal(subs[1][t] << 3, i.Gas);
            Assert.Equal(subs[2][t] << 3, i.Brake);
            Assert.Equal(subs[3][t], i.Buttons);
        }
    }

    [GameFact]
    public void Countdown_AppliesManualGearPresses()
    {
        var runner = new RaceRunner(TrackSessions.FreshReplay(), 0, withAi: false);
        Assert.False(runner.Automatic);                   // REPLAY.RPY car 0: Transmission 0 = manual
        Assert.Equal(1, runner.Car.Gear);                 // neutral on the grid
        var run = new ReplayRun(runner, new ReplayInputSource(Rpy()));
        Assert.Equal(2, run.Countdown());                 // up presses at samples 78 and 106
        Assert.Equal(3, runner.Car.Gear);                 // N → 1st → 2nd
    }

    [GameFact]
    public void ReplayRun_IsDeterministic()
    {
        string Run()
        {
            var runner = new RaceRunner(TrackSessions.FreshReplay(), 0, withAi: true);
            return new ReplayRun(runner, new ReplayInputSource(Rpy())).Run(maxSteps: 32 * 40);
        }
        string a = Run(), b = Run();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a))),
                     Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b))));
        Assert.Equal(1 + 40 + 1, a.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.StartsWith(ReplayRun.CsvHeader, a);
    }

    [GameFact]
    public void Recorder_RoundTripsOddLengthAndInterleavedSplitInputsWithoutMutatingSetup()
    {
        var setup = RaceInfo.Setup("replay", GameData.Dir);
        setup.Set(0x6C, 2);
        var recorder = new ReplayRecorder(setup);
        setup.Set(0x24, 12); // construction must keep its own header snapshot
        var inputs = new PlayerInput[37, 2];
        for (int i = 0; i < 37; i++)
        {
            inputs[i, 0] = new PlayerInput((sbyte)(i % 2 == 0 ? -7 : 11), (byte)(i * 5), (byte)(i * 3), (byte)(i % 4));
            inputs[i, 1] = new PlayerInput((sbyte)(i % 2 == 0 ? 7 : -11), (byte)(i * 3), (byte)(i * 5), (byte)(i % 8));
            recorder.Record(inputs[i, 0], inputs[i, 1]);
        }

        var replay = recorder.Build(75); // odd 64 Hz race length → 37 sim samples
        var source = new ReplayInputSource(replay);
        Assert.Equal(2, source.NumPlayers);
        Assert.Equal(75, replay.RaceTicks);
        Assert.Equal(2, replay.Config.ReplayMode);
        Assert.Equal(4, replay.Config.Track); // immutable constructor snapshot
        for (int i = 0; i < 37; i++)
        {
            Assert.Equal((sbyte)(inputs[i, 0].Steer / 4 * 4), source.At(i, 0).Steer);
            Assert.Equal((sbyte)(inputs[i, 1].Steer / 4 * 4), source.At(i, 1).Steer);
            Assert.Equal((byte)(inputs[i, 0].Gas / 8 * 8), source.At(i, 0).Gas);
            Assert.Equal((byte)(inputs[i, 1].Brake / 8 * 8), source.At(i, 1).Brake);
            Assert.Equal(inputs[i, 0].Buttons, source.At(i, 0).Buttons);
            Assert.Equal(inputs[i, 1].Buttons, source.At(i, 1).Buttons);
        }
    }

    [GameFact]
    public void RecordedConsumedInputs_ReplayBitExactlyForManualSplitRace()
    {
        var dir = GameData.Dir;
        RaceSetup Setup()
        {
            var s = RaceInfo.Setup("replay", dir);
            s.Set(0x6C, 2);
            s.Set(0x78 + RaceConfig.CarRecordSize + 4, 1); // car 1 becomes the second human
            return s;
        }
        RaceSession Fresh() => new RaceLoader(dir).Load(Setup());
        var live = new RaceRunner(Fresh(), 0, withAi: false, secondPlayerIndex: 1);
        var recorder = new ReplayRecorder(live.Session.Setup);
        const int steps = 700; // passes the 512-tick countdown and compares active driving as well
        var input1 = new PlayerInput[steps];
        var input2 = new PlayerInput[steps];
        int gearAtGreen = -1;
        for (int i = 0; i < input1.Length; i++)
        {
            byte buttons1 = i is 78 or 106 or 270 or 310 or 500 or 550 ? PlayerInput.BtnGearUp : (byte)0;
            byte buttons2 = i is 90 or 118 or 290 or 340 or 520 ? PlayerInput.BtnGearUp : (byte)0;
            input1[i] = new PlayerInput((sbyte)(i % 3 == 0 ? -7 : 11), (byte)(i % 2 == 0 ? 247 : 123),
                (byte)(i % 5 == 0 ? 31 : 0), buttons1);
            input2[i] = new PlayerInput((sbyte)(i % 4 == 0 ? -11 : 7), (byte)(i % 3 == 0 ? 199 : 0),
                (byte)(i % 2 == 0 ? 71 : 0), buttons2);
            recorder.Record(input1[i], input2[i]);
            live.Step(input1[i], input2[i]);
            if (live.Tick == 512) gearAtGreen = live.Car.Gear;
        }
        Assert.Equal(steps * 2, live.Tick);
        Assert.True(live.PlayerSim.Distance > 0, "player did not move after the countdown");
        Assert.NotEqual(gearAtGreen, live.Car.Gear); // includes active-race manual shifts

        var source = new ReplayInputSource(recorder.Build(live.Tick));
        var replay = new RaceRunner(Fresh(), 0, withAi: false, secondPlayerIndex: 1);
        while (replay.Tick < source.RaceTicks64)
            replay.Step(source.ForRaceTick(replay.Tick, 0), source.ForRaceTick(replay.Tick, 1));
        Assert.Equal(live.Tick, replay.Tick);
        foreach (int car in new[] { 0, 1 })
        {
            Assert.Equal(live.Cars[car].Obj.Pos, replay.Cars[car].Obj.Pos);
            Assert.Equal(live.Cars[car].Obj.Orient, replay.Cars[car].Obj.Orient);
            Assert.Equal(live.Cars[car].Obj.SpeedApprox, replay.Cars[car].Obj.SpeedApprox);
            Assert.Equal(live.Cars[car].Obj.Gear, replay.Cars[car].Obj.Gear);
        }
    }

    [GameFact]
    public void ReplayParser_RejectsTruncatedAndMalformedPacketsAsDataErrors()
    {
        var raw = GameData.ReadRaw("REPLAY.RPY");
        Assert.Throws<InvalidDataException>(() => Replay.Parse(raw[..RaceConfig.Size]));
        Assert.Throws<InvalidDataException>(() => Replay.Parse(raw[..(Replay.StreamOffset + 2)]));
        var bad = raw.ToArray();
        bad[Replay.StreamOffset] = 0xFF;
        Assert.Throws<InvalidDataException>(() => Replay.Parse(bad));
    }

    [GameFact]
    public void Recorder_RejectsRaceStartedAfterTickZero()
    {
        var recorder = new ReplayRecorder(RaceInfo.Setup("replay", GameData.Dir), initialRaceTick: 0x200);
        Assert.Throws<InvalidOperationException>(() => recorder.Build(0x200));
    }
}
