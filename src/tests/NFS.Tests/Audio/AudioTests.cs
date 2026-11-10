using System.Buffers.Binary;
using NFS.Audio;
using NFS.Data;
using NFS.Tests.Data;

namespace NFS.Tests.Audio;

public class EaXaDecoderTests
{
    [Fact]
    public void Mono_ZeroBlock_IsSilence()
    {
        var o = EaXaDecoder.DecodeMono(new byte[15], 28);
        Assert.All(o, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Mono_KnownValues_Coef0()
    {
        // header 0x00: k=0 (C1=C2=0), shift 8; nibble 7 → (7<<28)>>8 = 0x7000000; +0x80 >> 8 = 0x70000 → clip 32767
        // header 0x0C: shift 20 → (7<<28)>>20 = 0x700 → (0x700+0x80)>>8 = 7
        var blk = new byte[15];
        blk[0] = 0x0C;
        blk[1] = 0x7F;   // nibbles 7, -1
        var o = EaXaDecoder.DecodeMono(blk, 2);
        Assert.Equal(7, o[0]);
        Assert.Equal(-1, o[1]);   // (-1<<28)>>20 = -256 → (-256+128)>>8 = -1
    }

    [Fact]
    public void Mono_NegativeNibble_RoundsAsExe()
    {
        var blk = new byte[15];
        blk[0] = 0x0C;
        blk[1] = 0xF0;   // -1 then 0
        var o = EaXaDecoder.DecodeMono(blk, 2);
        Assert.Equal(-1, o[0]);  // (-0x100 + 0x80) >> 8 = -1 (arithmetic)
        Assert.Equal(0, o[1]);
    }

    [Fact]
    public void Mono_Prediction_UsesC1C2()
    {
        // k=1: C1=240, C2=0, shift 20, nibble 0: v = (240*h1 + 0x80) >> 8
        var blk = new byte[15];
        blk[0] = 0x1C;
        short[] hist = [0, 1000];
        var o = new short[1];
        EaXaDecoder.DecodeMono(blk, 1, hist, o);
        Assert.Equal((240 * 1000 + 0x80) >> 8, o[0]);
    }

    [Fact]
    public void Stereo_SizeHelpers()
    {
        Assert.Equal(53 * 30, EaXaDecoder.StereoBytes(1484));
        Assert.Equal(15, EaXaDecoder.MonoBytes(28));
        Assert.Equal(30, EaXaDecoder.MonoBytes(29));
    }

    [Fact]
    public void Stereo_ClipsToInt16()
    {
        var blk = new byte[30];
        blk[0] = 0x11; blk[1] = 0x00;  // k=1 (C1=240) both, shift 8 both
        blk[2] = 0x78;                  // L=7 → 28672 + 30000, R=-8 → -32768 - 30000
        var o = EaXaDecoder.DecodeStereo(blk, 1, new short[] { 0, 32000, 0, -32000 });
        Assert.Equal(32767, o[0]);
        Assert.Equal(-32768, o[1]);
    }
}

public class PtHeaderTests
{
    [Fact]
    public void ReadBe_SignExtendsShortValues()
    {
        Assert.Equal(-1, PtHeader.ReadBe([0xFF]));
        Assert.Equal(-2, PtHeader.ReadBe([0xFF, 0xFE]));
        Assert.Equal(0x7FFFFF, PtHeader.ReadBe([0x7F, 0xFF, 0xFF]));
        Assert.Equal(0xFFFFFFFFL, PtHeader.ReadBe([0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.Equal(22050, PtHeader.ReadBe([0x56, 0x22]));
    }

    [Fact]
    public void Parse_SyntheticTags()
    {
        byte[] d = [(byte)'P', (byte)'T', 0, 0, 0xFD, 0x82, 1, 2, 0x83, 1, 7, 0x84, 2, 0x56, 0x22, 0x85, 0xFF, 0, 0, 0, 2, 0x12, 0x34,
                    0x86, 1, 0xFF, 0xFF];
        var h = PtHeader.Parse(d, 0);
        Assert.Equal(2, h.Channels);
        Assert.Equal(7, h.Codec);
        Assert.Equal(22050, h.SampleRate);
        Assert.Equal(0x1234, h.Samples);
        Assert.Equal(-1, h.LoopStart);
        Assert.Equal(16, h.Bits);   // default
        Assert.Equal(d.Length, h.End);
    }

    [Fact]
    public void Parse_EightByteHeaderFlag()
    {
        byte[] d = [(byte)'P', (byte)'T', 0, 2, 9, 9, 9, 9, 0x82, 1, 2, 0xFF];
        Assert.Equal(2, PtHeader.Parse(d, 0).Channels);
    }

    [Fact]
    public void Parse_RejectsNonPt() => Assert.Throws<InvalidDataException>(() => PtHeader.Parse(new byte[8], 0));
}

public class AsfStreamTests
{
    const string TempAsf = "GAMEDATA/AUDIO/PC/TEMP.ASF";

    [GameFact]
    public void TempAsf_Header()
    {
        var s = AsfStream.Parse(GameData.ReadRaw(TempAsf));
        Assert.Equal(2, s.Channels);
        Assert.Equal(7, s.Header.Codec);
        Assert.Equal(22050, s.SampleRate);
        Assert.Equal(3697217, s.Header.Samples);
        Assert.Equal(288501, s.Header.LoopStart);
        Assert.Equal(3357946, s.Header.LoopEnd);
        Assert.True(s.HasEnd);
        Assert.Equal(s.Header.Samples, s.TotalFrames);
        if (s.DeclaredBlocks >= 0) Assert.Equal(s.DeclaredBlocks, s.Blocks.Count);
    }

    [GameFact]
    public void TempAsf_LoopPointsAtLoopStartFrame()
    {
        var s = AsfStream.Parse(GameData.ReadRaw(TempAsf));
        Assert.True(s.LoopTarget > 0);
        int bi = s.BlockIndexAt(s.LoopTarget);
        Assert.True(bi >= 0, "SCLl target is an SCDl block");
        Assert.Equal(s.Header.LoopStart, s.Blocks.Take(bi).Sum(b => b.Frames));
        // SCLl stands at frame = loop end
        Assert.Equal(s.Header.LoopEnd, s.Blocks.Where(b => b.Offset < s.LoopChunkOffset).Sum(b => b.Frames));
    }

    [GameFact]
    public void TempAsf_First10Blocks_MatchPython()
    {
        // Reference: decoded with the reference decoder (TEMP.ASF, 'new_old', 10) [ref:3466]
        var s = AsfStream.Parse(GameData.ReadRaw(TempAsf));
        var pcm = s.Decode(10);
        Assert.Equal(29680, pcm.Length);
        var bytes = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * i), pcm[i]);
        Assert.Equal("af0542bb65461ab27801912683a8ebf8317f592f79d3e37996024b2323ef81d2", GameData.Sha256(bytes));
    }

    [GameFact]
    public void TempAsf_BlockHistory_ContinuesPreviousBlock()
    {
        // doc 5.3: stored history ≈ final decoder state of the previous block (±few LSB)
        var d = GameData.ReadRaw(TempAsf);
        var s = AsfStream.Parse(d);
        var prev = s.DecodeBlock(3);
        var b = s.Blocks[4];
        short lNew = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(b.Offset + 12));
        short rNew = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(b.Offset + 16));
        Assert.InRange(lNew - prev[^2], -16, 16);
        Assert.InRange(rNew - prev[^1], -16, 16);
    }

    [GameFact]
    public void AllAsfAndMus_Parse()
    {
        int n = 0;
        foreach (var f in GameData.Files("GAMEDATA/AUDIO/PC", ".ASF", ".MUS"))
        {
            var secs = AsfStream.ParseAll(GameData.ReadRaw(f));
            Assert.NotEmpty(secs);
            Assert.All(secs, s => Assert.Equal(PtHeader.CodecEaXa, s.Header.Codec));
            n++;
        }
        Assert.True(n > 10);
    }

    [GameFact]
    public void TempMus_HasSectionsMatchingMap()
    {
        var secs = AsfStream.ParseAll(GameData.ReadRaw("GAMEDATA/AUDIO/PC/TEMP.MUS"));
        Assert.True(secs.Count > 1);
        Assert.All(secs, s => Assert.True(s.HasEnd));
        Assert.All(secs, s => Assert.Equal(0, s.Start & 3));
    }
}

public class BnkBankTests
{
    [GameFact]
    public void CntEng_MatchesPython()
    {
        // Reference: Python reference decoder on GAMEDATA/AUDIO/SFX/PC/CNTENG.BNK
        var b = BnkBank.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/SFX/PC/CNTENG.BNK"));
        Assert.Equal(2, b.Version);
        Assert.Equal(4, b.Slots.Count);
        int[] samples = [6755, 7327, 9219, 10303], offs = [156, 3788, 7720, 12672];
        string[] hashes = ["0835a26522dd89d3", "f4297b9227ede94e", "903ab56cb60b53be", "50c21dbafea7df4b"];
        for (int i = 0; i < 4; i++)
        {
            var s = b.Slots[i]!;
            Assert.Equal(7, s.Codec);
            Assert.Equal(1, s.Channels);
            Assert.Equal(16000, s.SampleRate);
            Assert.Equal(samples[i], s.Samples);
            Assert.Equal(offs[i], s.DataOffset);
            var pcm = b.Decode(s);
            var bytes = new byte[pcm.Length * 2];
            for (int j = 0; j < pcm.Length; j++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * j), pcm[j]);
            Assert.StartsWith(hashes[i], GameData.Sha256(bytes));
        }
    }

