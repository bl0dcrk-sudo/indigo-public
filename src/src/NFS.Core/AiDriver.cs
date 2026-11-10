using NFS.Core.Tables;
using NFS.Data.Track;

namespace NFS.Core;

/// <summary>
/// Opponent AI driver. RaceRunner supplies speed/lane plans from AiSpeedPlanning [ref:0097]
/// then Drive uses the original in/out-of-control model, road-frame target point and TRAFCFG parameters.
/// Think/ChooseLane retain the earlier simplified fallback for callers outside that planned path.
/// </summary>
public sealed class AiDriver
{
    public static Action<string>? DebugTrace { get; set; }
    public IReadOnlyDictionary<int, sbyte[]>? SlicePrefixForward { get; set; }
    /// <summary>Default speed byte when no .QPS is loaded (AiSpeeds.DefaultSpeed 0x32 = 50 m/s).</summary>
    public const int DefaultSpeedByte = 0x32;

    public TrackSlice[] Slices { get; }
    public bool Backward { get; }
    /// <summary>Per-slice speed byte (m/s); null → <see cref="DefaultSpeedByte"/>.</summary>
    public Func<int, int>? SpeedAt { get; }
    /// <summary>Skill speed factor (16.16; 1.0 by default). [ref:0098]</summary>
    public int Skill { get; set; } = Fx.One;
    /// <summary>Desired lateral offset from the slice centre, + = right in race direction (car <c>+0x394</c>).</summary>
    public int LaneOffset { get; set; }
    /// <summary>Catch-up factor (car <c>+0x558</c>, <see cref="SpeedGlue"/>; 1.0 = off).</summary>
    public int Glue { get; set; } = Fx.One;
    /// <summary>Last computed target speed (car <c>+0x398</c>, unsigned here).</summary>
    public int TargetSpeed { get; private set; }

    public AiDriver(TrackSlice[] slices, bool backward, Func<int, int>? speedAt, int laneOffset)
    {
        Slices = slices; Backward = backward; SpeedAt = speedAt; LaneOffset = laneOffset;
    }

    private int Dir => Backward ? -1 : 1;
    private TrackSlice At(int s) { int n = Slices.Length; return Slices[((s % n) + n) % n]; }

    /// <summary>Heading of a slice in race direction (0x400 = 360°).</summary>
    private int Heading(int s)
    {
        var sl = At(s);
        return FxMath.Atan2(sl.Forward[0] * Dir, sl.Forward[2] * Dir);
    }

    private static int Wrap(int a) => ((a + 0x200) & 0x3FF) - 0x200;

    /// <summary>
    /// Turn slowdown factor, from the heading difference of slices [ref:0099]
    /// s+k and s−k; above a dead zone the speed is reduced linearly, down to 0.45
    /// [the +0x4F0/+0x4F4/+0x4F8 parameters and personality handling are replaced with constants].
    /// </summary>
    public int TurnFactor(int slice)
    {
        int turn = Math.Abs(Wrap(Heading(slice + 12 * Dir) - Heading(slice)));
        const int deadZone = 8; // ≈ 2.8°
        if (turn <= deadZone) return Fx.One;
        return Math.Max(0x7333, Fx.One - (turn - deadZone) * 0x500);
    }

    /// <summary>Target speed without glue or start boost: base × skill × turn. [ref:0100]</summary>
    public int ComputeTargetSpeed(int slice)
    {
        int b = SpeedAt?.Invoke(((slice % Slices.Length) + Slices.Length) % Slices.Length) ?? DefaultSpeedByte;
        if (b == 0) b = DefaultSpeedByte;
        return Fx.Mul(Fx.Mul(b << 16, Fx.Mul(Skill, Glue)), TurnFactor(slice));
    }

    // ---- glue / catch-up (step 3) ---- [ref:0101]

    /// <summary>Progress thresholds by lap count (2 / 4 / other). [ref:0102]</summary>
    private static readonly int[][] GlueLapThresholds = [[300, 500, 650, 800], [500, 750, 800, 1000], [750, 1000, 1250, 1500]];

    /// <summary>Glue factors and their inverses. [ref:0103]</summary>
    public readonly record struct GlueFactors(int Speed, int Acc, int SpeedInv, int AccInv);

