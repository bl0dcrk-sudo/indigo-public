using Cockpit = NFS.ModApi.Cockpit;
using NFS.ModApi;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Core.Tables;
using NFS.Data;
using NFS.Data.Car;
using NFS.Game.Race;
using NFS.Render;

namespace NFS.Game.Cars;

/// <summary>
/// The 0x35 original cars, built from the existing tables (<see cref="RaceConstants.CarNames"/>, <c>CarInfo</c>,
/// <c>ModelVmax</c>, <see cref="AudioConstants"/> banks) and the raw physics bytes of CARDATA.VIV.
/// The table part needs no game files: physics are read through <c>readCarFile</c> (file name in CARDATA.VIV, null when
/// absent). Without a reader every car gets <see cref="NoPhysics"/>, exactly like RaceLoader leaves specs null.
/// </summary>
public sealed class VanillaCarSource : ICarSource
{
    private readonly Func<string, byte[]?>? _readCarFile;

    public VanillaCarSource(Func<string, byte[]?>? readCarFile = null) => _readCarFile = readCarFile;

    /// <summary>Source reading CARDATA.VIV from the game directory (null = the standard search).</summary>
    public static VanillaCarSource FromGameDir(string? gameDir = null)
    {
        var viv = Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", gameDir);
        return new VanillaCarSource(name =>
            viv.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? viv.Get(name) : null); // as RaceLoader.CarFile
    }

    public string Name => "vanilla";
    public bool IsVanilla => true;

    /// <summary>Key of car <paramref name="index"/>: <c>nfs2se/NAME</c>; names shared by several cars (CITR, MIAT, SBUS) get <c>-NN</c> (decimal index).</summary>
    public static CarKey KeyOf(int index)
    {
        string name = RaceConstants.CarNames[index];
        bool shared = RaceConstants.CarNames.Count(n => n == name) > 1;
        return CarKey.Vanilla(shared ? $"{name}-{index:00}" : name);
    }

    public IEnumerable<CarDefinition> GetCars()
    {
        for (int i = 0; i < CarCatalog.VanillaCount; i++)
            yield return new CarDefinition(KeyOf(i), RaceConstants.CarNames[i], i, PhysicsOf(i), TraitsOf(i));
    }

    private CarPhysics PhysicsOf(int i)
    {
        if (_readCarFile is null) return new NoPhysics();
        string model = RaceConstants.CarNames[i];
        // the same files as RaceLoader.CreateCar (game option bit 4 clear) [ref:1555]
        var specs = _readCarFile(RaceLoader.SpecsFile(i, model, 0));
        var ai = _readCarFile(RaceLoader.AiModelFile(i, model));
        return specs is { Length: RawPhysics.CarSpecsSize } && ai is { Length: RawPhysics.CarAiModelSize }
            ? new RawPhysics(specs, ai) : new NoPhysics();
    }

    /// <summary>Traits of original car <paramref name="i"/>; each line names the original condition it mirrors.</summary>
    public static CarTraits TraitsOf(int i)
    {
        var info = GameTables.Default.CarInfo;
        int massIdx = i * GameTables.CarInfoStride + 1;
        var vmax = GameTables.Default.ModelVmax;
        return new CarTraits
        {
            RaceClass = i is 2 or 10 or 5 or 8 or 14 ? CarRaceClass.C : i is 6 or 4 or 9 or 11 or 13 ? CarRaceClass.B : CarRaceClass.A, // menu label (RatingTexts): A = fastest pool (0, 7...), C = slowest (2, 10...); FeRaceArgsBuilder [ref:1556]
            HasAbs = SimModel.HasAbs(i),                                   // SimModel.cs:73
            RearAeroBoost = i is 0 or 0xE,                                 // SimModel.cs:388
            SkidMarks = i == 7,                                            // SimModel.cs:269
            SpecialRules0E = i == ArcadeModel.ModelSpecial,                // SimModel.cs:318, ArcadeModel.cs:56/92/109/322
            WheelShift = CarGeo.FrontWheelShift(i),                        // CarGeo.cs (front wheels, type 4)
            RearWheelShift = CarGeo.RearWheelShift(i),                     // CarGeo.cs (rear wheels, type < 0xF)
            FullAngleSteer = CarAnim.VanillaFullAngleSteer(i),             // CarAnim.cs (SteerScale, type 4)
            Cockpit = CockpitOf(i),                                        // Cockpit.cs:39 and the carType ifs of its ctor/Draw
            TrafficPaintVariants = CarModel.VanillaPaintVariants(i),     // CarModel.cs (PaintVariants)
            BodyMassScale = 1,                                             // RaceLoader.cs:139 follows the car NAME "SIL_SPD", not the model
            BodyMass = massIdx < info.Length ? info[massIdx] : null,       // RaceLoader.cs:134-140
            AiTopSpeed = i is >= 0 and < 15 && vmax.Length == 15 ? vmax[i] : 0, // RaceRunner.cs:226
            EngineBank = new EngineBank(PlayerBank(i), SplitBank(i), OpponentBank(i)),
        };
    }

    // the vanilla rule lives in NFS.Render.CockpitStyle.Vanilla (the Cockpit ctor default), so the traits and the renderer agree
    private static Cockpit CockpitOf(int i)
    {
        if (!NFS.Render.Cockpit.HasDashboard(i)) return new Cockpit(CockpitKind.None, null);
        var st = NFS.Render.CockpitStyle.Vanilla(i);
        return new Cockpit(st.Digital ? CockpitKind.Digital : CockpitKind.Dash, st.DashFile, st.ExtraFile, st.Tinted, st.LeftMirror,
            st.NoBitOverlays, st.GearLetters, st.MirrorOffsetX, st.MirrorOffsetY);
    }

    // RaceAudio.cs:136-155 [ref:1557]
    private static string PlayerBank(int m) => m < 15 ? AudioConstants.PlayerBanks[m]
        : m == 43 ? "TREX" : m >= 40 ? "CRATE" : m is 18 or 29 or 36 ? "PUTT" : m is 20 or 37 or 21 or 31 or 33 ? "TRUCK" : "LIMO";
    private static string SplitBank(int m) => m < 15 ? AudioConstants.SplitBanks[m]
        : m == 43 ? "STREX" : m >= 40 ? "SCRATE" : m is 18 or 29 or 36 ? "SPUTT" : m is 20 or 37 or 21 or 31 or 33 ? "STRUCK" : "SLIMO";
    private static string OpponentBank(int m) => AudioConstants.OpponentBanks[m < 15 ? m : 0]; // RaceAudio.cs:154 (>= 15 falls back to model 0)
}
