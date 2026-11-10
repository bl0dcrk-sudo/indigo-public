using NFS.Data.FrontEnd;
using NFS.Game.FrontEnd;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Cheat codes typed in the menu: the list from the original game, the effect of each code, buffer reset, screen/mode gates [ref:3125]
/// persistent unlocks and carrying the effects over to the race arguments. [ref:3126]</summary>
public class CheatCodesTests
{
    private static MenuController Menu() => MenuController.Load(GameData.Dir);
    private static void Type(MenuController m, string s) { foreach (char ch in s) m.TypeChar(ch); }
    [GameFact]
    public void CodeTable_IsDecodedFromTheExe()
    {
        Assert.Equal(["FZR2000", "TOMBSTONE", "BOMBER", "HOLLYWOOD", "PIONEER", "SLIP", "SLOT", "MAD", "RAIN", "CHASE",
            "KCJONES", "RUSHHOUR", "REXHOUR", "MADLAND", "SCHOOLZONE", "VIP", "ROADRAGE"], Menu().Cheats.Codes);
    }

    [GameFact]
    public void Fzr2000_UnlocksAllThreeCars_AndSelectsFzr_AndPersists()
    {
        var m = Menu(); int persisted = 0; m.PersistRequested += () => persisted++;
        Assert.Equal(0, m.Config[0x1D]);
        Type(m, "fzr2000");   // letter case does not matter
        Assert.Equal(3, m.Config[0x1D]);
        Assert.Equal(0xE, m.Config.P1Car);
        Assert.Equal(1, persisted);
    }

    [GameFact]
    public void Tombstone_And_Bomber_UnlockOnlyWhenLevelIsLower()
    {
        var m = Menu();
        Type(m, "BOMBER");
        Assert.Equal(1, m.Config[0x1D]); Assert.Equal(0xC, m.Config.P1Car);
        Type(m, "TOMBSTONE");
        Assert.Equal(2, m.Config[0x1D]); Assert.Equal(0xD, m.Config.P1Car);
        Type(m, "BOMBER");                    // level 2: BOMBER requires 0
        Assert.Equal(2, m.Config[0x1D]); Assert.Equal(0xD, m.Config.P1Car);
        m.Config.P1Car = 3; m.Config[0x1D] = 3;
        Type(m, "TOMBSTONE"); Type(m, "FZR2000");
        Assert.Equal(3, m.Config[0x1D]); Assert.Equal(3, m.Config.P1Car);
    }

    [GameFact]
    public void Hollywood_UnlocksMonolithicTrack_SelectsIt_AndPersists()
    {
        var m = Menu(); int persisted = 0; m.PersistRequested += () => persisted++;
        Type(m, "HOLLYWOOD");
        Assert.Equal(1, m.Config[0x1C] & 1);
        Assert.Equal(7, m.Config.Track);          // does not move the track back, because it is unlocked [ref:3127]
        Assert.Equal(1, persisted);
    }

    [GameFact]
    public void Slot_UnlocksSlotCarOptions()
    {
        var m = Menu();
        Type(m, "SLOT");
        Assert.Equal(1, m.Config[0x1E]);
    }

    [GameFact]
    public void MadAndKcJones_AreAcceptedButDoNothing()
    {
        var m = Menu(); var before = m.Config.ToBytes();
        Type(m, "MAD"); Type(m, "KCJONES");
        Assert.Equal(before, m.Config.ToBytes());
        Assert.False(m.Cheats.Effects.Any);
    }

    [GameFact]
    public void SessionCodes_SetTheirFlagsAndModels()
    {
        var m = Menu();
        Type(m, "RAIN"); Assert.True(m.Cheats.Effects.Rain);
        Type(m, "CHASE"); Assert.True(m.Cheats.Effects.Chase);
        Type(m, "SLIP"); Assert.True(m.Cheats.Effects.Slip);
        Type(m, "ROADRAGE"); Assert.True(m.Cheats.Effects.RoadRage);
        Type(m, "RUSHHOUR"); Assert.True(m.Cheats.Effects.Crowd); Assert.Equal(0, m.Cheats.Effects.ForcedModel);
        Assert.False(m.Cheats.Effects.Pioneer);
        Type(m, "REXHOUR"); Assert.Equal(0, m.Cheats.Effects.ForcedModel); // the 'R' ending RUSHHOUR consumes the first 'R' (no retry)
        Type(m, "-REXHOUR"); Assert.Equal(0x2B, m.Cheats.Effects.ForcedModel);
        Type(m, "MADLAND"); Assert.Equal(0x2A, m.Cheats.Effects.ForcedModel); Assert.True(m.Cheats.Effects.Pioneer);
        Type(m, "SCHOOLZONE"); Assert.Equal(0x14, m.Cheats.Effects.ForcedModel);
        Type(m, "VIP"); Assert.Equal(0x23, m.Cheats.Effects.ForcedModel);
    }

