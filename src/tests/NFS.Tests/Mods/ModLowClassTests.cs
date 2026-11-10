using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.FrontEnd;
using NFS.Game.Mods;
using NFS.Game.Race;
using NFS.ModApi;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Mods;

/// <summary>openspec modding-api-cars 4.7 / D10: classes D..G below the original C - visible in the menu only when a mod car has them,
/// the pool of such a class is made of its mod cars (copies differ by colour), the physics template is the F355, a whole race of them runs.</summary>
public sealed class ModLowClassTests(ITestOutputHelper output)
{
    const int Duster = CarCatalog.VanillaCount;
    static string RepoMods => Path.GetFullPath(Path.Combine(GameData.ProjectRoot, "..", "mods"));

    /// <summary>The vanilla cars plus the repo mods; <paramref name="extra"/> adds more mod cars next to the Duster.</summary>
    static CarCatalog Catalog(Func<CarDefinition, IEnumerable<CarDefinition>>? extra = null)
    {
        var diag = new ModDiagnostics();
        var mods = ModLoader.Load(RepoMods, diag);
        var cars = new ModCarSource(mods, diag).GetCars().ToList();
        if (extra != null) cars = [.. cars.SelectMany(c => new[] { c }.Concat(extra(c)))];
        var catalog = new CarCatalog();
        foreach (var v in VanillaCarSource.FromGameDir(GameData.Dir).GetCars()) catalog.Register(v, true);
        foreach (var c in cars) catalog.Register(c, false);
        catalog.Freeze();
        return catalog;
    }

    /// <summary>The Duster plus a clone of it in the given class (key "indigo.duster/clone-suffix").</summary>
    static CarCatalog WithClone(CarRaceClass cls, string suffix = "x") => Catalog(d => [d with
    {
        Key = CarKey.Parse("indigo.duster/clone-" + suffix),
        Traits = d.Traits with { RaceClass = cls },
    }]);

    static MenuController Menu(CarCatalog? cars)
    {
        var menu = MenuController.Load(GameData.Dir);
        if (cars != null) menu.Cars = cars;
        return menu;
    }

    static void Keys(MenuController menu, params MenuKey[] keys) { foreach (var k in keys) menu.HandleKey(k); }

    /// <summary>OPPONENTS (Down Down Down Enter, as the render tests) with the car item selected.</summary>
    static void OpenOpponentCar(MenuController menu)
    {
        Keys(menu, MenuKey.Down, MenuKey.Down, MenuKey.Down, MenuKey.Enter);
        for (int i = 0; i < menu.CurrentScreen.Items.Count && menu.CurrentScreen.Items[menu.SelectedIndex].Text != "car"; i++) menu.HandleKey(MenuKey.Down);
    }

    [GameFact]
    public void Visibility_WithoutModsNoLowClass_WithDusterOnlyG()
    {
        Assert.Empty(Menu(null).VisibleLowClasses);
        Assert.Equal([CarRaceClass.G], Menu(Catalog()).VisibleLowClasses);
        Assert.Equal([CarRaceClass.D, CarRaceClass.G], Menu(WithClone(CarRaceClass.D)).VisibleLowClasses);
        Assert.Equal([CarRaceClass.G], Menu(WithClone(CarRaceClass.B)).VisibleLowClasses);   // a mod car of class A..C adds no low class
    }

    [GameFact]
    public void OpponentList_WithoutModsIsTheOriginal_WithModsLowClassesSitBetweenCAndNone()
    {
        var plain = Menu(null);
        OpenOpponentCar(plain);
        var seen = new HashSet<int>();
        for (int i = 0; i < 20; i++) { seen.Add(plain.Config.OpponentCar); plain.HandleKey(MenuKey.Right); }
        Assert.Equal(Enumerable.Range(0, 16).ToHashSet(), seen);   // 12 cars, 3 classes, none: no value of a low class

        var menu = Menu(WithClone(CarRaceClass.D));
        OpenOpponentCar(menu);
        menu.Config.OpponentCar = 14;
        var after = new List<int>();
        for (int i = 0; i < 4; i++) { menu.HandleKey(MenuKey.Right); after.Add(menu.Config.OpponentCar); }
        Assert.Equal([16, 19, 15, 0], after);   // D, G, none, then the first car again
    }

    [GameFact]
    public void LowClassWithoutCar_IsResetWhenTheModDisappears()
    {
        var menu = Menu(Catalog());
        menu.Config.OpponentCar = 19;
        menu.SanitizeCarSelection();
        Assert.Equal(19, menu.Config.OpponentCar);
        menu.Cars = CarCatalog.VanillaTables;
        Assert.Equal(12, menu.Config.OpponentCar);
    }

