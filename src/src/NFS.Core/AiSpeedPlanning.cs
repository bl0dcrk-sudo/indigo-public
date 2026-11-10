using NFS.Data.Ai;
using NFS.Data.Track;

namespace NFS.Core;

/// <summary>Opponent speed planning: scanning and following. [ref:0160]</summary>
public static class AiSpeedPlanning
{
    // Track mask bit order is the reverse of the lane order (lanes use 1 << lane). [ref:0161]
    public static int LaneBit(int lane) => 1 << (15 - Math.Clamp(lane + 1, 0, 15));
    // stable insertion sort, deliberately EXCLUDING the last entry; reverse the whole list afterward. [ref:0162]
    public static void SortNeighbors(CarObj[] cars, bool reverse)
    {
        for (int i = 1; i < cars.Length - 1; i++)
        {
            var c = cars[i];
            int j = i - 1;
            while (j >= 0 && cars[j].Trk.Slice > c.Trk.Slice) { cars[j + 1] = cars[j]; j--; }
            cars[j + 1] = c;
        }
        if (reverse) Array.Reverse(cars);
        for (int i = 0; i < cars.Length; i++) cars[i].ListPosition = i;
    }
    // first snap across the actual lateral position, then move by 0.2 m. [ref:0163]
    public static void SmoothLane(CarObj c)
    {
        int actual = c.LateralOffsetCur;
        if ((c.LaneTargetOffset < actual && actual < c.SmoothedLaneOffset)
            || (c.SmoothedLaneOffset < actual && actual < c.LaneTargetOffset)) c.SmoothedLaneOffset = actual;
        c.SmoothedLaneOffset += Math.Clamp(c.LaneTargetOffset - c.SmoothedLaneOffset, -0x3333, 0x3333);
    }
    public static int SliceDelta(int a, int b, int n)
    {
        int d = a - b;
        if (d > n / 2) d -= n;
        else if (d < -n / 2) d += n;
        return d;
    }

    // cap BEFORE multiplying factors; near-human clamp BEFORE the start boost. [ref:0164]
    public static void Desired(CarObj c, int speedByte, int vmax, int skill, int turn,
        int glue, int trackFactor, IReadOnlyList<CarObj> humans, int slices, bool reverse, TrackSlice? road = null)
    {
        int speed = speedByte << 16;
        if (vmax > 0) speed = Math.Min(speed, vmax); // cap the base speed at the model's vmax [ref:0165]
        int factor = Fx.Mul(Fx.Mul(skill, Fx.Mul(turn, glue)), trackFactor);
        if (humans.Any(h => Math.Abs(SliceDelta(c.Trk.Slice, h.Trk.Slice, slices)) <= 20))
            factor = Math.Clamp(factor, 0x9999, 0x14CCC); // clamp the factor near a human [ref:0166]
        if (c.Progress < 0x41) factor = Fx.Mul(factor, (0x41 - c.Progress) * 0x3F0 + Fx.One);
        c.Glue = factor;
        c.TimeScale = Math.Max(Fx.One, factor);
        speed = Fx.Mul(speed, factor);
        if (road != null) speed = FinishSpeed(c, road, slices, reverse, speed);
        c.AiTargetSpeed = speed * (reverse ? -1 : 1);
    }

    // Finished racers: roll at 11 m/s while inside the roadway [ref:0167]
    // then nearly stop (1/65536 m/s) on the shoulder or more than 60 slices beyond the line.
    public static int FinishSpeed(CarObj c, TrackSlice road, int slices, bool reverse, int speed)
    {
        if ((c.Flags & CarFlags.Racer) == 0 || c.RaceState != 2) return speed;
        int lo = -(road.Lanes >> 4) * road.HalfWidthLeft * 0x8000;
        int hi = (road.Lanes & 15) * road.HalfWidthRight * 0x8000;
        int left = c.LateralOffsetCur - c.HalfWidth, right = c.LateralOffsetCur + c.HalfWidth;
        bool past = reverse ? c.Trk.Slice < slices - 60 : c.Trk.Slice > 60;
        return past || left < lo || left > hi || right < lo || right > hi ? 1 : 0xB0000;
    }

