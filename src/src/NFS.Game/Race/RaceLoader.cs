using NFS.ModApi;
using NFS.Core;
using NFS.Core.Constants;
using NFS.Core.Tables;
using NFS.Data;
using NFS.Data.Ai;
using NFS.Data.Car;
using NFS.Data.Track;
using NFS.Game.Cars;
using NFS.Game.Mods;

namespace NFS.Game.Race;

/// <summary>One car of the session: config record ([i]), car object and its data files. [ref:2112]</summary>
public sealed record RaceCar(int Index, CarSetup Setup, string Model, CarObj Obj,
    CarSpecs? Specs, CarAiModel? AiModel, CarGeo? Geo, int GridSlot, int StartSlice, CarTraits? Traits = null);

/// <summary>Everything loads (headless part) plus the created cars. [ref:2113]</summary>
public sealed record RaceSession(
    RaceSetup Setup, int TrackNumber, bool Backward,
    Trk Trk, Col Col, TrackSlice[] Slices, Dis Dis, Hrz? Hrz, Can? IntroCamera,
    AiSpeeds? Speeds, Prsonal? Personalities, TrafCfg? Traffic, SimTune? SimTune, StaticCars? Smackables,
    IReadOnlyList<RaceCar> Cars)
{
    public AiLine? Line { get; init; }
    /// <summary>Car catalog the session was built with (car number to <c>CarKey</c> for the replay mods header); null when not built by <see cref="RaceLoader"/>.</summary>
    public NFS.Game.Cars.CarCatalog? Catalog { get; init; }
    /// <summary>Parked cars from SMnn.DAT, each with body, orientation [ref:2114]
    /// and start position; null when the session was not built by <see cref="RaceLoader"/>. [ref:2115]</summary>
    public IReadOnlyList<CarObj>? Parked { get; init; }
}

/// <summary>Headless race loader (load order: HRZ, then TRK/COL). [ref:2116]</summary>
public sealed class RaceLoader(string? gameDir = null, CarCatalog? catalog = null)
{
    private const string Tracks = "GAMEDATA/TRACKS/";
    private Viv? _carData;

    // Vanilla catalogs per game dir (the VIV is read once); a caller with its own catalog passes it to the constructor.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CarCatalog> VanillaCatalogs = new();

    /// <summary>Cars of this loader: the given catalog, else the cached vanilla one of the game dir.</summary>
    public CarCatalog Catalog { get; } = catalog ?? DefaultCatalog(gameDir);

    /// <summary>Vanilla cars (numbers 0..0x34) plus the cars of <see cref="ModSession.Current"/> (0x35.. in load order); cached per game dir and
    /// mods dir. Without mods it is exactly the vanilla catalog.</summary>
    public static CarCatalog DefaultCatalog(string? gameDir)
    {
        var session = ModSession.Current;
        return VanillaCatalogs.GetOrAdd($"{gameDir}|{string.Join("|", session.Mods.Select(m => m.Dir))}", _ =>
        {
            ICarSource[] sources = session.Mods.Count == 0
                ? [VanillaCarSource.FromGameDir(gameDir)]
                : [VanillaCarSource.FromGameDir(gameDir), new ModCarSource(session.Mods, session.Diagnostics)];
            return CarCatalog.Build(sources).Catalog;
        });
    }

    private CarDefinition Car(int carType) =>
        Catalog.TryGetByNumber(carType, out var def) ? def : throw new KeyNotFoundException($"car number {carType}");

    /// <summary>Car model name (catalog definition name; vanilla = <see cref="RaceConstants.CarNames"/>, as in the original game). [ref:2117]</summary>
    public string ModelName(int carType) => Car(carType).DisplayName;

    private byte[]? TryRead(string rel) => GameFiles.Exists(rel, gameDir) ? GameFiles.Read(rel, gameDir) : null;
    private byte[]? TryReadRaw(string rel) => GameFiles.Exists(rel, gameDir) ? GameFiles.ReadRaw(rel, gameDir) : null;