    [GameFact]
    public void Pioneer_ShowsTheDialogText_AndSetsFlag4()
    {
        var m = Menu();
        Type(m, "PIONEER");
        Assert.True(m.Cheats.Effects.Pioneer);
        Assert.False(string.IsNullOrEmpty(m.LastMessage));
        Assert.Equal(4, m.Cheats.Effects.Flags(m.Config) & 4);
    }

    [GameFact]
    public void GoCode_ForcesModel_WithOriginalRemapsAndRange()
    {
        var cases = new (string Code, int Model)[] { ("GO18", 0x14), ("GO22", 0x14), ("GO24", 0x14), ("GO25", 0x14), ("GO29", 0x14),
            ("GO30", 0x14), ("GO32", 0x14), ("GO19", 19), ("GO20", 20), ("GO34", 34), ("go43", 43), ("GO51", 51) };
        foreach (var (code, model) in cases)
        {
            var m = Menu(); Type(m, code);
            Assert.Equal(model, m.Cheats.Effects.ForcedModel);
        }
        foreach (string code in new[] { "GO17", "GO00", "GO52", "GO99", "GOX1", "GO1X", "G0O22", "GGO22" })
        {
            var m = Menu(); Type(m, code);
            Assert.Equal(0, m.Cheats.Effects.ForcedModel);
        }
    }

    [GameFact]
    public void WrongOrTooLongInput_ResetsTheBuffer_WithoutRetryingTheCharacterAsAStart()
    {
        var m = Menu();
        Type(m, "FZR200"); Type(m, "X"); Type(m, "0");   // interrupted by a wrong character
        Assert.Equal(0, m.Config[0x1D]);
        Type(m, "FFZR2000");                              // the second 'F' resets the progress and is not a new start
        Assert.Equal(0, m.Config[0x1D]);
        Type(m, "FZR20000");                              // an extra character after a full code is harmless
        Assert.Equal(3, m.Config[0x1D]);
        m.Config[0x1D] = 0;
        Type(m, "FZR2000"); Type(m, "FZR2000");           // the code can be typed again right after
        Assert.Equal(3, m.Config[0x1D]);
    }

    [GameFact]
    public void Codes_DoNotWorkOutsideSingleRaceOfLocalGames()
    {
        var m = Menu(); m.Config.RaceType = 1;
        Type(m, "FZR2000"); Type(m, "REXHOUR"); Type(m, "GO30");
        Assert.Equal(0, m.Config[0x1D]); Assert.False(m.Cheats.Effects.Any);
        m.Config.RaceType = 0; m.Config.GameType = 2;
        Type(m, "FZR2000");
        Assert.Equal(0, m.Config[0x1D]);
        m.Config.GameType = 1;                             // split screen is allowed
        Type(m, "FZR2000");
        Assert.Equal(3, m.Config[0x1D]);
    }

    [GameFact]
    public void Codes_AreIgnoredWhileARaceIsRunning()
    {
        var m = Menu(); m.Config.Track = 2; m.Config.Style = 2;
        m.StartRace();
        Assert.Equal(MenuStatus.Race, m.Status);
        Type(m, "FZR2000");
        Assert.Equal(0, m.Config[0x1D]);
    }

    [GameFact]
    public void ForcedModel_ReplacesThePlayersCar_AndTrafficCrowdIs11_10_9()
    {
        var m = Menu(); m.Config.Track = 2; m.Config.Style = 2; m.Config.OpponentCount = 0; m.Config.TrafficOff = 0;
        Type(m, "REXHOUR");
        var s = m.BuildSetup();
        Assert.Equal(0x2B, s.Car(0).CarType);                       // the player drives the T-Rex (w) [ref:3128]
        var traffic = s.Cars.Where(c => c.CarClass == 4).ToList();
        Assert.Equal(11, traffic.Count);
        Assert.All(traffic, c => Assert.Equal(0x2B, c.CarType));
        Assert.Equal(0x400 | 0x1000, s.Get(0x20) & (0x400 | 0x1000)); // denser traffic and model skin > 0x25
        Assert.Equal(12, s.NumCars);

        var one = Menu(); one.Config.Track = 2; one.Config.OpponentCount = 1; one.Config.TrafficOff = 0;
        Type(one, "RUSHHOUR");
        var s1 = one.BuildSetup();
        Assert.Equal(10, s1.Cars.Count(c => c.CarClass == 4));
        Assert.Equal(0x400, s1.Get(0x20) & (0x400 | 0x1000));        // without a forced model: no 0x1000
        Assert.NotEqual(0x2B, s1.Car(0).CarType);

        var plain = Menu(); plain.Config.Track = 2; plain.Config.OpponentCount = 1; plain.Config.TrafficOff = 0;
        Assert.Equal(4, plain.BuildSetup().Cars.Count(c => c.CarClass == 4));
    }

