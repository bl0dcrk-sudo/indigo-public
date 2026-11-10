using System.Numerics;
using NFS.Core;
using NFS.Core.Tables;
using NFS.Data;
using NFS.Data.Track;
using NFS.Game.Cars;
using NFS.Game.Race;
using NFS.Game.Session;
using NFS.Render;

namespace NFS.Desktop;

/// <summary>
/// Race grid view (no MonoGame types, shared with the headless CLI tools (render-race)): RaceLoader → RaceSession, one
/// <see cref="CarInstance"/> per car at its grid pose (CarObj.Pos/Orient, 16.16) and a chase camera behind the
/// local human car. During a race (stage 5a.2) every per-car read comes from the latest host <see cref="RaceViewFrame"/>
/// (<see cref="CapturePoses"/> / <see cref="SnapPoses(RaceViewFrame)"/>); before the first frame – a standalone scene of the
/// headless tools – the loaded session's grid cars are read instead.
/// </summary>
public sealed class RaceScene
{
    public RaceSession Session { get; }
    public TrackRenderer Track { get; }
    public OriginalHud Hud { get; }
    public ResultScreen ResultScreen { get; }
    public RaceMap Map { get; }
    public Cockpit? Cockpit { get; }
    public (bool Map, bool Text) InitialHudVisibility { get; }
    public MapCar[] MapCars()
    {
        var cars = new MapCar[MapCarCount()];
        FillMapCars(cars);
        return cars;
    }
    int MapCarCount()
    {
        int n = 0;
        for (int id = 0; id < Session.Cars.Count; id++) if ((Session.Cars[id].Setup.CarClass & 3) != 0) n++;
        return n;
    }
    void FillMapCars(MapCar[] dest)
    {
        int k = 0;
        for (int id = 0; id < Session.Cars.Count; id++)
        {
            var c = Session.Cars[id];
            if ((c.Setup.CarClass & 3) == 0) continue;
            int i = Array.IndexOf(_sources, c);
            var v = View(id);
            dest[k++] = new MapCar(i >= 0 ? _cars[i].Position : ToWorld(v.Pos), c.Setup.Colour,
                _remoteSeat ? id == PlayerCarId : (v.Flags & CarFlags.LocalHuman) != 0);
        }
    }
    MapCar[] _mapCars = [];
    /// <summary><see cref="MapCars"/> into an array that is reused by the next call (the render loop draws it at once; no allocation per frame).</summary>
    internal MapCar[] MapCarsShared()
    {
        int n = MapCarCount();
        if (_mapCars.Length != n) _mapCars = new MapCar[n];
        FillMapCars(_mapCars);
        return _mapCars;
    }
    public int MapPlayer => MapIndexOf(Session.Cars[PlayerCarId]);
    /// <summary>Split screen: index of the second human in <see cref="MapCars"/> (-1 without one).</summary>
    public int MapSecondPlayer => SecondPlayerInstance < 0 ? -1 : MapIndexOf(_sources[SecondPlayerInstance]);
    /// <summary>Position of <paramref name="car"/> among the cars shown on the map (-1 when it is not one of them).</summary>
    int MapIndexOf(RaceCar car)
    {
        int k = 0;
        for (int id = 0; id < Session.Cars.Count; id++)
        {
            var c = Session.Cars[id];
            if ((c.Setup.CarClass & 3) == 0) continue;
            if (Equals(c, car)) return k;
            k++;
        }
        return -1;
    }
    /// <summary>Surface types that keep tyre marks (<see cref="NFS.Core.Constants.RaceConstants.SkidMarkSurfaces"/>).</summary>
    public bool[] SkidSurfaces { get; }
    /// <summary>TRxx01.CAN, on a reverse track TRxx06.CAN (null when the file is missing). [ref:1473]</summary>
    public Can? FinishCan { get; }
    /// <summary>start camera animation CAN selected by race type and track direction. [ref:1474]</summary>
    public Can? IntroCan { get; }
    public int IntroCanIndex { get; }
    /// <summary>HUDCON.DAT +0x1C : camera view selected when the intro animation ends. [ref:1475]</summary>
    public int IntroReturnMode { get; internal set; }
    /// <summary>The HUDCON.DAT bytes the race started from (user copy, else the game's file, else empty): the base of the next snapshot.</summary>
    public byte[] HudconBaseline { get; }
    internal static Can? ParseIntroCan(byte[] bytes)
    {
        if (bytes.Length < 8) return null;
        int keys = BitConverter.ToUInt16(bytes, 4);
        if (keys < 2 || bytes.Length < 8 + 20 * keys) return null;
        return Can.Parse(bytes);
    }

