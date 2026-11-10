using System.Reflection;
using NFS.Core;

namespace NFS.Tests.Core;

/// <summary>Consistency of the merged carObj / TrackPos field tables ([Orig] offsets: no overlaps, known fields).</summary>
public class LayoutTests
{
    private static List<(string Name, int Off, int Size)> Fields(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Instance)
         .Select(f => (f.Name, a: f.GetCustomAttribute<OrigAttribute>()))
         .Where(p => p.a != null)
         .Select(p => (p.Name, p.a!.Offset, p.a.Size))
         .OrderBy(p => p.Offset).ToList();

    [Theory]
    [InlineData(typeof(CarObj), CarObj.Size)]
    [InlineData(typeof(TrackPos), TrackPos.Size)]
    public void Fields_DoNotOverlapAndFit(Type type, int size)
    {
        var f = Fields(type);
        Assert.NotEmpty(f);
        for (int i = 1; i < f.Count; i++)
            Assert.True(f[i - 1].Off + f[i - 1].Size <= f[i].Off, $"{f[i - 1].Name} overlaps {f[i].Name}");
        Assert.True(f[^1].Off + f[^1].Size <= size);
        // every public instance field is annotated
        Assert.Equal(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Length, f.Count);
    }

    [Fact]
    public void TrackPos_FillsAllBytes() =>
        Assert.Equal(TrackPos.Size, Fields(typeof(TrackPos)).Sum(f => f.Size));

    [Theory]
    [InlineData("Trk", 0x08)]
    [InlineData("Pos", 0x9C)]
    [InlineData("Vel", 0xA8)]
    [InlineData("Orient", 0xC4)]
    [InlineData("AngVel", 0xE8)]
    [InlineData("RoadFrame", 0x118)]
    [InlineData("DriveFrame", 0x188)]
    [InlineData("Flags", 0x1F4)]
    [InlineData("CatchUp", 0x218)]
    [InlineData("Rpm", 0x2F0)]
    [InlineData("TaskTrack", 0x36C)]
    [InlineData("TaskPost", 0x390)]
    [InlineData("AccelCurve", 0x408)]
    [InlineData("Glue", 0x558)]
    public void CarObj_KeyOffsets(string name, int off) =>
        Assert.Equal(off, Fields(typeof(CarObj)).Single(f => f.Name == name).Off);

    [Fact]
    public void CarObj_TrackPosAliasesMatchDocs()
    {
        // car+0x14 slice, +0x18 poly, +0x20 VROAD, +0x24 XBID6, +0x28 corners, +0x76, +0x80 slot [ref:3476]
        var tp = Fields(typeof(TrackPos)).ToDictionary(f => f.Name, f => f.Off + 8);
        Assert.Equal(0x14, tp["Slice"]);
        Assert.Equal(0x18, tp["RoadPoly"]);
        Assert.Equal(0x20, tp["VRoad"]);
        Assert.Equal(0x24, tp["Xbid6Slice"]);
        Assert.Equal(0x28, tp["Corners"]);
        Assert.Equal(0x76, tp["Unk6E"]);
        Assert.Equal(0x80, tp["Slot"]);
    }
}