    [GameFact]
    public void Crowd_UsesFourModelColourPairs_RepeatedModuloFour()
    {
        var m = Menu(); m.Config.Track = 3; m.Config.OpponentCount = 0; m.Config.TrafficOff = 0;
        Type(m, "RUSHHOUR");
        var s = m.BuildSetup();
        var t = s.Cars.Where(c => c.CarClass == 4).ToList();
        Assert.Equal(11, t.Count);
        for (int i = 4; i < t.Count; i++) { Assert.Equal(t[i - 4].CarType, t[i].CarType); Assert.Equal(t[i - 4].Colour, t[i].Colour); }
        Assert.Contains(t[0].CarType, new[] { 18, 23, 24, 26, 27, 29, 31, 32 }); // the menu track 3 pool [ref:3129]
    }

    [GameFact]
    public void CheatFlags_ReachTheRaceConfig_OnlyForSingleRace()
    {
        var m = Menu(); m.Config.Track = 2;
        Type(m, "RAIN"); Type(m, "CHASE"); Type(m, "SLIP"); Type(m, "ROADRAGE"); Type(m, "PIONEER");
        Assert.Equal(0x40 | 0x100 | 8 | 0x800 | 4, m.BuildSetup().Get(0x20));
        // switching to the tournament after typing: the arguments have flags 0, and the effects are zeroed [ref:3130]
        m.Config.RaceType = 1;
        Assert.Equal(0, m.BuildSetup().Get(0x20));
        Assert.False(m.Cheats.Effects.Any);
    }

    [GameFact]
    public void ForcedModelsLoadAndDrive_AsPlayerAndAsTraffic()
    {
        foreach (string code in new[] { "REXHOUR", "MADLAND", "SCHOOLZONE", "VIP", "GO34" })
        {
            var m = Menu(); m.Config.Track = 2; m.Config.Style = 2; m.Config.OpponentCount = 0; m.Config.TrafficOff = 0;
            Type(m, code);
            var s = new RaceLoader(GameData.Dir).Load(m.BuildSetup());
            Assert.NotNull(s.Cars[0].Specs);
            Assert.NotNull(s.Cars[0].Geo);
            var run = new RaceRunner(s, 0, automatic: true, withAi: true);
            for (int t = 0; t < 32 * 3; t++) run.Step(default);
            Assert.True(run.Tick > 0);
        }
    }

    [GameFact]
    public void Unlocks_ArePersistedInUserSettings()
    {
        var m = Menu();
        Type(m, "FZR2000"); Type(m, "HOLLYWOOD"); Type(m, "SLOT");
        var restored = Menu().Config;
        UserSettings.From(m.Config).ApplyTo(restored);
        Assert.Equal(3, restored[0x1D]); Assert.Equal(1, restored[0x1C] & 1); Assert.Equal(1, restored[0x1E]);
        Assert.Equal(0xE, restored.P1Car);
        var path = Path.Combine(Path.GetTempPath(), $"nfs-cheat-{Guid.NewGuid():N}.json");
        try
        {
            UserSettings.From(m.Config).Save(path);
            var fromDisk = Menu().Config;
            UserSettings.Load(path).ApplyTo(fromDisk);
            Assert.Equal(3, fromDisk[0x1D]); Assert.Equal(1, fromDisk[0x1E]);
        }
        finally { File.Delete(path); }
    }

    [GameFact]
    public void SessionEffects_DoNotSurviveTheReturnToTheFrontEnd()
    {
        var m = Menu(); m.Config.Track = 2; m.Config.Style = 2;
        Type(m, "RAIN"); Type(m, "REXHOUR");
        m.StartRace();
        var results = new RaceResults(Enumerable.Range(0, m.Session!.Cars.Count).Select(i => new RacerResult(i + 1, i,
            $"RACER {i + 1}", i == 0, i != 0, 6000 + i * 300, [], null)).ToArray());
        m.CompleteRace(results);
        Assert.False(m.Cheats.Effects.Any);
    }
}