    public IReadOnlyList<CarInstance> Cars => _cars;
    public IReadOnlyList<CarInstance> Smackables => _smackables;
    /// <summary>draws only cars with +0x8D (physics active) set: a parked (hidden) traffic car is not drawn. [ref:1476]</summary>
    public IEnumerable<CarInstance> RenderCars => _cars.Where((_, i) => InstanceView(i).Active != 0).Concat(_smackables);
    /// <summary><see cref="RenderCars"/> without the player's own car (cockpit views); the exclusion is by car index, so it [ref:1477]
    /// must not be applied to the already filtered sequence.</summary>
    public IEnumerable<CarInstance> RenderCarsExceptPlayer =>
        _cars.Where((_, i) => i != PlayerInstance && InstanceView(i).Active != 0).Concat(_smackables);

    readonly ResetBlink[] _blink = Enumerable.Range(0, 64).Select(_ => new ResetBlink()).ToArray();

    /// <summary>for the main view: <see cref="RenderCars"/> / <see cref="RenderCarsExceptPlayer"/> with [ref:1478]
    /// cars still in their post-reset cooldown (+0x16C) skipped on every other race tick (<see cref="ResetBlink"/>).</summary>
    public IEnumerable<CarInstance> RenderCarsBlinking(int raceTick, bool paused, bool exceptPlayer)
        => RenderCarsBlinking(raceTick, paused, exceptPlayer ? PlayerInstance : -1);

    /// <summary>Same, leaving out the car of instance <paramref name="exceptInstance"/> (the view's own car in a cockpit/hood view; -1 = none).</summary>
    public IEnumerable<CarInstance> RenderCarsBlinking(int raceTick, bool paused, int exceptInstance)
    {
        var list = new List<CarInstance>();
        FillRenderCars(list, raceTick, paused, exceptInstance);
        return list;
    }

    /// <summary><see cref="RenderCarsBlinking(int, bool, int)"/> into <paramref name="list"/> (cleared first), so the renderer can reuse one list per frame.</summary>
    internal void FillRenderCars(List<CarInstance> list, int raceTick, bool paused, int exceptInstance)
    {
        list.Clear();
        for (int i = 0; i < _cars.Length; i++)
        {
            var obj = InstanceView(i);
            if (obj.Active == 0 || i == exceptInstance) continue;
            if (_blink[i].Visible(obj.CollisionCooldown, raceTick, paused)) list.Add(_cars[i]);
        }
        for (int i = 0; i < _smackables.Length; i++) list.Add(_smackables[i]);
    }

    /// <summary>Mirror car: range 150m, LOD1 below 0x1AAAAA, otherwise LOD2. [ref:1479]
    /// The camera car is excluded in view1; airborne/crashing cars retain LOD1. [ref:1480]</summary>
    public IEnumerable<CarInstance> MirrorCars(Camera camera)
    {
        var list = new List<CarInstance>();
        FillMirrorCars(list, camera);
        return list;
    }

    CarInstance?[]? _lodSource;
    CarInstance?[]? _lodVariants;   // [2 * i + lod - 1]: the LOD1 / LOD2 copy of instance i, kept while _cars[i] is the same object