    [GameFact]
    public void ClassGPool_IsOnlyDustersInDifferentColours()
    {
        var menu = Menu(Catalog());
        OpenOpponentCar(menu);
        menu.Config.OpponentCar = 14;
        Keys(menu, MenuKey.Right);
        Assert.Equal(19, menu.Config.OpponentCar);
        Assert.Equal("car: class G", menu.ItemText(menu.CurrentScreen.Items[menu.SelectedIndex]));
        var setup = menu.BuildSetup();
        Assert.Equal(8, setup.NumCars);
        var colours = new HashSet<int>();
        for (int i = 1; i < 8; i++)
        {
            Assert.Equal(Duster, setup.Car(i).CarType);
            colours.Add(setup.Car(i).Colour);
        }
        Assert.True(colours.Count >= 4, "copies of the Duster differ by colour: " + string.Join(",", colours));
        Assert.All(colours, c => Assert.InRange(c, 0, menu.ModCarOf(Duster)!.Colors.Count - 1));
    }

    [GameFact]
    public void ClassGPool_ListsEveryCarOfTheClassFirst_ThenRepeats()
    {
        var catalog = WithClone(CarRaceClass.G, "g2");
        int clone = catalog.NumberOf(CarKey.Parse("indigo.duster/clone-g2"));
        var b = new FeRaceArgsBuilder(catalog);
        var config = new FeConfig(new byte[FeConfig.Size]) { OpponentCar = 19 };
        var got = Enumerable.Range(0, 7).Select(i => b.ClassOpponentCar(config, i)).ToArray();
        Assert.Equal([Duster, clone, Duster, clone, Duster, clone, Duster], got);
        // series: a class G player gets the class G pool (tournament and knockout), also with a faster second player in split screen
        foreach (bool ko in new[] { false, true })
            for (int i = 0; i < 7; i++)
            {
                Assert.Equal(b.SeriesOpponent(ko, Duster, null, i), b.SeriesOpponent(ko, Duster, null, i));
                Assert.Contains(b.SeriesOpponent(ko, Duster, null, i), new[] { Duster, clone });
                Assert.Contains(b.SeriesOpponent(ko, 0, Duster, i), new[] { Duster, clone });
            }
        // the original pools are untouched by mod cars without class A..C
        var original = new FeRaceArgsBuilder(CarCatalog.VanillaTables);
        for (int i = 0; i <= 10; i++) Assert.Equal(original.SeriesOpponent(false, 2, null, i), b.SeriesOpponent(false, 2, null, i));
    }

    [Fact]
    public void LowClasses_UseTheF355Template_AndHelpersAreConsistent()
    {
        foreach (var c in CarRaceClasses.Low) Assert.Equal(2, SiPhysicsConverter.TemplateNumber(c));
        for (int i = 0; i < CarRaceClasses.Low.Count; i++)
        {
            Assert.Equal(CarRaceClasses.Low[i], CarRaceClasses.FromOpponentValue(CarRaceClasses.OpponentValueBase + i));
            Assert.Equal(CarRaceClasses.OpponentValueBase + i, CarRaceClasses.OpponentValue(CarRaceClasses.Low[i]));
        }
        Assert.Null(CarRaceClasses.FromOpponentValue(15));
        Assert.Null(CarRaceClasses.FromOpponentValue(CarRaceClasses.OpponentValueBase + CarRaceClasses.Low.Count));
        Assert.Equal("class G", CarRaceClasses.Label("class C", CarRaceClass.G));
        Assert.True(CarRaceClasses.SlowRank(CarRaceClass.G) < CarRaceClasses.SlowRank(CarRaceClass.D));
        Assert.True(CarRaceClasses.SlowRank(CarRaceClass.D) < CarRaceClasses.SlowRank(CarRaceClass.C));
    }

    [GameFact]
    public void ClassGRace_OfDustersOnTrack0_FinishesWithoutException()
    {
        var menu = Menu(Catalog());
        OpenOpponentCar(menu);
        menu.Config.OpponentCar = 19;
        menu.Config.P1Car = Duster;
        var setup = menu.BuildSetup();
        setup.Set(0x04, 1);   // one lap
        Assert.Equal(8, setup.NumCars);
        var runner = new RaceRunner(new RaceLoader(GameData.Dir, menu.Cars).Load(setup), 0, automatic: true, withAi: true, countdown: true);
        Assert.Equal(0, runner.Session.TrackNumber);
        int t = 0;
        while (!runner.Laps.RaceOver && t < 32 * 600) { runner.Step(ModRaceTests.Autopilot(runner)); t++; }
        output.WriteLine($"class G race: ticks {t}, player state {runner.Laps.Cars[0].State}");
        Assert.True(runner.Laps.RaceOver, "the race did not end");
        Assert.Equal(2, runner.Laps.Cars[0].State);
        for (int i = 1; i < 8; i++) Assert.Equal(Duster, runner.Cars[i].Race.Setup.CarType);
    }
}