    // Adds to the high word of the 16.16 score (150 points). [ref:0168]
    public static void ScoreFinished(CarObj c, TrackSlice road, Span<int> scores)
    {
        if ((c.Flags & CarFlags.Racer) == 0 || c.RaceState != 2) return;
        int column = c.Lane == 6 - (road.Lanes >> 4) || c.Lane == 7 + (road.Lanes & 15)
            ? 1 : c.Lane < 7 ? 0 : 2;
        scores[column] = unchecked(scores[column] + 150 * Fx.One);
    }

    public sealed class Scan
    {
        public int[] Speed { get; } = new int[3];
        public int[] SpeedNear { get; } = new int[3];
        public CarObj?[] Blocker { get; } = new CarObj?[3];
    }

    // first blocking car in cyclic car-list order, not the nearest car. [ref:0169]
    public static Scan ScanAhead(CarObj c, IReadOnlyList<CarObj> cars, TrackSlice[] slices, bool reverse, int lookAhead,
        IReadOnlyList<int>? occupancy = null)
    {
        var r = new Scan();
        Array.Fill(r.Speed, c.AiTargetSpeed);
        Array.Fill(r.SpeedNear, c.AiTargetSpeed);
        int dir = reverse ? -1 : 1;
        int index = cars.ToList().IndexOf(c);
        int occupied = 0;
        if ((c.AiFlags & 0x1000) != 0) { ScanOncoming(c, cars, slices, dir, index, occupancy, r); return r; }
        for (int j = 1; j < cars.Count && occupied != 7; j++)
        {
            int oi = (index + j) % cars.Count;
            var o = cars[oi];
            if (o.Active == 0 || (o.Flags & CarFlags.Static) != 0) continue;
            int d = SliceDelta(o.Trk.Slice, c.Trk.Slice, slices.Length) * dir;
            if (Math.Abs(d) < 3)
                d = (Fx.Mul(o.Pos.X - c.Pos.X, c.RoadFrame.M20) + Fx.Mul(o.Pos.Z - c.Pos.Z, c.RoadFrame.M22)) * dir;
            else d <<= 16;
            int range = (c.Flags & CarFlags.Opponent) != 0 && (o.AiFlags & 0x1000) != 0 ? lookAhead : 30;
            if (Math.Abs(c.AiSpeed - o.AiSpeed) > 0x190000) range *= 5;
            if (d <= c.HalfLength || d >= range * Fx.One) continue;
            int gap = d / Fx.One - 2;
            gap = Math.Max((c.Flags & CarFlags.Opponent) != 0 ? 1 : 0, gap);
            var sl = slices[o.Trk.Slice];
            int lo = AiDriver.LaneFromOffset(sl, o.LateralOffsetCur - o.LaneHalfWidth + 0x8000);
            int hi = AiDriver.LaneFromOffset(sl, o.LateralOffsetCur + o.LaneHalfWidth - 0x8000);
            for (int k = 0; k < 3; k++)
            {
                int lane = c.Lane + k - 1;
                if (lane < 0 || lane >= 14 || (occupancy == null ? lane < lo || lane > hi : (occupancy[oi] & (1 << lane)) == 0)
                    || (occupied & (1 << k)) != 0) continue;
                r.Speed[k] = o.AiSpeed + gap * 0x50000 * dir;
                r.SpeedNear[k] = o.AiSpeed + gap * 0x1B77C * dir;
                r.Blocker[k] = o;
                occupied |= 1 << k;
            }
        }
        return r;
    }

