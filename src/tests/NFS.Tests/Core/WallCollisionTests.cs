using NFS.Core;
using NFS.Data.Track;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Core;

public class WallCollisionTests(ITestOutputHelper output)
{
    [Fact]
    public void CalcCorners_FrontPairAndCentrePair()
    {
        var c = new CarObj { HalfLength = 0x20000, HalfWidth = 0x10000, UpDotN = 0, PrevRoadHeight = 0x5000, Pos = new(0x100000, 0x8000, 0x200000) };
        c.DriveFrame = Mat3.Identity;
        WallCollision.CalcCorners(c);
        // F = fwd·(0x200)/256 = 2.0 along z; R = 1.0 along x; base y = previous road height
        Assert.Equal(new Vec3(0x100000 - 0x10000, 0x5000, 0x200000 + 0x20000), c.Corners[0]);
        Assert.Equal(new Vec3(0x100000 + 0x10000, 0x5000, 0x200000 + 0x20000), c.Corners[1]);
        Assert.Equal(new Vec3(0x100000 - 0x10000, 0x5000, 0x200000), c.Corners[2]);
        Assert.Equal(new Vec3(0x100000 + 0x10000, 0x5000, 0x200000), c.Corners[3]);
    }

    [Fact]
    public void WallSlide_DropsNormalVelocity_PushesOut()
    {
        var st = new int[29];
        st[WallCollision.StWallFriction] = 0x40000; // 4.0 → /4 = 1.0
        var c = new CarObj { Pos = new(0, 0, 0), Vel = new(-0x100000, 0x1000, 0x80000), Orient = Mat3.Identity };
        var n = new Vec3(Fx.One, 0, 0);                        // wall on −x, normal into the road (+x)
        int vn = Vec3.Dot(n, c.Vel);                           // −16.0
        int j = Math.Min(-Math.Abs(vn) / 16, -0x7AE);          // −1.0
        int half = WallCollision.WallSlide(c, j, new Mat3(n.X, n.Y, n.Z, 0, Fx.One, 0, -n.Z, 0, n.X), st);
        Assert.Equal(0x80000, half);
        Assert.Equal(new Vec3(-j, 0, 0), c.Pos);               // pushed +1.0 along n
        // tangent t = (0,0,1): vt = 8.0 > 5.0 → 8.0 − 8.0·1.0 = 0 (clamped at 0); v·n dropped; upward v.y dropped
        // (if v.y > 0 then v.y = 0 – confirmed by the original's state dump, tick 1982 of REPLAY.RPY) [ref:3109]
        Assert.Equal(new Vec3(0, 0, 0), c.Vel);
        Assert.Equal(new Vec3(0, 0, 0), c.LocalVel);

        var d = new CarObj { Vel = new(-0x100000, -0x1000, 0x80000), Orient = Mat3.Identity };
        WallCollision.WallSlide(d, j, new Mat3(n.X, n.Y, n.Z, 0, Fx.One, 0, -n.Z, 0, n.X), st);
        Assert.Equal(-0x1000, d.Vel.Y);                        // downward v.y kept
    }

