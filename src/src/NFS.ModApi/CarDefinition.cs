namespace NFS.ModApi;

/// <summary>Car physics: <see cref="RawPhysics"/> (EA bytes) or <see cref="SiPhysics"/> (SI units from a mod).</summary>
public abstract record CarPhysics;

/// <summary>Original EA format: 356 B CarSpecs + 524 B CarAiModel, passed to the simulation unchanged.</summary>
public sealed record RawPhysics : CarPhysics
{
    public const int CarSpecsSize = 356;
    public const int CarAiModelSize = 524;

    public ReadOnlyMemory<byte> CarSpecs { get; }
    public ReadOnlyMemory<byte> CarAiModel { get; }

    public RawPhysics(byte[] carSpecs, byte[] carAiModel)
    {
        if (carSpecs.Length != CarSpecsSize) throw new ArgumentException($"CarSpecs must be {CarSpecsSize} B.", nameof(carSpecs));
        if (carAiModel.Length != CarAiModelSize) throw new ArgumentException($"CarAiModel must be {CarAiModelSize} B.", nameof(carAiModel));
        CarSpecs = carSpecs.ToArray();
        CarAiModel = carAiModel.ToArray();
    }
}

/// <summary>Driven axle of a car.</summary>
public enum CarDrive { Rear, Front, All }

/// <summary>
/// Car physics in SI units, as written in <c>car.json</c> by a mod author. The adapter (<c>SiPhysicsConverter</c>, NFS.Game) turns it
/// into the 356 B CarSpecs / 524 B CarAiModel; unit names are part of the member names. Defaults only for optional members.
/// </summary>
public sealed record SiPhysics : CarPhysics
{
    /// <summary>Kerb mass, kg.</summary>
    public double MassKg { get; init; }
    /// <summary>Peak engine power, kW, reached at <see cref="PowerRpm"/>.</summary>
    public double PowerKw { get; init; }
    public double PowerRpm { get; init; }
    /// <summary>Peak engine torque, Nm, reached at <see cref="TorqueRpm"/> (not above <see cref="PowerRpm"/>).</summary>
    public double TorqueNm { get; init; }
    public double TorqueRpm { get; init; }
    /// <summary>Rev limit, rpm (at most 10240, the end of the torque table).</summary>
    public double RedlineRpm { get; init; }
    /// <summary>Forward gearbox ratios, first gear first, strictly decreasing (1..6 gears).</summary>
    public IReadOnlyList<double> GearRatios { get; init; } = [];
    /// <summary>Reverse gearbox ratio; 0 = 1.1 x the first forward ratio.</summary>
    public double ReverseGearRatio { get; init; }
    public double FinalDrive { get; init; }
    /// <summary>Driven wheel radius, m (rpm per m/s follows from it).</summary>
    public double WheelRadiusM { get; init; } = 0.33;
    public double TopSpeedKmh { get; init; }
    public CarDrive Drive { get; init; }
    public double WheelbaseM { get; init; }
    /// <summary>Tyre grip relative to the template car of the class (1 = same); null = 1.</summary>
    public double? TyreGrip { get; init; }
    /// <summary>Peak braking deceleration, m/s^2; null = the template car's.</summary>
    public double? BrakeDecelerationMs2 { get; init; }
}

/// <summary>The physics files were not available (table-only vanilla source, or the VIV lacks them).</summary>
public sealed record NoPhysics : CarPhysics;

/// <summary>Outer body size in metres (length along the car, width, height).</summary>
public sealed record CarDimensions(double LengthM, double WidthM, double HeightM);

/// <summary>One paint colour choice of a mod car (car.json "colors"): display names by language and the RGB of the paint.</summary>
public sealed record CarColor(IReadOnlyDictionary<string, string> Names, byte R, byte G, byte B)
{
    public string Name(string lang) => Names.GetValueOrDefault(lang) ?? Names.GetValueOrDefault("en") ?? Names.GetValueOrDefault("pl") ?? $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>One car in the catalog. <see cref="LegacyIndex"/> (0..0x34) only for original cars.</summary>
public sealed record CarDefinition(CarKey Key, string DisplayName, int? LegacyIndex, CarPhysics Physics, CarTraits Traits)
{
    /// <summary>Descriptions by language ("pl", "en"); empty for original cars.</summary>
    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();
    public string? Manufacturer { get; init; }
    public int? Year { get; init; }
    /// <summary>Full path of the validated .glb of a mod car; null for original cars.</summary>
    public string? ModelPath { get; init; }
    /// <summary>Outer size of the body (car.json "dimensions"); the race loader builds the collision box from it when there is no
    /// <c>&lt;DisplayName&gt;.geo</c> in CARDATA.VIV (always the case for a new mod car). Null = no box (zero half sizes).</summary>
    public CarDimensions? Dimensions { get; init; }

    /// <summary>Paint colours to choose from (car.json "colors"); empty = the model keeps its own colours. Index 0 is the default.</summary>
    public IReadOnlyList<CarColor> Colors { get; init; } = [];
    /// <summary>glTF material names that take the paint colour (car.json "paintMaterials"); empty = every material whose name contains "paint".</summary>
    public IReadOnlyList<string> PaintMaterials { get; init; } = [];

    /// <summary>Version of the <c>car.json</c> / definition schema.</summary>
    public const int SchemaVersion = 1;
}
