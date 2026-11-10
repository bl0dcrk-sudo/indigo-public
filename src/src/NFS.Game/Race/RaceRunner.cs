using NFS.Core;
using NFS.Core.Tables;

namespace NFS.Game.Race;

/// <summary>One simulated car of the runner: object, derived specs, control filter state and (for AI) the driver.</summary>
public sealed class SimCar
{
    public required RaceCar Race { get; init; }
    public CarObj Obj => Race.Obj;
    public required DerivedCarSpecs Specs { get; init; }
    public required DriveControlSpecs Controls { get; init; }
    public required DriveOptions Options { get; init; }
    public required bool Automatic { get; init; }
    /// <summary>Null for the local player (input from <see cref="RaceRunner.Step"/>).</summary>
    public AiDriver? Ai { get; init; }
    public int WallHits { get; internal set; }
    public int EdgeHits { get; internal set; }
    public int CarHits { get; internal set; }
    public int StartSlice { get; init; }
    /// <summary>Unwrapped progress in slices since the start (stand-in for car <c>+0x06</c> prog).</summary>
    public int Distance { get; internal set; }
    internal int LastSlice;
    /// <summary>AI personality (PRSONAL.DAT) used for glue; null = no glue.</summary>
    public NFS.Data.Ai.Personality? Personality { get; init; }
    internal ArcadeEnv Env;
    /// <summary>Inputs/globals of the SIM driving model (<see cref="SimModel"/>); unused in arcade/wild.</summary>
    internal SimEnv Sim = new();
}

/// <summary>
/// Minimal race loop: per 32 Hz sim tick, for every car: locator + road frame [ref:2156]
/// per-car progress (lateral offset +0x3AC, heading +0x204) [ref:2157]
/// driver (player input; AI: <see cref="AiDriver"/> afterwards) [ref:2158]
/// (with aids, when the race config asks for them) [ref:2159]
/// → wall corners (surface bit 0x80) → road edges; then [ref:2160]
/// car–car collisions over all pairs and 2 × 64 Hz integrators (pos, rot). [ref:2161]
/// Style 0 (simulation): <see cref="SimModel"/> replaces the arcade model. [ref:2162]
/// Not yet: RaceTicker/TaskQueue scheduling. [ref:2163]
/// </summary>
public sealed class RaceRunner
{
    /// <summary>Tick at which the race starts (lights green). [ref:2164]</summary>
    public const int StartTick = 0x200;

    public RaceSession Session { get; }
    public TrackLocator Locator { get; }
    /// <summary>Hittable track objects (XBID 11) and their knocked-over state (at race start). [ref:2165]</summary>
    public DynamicObjects DynObjects { get; }
    public TrackRoadQuery Road { get; }
    public RaceCar Player { get; }
    public SimCar PlayerSim { get; }
    /// <summary>Split screen (COMMMODE 1): the second human ([1], input slot 1); null for one player. [ref:2166]
    /// A list-seat race with 3+ humans also sets it to the second seat (seat order, not a split-screen claim).</summary>
    public RaceCar? Player2 { get; }
    public SimCar? Player2Sim { get; }
    /// <summary>Simulated cars of the local humans in seat order (one, two in split screen, or the list given to the runner).</summary>
    public IReadOnlyList<SimCar> HumanSims { get; }
    /// <summary>the local humans in order (one, two in split screen, or 3+ for a list-seat race). [ref:2167]</summary>
    public IReadOnlyList<CarObj> Humans { get; }
    /// <summary>All simulated cars (player first); without AI only the player.</summary>
    public IReadOnlyList<SimCar> Cars { get; }
    public CarObj Car => Player.Obj;
    public DerivedCarSpecs Specs => PlayerSim.Specs;
    public DriveControlSpecs Controls => PlayerSim.Controls;
    public DriveOptions Options => PlayerSim.Options;
    public bool Automatic => PlayerSim.Automatic;
    public int ManualResets { get; private set; }

    /// <summary>Puts a human car back on the road; <paramref name="target"/> null = the first human. [ref:2168]</summary>
    public bool ManualReset(CarObj? target = null)
    {
        var car = target ?? Car;
        var env = new CarPostEnv { SimTune = SimTune, ArcadeMode = ArcadeMode, Slices = Session.Slices,
            Reverse = Session.Backward, TrackUpdate = Road.UpdateCar };
        if (!CarPost.ManualReset(car, env, RaceStarted)) return false;
        if (Player2 != null && ReferenceEquals(car, Player2.Obj)) ManualResets2++;
        else if (ReferenceEquals(car, Car)) ManualResets++;
        return true;
    }
    /// <summary>Manual resets of the second human (split screen).</summary>
    public int ManualResets2 { get; private set; }
    public int[] SimTune { get; }
    /// <summary>Race style: 0 = simulation (STYLE tag 5 = 0), 1 = wild/arcade. Selects the [ref:2169]
    /// driving model (<see cref="SimModel"/> vs <see cref="ArcadeModel"/>), the simtune set, the road-edge/landing/launch
    /// rules and the AI parameter set.</summary>
    public int ArcadeMode => Session.Setup.Style != 0 ? 1 : 0;
    /// <summary>Current tick (64 Hz ticks). [ref:2170]</summary>
    public int Tick { get; private set; }
    // Audio observers: countdown before sim; impacts before clearing; [ref:2171]
    // after incrementing an even tick. Observers must not mutate simulation state. [ref:2172]
    public event Action<int>? OnCountdown;
    public event Action<SimCar>? BeforeCarPost;
    /// <summary>After each car's step (presentation observers, e.g. tyre marks; must not mutate simulation state). [ref:2173]</summary>
    public event Action<SimCar>? AfterCarPost;
    public event Action? OnSoundFrame;
    public event Action<CarObj, int, int, int>? OnHornRequest;

    /// <summary>Set once the tick reaches <see cref="StartTick"/>. [ref:2174]
    /// Before the green light, full brake is forced and the gear change is skipped; only [ref:2175]
    /// clears the skid levels and returns without driving – see <see cref="Countdown"/>. [ref:2176]</summary>
    public bool RaceStarted => Tick >= StartTick;

    /// <summary>Option flags (race config +0x20, tag 0x14) from the menu / cheat codes [ref:2177]
    /// (SLIP 8 → grip class table, CHASE 0x100, ROADRAGE 0x800 → horn). [ref:2178]</summary>
    public int GameOptions => Session.Setup.Get(0x20);
    private CarObj? _leadHuman;   // [ref:2179]
    /// <summary>The human the AI glue refers to; the player when there is only one. [ref:2180]</summary>
    private CarObj AiLead => Humans.Count > 1 ? _leadHuman ?? Car : Car;

