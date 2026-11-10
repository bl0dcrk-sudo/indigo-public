using System.Buffers.Binary;
using System.Reflection;

namespace NFS.Core;

/// <summary>
/// The value fields of <see cref="CarObj"/> (and its <see cref="TrackPos"/> at +0x08) addressed by their
/// <see cref="OrigAttribute"/> offsets, so a raw 0x684-byte carObj dumped from the original can be [ref:3366]
/// compared with the port field by field, or loaded into a port car. Pointers/delegates/object references and the
/// model name are skipped. Every value is widened to <c>long</c> (sign as declared: byte/ushort unsigned).
/// </summary>
public static class CarDump
{
    /// <summary>One scalar slot: <paramref name="Name"/> ("Pos.X", "Trk.Slice", "LapTimes[3]"…), byte offset in the
    /// carObj and size (1, 2 or 4).</summary>
    public sealed record Slot(string Name, int Offset, int Size, bool Signed, Func<CarObj, long> Get, Action<CarObj, long> Set);

    public static IReadOnlyList<Slot> Slots { get; } = Build();

    private static List<Slot> Build()
    {
        var list = new List<Slot>();
        AddFields(list, typeof(CarObj), 0, "", c => c);
        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return list;
    }

    private static void AddFields(List<Slot> list, Type t, int baseOff, string prefix, Func<CarObj, object> owner)
    {
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.GetCustomAttribute<OrigAttribute>() is not { } a) continue;
            int off = baseOff + a.Offset;
            string name = prefix + f.Name;
            var ft = f.FieldType;
            if (ft == typeof(TrackPos))
            {
                AddFields(list, ft, off, name + ".", c => f.GetValue(owner(c))!);
            }
            else if (ft == typeof(Vec3))
            {
                AddVec(list, name, off, c => (Vec3)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), v));
            }
            else if (ft == typeof(Mat3))
            {
                string[] m = ["M00", "M01", "M02", "M10", "M11", "M12", "M20", "M21", "M22"];
                for (int i = 0; i < 9; i++)
                {
                    int k = i;
                    list.Add(new Slot($"{name}.{m[k]}", off + 4 * k, 4, true,
                        c => MatGet((Mat3)f.GetValue(owner(c))!, k),
                        (c, v) => { var x = (Mat3)f.GetValue(owner(c))!; MatSet(ref x, k, (int)v); f.SetValue(owner(c), x); }));
                }
            }
            else if (ft == typeof(Vec3[]))
            {
                int n = a.Size / 12;
                for (int i = 0; i < n; i++)
                {
                    int k = i;
                    AddVec(list, $"{name}[{k}]", off + 12 * k, c => ((Vec3[])f.GetValue(owner(c))!)[k],
                        (c, v) => ((Vec3[])f.GetValue(owner(c))!)[k] = v);
                }
            }
            else if (ft == typeof(int[]) || ft == typeof(short[]))
            {
                int es = ft == typeof(int[]) ? 4 : 2, n = a.Size / es;
                for (int i = 0; i < n; i++)
                {
                    int k = i;
                    if (es == 4)
                        list.Add(new Slot($"{name}[{k}]", off + 4 * k, 4, true,
                            c => ((int[])f.GetValue(owner(c))!)[k], (c, v) => ((int[])f.GetValue(owner(c))!)[k] = (int)v));
                    else
                        list.Add(new Slot($"{name}[{k}]", off + 2 * k, 2, true,
                            c => ((short[])f.GetValue(owner(c))!)[k], (c, v) => ((short[])f.GetValue(owner(c))!)[k] = (short)v));
                }
            }
            else if (ft == typeof(int))
                list.Add(new Slot(name, off, 4, true, c => (int)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), (int)v)));
            else if (ft == typeof(ushort))
                list.Add(new Slot(name, off, 2, false, c => (ushort)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), (ushort)v)));
            else if (ft == typeof(short))
                list.Add(new Slot(name, off, 2, true, c => (short)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), (short)v)));
            else if (ft == typeof(byte))
                list.Add(new Slot(name, off, 1, false, c => (byte)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), (byte)v)));
            else if (ft == typeof(CarFlags))
                list.Add(new Slot(name, off, 1, false, c => (byte)(CarFlags)f.GetValue(owner(c))!, (c, v) => f.SetValue(owner(c), (CarFlags)(byte)v)));
            // object / delegate / CarObj references and the model name: not comparable
        }
    }

    private static void AddVec(List<Slot> list, string name, int off, Func<CarObj, Vec3> get, Action<CarObj, Vec3> set)
    {
        list.Add(new Slot(name + ".X", off, 4, true, c => get(c).X, (c, v) => { var x = get(c); x.X = (int)v; set(c, x); }));
        list.Add(new Slot(name + ".Y", off + 4, 4, true, c => get(c).Y, (c, v) => { var x = get(c); x.Y = (int)v; set(c, x); }));
        list.Add(new Slot(name + ".Z", off + 8, 4, true, c => get(c).Z, (c, v) => { var x = get(c); x.Z = (int)v; set(c, x); }));
    }

    private static int MatGet(Mat3 m, int i) => i switch
    {
        0 => m.M00, 1 => m.M01, 2 => m.M02, 3 => m.M10, 4 => m.M11, 5 => m.M12, 6 => m.M20, 7 => m.M21, _ => m.M22,
    };

    private static void MatSet(ref Mat3 m, int i, int v)
    {
        switch (i)
        {
            case 0: m.M00 = v; break; case 1: m.M01 = v; break; case 2: m.M02 = v; break;
            case 3: m.M10 = v; break; case 4: m.M11 = v; break; case 5: m.M12 = v; break;
            case 6: m.M20 = v; break; case 7: m.M21 = v; break; default: m.M22 = v; break;
        }
    }

    /// <summary>Value of a slot in a raw carObj dump.</summary>
    public static long Read(ReadOnlySpan<byte> raw, Slot s) => s.Size switch
    {
        4 => BinaryPrimitives.ReadInt32LittleEndian(raw[s.Offset..]),
        2 => s.Signed ? BinaryPrimitives.ReadInt16LittleEndian(raw[s.Offset..]) : BinaryPrimitives.ReadUInt16LittleEndian(raw[s.Offset..]),
        _ => s.Signed ? (sbyte)raw[s.Offset] : raw[s.Offset],
    };

    /// <summary>Writes the port car's value fields into a raw carObj image (other bytes untouched).</summary>
    public static void Write(CarObj c, Span<byte> raw)
    {
        foreach (var s in Slots)
        {
            long v = s.Get(c);
            if (s.Size == 4) BinaryPrimitives.WriteInt32LittleEndian(raw[s.Offset..], (int)v);
            else if (s.Size == 2) BinaryPrimitives.WriteUInt16LittleEndian(raw[s.Offset..], (ushort)v);
            else raw[s.Offset] = (byte)v;
        }
    }

    /// <summary>Loads every value field from a raw carObj dump into the port car (<paramref name="only"/> = optional
    /// name filter).</summary>
    public static void Load(CarObj c, ReadOnlySpan<byte> raw, Func<Slot, bool>? only = null)
    {
        foreach (var s in Slots)
            if (only == null || only(s)) s.Set(c, Read(raw, s));
    }

    /// <summary>Slots whose port value differs from the dump: (slot, port, original).</summary>
    public static IEnumerable<(Slot Slot, long Port, long Orig)> Diff(CarObj c, ReadOnlySpan<byte> raw)
    {
        var res = new List<(Slot, long, long)>();
        foreach (var s in Slots)
        {
            long p = s.Get(c), o = Read(raw, s);
            if (s.Size == 2 && !s.Signed) p = (ushort)p;
            if (p != o) res.Add((s, p, o));
        }
        return res;
    }
}
