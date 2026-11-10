using NFS.Data.Track;
using NFS.Render;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Render;

/// <summary>TrackShade decoder: u16 shade = 4 × 3-bit corner intensity in EmitPolys order. [ref:3309]</summary>
public class TrackShadeTests
{
    [Fact]
    public void TrackShade_Decode_KnownValues()
    {
        TrackShade.Decode(0xFFFF, out int a, out int b, out int c, out int d);
        Assert.Equal((7, 7, 7, 7), (a, b, c, d));
        TrackShade.Decode(0x000F, out a, out b, out c, out d);
        Assert.Equal((0, 0, 0, 0), (a, b, c, d));
        // corner0 = bits 4-6, corner1 = 7-9, corner2 = 10-12, corner3 = 13-15
        int s = (1 << 4) | (2 << 7) | (3 << 10) | (4 << 13) | 0xF;
        TrackShade.Decode(s, out a, out b, out c, out d);
        Assert.Equal((1, 2, 3, 4), (a, b, c, d));
        for (int k = 0; k < 4; k++) Assert.Equal(k + 1, TrackShade.Corner(s, k));
    }

    [Fact]
    public void TrackShade_Range_AllValues()
    {
        for (int s = 0; s <= 0xFFFF; s++)
        {
            TrackShade.Factors256(s, out int a, out int b, out int c, out int d);
            foreach (int f in new[] { a, b, c, d })
                Assert.InRange(f, TrackShade.DefaultLmin256, TrackShade.One);
            for (int k = 0; k < 4; k++) Assert.InRange(TrackShade.Corner(s, k), 0, 7);
        }
    }

    [Fact]
    public void TrackShade_Factor_Mapping()
    {
        Assert.Equal(TrackShade.DefaultLmin256, TrackShade.Factor256(0));
        Assert.Equal(256, TrackShade.Factor256(7));
        Assert.Equal(256, TrackShade.Factor256(99));
        Assert.Equal(TrackShade.DefaultLmin256, TrackShade.Factor256(-1));
        Assert.Equal(0.35f, TrackShade.Factor(0), 5);
        Assert.Equal(1f, TrackShade.Factor(7), 5);
        Assert.Equal(0.35f + 0.65f * 3 / 7, TrackShade.Factor(3), 5);
        for (int c = 1; c <= 7; c++) Assert.True(TrackShade.Factor256(c) > TrackShade.Factor256(c - 1));
        for (int c = 0; c <= 7; c++) Assert.Equal(TrackShade.Factor(c) * 256, TrackShade.Factor256(c), 1.0);
    }

    [Fact]
    public void TrackShade_IsNeutral()
    {
        Assert.True(TrackShade.IsNeutral(0xFFFF));
        Assert.True(TrackShade.IsNeutral(0xFFF0));
        Assert.False(TrackShade.IsNeutral(0x000F));
        Assert.False(TrackShade.IsNeutral(0xFFEF));
        Assert.False(TrackShade.IsNeutral(0x7FFF));
    }

    [GameFact]
    public void TrackShade_TR06_NonNeutralShareAndVertexConsistency()
    {
        var trk = Trk.Load("GAMEDATA/TRACKS/SE/TR06.TRK", GameData.Dir);
        int total = 0, nonNeutral = 0, shared = 0, agree = 0;
        foreach (var b in trk.Blocks)
        {
            if (b.PolyGroups.Length < 6) continue;
            for (int g = 4; g < 6; g++)
                foreach (var p in b.PolyGroups[g])
                {
                    int shade = (ushort)p.Flags;
                    total++;
                    if (!TrackShade.IsNeutral(shade)) nonNeutral++;
                }
            var map = new Dictionary<int, (int val, bool ok)>();
            var cnt = new Dictionary<int, int>();
            for (int g = 4; g < 6; g++)
                foreach (var p in b.PolyGroups[g])
                {
                    int shade = (ushort)p.Flags;
                    Span<int> idx = [p.V1, p.V0, p.V3, p.V2];
                    for (int k = 0; k < 4; k++)
                    {
                        int v = idx[k], val = TrackShade.Corner(shade, k);
                        if (!map.TryGetValue(v, out var e)) { map[v] = (val, true); cnt[v] = 1; }
                        else
                        {
                            cnt[v]++;
                            map[v] = (e.val, e.ok && e.val == val);
                        }
                    }
                }
            foreach (var (v, e) in map)
                if (cnt[v] > 1) { shared++; if (e.ok) agree++; }
        }
        double share = nonNeutral / (double)total, cons = agree / (double)shared;
        Console.WriteLine($"TR06 polygons(groups 4,5)={total} nonNeutral={nonNeutral} ({share:P1}) sharedVerts={shared} consistent={agree} ({cons:P1})");
        Assert.InRange(share, 0.38, 0.58);
        Assert.True(cons >= 0.95, $"consistency {cons:P1}");
    }
}