    /// <summary>Oncoming scan, used for a car with +0x3A9 &amp; 0x10 (oncoming traffic; also an opponent flagged wrong way by the psycho [ref:0170]
    /// logic): the list is walked backwards from the car itself, and a car counts when it is 1..29 slices behind it in race
    /// direction (slice delta · dir &lt; 0); speeds use (delta + 2) slices. Afterwards a traffic car (flag 0x10) whose same-lane
    /// blocker is closer than 3 slices halves the lane speeds (all three columns when the blocker is not faster than its target).</summary>
    private static void ScanOncoming(CarObj c, IReadOnlyList<CarObj> cars, TrackSlice[] slices, int dir, int index,
        IReadOnlyList<int>? occupancy, Scan r)
    {
        int occupied = 0, found = 0, n = cars.Count;
        for (int k = 0; k < n && found != 3; k++)
        {
            int oi = ((n + index - k) % n + n) % n;
            var o = cars[oi];
            if (ReferenceEquals(o, c) || o.Active == 0 || (o.Flags & CarFlags.Static) != 0) continue;
            int d = SliceDelta(o.Trk.Slice, c.Trk.Slice, slices.Length) * dir;
            if (!(d < 0 && -d < 0x1E)) continue;
            d += 2;
            for (int col = 0; col < 3; col++)
            {
                int lane = c.Lane + col - 1;
                if (col != 1 && (lane < 0 || lane >= 14)) continue;
                int bit = 1 << lane;
                if (occupancy == null || (occupancy[oi] & bit) == 0 || (occupied & (1 << col)) != 0) continue;
                r.Speed[col] = o.AiSpeed + d * 0x50000 * dir;
                r.SpeedNear[col] = o.AiSpeed + d * 0x1B77C * dir;
                r.Blocker[col] = o;
                occupied |= 1 << col;
                found++;
            }
        }
        var b = r.Blocker[1];
        if (b != null && (c.Flags & CarFlags.Traffic) != 0 && SliceDelta(c.Trk.Slice, b.Trk.Slice, slices.Length) * dir < 3)
        {
            r.SpeedNear[1] >>= 1; r.Speed[1] >>= 1;
            if (b.AiSpeed <= c.AiTargetSpeed) { r.SpeedNear[0] >>= 1; r.Speed[2] >>= 1; r.SpeedNear[2] >>= 1; r.Speed[0] >>= 1; }
        }
    }

    /// <summary>Rows 9–10: with traffic in the race (class-4 count != 0) [ref:0171]
    /// after the start phase (with k = 0xF), −3.0 on the half of the road against the traffic direction. [ref:0172]</summary>
    public static void ScoreKeepSide(CarObj c, TrackSlice sl, int opponents, bool reverse, int laneSide, int trafficCount, Span<int> scores)
    {
        if (trafficCount == 0 || InStartPhase(c, sl, opponents, reverse, 0xF)) return;
        bool oncoming = (c.AiFlags & 0x1000) != 0;
        bool preferRight = ((!oncoming) != reverse && laneSide == 1) || (oncoming != reverse && laneSide == -1);
        if (!preferRight)
        {
            if (c.Lane > 6) scores[1] -= 0x30000;
            if (c.Lane + 1 > 6) scores[2] -= 0x30000;
        }
        else
        {
            if (c.Lane - 1 < 7) scores[0] -= 0x30000;
            if (c.Lane < 7) scores[1] -= 0x30000;
        }
    }

    public static int Occupancy(CarObj c, TrackSlice sl)
    {
        int lo = AiDriver.LaneFromOffset(sl, c.LateralOffsetCur - c.LaneHalfWidth + 0x8000);
        int hi = AiDriver.LaneFromOffset(sl, c.LateralOffsetCur + c.LaneHalfWidth - 0x8000);
        int mask = 0;
        for (int lane = lo; lane <= hi; lane++) mask |= 1 << lane;
        return mask;
    }

    // The start phase is progress/list-order based, not a timer. [ref:0173]
    public static bool InStartPhase(CarObj c, TrackSlice sl, int opponents, bool reverse, int extra = 0)
    {
        if (c.Lane == 6 - (sl.Lanes >> 4) || c.Lane == 7 + (sl.Lanes & 15)) return false;
        int limit = opponents < 2 ? 50 : opponents < 5 ? 200 : 400;
        if (reverse) limit -= 26;
        return c.ListPosition * 14 + c.Progress <= limit + extra;
    }

    // Reads track parameter +0xC; it is populated by the track setup, and track 1 stays zero in BSS. [ref:0174]
    public static int TrackLaneSide(int track) => track == 1 ? 0 : track == 2 ? -1 : 1;

    // Row 16: after the start phase, bias adjacent columns toward the traffic side. [ref:0175]
    public static void ScoreLaneSide(CarObj c, TrackSlice sl, int opponents, bool reverse, int laneSide, Span<int> scores)
    {
        if (InStartPhase(c, sl, opponents, reverse)) return;
        bool oncoming = (c.AiFlags & 0x1000) != 0;
        bool preferRight = ((!oncoming) != reverse && laneSide == 1)
            || (oncoming != reverse && laneSide == -1);
        if (preferRight)
        {
            if (c.Lane - 1 < 7) scores[0] -= Fx.One;
            if (c.Lane < 7) scores[2] += Fx.One;
        }
        else
        {
            if (c.Lane + 1 > 6) scores[2] -= Fx.One;
            if (c.Lane > 6) scores[0] += Fx.One;
        }
    }