    /// <summary>
    /// computed from the 24-int TRAFCFG group of the race type, for i in {speed, acc}: [ref:0104]
    /// r = overall[i]·laps[L][i]·mode[arcade][i]·skill[level][i]·traffic[some][i]·opp[many][i] (each 16.16 product
    /// rounded; the innermost is traffic·opp), L = laps 2→0, 4→1, else 2; inverse = floor(2^32 / r).
    /// </summary>
    public static GlueFactors ReadGlueFactors(ReadOnlySpan<int> g, int laps, bool arcade, int skillLevel, bool traffic, bool manyOpponents)
    {
        int L = laps == 2 ? 0 : laps == 4 ? 1 : 2;
        Span<int> r = stackalloc int[2];
        for (int i = 0; i < 2; i++)
        {
            int v = Fx.Mul(g[(manyOpponents ? 1 : 0) * 2 + i + 0x14], g[(traffic ? 1 : 0) * 2 + i + 0x10]);
            v = Fx.Mul(g[skillLevel * 2 + i + 0xC], v);
            v = Fx.Mul(g[(arcade ? 1 : 0) * 2 + i + 8], v);
            v = Fx.Mul(g[L * 2 + i + 2], v);
            r[i] = Fx.Mul(g[i], v);
        }
        static int Inv(int x) => x == 0 ? 0 : (int)((1L << 32) / x);
        return new GlueFactors(r[0], r[1], Inv(r[0]), Inv(r[1]));
    }

    /// <summary>Acceleration glue: no lap ramp and no ±30 progress dead zone (unlike speed glue). [ref:0105]
    /// Progress is read as ushort; p in the original is !catchupEnabled.</summary>
    public static int AccelGlue(CarObj c, CarObj leader, ReadOnlySpan<int> curve, bool catchupEnabled,
        bool reverse, int raceType, int skillAcc, int glueAcc, int glueAccInv)
    {
        if (!catchupEnabled || (c.Flags & CarFlags.Opponent) == 0) return Fx.One;
        int prog = (ushort)c.Progress, leadProg = (ushort)leader.Progress;
        int g = curve[Math.Clamp((prog - leadProg) / 15 + 10, 0, 20)];
        g = Fx.Mul(g, g < Fx.One ? glueAccInv : glueAcc);
        if (raceType != 1 && raceType != 2) g = Fx.Mul(g, skillAcc);
        if (leader.AiSpeed * (reverse ? -1 : 1) < 0x20000 && leader.Unk14E == 0) return Fx.One;
        if (prog < leadProg && leader.Unk14E != 0 && g > Fx.One) return Fx.One;
        return g;
    }

    /// <summary>glue fades in during the race (0, ¼, ½, ¾, full). [ref:0106]</summary>
    public static int GlueLapRamp(int prog, int g, int laps)
    {
        var T = GlueLapThresholds[laps == 2 ? 0 : laps == 4 ? 1 : 2];
        if (prog > T[3]) return g;
        if (prog > T[2]) return Fx.One + Fx.Mul(g - Fx.One, 0xC000);
        if (prog > T[1]) return Fx.One + Fx.Mul(g - Fx.One, 0x8000);
        if (prog > T[0]) return Fx.One + Fx.Mul(g - Fx.One, 0x4000);
        return Fx.One;
    }

    /// <summary>
    /// Speed glue (rubber band toward the leading human L): <c>d = prog − L.prog [ref:0107]</c>
    /// <c>g = speedGlue[clamp(d/15 + 10, 0, 20)]</c>, then when |d| &gt; 30 multiplied by the glue factor (or its inverse when g &lt; 1) [ref:0108]
    /// 1.0 when L is slow (&lt; 2 m/s) and crashed, when we lead while L is slow, or when L crashed and we are behind with g &gt; 1; then the lap ramp.
    /// Then the start boost <c>× (1 + (0x41 − prog)·0x3F0)</c> while prog &lt; 0x41. [ref:0109]
    /// </summary>
    public static int SpeedGlue(ReadOnlySpan<int> speedGlue, int prog, int leaderProg, int leaderSpeed, bool leaderCrashed,
        int laps, int glueSpeed = Fx.One, int glueSpeedInv = Fx.One, bool startBoost = true)
    {
        int d = prog - leaderProg;
        int g = speedGlue[Math.Clamp(d / 15 + 10, 0, 20)];
        if (Math.Abs(d) > 30) g = Fx.Mul(g, g >= Fx.One ? glueSpeed : glueSpeedInv);
        if ((leaderSpeed < 0x20000 && leaderCrashed) || (prog < leaderProg && leaderCrashed && g > Fx.One) ||
            (leaderSpeed < 0x20000 && prog > leaderProg)) g = Fx.One;
        g = GlueLapRamp(prog, g, laps);
        if (startBoost && prog < 0x41) g = Fx.Mul(g, Fx.One + (0x41 - prog) * 0x3F0);
        return g;
    }