    /// <summary><see cref="MirrorCars"/> into <paramref name="list"/> (cleared first); the LOD copies of a car are reused while its pose object stays the same.</summary>
    internal void FillMirrorCars(List<CarInstance> list, Camera camera)
    {
        list.Clear();
        _lodSource ??= new CarInstance?[_cars.Length];
        _lodVariants ??= new CarInstance?[_cars.Length * 2];
        for (int i = 0; i < _cars.Length; i++)
        {
            if (i == PlayerInstance || InstanceView(i).Active == 0) continue;
            var car = _cars[i];
            var d = car.Position - camera.Position;
            float x = MathF.Abs(d.X), z = MathF.Abs(d.Z);
            if (MathF.Max(x, z) + MathF.Min(x, z) / 4 > 150) continue;
            var obj = InstanceView(i);
            int lod = camera.ToView(car.Position).Z < 0x1AAAAA / 65536f || obj.CrashSlice != 0 || obj.UpDotN < 0xCCCC ? 1 : 2;
            if (!ReferenceEquals(_lodSource[i], car)) { _lodSource[i] = car; _lodVariants[2 * i] = _lodVariants[2 * i + 1] = null; }
            var variant = _lodVariants[2 * i + lod - 1] ??= car with { Lod = lod };
            variant.SetPose(car.Model, car.Position, car.Right, car.Up, car.Forward);   // the car is updated in place, so its LOD copy follows it
            list.Add(variant);
        }
        for (int i = 0; i < _smackables.Length; i++) list.Add(_smackables[i]);
    }
    readonly CarInstance[] _smackables;
    readonly CarObj[] _smackableSources;
    readonly PoseHistory[] _smackablePoses;
    readonly CarInstance[] _cars;
    /// <summary>Per instance: CarId (index in <see cref="RaceSession.Cars"/>).</summary>
    readonly int[] _carIds;
    RaceViewFrame? _frame;
    readonly CarAnim[] _anims;
    readonly GameTables _tables;
    readonly PoseHistory[] _poses;
    readonly PoseHistory _playerPose;
    readonly bool _remoteSeat;
    /// <summary>Index of the player car's instance in <see cref="Cars"/> (-1 = player not drawn).</summary>
    public int PlayerInstance { get; } = -1;
    /// <summary>Split screen (COMMMODE 1): instance of the second human car, -1 for one player.</summary>
    public int SecondPlayerInstance { get; } = -1;
    /// <summary>The second human's car (null without split screen).</summary>
    public RaceCar? SecondPlayerCar => SecondPlayerInstance < 0 ? null : _sources[SecondPlayerInstance];
    internal CarAnim? SecondPlayerAnimation => SecondPlayerInstance >= 0 ? _anims[SecondPlayerInstance] : null;
    /// <summary>Set by <see cref="CapturePoses"/>: the second human's pose jumped (reset/teleport) in the last captured step.</summary>
    public bool SecondSnapped { get; private set; }
    internal CarAnim? PlayerAnimation => PlayerInstance >= 0 ? _anims[PlayerInstance] : null;

    public RaceScene(string source, string? gameDir = null, string? hudconDir = null)
        : this(LoadSession(source, gameDir), gameDir, hudconDir: hudconDir) { }

    private static RaceSession LoadSession(string source, string? gameDir)
    {
        var dir = GameFiles.FindGameDir(gameDir);
        return LoadSession(RaceInfo.Setup(source, dir), dir);
    }