    /// <summary>True when the runner was created to simulate the countdown: starts at 0 instead of [ref:2181]
    /// <see cref="StartTick"/> (original runs the sim queues from tick 0, the cars settle on the grid). [ref:2182]</summary>
    public bool Countdown { get; }
    public int WallHits => PlayerSim.WallHits;
    public int EdgeHits => PlayerSim.EdgeHits;
    /// <summary>Number of colliding car pairs (all ticks).</summary>
    public int CarHits { get; private set; }
    /// <summary>AI lane choice (<see cref="AiDriver.ChooseLane"/>, simplified); off = fixed grid lane. [ref:2183]</summary>
    public bool AiLaneChange { get; set; } = true;
    private CarObj[]? _objs;
    private CarPostEnv? _post;
    private int _aiTrackTurn;
    private int[]? _aiOccupancy;
    private readonly Dictionary<SimCar, int> _humanSeat = new();
    private readonly CarObj[] _scanOrder;
    private readonly CarObj[] _parked;
    private readonly TrafficEnv? _traffic;

    /// <summary>AI LCG (*..EC), seeded from (race config [ref:2184]
    /// +0x2C), starting at 0. Drawn [ref:2185]
    /// from the first sim tick (also during the countdown). [ref:2186]</summary>
    public AiRandom AiRng { get; }

    /// <summary>Traffic cycle state (class-4 cars, <see cref="AiTraffic"/>); null when the session has no traffic cars.</summary>
    public TrafficEnv? Traffic => _traffic;
    /// <summary>Racing cars (flag 1: humans and racing opponents): the "n" of the HUD place n/m (counts the race [ref:2187]
    /// order entries, never traffic).</summary>
    public int RacerCount => Cars.Count(x => (x.Obj.Flags & CarFlags.Racer) != 0);
    /// <summary>The class-4 cars (flag 0x10) in order. [ref:2188]</summary>
    public IReadOnlyList<SimCar> TrafficCars => Cars.Where(x => (x.Obj.Flags & CarFlags.Traffic) != 0).ToArray();

    /// <param name="withAi">Also simulate all other cars of the session with <see cref="AiDriver"/>.</param>
    /// <param name="countdown">Default true (faithful): start at 0 and simulate the countdown (race_started only from tick 0x200); false = legacy start at 0x200 (AI LCG draws from tick 0 are then missing). [ref:2189]</param>
    /// <param name="secondPlayerIndex">Split screen: index (in the session) of the second human car; it is driven by the second input
    /// of <see cref="Step(PlayerInput, PlayerInput?)"/> instead of an <see cref="AiDriver"/>.</param>
    /// <param name="automatic2">Gearbox of the second human (null = from its car record).</param>
    /// <param name="humanIndices">Seat → index in <see cref="RaceSession.Cars"/> for a list-seat race (3+ humans for the
    /// network foundation, seat 0 first). The cars should be configured as human (class 1); the local-human flag is
    /// enforced and the AI flags are cleared here. Null = the legacy <paramref name="playerIndex"/> /
    /// <paramref name="secondPlayerIndex"/> path, unchanged. <paramref name="automatic"/> applies to seat 0.</param>
    public RaceRunner(RaceSession s, int playerIndex = 0, bool? automatic = null, bool withAi = false, bool countdown = true,
        int? secondPlayerIndex = null, bool? automatic2 = null, IReadOnlyList<int>? humanIndices = null)
    {
        Session = s;
        Countdown = countdown;
        Tick = countdown ? 0 : StartTick;
        AiRng = new AiRandom(s.Setup.Random);
        Locator = new TrackLocator(new TrackGeometry(s.Trk, s.Slices));
        DynObjects = new DynamicObjects(Locator.Geometry);
        bool listSeats = humanIndices != null;
        int[] seats;
        if (listSeats)
        {
            seats = humanIndices!.ToArray();
            if (seats.Length == 0) throw new ArgumentException("at least one human seat is required", nameof(humanIndices));
            if (seats.Distinct().Count() != seats.Length)
                throw new ArgumentException("human car indices must be distinct", nameof(humanIndices));
            if (seats.Any(i => (uint)i >= (uint)s.Cars.Count))
                throw new ArgumentOutOfRangeException(nameof(humanIndices), "human car index outside the session");
        }
        else seats = secondPlayerIndex is { } second && second != playerIndex ? [playerIndex, second] : [playerIndex];
        Player = s.Cars[seats[0]];
        SimTune = s.SimTune?.For(ArcadeMode != 0).Values ?? new int[29];
        Road = new TrackRoadQuery(Locator, s.TrackNumber, SimTune) { ArcadeMode = ArcadeMode };

        var cars = new List<SimCar>(seats.Length);
        var humanObjs = new CarObj[seats.Length];
        var humanSims = new SimCar[seats.Length];
        for (int seat = 0; seat < seats.Length; seat++)
        {
            var rc = s.Cars[seats[seat]];
            bool? gearbox = seat switch { 0 => automatic, 1 when !listSeats => automatic2, _ => null };
            var sim = Create(rc, gearbox ?? rc.Setup.Transmission == 1, null);
            // +0x1EC = slot (others keep −1). A car designated as a human [ref:2190]
            // seat is local even if the session record was built as an AI car (list-seat races): clear its AI flags.
            rc.Obj.Flags |= CarFlags.LocalHuman;
            if (listSeats) rc.Obj.Flags &= ~(CarFlags.NotLocal | CarFlags.Opponent);
            rc.Obj.HumanIndex = seat;
            humanObjs[seat] = rc.Obj;
            humanSims[seat] = sim;
            _humanSeat[sim] = seat;
            cars.Add(sim);
        }
        PlayerSim = humanSims[0];
        PlayerAssist = s.Setup.WildArcade;
        // The physics LOD measures every car's distance [ref:2191]
        // to the local player and drops the update rate of distant cars (16 Hz from 64 m, 8 Hz from 128 m, rails
        // above 244 m). Without it +0x88 stays 0 and every car runs at the full 32 Hz. [ref:2192]
        Road.LocalHuman = humanObjs[0];
        Road.HumanCount = seats.Length;
        if (seats.Length > 1)
        {
            // [1 −] (split screen, HumanCount == 2) and the second seat of a list-seat race. [ref:2193]
            Player2 = s.Cars[seats[1]];
            Player2Sim = humanSims[1];
            Road.OtherHuman = humanObjs[1];
        }
        Humans = Array.AsReadOnly(humanObjs);
        HumanSims = Array.AsReadOnly(humanSims);
        if (withAi)
        {
            var humanCars = new HashSet<RaceCar>(seats.Select(i => s.Cars[i]), ReferenceEqualityComparer.Instance);
            Func<int, int>? speedAt = s.Speeds is { } sp ? sp.SpeedAt : null;
            foreach (var rc in s.Cars)
            {
                if (humanCars.Contains(rc) || rc.Specs == null) continue;
                Road.UpdateCar(rc.Obj); // lateral offset of the grid slot = preferred lane
                var ai = new AiDriver(s.Slices, s.Backward, speedAt, rc.Obj.LateralOffset * (s.Backward ? -1 : 1));
                ai.Arcade = ArcadeMode != 0;   // selects the AI parameter set (accel curve, lookahead, brake decel) [ref:2194]
                ai.SlicePrefixForward = s.Col.SlicePrefixForward;
                ai.SkillLevel = s.Setup.Skill;
                if (s.Traffic is { } tc) ai.Configure(tc);
                if (rc.AiModel is { } am) AiDriver.LoadModel(rc.Obj, am, ai.Arcade, rc.Setup.CarType);
                else ai.UseArcadeFallback = true; // no <model>.dat → no accel curve
                rc.Obj.Flags |= CarFlags.NotLocal;
                if (rc.Traits is { } traits) ai.ModelVmax = traits.AiTopSpeed;   // CarTraits.AiTopSpeed (original, types 0..14) [ref:2195]
                else if (rc.Setup.CarType is >= 0 and < 15 && GameTables.Default.ModelVmax is { Length: 15 } vm) ai.ModelVmax = vm[rc.Setup.CarType];
                cars.Add(Create(rc, true, ai));
            }
        }
        Cars = cars;
        // parked records (SMnn.DAT) are real cars after the racers: neighbour sort [ref:2196]
        // (cars can hit and push them once they leave the rails) and are handled by StaticCar. [ref:2197]
        var scan = cars.Select(c => c.Obj).ToList();
        var parkedCars = s.Parked ?? (s.Smackables?.Cars.Take(s.Setup.NumSmackables) ?? []).Select(sm =>
        {
            var p = new CarObj { Id = 0x200 | (cars.Count + 0), Flags = CarFlags.Static, Active = 1, Rail = 1, Pos = new(sm.X, sm.Y, sm.Z) };
            p.Trk.Slice = sm.Slice;
            return p;
        }).ToList();
        foreach (var parked in parkedCars)
        {
            // parked lane scoring uses the actual position/road frame. [ref:2198]
            Road.UpdateCar(parked);
            parked.LateralOffset = TrackRoadQuery.LateralOffset(parked, s.Slices);
            scan.Add(parked);
        }
        _scanOrder = scan.ToArray();
        _parked = scan.Where(c => (c.Flags & CarFlags.Static) != 0).ToArray();
        SetLaps(s.Setup.NumLaps);
        // then for every car in order; a class-4 [ref:2199]
        // car is parked (one LCG draw each) and placed 100 m above slice 0. [ref:2200]
        var traffic = cars.Where(x => (x.Obj.Flags & CarFlags.Traffic) != 0).ToList();
        if (traffic.Count > 0 && s.Traffic is { } tcfg)
        {
            int options = s.Setup.Get(0x20);
            _traffic = new TrafficEnv
            {
                Slices = s.Slices, Reverse = s.Backward, Rng = AiRng,
                Track = TrafficTrackParams.For(s.TrackNumber, s.Slices, options),
                BaseSpeed = Fx.Mul(tcfg.TrafficBaseSpeedKmh, 0x471C),
                SpeedFactors = tcfg.TrafficSpeedFactors.ToArray(), LaneOffsets = tcfg.TrafficLaneOffsets.ToArray(),
                GameOptions = options, SplitScreen = s.Setup.CommMode == 1,
                TrafficCount = traffic.Count, CopCount = cars.Count(x => (x.Obj.Flags & CarFlags.Cop) != 0),
                Opponents = cars.Count(x => (x.Obj.Flags & CarFlags.Opponent) != 0),
                Order = s.Cars.Select(x => x.Obj).Concat(parkedCars).ToArray(),
                Sorted = _scanOrder, Humans = Humans, FirstTraffic = traffic[0].Obj,
                TrackUpdate = Road.UpdateCar,
            };
            foreach (var t in cars.OrderBy(x => x.Obj.PlayerIndex))
                if ((t.Obj.Flags & CarFlags.Traffic) != 0) AiTraffic.Reset(t.Obj, _traffic);
        }
    }