    // Choose the larger free gap around a close blocker, otherwise lane centre/shoulder and road clamp. [ref:0176]
    public static int TargetOffset(CarObj c, Scan scan, int lane, TrackSlice[] slices, int opponents, bool reverse,
        int blockedDirection = 0)
    {
        var sl = slices[c.Trk.Slice];
        var blocker = scan.Blocker[lane < c.Lane ? 0 : lane > c.Lane ? 2 : 1];
        if (blocker != null && Math.Abs(SliceDelta(blocker.Trk.Slice, c.Trk.Slice, slices.Length)) < 5
            && !InStartPhase(c, sl, opponents, reverse, 250))
        {
            var otherSlice = slices[blocker.Trk.Slice];
            int width = (lane < 7 ? otherSlice.HalfWidthLeft : otherSlice.HalfWidthRight) * 0x8000;
            int lo = lane < 7 ? -(7 - lane) * width : (lane - 7) * width;
            int hi = lo + width;
            int left = blocker.LateralOffsetCur - blocker.LaneHalfWidth - lo;
            int right = hi - blocker.LateralOffsetCur - blocker.LaneHalfWidth;
            int min = c.HalfWidth + c.HalfWidth / 2;
            if (min < left || min < right)
                return left < right ? blocker.LateralOffsetCur + blocker.LaneHalfWidth + right / 2
                    : blocker.LateralOffsetCur - blocker.LaneHalfWidth - left / 2;
        }
        int result;
        if (lane == 6 - (sl.Lanes >> 4) || lane == 7 + (sl.Lanes & 15))
            result = lane < 7 ? -(6 - lane) * sl.HalfWidthLeft * 0x8000 - c.HalfWidth
                : (lane - 7) * sl.HalfWidthRight * 0x8000 + c.HalfWidth;
        else result = AiDriver.LaneCentre(sl, lane);
        result = Math.Min(result, sl.RightEdge * 256 - c.HalfWidth);
        result = Math.Max(result, c.HalfWidth - sl.LeftEdge * 256);
        if (blockedDirection != 0) return result + (blockedDirection < 0 ? c.HalfWidth : -c.HalfWidth);
        return result + (((c.AiFlags & 0x1000) != 0) == reverse ? -c.LaneRandomOffset : c.LaneRandomOffset);
    }

    // Current masks plus lookahead mask sampling; the rightmost blocked side wins. [ref:0177]
    public static int ScoreMask(CarObj c, TrackSlice[] slices, bool reverse, int thinkTicks, Span<int> scores)
    {
        scores.Clear();
        int n = slices.Length, s = c.Trk.Slice;
        int dir = ((c.AiFlags & 0x1000) == 0) == reverse ? -1 : 1;
        int look = Math.Abs(c.AiSpeed / Fx.One) * 2;
        ushort Mask(int at) => slices[((at % n) + n) % n].Mask;
        int blocked = 0;
        for (int k = 0; k < 3; k++)
            if ((Mask(s) & LaneBit(c.Lane + k - 1)) == 0)
            {
                scores[k] -= 10 * Fx.One;
                blocked = k - 1;
            }
        int stride;
        if (Mask(s) == Mask(s + look * dir) && Mask(s) == Mask(s + (look + 1) * dir) && c.LaneScanCounter == 0)
            stride = look * 0x3333 / Fx.One;
        else
        {
            if (Mask(s) != Mask(s + look * dir) || Mask(s) != Mask(s + (look + 1) * dir)) c.LaneScanCounter = 0x49;
            stride = look * 0x51E / Fx.One;
            c.LaneScanCounter -= thinkTicks;
        }
        stride = Math.Max(1, stride);
        int seen = 0;
        for (int j = 0; j < look; j += stride)
            for (int k = 0; k < 3; k++)
                if ((seen & (1 << k)) == 0 && (Mask(s + dir * j) & LaneBit(c.Lane + k - 1)) == 0)
                {
                    seen |= 1 << k;
                    scores[k] -= 40 * Fx.One;
                    if (k != 1) blocked = k - 1;
                }
        return blocked;
    }