    /// <summary>
    /// Loads a setup built from a source file (CLI / --race). Legacy PREFS files name traffic car types (15, 16) whose carSpecs
    /// file (ptrNN.dat) is not in CARDATA: such a car has no sim and the AI runner drops it (its ids then exceed the scan list,
    /// IndexOutOfRange in RaceRunner). They are re-typed to the last built-in car (14) so every configured car gets a sim.
    /// Setups whose cars all have specs (the menu, REPLAY.RPY, CONFIG.DAT) load unchanged.
    /// </summary>
    public static RaceSession LoadSession(RaceSetup setup, string? gameDir)
    {
        var loader = new RaceLoader(gameDir);
        var session = loader.Load(setup);
        var missing = session.Cars.Where(c => c.Specs == null && c.Setup.CarType >= 15).Select(c => c.Index).ToList();
        if (missing.Count == 0) return session;
        foreach (int i in missing) setup.Set(0x78 + i * NFS.Data.FrontEnd.RaceConfig.CarRecordSize, 14);
        return loader.Load(setup);
    }

    /// <summary>The menu passes its loaded session directly, preserving selected options and car objects.</summary>
    public RaceScene(RaceSession session, string? gameDir = null, int? playerCarId = null, string? hudconDir = null)
    {
        _remoteSeat = playerCarId.HasValue;
        var dir = GameFiles.FindGameDir(gameDir);
        Session = session;
        Hud = new OriginalHud(dir, session.Setup.Get(0x3C));
        ResultScreen = new ResultScreen(dir, session.Setup.Get(0x3C));
        const string hudcon = "FEDATA/PC/CONFIG/HUDCON.DAT";
        // The user copy (written by the previous race, HudConPersistence) wins over the file shipped with the game.
        var userHud = HudConPersistence.Load(hudconDir);
        var gameHud = userHud ?? (GameFiles.Exists(hudcon, dir) ? GameFiles.ReadRaw(hudcon, dir) : []);
        HudconBaseline = gameHud;
        InitialHudVisibility = OriginalHud.Visibility(session.Setup.Get(0x1C) == 0 ? gameHud : []);
        // The frozen network setup carries CommMode 1 (the server builds it from the split-screen menu branch), the original had > 1 there: [ref:1481]
        // a remote seat reads the camera view like any non-split race.
        if ((session.Setup.CommMode != 1 || _remoteSeat) && gameHud.Length == NFS.Data.FrontEnd.HudConfig.Size)
        {
            int mode = BitConverter.ToInt32(gameHud, NFS.Data.FrontEnd.HudConfig.CameraOffset);
            IntroReturnMode = mode is >= 0 and <= 3 ? mode : 0;
        }
        var finishCan = $"GAMEDATA/TRACKS/DATA/PC/TR{session.TrackNumber:00}{(session.Backward ? 6 : 1):00}.CAN";
        FinishCan = GameFiles.Exists(finishCan, dir) ? Can.Load(finishCan, dir) : null;
        IntroCanIndex = RaceCamera.StartCanIndex(session.Setup.RaceType, session.Setup.CommMode, session.Backward);
        var introCan = $"GAMEDATA/TRACKS/DATA/PC/TR{session.TrackNumber:00}{IntroCanIndex:00}.CAN";
        if (GameFiles.Exists(introCan, dir))
        {
            IntroCan = ParseIntroCan(GameFiles.Read(introCan, dir));
        }
        SkidSurfaces = SkidMarkTask.SurfaceTable();
        Track = new TrackRenderer(Session.TrackNumber, dir);
        var models = new Dictionary<string, CarModel>(StringComparer.OrdinalIgnoreCase);
        var cars = new List<CarInstance>();
        var sources = new List<RaceCar>();
        var carIds = new List<int>();
        var anims = new List<CarAnim>();
        var skins = new List<CarModel[]>();
        _tables = GameTables.Default;
        var player = playerCarId is { } playerId && (uint)playerId < (uint)Session.Cars.Count
            ? Session.Cars[playerId] : PlayerCar(Session.Cars);
        // e.g. VPILOT.TXT: a controls file in the PREFS folder, no cars in it
        if (player == null) throw new InvalidDataException("race has no player car (not a race PREFS file?)");
        Map = new RaceMap(Session.Slices, dir, player.Setup.CarType);
        // CarTraits.Cockpit (openspec modding-api-cars D3); a race car without traits (hand-built sessions) keeps the vanilla rule
        var cockpitStyle = player.Traits is { } playerTraits ? playerTraits.Cockpit.ToStyle()
            : NFS.Render.Cockpit.HasDashboard(player.Setup.CarType) ? NFS.Render.CockpitStyle.Vanilla(player.Setup.CarType) : (NFS.Render.CockpitStyle?)null;
        if (cockpitStyle is { } style)
            Cockpit = new Cockpit(player.Setup.CarType, dir, player.Setup.Colour, style);
        if (userHud != null && session.Setup.Get(0x1C) == 0) Cockpit?.RestoreMirror(userHud);
        for (int id = 0; id < Session.Cars.Count; id++)
        {
            var c = Session.Cars[id];
            // a mod car (openspec modding-api-cars 4.5) has no .geo: it is drawn from the glTF of its definition, painted with the configured colour index
            var modDef = c.Geo == null && Session.Catalog is { } catalog && catalog.TryGetByNumber(c.Setup.CarType, out var cd) && cd.ModelPath != null ? cd : null;
            if (modDef == null && (c.Geo == null || string.IsNullOrEmpty(c.Model))) continue;
            // every car is painted with its configured colour (+0xC of the config record); traffic [ref:1482]
            // cars hold three palette variants, the shown one is CarObj.TrafficSkin. [ref:1483]
            var paints = modDef != null ? [c.Setup.Colour]
                : c.Traits is { } traits
                ? CarModel.PaintVariants(c.Setup.CarType, c.Setup.Colour, traits.TrafficPaintVariants)
                : CarModel.PaintVariants(c.Setup.CarType, c.Setup.Colour);
            CarModel? m;
            var variants = new CarModel[paints.Length];
            try
            {
                for (int v = 0; v < paints.Length; v++)
                {
                    string key = $"{c.Model}|{c.Setup.CarType}|{paints[v]}";
                    if (!models.TryGetValue(key, out var vm))
                        models[key] = vm = modDef != null
                            ? new CarModel(modDef.DisplayName, ModCarModel.Load(modDef, paints[v]))
                            : new CarModel(c.Model, dir, carType: c.Setup.CarType, paintIndex: paints[v],
                            wheelShift: c.Traits?.WheelShift, rearWheelShift: c.Traits?.RearWheelShift);
                    variants[v] = vm;
                }
            }
            catch (Exception e) when (e is IOException or KeyNotFoundException or InvalidDataException) { continue; }
            m = variants[0];
            skins.Add(variants);
            if (ReferenceEquals(c, player)) PlayerInstance = cars.Count;
            else if (!_remoteSeat && session.Setup.CommMode == 1 && SecondPlayerInstance < 0 && c.Setup.CarClass == 1) SecondPlayerInstance = cars.Count;
            var anim = CarAnim.For(c.Setup.CarType, _tables, seed: cars.Count, fullAngleSteer: c.Traits?.FullAngleSteer);
            anims.Add(anim);
            cars.Add(Instance(m, c.Obj.Pos, c.Obj.Orient) with { Anim = anim });
            sources.Add(c);
            carIds.Add(id);
        }
        _cars = cars.ToArray();
        _skins = skins.ToArray();
        _anims = anims.ToArray();
        _sources = sources.ToArray();
        _carIds = carIds.ToArray();
        PlayerCarId = Session.Cars.ToList().IndexOf(player!);
        _poses = _sources.Select(c => new PoseHistory(RenderPose.From(c.Obj.Pos, c.Obj.Orient))).ToArray();
        // the SMnn.DAT record selects its own model. The pose belongs [ref:1484]
        // to the live parked CarObj, which StepParked can move after a collision.
        var smackables = new List<CarInstance>();
        var records = session.Smackables?.Cars.Take(session.Setup.NumSmackables).ToArray() ?? [];
        _smackableSources = session.Parked?.ToArray() ?? [];
        var loader = new RaceLoader(dir);
        for (int i = 0; i < _smackableSources.Length; i++)
        {
            string name = loader.ModelName(records[i].Model);
            var smackTraits = loader.Catalog.TryGetByNumber(records[i].Model, out var smackDef) ? smackDef.Traits : null;
            if (!models.TryGetValue(name, out var model))
                models[name] = model = new CarModel(name, dir, carType: records[i].Model, paintIndex: 0,
                    wheelShift: smackTraits?.WheelShift, rearWheelShift: smackTraits?.RearWheelShift); // record +0xC = 0 -> colour 0 [ref:1485]
            var obj = _smackableSources[i];
            smackables.Add(Instance(model, obj.Pos, obj.Orient));
        }
        _smackables = smackables.ToArray();
        _smackablePoses = _smackableSources.Select(c => new PoseHistory(RenderPose.From(c.Pos, c.Orient))).ToArray();
        _playerPose = PlayerInstance >= 0 ? _poses[PlayerInstance] :
            new PoseHistory(RenderPose.From(player!.Obj.Pos, player.Obj.Orient));
    }