    // ---- lane choice (step 3, simplified) ---- [ref:0110]

    /// <summary>Current lane 0..13 (car <c>+0x500</c>), −1 = not chosen yet.</summary>
    public int Lane { get; private set; } = -1;
    /// <summary>Lane picked by the scoring. [ref:0111]</summary>
    public int TargetLane { get; private set; } = -1;
    /// <summary>Lane offset the car is steering to now, smoothed toward <see cref="LaneOffset"/> by ±0.2 m per think (<c>+0x548</c>).</summary>
    public int SmoothOffset { get; private set; }
    /// <summary>Last column sums for lane−1, lane, lane+1. [ref:0112]</summary>
    public int[] LaneSums { get; } = new int[3];

    public void RecordPlannedLane(CarObj c, int targetLane)
    {
        Lane = c.Lane;
        TargetLane = targetLane;
        LaneOffset = c.LaneTargetOffset * Dir;
        SmoothOffset = c.SmoothedLaneOffset * Dir;
    }

    // [0] = 0, [i] = FXDIV(1.0, i · 0.25 m) for i = 1..79 (index = 2 · width byte) [ref:0113]
    private static readonly int[] InvLaneWidth = BuildInvLaneWidth();

    private static int[] BuildInvLaneWidth()
    {
        var t = new int[512];
        for (int i = 1; i < t.Length; i++) t[i] = Fx.Div(Fx.One, i * 0x4000);   // [80..] are not initialised in the original (other globals)
        return t;
    }

    /// <summary>Lane index: (x &lt; 0 ? 6 : 7) + trunc(FX(x, [2 · width byte]) / 65536) [ref:0114]
    /// (width byte +0x20 left / +0x21 right; a zero width has inverse 0 → lane 6 / 7), negative → 0, clamp 13.
    /// <paramref name="x"/> is in the slice frame.</summary>
    public static int LaneFromOffset(TrackSlice sl, int x)
    {
        int w = x < 0 ? sl.HalfWidthLeft : sl.HalfWidthRight;
        int l = (x < 0 ? 6 : 7) + Fx.DivPow2(Fx.Mul(x, InvLaneWidth[w * 2]), 16);
        return l < 0 ? 0 : Math.Min(l, 13);
    }

    /// <summary>Centre of lane <paramref name="lane"/> in the slice frame (±(k·w + w/2)). [ref:0115]</summary>
    public static int LaneCentre(TrackSlice sl, int lane)
    {
        int wl = sl.HalfWidthLeft * 0x8000, wr = sl.HalfWidthRight * 0x8000;
        return lane >= 7 ? (lane - 7) * wr + wr / 2 : -((6 - lane) * wl + wl / 2);
    }

    /// <summary>Roadway lanes <c>[7 − (b1F&gt;&gt;4), 6 + (b1F&amp;0xF)]</c> (lane selection in the original game).</summary>
    public static (int Lo, int Hi) Roadway(TrackSlice sl) => (7 - (sl.Lanes >> 4), 6 + (sl.Lanes & 0xF));