    // penalize adjacent lanes occupied by nearby cars; distance is projected onto our road frame. [ref:0178]
    public static void ScoreSideCars(CarObj c, IReadOnlyList<CarObj> cars, int slices, bool reverse, Span<int> scores)
    {
        if ((c.Flags & CarFlags.Opponent) == 0) return;
        int limit = c.HalfLength * 2 + c.HalfLength / 2;
        int dir = reverse ? -1 : 1;
        foreach (var other in cars)
        {
            if (ReferenceEquals(c, other) || other.Active == 0 || (other.Flags & CarFlags.Static) != 0) continue;
            int delta = SliceDelta(other.Trk.Slice, c.Trk.Slice, slices) * dir;
            if (delta < -2 || delta > 2) continue;
            int distance = (Fx.Mul(other.Pos.X - c.Pos.X, c.RoadFrame.M20)
                + Fx.Mul(other.Pos.Z - c.Pos.Z, c.RoadFrame.M22)) * dir;
            if (distance <= 0) distance = -distance;
            if (distance >= limit) continue;
            if (other.Lane == c.Lane - 1) scores[0] -= 5 * Fx.One;
            else if (other.Lane == c.Lane + 1) scores[2] -= 5 * Fx.One;
        }
    }

    /// <summary>
    /// −40 for a column whose lane is on the roadway here ([7 − hi, 6 + lo], slice +0x1F) [ref:0179]
    /// but not at the slice max(5, |+0x39C|·0.625) ahead (direction from +0x3A9 bit 4, xor reverse). [ref:0180]
    /// </summary>
    public static void ScoreNarrowing(CarObj c, TrackSlice[] slices, bool reverse, Span<int> scores)
    {
        int n = Math.Max(5, Fx.Mul(Math.Abs(c.AiSpeed), 0xA000) >> 16);
        if (((c.AiFlags & 0x1000) == 0) == reverse) n = -n;
        int at = c.Trk.Slice + n;
        if (at < 0) at += slices.Length; else if (at >= slices.Length) at -= slices.Length;
        byte ahead = slices[at].Lanes, here = slices[c.Trk.Slice].Lanes;
        static bool Inside(byte lanes, int lane) => 7 - (lanes >> 4) <= lane && lane <= (lanes & 0xF) + 6;
        for (int k = 0; k < 3; k++)
        {
            int lane = c.Lane + k - 1;
            if (!Inside(ahead, lane) && Inside(here, lane)) scores[k] -= 40 * Fx.One;
        }
    }

    // After the start phase, +5 for columns with no car in [-4 m, 106 m) along the road. [ref:0181]
    public static void ScoreFree(CarObj c, IReadOnlyList<CarObj> cars, TrackSlice[] slices, bool reverse,
        int opponents, Span<int> scores, IReadOnlyList<int>? occupancy = null)
    {
        if ((c.Flags & CarFlags.Opponent) == 0 || InStartPhase(c, slices[c.Trk.Slice], opponents, reverse)) return;
        int dir = ((c.AiFlags & 0x1000) == 0) == reverse ? -1 : 1;
        int occupied = 0;
        for (int i = 0; i < cars.Count; i++)
        {
            var other = cars[i];
            if (ReferenceEquals(c, other) || other.Active == 0 || (other.Flags & CarFlags.Static) != 0) continue;
            int delta = SliceDelta(other.Trk.Slice, c.Trk.Slice, slices.Length) * dir;
            int distance = delta < -1 || delta > 3 ? delta * 0x40000
                : (Fx.Mul(other.Pos.X - c.Pos.X, c.RoadFrame.M20)
                    + Fx.Mul(other.Pos.Z - c.Pos.Z, c.RoadFrame.M22)) * dir;
            if (distance < -0x40000 || distance >= 0x6A0000) continue;
            occupied |= occupancy?[i] ?? Occupancy(other, slices[other.Trk.Slice]);
        }
        for (int k = 0; k < 3; k++)
        {
            int lane = c.Lane + k - 1;
            // The current column is always checked; adjacent columns only have valid lane bits in 0..13.
            if (k != 1 && (lane < 0 || lane >= 14) || (occupied & (1 << lane)) == 0) scores[k] += 5 * Fx.One;
        }
    }