    /// <summary>Moves the player's render instance to the CarObj pose (driving).</summary>
    public void SetPlayerPose(Vec3 pos, Mat3 o)
    {
        if (PlayerInstance >= 0) _cars[PlayerInstance] = Instance(SkinModel(PlayerInstance), pos, o) with { Anim = _anims[PlayerInstance], Ground = GroundFor(PlayerInstance) };
    }

    private readonly RaceCar[] _sources;
    readonly CarModel[][] _skins;

    /// <summary>The palette variant of car i that is shown now (+0x618 = +0x61C[+0x634]); one entry for non-traffic cars.</summary>
    CarModel SkinModel(int i) => _skins[i][Math.Min(InstanceView(i).TrafficSkin, _skins[i].Length - 1)];

    /// <summary>CarId (index in <see cref="RaceSession.Cars"/>) of the local player car.</summary>
    public int PlayerCarId { get; }

    /// <summary>Latest frame consumed by the scene (null for a standalone scene).</summary>
    public RaceViewFrame? Frame => _frame;

    /// <summary>State of car <paramref name="carId"/>: from the latest frame; without one (standalone scene) a copy of the loaded grid car.</summary>
    public CarView View(int carId) => _frame?.Cars[carId].State ?? CarView.From(Session.Cars[carId].Obj);

