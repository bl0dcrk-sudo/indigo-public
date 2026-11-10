using NFS.Data;
using NFS.Data.Track;
using NFS.Game.Race;
using NFS.Tests.Data;
using Xunit;

namespace NFS.Tests.Game;

public class GridPositionTests
{
    private static TrackSlice Slice(byte lanes, byte left, byte right) =>
        new(0, 0, 0, [0, 126, 0], [0, 0, 126], [126, 0, 0], 0, 0, 0, 0, 0, 0, lanes, left, right, 0, 0);

    [Fact]
    public void OneWayTable()
    {
        var sl = new[] { Slice(0x11, 8, 8), Slice(0x11, 8, 8) };
        Assert.Equal([true, false, false, false, false, false, true, false, true], // records + 0x1C·track: 0, 6, 8 [ref:3136]
            Enumerable.Range(0, 9).Select(t => RaceLoader.IsOneWay(t, sl)).ToArray());
        Assert.False(RaceLoader.IsOneWay(-1, sl));
        // track >= 9: one-way when the high nibble of slice 1 +0x1F is 0
        Assert.True(RaceLoader.IsOneWay(9, [Slice(0x11, 8, 8), Slice(0x03, 8, 8)]));
        Assert.False(RaceLoader.IsOneWay(9, [Slice(0x11, 8, 8), Slice(0x13, 8, 8)]));
    }

    [Fact]
    public void Formula_TwoWayAndOneWay()
    {
        var sl = Enumerable.Repeat(Slice(0x04, 6, 10), 20).ToArray();
        // two-way (track 1): even −b20·0x8000/2, odd +b21·0x8000/2
        Assert.Equal(-6 * 0x4000, RaceLoader.GridPosition(0, 8, false, sl, 1).Local.X);
        Assert.Equal(10 * 0x4000, RaceLoader.GridPosition(1, 8, false, sl, 1).Local.X);
        // one-way (track 0): w = b21·0x8000, base = (4/2)·w, x = base ∓ w/2
        int w = 10 * 0x8000;
        Assert.Equal(2 * w - w / 2, RaceLoader.GridPosition(0, 8, false, sl, 0).Local.X);
        Assert.Equal(2 * w + w / 2, RaceLoader.GridPosition(1, 8, false, sl, 0).Local.X);
        Assert.Equal((2, 0x8000), (RaceLoader.GridPosition(0, 8, false, sl, 0).Slice, RaceLoader.GridPosition(0, 8, false, sl, 0).Local.Y));
    }

    [GameTheory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Grid_OnRoad(int track)
    {
        var col = Col.Load($"GAMEDATA/TRACKS/SE/TR{track:00}.COL", GameData.Dir);
        var sl = col.Slices;
        foreach (bool back in new[] { false, true })
            for (int slot = 0; slot < 8; slot++)
            {
                var (s, p) = RaceLoader.GridPosition(slot, 8, back, sl, track);
                var x = sl[s];
                // road edges (+0x1A/+0x1C, ×256 → 16.16) bound every grid position
                Assert.InRange(p.X, -x.LeftEdge * 256, x.RightEdge * 256);
                if (!RaceLoader.IsOneWay(track, sl)) // two-way: within the half widths (6, 8: left width 0 → x = 0)
                    Assert.InRange(p.X, -x.HalfWidthLeft * 0x8000, x.HalfWidthRight * 0x8000);
            }
    }
}