    /// <summary>
    /// (racing opponents only, flag 8): for every active local human (flag 4, [ref:0182]
    /// not parked) behind the car within [pers+0xB8, pers+0xB4·4 m) (distance along the road, ≥ −4 m; slice delta·4 m beyond
    /// −1..3 slices), standing on a lane of the road and faster than 20 m/s in race direction, +4.5 on the column of the human's
    /// lane – the AI blocks him. Only with skill level ≠ 0, outside the start phase (k = 100) and on a straight (track turn &lt; 5, [ref:0183]
    /// taken for THIS car). The honk part needs class-4 traffic. [ref:0184]
    /// </summary>
    public static void ScoreBlockHuman(CarObj c, IReadOnlyList<CarObj> cars, TrackSlice[] slices, bool reverse, int opponents,
        int skill, int trackTurn, Personality? pers, Span<int> scores)
    {
        if ((c.Flags & CarFlags.Opponent) == 0 || pers == null || skill == 0 || trackTurn >= 5) return;
        int dir = reverse ? -1 : 1;
        if (InStartPhase(c, slices[c.Trk.Slice], opponents, reverse, 100)) return;
        int limit = (int)((double)pers.BlockLookBehind * 4.0 * 65536.0);   // FILD · 4.0 · 65536.0, FRNDINT (chop)
        foreach (var h in cars)
        {
            if (ReferenceEquals(h, c) || h.Active == 0 || (h.Flags & CarFlags.Static) != 0 || (h.Flags & CarFlags.LocalHuman) == 0) continue;
            int delta = SliceDelta(c.Trk.Slice, h.Trk.Slice, slices.Length) * dir;
            int dist = delta < -1 || delta > 3 ? delta * 0x40000
                : (Fx.Mul(h.Pos.X - c.Pos.X, c.RoadFrame.M20) + Fx.Mul(h.Pos.Z - c.Pos.Z, c.RoadFrame.M22)) * dir;
            if (dist < -0x40000 || dist >= limit || dist < pers.BlockLookAhead) continue;
            var sl = slices[h.Trk.Slice];
            if (h.Lane < 7 - (sl.Lanes >> 4) || h.Lane > (sl.Lanes & 0xF) + 6) continue;
            if (h.AiSpeed * dir <= 0x140000) continue;
            scores[h.Lane < c.Lane ? 0 : h.Lane == c.Lane ? 1 : 2] += 0x48000;
        }
    }

    /// <summary>Implemented lane-score rows: mask, speed, edges, AI line, side cars, free lanes, traffic-side bias and parked cars. Other rows remain unported. [ref:0185]</summary>
    public static int ChooseLane(CarObj c, TrackSlice sl, Scan scan, bool reverse, int preferred, Span<int> scores,
        int[]? maskScores = null, IReadOnlyList<CarObj>? cars = null, int slices = 0,
        TrackSlice[]? track = null, int opponents = 0, IReadOnlyList<int>? occupancy = null, int laneSide = 1,
        IReadOnlyList<CarObj>? parked = null, int trafficCount = 0, int skill = 0, int trackTurn = 0, Personality? pers = null)
    {
        int dir = reverse ? -1 : 1;
        for (int k = 0; k < 3; k++)
        {
            int lane = c.Lane + k - 1, score = maskScores?[k] ?? 0;
            if (maskScores == null && (sl.Mask & LaneBit(lane)) == 0) score -= 10 * Fx.One;
            // rows 0..3: desired-speed penalties only after progress 149. [ref:0186]
            if (scan.Blocker[k] != null)
            {
                int d = (c.AiSpeed - scan.Speed[k]) * dir;
                if (d > 0) score += Fx.Mul(d, -0x14CCC);
                d = (c.AiSpeed - scan.SpeedNear[k]) * dir;
                if (d > 0) score += Fx.Mul(d, -0x8000);
                if (c.Progress > 149)
                {
                    d = (c.AiTargetSpeed - scan.Speed[k]) * dir;
                    if (d > 0) score += Fx.Mul(d, -0x8000);
                    d = (c.AiTargetSpeed - scan.SpeedNear[k]) * dir;
                    if (d > 0) score += Fx.Mul(d, -0x1999);
                }
            }
            // road edge/shoulder scores. [ref:0187]
            int lo = 6 - (sl.Lanes >> 4), hi = 7 + (sl.Lanes & 15);
            if (lane < lo || lane > hi) score -= 1000 * Fx.One;
            if (lane == lo || lane == hi)
            {
                score -= ((c.Flags & CarFlags.Opponent) != 0 ? 12 : 38) * Fx.One; // −0xC0000 opponent, −0x260000 traffic [ref:0188]
                int width = lane < 7 ? sl.LeftEdge * 256 - (sl.Lanes >> 4) * sl.HalfWidthLeft * 0x8000
                    : sl.RightEdge * 256 - (sl.Lanes & 15) * sl.HalfWidthRight * 0x8000;
                if (width < c.HalfWidth) score -= 100 * Fx.One;
            }
            // +2 toward the preferred lane, not necessarily its exact centre. [ref:0189]
            if (preferred != 0 && k == (preferred < c.Lane ? 0 : preferred > c.Lane ? 2 : 1)) score += 2 * Fx.One;
            scores[k] = score;
        }
        if (track != null) ScoreNarrowing(c, track, reverse, scores);
        if (cars != null)
        {
            if (track != null) ScoreFree(c, cars, track, reverse, opponents, scores, occupancy);
            ScoreSideCars(c, cars, slices, reverse, scores);
            if (track != null) ScoreBlockHuman(c, cars, track, reverse, opponents, skill, trackTurn, pers, scores);
        }
        ScoreLaneSide(c, sl, opponents, reverse, laneSide, scores);
        ScoreKeepSide(c, sl, opponents, reverse, laneSide, trafficCount, scores);
        // afterwards: an oncoming car with a faster (> 10 m/s) blocker in its own lane gets −30 on [ref:0190]
        // the column of the traffic side (the middle column on track 1) [ref:0191]
        if ((c.AiFlags & 0x1000) != 0 && scan.Blocker[1] is { } ahead && ahead.AiSpeed > 0xA0000) scores[1 + laneSide] -= 0x1E0000;
        ScoreFinished(c, sl, scores);
        if (parked != null && track != null)
            AiDriver.ScoreStatic(c, parked, track, (c.AiFlags & 0x1000) != 0, reverse, scores);
        // ties keep the current lane; left needs a strictly greater sum, then right a strictly greater one [ref:0192]
        int pick = c.Lane, best = scores[1];
        if (best < scores[0]) { pick--; best = scores[0]; }
        if (best < scores[2]) pick = c.Lane + 1;
        return Math.Clamp(pick, 0, 13);
    }