    CarView InstanceView(int instance) => View(_carIds[instance]);

    /// <summary>CarId of render instance <paramref name="instance"/>.</summary>
    public int CarIdOf(int instance) => _carIds[instance];

    /// <summary>Standalone scene (headless tools without a host): moves every render instance to its CarObj pose.</summary>
    public void SyncAllPoses()
    {
        for (int i = 0; i < _cars.Length; i++) _cars[i] = Instance(SkinModel(i), _sources[i].Obj.Pos, _sources[i].Obj.Orient) with { Anim = _anims[i], Ground = GroundFor(i) };
        for (int i = 0; i < _smackables.Length; i++)
            _smackables[i] = Instance(_smackables[i].Model, _smackableSources[i].Pos, _smackableSources[i].Orient);
    }

    /// <summary>Consumes one published Step/Reset frame (every catch-up step has its own): car animations advance one tick and
    /// every pose history gets the new pose; a changed <see cref="CarFrame.PoseEpoch"/> snaps instead of blending.
    /// Returns whether the player's pose snapped (<see cref="SecondSnapped"/> for the second human).</summary>
    public bool CapturePoses(RaceViewFrame frame)
    {
        _frame = frame;
        bool playerSnapped = false;
        for (int i = 0; i < _anims.Length; i++) _anims[i].Tick(InstanceView(i), _tables);   // render-only, reads the copied view
        for (int i = 0; i < _poses.Length; i++)
        {
            var cf = frame.Cars[_carIds[i]];
            bool snapped = _poses[i].Push(RenderPose.From(cf.State.Pos, cf.State.Orient), cf.PoseEpoch);
            if (i == PlayerInstance) playerSnapped = snapped;
            if (i == SecondPlayerInstance) SecondSnapped = snapped;
        }
        if (PlayerInstance < 0)
        {
            var pf = frame.Cars[PlayerCarId];
            playerSnapped = _playerPose.Push(RenderPose.From(pf.State.Pos, pf.State.Orient), pf.PoseEpoch);
        }
        for (int i = 0; i < _smackablePoses.Length; i++)
        {
            var sf = frame.Parked[i];
            _smackablePoses[i].Push(RenderPose.From(sf.State.Pos, sf.State.Orient), sf.PoseEpoch);
        }
        return playerSnapped;
    }