    private string TrackFile(string ext, int t)
    {
        foreach (var dir in new[] { "SE/", "PC/" })
        {
            var rel = $"{Tracks}{dir}TR{t:00}.{ext}";
            if (GameFiles.Exists(rel, gameDir)) return rel;
        }
        throw new FileNotFoundException($"track {t} {ext} not found");
    }

    public RaceSession Load(RaceSetup s)
    {
        int t = s.Track;
        bool back = s.Backward;
        var smack = TryReadRaw($"{Tracks}DATA/PC/SM{t:00}.DAT") is { } sm ? StaticCars.Parse(sm) : null; // %sSm%02d.dat
        var hrzRel = $"{Tracks}PC/TR{t:00}.HRZ";
        var hrz = GameFiles.Exists(hrzRel, gameDir) ? Hrz.Load(hrzRel, gameDir) : null;
        var trk = Trk.Load(TrackFile("TRK", t), gameDir);
        var col = Col.Load(TrackFile("COL", t), gameDir);
        var slices = col.Slices;
        var disRel = $"{Tracks}SE/TR{t:00}.DIS";
        var dis = TryReadRaw(disRel) is { } d ? Dis.Parse(d, slices.Length) : Dis.Default(slices.Length);
        var can = TryRead($"{Tracks}DATA/PC/TR{t:00}00.CAN") is { } c ? Can.Parse(c) : null;
        // Chooses the speed set: sim.QPS/.QPL, arcade.QAS/.QAL [ref:2118]
        var speeds = TryReadRaw($"{Tracks}SPEEDS/TR{t:00}{(back ? "B" : "")}.Q{(s.Style != 0 ? "A" : "P")}S") is { } q
            ? AiSpeeds.Parse(q) : null;
        var pers = TryReadRaw("GAMEDATA/SIM/AI/PRSONAL.DAT") is { } p ? Prsonal.Parse(p) : null;
        var traf = TryReadRaw("GAMEDATA/SIM/AI/TRAFCFG.DAT") is { } tc ? TrafCfg.Parse(tc) : null;
        var tune = TryReadRaw("GAMEDATA/CARMODEL/PC/SIMTUNE.DAT") is { } st ? SimTune.Parse(st) : null;
        var locator = new TrackLocator(new TrackGeometry(trk, slices));
        var road = new TrackRoadQuery(locator, t, tune?.For(s.Style != 0).Values ?? new int[29]) { ArcadeMode = s.Style != 0 ? 1 : 0 };

        _carData ??= Viv.Load("GAMEDATA/SIM/CARDATA/CARDATA.VIV", gameDir);
        var cars = new List<RaceCar>();
        foreach (var (cs, i) in s.Cars.Select((x, i) => (x, i)))
            cars.Add(CreateCar(s, i, cs, slices, back, road));
        var line = TryReadRaw($"{Tracks}SPEEDS/TR{t:00}{(back ? "B" : "")}.Q{(s.Style != 0 ? "A" : "P")}L") is { } ln
            ? AiLine.Parse(ln) : null;
        return new RaceSession(s, t, back, trk, col, slices, dis, hrz, can, speeds, pers, traf, tune, smack, cars)
        { Line = line, Parked = CreateParked(s, smack), Catalog = Catalog };
    }

    /// <summary>
    /// Creates a car object for each SMnn.DAT record (at most NumSmackables) [ref:2119]
    /// with flag 0x40 (class 0x10), index = configured car count + i, model name from the record (CART, BEER, RUIN…) [ref:2120]
    /// body (mass/inertia/box) from the model (models ≥ 0x28 load their own .geo and have their own [ref:2121]
    /// mass in the data); position and slice come from the record, orientation from its heading, rail mode (+0x8C = 1). [ref:2122]
    /// </summary>
    private IReadOnlyList<CarObj> CreateParked(RaceSetup s, StaticCars? smack)
    {
        var list = new List<CarObj>();
        int racers = s.Cars.Count();
        foreach (var sm in smack?.Cars.Take(s.NumSmackables) ?? [])
        {
            var def = Car(sm.Model);
            string model = def.DisplayName;
            var geo = CarFile($"{model}.geo") is { } gd ? CarGeo.Parse(gd) : null;
            geo?.ApplyWheelShift(def.Traits.WheelShift, def.Traits.RearWheelShift);
            int idx = racers + list.Count;
            var obj = new CarObj
            {
                Active = 1, Id = 0x200 | idx, PlayerIndex = idx, Flags = CarFlags.Static,
                ModelName = model.ToLowerInvariant(), Mass = 10 * Fx.One, Inertia = 15 * Fx.One,
                CatchUp = Fx.One, Glue = Fx.One, UpDotN = Fx.One, TimeScale = Fx.One, Rail = 1,
                Pos = new Vec3(sm.X, sm.Y, sm.Z), Orient = FxMath.RotY(sm.Heading),
            };
            BodyFromModel(obj, def, model, s.Skill, geo);
            obj.Trk.Slice = sm.Slice;
            list.Add(obj);
        }
        return list;
    }