    /// <summary>
    /// Simplified lane scoring (columns lane−1 / lane / lane+1) with rows: 11 edges (outside roadway −1000),
    /// 13 free lane (+5 unless a car is in [−1, 3] slices), 0 slow blocking car ahead (+FX(v_other − target, 1.3),
    /// within 2..10 slices), 12 car alongside (±2 slices) −5, 7 parked cars (<see cref="ScoreStatic"/>, optional); pick the best column; the target [ref:0116]
    /// offset is the lane centre. Not ported: rows 1–6, 8–10, 14–17 (mask, parked cars, AI line, blocking the human…).
    /// </summary>
    public void ChooseLane(CarObj self, IReadOnlyList<CarObj> cars, IReadOnlyList<CarObj>? parked = null)
    {
        int slice = self.Trk.Slice, n = Slices.Length;
        var sl = At(slice);
        if (Lane < 0) SmoothOffset = LaneOffset;
        Lane = LaneFromOffset(sl, self.LateralOffset); // +0x500 = lane of the actual position (lat3AC), not the target
        self.Lane = Lane;
        int target = ComputeTargetSpeed(slice);
        var (lo, hi) = Roadway(sl);
        for (int k = 0; k < 3; k++)
        {
            int lane = Lane - 1 + k, s = 0;
            if (lane < lo || lane > hi) s -= 1000 << 16;
            else
            {
                bool free = true;
                foreach (var o in cars)
                {
                    if (ReferenceEquals(o, self)) continue;
                    int d = Dir * (o.Trk.Slice - slice);
                    d = ((d % n) + n + n / 2) % n - n / 2; // wrap to ±n/2
                    int ol = LaneFromOffset(At(o.Trk.Slice), o.LateralOffset);
                    if (ol == lane && d >= -1 && d <= 3) free = false;
                    if (ol == lane && d >= 2 && d <= 10 && k != 1 || ol == lane && d >= 1 && d <= 10 && k == 1)
                    {
                        int dv = o.LocalVel.Z - target;
                        if (dv < 0) s += Fx.Mul(dv, 0x14CCC);
                    }
                    if (k != 1 && ol == lane && Math.Abs(d) <= 2) s -= 5 << 16;
                }
                if (free) s += 5 << 16;
            }
            LaneSums[k] = s;
        }
        if (parked != null) ScoreStatic(self, parked, Slices, false, Backward, LaneSums); // row 7
        int best = LaneSums[1], pick = Lane;                  // best column; left must be strictly better than the current lane, right strictly better than the best so far [ref:0117]
        if (LaneSums[1] < LaneSums[0]) { pick = Lane - 1; best = LaneSums[0]; }
        if (best < LaneSums[2]) pick = Lane + 1;
        TargetLane = Math.Clamp(pick, 0, 13);
        LaneOffset = LaneCentre(sl, TargetLane) * Dir;
        int step = Math.Clamp(LaneOffset - SmoothOffset, -0x3333, 0x3333);
        SmoothOffset += step;
    }
    /// <summary>
    /// Row 7: parked cars (flag 0x40) in front of us within [ref:0118]
    /// <c>|v39C|</c> slices; distance <c>x = d·1.716 m</c> (0x1B77C); when x &lt; v (track direction) the column of the
    /// parked car's lane (lane−1 / lane / lane+1) gets <c>FX(v − x, −0.5)</c>. <paramref name="wrongWay"/> = bit 0x1000
    /// of +0x3A8, <paramref name="trackReverse"/>. Adds into <paramref name="sums"/> (3 columns). [ref:0119]
    /// </summary>
    public static void ScoreStatic(CarObj self, IReadOnlyList<CarObj> parked, TrackSlice[] slices, bool wrongWay,
        bool trackReverse, Span<int> sums)
    {
        int v = self.AiSpeed, n = slices.Length;
        bool u = !wrongWay ^ trackReverse;
        int s2 = u ? 1 : -1;
        foreach (var o in parked)
        {
            int d = o.Trk.Slice - self.Trk.Slice;                     // slice distance, wrapped to the track [ref:0120]
            d = ((d % n) + n + n / 2) % n - n / 2;
            d *= s2;
            if (d < 0 || d >= Math.Abs(v) >> 16) continue;
            int x = d * 0x1B77C * s2, p;
            if (!u) p = x - v <= 0 ? 0 : Fx.Mul(x - v, -0x8000);
            else p = x < v ? Fx.Mul(v - x, -0x8000) : 0;
            if (p == 0) continue;
            int lane = LaneFromOffset(slices[o.Trk.Slice % n], o.LateralOffset);
            for (int k = 0; k < 3; k++) if (lane == self.Lane - 1 + k) sums[k] += p;
        }
    }