    /// <summary>Selects the HUD/visibility/durable data frame after pose histories have been advanced to its interpolation pair.</summary>
    internal void UsePresentationFrame(RaceViewFrame frame) => _frame = frame;

    // Remote animation cadence is presentation time, independent of snapshot delivery/loss.
    internal void TickRemoteAnimations(RaceViewFrame frame)
    {
        for (int i = 0; i < _anims.Length; i++) _anims[i].Tick(frame.Car(_carIds[i]), _tables);
    }

    public RenderPose PresentPoses(double fraction)
    {
        for (int i = 0; i < _cars.Length; i++)
        {
            var p = _poses[i].Sample(fraction);
            var ground = GroundFor(i);
            if (_cars[i].Anim == _anims[i] && _cars[i].Lod == 0) { _cars[i].SetPose(SkinModel(i), p.Position, p.Right, p.Up, p.Forward); _cars[i].Ground = ground; }
            else _cars[i] = new CarInstance(SkinModel(i), p.Position, p.Right, p.Up, p.Forward, Anim: _anims[i]) { Ground = ground };
        }
        for (int i = 0; i < _smackables.Length; i++)
        {
            var p = _smackablePoses[i].Sample(fraction);
            var sm = _smackables[i];
            if (sm.Anim == null && sm.Lod == 0) sm.SetPose(sm.Model, p.Position, p.Right, p.Up, p.Forward);
            else _smackables[i] = new CarInstance(sm.Model, p.Position, p.Right, p.Up, p.Forward);
        }
        return _playerPose.Sample(fraction);
    }

    /// <summary>Instance index of car <paramref name="carId"/> in <see cref="Cars"/> (-1 when it has none).</summary>
    public int InstanceIndex(int carId) => Array.IndexOf(_carIds, carId);

    /// <summary>Index of car <paramref name="carId"/> in <see cref="MapCars"/> (-1 for cars not shown on the map).</summary>
    public int MapIndex(int carId) => MapIndexOf(Session.Cars[carId]);

    internal CarAnim? AnimationOf(int instance) => instance >= 0 ? _anims[instance] : null;

    /// <summary>Interpolated pose of car instance <paramref name="instance"/> (after <see cref="PresentPoses"/>; split-screen camera of the second human).</summary>
    public RenderPose PoseOf(int instance, double fraction) => _poses[instance].Sample(fraction);

    /// <summary>Places every instance exactly at the poses of <paramref name="frame"/> (race start, after a deterministic run).</summary>
    public void SnapPoses(RaceViewFrame frame)
    {
        _frame = frame;
        for (int i = 0; i < _poses.Length; i++)
        {
            var cf = frame.Cars[_carIds[i]];
            _poses[i].Snap(RenderPose.From(cf.State.Pos, cf.State.Orient), cf.PoseEpoch);
        }
        var pf = frame.Cars[PlayerCarId];
        _playerPose.Snap(RenderPose.From(pf.State.Pos, pf.State.Orient), pf.PoseEpoch);
        for (int i = 0; i < _smackablePoses.Length; i++)
        {
            var sf = frame.Parked[i];
            _smackablePoses[i].Snap(RenderPose.From(sf.State.Pos, sf.State.Orient), sf.PoseEpoch);
        }
        PresentPoses(1);
    }