    [Fact]
    public void WallSpin_KicksYaw_PlayerOnly()
    {
        var c = new CarObj { Flags = CarFlags.LocalHuman, DriveFrame = new Mat3(0, 0, -Fx.One, 0, Fx.One, 0, Fx.One, 0, 0) };
        WallCollision.WallSpin(c, 0x8000, new Vec3(0, 0, Fx.One), WallCollision.ImpactFlagsWall);
        Assert.Equal(new Vec3(0, Fx.One, 0), c.AngVel);             // a × fwd = (0,0,1) × (1,0,0)
        Assert.Equal((0x30000, 0x40004), (c.Impact, c.ImpactType));

        c.AngVel = Vec3.Zero; c.LocalVel = new Vec3(0, 0, 0x150000); // hard hit at speed: no kick
        WallCollision.WallSpin(c, 0xF0000, new Vec3(0, 0, Fx.One), 4);
        Assert.Equal(Vec3.Zero, c.AngVel);
        Assert.Equal(0xF0000 * 6, c.Impact);

        c.DriveFrame = new Mat3(Fx.One, 0, 0, 0, Fx.One, 0, 0, 0x8000, 0x8000); // a·fwd > 0 → negated
        c.LocalVel = Vec3.Zero;
        WallCollision.WallSpin(c, 0, new Vec3(0, 0, Fx.One), 4);
        Assert.Equal(new Vec3(0x8000, 0, 0), c.AngVel);              // −((0,0,1) × (0,.5,.5)) = −(−.5,0,0)

        // AI cars never get the angular kick (→ turns the orientation instead) [ref:3110]
        var ai = new CarObj { DriveFrame = c.DriveFrame, Orient = Mat3.Identity };
        WallCollision.WallSpin(ai, 0x8000, new Vec3(0, 0, Fx.One), 4);
        Assert.Equal((Vec3.Zero, 0x30000, 0x40004), (ai.AngVel, ai.Impact, ai.ImpactType));
    }

    [Fact]
    public void AiWallReact_TurnsForwardRowTowardTheWall_AndRebuildsRightRow()
    {
        // |a·driveFwd| = 0.5 > simtune[20] = 0 → forward row moves a fraction t = clamp(|v|/16, 0x3333, 0xC000) toward a
        var c = new CarObj { DriveFrame = new Mat3(0, 0, 0, 0, 0, 0, Fx.One / 2, 0, Fx.One / 2), Orient = Mat3.Identity };
        int[] st = new int[24];
        WallCollision.AiWallReact(c, 0x8000, new Vec3(Fx.One, 0, 0), 4, st);
        var o = c.Orient;
        Assert.InRange(o.M20, 0x3000, 0x4000);                    // (0x3333, 0, 0xCCCD) normalised ≈ (0.2425, 0, 0.9701)
        Assert.InRange(o.M22, 0xF000, 0x10000);   // uses a sqrt table (approximate) [ref:3111]
        Assert.Equal((o.M22, -o.M20), (o.M00, o.M02));            // right row = up × forward
        Assert.Equal((0x30000, 0x40004), (c.Impact, c.ImpactType));

        // a wall direction against the heading is negated first (a·driveFwd < 0)
        var d = new CarObj { DriveFrame = c.DriveFrame, Orient = Mat3.Identity };
        WallCollision.AiWallReact(d, 0x8000, new Vec3(-Fx.One, 0, 0), 4, st);
        Assert.Equal(o.M20, d.Orient.M20);

        // small |a·driveFwd| ≤ threshold: forward row nudged by (right/128 · steer)/16 – only with the throttle byte > 0x32
        st[20] = Fx.One;
        var e = new CarObj { DriveFrame = c.DriveFrame, Orient = Mat3.Identity, ThrottleRamped = 0x20, Steer = 0x80000 };
        WallCollision.AiWallReact(e, 0x8000, new Vec3(Fx.One, 0, 0), 4, st);
        Assert.Equal((0, 0), (e.Orient.M20, e.Orient.M21));        // only re-normalised (table sqrt: ≈ 1.0)
        Assert.InRange(e.Orient.M22, 0xFC00, 0x10000);
        e.ThrottleRamped = 0x40;
        WallCollision.AiWallReact(e, 0x8000, new Vec3(Fx.One, 0, 0), 4, st);
        Assert.InRange(e.Orient.M20, 0xF000, 0x10000);    // forward row swung to ≈ the old right row

        // hard hit at speed: only the impact fields are written
        var f = new CarObj { DriveFrame = c.DriveFrame, Orient = Mat3.Identity, LocalVel = new Vec3(0, 0, 0x150000) };
        WallCollision.AiWallReact(f, 0xF0000, new Vec3(Fx.One, 0, 0), 4, new int[24]);
        Assert.Equal((Mat3.Identity, 0xF0000 * 6), (f.Orient, f.Impact));   // orientation untouched
    }

