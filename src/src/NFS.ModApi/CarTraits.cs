namespace NFS.ModApi;

/// <summary>Kind of cockpit view: none, analogue dash, or the digital dash of the original car 4.</summary>
public enum CockpitKind { None, Dash, Digital }

/// <summary>
/// Cockpit description. <see cref="DashFile"/> = DSHxx.QFS (null = none). The remaining flags are read by <c>NFS.Render.Cockpit</c>
/// through <c>CockpitExtensions.ToStyle</c> (NFS.Game): left mirror (car 0), no bit1/bit2 overlays (3, 4), HSV-tinted dash (5), extra HD file and
/// ext0/ext1 background (7), gear letters r/n/1.. (2), offset of the rear-view strip (5: 495,49; 9: 504,62).
/// </summary>
public readonly record struct Cockpit(CockpitKind Kind, string? DashFile, string? ExtraFile = null,
    bool Tinted = false, bool LeftMirror = false, bool NoBitOverlays = false,
    bool GearLetters = false, int MirrorOffsetX = 0, int MirrorOffsetY = 0);

/// <summary>Engine sound banks (BNK names) for the player, split-screen and opponent engine.</summary>
public readonly record struct EngineBank(string Player, string Split, string Opponent);

/// <summary>Race class as the menu labels it (RatingTexts): A = the fastest pool of the original (McLaren F1, LGT1...), C = the slowest (F355...).
/// Picks opponents of the series tables; for mod cars it also picks the physics template.
/// D..G (API 1.1) are slower classes without cars in the original, hidden in the menu until a mod car of the class exists
/// (about 30 km/h per step below C: D 250..279, E 220..249, F 190..219, G 160..189 km/h; the mod author sets the letter by hand).</summary>
public enum CarRaceClass { C, B, A, D, E, F, G }

/// <summary>Per-car behaviour flags that the original code derived from the model number.</summary>
public sealed record CarTraits
{
    public bool HasAbs { get; init; }
    /// <summary>Class used to pick tournament/knockout opponents (<c>FeRaceArgsBuilder</c>); mods: car.json "class".</summary>
    public CarRaceClass RaceClass { get; init; }
    /// <summary>Rear aero boost (original: model 0 / 0xE).</summary>
    public bool RearAeroBoost { get; init; }
    /// <summary>Skid mark on every tick under ABS braking (original: model 7).</summary>
    public bool SkidMarks { get; init; }
    /// <summary>Special rules of model 0xE.</summary>
    public bool SpecialRules0E { get; init; }
    /// <summary>Front wheel shift applied by <c>CarGeo.ApplyWheelShift</c> (original: model 4).</summary>
    public bool WheelShift { get; init; }
    /// <summary>Rear wheel shift applied by <c>CarGeo.ApplyWheelShift</c> (original: model &lt; 0xF).</summary>
    public bool RearWheelShift { get; init; }
    /// <summary>Front wheels steer at the full angle from every view (original: model 4, <c>CarAnim.SteerScale</c>).</summary>
    public bool FullAngleSteer { get; init; }
    public Cockpit Cockpit { get; init; } = new(CockpitKind.None, null);
    /// <summary>Number of palette variants of a traffic car (original: 3 for types 0x11..0x27 except 0x24, else 1).</summary>
    public int TrafficPaintVariants { get; init; } = 1;
    /// <summary>Body mass scale (the SIL_SPD x4 case: it follows the configured car name, never the model); 1 = unchanged.</summary>
    public int BodyMassScale { get; init; } = 1;
    /// <summary>Body mass 16.16 fixed-point; null = no row (the loader leaves the default). [ref:2428]</summary>
    public int? BodyMass { get; init; }
    /// <summary>AI top speed 16.16 (original <c>ModelVmax</c>, types 0..14 only; 0 = none).</summary>
    public int AiTopSpeed { get; init; }
    public EngineBank EngineBank { get; init; }
}
