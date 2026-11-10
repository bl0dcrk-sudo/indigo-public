using System.Numerics;
using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;
using Xunit.Abstractions;

namespace NFS.Tests.Render;

/// <summary>Road marking lines: (XBID 9 → 4 s16 vertices per entry) and the polygon splitters of [ref:3282]
/// textures lin0..7. [ref:3283]</summary>
public class LaneMarksTests(ITestOutputHelper log)
{
    static readonly Vector3 A = new(0, 0, 10), B = new(16, 0, 10), C = new(16, 0, 26), D = new(0, 0, 26);   // a,b,c,d = record vertex pointers +0xC..+0x18

    static LaneMarks.Quad[] Split(int type, int key = 0, float z = 20)
    {
        var q = new LaneMarks.Quad[2];
        int n = LaneMarks.Split(type, key, z, A, B, C, D, q);
        return q[..n];
    }

    [Fact]
    public void Beyond110Metres_OneFlatQuadOfLin0OrLin1()
    {
        var far0 = Split(3, 0, 110.5f); var far5 = Split(5, 0, 150);
        Assert.Single(far0); Assert.Equal(0, far0[0].Texture); Assert.Equal((A, B, C, D), (far0[0].A, far0[0].B, far0[0].C, far0[0].D));
        Assert.Single(far5); Assert.Equal(1, far5[0].Texture);
        Assert.Equal(2, Split(3, 0, 110f).Length);   // z ≤ 0x6E0000 is still the detailed split
    }

    [Theory]
    [InlineData(0, 0, 2, 6)]   // flag 0: key&6 = 0 → Table[0] = 2, second polygon Table[2+2] = 6
    [InlineData(0, 2, 4, 4)]   //          key&6 = 2 → Table[2] = 4 → Table[6] = 4
    [InlineData(0, 4, 6, 2)]   //          key&6 = 4 → Table[4] = 6 → Table[8] = 2
    [InlineData(0, 6, 4, 4)]
    [InlineData(5, 0, 3, 7)]   // type 5..7 = flag 1: Table[1] = 3 → Table[5] = 7
    [InlineData(5, 2, 5, 5)]
    [InlineData(5, 4, 7, 3)]
    public void TextureVariantFollowsVertexPosition(int type, int key, int first, int second)
    {
        var q = Split(type == 5 ? 7 : 3, key);   // type 7 = splitter 2 (two polygons) with flag 1; type 3 = splitter 3 with flag 0
        Assert.Equal(2, q.Length);
        Assert.Equal(first, q[0].Texture); Assert.Equal(second, q[1].Texture);
        Assert.Equal(Split(type == 5 ? 7 : 3, key | 1).Select(x => x.Texture), q.Select(x => x.Texture));   // bit 0 of the key is ignored
    }

    [Fact]
    public void Splitters_FiveSixteenthsAlongBothEdges()
    {
        // type 2 : the two end strips, 5/16 of the a→b / d→c edge each [ref:3284]
        var s = Split(2);
        Assert.Equal(new Vector3(5, 0, 10), s[0].B); Assert.Equal(new Vector3(5, 0, 26), s[0].C);   // a→b: 16 m · 5/16
        Assert.Equal(new Vector3(11, 0, 10), s[1].A); Assert.Equal(new Vector3(11, 0, 26), s[1].D);
        Assert.Equal(B, s[1].B); Assert.Equal(C, s[1].C);
        // type 0 : the middle part between 5/16 and 11/16 [ref:3285]
        var m = Split(0).Single();
        Assert.Equal((new Vector3(5, 0, 10), new Vector3(11, 0, 10), new Vector3(11, 0, 26), new Vector3(5, 0, 26)), (m.A, m.B, m.C, m.D));
        // type 1 : from the middle of the strip (between 5/16 and the far end) to the far end [ref:3286]
        var f = Split(1).Single();
        Assert.Equal(new Vector3(5, 0, 18), f.A); Assert.Equal(new Vector3(11, 0, 18), f.B); Assert.Equal(new Vector3(11, 0, 26), f.C); Assert.Equal(new Vector3(5, 0, 26), f.D);
    }