    // The reverse / non-oncoming path has an upper +1.3 cap, not a lower −1.3 cap. [ref:0193]
    public static void Follow(CarObj c, Scan scan, int targetLane, bool reverse, Personality? p, int slices)
    {
        int dir = reverse ? -1 : 1;
        var blocker = scan.Blocker[1];
        int v = scan.Speed[1];
        if (targetLane != c.Lane && blocker != null && blocker.Unk14E == 0)
        {
            int dv = Math.Abs(c.AiSpeed - blocker.AiSpeed);
            int offset = Math.Abs(c.LaneTargetOffset - c.LateralOffsetCur);
            int change = Fx.Mul(p?.LaneChangeSpeedFactor ?? 0x4000, c.AiSpeed);
            int cap = (p?.MaxLaneChangeSpeed ?? 0x33333) * dir;
            change = Math.Abs(reverse ? Math.Max(change, cap) : Math.Min(change, cap)); // lane change speed, limited by the personality's maximum [ref:0194]
            int lhs = Fx.Mul(Fx.Mul(offset, dv), 0x13333);
            int rhs = (int)(((long)(Math.Abs((int)c.Progress - blocker.Progress) << 18) * change) >> 16);
            if (lhs < rhs) v = scan.Speed[targetLane < c.Lane ? 0 : 2];
        }
        if ((c.Flags & CarFlags.Opponent) != 0 && targetLane == c.Lane && blocker != null)
        {
            int length = c.HalfLength + blocker.HalfLength;
            int gap = SliceDelta(blocker.Trk.Slice, c.Trk.Slice, slices) * dir * 0x40000;
            int limit = Math.Min(length * 2, (c.AiSpeed * dir / Fx.One / 10 + 1) * length);
            if (gap > 0 && gap < limit) v = Fx.Mul(v, 0xCCCC);
        }
        bool oncoming = (c.AiFlags & 0x1000) != 0;
        if ((!oncoming) == reverse) c.AiTargetSpeed = Math.Min(0x14CCC, Math.Max(c.AiTargetSpeed, v));
        else c.AiTargetSpeed = Math.Max(-0x14CCC, Math.Min(c.AiTargetSpeed, v));
        if (targetLane != c.Lane && blocker != null && Math.Abs(blocker.AiSpeed) < 0xA0000)
            c.AiTargetSpeed = ((!oncoming) == reverse) ? Math.Min(c.AiTargetSpeed, -0x40000) : Math.Max(c.AiTargetSpeed, 0x40000);
    }
}
