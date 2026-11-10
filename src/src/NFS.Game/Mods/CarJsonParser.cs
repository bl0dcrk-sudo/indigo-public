using System.Text.Json;
using System.Text.RegularExpressions;
using NFS.Core.Constants;
using NFS.Data.Car;
using NFS.Game.Cars;
using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>
/// Parses <c>cars/&lt;id&gt;/car.json</c> into a <see cref="CarDefinition"/> (openspec modding-api-cars 3.4). Format (all paths relative to the
/// MOD directory, validated with <see cref="ModPaths"/>):
/// <code>
/// { "schemaVersion": 1, "id": "duster", "names": {"pl": "..", "en": ".."}, "descriptions": {"pl": "..", "en": ".."},
///   "manufacturer": "..", "year": 2010, "class": "A|B|C", "traits": {"abs": true, "skidMarks": false, "rearAeroBoost": false},
///   "engineBank": "MacF1", "model": "cars/duster/duster.glb",
///   "dimensions": {"lengthM": 4.34, "widthM": 1.80, "heightM": 1.69},   // optional: collision box when CARDATA.VIV has no &lt;name&gt;.geo
///   "colors": [{"names": {"pl": "..", "en": ".."}, "rgb": "#RRGGBB"}],  // optional: paint choices (index 0 = default), max 16
///   "paintMaterials": ["UV_body"],                                      // optional: glTF materials that take the paint (default: names containing "paint")
///   "bodyMass": 13.0,                                                   // optional: simulation body mass (original table: mass/100), default = mass/100
///   "physics": { SI fields of SiPhysics in camelCase }      // exactly one of physics / raw
///   "raw": {"specs": "cars/duster/p.dat", "ai": "cars/duster/ai.dat"} }
/// </code>
/// DisplayName = <c>names.en</c>, else <c>names.pl</c> (the game has no per-language car name yet). <c>engineBank</c> = one of
/// <see cref="AudioConstants.PlayerBanks"/> (case-insensitive); the split-screen and opponent banks are the matching entries of the other two
/// tables. SI physics is range checked at load by <see cref="SiPhysicsConverter"/> but kept as <see cref="SiPhysics"/> in the definition; the
/// race loader converts it later (it needs the template car from CARDATA.VIV).
/// </summary>
public static class CarJsonParser
{
    static readonly Regex IdPattern = new("^[a-z0-9_-]{1,48}$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Known = new(StringComparer.Ordinal)
        { "schemaVersion", "id", "names", "descriptions", "manufacturer", "year", "class", "traits", "engineBank", "model", "physics", "raw", "dimensions", "bodyMass", "colors", "paintMaterials" };
    const int MaxColors = 16;
    static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> KnownTraits = new(StringComparer.Ordinal) { "abs", "skidMarks", "rearAeroBoost" };

    /// <summary>On failure: false and <paramref name="error"/> names the field. Unknown fields only add warnings to <paramref name="diag"/>.</summary>
    public static bool TryParse(string json, string modId, string modDir, string carDirName, ModDiagnostics diag,
        out CarDefinition? car, out string? error)
    {
        car = null;
        error = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { error = "invalid JSON: " + e.Message; return false; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "car.json must be a JSON object"; return false; }
            foreach (var p in root.EnumerateObject())
                if (!Known.Contains(p.Name)) diag.Warning(modId, $"cars/{carDirName}/car.json: unknown field '{p.Name}' ignored");

            if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out int ver))
            { error = "field 'schemaVersion' is required (integer)"; return false; }
            if (ver != CarDefinition.SchemaVersion)
            { error = $"field 'schemaVersion' is {ver}, this game reads {CarDefinition.SchemaVersion}"; return false; }

            var id = Str(root, "id", true, out error);
            if (id is null) return false;
            if (!IdPattern.IsMatch(id)) { error = "field 'id' must be 1-48 characters of lowercase letters, digits, '-', '_'"; return false; }
            if (id != carDirName) { error = $"field 'id' '{id}' must equal the directory name '{carDirName}'"; return false; }
            if (!CarKey.TryParse($"{modId}/{id}", out var key)) { error = $"'{modId}/{id}' is not a valid car key"; return false; }