    private byte[]? CarFile(string name) =>
        _carData!.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? _carData.Get(name) : null;

    /// <summary>Race config car record +0x34: the name (human: player name, AI: driver name), ≤ 12 chars.</summary>
    public static string CarName(RaceSetup s, int i)
    {
        var b = s.Block.AsSpan(0x78 + i * NFS.Data.FrontEnd.RaceConfig.CarRecordSize + 0x34, 12);
        int z = b.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(z < 0 ? b : b[..z]);
    }

    /// <summary>
    /// The physics body of racers (a car of a model already loaded copies these values): [ref:2123]
    /// mass +0xB4 = the definition's BodyMass ([type].mass, stride 12; ×4 for the name "SIL_SPD"), +0xB8 = 1/mass; [ref:2124]
    /// inertia +0xF4: AI (flag 2) 2·m and mass ×0.85 at skill 0 (the inverse mass keeps the unscaled value), 3·m
    /// otherwise; humans 9·m at skill 0, 4·m otherwise; +0xF8 = 1/inertia. Half sizes +0x108/+0x10C/+0x110 =
    /// ((max − min)/2)·256 over the vertices of all 32 .geo parts (s16 + part position/256, 1/256 m) of the geo as
    /// left it (<see cref="CarGeo.ApplyWheelShift"/>); radius +0x114 [ref:2125]
    /// √(√(w²+h²)² + l²) with the fixed-point square root on the /256 values (then keeps them; it only fills zeros). [ref:2126]
    /// </summary>
    public static void BodyFromModel(CarObj c, CarDefinition def, string name, int skill, CarGeo? geo)
    {
        if (def.Traits.BodyMass is { } bodyMass)
        {
            int m = bodyMass;
            if (name == "SIL_SPD") m <<= 2;
            c.Mass = m;
            c.InvMass = Fx.Div(Fx.One, m);
            if ((c.Flags & CarFlags.NotLocal) != 0)
            {
                if (skill == 0) { c.Inertia = m * 4 / 2; c.Mass = Fx.Mul(m, 0xD999); }
                else c.Inertia = m * 6 / 2;
            }
            else c.Inertia = skill == 0 ? m * 18 / 2 : m * 8 / 2;
            c.InvInertia = Fx.Div(Fx.One, c.Inertia);
        }
        if (geo == null)
        {
            if (def.Dimensions is { } dim) BoxFromDimensions(c, dim);
            return;
        }
        int minX = 0x630000, minY = 0x630000, minZ = 0x630000, maxX = unchecked((int)0xFF9D0000), maxY = maxX, maxZ = maxX;
        foreach (var p in geo.Parts)
        {
            int ox = Fx.DivPow2(p.X, 8), oy = Fx.DivPow2(p.Y, 8), oz = Fx.DivPow2(p.Z, 8);
            for (int v = 0; v < p.NumVerts; v++)
            {
                int x = p.Vertices[3 * v] + ox, y = p.Vertices[3 * v + 1] + oy, z = p.Vertices[3 * v + 2] + oz;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
        }
        c.HalfWidth = (maxX - minX) / 2 << 8;
        c.HalfHeight = (maxY - minY) / 2 << 8;
        c.HalfLength = (maxZ - minZ) / 2 << 8;
        static int Hyp(int a, int b) { int x = Fx.DivPow2(a, 8), y = Fx.DivPow2(b, 8); return FxMath.SqrtTab((uint)(x * x + y * y)); }
        c.Radius = Hyp(Hyp(c.HalfWidth, c.HalfHeight), c.HalfLength);
    }

    /// <summary>
    /// Collision box of a mod car without a .geo (car.json "dimensions"): same units as <see cref="BodyFromModel"/> (half size = (size in
    /// 1/256 m)/2 &lt;&lt; 8; x = width, y = height, z = length) and the same radius formula.
    /// </summary>
    static void BoxFromDimensions(CarObj c, CarDimensions d)
    {
        static int Half(double m) => (int)Math.Round(m * 256) / 2 << 8;
        c.HalfWidth = Half(d.WidthM);
        c.HalfHeight = Half(d.HeightM);
        c.HalfLength = Half(d.LengthM);
        static int Hyp(int a, int b) { int x = Fx.DivPow2(a, 8), y = Fx.DivPow2(b, 8); return FxMath.SqrtTab((uint)(x * x + y * y)); }
        c.Radius = Hyp(Hyp(c.HalfWidth, c.HalfHeight), c.HalfLength);
    }

    /// <summary>Maps the race class to the car flags field (+0x1F4). [ref:2127]</summary>
    public static CarFlags FlagsOf(int carClass) => carClass switch
    {
        1 => CarFlags.Racer | CarFlags.LocalHuman,
        2 => CarFlags.Racer | CarFlags.NotLocal | CarFlags.Opponent,
        4 => CarFlags.NotLocal | CarFlags.Traffic,
        8 => CarFlags.NotLocal | CarFlags.Cop,
        _ => CarFlags.Static,
    };

    private RaceCar CreateCar(RaceSetup s, int i, CarSetup cs, TrackSlice[] slices, bool back, TrackRoadQuery road)
    {
        var def = Car(cs.CarType);
        string model = def.DisplayName;
        // the catalog holds the files for game option bit 4 clear; with it set, types 15..0x24 read "pjagr.dat" [ref:2128]
        int options = s.Get(0x20);
        // a mod car with SI physics (car.json "physics") is converted here: the template car of its class comes from CARDATA.VIV
        var raw = def.Physics is SiPhysics si ? SiPhysicsConverter.ToRaw(si, def.Traits.RaceClass, CarFile) : def.Physics as RawPhysics;
        var specsBytes = (options & 4) != 0 && cs.CarType is >= 0xF and <= 0x24
            ? CarFile(SpecsFile(cs.CarType, model, options)) : raw?.CarSpecs.ToArray();
        var specs = specsBytes is { Length: CarSpecs.Size } sd ? CarSpecs.Parse(sd) : null;
        var ai = raw?.CarAiModel.ToArray() is { Length: CarAiModel.Size } ad ? CarAiModel.Parse(ad) : null;
        var geo = CarFile($"{model}.geo") is { } gd ? CarGeo.Parse(gd) : null;
        geo?.ApplyWheelShift(def.Traits.WheelShift, def.Traits.RearWheelShift); // CarTraits; patches the wheels per car type before the box is read [ref:2129]
        bool racer = cs.CarClass is 1 or 2;
        bool traffic = cs.CarClass == 4;
        var obj = new CarObj
        {
            Active = 1,
            Id = i | 0x100,                   // every configured car (traffic included): index | 0x100 [ref:2130]
            PlayerIndex = i,                  // +0x1E8 = index [ref:2131]
            Flags = FlagsOf(cs.CarClass),
            ModelName = model.ToLowerInvariant(),
            Config = cs,
            Specs = specs,
            Mass = 10 * Fx.One,
            Inertia = 15 * Fx.One,
            CatchUp = Fx.One,
            Glue = Fx.One,
            UpDotN = Fx.One,                  // +0x100 = 1.0 (the initial value) [ref:2132]
        };
        if (racer || traffic) BodyFromModel(obj, def, CarName(s, i), s.Skill, geo); // (traffic: same body path) [ref:2133]
        int slot = s.GridSlot(i), slice;
        Vec3 local;
        if (racer)
        {
            (slice, local) = GridPosition(slot, s.NumPlayerCars + s.NumOpponentCars, back, slices, s.Track);
        }
        else
        {
            // the code below places every car (traffic included) on its grid slot [ref:2134]
            // the runner then parks the class-4 car at once. Backward, a slot ≥ the racer count gives a negative [ref:2135]
            // slice index in the original (reads outside) – clamped here; the car is hidden anyway. [ref:2136]
            (slice, local) = GridPosition(slot, s.NumPlayerCars + s.NumOpponentCars, back, slices, s.Track);
            slice = Math.Clamp(slice, 0, slices.Length - 1);
        }
        NewtonPlace.PlaceOnSlice(obj, slices, slice, local, back, road.UpdateCar);
        obj.PrevSlice = (ushort)slice;
        obj.Lap = 0;
        return new RaceCar(i, cs, model, obj, specs, ai, geo, slot, slice,
            def.Traits);
    }

    /// <summary>Specs file of a configured car: type < 15 "p<name>.dat", 15..0x24 "ptr<NN>.dat" [ref:2137]
    /// (with game option bit 4: "pjagr.dat"), above that "pcala.dat".</summary>
    public static string SpecsFile(int carType, string model, int gameOptions) => carType switch
    {
        < 0xF => $"p{model}.dat",
        <= 0x24 => (gameOptions & 4) != 0 ? "pjagr.dat" : $"ptr{carType:00}.dat",
        _ => "pcala.dat",
    };

    /// <summary>AI model file: type < 15 "<name>.dat", otherwise "STRD.dat" (the traffic model). [ref:2138]</summary>
    public static string AiModelFile(int carType, string model) => carType < 0xF ? $"{model}.dat" : "STRD.dat";

    /// <summary>One-way flag per track 0..8 (records of stride 0x1C) [ref:2139]
    /// records exist for track 0, track 2 … track 8; track 1 does not exist. [ref:2140]
    /// tracks 0, 6 and 8 are one-way (see <see cref="TrafficTrackParams"/>; their slices have no left lanes / half width 0 —
    /// an earlier reading of the records as consecutive tracks gave 0, 5, 7); other tracks get the value 1 when the high nibble of
    /// slice 1 +0x1F (+0x43) is 0. [ref:2141]</summary>
    public static bool IsOneWay(int track, TrackSlice[] slices) => track switch
    {
        < 0 => false, // caller without a track id: two-way (legacy behaviour)
        _ => TrafficTrackParams.For(track, slices, 0).OneWay,
    };

    /// <summary>slice = 5·slot + 2 (backward: 5·(n−1−slot) + 2), side from slot parity [ref:2142]
    /// (inverted backward), y = 0.5. Two-way road: x = odd ? +b21·0x8000/2 : −b20·0x8000/2 (slice +0x20/+0x21 =
    /// half widths in 0.5 m). One-way road: w = b21·0x8000, base ((b1F &amp; 0xF)/2)·w [ref:2143]
    /// x = base ± w/2 (odd → +).</summary>
    public static (int Slice, Vec3 Local) GridPosition(int slot, int racers, bool backward, TrackSlice[] slices, int track = -1)
    {
        int row = backward ? racers - 1 - slot : slot;
        int s = row * 5 + 2;
        bool odd = (slot & 1) != 0;
        if (backward) odd = !odd;
        var sl = slices[Math.Clamp(s, 0, slices.Length - 1)];
        int x;
        if (!IsOneWay(track, slices))
            x = odd ? sl.HalfWidthRight * 0x8000 / 2 : -(sl.HalfWidthLeft * 0x8000) / 2;
        else
        {
            int w = sl.HalfWidthRight * 0x8000;
            int baseX = (sl.Lanes & 0xF) / 2 * w;
            x = odd ? baseX + w / 2 : baseX - w / 2;
        }
        return (s, new Vec3(x, 0x8000, 0));
    }

}