    /// <summary>World position of the lane point <paramref name="ahead"/> slices in race direction.</summary>
    public Vec3 LanePoint(int slice, int ahead)
    {
        var sl = At(slice + ahead * Dir);
        int k = (Lane >= 0 ? SmoothOffset : LaneOffset) * Dir / 126; // smoothed once lane choice runs // Right is scaled 126 ≈ 1.0, oriented to the track's forward
        return new Vec3(sl.X + sl.Right[0] * k, sl.Y + sl.Right[1] * k, sl.Z + sl.Right[2] * k);
    }

    // ---- own AI driving model (AiDriveModel) ---- [ref:0121]

    /// <summary>true = old behaviour: <see cref="Think"/> → player control filter + arcade model (for comparison).</summary>
    public bool UseArcadeFallback { get; set; }
    /// <summary>In-control model parameters (TRAFCFG [91..101]). [ref:0122]</summary>
    public AiDriveModel.Params InControl { get; set; } = AiDriveModel.Params.DefaultInControl;
    /// <summary>Out-of-control model parameters (TRAFCFG [102..112]). [ref:0123]</summary>
    public AiDriveModel.Params OutOfControl { get; set; } = AiDriveModel.Params.DefaultOutOfControl;
    /// <summary>Look-ahead for lateral velocity, in slices (TRAFCFG [84]). [ref:0124]</summary>
    public int LatVelLookahead { get; set; } = 4;
    /// <summary>Minimum look-ahead, in slices (TRAFCFG [85]). [ref:0125]</summary>
    public int MinLookahead { get; set; } = 2;
    /// <summary>TRAFCFG [88]/[86] max look-ahead (slices), arcade/sim.</summary>
    public int MaxLookahead { get; set; } = 30;
    /// <summary>TRAFCFG [89]/[87] look-ahead factor (16.16), arcade/sim.</summary>
    public int LookaheadFactor { get; set; } = 0x18000;
    /// <summary>Arcade model (true) or sim model (false); selects the TRAFCFG values. [ref:0126]</summary>
    public bool Arcade { get; set; } = true;
    public int SkillLevel { get; set; } = 1;
    /// <summary>Speed cap of the model (16.16 m/s; 0 = no cap); returns 0 above it. [ref:0127]</summary>
    public int ModelVmax { get; set; }
    /// <summary>Acceleration glue factor, supplied from the current leading human and the personality. [ref:0128]</summary>
    public int AccelerationGlue { get; set; } = Fx.One;
    /// <summary>Last step was out of control. [ref:0129]</summary>
    public bool OutOfControlNow { get; private set; }

    /// <summary>Loads the AI part of <c>&lt;model&gt;.dat</c> into the car. [ref:0130]
    /// <paramref name="carType"/> (cfg+0, also <c>[car+0x21C][0]</c>) selects the scaling of the accel curve [ref:0131]
    /// which the original calls on the freshly read curve; −1 / ≥ 0xF = no scaling.</summary>
    public static void LoadModel(CarObj c, NFS.Data.Car.CarAiModel m, bool arcade, int carType = -1, GameTables? tables = null)
    {
        m.AccelCurve(arcade).CopyTo(c.AccelCurve, 0);
        if (carType is >= 0 and < 15)
        {
            int scale = (tables ?? GameTables.Default).AiAccelScaleOf(carType, arcade);
            for (int i = 0; i < c.AccelCurve.Length; i++) c.AccelCurve[i] = (short)ScaleAccel(c.AccelCurve[i], scale);
        }
        c.MaxLatAccel = arcade ? m.ArcadeMaxLateralAccel : m.SimMaxLateralAccel;
        c.MaxYawAccel = arcade ? m.ArcadeMaxYawAccel : m.SimMaxYawAccel;
        c.Unk560 = m.TrafficSpeedMultiplier; // [129] -> +0x560 (read by the traffic desired speed) [ref:0132]
        c.ShiftTime = m.GearShiftTime;
        c.MaxRpm = m.Unknown_1;
        c.CornerMaxSteer = m.Unknown_126;
        c.CornerMinSteer = m.Unknown_127;
        c.CornerSlope = m.Unknown_126 == m.Unknown_127 ? 0 : Fx.Div(m.Unknown_128, (m.Unknown_126 - m.Unknown_127) << 16);
        for (int i = 0; i < 7; i++) c.GearSpeeds[i] = m.GearSpeeds[i];
        // inv[0] = FXDIV(1, gs[0]); inv[g] = FXDIV(1, gs[g] − gs[g−1]), 0 when gs[g] = 0 [ref:0133]
        for (int i = 0; i < 7; i++)
        {
            int span = i == 0 ? c.GearSpeeds[0] : c.GearSpeeds[i] == 0 ? 0 : c.GearSpeeds[i] - c.GearSpeeds[i - 1];
            c.GearSpeedsInv[i] = span == 0 ? 0 : Fx.Div(Fx.One, span);
        }
        c.Gear = 0; c.ShiftCounter = 0;
    }