    static int CheckBank(byte[] d, string name)
    {
        var b = BnkBank.Parse(d);
        Assert.Equal(2, b.Version);
        int n = 0;
        foreach (var s in b.Samples)
        {
            Assert.InRange(s.SampleRate, 8000, 48000);
            Assert.InRange(s.Channels, 1, 2);
            Assert.Contains(s.Codec, new[] { 0, 7 });
            int bytes = s.Codec == 7 ? (s.Channels == 1 ? EaXaDecoder.MonoBytes(s.Samples) : EaXaDecoder.StereoBytes(s.Samples))
                                     : s.Samples * s.Channels * s.Header.Bits / 8;
            Assert.True(s.DataOffset + bytes <= d.Length, $"{name} slot {s.Slot} data past end");
            Assert.Equal(s.Samples * s.Channels, b.Decode(s).Length);
            n++;
        }
        return n;
    }

    [GameFact]
    public void AllBnkFiles_ParseAndDecode()
    {
        int banks = 0, samples = 0;
        foreach (var f in GameData.Files(null, ".BNK"))
        {
            samples += CheckBank(GameData.ReadRaw(f), f);
            banks++;
        }
        Assert.True(banks >= 140, $"banks {banks}");
        Assert.True(samples > 100);
    }

    [GameFact]
    public void AllBnkSamples_MatchPythonHashes()
    {
        // Reference hashes generated by an independent Python decoder (EA-XA mono / PCM16) [ref:3467]
        var refPath = Path.Combine(GameData.ProjectRoot, "tests", "NFS.Tests", "Audio", "py_bnk_hashes.tsv");
        if (!File.Exists(refPath)) return;
        var expected = File.ReadLines(refPath).Where(l => l.Length > 0).Select(l => l.Split('\t'))
            .GroupBy(p => (p[0].ToUpperInvariant(), int.Parse(p[1]))).ToDictionary(g => g.Key, g => g.First()[2]);
        int checkedCount = 0;
        foreach (var f in GameData.Files(null, ".BNK"))
        {
            var b = BnkBank.Parse(GameData.ReadRaw(f));
            foreach (var s in b.Samples)
            {
                if (!expected.TryGetValue((f.ToUpperInvariant(), s.Slot), out var h)) continue;
                var pcm = b.Decode(s);
                var bytes = new byte[pcm.Length * 2];
                for (int j = 0; j < pcm.Length; j++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(2 * j), pcm[j]);
                Assert.True(GameData.Sha256(bytes).StartsWith(h), $"{f} slot {s.Slot}");
                checkedCount++;
            }
        }
        Assert.Equal(expected.Count, checkedCount);
    }