    private static TrackSlice EdgeSlice(byte surround) =>
        new(0, 0, 0, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0x500, 0x500, surround, 0, 0, 0, 0, 0);

    private static CarObj EdgeCar(int x, int vx) => new()
    {
        Flags = CarFlags.LocalHuman, Pos = new(x, 0x8000, 0), Vel = new(vx, 0, 0x100000), Orient = Mat3.Identity,
        HalfWidth = 0x10000, HalfLength = 0x20000, UpDotN = Fx.One, Gear = 1,
        RoadFrame = new Mat3(126 << 9, 0, 0, 0, 126 << 9, 0, 0, 0, 126 << 9), DriveFrame = Mat3.Identity,
    };

    [Fact]
    public void RoadEdge_RightHit_PushesBackAndSlides()
    {
        var sl = new[] { EdgeSlice(0x11) };
        var st = new int[29];
        var c = EdgeCar(0x4CCCC, 0x20000);                 // 4.8 m right of the centre, moving outward 2 m/s
        int lat = Vec3.Dot(new Vec3(126 << 9, 0, 0), new Vec3(0x4CCCC + 0x20000 / 32, 0x8000, 0x100000 / 32));
        int lim = 0x500 * 0x100 - Fx.Mul(0x10000, 126 << 9) - 0x4000;   // e = |halfW·(right·R)| (halfL term 0)
        Assert.Equal(1, WallCollision.RoadEdgeCheck(c, sl, st, true));
        Assert.Equal(lat, c.LateralOffset);
        Assert.Equal(1, c.EdgeHit);
        int j = 2 * (lat - lim);
        Assert.Equal(0x4CCCC - Fx.Mul(j, 126 << 9), c.Pos.X);        // pos.x −= j·M00
        Assert.Equal(0, c.Vel.X);                                    // normal component removed
        Assert.Equal(Fx.Mul(Fx.Mul(0x100000, 126 << 9), 126 << 9), c.Vel.Z); // tangential kept (< 5.0 → no friction)
        Assert.Equal(new Vec3(0x4CCCC + 0xFC00, 0x8000, 0), c.LastHit);   // pos + R·(halfW/256)/256 before the push
    }

    [Fact]
    public void RoadEdge_NoHit_InsideOrWithoutWallNibble()
    {
        var st = new int[29];
        var c = EdgeCar(0, 0x20000);
        Assert.Equal(0, WallCollision.RoadEdgeCheck(c, [EdgeSlice(0x11)], st, true));
        Assert.Equal(0, c.EdgeHit);
        c = EdgeCar(-0x4CCCC, -0x20000);                   // left side beyond the edge, but nibble (high) = 0
        Assert.Equal(0, WallCollision.RoadEdgeCheck(c, [EdgeSlice(0x01)], st, true));
        c = EdgeCar(-0x4CCCC, -0x20000);
        Assert.Equal(-1, WallCollision.RoadEdgeCheck(c, [EdgeSlice(0x10)], st, true));
        Assert.True(c.Pos.X > -0x4CCCC);
    }

    private static Vec3 QuadCentre(TrackGeometry g, int block, int poly)
    {
        var q = g.RoadPolys[block][poly];
        var v = g.Verts[block];
        int nb = block + 1 >= g.NumBlocks ? 0 : block + 1;
        Vec3 W(int i) { var r = i < g.NumStick[block] ? g.BlockRef[nb] : g.BlockRef[block]; return new(r.X + v[i * 3] * 256, r.Y + v[i * 3 + 1] * 256, r.Z + v[i * 3 + 2] * 256); }
        Vec3 a = W(q.V0), b = W(q.V1), c = W(q.V2), d = W(q.V3);
        return new((a.X + b.X + c.X + d.X) / 4, (a.Y + b.Y + c.Y + d.Y) / 4, (a.Z + b.Z + c.Z + d.Z) / 4);
    }