    /// <summary>
    /// Body: <c>v' = ((v &lt;&lt; 8) * scale + 0x8000) &gt;&gt; 16 &gt;&gt; 8</c>; the original does the [ref:0134]
    /// 64-bit product then rounds at bit 16 and arithmetically shifts the 32-bit result by 8.
    /// </summary>
    public static int ScaleAccel(int v, int scale) => (int)(((long)(v << 8) * scale + 0x8000) >> 16) >> 8;

    /// <summary>Physics parameters block from TRAFCFG. [ref:0135]</summary>
    public void Configure(NFS.Data.Ai.TrafCfg t)
    {
        InControl = AiDriveModel.Params.From(t.InControl);
        OutOfControl = AiDriveModel.Params.From(t.OutOfControl);
        LatVelLookahead = t.LatVelLookahead; MinLookahead = t.MinLookahead;
        MaxLookahead = Arcade ? t.ArcadeMaxLookahead : t.SimMaxLookahead;
        LookaheadFactor = Arcade ? t.ArcadeLookaheadFactor : t.SimLookaheadFactor;
        AiDriveModel.SkidValue = t.SkidValue;
    }

    /// <summary>Look-ahead of the target point in slices. [ref:0136]</summary>
    public int TargetLookahead(int absSpeed)
    {
        int n = Fx.Mul(Fx.Mul(0x4000, LookaheadFactor), absSpeed) / 65536;
        if (0 <= n && n < MinLookahead) n = MinLookahead;
        return Math.Min(n, MaxLookahead);
    }

