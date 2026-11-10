using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.FrontEnd;
using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Mods;

/// <summary>openspec modding-api-cars 4.4: the car lists of the menu (player car, opponent car, showcase) with the mod car after the original
/// ones, its colour from car.json, the Duster in the opponent pools, the selection kept by car key in the settings. Uses the repo mod
/// <c>mods/indigo.duster</c>; mod cars are always available (no unlock bit, cheat codes or settings byte involved).</summary>
public sealed class ModMenuTests
{
    const string DusterKey = "indigo.duster/duster";
    const int Duster = CarCatalog.VanillaCount;   // first mod number

    static string RepoMods => Path.GetFullPath(Path.Combine(GameData.ProjectRoot, "..", "mods"));

    static CarCatalog DusterCatalog()
    {
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(RepoMods, diag);
        var (catalog, problems) = CarCatalog.Build([VanillaCarSource.FromGameDir(GameData.Dir), new ModCarSource(mods, diag)]);
        Assert.Empty(problems);
        return catalog;
    }

    static MenuController Menu(bool mods = true)
    {
        var menu = MenuController.Load(GameData.Dir);
        if (mods) menu.Cars = DusterCatalog();
        return menu;
    }

    static void Keys(MenuController menu, params MenuKey[] keys) { foreach (var key in keys) menu.HandleKey(key); }

    static void Select(MenuController menu, string text)
    {
        for (int i = 0; i < menu.CurrentScreen.Items.Count; i++)
        {
            if (menu.CurrentScreen.Items[menu.SelectedIndex].Text == text) return;
            menu.HandleKey(MenuKey.Down);
        }
        throw new Exception($"Cannot select {text} on {menu.CurrentScreen.Id}");
    }

    static void Open(MenuController menu, string text) { Select(menu, text); menu.HandleKey(MenuKey.Enter); }

    static string Item(MenuController menu) => menu.ItemText(menu.CurrentScreen.Items[menu.SelectedIndex]);

    /// <summary>PLAYER ONE with the car item selected.</summary>
    static MenuController PlayerOne(bool mods = true)
    {
        var menu = Menu(mods);
        Open(menu, "player one");
        Select(menu, "car");
        return menu;
    }

    [GameFact]
    public void PlayerCar_ModCarFollowsTheOriginalCarsAndTheListWraps()
    {
        var menu = PlayerOne();
        Assert.Equal(0, menu.Config.P1Car);
        Keys(menu, MenuKey.Left);   // before the first original car = the last of the list = the mod car
        Assert.Equal(Duster, menu.Config.P1Car);
        Assert.Equal("car: Duster", Item(menu));
        Keys(menu, MenuKey.Right);
        Assert.Equal(0, menu.Config.P1Car);
        for (int i = 0; i < MenuController.ShowcaseCarCount; i++) Keys(menu, MenuKey.Right);
        Assert.Equal(Duster, menu.Config.P1Car);   // after the 12 original cars comes the mod car
        Keys(menu, MenuKey.Left);
        Assert.Equal(MenuController.ShowcaseCarCount - 1, menu.Config.P1Car);
    }

    [GameFact]
    public void PlayerCar_WithoutModsIsTheOriginalList()
    {
        var menu = PlayerOne(mods: false);
        Keys(menu, MenuKey.Left);
        Assert.Equal(MenuController.ShowcaseCarCount - 1, menu.Config.P1Car);
        Keys(menu, MenuKey.Right);
        Assert.Equal(0, menu.Config.P1Car);
    }

    [GameFact]
    public void PlayerCar_ModCarIsUnlockedWithoutCheatsOrUnlockBits()
    {
        var menu = PlayerOne();
        byte unlocks = menu.Config[0x1D];
        Keys(menu, MenuKey.Left);
        Assert.Equal(Duster, menu.Config.P1Car);
        Assert.Equal(unlocks, menu.Config[0x1D]);   // choosing the mod car neither needs nor changes an unlock bit
    }