    [GameFact]
    public void CarDrivingIntoWall_IsStopped()
    {
        var r = TrackSessions.FreshReplay();
        var loc = TrackSessions.Locator(r);
        var g = loc.Geometry;
        var q = new TrackRoadQuery(loc, r.TrackNumber);
        var st = r.SimTune!.For(true).Values;

        // a slice whose road row has a wall column (surface & 0x3F == 0) next to a drivable one
        int found = 0, sSel = -1, wallCol = -1, roadCol = -1;
        for (int s = 40; s < g.NumSlices - 40; s++)
        {
            var sr = g.SliceRoadOf(s);
            int b = g.Slices[s].BlockNumber;
            for (int k = 1; k < sr.NumPolys; k++)
            {
                int f0 = g.Attrs[b][sr.FirstPoly + k - 1].Flags, f1 = g.Attrs[b][sr.FirstPoly + k].Flags;
                bool w0 = (f0 & 0x3F) == 0, w1 = (f1 & 0x3F) == 0;
                bool d0 = !w0 && f0 != 0x0E, d1 = !w1 && f1 != 0x0E;
                if (w1 && d0 || w0 && d1)
                {
                    found++;
                    if (sSel < 0 && s >= 200) { sSel = s; wallCol = w1 ? k : k - 1; roadCol = w1 ? k - 1 : k; }
                }
            }
        }
        var hist = new SortedDictionary<int, int>();
        for (int s = 0; s < g.NumSlices; s++)
        {
            var sr = g.SliceRoadOf(s);
            int b = g.Slices[s].BlockNumber;
            for (int k = 0; k < sr.NumPolys; k++) { int f = g.Attrs[b][sr.FirstPoly + k].Flags; hist[f] = hist.GetValueOrDefault(f) + 1; }
        }
        output.WriteLine("road column surface bytes: " + string.Join(", ", hist.Select(kv => $"{kv.Key:X2}×{kv.Value}")));
        output.WriteLine($"wall/road column pairs: {found}; using slice {sSel} wall col {wallCol} road col {roadCol}");
        Assert.True(sSel >= 0);

        int blk = g.Slices[sSel].BlockNumber;
        var srSel = g.SliceRoadOf(sSel);
        var start = QuadCentre(g, blk, srSel.FirstPoly + roadCol);
        var wall = QuadCentre(g, blk, srSel.FirstPoly + wallCol);
        var dir = new Vec3(wall.X - start.X, 0, wall.Z - start.Z);
        FxMath.NormalizeBig(ref dir);
        var right = new Vec3(dir.Z, 0, -dir.X); // up × fwd with up = (0,1,0)

        var car = r.Cars[0].Obj;
        TrackRoadQueryTests.Prepare(car);
        car.Trk.Slice = sSel;
        car.Pos = new Vec3(start.X, start.Y + car.HalfHeight, start.Z);
        car.Orient = new Mat3(right.X, right.Y, right.Z, 0, Fx.One, 0, dir.X, dir.Y, dir.Z);
        car.DriveFrame = car.Orient;
        car.Vel = Vec3.Scale(dir, 15 * Fx.One);   // 15 m/s straight at the wall
        car.LocalVel = new Vec3(0, 0, 15 * Fx.One);

        int hits = 0;
        for (int t = 0; t < 64; t++)
        {
            TrackRoadQueryTests.Tick(car, q, st, 0x300 + t * 2);
            WallCollision.CalcCorners(car);
            if (WallCollision.CheckCorners(car, loc, r.TrackNumber, st)) hits++;
            var at = TrackLocator.Clone(car.Trk);
            loc.Update(car.Pos, at);
            Assert.NotEqual(0, TrackLocator.SurfaceFlags(at) & 0x3F); // the centre never gets onto the wall polygon
        }
        int towardWall = Vec3.Dot(car.Vel, dir);
        output.WriteLine($"hits {hits}, v {car.Vel}, v·dir {towardWall:X}, pos {car.Pos}");
        Assert.True(hits > 0);
        Assert.True(towardWall <= 0x8000, "velocity into the wall removed");
    }
}