    [Fact]
    public void Splitters_Type3AndType4_HalfWidthStrips()
    {
        var t3 = Split(3);   // strip at a/d, and the half-width strip at b/c [ref:3287]
        Assert.Equal((A, new Vector3(5, 0, 10), new Vector3(5, 0, 26), D), (t3[0].A, t3[0].B, t3[0].C, t3[0].D));
        Assert.Equal(new Vector3(11, 0, 18), t3[1].A); Assert.Equal(new Vector3(16, 0, 18), t3[1].B);   // ((b − d34) + e2)/2, (b + c)/2
        Assert.Equal(C, t3[1].C);
        var t4 = Split(4);   // mirror image – half-width strip at a/d, full strip at b/c [ref:3288]
        Assert.Equal(new Vector3(0, 0, 18), t4[0].A); Assert.Equal(new Vector3(5, 0, 18), t4[0].B); Assert.Equal(D, t4[0].D);
        Assert.Equal((new Vector3(11, 0, 10), B, C, new Vector3(11, 0, 26)), (t4[1].A, t4[1].B, t4[1].C, t4[1].D));
    }

    static TrackSlice Slice(sbyte[] forward, sbyte[] right) => new(0, 0, 0, [0, 0, 0], forward, right, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void BuildSlots_QuadFromTwoEntries_ForwardHalfAndRightVector()
    {
        // vertices: 0 = (100,0,0) in the block, 1 = (100,0,1000); slice 0: forward (0,0,126), right (126,0,0)
        short[] verts = [100, 0, 0, 100, 0, 1000];
        byte[] entries = [0, 0, 1, 0, 1, 0, 0xFF, 0];
        var slots = LaneMarks.BuildSlots(entries, 2, verts, 0, [Slice([0, 0, 126], [126, 0, 0])], 0, new int[3], new int[3]);
        Assert.Equal(new short[] { 100, 0, -63, 226, 0, -63, 100, 0, 1063, 226, 0, 1063 }, slots[..12]);   // A − w, A − w + d, B + w, B + w + d (w = forward / 2)
        Assert.All(slots[12..], v => Assert.Equal(0, v));   // the last entry has no partner
    }

    [Fact]
    public void BuildSlots_SeamVerticesAreShiftedByTheReferenceDeltaPlusItsFiveHundredthsPart()
    {
        // next block 100 m further in x: delta = (100·65536 >> 8) = 25600 → (short) 25600 + (25600 >> 9 = 50) = 25650 (1/256 m); only vertices < numStick
        short[] verts = [10, 0, 0, 10, 0, 0];
        byte[] entries = [0, 0, 1, 0, 1, 0, 0xFF, 0];
        var sl = Slice([0, 0, 0], [0, 0, 0]);
        var slots = LaneMarks.BuildSlots(entries, 2, verts, 1, [sl], 0, new int[3], [100 * 65536, 0, 0]);
        Assert.Equal(10 + 25650, slots[0]);   // vertex 0 < numStick: relative to the next block
        Assert.Equal(10, slots[6]);           // vertex 1 ≥ numStick: own block
        // the 16-bit wrap of the original (s16 arithmetic): a delta of 200 m wraps in (short)(x >> 8)
        var wrap = LaneMarks.BuildSlots(entries, 2, verts, 1, [sl], 0, new int[3], [200 * 65536, 0, 0]);
        int d = unchecked((short)(200 * 65536 >> 8)); d += d >> 9;
        Assert.Equal((short)(10 + d), wrap[0]);
    }

    [Fact]
    public void BuildSlots_UsesFirstSliceOfTheBlockAndSkipsMissingSlices()
    {
        short[] verts = [0, 0, 0, 0, 0, 100];
        byte[] entries = [0, 1, 1, 0, 1, 1, 0xFF, 0];   // slice 1 of the block, block starts at slice 2 → slice 3
        var slices = new[] { Slice([0, 0, 0], [0, 0, 0]), Slice([0, 0, 0], [0, 0, 0]), Slice([0, 0, 0], [0, 0, 0]), Slice([0, 0, 40], [10, 0, 0]) };
        var slots = LaneMarks.BuildSlots(entries, 2, verts, 0, slices, 2, new int[3], new int[3]);
        Assert.Equal(new short[] { 0, 0, -20, 10, 0, -20 }, slots[..6]);
        Assert.All(LaneMarks.BuildSlots(entries, 2, verts, 0, slices, 9, new int[3], new int[3]), v => Assert.Equal(0, v));   // slice out of range
    }

    [Fact]
    public void Key_IsXPlusZOfTheRawVertex()
    {
        short[] verts = [3, 77, 4, -9, 1, 20];
        Assert.Equal(7, LaneMarks.Key(verts, 0)); Assert.Equal(11, LaneMarks.Key(verts, 1)); Assert.Equal(0, LaneMarks.Key(verts, 5));
    }

    // ---- game data ----

    static IEnumerable<int> Tracks() => Enumerable.Range(0, 9).Where(t => GameData.Dir != null && File.Exists(Path.Combine(GameData.Dir, $"GAMEDATA/TRACKS/SE/TR{t:00}.TRK")));

    [GameFact]
    public void Xbid9Entries_AreConsistentInEveryTrack()
    {
        int total = 0;
        foreach (int t in Tracks())
        {
            var r = new TrackRenderer(t, GameData.Dir);
            int blocks = 0, entries = 0, strips = 0;
            foreach (var b in r.Trk.Blocks)
            {
                if (b.Xb(9) is not { } xb) continue;
                blocks++; entries += xb.Count;
                for (int e = 0; e < xb.Count; e++)
                {
                    var rec = xb.Record(e, 4);
                    Assert.True(rec[0] < b.NumVertices, $"TR{t:00} block {b.Index} entry {e}: vertex {rec[0]} of {b.NumVertices}");
                    Assert.True(b.FirstSlice + rec[1] < r.Slices.Length);
                    Assert.True(rec[2] <= 9 || rec[2] == 0xFF, $"type {rec[2]}");
                    if (rec[2] == 0xFF) strips++;
                }
                Assert.Equal(0xFF, xb.Record(xb.Count - 1, 4)[2]);   // every block's list ends with a strip terminator
            }
            log.WriteLine($"TR{t:00}: XBID 9 in {blocks}/{r.Trk.Blocks.Count} blocks, {entries} entries, {strips} strips; textures lin0..7: {r.LaneTextures.Count(x => x != null)}");
            Assert.Equal(8, r.LaneTextures.Count(x => x != null));
            total += entries;
        }
        Assert.True(total > 0);
    }

    [GameFact]
    public void LaneMarksAppearOnlyInNearBlocksAndCanBeSwitchedOff()
    {
        int found = 0;
        foreach (int t in Tracks())
        {
            var r = new TrackRenderer(t, GameData.Dir);
            int bi = -1; for (int i = 0; i < r.Trk.Blocks.Count && bi < 0; i++) if (r.Trk.Blocks[i].Xb(9)?.Count > 4) bi = i;
            if (bi < 0) continue;
            var b = r.Trk.Blocks[bi];
            var s = r.Slices[b.FirstSlice + 3];
            var pos = new Vector3(s.X, s.Y, s.Z) / 65536f + new Vector3(0, 1.5f, 0);
            var cam = new Camera { Position = pos };
            var fwd = new Vector3(s.Forward[0], s.Forward[1], s.Forward[2]); fwd = Vector3.Normalize(fwd);
            cam.LookAlong(fwd, Vector3.UnitY);
            int On(bool lanes)
            {
                r.Render(new FrameBuffer(), cam, new RenderOptions { Sky = false, LaneMarks = lanes });
                return r.LastEmitted;
            }
            int off = On(false), on = On(true);
            log.WriteLine($"TR{t:00} block {bi}: polygons {off} → {on}");
            Assert.True(on > off, $"TR{t:00}: lane marks missing at a block with XBID 9");
            // the near LOD threshold (ViewRange.LodNear, XZ distance of the block reference to the camera): 0 m = no block is near, so no lane marks
            var none = ViewRange.Main with { LodNear = 0 };
            r.Render(new FrameBuffer(), cam, new RenderOptions { Sky = false, LaneMarks = false }, range: none); int farOff = r.LastEmitted;
            r.Render(new FrameBuffer(), cam, new RenderOptions { Sky = false, LaneMarks = true }, range: none);
            Assert.Equal(farOff, r.LastEmitted);
            found++;
        }
        Assert.True(found > 0, "no track with XBID 9 data");
    }
}
