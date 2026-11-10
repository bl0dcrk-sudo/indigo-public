using System.Text;

namespace NFS.Data;

/// <summary>Field label from an EA editor description file.</summary>
public readonly record struct DesField(int Offset, string Type, string Description);

/// <summary>*.DES — descriptions written by EA's (DOS) data editor next to the .DAT files; the game never reads them.
/// The files contain memory garbage, so this is a heuristic scan: a type keyword ("Fixed", "Integer", "Array", …)
/// NUL-terminated, with the field description 10 B after the start of the keyword (record stride usually 154 B).
/// Useful only as documentation (names of carSpecs / simtune / trafcfg / prsonal fields).</summary>
public static class DesFile
{
    private static readonly string[] Types = ["Fixed", "Integer", "Array", "Byte", "Short", "Struct", "String"];

    public static IReadOnlyList<DesField> Parse(byte[] d)
    {
        var r = new List<DesField>();
        for (int i = 0; i + 12 < d.Length; i++)
        {
            foreach (var t in Types)
            {
                if (!Bin.HasTag(d, i, t) || i + t.Length >= d.Length || d[i + t.Length] != 0) continue;
                var desc = Bin.CString(d, i + 10, Math.Min(80, d.Length - i - 10));
                if (desc.Length >= 3 && desc.All(c => c >= 0x20 && c < 0x7F))
                    r.Add(new DesField(i, t, desc));
                break;
            }
        }
        return r;
    }

    public static IReadOnlyList<DesField> Load(string relative, string? gameDir = null) => Parse(GameFiles.ReadRaw(relative, gameDir));
}