    /// <summary>
    /// One AI drive step with <see cref="AiDriveModel"/>: sets <c>+0x398</c> target speed (track [ref:0137]
    /// direction sign), <c>+0x3AC/+0x548</c> lateral offsets (slice frame), the target point on the smoothed lane and
    /// the lateral speed at <c>slice ± LatVelLookahead</c>, then steering toward the target (dt = 1/32 s). [ref:0138]
    /// Call after Road.UpdateCar, before Newton.Step. With planned=true the caller owns +0x398/+0x558/+0x548;
    /// the target uses the current road frame, and the caller supplies LOD dt/ticks. [ref:0139]
    /// </summary>
    public void Drive(CarObj c, int dt = 0x800, bool planned = false, bool updateGearbox = true, int lodTicks = 1)
    {
        int slice = c.Trk.Slice;
        int driveDir = (c.AiFlags & 0x1000) == 0 ? Dir : -Dir; // reversed when the wrong-way bit (0x1000) is set [ref:0140]
        if (!planned)
        {
            TargetSpeed = ComputeTargetSpeed(slice);
            c.AiTargetSpeed = TargetSpeed * Dir;
            c.Glue = Glue;
        }
        else TargetSpeed = Math.Abs(c.AiTargetSpeed);
        // +0x3AC is not written here: the progress task owns it, and the drive [ref:0141]
        // step only reads it (AiDriveModel.Steer*); the caller runs that task before this one.
        if (!planned) c.SmoothedLaneOffset = (Lane >= 0 ? SmoothOffset : LaneOffset) * Dir;
        var cur = At(slice);
        int vTrack = planned ? c.AiSpeed : Fx.Mul(c.Vel.X, cur.Forward[0] * 512) + Fx.Mul(c.Vel.Y, cur.Forward[1] * 512) + Fx.Mul(c.Vel.Z, cur.Forward[2] * 512);
        int n = TargetLookahead(Math.Abs(vTrack));
        var target = planned
            ? new Vec3(At(slice + n * driveDir * c.Direction).X, At(slice + n * driveDir * c.Direction).Y, At(slice + n * driveDir * c.Direction).Z)
                + Vec3.Scale(new Vec3(c.RoadFrame.M00, c.RoadFrame.M01, c.RoadFrame.M02), c.SmoothedLaneOffset) // lateral offset in the road frame [ref:0142]
            : LanePoint(slice, n);
        bool psychoTarget = AiReactions.TargetPsycho(c, Slices.Length); // called before the lane lookahead [ref:0143]
        int latAhead = LatVelLookahead * driveDir;
        if (planned)
        {
            // on a bend toward the target lane halve the lateral-velocity lookahead. [ref:0144]
            int bendSlice = slice + 3 * driveDir;
            var forward = bendSlice < 0 && SlicePrefixForward?.TryGetValue(bendSlice, out var prefix) == true
                ? prefix : At(bendSlice).Forward;
            int curve = (Fx.Mul(forward[0] * 512, cur.Right[0] * 512)
                + Fx.Mul(forward[2] * 512, cur.Right[2] * 512)) * driveDir; // negative indices read the COL prefix [ref:0145]
            if ((curve > 0 && c.SmoothedLaneOffset < c.LateralOffsetCur)
                || (curve < 0 && c.LateralOffsetCur < c.SmoothedLaneOffset)) latAhead /= 2;
        }
        var ra = At(slice + latAhead);
        var rightAhead = new Vec3(ra.Right[0] * 512, ra.Right[1] * 512, ra.Right[2] * 512);
        DebugTrace?.Invoke($"AI id={c.Id} slice={slice} latAhead={latAhead} target={c.AiTargetSpeed} smooth={c.SmoothedLaneOffset} "
            + string.Join(" ", new[] { 0, -1, -2, -3, -4, -5 }.Select(k => {
                var r = At(slice + k).Right;
                return $"lat[{k}]={Fx.Dot(c.Vel.X, c.Vel.Y, c.Vel.Z, r[0]*512,r[1]*512,r[2]*512)}";
            })));
        if (updateGearbox) AiDriveModel.GearboxRpm(c, lodTicks); // runs before the step, also when airborne [ref:0146]
        OutOfControlNow = AiDriveModel.Step(c, target, rightAhead, InControl, OutOfControl, driveDir, Arcade, dt, vTrack, ModelVmax, SkillLevel, lodTicks,
            AccelerationGlue, psychoTarget);
    }

    /// <summary>One AI think (32 Hz): steering toward the lane point, gas/brake toward the target speed.</summary>
    public PlayerInput Think(CarObj c)
    {
        int slice = c.Trk.Slice;
        TargetSpeed = ComputeTargetSpeed(slice);
        int v = c.LocalVel.Z;
        int ahead = 3 + Math.Clamp(v >> 18, 0, 6); // look further at speed (4 m/s per extra slice)
        var p = LanePoint(slice, ahead);
        int want = FxMath.Atan2(p.X - c.Pos.X, p.Z - c.Pos.Z);
        int have = FxMath.Atan2(c.Orient.M20, c.Orient.M22);
        int steer = Math.Clamp(Wrap(want - have) * 3, -127, 127);

        int err = TargetSpeed - v;
        byte gas = 0, brake = 0;
        if (err > 0) gas = (byte)Math.Clamp(96 + (err >> 12), 0, 255);
        else if (err < -2 * Fx.One) brake = (byte)Math.Clamp((-err) >> 13, 0, 255);
        // automatic gearbox: in R/N send a gear-up request (to D) [ref:0147]
        byte buttons = c.Gear < 2 ? PlayerInput.BtnGearUp : (byte)0;
        return new PlayerInput((sbyte)steer, gas, brake, buttons);
    }
}