    /// <summary>Laps / finish / ranking. [ref:2201]</summary>
    public RaceProgress Laps { get; private set; } = null!;

    /// <summary>(Re)creates lap tracking with the given lap count (tests use 1 lap).</summary>
    public void SetLaps(int numLaps)
    {
        int n = Session.Slices.Length;
        _numLaps = numLaps;
        Laps = new RaceProgress(n, numLaps,
            Cars.Select(c => c.Obj).ToArray(),
            Cars.Select(c => (c.Obj.Flags & CarFlags.LocalHuman) != 0).ToArray(),
            Cars.Select(c => c.Env.Model).ToArray());
    }

    private SimCar Create(RaceCar rc, bool automatic, AiDriver? ai)
    {
        var specs = rc.Specs ?? throw new InvalidOperationException($"no carSpecs for {rc.Model}");
        var derived = CarSpecsDerive.Derive(specs, ArcadeMode != 0
            ? new DeriveOptions(true, false)
            : new DeriveOptions(false, (GameOptions & RaceOptionEffects.OptBoost) != 0 && rc.Setup.CarType < 0xF, rc.Setup.FinalDrive, rc.Setup.SteerScale));
        var c = rc.Obj;
        c.Active = 1;
        c.TimeScale = Fx.One;
        if (c.HalfWidth == 0) { c.HalfWidth = 0xE000; c.HalfHeight = 0x8000; c.HalfLength = 0x20000; c.Radius = 0x28000; }
        if (c.InvMass == 0) c.InvMass = Fx.Div(Fx.One, c.Mass);          // RaceLoader.BodyFromModel sets both for racers
        if (c.InvInertia == 0) c.InvInertia = Fx.Div(Fx.One, c.Inertia);
        // +0x8F orthoCount: NOT initialised here – the freshly created car of the original has 0 (trace, tick 2) and
        // only sets it to 0x10 when it orthonormalises. Forcing 0x10 here shifted the [ref:2202]
        // whole cadence by 0x10 decrements and made the port orthonormalise at tick 956 where the original did not.
        c.OrthoCount = 0;
        c.LapOffset = Session.Backward ? 1 : 0; // trace tick 1: all racers = 1 in reverse [ref:2203]
        // the following call runs unconditionally (+6 = 0 while +0x1FC > 0); [ref:2204]
        // per tick the update only runs on a slice change, so the grid value must be set here. [ref:2205]
        RaceProgress.UpdateCarProgress(c, Session.Slices.Length, Session.Backward);
        RacerLapTask.StatsReset(c);   // [ref:2206]
        // gearbox bytes of a freshly created car: the original's trace at tick 1 has RequestedGear +0x2D6, PrevGear
        // +0x2D9 and Gear +0x2DA = 1 (N) for all eight cars (+0x2DB shift counter = 0). The AI's own gearbox
        // renumbers the gear to its index 0 (1st) on its first driving tick (|v| = 0 → the [ref:2207]
        // downshift branch) – the trace shows the AI cars at Gear 1 up to tick 512 and 0 at tick 513, while the
        // player shifts N → 1st later. Starting the AI cars at their index 0 made every AI car sit in 1st/R on the
        // grid and diverged from the original at tick 2 (`replay-compare --ai --car N`).
        c.Gear = c.RequestedGear = c.PrevGear = 1;
        c.DriveFrame = c.Orient;
        var sim = new SimCar
        {
            Race = rc, Specs = derived, Controls = derived.ToDriveControlSpecs(specs),
            // reads the ramps from the car's own config record (car+0x21C = [idx]): [ref:2208]
            // +0x10 steering, +0x14 slow throttle, +0x18 brake. Was hardcoded false/false/false before.
            Options = new DriveOptions(automatic, rc.Setup.SteerRamp, rc.Setup.SlowThrottle, rc.Setup.BrakeRamp),
            Automatic = automatic, Ai = ai,
            StartSlice = c.Trk.Slice, LastSlice = c.Trk.Slice,
            Personality = ai == null || (c.Flags & CarFlags.Traffic) != 0 ? null : PersonalityOf(rc), // only opponents get a personality [ref:2209]
        };
        sim.Env.Model = rc.Setup.CarType;
        sim.Sim.Model = rc.Setup.CarType;
        sim.Sim.HasAbs = rc.Traits?.HasAbs;
        sim.Sim.SkidAlways = rc.Traits?.SkidMarks;
        sim.Sim.RearAeroBoost = rc.Traits?.RearAeroBoost;
        sim.Sim.SpecialRules0E = rc.Traits?.SpecialRules0E;
        sim.Env.SpecialRules0E = rc.Traits?.SpecialRules0E;   // CarTraits (null = vanilla rule from Model)
        sim.Sim.DownforceFront = rc.Setup.DownforceFront; sim.Sim.DownforceRear = rc.Setup.DownforceRear;
        sim.Sim.BrakeBias = rc.Setup.BrakeBias;
        sim.Sim.PlayerBoost = (GameOptions & RaceOptionEffects.OptBoost) != 0 && rc.Setup.CarType < 0xF;
        sim.Sim.SlipOption = (GameOptions & RaceOptionEffects.OptSlip) != 0;
        sim.Sim.ArcadeMode = ArcadeMode;
        sim.Sim.Slices = Session.Slices;
        sim.Env.AiSpeeds = Session.Speeds?.Raw;   // used by the auto-drift [ref:2210]
        return sim;
    }