    [GameFact]
    public void PlayerColour_ComesFromCarJsonPerCarAndSurvivesSwitchingCars()
    {
        var menu = PlayerOne();
        Keys(menu, MenuKey.Left);
        var duster = menu.ModCarOf(Duster)!;
        Assert.True(duster.Colors.Count > 1);
        Select(menu, "color");
        Assert.StartsWith("color: " + duster.Colors[0].Name("en"), Item(menu));
        Keys(menu, MenuKey.Right, MenuKey.Right);
        Assert.Equal(2, menu.ModColour(0, duster));
        Assert.Equal("color: " + duster.Colors[2].Name("en"), Item(menu));
        Keys(menu, MenuKey.Left, MenuKey.Left, MenuKey.Left);   // wraps below 0
        Assert.Equal(duster.Colors.Count - 1, menu.ModColour(0, duster));
        Keys(menu, MenuKey.Right);
        // the original config bytes of the colours are not touched
        var before = menu.Config.P1Colours.ToArray();
        Keys(menu, MenuKey.Right);
        Assert.Equal(before, menu.Config.P1Colours.ToArray());
        // the race gets the chosen colour index
        Assert.Equal(1, menu.ModColour(0, duster));
        var car = menu.BuildSetup().Car(0);
        Assert.Equal(Duster, car.CarType);
        Assert.Equal(1, car.Colour);
    }

    [GameFact]
    public void Opponent_ModCarIsChoosableAfterTheOriginalCarsBeforeTheClasses()
    {
        var menu = Menu();
        Open(menu, "opponents");
        Select(menu, "car");
        Assert.Equal(12, menu.Config.OpponentCar);   // default: class
        Keys(menu, MenuKey.Left);                    // the car before the first class = the mod car
        Assert.Equal(Duster, menu.Config.OpponentCar);
        Assert.Equal("car: Duster", Item(menu));
        Assert.Equal(2, menu.Config.OpponentCount);  // full grid stays
        Keys(menu, MenuKey.Left);
        Assert.Equal(MenuController.ShowcaseCarCount - 1, menu.Config.OpponentCar);
        Keys(menu, MenuKey.Right, MenuKey.Right);
        Assert.Equal(12, menu.Config.OpponentCar);
        Keys(menu, MenuKey.Right, MenuKey.Right, MenuKey.Right);
        Assert.Equal(CarRaceClasses.OpponentValue(CarRaceClass.G), menu.Config.OpponentCar);   // the Duster is class G: it is the only low class shown, after C
        Assert.Equal("car: class G", Item(menu));
        Assert.Equal(2, menu.Config.OpponentCount);
        Keys(menu, MenuKey.Right);
        Assert.Equal(15, menu.Config.OpponentCar);   // "no opponents" is still after the classes
        Assert.Equal(0, menu.Config.OpponentCount);
    }

    [GameFact]
    public void Opponent_DusterAsTheOpponentOfASingleRace()
    {
        var menu = Menu();
        Open(menu, "opponents");
        Select(menu, "car");
        Keys(menu, MenuKey.Left);
        Assert.Equal(Duster, menu.Config.OpponentCar);
        Select(menu, "number"); Keys(menu, MenuKey.Left);   // one opponent: a car (not a class) stays a car
        Assert.Equal(1, menu.Config.OpponentCount);
        Assert.Equal(Duster, menu.Config.OpponentCar);
        var setup = menu.BuildSetup();
        Assert.Equal(2, setup.NumCars);
        Assert.Equal(Duster, setup.Car(1).CarType);
        Assert.InRange(setup.Car(1).Colour, 0, menu.ModCarOf(Duster)!.Colors.Count - 1);
    }

    [GameFact]
    public void Opponent_FullGridOfDusters()
    {
        var menu = Menu();
        Open(menu, "opponents");
        Select(menu, "car");
        Keys(menu, MenuKey.Left);
        var setup = menu.BuildSetup();
        Assert.Equal(8, setup.NumCars);
        for (int i = 1; i < 8; i++) Assert.Equal(Duster, setup.Car(i).CarType);
    }

    [GameFact]
    public void Pools_DusterIsClassGSoTheOriginalPoolsAreUnchanged()
    {
        var withMod = new FeRaceArgsBuilder(DusterCatalog());
        var original = new FeRaceArgsBuilder(CarCatalog.VanillaTables);
        int classA = 0, classC = 2, classB = 11;   // player cars of the class (menu labels): McLaren F1 is A, F355 (2) is C, car 11 is B
        foreach (bool knockout in new[] { false, true })
            for (int i = 0; i <= 10; i++)
                foreach (int player in new[] { classA, classB, classC })
                    Assert.Equal(original.SeriesOpponent(knockout, player, null, i), withMod.SeriesOpponent(knockout, player, null, i));
        var config = new FeConfig(new byte[FeConfig.Size]);
        foreach (byte cls in new byte[] { 12, 13, 14 })
        {
            config.OpponentCar = cls;
            for (int i = 0; i <= 10; i++) Assert.Equal(original.ClassOpponentCar(config, i), withMod.ClassOpponentCar(config, i));
        }
    }

