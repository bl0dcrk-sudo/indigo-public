using System.Numerics;
using NFS.Core;
using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>XBID 11 track objects of Last Resort (TR04): every type-4 reference (boulders [ref:3304]
/// signs, posts) is drawn from its XBID 8 model at the reference position; a knocked-over object [ref:3305]
/// tumbles along its CAN animation (TR0402/03.CAN) and then lies flat in the direction the car was travelling.</summary>
public class TrackObjectsTests
{
    static readonly Lazy<TrackRenderer> Tr = new(() => new TrackRenderer(4, GameData.Dir));
    const int SignBlock = 22;            // XBID 11 point 0 = type-2 sign (object id 3), model 8 vertices, 665/256 = 2.6 m tall
    const int BoulderBlock = 125;        // cube boulders (6 polygons), 3–4 m radius

    record Obj(int Block, int Index, Vector3 Pos, TrackStructure Model, ushort Id, byte Type);

    static List<Obj> Objects()
    {
        var r = Tr.Value; var list = new List<Obj>();
        for (int bi = 0; bi < r.Trk.Blocks.Count; bi++)
        {
            var b = r.Trk.Blocks[bi];
            var pts = b.Xb(11)?.Points() ?? [];
            var structs = b.Xb(8)?.Structures() ?? [];
            var refs = new[] { 7, 19, 18 }.SelectMany(x => b.Xb(x)?.ObjectRefs() ?? []).Where(x => x.Type == 4).ToArray();
            foreach (var rf in refs)
            {
                var p = pts[rf.Raw[16]];
                list.Add(new Obj(bi, rf.Raw[16], new Vector3(p.X, p.Y, p.Z) / 65536f, structs[rf.StructIndex], p.ObjectId, p.Type));
            }
        }
        return list;
    }

    static Camera CamAt(Obj o) => LookAt(o.Pos + new Vector3(7, 2.5f, 3), o.Pos + new Vector3(0, 1, 0));

    static Camera LookAt(Vector3 from, Vector3 to)
    {
        var cam = new Camera { Position = from };
        cam.LookAlong(Vector3.Normalize(to - from), Vector3.UnitY);
        return cam;
    }

    static int Diff(FrameBuffer a, FrameBuffer b)
    {
        int n = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.Get(x, y) != b.Get(x, y)) n++;
        return n;
    }

    static FrameBuffer Frame(Camera cam, RenderOptions? o = null, DynamicObjects? dyn = null, int tick = 0)
    {
        var fb = new FrameBuffer();
        Tr.Value.Render(fb, cam, o, null, dyn?.Knocked, tick);
        return fb;
    }

    [GameFact]
    public void Track4_EveryXbid11PointHasADrawnModel_AllObjectsAppearInTheFrame()
    {
        var objs = Objects();
        int points = Enumerable.Range(0, Tr.Value.Trk.Blocks.Count).Sum(b => Tr.Value.Trk.Blocks[b].Xb(11)?.Points().Length ?? 0);
        Assert.Equal(79, points);
        Assert.Equal(points, objs.Count);                       // every XBID 11 point is referenced by a type-4 object
        Assert.Equal(5, objs.Count(o => o.Type == 2));          // hittable signs
        Assert.Equal(8, objs.Count(o => o.Model.Polygons.Length == 6));   // cube boulders (TR04 blocks 124/125)
        foreach (var o in objs)
        {
            var cam = o.Pos.Y > 0 ? LookAt(o.Pos + new Vector3(10, 2.5f, 4), o.Pos + new Vector3(0, 1, 0)) : CamAt(o);
            // this camera sees the object from the side; the frame must differ from the same frame without objects
            Assert.True(Diff(Frame(cam), Frame(cam, new RenderOptions { Objects = false })) > 30, $"object block {o.Block} point {o.Index} is not drawn");
        }
    }

    [GameFact]
    public void BlockObjects_AreNotCutByTheObjectRange()
    {
        // passes range −1 for block objects (only the COL globals use view +0xA4) [ref:3306]
        var o = Objects().First(x => x.Block == BoulderBlock);
        var cam = CamAt(o);
        Assert.True(Diff(Frame(cam, new RenderOptions { ObjectRange = 1f }), Frame(cam, new RenderOptions { Objects = false })) > 1000);
    }

    [GameFact]
    public void ColGlobals_Between250And500Metres_AreDrawnByTheFullScreenDefault()
    {
        var r = Tr.Value;
        // Place the camera 300 m behind each COL object and look directly at it. Block objects are unaffected;
        // only a COL reference inside the 500 m view range can increase the emitted polygon count.
        foreach (var obj in r.Col.ObjectRefs.Where(x => x.Type is 1 or 3 or 4 && x.StructIndex < r.Col.Structures.Length))
        {
            var pos = obj.Type == 3 ? AnimatedObjectPose.Eval(obj.Raw, 0) : null;
            if (obj.Type == 3 && pos is null) continue;
            var target = pos is { } a ? new Vector3(a.X, a.Y, a.Z) / 65536f : new Vector3(obj.X, obj.Y, obj.Z) / 65536f;
            var cam = new Camera { Position = target - new Vector3(0, 0, 300) };
            cam.LookAlong(Vector3.UnitZ, Vector3.UnitY);
            r.Render(new FrameBuffer(), cam, new RenderOptions { Sky = false });
            int fullScreenDefault = r.LastEmitted;
            r.Render(new FrameBuffer(), cam, new RenderOptions { Sky = false, ObjectRange = 250f });
            int cappedAt250 = r.LastEmitted;
            if (fullScreenDefault > cappedAt250) return;
        }
        Assert.Fail("TR04 COL has no visible object in the 250–500 m band for the test camera.");
    }

    [GameFact]
    public void KnockedSign_IsDrawnTumbledAndLyingNotStanding()
    {
        var r = Tr.Value;
        var sign = Objects().First(x => x.Block == SignBlock);
        Assert.Equal(2, sign.Type);
        var dyn = new DynamicObjects(new TrackGeometry(r.Trk, r.Slices)) { Tick = 100 };
        var pt = r.Trk.Blocks[SignBlock].Xb(11)!.Points()[sign.Index];
        var car = new CarObj { Flags = CarFlags.LocalHuman, Vel = new Vec3(20 << 16, 0, 0) };
        Assert.Equal(-1, dyn.Hit(car, pt));
        var cam = CamAt(sign);
        var standing = Frame(cam);
        Assert.Equal(0, Diff(standing, Frame(cam, null, dyn, 99)));                    // before the hit tick nothing differs... 
        Assert.True(Diff(standing, Frame(cam, null, dyn, 100 + 6 * 3)) > 100);         // tumbling (CAN 2, key 3)
        var lying = Frame(cam, null, dyn, 100 + 1000);
        Assert.True(Diff(standing, lying) > 100);                                      // lying flat
        Assert.Equal(0, Diff(lying, Frame(cam, null, dyn, 100 + 5000)));               // stays there
    }

    [GameFact]
    public void Hit_RecordsTickCanFileBySpeedAndAngleByteOfTheVelocity()
    {
        var r = Tr.Value;
        var dyn = new DynamicObjects(new TrackGeometry(r.Trk, r.Slices)) { Tick = 777 };
        TrackPoint P(ushort id) => new(0, 0, 0, 0x100, id, 0, 0, 0, 2);
        Assert.Equal(-1, dyn.Hit(new CarObj { Flags = CarFlags.LocalHuman, Vel = new Vec3(20 << 16, 0, 0) }, P(1)));
        Assert.Equal(new KnockedObject(777, 2, 0x40), dyn.Knocked[1]);                       // < 25 m/s → CAN 2; +x → angle 0x4000 >> 8
        Assert.Equal(-1, dyn.Hit(new CarObj { Flags = CarFlags.LocalHuman, Vel = new Vec3(0, 0, 25 << 16) }, P(2)));
        Assert.Equal(new KnockedObject(777, 3, 0), dyn.Knocked[2]);                          // ≥ 25 m/s → CAN 3; +z → angle 0
        Assert.Equal(-1, dyn.Hit(new CarObj { Flags = CarFlags.NotLocal, Vel = new Vec3(-12 << 16, 0, 0) }, P(3)));
        Assert.Equal(-64, dyn.Knocked[3].Angle);                                             // −x → −0x4000 >> 8
        dyn.Tick = 900;
        Assert.Equal(0, dyn.Hit(new CarObj { Flags = CarFlags.LocalHuman, Vel = new Vec3(20 << 16, 0, 0) }, P(1)));
        Assert.Equal(777, dyn.Knocked[1].HitTick);                                           // only the first hit counts
        Assert.Equal(3, dyn.Knocked.Count);
    }

    static Vector3 Apply(int[] m, Vector3 v) =>   // row vector · matrix, 16.16 [ref:3307]
        new Vector3(v.X * m[0] + v.Y * m[3] + v.Z * m[6], v.X * m[1] + v.Y * m[4] + v.Z * m[7], v.X * m[2] + v.Y * m[5] + v.Z * m[8]) / 65536f;

    [Theory]
    [InlineData(0x40, 0)]
    [InlineData(0x40, 0x20)]
    [InlineData(0, -0x30)]
    [InlineData(-0x40, 0x7F)]
    [InlineData(0x55, -0x80)]
    public void LyingPose_TopOfTheObjectFallsFlatInTheDirectionOfTheHit(int angle, int heading)
    {
        var (m, ox, oy, oz, lying) = KnockedObjectPose.Pose(null, 0, (sbyte)angle, (sbyte)heading);   // no CAN file → state −2 at once
        Assert.True(lying);
        Assert.Equal((0, 0, 0), (ox, oy, oz));
        var top = Apply(m, new Vector3(0, 2.6f, 0));                                                   // sign: 665/256 m tall
        double a = angle * 256 * 2 * Math.PI / 65536;
        Assert.Equal(0, top.Y, 2);                                                                     // lies on the ground
        Assert.Equal(2.6 * Math.Sin(a), top.X, 2);                                                     // travel direction (+z toward +x)
        Assert.Equal(2.6 * Math.Cos(a), top.Z, 2);
    }

    [GameFact]
    public void CanTumble_StartsUprightFlyingUpForFastHitsAndEndsLying()
    {
        var can2 = Can.Load("GAMEDATA/TRACKS/DATA/PC/TR0402.CAN", GameData.Dir);
        var can3 = Can.Load("GAMEDATA/TRACKS/DATA/PC/TR0403.CAN", GameData.Dir);
        var up = new Vector3(0, 2.6f, 0);
        var (m0, _, _, _, l0) = KnockedObjectPose.Pose(can2, 0, 0x40, 0x10);
        Assert.False(l0);
        Assert.True((Apply(m0, up) - up).Length() < 0.01f);                                           // key 0 = identity
        var (m3, _, y3, _, _) = KnockedObjectPose.Pose(can3, 3 * 6, 0x40, 0x10);                      // fast hit, key 3
        Assert.Equal(3.0, y3 / 65536.0, 2);                                                           // CAN 3 throws the object up 3 m
        var (_, _, _, _, lEnd) = KnockedObjectPose.Pose(can2, (can2.Keys.Count - 1) * 6, 0x40, 0x10);
        Assert.True(lEnd);                                                                            // animation over (returns 0) → lying [ref:3308]
        var (mMid, _, _, _, lMid) = KnockedObjectPose.Pose(can2, 4 * 6 + 3, 0x40, 0x10);
        Assert.False(lMid);
        Assert.InRange(Apply(mMid, up).Y, 0.5f, 1.5f);                                                // between key 4 and 5: tipped well over, not yet flat
    }
}