            if (!Texts(root, "names", true, out var names, out error)) return false;
            string display = names.GetValueOrDefault("en") ?? names.GetValueOrDefault("pl") ?? "";
            if (display.Length == 0) { error = "field 'names' needs a non-empty 'en' or 'pl'"; return false; }
            if (!Texts(root, "descriptions", false, out var descriptions, out error)) return false;
            var manufacturer = Str(root, "manufacturer", false, out error);
            if (error is not null) return false;
            int? year = null;
            if (root.TryGetProperty("year", out var y))
            {
                if (y.ValueKind != JsonValueKind.Number || !y.TryGetInt32(out int yi) || yi is < 1880 or > 2100)
                { error = "field 'year' must be an integer 1880..2100"; return false; }
                year = yi;
            }

            var cls = Str(root, "class", true, out error);
            if (cls is null) return false;
            CarRaceClass raceClass;
            switch (cls)
            {
                case "A": raceClass = CarRaceClass.A; break;
                case "B": raceClass = CarRaceClass.B; break;
                case "C": raceClass = CarRaceClass.C; break;
                case "D": raceClass = CarRaceClass.D; break;
                case "E": raceClass = CarRaceClass.E; break;
                case "F": raceClass = CarRaceClass.F; break;
                case "G": raceClass = CarRaceClass.G; break;
                default: error = $"field 'class' must be A, B, C, D, E, F or G (got '{cls}')"; return false;
            }

