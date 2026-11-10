using NFS.Data;
using NFS.Data.Track;

namespace NFS.Tests.Data;

public class TrackTests
{
    public static TheoryData<int> Tracks() => new(0, 2, 3, 4, 5, 6, 7, 8);

    [GameTheory]
    [MemberData(nameof(Tracks))]
    public void TrkColDisParseAndAgree(int t)
    {
        var trk = Trk.Load($"GAMEDATA/TRACKS/SE/TR{t:00}.TRK", GameData.Dir);
        var col = Col.Load($"GAMEDATA/TRACKS/SE/TR{t:00}.COL", GameData.Dir);
        Assert.Equal(0x16u, trk.Version);
        Assert.Equal(0xBu, col.Version);
        Assert.Equal(trk.Blocks.Count * 3, trk.BlockRefs.Length);
        for (int i = 0; i < trk.Blocks.Count; i++)
        {
            var b = trk.Blocks[i];
            Assert.Equal((uint)i, b.Serial);
            Assert.Equal(i * Trk.SlicesPerBlock, b.FirstSlice);
            Assert.Equal(b.Size, b.Size2);
            Assert.True(trk.SuperOfBlock[i] < trk.SuperBlockOffsets.Length);
            // geometry ends before the extrablock table
            int geomEnd = 0x58 + 6 * b.NumVertices + 8 * b.PolyCounts.Sum(c => c);
            Assert.True(geomEnd <= 0x40 + b.XbTableOffset, $"block {i}: geometry overlaps xb table");
            Assert.All(b.PolyGroups.SelectMany(g => g), p => Assert.True(Math.Max(Math.Max(p.V0, p.V1), Math.Max(p.V2, p.V3)) < b.NumVertices));
            Assert.Equal(b.PolyCounts[4], b.Xb(5)!.PolyAttrs().Length);
            Assert.InRange(b.Xb(4)!.Neighbors().Length, 1, 100);
            foreach (var x in b.ExtraBlocks)
            {
                _ = x.Id switch
                {
                    7 or 18 or 19 => x.ObjectRefs().Length,
                    8 => x.Structures().Length,
                    11 => x.Points().Length,
                    13 => x.VRoad().Length,
                    9 => x.LaneMarks().Length,
                    _ => 0,
                };
            }
        }
        var slices = col.Slices;
        Assert.InRange(slices.Length, trk.NumSlices - 7, trk.NumSlices);
        for (int i = 0; i < slices.Length; i++) Assert.Equal(i / 8, slices[i].BlockNumber);
        // +0x20/+0x21 = road half widths (0.5 m units): grid slices 5·slot+2 must have road (tracks 0/6/8 have 0 on one side at the start – a one-way road case?) [ref:3122]
        for (int slot = 0; slot < 8; slot++)
            Assert.True(slices[5 * slot + 2].HalfWidthLeft + slices[5 * slot + 2].HalfWidthRight > 0, $"grid slot {slot}");
        // XBID 6 = one entry per slice of the block (the last block may be shorter)
        foreach (var b in trk.Blocks)
            Assert.Equal(Math.Min(8, slices.Length - b.FirstSlice), b.Xb(6)!.SliceRoads().Length);
        Assert.NotEmpty(col.Textures);
        Assert.All(col.Textures, x => Assert.True(x.Frames >= 1));
        _ = col.ObjectRefs; _ = col.Structures;

        var disPath = $"GAMEDATA/TRACKS/SE/TR{t:00}.DIS";
        var dis = GameFiles.Exists(disPath, GameData.Dir)
            ? Dis.Parse(GameData.ReadRaw(disPath), slices.Length) : Dis.Default(slices.Length);
        Assert.Equal(slices.Length * 8, dis.Raw.Length);
        Assert.InRange(dis.VisibleCount(0, true), 1, 100);
    }