    private NFS.Data.Ai.Personality? PersonalityOf(RaceCar rc)
    {
        var p = Session.Personalities;
        if (p == null || p.Personalities.Count == 0) return null;
        // grid order, rather than the unused cfg personality field. [ref:2211]
        int letter = GameTables.Default.PersonalityLetterFor(rc.GridSlot, Session.Setup.NumCars,
            GameTables.AiGameType(Session.Setup.RaceType, Session.Setup.NumOpponentCars));
        return p.ByLetter((char)('a' + letter)) ?? p.Personalities[0];
    }

    /// <summary>Player steering assist (0x10/0x20). Off by default: [ref:2212]
    /// the race config/replay header carries at +0x20, and the shipped REPLAY.RPY has 0 there [ref:2213]
    /// (both bits clear), so off is the recorded behaviour; the flag stays
    /// as a diagnostic override.</summary>
    public bool PlayerSteerAssist { get; set; }

    /// <summary>Race config +0x38 (landing alignment + player [ref:2214]
    /// auto-drift). Default from the race config/replay header; the shipped REPLAY.RPY was recorded with it on.</summary>
    public bool PlayerAssist { get; set; }

    /// <summary>Diagnostics (replay-compare --ai-frozen): AI cars are not driven/simulated, only collided and
    /// integrated — their state is loaded from the original's trace before every step.</summary>
    public bool FreezeAi { get; set; }

    /// <summary>All AI cars use the old arcade path (<see cref="AiDriver.Think"/> + ArcadeModel) instead of AiDriveModel.</summary>
    public bool AiArcadeFallback { get; set; }

    // Glue speed accessors (_glueSpeed, _glueSpeedInv) [ref:2215]
    private int _glueSpeed => _glue.Speed;
    private int _glueSpeedInv => _glue.SpeedInv;
    private AiDriver.GlueFactors _glueCache;
    private bool _glueReady;
    private AiDriver.GlueFactors _glue
    {
        get
        {
            if (!_glueReady)
            {
                _glueReady = true;
                var st = Session.Setup;
                _glueCache = Session.Traffic is { } tc
                    ? AiDriver.ReadGlueFactors(tc.GlueGroup(Math.Clamp(st.RaceType, 0, 2)), st.NumLaps, st.Style != 0, st.Skill,
                        st.Cars.Any(x => x.CarClass == 4), st.Cars.Count(x => x.CarClass == 2) > 1)
                    : new AiDriver.GlueFactors(Fx.One, Fx.One, Fx.One, Fx.One);
            }
            return _glueCache;
        }
    }

    /// <summary>AI catch-up (<see cref="AiDriver.SpeedGlue"/>) relative to the player; off = glue 1.0.</summary>
    public bool AiGlue { get; set; } = true;

    /// <summary>One 32 Hz simulation tick: player with the given input, AI cars with their drivers.</summary>
    public void Step(PlayerInput input) => Step(input, null);

    /// <summary>One 32 Hz tick of a split-screen race: <paramref name="input2"/> drives the second human (<see cref="Player2"/>);
    /// null = no input (also used when the race has one human).</summary>
    public void Step(PlayerInput input, PlayerInput? input2)
    {
        if (Humans.Count > 2) throw new InvalidOperationException("use StepHumans for a race with more than two human seats");
        StepCore(input, input2, null);
    }

