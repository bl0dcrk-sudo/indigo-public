using NFS.ModApi;
namespace NFS.Game.Cars;

/// <summary>Classes D..G below the original C (openspec modding-api-cars D10): no cars in the original, so they exist only while a mod car
/// has them. In the menu the opponent value (config +0x55) of such a class is <see cref="OpponentValueBase"/> + 0..3 – the free values between
/// "no opponents" (15) and the first mod car number (0x35).</summary>
public static class CarRaceClasses
{
    /// <summary>The classes below C, fastest first; add a letter here (and to <see cref="CarRaceClass"/>) to extend the scale.</summary>
    public static readonly IReadOnlyList<CarRaceClass> Low = [CarRaceClass.D, CarRaceClass.E, CarRaceClass.F, CarRaceClass.G];

    /// <summary>Opponent value (config +0x55) of the first low class (D).</summary>
    public const int OpponentValueBase = 16;

    public static bool IsLow(CarRaceClass c) => Low.Contains(c);

    /// <summary>Menu opponent value of a low class.</summary>
    public static int OpponentValue(CarRaceClass c) => OpponentValueBase + Low.ToList().IndexOf(c);

    /// <summary>The low class of a menu opponent value, null for any other value.</summary>
    public static CarRaceClass? FromOpponentValue(int value)
        => value >= OpponentValueBase && value < OpponentValueBase + Low.Count ? Low[value - OpponentValueBase] : null;

    /// <summary>Speed rank, 0 = the slowest class (G) ... for choosing the slowest class of two players (the original also lets the slowest win).</summary>
    public static int SlowRank(CarRaceClass c) => c switch
    {
        CarRaceClass.A => 100, CarRaceClass.B => 90, CarRaceClass.C => 80,
        _ => 70 - Low.ToList().IndexOf(c),
    };

    /// <summary>Label "class X" in the style of the original texts: <paramref name="classCText"/> (text 123, "class C") with the last letter replaced.</summary>
    public static string Label(string classCText, CarRaceClass c)
        => classCText.Length == 0 ? c.ToString() : classCText[..^1] + c;

    /// <summary>Low classes that have at least one mod car in <paramref name="cars"/>, fastest first.</summary>
    public static List<CarRaceClass> Present(CarCatalog cars)
    {
        var list = new List<CarRaceClass>();
        foreach (var c in Low)
            if (cars.ModCars.Any(m => m.Traits.RaceClass == c)) list.Add(c);
        return list;
    }

    /// <summary>In-memory numbers of the mod cars of class <paramref name="c"/> in catalog order.</summary>
    public static List<int> CarNumbers(CarCatalog cars, CarRaceClass c)
        => [.. cars.ModCars.Where(m => m.Traits.RaceClass == c).Select(m => cars.NumberOf(m.Key))];
}