    [GameFact]
    public void Tr00KnownValues()
    {
        var trk = Trk.Load("GAMEDATA/TRACKS/SE/TR00.TRK", GameData.Dir);
        Assert.Equal(21, trk.SuperBlockOffsets.Length);
        Assert.Equal(161, trk.Blocks.Count);
        var b0 = trk.Blocks[0];
        Assert.Equal((12, 24, 48, 96), (b0.NumStickToNext, b0.NumLoV, b0.NumMedV, b0.NumHiV));
        Assert.Equal([22, 0, 44, 0, 88, 0], b0.PolyCounts.Select(x => (int)x));
        Assert.Equal([5, 4, 8, 7, 18, 6, 13, 9], b0.ExtraBlocks.Select(x => (int)x.Id));
        Assert.Equal([196608, 65536, 5505024], trk.BlockRefs[..3]);
        var col = Col.Load("GAMEDATA/TRACKS/SE/TR00.COL", GameData.Dir);
        Assert.Equal([2, 8, 7, 15], col.ExtraBlocks.Select(x => (int)x.Id));
        Assert.Equal(321, col.Textures.Length);
        Assert.Equal(1287, col.Slices.Length);
    }

    [GameFact]
    public void Tr04NearSceneryIncludesTheFullResolutionWall()
    {
        // TR04 block 23: six u16 counts at +0x4C..+0x56 store the start of group 5 [ref:3123]
        // emitted beside group 4 for the near LOD. [ref:3124]
        var block = Trk.Load("GAMEDATA/TRACKS/SE/TR04.TRK", GameData.Dir).Blocks[23];
        Assert.Equal(new ushort[] { 22, 4, 43, 8, 85, 16 }, block.PolyCounts);
        var wall = block.PolyGroups[5];
        Assert.Equal(16, wall.Length);
        Assert.Equal(8, wall.Count(p => p.Texture == 66)); // top, one quad per slice
        Assert.Equal(8, wall.Count(p => p.Texture == 67)); // side
    }

    [GameFact]
    public void AllCanFilesParse()
    {
        int n = 0;
        foreach (var rel in GameData.Files("GAMEDATA/TRACKS", ".CAN"))
        {
            var raw = GameData.ReadRaw(rel);
            var c = Can.Parse(raw);
            Assert.Equal(raw.Length, 8 + 20 * c.Keys.Count);
            Assert.InRange(c.TicksPerKey, 1, 400);
            n++;
        }
        Assert.Equal(64, n);
        var c0 = Can.Load("GAMEDATA/TRACKS/DATA/PC/TR0000.CAN", GameData.Dir);
        Assert.Equal(51, c0.Keys.Count);
        Assert.Equal(6, c0.TicksPerKeyRaw);
        Assert.Equal(0x050B, c0.Keys[0].X);
    }

    [GameFact]
    public void HrzParse()
    {
        var h = Hrz.Load("GAMEDATA/TRACKS/PC/TR00.HRZ", GameData.Dir);
        Assert.Equal((0, 1, 1500, 0, 1000, -700, 1043, 60), (h.BlackHorizon, h.MirrorFlag, h.Radius, h.AngleDegrees, h.FlatDistance, h.BaseHeight, h.Height, h.PixmapBasePercent));
        Assert.Equal([21, 18, 45], h.SkyRgb);
        Assert.Equal([151, 154, 190], h.HorizonRgb);
        Assert.Equal([0, 250, 500, 900], h.DepthCueDistances);
        Assert.Equal([0, 0, 10, 15], h.DepthCueLevels);
        Assert.Equal(0, h.ColorSystem);
        Assert.Equal([40, 40, 40], h.FogColor);
        int n = 0;
        // 3TRxx.HRZ = 3Dfx variant with an extra "cloud dome" header — not read by the software renderer
        foreach (var rel in GameData.Files("GAMEDATA/TRACKS", ".HRZ").Where(r => !Path.GetFileName(r).StartsWith('3')))
        {
            var x = Hrz.Parse(GameData.ReadRaw(rel));
            Assert.InRange(x.Radius, 1, 100000);
            Assert.All(x.DepthCueLevels, v => Assert.InRange(v, 0, 15));
            n++;
        }
        Assert.Equal(10, n);
    }
}