    public static Vector3 ToWorld(Vec3 v) => new(v.X / (float)Fx.One, v.Y / (float)Fx.One, v.Z / (float)Fx.One);

    /// <summary>Road plane under a car for the shadow, from the published state (null on rails, or without a usable road normal): the plane of the sim,
    /// <c>Pos − n·(HalfHeight + HeightAboveRoad)</c> (the bottom of the collision box, Newton.PlaneDist). The model is drawn around <see cref="CarView.Pos"/> like
    /// the original (see <see cref="CarModel.PartOrigin"/>), so its tyres rest on this plane. The sim measures <see cref="CarView.HeightAboveRoad"/> at the start
    /// of the step, before the position is integrated (<c>Vel/32</c> per step), so the plane is anchored to the pre-integration position, which it fits exactly.</summary>
    public static CarGround? GroundOf(CarView v)
    {
        if (v.Rail != 0) return null;
        var n = ToWorld(v.RoadFrame.Row1);
        float len = n.Length();
        if (len < 0.5f) return null;
        n /= len;
        float above = Math.Max(0, v.HeightAboveRoad) / (float)Fx.One;
        float half = v.HalfHeight / (float)Fx.One;
        var start = ToWorld(v.Pos) - ToWorld(v.Vel) / 32f;
        return new CarGround(start - n * (half + above), n);
    }

    CarGround? GroundFor(int instance) => GroundOf(InstanceView(instance));

    /// <summary>CarObj pose (Pos 16.16, Orient rows right/up/forward, 1.0 = Fx.One) → render instance.</summary>
    public static CarInstance Instance(CarModel m, Vec3 pos, Mat3 o) =>
        new(m, ToWorld(pos), ToWorld(o.Row0), ToWorld(o.Row1), ToWorld(new Vec3(o.M20, o.M21, o.M22)));

    /// <summary>Chase camera: <paramref name="back"/> m behind and <paramref name="height"/> m above the car, looking along its forward.</summary>
    public static Camera ChaseCamera(Vec3 pos, Mat3 o, float back = 6f, float height = 2f, int width = 640, int height2 = 480)
    {
        var p = ToWorld(pos);
        var up = Vector3.Normalize(ToWorld(o.Row1));
        var fwd = Vector3.Normalize(ToWorld(new Vec3(o.M20, o.M21, o.M22)));
        var cam = new Camera(width, height2, baseWidth: Camera.PanoramaBase(width, height2));
        cam.LookAlong(fwd, up);
        cam.Position = p - fwd * back + up * height;
        return cam;
    }

    /// <summary>Camera behind the player car (the local human, else the first car).</summary>
    public Camera PlayerCamera(int width = 640, int height = 480)
    {
        var player = PlayerCar(Session.Cars) ?? throw new InvalidOperationException("race has no cars");
        return ChaseCamera(player.Obj.Pos, player.Obj.Orient, width: width, height2: height);
    }

    /// <summary>Local human car (race config +0x14, LOCALCAR) is [ref:1486]
    /// flagged <see cref="CarFlags.LocalHuman"/> (class 1). It is <b>not</b> grid slot 0: the grid is rebuilt per
    /// series race (KO grid = entrants − previous place, tournament = previous place), so slot 0 [ref:1487]
    /// holds whichever car finished last. Split screen (two humans) is not ported; the first one wins.</summary>
    public static RaceCar? PlayerCar(IReadOnlyList<RaceCar> cars) =>
        cars.FirstOrDefault(c => (c.Obj.Flags & CarFlags.LocalHuman) != 0) ?? cars.FirstOrDefault();

    public void Render(FrameBuffer fb, RenderOptions? opt = null) =>
        Track.Render(fb, PlayerCamera(fb.Width, fb.Height), opt ?? new RenderOptions(), RenderCars);
}
