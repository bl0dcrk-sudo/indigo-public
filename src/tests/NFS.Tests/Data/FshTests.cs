using NFS.Data;

namespace NFS.Tests.Data;

public class FshTests
{
    /// <summary>Counts of main/attached block codes over all FSH/QFS files (expected values). [ref:3486]</summary>
    [GameFact]
    public void ScanMatchesPythonStatistics()
    {
        var main = new Dictionary<int, int>();
        var att = new Dictionary<int, int>();
        int files = 0, decoded = 0;
        foreach (var rel in GameData.Files(null, ".FSH", ".QFS"))
        {
            var raw = GameData.ReadRaw(rel);
            var data = EaCompression.UnpackIfCompressed(raw);
            if (!Bin.HasTag(data, 0, "SHPI")) continue;
            var f = Fsh.Parse(data);
            files++;
            Assert.Equal("GIMX", f.DirectoryId);
            Assert.Equal((uint)data.Length, f.DeclaredSize);
            var gpal = f.GlobalPalette();
            foreach (var e in f.Entries)
            {
                main[e.Main.Code] = main.GetValueOrDefault(e.Main.Code) + 1;
                foreach (var b in e.Blocks.Skip(1)) att[b.Code] = att.GetValueOrDefault(b.Code) + 1;
                var img = f.Decode(e, gpal);
                if (img != null) { Assert.Equal(img.Width * img.Height * 4, img.Rgba.Length); decoded++; }
            }
        }
        Assert.Equal(310, files);
        Assert.Equal(new Dictionary<int, int> { [0x24] = 17, [0x2A] = 96, [0x78] = 5706, [0x7A] = 8, [0x7B] = 9251, [0x7D] = 223, [0x7E] = 1, [0x7F] = 17 },
            main.OrderBy(k => k.Key).ToDictionary());
        Assert.Equal(new Dictionary<int, int> { [0x24] = 600, [0x29] = 10, [0x2A] = 482, [0x2D] = 21, [0x6F] = 11, [0x7C] = 31 },
            att.OrderBy(k => k.Key).ToDictionary());
        Assert.Equal(5706 + 8 + 9251 + 223 + 1 + 17, decoded);
    }

    /// <summary>RGBA decode byte-identical to the reference (decode + global palette), one sample per pixel/palette type. [ref:3487]</summary>
    [GameTheory]
    [InlineData("GAMEDATA/TRACKS/PC/TR000.QFS", "0000", 128, 256, "5b51007c7894c4fafd0542150011e48d097e7dca5d729200412966e2d23645bd")]
    [InlineData("FEDATA/PC/ART/CREDITS.QFS", "knwd", 196, 38, "57d12b47324c79702160c0b1285d24b11445fa900ef5c9e75150de0074a4fcf2")]
    [InlineData("FEDATA/PC/ART/MOUSE.FSH", "ptr0", 27, 28, "2890539af0de7c5731432a61af9785efed040fa63614c555c8f32b190f601385")]
    [InlineData("GAMEDATA/RENDER/PC/FXTEXT.FSH", "f001", 8, 16, "562131a55b2e572fad0394e284661a803ce0e52d3558dd68e9efe4992bbf74ee")]
    [InlineData("FEDATA/PC/ART/TRACK/LASTREC.QFS", "dot ", 6, 6, "ff60c7f050478fb4b581aa5b2af855c3c2056080fb8fd479b1a43e5cb13e00b4")]
    [InlineData("FEDATA/PC/ART/SLIDES/SLDMUS8.QFS", "back", 640, 480, "6a0919e1888490271f8c89bf942cc0294f755a7cd1629fe4befbf49229d968f9")]
    [InlineData("FEDATA/PC/ART/CREDITS.QFS", "Bac1", 640, 480, "0a8b18aaa09d29a5efee0b89205076a710d00034f950d26b6eeee3d34a94e3e6")]
    [InlineData("FEDATA/PC/ART/HISFER.QFS", "back", 640, 480, "58d4993742a68459a0ec3191325c5a8f7e718cdde5fae4f36d323029eed73f2a")]
    public void DecodeMatchesPython(string rel, string name, int w, int h, string sha)
    {
        var f = Fsh.Parse(GameData.ReadRaw(rel));
        var img = f.Decode(f.Find(name)!, f.GlobalPalette())!;
        Assert.Equal((w, h), (img.Width, img.Height));
        Assert.Equal(sha, GameData.Sha256(img.Rgba));
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        PngWriter.WriteRgba(Path.Combine(GameData.OutDir(), $"cs_{Path.GetFileNameWithoutExtension(rel)}_{safe}.png"), w, h, img.Rgba);
    }

    [GameFact]
    public void TrackPaletteAndPositions()
    {
        var f = Fsh.Parse(GameData.ReadRaw("GAMEDATA/TRACKS/PC/TR000.QFS"));
        var pal = f.Find("!pal");
        Assert.NotNull(pal);
        Assert.Equal(0x24, pal!.Main.Code);
        Assert.NotNull(f.GlobalPalette());
        Assert.Equal(0x7B, f.Find("0000")!.Main.Code);
        Assert.Equal(128 * 256, f.Indices(f.Find("0000")!).Length);
    }
}

public class VivTests
{
    [GameTheory]
    [InlineData("GAMEDATA/SIM/CARDATA/CARDATA.VIV", 127, 2224)]
    [InlineData("GAMEDATA/DASHHUD/PC/HUDDATA.VIV", 38, 692)]
    [InlineData("FEDATA/PC/AUDIO/ENGLISH/ENGLISH.VIV", 169, 2044)]
    public void ArchivesListLikePython(string rel, int count, int dirEnd)
    {
        var v = Viv.Load(rel, GameData.Dir);
        Assert.True(v.IsBigF);
        Assert.Equal(count, v.Entries.Count);
        Assert.Equal((uint)count, v.DeclaredCount);
        Assert.Equal(dirEnd, v.DirectoryEnd);
        Assert.Equal((uint)v.Data.Length, v.DeclaredSize);
        Assert.All(v.Entries, e => Assert.InRange(e.Offset + e.Size, dirEnd, v.Data.Length));
        Assert.NotNull(v.Find(v.Entries[0].Name.ToUpperInvariant()));
    }

    [Fact]
    public void C0FbSynthetic()
    {
        // C0 FB, u16 BE dir length (excl. first 4 B), count u16, entry {u24 off, u24 size, "a\0"}, data
        byte[] d = [0xC0, 0xFB, 0x00, 0x0A, 0x00, 0x01, 0, 0, 14, 0, 0, 2, (byte)'a', 0, (byte)'h', (byte)'i'];
        var v = Viv.Parse(d);
        Assert.False(v.IsBigF);
        Assert.Equal("hi", System.Text.Encoding.ASCII.GetString(v.Get("A")));
    }
}