    /// <summary>One 32 Hz tick of a list-seat race: <paramref name="inputs"/> holds one input per human seat, in
    /// <see cref="HumanSims"/> order. AI cars still use their drivers. The count must match the seats.</summary>
    public void StepHumans(IReadOnlyList<PlayerInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != Humans.Count)
            throw new ArgumentException($"expected {Humans.Count} seat inputs, got {inputs.Count}", nameof(inputs));
        StepCore(default, null, inputs);
    }

    /// <summary>Replay camera: set only when the race plays back a recording (2); its camera [ref:2216]
    /// changes draw from <see cref="Rand"/> like the original. Null = live race (no draws).</summary>
    public ReplayCamera? ReplayCam { get; set; }

    private void StepCore(PlayerInput input, PlayerInput? input2, IReadOnlyList<PlayerInput>? humanInputs)
    {
        if (ReplayCam is { } rc && Humans.Count > 0)
        {
            // (cycle timer) then, once per 64 Hz race tick, before the sim queue [ref:2217]
            int nsl = Session.Slices.Length;
            rc.Cycle(Tick, RaceStarted, Humans[0], nsl);
        }
        if (Countdown && Tick is 0x140 or 0x180 or 0x1C0 or 0x200)
            OnCountdown?.Invoke((Tick - 0x140) / 0x40); // samples 0,1,2,3 ("3", "2", "1", "GO") at 0x140/0x180/0x1C0/0x200 [ref:2218]
        _objs ??= Cars.Select(x => x.Obj).ToArray();
        _leadHuman = RaceOptionEffects.FindLeadHuman(Humans, _leadHuman); // (via a forwarding wrapper) [ref:2219]
        // Lead human: with one human that is always the player [ref:2220]
        var lead = AiLead;
        // Priorities 6/8 run over ALL cars before the AI input tasks. [ref:2221]
        foreach (var car in Cars) if (!(FreezeAi && car.Ai != null)) UpdateRoadProgress(car);
        // Parked cars also have the prio-6 track task. On returning from rails [ref:2222]
        // builds their road frame; projects their current position onto that frame each think. [ref:2223]
        foreach (var parked in _parked)
        {
            Road.UpdateCar(parked);
            parked.LateralOffset = TrackRoadQuery.LateralOffset(parked, Session.Slices);
        }
        if (!FreezeAi)
        {
            _aiOccupancy ??= new int[_scanOrder.Length];
            AiSpeedPlanning.SortNeighbors(_scanOrder, Session.Backward);
            foreach (var car in Cars)
            {
                if (!PhysLod.Timestep(1, 0, car.Obj.DistToPlayer, car.Obj.Id, Tick, out var thinkLod)) continue;
                var c = car.Obj;
                // leave a parked (+0x3A8 & 0x10) or inactive (+0x8D = 0) car alone [ref:2224]
                if ((c.Flags & CarFlags.Traffic) != 0 && ((c.AiFlags & AiTraffic.FlagParked) != 0 || c.Active == 0))
                { _aiOccupancy[Cars.ToList().IndexOf(car)] = 0; continue; }
                c.Lane = AiDriver.LaneFromOffset(Session.Slices[c.Trk.Slice], c.LateralOffsetCur);
                _aiOccupancy[Cars.ToList().IndexOf(car)] = AiSpeedPlanning.Occupancy(c, Session.Slices[c.Trk.Slice]);
                if (car.Ai == null) continue;
                int skill = Session.Setup.RaceType == 2 ? Fx.One
                    : GameTables.Default.AiSkillSpeedOf((car.Personality?.Letter ?? 'a') - 'a', Session.Setup.Skill);
                if (c.Progress > 100) skill = Fx.Mul(skill, GameTables.Default.AiTrackSkillOf(Session.TrackNumber, Session.Setup.Skill));
                int turn = Math.Clamp(Math.Min(Math.Abs(_aiTrackTurn), c.CornerMaxSteer), c.CornerMinSteer, int.MaxValue);
                int turnFactor = Fx.One - Fx.Mul((int)(((long)((turn - c.CornerMinSteer) << 16) * c.CornerSlope) >> 16),
                    car.Personality?.HandlingAdjust ?? Fx.One); // [ref:2225]
                int glue = AiGlue && car.Personality is { } p
                    ? AiDriver.SpeedGlue(p.SpeedGlue, c.Progress, lead.Progress, lead.AiSpeed * (Session.Backward ? -1 : 1),
                        lead.Unk14E != 0, Session.Setup.NumLaps, _glueSpeed, _glueSpeedInv, startBoost: false) : Fx.One;
                // the high word is cleared before the progress comparison, so it uses 0. [ref:2226]
                if ((c.Flags & CarFlags.Traffic) != 0 && _traffic != null)
                    AiTraffic.DesiredSpeed(c, _traffic, Car, Session.Speeds?.SpeedAt(c.Trk.Slice) ?? 50); // traffic branch [ref:2227]
                else
                {
                    AiSpeedPlanning.Desired(c, Session.Speeds?.SpeedAt(c.Trk.Slice) ?? 50, car.Ai.ModelVmax,
                        skill, turnFactor, glue, Fx.One, Humans, Session.Slices.Length, Session.Backward, Session.Slices[c.Trk.Slice]);
                    AiReactions.UpdatePsycho(c, Session.Slices.Length, Session.Backward, RaceStarted,
                        (car, pattern, length, gap) => OnHornRequest?.Invoke(car, pattern, length, gap)); // [ref:2228]
                }
                // (gate: not parked, not hidden) [ref:2229]
                if ((c.Flags & CarFlags.Static) == 0 && (c.AiFlags & 0x10) == 0)
                    AiLaneOffset.MaybeChange(c, AiRng, Session.Slices.Length, Session.Backward,
                        car.Personality?.LaneSlack ?? [], car.Personality?.NewSlackProbability ?? 0,
                        Session.Traffic is { } trf ? trf.TrafficLaneOffsets : default);
                // gates (not parked/hidden, not LocalHuman, +0x14E == 0, not static) then option 0x100 (CHASE) [ref:2230]
                if ((c.Flags & (CarFlags.LocalHuman | CarFlags.Static)) == 0 && c.Unk14E == 0)
                    RaceOptionEffects.Chase(c, _leadHuman, GameOptions);
                var scan = AiSpeedPlanning.ScanAhead(c, _scanOrder, Session.Slices, Session.Backward,
                    car.Personality?.LookAhead ?? 30, _scanOrder.Select(o => _aiOccupancy[o.Id & 31]).ToArray());
                int lineSlice = (c.Trk.Slice + (Fx.Mul(c.AiSpeed, 0x4CCC) / Fx.One) * (Session.Backward ? -1 : 1)
                    + Session.Slices.Length) % Session.Slices.Length;
                int[] maskScores = new int[3];
                // (+2 toward the QAL lane) runs only when +0x8C (rail mode) == 0 [ref:2231]
                int blockedDirection = AiSpeedPlanning.ScoreMask(c, Session.Slices, Session.Backward, thinkLod.Ticks, maskScores);
                // of THIS car (runs at the start of the step; 0 without flag 8) [ref:2232]
                int turnNow = 0;
                if ((c.Flags & CarFlags.Opponent) != 0)
                {
                    var tsl = Session.Slices[(c.Trk.Slice + (Session.Backward ? -1 : 1) + Session.Slices.Length) % Session.Slices.Length];
                    var tnx = Session.Slices[(c.Trk.Slice + (Session.Backward ? 0 : 2)) % Session.Slices.Length];
                    turnNow = FxMath.Atan2(tnx.X - tsl.X, tnx.Z - tsl.Z) - c.RoadHeading;
                    if (turnNow > 0x200) turnNow -= 0x400;
                    else if (turnNow < -0x200) turnNow += 0x400;
                }
                int targetLane = AiLaneChange ? AiSpeedPlanning.ChooseLane(c, Session.Slices[c.Trk.Slice], scan,
                    Session.Backward, c.Rail != 0 ? 0 : Session.Line?.LaneAt(lineSlice) ?? 0, car.Ai.LaneSums, maskScores,
                    _objs, Session.Slices.Length, Session.Slices, Session.Setup.NumOpponentCars, _aiOccupancy,
                    AiSpeedPlanning.TrackLaneSide(Session.TrackNumber), _parked, _traffic?.TrafficCount ?? 0,
                    Session.Setup.Skill, turnNow, car.Personality) : c.Lane;
                if (_traffic != null && (c.Flags & CarFlags.Traffic) != 0 && c.Rail == 0)
                    AiTraffic.Honk(c, _traffic, scan, (car, pattern, length, gap) => OnHornRequest?.Invoke(car, pattern, length, gap)); // [ref:2233]
                c.LaneTargetOffset = AiSpeedPlanning.TargetOffset(c, scan, targetLane, Session.Slices,
                    Session.Setup.NumOpponentCars, Session.Backward, blockedDirection);
                if (AiLaneChange) car.Ai.RecordPlannedLane(c, targetLane);
                AiSpeedPlanning.Follow(c, scan, targetLane, Session.Backward, car.Personality, Session.Slices.Length);
                var sl = Session.Slices[(c.Trk.Slice + (Session.Backward ? -1 : 1) + Session.Slices.Length) % Session.Slices.Length];
                var next = Session.Slices[(c.Trk.Slice + (Session.Backward ? 0 : 2)) % Session.Slices.Length];
                _aiTrackTurn = FxMath.Atan2(next.X - sl.X, next.Z - sl.Z) - c.RoadHeading; // [ref:2234]
                if (_aiTrackTurn > 0x200) _aiTrackTurn -= 0x400;
                else if (_aiTrackTurn < -0x200) _aiTrackTurn += 0x400;
            }
        }
        // the sim queue runs by priority, each priority over all cars in insertion order [ref:2235]
        foreach (var car in Cars)
            if (!(FreezeAi && car.Ai != null))
                StepCar(car, InputFor(car, input, input2, humanInputs));   // prio 6 … 0x1E
        foreach (var parked in _parked) StepParked(parked);                                   // parked cars, same priority, after the racers
        _coll.Clear();                                                                        // [ref:2236]
        foreach (var car in Cars) CollTask(car.Obj, car);                               // prio 0x28
        foreach (var parked in _parked) CollTask(parked, null);
        // (prio 0x32, after the collision tasks): takeoff spin, slope slide, crash/auto reset [ref:2237]
        _post ??= new CarPostEnv { SimTune = SimTune, ArcadeMode = ArcadeMode, Slices = Session.Slices, Reverse = Session.Backward, TrackUpdate = Road.UpdateCar, Rand = Rand,
            SkillLevel = Session.Setup.Skill, PersonalityOf = c => Cars.FirstOrDefault(x => x.Obj == c)?.Personality };
        foreach (var car in Cars)
        {
            BeforeCarPost?.Invoke(car);
            CarPost.Update(car.Obj, _post, Tick);
            AfterCarPost?.Invoke(car);
        }
        foreach (var parked in _parked) StaticCar.Post(parked, SimTune, _post.ArcadeMode, Tick, _post.Tables); // post step for parked cars [ref:2238]
        Laps.CheckFinish(Tick); // after sim, before frame queue [ref:2239]
        for (int k = 0; k < 2; k++)
        {
            LapTasks();   // frame queue (after the sim queue of an even tick, before the next one) [ref:2240]
            foreach (var car in Cars)
            {
                NewtonIntegrate.Pos(car.Obj);
                if (car.Ai == null) NewtonIntegrate.Rot64(car.Obj); // AI rotation belongs to the sim queue, not frame
            }
            foreach (var parked in _parked) NewtonIntegrate.Pos(parked);
            Tick++;
            if ((Tick & 1) == 0)
            {
                // wrong-way counter of each human car, before the sound frame [ref:2241]
                foreach (var human in Humans) WrongWayCounter.Advance(human, Session.Slices, Session.Backward);
                OnSoundFrame?.Invoke();
            }
            RankRacers();
            if (ReplayCam is { } rc2 && Humans.Count > 0)
            {   // frame queue task of every 64 Hz tick, after the sim queue of that tick and before the next one [ref:2242]
                // (its draw falls between the two lap tasks of this step) [ref:2243]
                rc2.Update(Tick - 1, Tick - 1 >= StartTick, Humans[0], Session.Slices.Length);
            }
        }
        int n = Session.Slices.Length, dir = Session.Backward ? -1 : 1;
        foreach (var car in Cars)
        {
            int d = dir * (car.Obj.Trk.Slice - car.LastSlice);
            car.Distance += ((d % n) + n + n / 2) % n - n / 2;
            car.LastSlice = car.Obj.Trk.Slice;
        }

    }

    /// <summary>Lap task of every racer (flag 1) at the current 64 Hz tick. [ref:2244]</summary>
    private void LapTasks()
    {
        _order ??= new RaceOrder(Cars.Count(x => (x.Obj.Flags & CarFlags.Racer) != 0));
        _order.Reset();
        foreach (var car in Cars)
            if ((car.Obj.Flags & CarFlags.Racer) != 0)
                RacerLapTask.Run(car.Obj, car.Env.Model, Tick, _numLaps, Session.Slices.Length, Session.Backward,
                    GameTables.Default.ModelVmax, Rand, _order, car.Race.Traits?.AiTopSpeed);
    }

    /// <summary>Sets +0x278 = place in the order table. [ref:2245]</summary>
    private void RankRacers()
    {
        foreach (var car in Cars)
            if ((car.Obj.Flags & CarFlags.Racer) != 0) car.Obj.RacePosition = _order!.PositionOf(car.Obj);
        if (Session.Setup.Get(0x18) != 0) ApplyHumanCatchUp();
    }

    /// <summary>Catch-up branch: every human's time scale (+0x218) follows its gap to the leader. [ref:2246]</summary>
    private void ApplyHumanCatchUp()
    {
        bool Observer(int id) => Cars.FirstOrDefault(x => x.Obj.PlayerIndex == id)?.Env.Model == 0x24;
        bool haveLeader = _order!.TryLeaderDistance(Observer, out int leader);
        foreach (var human in Humans)
        {
            if (Cars.First(x => ReferenceEquals(x.Obj, human)).Env.Model == 0x24) { human.CatchUp = Fx.One; continue; }
            if (haveLeader) human.CatchUp = NFS.Core.HumanCatchUp.Multiplier(leader - human.DistanceSlices, SimTune);
        }
    }

    private int _numLaps;
    /// <summary>(start state 1): impact body shake and the opponents' per-lap max speed at the model cap. [ref:2247]
    /// The replay camera (<see cref="ReplayCam"/>) also draws from it during a replay. [ref:2248]</summary>
    public WatcomRandom Rand { get; } = new();
    private RaceOrder? _order;

    /// <summary>Input of one car this tick: its seat input for a local human (legacy two-input call or the list API),
    /// the caller's input for everybody else (AI cars replace it in <see cref="StepCar"/>).</summary>
    private PlayerInput InputFor(SimCar car, PlayerInput input, PlayerInput? input2, IReadOnlyList<PlayerInput>? humanInputs)
    {
        if (!_humanSeat.TryGetValue(car, out int seat)) return input;
        if (humanInputs != null) return humanInputs[seat];
        return seat == 0 ? input : input2 ?? default;
    }

    private void UpdateRoadProgress(SimCar car)
    {
        var c = car.Obj;
        Road.UpdateCar(c);
        // (sim queue prio 8: after prio 6, before prio 0x15) [ref:2249]
        // the per-car progress update of the active cars: +0x214 lane half width, +0x3AC [ref:2250]
        // +0x204 of the drive frame's forward, +0x39C AI speed, laps/race progress. Only the two offsets [ref:2251]
        // the heading are used so far; +0x204 is read by the next line and +0x3AC by the assists. [ref:2252]
        if (c.Active != 0)
        {
            c.LateralOffsetCur = TrackRoadQuery.LateralOffset(c, Session.Slices);
            c.Heading = FxMath.Atan2(c.DriveFrame.M20, c.DriveFrame.M22);
            car.Env.RoadCurveAhead = TrackRoadQuery.RoadCurveAhead(c, Session.Slices);
            if (c.Rail != 1) c.AiSpeed = AiDriveModel.MMul(c.RoadFrame, c.Vel).Z; // [ref:2253]
            // The progress update (zero while the start-line offset is [ref:2254]
            // still positive) runs only when +0x74 (TrackPos +0x6C, slice changed this update) != 0. [ref:2255]
            // Matters after rail entry, which leaves +6 = slice until the next change. [ref:2256]
            if (c.Trk.SliceChanged != 0) RaceProgress.UpdateCarProgress(c, Session.Slices.Length, Session.Backward);
            c.LaneHalfWidth = Fx.Mul(Math.Abs(Fx.Dot(c.RoadFrame.M00, c.RoadFrame.M01, c.RoadFrame.M02,
                c.Orient.M00, c.Orient.M01, c.Orient.M02)), c.HalfWidth)
                + Fx.Mul(Math.Abs(Fx.Dot(c.RoadFrame.M00, c.RoadFrame.M01, c.RoadFrame.M02,
                    c.Orient.M10, c.Orient.M11, c.Orient.M12)), c.HalfHeight)
                + Fx.Mul(Math.Abs(Fx.Dot(c.RoadFrame.M00, c.RoadFrame.M01, c.RoadFrame.M02,
                    c.Orient.M20, c.Orient.M21, c.Orient.M22)), c.HalfLength); // [ref:2257]
        }
    }

    private void StepCar(SimCar car, PlayerInput input)
    {
        var c = car.Obj;
        car.Env.PlayerAssist = PlayerAssist;
        if (car.Ai != null && PhysLod.Timestep(1, 1, c.DistToPlayer, c.Id, Tick, out _))
        {
            int ax = Math.Abs(c.Vel.X), az = Math.Abs(c.Vel.Z);
            c.AiSpeedApprox = ax > az ? ax + (az >> 2) : az + (ax >> 2); // also airborne [ref:2258]
            AiSpeedPlanning.SmoothLane(c);
        }
        if (_traffic != null && car.Ai != null && (c.Flags & CarFlags.Traffic) != 0
            && PhysLod.Timestep(1, 1, c.DistToPlayer, c.Id, Tick, out var trafficLod))
        {
            // Traffic (class 4): an active car far from its anchor relocates or parks [ref:2259]
            // a parked car counts down its spawn delay and does not drive. [ref:2260]
            _traffic.Tick = Tick; _traffic.LodTicks = trafficLod.Ticks;
            if ((c.AiFlags & AiTraffic.FlagParked) == 0) AiTraffic.Relocate(c, _traffic);
            if ((c.AiFlags & AiTraffic.FlagParked) != 0) AiTraffic.SpawnWait(c, _traffic);
        }
        if (car.Ai != null && (c.Flags & CarFlags.Traffic) != 0 && (c.AiFlags & AiTraffic.FlagParked) != 0)
        {
            Newton.Step(c, SimTune, Tick, Road);   // returns at once for the inactive car (+0x8D = 0)
            NewtonIntegrate.Rot32(c);
            return;
        }
        if (car.Ai != null && !RaceStarted)
        {
            // while == 0: the whole task sits behind its own LOD gate [ref:2261]
            // (with the AI row 1 – 32 Hz up to 160 m – unlike row 3), then [ref:2262]
            // writes +0x3A0 (speed approx) and runs, and an AI opponent [ref:2263]
            // (flag 8) still on the ground (AirTicks == 0) ends in a stop, which zeroes the velocity AND the [ref:2264]
            // angular velocity – the cars are held on the grid. The drive task runs before (same [ref:2265]
            // priority 0x1E, inserted first), so a task that its own LOD skips [ref:2266]
            // leaves the car with velocity 0.
            if (PhysLod.Timestep(1, 1, c.DistToPlayer, c.Id, Tick, out _))
            {
                int ax = Math.Abs(c.Vel.X), az = Math.Abs(c.Vel.Z);
                c.AiSpeedApprox = ax > az ? ax + (az >> 2) : az + (ax >> 2);   // [ref:2267]
                if ((c.Flags & CarFlags.Opponent) != 0 && c.AirTicks == 0)
                {
                    c.Vel = default;       // +0xA8/+0xAC/+0xB0 [ref:2268]
                    c.AngVel = default;    //           +0xE8/+0xEC/+0xF0
                }
            }
            Newton.Step(c, SimTune, Tick, Road);
            return;
        }
        if (car.Ai != null && !(AiArcadeFallback || car.Ai.UseArcadeFallback))
        {
            if (PhysLod.Timestep(1, 1, c.DistToPlayer, c.Id, Tick, out var lod)) // same gate, during and after the countdown [ref:2269]
            {
                AiDriveModel.GearboxRpm(c, lod.Ticks);
                // outer guard: only opponents while the leading human is not crashed. [ref:2270]
                car.Ai.AccelerationGlue = (c.Flags & CarFlags.Opponent) != 0 && AiLead.Unk14E == 0 && car.Personality is { } p
                    ? AiDriver.AccelGlue(c, AiLead, p.AccGlue, Session.Setup.Get(0x18) != 0, Session.Backward,
                        Session.Setup.RaceType, GameTables.Default.AiSkillAccOf(p.Letter - 'a', Session.Setup.Skill), _glue.Acc, _glue.AccInv)
                    : Fx.One;
                if (c.Rail != 0) // rail mode, in place of the branches below [ref:2271]
                    AiDriveModel.RailStep(c, lod.Dt, Session.Backward, car.Ai.Arcade, car.Ai.ModelVmax, car.Ai.SkillLevel, car.Ai.AccelerationGlue);
                else if (c.AirTicks == 0 && c.Orient.M11 < 0x4CCC) // first test: car lying on its side/roof (+0xD4 < 0.3) [ref:2272]
                    ArcadeModel.AirDamping(c);                       // air damping only (no steering, no integration) [ref:2273]
                else if (AiDriveModel.FinishStop(c)) { } // finished racer below 2 m/s [ref:2274]
                else if (c.AirTicks == 0) // airborne cars skip heading/steering/integration [ref:2275]
                {
                    // prologue (the AI slides along the wall itself, before [ref:2276]
                    // the collision task); a wall hit or off-road toggles reversing (0x40) [ref:2277]
                    AiDriveModel.ReverseUpdate(c, Tick);
                    if (WallCollision.RoadEdgeCollision(c, Session.Slices, SimTune) != 0
                        || AiDriveModel.OffroadCheck(c, Session.Slices[c.Trk.Slice], lod.Ticks))
                        AiDriveModel.ReverseToggle(c, 0x40, Tick);
                    car.Ai.Drive(c, lod.Dt, planned: true, updateGearbox: false, lodTicks: lod.Ticks);
                }
            }
        }
        else
        {
            if (car.Ai != null) input = car.Ai.Think(c);
            if (PlayerDriver.Apply(c, input, car.Automatic, car.Controls.NumGears) && car.Ai == null)
                ManualReset(c); // after input, before drive/newton tasks [ref:2278]
            if (car.Ai == null)   // option 0x800 (ROADRAGE) + horn [ref:2279]
                RaceOptionEffects.HornLaunch(c, _traffic?.Order ?? _objs!, GameOptions, RaceStarted, ArcadeMode, Tick, _post?.Tables ?? GameTables.Default);
            if (!ArcadeModel.NoContactDrive(c)) // UpDotN < 0x199A skips [ref:2280]
            {
                if (ArcadeMode == 0)   // == 0 (runs inside it) [ref:2281]
                {
                    car.Sim.RaceTick = Tick;
                    SimModel.Step(c, car.Specs, car.Sim, x => DriveControls.Filter(x, car.Controls, car.Options, RaceStarted, Tick),
                        car.Automatic ? x => DriveControls.AutoShift(x, car.Controls, car.Sim.Throttle) : null);
                }
                else
                {
                    var sig = DriveControls.Filter(c, car.Controls, car.Options, RaceStarted, Tick);
                    car.Env.Throttle = sig.Throttle;
                    car.Env.Brake = sig.Brake;
                    car.Env.SteerAbs = sig.SteerAbs;
                    if (car.Ai == null && PlayerSteerAssist)
                    {
                        car.Env.SteerAssist = true; car.Env.Slices = Session.Slices; car.Env.TrackReverse = Session.Backward ? 1 : 0;
                        car.Env.LaneSide = 1;
                    }
                    // only with an automatic gearbox (+8) [ref:2282]
                    ArcadeModel.Step(c, car.Specs, ref car.Env, RaceStarted, (GameOptions & RaceOptionEffects.OptSlip) != 0, // option 8 → table [ref:2283]
                        car.Automatic ? x => DriveControls.AutoShift(x, car.Controls, car.Env.Throttle) : null);
                }
            }
        }
        Newton.Step(c, SimTune, Tick, Road);
        if (car.Ai != null) NewtonIntegrate.Rot32(c); // AI drive → Newton → rot32, all priority 0x1E [ref:2284]
    }

    private readonly List<(CarObj Obj, SimCar? Sim)> _coll = [];

    /// <summary>
    /// (sim prio 0x28, after every car's update): +0x16C cooldown, dynamic objects (XBID11 [ref:2285]
    /// not ported), walls on flagged surfaces (bit 0x80), car–car pairs with the cars already processed this tick
    /// (only when the cooldown is over and the car is ≤ 100 m from a player), then the road edge check.
    /// </summary>
    /// <summary>Diagnostics (replay-compare --ai-mid): called at the entry of every car's collision task.</summary>
    public Action<SimCar>? BeforeCollTask { get; set; }

    /// <summary>Parked car (class 0x40) sim-queue tasks of prio 0x1E: [ref:2286]
    /// (see <see cref="StaticCar"/>); they run after the racers in <see cref="Step"/>. [ref:2287]</summary>
    private void StepParked(CarObj c)
    {
        StaticCar.Drive(c);
        Newton.Step(c, SimTune, Tick, Road);
        NewtonIntegrate.Rot32(c);
    }

    private void CollTask(CarObj c, SimCar? car)
    {
        if (car != null) BeforeCollTask?.Invoke(car);
        if (c.CollisionCooldown > 0) c.CollisionCooldown--;
        if (c.Rail != 0 || c.Active == 0) return;
        bool active = c.CollisionCooldown == 0 && c.DistToPlayer <= 100 * Fx.One;
        DynObjects.Tick = Tick;
        if ((c.SurfaceByte & 0x40) != 0 && active) DynObjects.Collide(c);   // XBID 11 objects [ref:2288]
        if ((c.SurfaceByte & 0x80) != 0)
        {
            WallCollision.CalcCorners(c);
            if (WallCollision.CheckCorners(c, Locator, Session.TrackNumber, SimTune, Tick) && car != null) car.WallHits++;
        }
        if (active)
        {
            foreach (var (oc, o) in _coll)
                if (CarCollision.Collide(c, oc, Session.Slices, SimTune, ArcadeMode != 0))
                {
                    CarHits++;
                    if (car != null) car.CarHits++;
                    if (o != null) o.CarHits++;
                }
            _coll.Add((c, car));
        }
        if (WallCollision.RoadEdgeCheck(c, Session.Slices, SimTune, ArcadeMode != 0, car?.Specs.GearRpmFactor, car?.Specs.Redline ?? 0) != 0 && car != null) car.EdgeHits++;
    }

    /// <summary>Progress in race direction (slices, mod track length) since the start.</summary>
    public int Progress(SimCar car)
    {
        int n = Session.Slices.Length, moved = (Session.Backward ? -1 : 1) * (car.Obj.Trk.Slice - car.StartSlice);
        return (moved % n + n) % n;
    }
}
