using NFS.Data;
using NFS.Render;
using NFS.Tests.Data;

namespace NFS.Tests.Render;

/// <summary>Car paint (HSV): [ref:3278]
/// Paint table. Expected values were computed by hand from the algorithm. [ref:3279]</summary>
public class CarPaintTests
{
    [Theory]
    [InlineData(0, 0, 210, 210, 210, 210)]   // s < 1: grey = v
    [InlineData(77, 200, 0, 0, 0, 0)]        // v < 1: black
    [InlineData(0, 256, 256, 256, 0, 0)]     // sector 0, F = 0: (v, t, p)
    [InlineData(128, 128, 200, 100, 200, 200)] // sector 3, F = 0: (p, q, v), p = 128*200>>8
    [InlineData(100, 200, 250, 54, 250, 122)]  // sector 2, F = 88/256: (p, v, t); q = 182, t = 122, p = 54
    [InlineData(254, 215, 249, 249, 39, 49)]   // table entry 2: sector 5, (v, p, q)
    public void HsvToRgbMatchesHandComputedValues(int h, int s, int v, int r, int g, int b) =>
        Assert.Equal((r, g, b), CarPaint.HsvToRgb(h, s, v));

    [Theory]
    [InlineData(249, 39, 49, 254, 215, 249)] // inverse of the table entry 2 colour; hue -10/210 sixths wraps by +1.0
    [InlineData(100, 100, 100, 0, 0, 100)]   // delta = 0 -> h = s = 0
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0, 200, 0, 85, 256, 200)]    // g = max: (2 + 0)/6 * 256 rounded = 85
    public void RgbToHsvMatchesHandComputedValues(int r, int g, int b, int h, int s, int v) =>
        Assert.Equal((h, s, v), CarPaint.RgbToHsv(r, g, b));

    [Fact]
    public void RecolourEntryShadowHighlightAndHighlightBranchesFollowTheAsm()
    {
        var grey = (210, 210, 210);
        // i = 47 <= threshold 80: 210*47/80 = 123; the real DSH05 key (0,175,0) has r = 0, so no highlight
        Assert.Equal(((byte)123, (byte)123, (byte)123), CarPaint.RecolourEntry((0, 175, 0), 80, grey));
        // i = 102 > 80: 210 + (255-210)*22/47 = 231
        Assert.Equal(((byte)231, (byte)231, (byte)231), CarPaint.RecolourEntry((0, 230, 0), 80, grey));
        // r > 0 and b > 0: i = 72 -> 189, r += 50*3/2 (capped 255), g += 72/2, b += 50*3/2 (capped 255)
        Assert.Equal(((byte)255, (byte)225, (byte)255), CarPaint.RecolourEntry((50, 200, 50), 80, grey));
        // table entry 2 (249,39,49), threshold 96, key (30,200,30): i = 72 -> (186,29,36), + (45, 36, 45)
        Assert.Equal(((byte)231, (byte)65, (byte)81), CarPaint.RecolourEntry((30, 200, 30), 96, (249, 39, 49)));
    }

    [Fact]
    public void RecolourEntryKeepsComponentsAsBytesLikeTheOriginal()
    {
        // the base 256 is truncated to the byte 0 in the "i > threshold" branch (cast to byte), 255 stays [ref:3495]
        Assert.Equal(((byte)0, (byte)255, (byte)0), CarPaint.RecolourEntry((0, 228, 0), 80, (256, 255, 256)));
    }

    [Fact]
    public void RecolourPaletteChangesOnlyKeyEntriesAndKeepsAlpha()
    {
        var pal = new byte[256 * 4];
        pal.AsSpan().Fill(0x40); pal[3] = 0xFF;
        pal[4] = 0; pal[5] = 175; pal[6] = 0; pal[7] = 0xAB;   // key
        pal[8] = 86; pal[9] = 175; pal[10] = 0;                  // r >= 0x56: not a key
        pal[12] = 0; pal[13] = 127; pal[14] = 0;                 // g <= 0x7F: not a key
        pal[16] = 0; pal[17] = 175; pal[18] = 86;                // b >= 0x56: not a key
        CarPaint.RecolourPalette(pal, [0, 0, 210, 80]);
        Assert.Equal(new byte[] { 123, 123, 123, 0xAB }, pal[4..8]);
        Assert.Equal(new byte[] { 86, 175, 0 }, pal[8..11]);
        Assert.Equal(new byte[] { 0, 127, 0 }, pal[12..15]);
        Assert.Equal(new byte[] { 0, 175, 86 }, pal[16..19]);
        Assert.Equal(new byte[] { 0x40, 0x40, 0x40, 0xFF }, pal[0..4]);
    }

    [GameFact]
    public void TableAtExeHasHandCheckedEntriesAndValidRanges()
    {
        Assert.Equal([0, 0, 210, 80], CarPaint.Hsv(0));
        Assert.Equal([0, 0, 60, 110], CarPaint.Hsv(1));
        Assert.Equal([254, 215, 249, 96], CarPaint.Hsv(2));
        Assert.Equal((249, 39, 49), CarPaint.HsvToRgb(254, 215, 249));
        for (int i = 0; i < CarPaint.TableEntries; i++)
        {
            var e = CarPaint.Hsv(i);
            Assert.InRange(e[0], 0, 255);   // sector 6 would leave the original's outputs undefined
            Assert.InRange(e[1], 0, 256);
            Assert.InRange(e[2], 0, 256);
            Assert.InRange(e[3], 0, 255);   // entry 0x58 has threshold 0, entry 20 has 159 (> 0x7F: only the shadow branch)
            var rgb = CarPaint.HsvToRgb(e[0], e[1], e[2]);
            for (int g = 0x80; g <= 0xFF; g++) CarPaint.RecolourEntry((0, g, 0), e[3], rgb); // no division by zero (entry 0x58, g = 0x80)
        }
    }

    [GameFact]
    public void CarModelPaintsKeyPaletteEntriesFromTheSelectedColour()
    {
        var fsh = Fsh.Load("GAMEDATA/CARMODEL/PC/F355.QFS", GameData.Dir);
        var pal = fsh.GlobalPalette()!;
        var expected = (byte[])pal.Clone();
        CarPaint.RecolourPalette(expected, CarPaint.Hsv(2));
        int keys = Enumerable.Range(0, 256).Count(i => pal[i * 4] < 0x56 && pal[i * 4 + 1] > 0x7F && pal[i * 4 + 2] < 0x56);
        Assert.True(keys > 0);
        Assert.NotEqual(pal, expected);
        var painted = new CarModel("F355", GameData.Dir, carType: 0, paintIndex: 2);
        var other = new CarModel("F355", GameData.Dir, carType: 0, paintIndex: 0);
        var body = painted.Geo.Parts[0].Polygons.Select(p => p.Texture).Distinct().ToArray();
        Assert.Contains(body, t => !painted.TextureFor(t)!.Rgb.SequenceEqual(other.TextureFor(t)!.Rgb));
    }
}
