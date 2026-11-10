using NFS.Core;
using NFS.Data;
using NFS.Data.FrontEnd;
using NFS.Desktop;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Race option flags of the cheat codes reach the simulation / render: SLIP (8) slippery grip class, CHASE (0x100) AI
/// targets the leading human, RAIN (0x40) weather overlay. Without the cheats the race is unchanged and deterministic.
/// NFS_FLAGS_REVIEW_DIR writes the RAIN review PNG.</summary>
public class RaceFlagEffectsTests
{
    static RaceDrive Drive(int menuTrack, CheatEffects? cheats = null)
    {
        var dir = GameFiles.FindGameDir();
        var cfg = FeConfig.Load(dir);
        cfg.GameType = 0; cfg.RaceType = 0; cfg.OpponentCount = 1; cfg.OpponentCar = 0; cfg.TrafficOff = 1;
        cfg.Track = (ushort)menuTrack; cfg.Style = 2; cfg.Backward = 0; cfg.Mirrored = 0;
        var builder = new FeRaceArgsBuilder() { Cheats = cheats ?? new CheatEffects() };
        var setup = RaceArgsParser.Parse(RaceTags.FromConstants(), builder.Build(cfg));
        return new RaceDrive(new RaceScene(new RaceLoader(dir).Load(setup), dir), countdown: false, withAi: true, automatic: true);
    }

    static (Vec3 Pos, Vec3 Vel, int Grip) Run(int seconds, CheatEffects? cheats = null)
    {
        var d = Drive(2, cheats);
        d.RunAutopilot(seconds);
        return (d.Car.Pos, d.Car.Vel, d.Car.GripClass);
    }

    [GameDataFact]
    public void CheatsReachTheRaceOptions_AndNothingElseChangesWithoutThem()
    {
        Assert.Equal(0, Drive(2).Runner.GameOptions & (8 | 0x40 | 0x100 | 0x800));
        Assert.Equal(8, Drive(2, new CheatEffects { Slip = true }).Runner.GameOptions & 8);
        var all = Drive(2, new CheatEffects { Slip = true, Rain = true, Chase = true, RoadRage = true }).Runner.GameOptions;
        Assert.Equal(8 | 0x40 | 0x100 | 0x800, all & (8 | 0x40 | 0x100 | 0x800));
    }

    [GameDataFact]
    public void Slip_ChangesThePlayersGripAndMotion_Deterministically()
    {
        var a = Run(6); var b = Run(6);
        Assert.Equal(a, b);                                   // without flags: repeatable
        var s1 = Run(6, new CheatEffects { Slip = true }); var s2 = Run(6, new CheatEffects { Slip = true });
        Assert.Equal(s1, s2);
        Assert.Equal(9, s1.Grip);                             // table: every surface grip class 9 [ref:2883]
        Assert.NotEqual(9, a.Grip);
        Assert.NotEqual(a.Pos, s1.Pos);                       // slippery tyres: another path
    }

    [GameDataFact]
    public void Chase_OpponentsTargetTheLeadingHuman_OnlyWithTheFlag()
    {
        int Seen(CheatEffects? cheats)
        {
            var d = Drive(2, cheats);
            int seen = 0;
            for (int i = 0; i < 100; i++)
            {
                // the beginner opponent drives away; keep it 2 slices behind the human so it is inside the chase window (-1..+4)
                foreach (var c in d.Runner.Cars)
                    if ((c.Obj.Flags & CarFlags.Opponent) != 0) c.Obj.Progress = (ushort)(d.Car.Progress - 2);
                d.RunAutopilot(1.0 / 32);
                foreach (var c in d.Runner.Cars)
                    if ((c.Obj.Flags & CarFlags.Opponent) != 0 && c.Obj.PsychoTarget == d.Car) seen++;
            }
            return seen;
        }
        Assert.Equal(0, Seen(null));
        Assert.True(Seen(new CheatEffects { Chase = true }) > 0);
    }

    [GameDataFact]
    public void Rain_DrawsGreyStreaksInTheZone_NotOutsideOrWithoutTheFlag()
    {
        int menu = Enumerable.Range(0, 8).First(t => Drive(t, new CheatEffects { Rain = true }).Weather.Zones.Count > 0); // tracks 0, 5, 7 have zones
        var d = Drive(menu, new CheatEffects { Rain = true });
        Assert.True(d.Weather.Enabled);
        var z = d.Weather.Zones[0];
        var fb = new FrameBuffer();
        d.Car.Trk.Slice = z.Center;                           // inside the zone (no tunnel there is not guaranteed; the zone is mid-track)
        d.SnapToHost();
        d.Render(fb);
        var dry = Drive(menu);
        Assert.False(dry.Weather.Enabled);
        var fb2 = new FrameBuffer();
        dry.Car.Trk.Slice = z.Center;
        dry.SnapToHost();
        dry.Render(fb2);
        int grey = 0;
        for (int y = 0; y < fb.Height; y++)
            for (int x = 0; x < fb.Width; x++) if (fb.Get(x, y) == (uint)Weather.Gray && fb2.Get(x, y) != (uint)Weather.Gray) grey++;
        Assert.True(d.Weather.Intensity > 0 && grey > 20, $"intensity {d.Weather.Intensity:X}, grey pixels {grey}");
        if (Environment.GetEnvironmentVariable("NFS_FLAGS_REVIEW_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 3; i++) d.Render(fb);
            fb.SavePng(Path.Combine(dir, "rain.png"));
        }
    }

    [GameDataFact]
    public void Rain_DropsOnTheWindscreenOnlyInTheCockpitView()
    {
        int menu = Enumerable.Range(0, 8).First(t => Drive(t, new CheatEffects { Rain = true }).Weather.Zones.Count > 0);
        FrameBuffer Shot(CheatEffects? c, int mode)
        {
            var d = Drive(menu, c);
            var pose = RenderPose.From(d.Car.Pos, d.Car.Orient);
            while (d.Cam.Mode != mode) d.Cam.Cycle(pose, d.Car);
            d.Car.Trk.Slice = d.Weather.Zones.Count > 0 ? d.Weather.Zones[0].Center : 0;
            d.SnapToHost();
            var fb = new FrameBuffer();
            d.Render(fb);
            d.Render(fb);
            return fb;
        }
        static int Diff(FrameBuffer a, FrameBuffer b)
        {
            int n = 0;
            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.Get(x, y) != b.Get(x, y)) n++;
            return n;
        }
        var rain = new CheatEffects { Rain = true };
        var wet = Shot(rain, 0);
        int cockpit = Diff(wet, Shot(null, 0)), chase = Diff(Shot(rain, 2), Shot(null, 2));
        Assert.True(cockpit > chase + 20, $"cockpit {cockpit} chase {chase}");
        if (Environment.GetEnvironmentVariable("NFS_DROPS_REVIEW_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            wet.SavePng(Path.Combine(dir, "cockpit_rain.png"));
        }
    }
}
