using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NFS.Core;
using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Core;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Stage 5a.1: <see cref="RaceHost"/> must step exactly like a bare <see cref="RaceRunner"/> fed with the same inputs
/// (state of every car, dynamic objects, tick, lap/finish state), record the applied inputs and capture the results once.</summary>
public class RaceHostTests
{
    /// <summary>Deterministic hash of every public field of an object graph (CarObj incl. locator, vectors, arrays).</summary>
    static string Dump(object? o, int depth = 0)
    {
        var sb = new StringBuilder();
        Append(sb, o, 0);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    static void Append(StringBuilder sb, object? o, int depth)
    {
        if (o == null) { sb.Append("null;"); return; }
        var t = o.GetType();
        if (t.IsPrimitive || t.IsEnum || o is string) { sb.Append(o).Append(';'); return; }
        if (depth > 4) return;
        if (o is IEnumerable e)
        {
            sb.Append('[');
            foreach (var x in e) Append(sb, x, depth + 1);
            sb.Append(']');
            return;
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) { sb.Append(f.Name).Append('='); Append(sb, f.GetValue(o), depth + 1); }
    }

    static string World(RaceRunner r) =>
        string.Join("|", r.Session.Cars.Select(c => Dump(c.Obj))) + "#" + r.Tick + "#" + r.Laps.RaceOver + "#" + r.CarHits +
        "#" + string.Join(",", r.DynObjects.Knocked.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")) + "#" + r.ManualResets + "#" + r.ManualResets2;

    /// <summary>Scripted human input: throttle, weaving steer, gear presses, handbrake, one reset request.</summary>
    static PlayerInput Script(int step, int player = 0)
    {
        int s = step + player * 7;
        sbyte steer = (sbyte)(Math.Sin(s / 9.0) * 90);
        byte buttons = 0;
        if (s % 40 == 5) buttons |= PlayerInput.BtnGearUp;
        if (s % 97 == 50) buttons |= PlayerInput.BtnGearDown;
        if (s % 53 > 48) buttons |= PlayerInput.BtnHandbrake;
        if (s == 1100) buttons |= PlayerInput.BtnReset;
        return new PlayerInput(steer, (byte)(s % 200 < 150 ? 255 : 80), (byte)(s % 200 >= 180 ? 200 : 0), buttons);
    }

    [GameFact]
    public void LiveSteps_MatchBareRunnerStateAndRecordAppliedInputs()
    {
        var direct = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var host = new RaceHost(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        var rec = new ReplayRecorder(direct.Session.Setup, direct.Tick);
        Assert.Equal(World(direct), World(host.Runner));
        var initial = World(host.Runner);
        Assert.True(initial.Length > 300);
        int resetFlags = 0, steps = 1300;
        for (int i = 0; i < steps; i++)
        {
            var input = Script(i);
            rec.Record(input);
            direct.Step(input);
            Assert.True(host.Step(input, null, (r1, r2) => { if (r1) resetFlags++; Assert.False(r2); }));
            if (i % 50 == 0 || i == steps - 1) Assert.Equal(World(direct), World(host.Runner));
            Assert.Equal(direct.Tick, host.Runner.Tick);
        }
        Assert.NotEqual(initial, World(host.Runner));
        Assert.Equal(steps, host.Steps);
        Assert.Equal(steps * 2, host.Runner.Tick);     // one host step = two 64 Hz ticks
        Assert.True(resetFlags > 0);
        Assert.Equal(direct.ManualResets, resetFlags); // callback flags mirror the runner's reset counter
        Assert.Equal(steps, host.Recorder.Samples);
        var expected = new ReplayInputSource(rec.Build(direct.Tick));
        var applied = new ReplayInputSource(host.Recorder.Build(host.Runner.Tick));
        Assert.Equal(expected.Length(), applied.Length());
        for (int i = 0; i < expected.Length(); i++) Assert.Equal(expected.At(i), applied.At(i));
        Assert.Null(host.Results);
    }

    [GameFact]
    public void Playback_IgnoresSuppliedInputsAndFollowsTheRecordingLikeDirectStepping()
    {
        var rpy = Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var src = new ReplayInputSource(rpy);
        var direct = new RaceRunner(TrackSessions.FreshReplay(), 0, withAi: true);
        direct.ReplayCam = new ReplayCamera(direct.Rand); // a playback host runs the replay camera director (shared) like the original [ref:3209]
        var host = new RaceHost(TrackSessions.FreshReplay(), 0, withAi: true, playbackSource: new ReplayInputSource(rpy));
        Assert.NotNull(host.Runner.ReplayCam);
        int n = 0;
        while (direct.Tick < src.RaceTicks64 && !direct.Laps.RaceOver)
        {
            direct.Step(src.ForRaceTick(direct.Tick));
            Assert.True(host.Step(new PlayerInput(127, 255, 255, 0xFF)));   // garbage "keyboard" must be ignored
            if (++n % 1500 == 0) Assert.Equal(World(direct), World(host.Runner));
            Assert.Equal(direct.Tick, host.Runner.Tick);
        }
        Assert.Equal(World(direct), World(host.Runner));
        host.EndPlaybackIfOutOfInput();
        host.EndPlaybackIfFinished();
        Assert.True(host.PlaybackEnded);
        Assert.Null(host.Results);          // playback never captures results
        Assert.Equal(0, host.Recorder.Samples); // and never records
        Assert.False(host.Step(default));
        Assert.Equal(n, host.Steps);
        Assert.True(n > 1000);
    }

    [GameFact]
    public void Finish_CapturesResultsOnceAndFreezesTheSimulation()
    {
        var direct = new RaceRunner(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true, countdown: false);
        var host = new RaceHost(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true, countdown: false);
        direct.SetLaps(2); host.Runner.SetLaps(2);
        for (int i = 0; i < 150; i++)
        {
            if (i == 100) { direct.Car.Lap = 1; host.Runner.Car.Lap = 1; }
            if (i == 140) { direct.Car.Lap = 2; host.Runner.Car.Lap = 2; }
            direct.Step(default); host.Step(default);
        }
        int guard = 0;
        while (!direct.Laps.RaceOver && guard++ < 5000) { direct.Step(default); host.Step(default); }
        Assert.True(direct.Laps.RaceOver);
        Assert.Equal(World(direct), World(host.Runner));
        var results = host.Results!;
        Assert.NotNull(results);
        var expected = RaceResults.Capture(direct);
        Assert.Equal(expected.Racers.Count, results.Racers.Count);
        for (int i = 0; i < expected.Racers.Count; i++)
        {
            var a = expected.Racers[i]; var b = results.Racers[i];
            Assert.Equal((a.Place, a.CarIndex, a.Model, a.Human, a.Estimated, a.RaceTicks, a.BestLapTicks, a.RaceState, a.Driver, a.TopSpeedFx),
                (b.Place, b.CarIndex, b.Model, b.Human, b.Estimated, b.RaceTicks, b.BestLapTicks, b.RaceState, b.Driver, b.TopSpeedFx));
            Assert.Equal(a.LapTicks, b.LapTicks);
        }
        int tick = host.Runner.Tick, steps = host.Steps, samples = host.Recorder.Samples;
        var before = World(host.Runner);
        Assert.False(host.Step(new PlayerInput(0, 255, 0, PlayerInput.BtnReset)));
        Assert.Same(results, host.Results);
        Assert.Equal((tick, steps, samples, before), (host.Runner.Tick, host.Steps, host.Recorder.Samples, World(host.Runner)));
    }

    [GameFact]
    public void Pause_SkipsStepsWithoutTouchingStateOrRecording()
    {
        var host = new RaceHost(TrackSessions.FreshReplay(), 0, automatic: true, withAi: true);
        for (int i = 0; i < 20; i++) host.Step(Script(i));
        host.Paused = true;
        var before = World(host.Runner);
        int samples = host.Recorder.Samples, steps = host.Steps;
        Assert.False(host.Step(Script(21)));
        Assert.Equal((before, samples, steps), (World(host.Runner), host.Recorder.Samples, host.Steps));
        host.Paused = false;
        Assert.True(host.Step(Script(21)));
        Assert.Equal(samples + 1, host.Recorder.Samples);
    }

    static RaceSession SplitSession()
    {
        var cfg = FeConfig.Load(GameData.Dir);
        cfg.GameType = 1; cfg.RaceType = 0; cfg.OpponentCount = 2; cfg.TrafficOff = 1; cfg.Track = 1;
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), new FeRaceArgsBuilder().Build(cfg));
        return new RaceLoader(GameData.Dir).Load(setup);
    }

    [GameFact]
    public void SplitScreen_TwoHumanInputsMatchBareRunnerAndAreBothRecorded()
    {
        var direct = new RaceRunner(SplitSession(), 0, withAi: true, secondPlayerIndex: 1);
        var host = new RaceHost(SplitSession(), 0, withAi: true, secondPlayerIndex: 1);
        var rec = new ReplayRecorder(direct.Session.Setup, direct.Tick);
        Assert.NotNull(host.Runner.Player2);
        int steps = 700;
        for (int i = 0; i < steps; i++)
        {
            PlayerInput a = Script(i), b = Script(i, 1);
            rec.Record(a, b);
            direct.Step(a, b);
            Assert.True(host.Step(a, b));
            if (i % 70 == 0 || i == steps - 1) Assert.Equal(World(direct), World(host.Runner));
        }
        Assert.Equal(2, host.Recorder.Players);
        var expected = new ReplayInputSource(rec.Build(direct.Tick));
        var applied = new ReplayInputSource(host.Recorder.Build(host.Runner.Tick));
        for (int p = 0; p < 2; p++)
            for (int i = 0; i < steps; i++) Assert.Equal(expected.At(i, p), applied.At(i, p));
    }
}
