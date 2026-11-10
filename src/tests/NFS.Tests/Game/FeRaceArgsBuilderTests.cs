using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class FeRaceArgsBuilderTests
{
    private static readonly Lazy<RaceTags> Tags = new(() => RaceTags.FromConstants());

    private static (FeConfig cfg, RaceSetup setup, int[] stream) Build(Action<FeConfig>? edit = null)
    {
        var cfg = FeConfig.Load(GameData.Dir);
        edit?.Invoke(cfg);
        var stream = new FeRaceArgsBuilder().Build(cfg);
        return (cfg, RaceArgsParser.Parse(Tags.Value, stream), stream);
    }

    [GameFact]
    public void Config_Loads()
    {
        var c = FeConfig.Load(GameData.Dir);
        Assert.Equal(FeConfig.Size, c.Raw.Length);
        Assert.InRange(c.Track, 0, 8);
        Assert.InRange(c.P1Car, 0, 15);
        Assert.InRange(c.Style, 0, 2);
    }

    [GameFact]
    public void TrackMap_FromExe()
    {
        var b = new FeRaceArgsBuilder();
        Assert.Equal([0, 2, 4, 5, 3, 6, 7, 8], Enumerable.Range(0, 8).Select(b.MapTrack)); // [ref:3131]
        Assert.Equal(0, b.MapTrack(-1)); // out of range → entry 0
    }

    [GameFact]
    public void DefaultConfig_SingleRace_FullGrid()
    {
        var (c, r, s) = Build(c => { c.GameType = 0; c.RaceType = 0; c.OpponentCount = 2; c.OpponentCar = 3; c.Style = 0; });
        Assert.Equal(0, s[^1]);
        Assert.Equal(0, r.RaceType);
        Assert.Equal(0, r.CommMode);
        Assert.Equal(c.Laps, r.NumLaps);
        Assert.Equal(new FeRaceArgsBuilder().MapTrack(c.Track), r.Track);
        Assert.Equal(8, r.NumCars);
        Assert.Equal(1, r.NumPlayerCars);
        Assert.Equal(7, r.NumOpponentCars);
        var cars = r.Cars.ToList();
        Assert.Equal(c.P1Car, cars[0].CarType);
        Assert.Equal(1, cars[0].CarClass);
        Assert.All(cars.Skip(1), x => { Assert.Equal(2, x.CarClass); Assert.Equal(3, x.CarType); });
        // personalities 0..6 (with base 0) [ref:3132]
        Assert.Equal(Enumerable.Range(0, 7), cars.Skip(1).Select(x => x.Personality));
        Assert.Equal(0, r.Style); // simulation
    }

    [GameFact]
    public void Traffic_AddsFourClass4Cars_AndArcadeStyle()
    {
        var (_, r, _) = Build(c => { c.GameType = 0; c.RaceType = 0; c.OpponentCount = 1; c.TrafficOff = 0; c.Track = 2; c.Style = 2; });
        Assert.Equal(6, r.NumCars); // human + 1 AI + 4 traffic
        Assert.Equal([1, 2, 4, 4, 4, 4], r.Cars.Select(x => x.CarClass));
        Assert.Equal(1, r.Style); // arcade → tag 5 = 1
    }

    [GameFact]
    public void Knockout_ForcesLapsAndSkill()
    {
        var (_, r, _) = Build(c => { c.GameType = 0; c.RaceType = 2; c.Skill = 0; });
        Assert.Equal(2, r.NumLaps);
        Assert.Equal(1, r.Skill);
        Assert.Equal(9, r.NumCars);
    }

    [GameFact]
    public void SplitScreen_TwoHumans()
    {
        var (c, r, _) = Build(c => { c.GameType = 1; c.RaceType = 0; c.OpponentCount = 0; c.TrafficOff = 1; });
        Assert.Equal(2, r.NumPlayerCars);
        Assert.Equal(2, r.NumCars);
        Assert.Equal(c[0x3A], r.Car(1).CarType);
    }

    [GameFact]
    public void RoundTrip_ReplayValues()
    {
        var rpy = NFS.Data.FrontEnd.Replay.Parse(GameData.ReadRaw("REPLAY.RPY"));
        var want = RaceArgsParser.Parse(Tags.Value, RaceArgsParser.StreamFromBlock(Tags.Value, rpy.Config.Raw));
        var b = new FeRaceArgsBuilder();
        int menuTrack = Enumerable.Range(0, 8).Single(i => b.MapTrack(i) == want.Track);
        var (_, r, _) = Build(c =>
        {
            c.GameType = 0; c.RaceType = (byte)want.RaceType; c.Track = (ushort)menuTrack; c.Laps = (byte)want.NumLaps;
            c.Backward = (byte)(want.Backward ? 1 : 0); c.Mirrored = (byte)(want.Mirrored ? 1 : 0);
            c.P1Car = (byte)want.Car(0).CarType; c.OpponentCount = 2; c.TrafficOff = 1;
            c.Style = want.Style == 0 ? (byte)0 : (byte)2;
        });
        Assert.Equal(want.Track, r.Track);
        Assert.Equal(want.NumLaps, r.NumLaps);
        Assert.Equal(want.NumCars, r.NumCars);
        Assert.Equal(want.NumPlayerCars, r.NumPlayerCars);
        Assert.Equal(want.Backward, r.Backward);
        Assert.Equal(want.Style, r.Style);
        Assert.Equal(want.Car(0).CarType, r.Car(0).CarType);
        Assert.Equal(want.Cars.Select(x => x.CarClass), r.Cars.Select(x => x.CarClass));
        for (int i = 0; i < r.NumCars; i++) Assert.Equal(want.GridSlot(i), r.GridSlot(i));
    }

    /// <summary>(race type 0): opponent car 12/13/14 = class C/B/A, models from the fixed tables by opponent index (no RNG). [ref:3133]</summary>
    [GameFact]
    public void ClassOpponent_UsesTheClassTableByIndex_WithoutRandomness()
    {
        short[][] tables =
        [
            [.. NFS.Core.Constants.FrontEndConstants.TournamentOpponentsC],
            [.. NFS.Core.Constants.FrontEndConstants.TournamentOpponentsB],
            [.. NFS.Core.Constants.FrontEndConstants.TournamentOpponentsA],
        ];
        for (int cls = 0; cls < 3; cls++)
        {
            var (c, r, _) = Build(c => { c.GameType = 0; c.RaceType = 0; c.OpponentCount = 2; c.OpponentCar = (byte)(12 + cls); });
            var types = r.Cars.Skip(1).Select(x => x.CarType).ToArray();
            Assert.Equal(7, types.Length);
            Assert.Equal(tables[cls].Take(7).Select(m => (int)m), types);
            var (_, again, _) = Build(c => { c.GameType = 0; c.RaceType = 0; c.OpponentCount = 2; c.OpponentCar = (byte)(12 + cls); });
            Assert.Equal(types, again.Cars.Skip(1).Select(x => x.CarType));
        }
    }

    [GameFact]
    public void ClassOpponent_ClassIsChosenByTheOpponentSetting_NotByThePlayersCar_AndIndexIsCapped()
    {
        var cfg = FeConfig.Load(GameData.Dir);
        var b = new FeRaceArgsBuilder();
        cfg.P1Car = 2; // a class A player car does not matter for race type 0
        cfg.OpponentCar = 12;
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.TournamentOpponentsC[3], b.ClassOpponentCar(cfg, 3));
        cfg.OpponentCar = 13;
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.TournamentOpponentsB[3], b.ClassOpponentCar(cfg, 3));
        cfg.OpponentCar = 14;
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.TournamentOpponentsA[3], b.ClassOpponentCar(cfg, 3));
        Assert.Equal(NFS.Core.Constants.FrontEndConstants.TournamentOpponentsA[10], b.ClassOpponentCar(cfg, 25));
        Assert.Equal(0, b.SequenceIndex); // the table lookup does not touch the traffic sequence
    }
}