    [GameFact]
    public void EnglishViv_SpeechBanks()
    {
        var viv = Viv.Load("FEDATA/PC/AUDIO/ENGLISH/ENGLISH.VIV", GameData.Dir);
        int banks = 0;
        foreach (var e in viv.Entries)
        {
            var d = viv.Data.AsSpan(e.Offset, e.Size).ToArray();
            if (!d.AsSpan(0, 4).SequenceEqual("BNKl"u8)) continue;
            CheckBank(d, e.Name);
            banks++;
        }
        Assert.Equal(169, banks);
    }
}

public class WavWriterTests
{
    [Fact]
    public void Encode_HeaderFields()
    {
        var w = WavWriter.Encode(new short[] { 1, -2, 3, -4 }, 22050, 2);
        Assert.Equal(44 + 8, w.Length);
        Assert.Equal("RIFF"u8.ToArray(), w[..4]);
        Assert.Equal(44 + 8 - 8, BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(w.AsSpan(22)));
        Assert.Equal(22050, BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(24)));
        Assert.Equal(22050 * 4, BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(28)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(40)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt16LittleEndian(w.AsSpan(46)));
    }

    [GameFact]
    public void Export_TempAsfAndBank_ToWav()
    {
        var dir = GameData.OutDir();
        var s = AsfStream.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/PC/TEMP.ASF"));
        var pcm = s.Decode(150);   // ~10 s
        var p1 = Path.Combine(dir, "cs_temp_asf_head.wav");
        WavWriter.Write(p1, pcm, s.SampleRate, s.Channels);
        Assert.Equal(44 + pcm.Length * 2, new FileInfo(p1).Length);

        var b = BnkBank.Parse(GameData.ReadRaw("GAMEDATA/AUDIO/SFX/PC/CNTENG.BNK"));
        foreach (var smp in b.Samples)
        {
            var p = Path.Combine(dir, $"cs_cnteng_{smp.Slot:D2}.wav");
            WavWriter.Write(p, b.Decode(smp), smp.SampleRate, smp.Channels);
            Assert.True(File.Exists(p));
        }
    }
}
