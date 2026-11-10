namespace NFS.Data.FrontEnd;

/// <summary>FNTF font. Glyph records are 11 bytes; [ref:0974]
/// the atlas is an embedded FSH bitmap, without an SHPI directory.</summary>
public sealed class Ffn
{
    public readonly record struct Glyph(int Width, int Height, int X, int Y, int Advance, int Left, int Top);
    readonly Dictionary<char, Glyph> _glyphs = new();
    public Fsh Atlas { get; }
    public byte[] Coverage { get; }
    public int Width => Atlas.Entries[0].Main.Width;
    /// <summary>Font height of the original: the signed bytes at +0x12 and +0x13 of the file added up. [ref:0975]
    /// SWISS13 / 16 / 18 / 36 give 28 / 34 / 39 / 79, so it is about twice the glyph cell height.</summary>
    public int TextHeight { get; }

    Ffn(byte[] data)
    {
        Bin.Require(data.Length >= 32 && Bin.HasTag(data, 0, "FNTF"), "not an FFN font");
        int count = Bin.U16(data, 10), table = Bin.S32(data, 20), bitmap = Bin.S32(data, 28);
        Bin.Require(table >= 32 && count <= (data.Length - table) / 11 && bitmap >= 32 && bitmap < data.Length - 16,
            "invalid FFN offsets");
        TextHeight = (sbyte)data[0x12] + (sbyte)data[0x13];
        for (int i = 0; i < count; i++)
        {
            int p = table + i * 11;
            _glyphs[(char)Bin.U16(data, p)] = new(data[p + 2], data[p + 3], Bin.U16(data, p + 4),
                Bin.U16(data, p + 6), (sbyte)data[p + 8], (sbyte)data[p + 9], (sbyte)data[p + 10]);
        }
        // Reuse the FSH reader for the embedded 4-bit atlas.
        var wrapped = new byte[24 + data.Length - bitmap];
        "SHPI"u8.CopyTo(wrapped);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrapped.AsSpan(4), wrapped.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrapped.AsSpan(8), 1);
        "font"u8.CopyTo(wrapped.AsSpan(16));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wrapped.AsSpan(20), 24);
        data.AsSpan(bitmap).CopyTo(wrapped.AsSpan(24));
        Atlas = Fsh.Parse(wrapped);
        Bin.Require(Atlas.Entries[0].Main.Code == 0x7A, "unsupported FFN atlas");
        Coverage = Atlas.Indices(Atlas.Entries[0]);
    }

    public Glyph? Find(char c) => _glyphs.TryGetValue(c, out var glyph) ? glyph : null;
    public int Measure(string text)
    {
        int sum = 0;
        foreach (char c in text) if (_glyphs.TryGetValue(c, out var glyph)) sum += glyph.Advance;
        return sum;
    }
    public static Ffn Parse(byte[] data) => new(data);
    public static Ffn Load(int size, string? gameDir = null, bool complete = false) =>
        new(GameFiles.ReadRaw($"FEDATA/PC/TEXT/SWISS{size}{(size == 36 && complete ? "A" : "")}.FFN", gameDir));
}