    [GameFact]
    public void Pools_WithoutModsAreTheOriginalTables()
    {
        var builder = new FeRaceArgsBuilder(CarCatalog.VanillaTables);
        Assert.Equal(FrontEndConstants_TournamentC(6), builder.TournamentOpponent(0, 6));
    }

    static int FrontEndConstants_TournamentC(int i) => NFS.Core.Constants.FrontEndConstants.TournamentOpponentsC[i];

    [GameFact]
    public void Showcase_ModCarHasNameAndNoHistoryOrSlideShow()
    {
        var menu = PlayerOne();
        Keys(menu, MenuKey.Left);
        Open(menu, "showcase");
        Assert.True(menu.InShowcase);
        Assert.Equal(Duster, menu.ShowcaseCar);
        var car = menu.ShowcaseModCar;
        Assert.NotNull(car);
        Assert.Equal("Duster", car!.DisplayName);
        Assert.Contains("Duster", MenuController.ModDescription(car));
        var items = menu.CurrentScreen.Items;
        foreach (int i in new[] { 1, 2, 3, 4 }) Assert.NotEqual(0, items[i].Flags & 1);   // more stats, history, slide show, video: disabled
        Assert.Equal(0, items[0].Flags & 1);                                              // next car works
        Keys(menu, MenuKey.Right);                                                         // next car after the mod car = the first original car
        Assert.Equal(0, menu.ShowcaseCar);
        Assert.Null(menu.ShowcaseModCar);
        Assert.Equal(0, menu.CurrentScreen.Items[2].Flags & 1);                            // history is back for an original car
        Keys(menu, MenuKey.Left);                                                          // and back to the mod car
        Assert.Equal(Duster, menu.ShowcaseCar);
        Keys(menu, MenuKey.Escape);
        Assert.Equal(Duster, menu.Config.P1Car);   // leaving the showcase keeps the viewed car, like the original
    }

    [GameFact]
    public void Showcase_WithoutModsKeepsTheTwelveOriginalCars()
    {
        var menu = PlayerOne(mods: false);
        Open(menu, "showcase");
        Keys(menu, MenuKey.Left);
        Assert.Equal(MenuController.ShowcaseCarCount - 1, menu.ShowcaseCar);
        Assert.Null(menu.ShowcaseModCar);
    }

    [GameFact]
    public void Settings_ModCarIsStoredByKeyAndFallsBackWhenTheModIsGone()
    {
        var menu = PlayerOne();
        Keys(menu, MenuKey.Left, MenuKey.Down, MenuKey.Down, MenuKey.Right);   // Duster, colour 1
        var settings = UserSettings.From(menu);
        Assert.Equal(DusterKey, settings.P1CarKey);
        Assert.Equal(0, settings.P1Car);   // the byte field stays a valid original car
        Assert.Equal(1, settings.P1ModColours![DusterKey]);

        // the json round trip is the same file the game writes
        string path = Path.Combine(Path.GetTempPath(), "nfs-menu-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            settings.Save(path);
            var loaded = UserSettings.Load(path);

            var withMod = Menu();
            loaded.ApplyTo(withMod.Config);
            loaded.ApplyCars(withMod);
            Assert.Equal(Duster, withMod.Config.P1Car);
            Assert.Equal(1, withMod.ModColour(0, withMod.ModCarOf(Duster)!));

            var withoutMod = Menu(mods: false);   // the mod was removed: the original car from the byte field stays
            loaded.ApplyTo(withoutMod.Config);
            loaded.ApplyCars(withoutMod);
            Assert.Equal(0, withoutMod.Config.P1Car);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [GameFact]
    public void Settings_OpponentModCarByKey()
    {
        var menu = Menu();
        Open(menu, "opponents"); Select(menu, "car"); Keys(menu, MenuKey.Left);
        var settings = UserSettings.From(menu);
        Assert.Equal(DusterKey, settings.OpponentCarKey);
        Assert.Equal(12, settings.OpponentCar);
        var fresh = Menu();
        settings.ApplyTo(fresh.Config);
        settings.ApplyCars(fresh);
        Assert.Equal(Duster, fresh.Config.OpponentCar);
    }

    [GameFact]
    public void ModNumberThatIsNotInTheCatalogFallsBackToAnOriginalCar()
    {
        var menu = Menu(mods: false);
        menu.Config.P1Car = (byte)Duster; menu.Config.OpponentCar = (byte)Duster;
        menu.SanitizeCarSelection();
        Assert.Equal(0, menu.Config.P1Car);
        Assert.Equal(12, menu.Config.OpponentCar);
    }
}