            bool abs = false, skid = false, aero = false;
            if (root.TryGetProperty("traits", out var tr))
            {
                if (tr.ValueKind != JsonValueKind.Object) { error = "field 'traits' must be an object"; return false; }
                foreach (var p in tr.EnumerateObject())
                {
                    if (!KnownTraits.Contains(p.Name)) { diag.Warning(modId, $"cars/{carDirName}/car.json: unknown trait '{p.Name}' ignored"); continue; }
                    if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"field 'traits.{p.Name}' must be true or false"; return false; }
                    bool v = p.Value.GetBoolean();
                    if (p.Name == "abs") abs = v; else if (p.Name == "skidMarks") skid = v; else aero = v;
                }
            }

            var bankName = Str(root, "engineBank", true, out error);
            if (bankName is null) return false;
            int bi = -1;
            for (int i = 0; i < AudioConstants.PlayerBanks.Count; i++)
                if (string.Equals(AudioConstants.PlayerBanks[i], bankName, StringComparison.OrdinalIgnoreCase)) { bi = i; break; }
            if (bi < 0)
            { error = $"field 'engineBank' '{bankName}' is not a known bank ({string.Join(", ", AudioConstants.PlayerBanks)})"; return false; }

            var modelRel = Str(root, "model", true, out error);
            if (modelRel is null) return false;
            if (!ModPaths.TryResolveFile(modDir, modelRel, out string modelFull, out var pe)) { error = $"field 'model': {pe}"; return false; }
            if (!File.Exists(modelFull)) { error = $"field 'model': file '{modelRel}' not found"; return false; }
            if (!modelRel.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) { error = "field 'model' must be a .glb file"; return false; }
            if (!ModPaths.TryValidateGlb(modelFull, out var ge)) { error = $"field 'model': {ge}"; return false; }

            CarDimensions? dims = null;
            if (root.TryGetProperty("dimensions", out var dm))
            {
                if (dm.ValueKind != JsonValueKind.Object) { error = "field 'dimensions' must be an object"; return false; }
                string? derr = null;
                double Dim(string n)
                {
                    if (derr is not null) return 0;
                    if (!dm.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Number) { derr = $"dimensions: field '{n}' is required (number)"; return 0; }
                    double d = v.GetDouble();
                    if (!(d >= 0.5 && d <= 20)) { derr = $"dimensions: field '{n}' must be 0.5..20 m"; return 0; }
                    return d;
                }
                double l = Dim("lengthM"), w = Dim("widthM"), h = Dim("heightM");
                if (derr is not null) { error = derr; return false; }
                dims = new CarDimensions(l, w, h);
            }
            double? bodyMassOverride = null;
            if (root.TryGetProperty("bodyMass", out var bmEl))
            {
                if (bmEl.ValueKind != JsonValueKind.Number || !(bmEl.GetDouble() is >= 0.5 and <= 200))
                { error = "field 'bodyMass' must be a number 0.5..200"; return false; }
                bodyMassOverride = bmEl.GetDouble();
            }

            var colors = new List<CarColor>();
            if (root.TryGetProperty("colors", out var cl))
            {
                if (cl.ValueKind != JsonValueKind.Array || cl.GetArrayLength() > MaxColors) { error = $"field 'colors' must be an array of at most {MaxColors} items"; return false; }
                foreach (var c in cl.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) { error = "field 'colors' items must be objects {names, rgb}"; return false; }
                    var rgb = Str(c, "rgb", true, out error, "colors[].");
                    if (rgb is null) return false;
                    if (!ColorPattern.IsMatch(rgb)) { error = $"field 'colors[].rgb' '{rgb}' must look like #RRGGBB"; return false; }
                    if (!Texts(c, "names", false, out var cn, out error)) return false;
                    colors.Add(new CarColor(cn, Convert.ToByte(rgb[1..3], 16), Convert.ToByte(rgb[3..5], 16), Convert.ToByte(rgb[5..7], 16)));
                }
            }
            var paintMaterials = new List<string>();
            if (root.TryGetProperty("paintMaterials", out var pm))
            {
                if (pm.ValueKind != JsonValueKind.Array || pm.GetArrayLength() > 8) { error = "field 'paintMaterials' must be an array of at most 8 material names"; return false; }
                foreach (var m in pm.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(m.GetString())) { error = "field 'paintMaterials' must contain non-empty strings"; return false; }
                    paintMaterials.Add(m.GetString()!);
                }
            }

            bool hasSi = root.TryGetProperty("physics", out var phys), hasRaw = root.TryGetProperty("raw", out var raw);
            if (hasSi == hasRaw) { error = hasSi ? "give either 'physics' or 'raw', not both" : "one of 'physics' (SI) or 'raw' (CarSpecs files) is required"; return false; }
            CarPhysics physics;
            int bodyMass, topSpeed;
            if (hasSi)
            {
                if (!ParseSi(phys, out var si, out error)) return false;
                if (!SiPhysicsConverter.TryConvert(si!, out var fx, out error)) { error = $"physics: {error}"; return false; }
                physics = si!;
                bodyMass = fx!.Mass / 100;
                topSpeed = fx.MaxVelocity;
            }
            else
            {
                if (!ParseRaw(raw, modDir, out var rp, out error)) return false;
                var specs = CarSpecs.Parse(rp!.CarSpecs.ToArray());
                physics = rp;
                bodyMass = specs.Mass / 100;
                topSpeed = specs.MaxVelocity;
            }

            if (bodyMassOverride is { } bmo) bodyMass = (int)Math.Round(bmo * 65536);
            car = new CarDefinition(key, display, null, physics, new CarTraits
            {
                RaceClass = raceClass,
                HasAbs = abs,
                SkidMarks = skid,
                RearAeroBoost = aero,
                BodyMass = bodyMass,                       // original table: mass / 100 (MCF1: 1134 kg -> 11.34)
                AiTopSpeed = topSpeed,                     // original ModelVmax ~ CarSpecs.MaxVelocity
                EngineBank = new EngineBank(AudioConstants.PlayerBanks[bi], AudioConstants.SplitBanks[bi], AudioConstants.OpponentBanks[bi]),
            })
            { Descriptions = descriptions, Manufacturer = manufacturer, Year = year, ModelPath = modelFull, Dimensions = dims, Colors = colors, PaintMaterials = paintMaterials };
            return true;
        }
    }

    static bool ParseRaw(JsonElement e, string modDir, out RawPhysics? raw, out string? error)
    {
        raw = null;
        error = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "field 'raw' must be an object"; return false; }
        byte[]? specs = null, ai = null;
        foreach (var (name, size) in new[] { ("specs", RawPhysics.CarSpecsSize), ("ai", RawPhysics.CarAiModelSize) })
        {
            var rel = Str(e, name, true, out error, "raw.");
            if (rel is null) return false;
            if (!ModPaths.TryResolveFile(modDir, rel, out var full, out var pe)) { error = $"field 'raw.{name}': {pe}"; return false; }
            if (!File.Exists(full)) { error = $"field 'raw.{name}': file '{rel}' not found"; return false; }
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length != size) { error = $"field 'raw.{name}': '{rel}' is {bytes.Length} B, must be {size} B"; return false; }
            if (name == "specs") specs = bytes; else ai = bytes;
        }
        raw = new RawPhysics(specs!, ai!);
        return true;
    }

    static bool ParseSi(JsonElement e, out SiPhysics? si, out string? error)
    {
        si = null;
        error = null;
        if (e.ValueKind != JsonValueKind.Object) { error = "field 'physics' must be an object"; return false; }
        string? err = null;
        double? Num(string n, bool required)
        {
            if (err is not null) return null;
            if (!e.TryGetProperty(n, out var v))
            {
                if (required) err = $"physics: field '{n}' is required";
                return null;
            }
            if (v.ValueKind != JsonValueKind.Number) { err = $"physics: field '{n}' must be a number"; return null; }
            return v.GetDouble();
        }
        double? mass = Num("massKg", true), pkw = Num("powerKw", true), prpm = Num("powerRpm", true),
            tnm = Num("torqueNm", true), trpm = Num("torqueRpm", true), red = Num("redlineRpm", true),
            fin = Num("finalDrive", true), top = Num("topSpeedKmh", true), wb = Num("wheelbaseM", true),
            rev = Num("reverseGearRatio", false), rad = Num("wheelRadiusM", false),
            grip = Num("tyreGrip", false), brk = Num("brakeDecelerationMs2", false);
        if (err is not null) { error = err; return false; }
        var gears = new List<double>();
        if (!e.TryGetProperty("gearRatios", out var g) || g.ValueKind != JsonValueKind.Array) { error = "physics: field 'gearRatios' is required (array of numbers)"; return false; }
        foreach (var item in g.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) { error = "physics: field 'gearRatios' must contain only numbers"; return false; }
            gears.Add(item.GetDouble());
        }
        if (!e.TryGetProperty("drive", out var d) || d.ValueKind != JsonValueKind.String) { error = "physics: field 'drive' is required (front, rear or all)"; return false; }
        CarDrive drive;
        switch (d.GetString())
        {
            case "front": drive = CarDrive.Front; break;
            case "rear": drive = CarDrive.Rear; break;
            case "all": drive = CarDrive.All; break;
            default: error = $"physics: field 'drive' must be front, rear or all (got '{d.GetString()}')"; return false;
        }
        si = new SiPhysics
        {
            MassKg = mass!.Value, PowerKw = pkw!.Value, PowerRpm = prpm!.Value, TorqueNm = tnm!.Value, TorqueRpm = trpm!.Value,
            RedlineRpm = red!.Value, GearRatios = gears, ReverseGearRatio = rev ?? 0, FinalDrive = fin!.Value,
            WheelRadiusM = rad ?? new SiPhysics().WheelRadiusM, TopSpeedKmh = top!.Value, Drive = drive, WheelbaseM = wb!.Value,
            TyreGrip = grip, BrakeDecelerationMs2 = brk,
        };
        return true;
    }

    static string? Str(JsonElement e, string name, bool required, out string? error, string prefix = "")
    {
        error = null;
        if (!e.TryGetProperty(name, out var v))
        {
            if (required) error = $"field '{prefix}{name}' is required";
            return null;
        }
        if (v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
        {
            error = $"field '{prefix}{name}' must be a non-empty string";
            return null;
        }
        return v.GetString();
    }

    static bool Texts(JsonElement root, string name, bool required, out Dictionary<string, string> result, out string? error)
    {
        result = [];
        error = null;
        if (!root.TryGetProperty(name, out var o))
        {
            if (required) error = $"field '{name}' is required";
            return !required;
        }
        if (o.ValueKind != JsonValueKind.Object) { error = $"field '{name}' must be an object with 'pl' and/or 'en'"; return false; }
        foreach (var p in o.EnumerateObject())
        {
            if (p.Name is not ("pl" or "en")) { error = $"field '{name}.{p.Name}': only 'pl' and 'en' are allowed"; return false; }
            if (p.Value.ValueKind != JsonValueKind.String) { error = $"field '{name}.{p.Name}' must be a string"; return false; }
            result[p.Name] = p.Value.GetString()!;
        }
        return true;
    }
}
