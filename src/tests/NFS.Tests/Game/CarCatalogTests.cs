using NFS.ModApi;
using NFS.Game.Cars;
using Xunit;

namespace NFS.Tests.Game;

/// <summary>Car catalog: registration, key conflicts, reserved vanilla prefix, order, numbering, freeze.</summary>
public class CarCatalogTests
{
    private static CarDefinition Car(string key, int? legacy = null, string? name = null)
        => new(CarKey.Parse(key), name ?? key, legacy,
            new RawPhysics(new byte[RawPhysics.CarSpecsSize], new byte[RawPhysics.CarAiModelSize]), new CarTraits());

    private sealed class Source(string name, bool vanilla, params CarDefinition[] cars) : ICarSource
    {
        public string Name => name;
        public bool IsVanilla => vanilla;
        public IEnumerable<CarDefinition> GetCars() => cars;
    }

    [Fact]
    public void Register_ThenLookupByKey()
    {
        var c = new CarCatalog();
        c.Register(Car("nfs2se/MCF1", 4), true);
        Assert.Equal("nfs2se/MCF1", c.Get(CarKey.Parse("nfs2se/MCF1")).Key.Value);
        Assert.False(c.TryGet(CarKey.Parse("a.b/c"), out _));
        Assert.Equal(4, c.NumberOf(CarKey.Vanilla("MCF1")));
    }

    [Theory]
    [InlineData("nokey")]
    [InlineData("/car")]
    [InlineData("mod/")]
    [InlineData("a/b/c")]
    [InlineData("a b/c")]
    public void Key_RejectsMalformed(string text)
    {
        Assert.False(CarKey.TryParse(text, out _));
        Assert.Throws<FormatException>(() => CarKey.Parse(text));
    }

    [Fact]
    public void DuplicateKey_Rejected_FirstUnchanged()
    {
        var c = new CarCatalog();
        c.Register(Car("indigo.duster/duster", name: "first"), false);
        Assert.False(c.TryRegister(Car("indigo.duster/duster", name: "second"), false, out var err));
        Assert.Contains("duplicate", err);
        Assert.Equal("first", c.Get(CarKey.Parse("indigo.duster/duster")).DisplayName);
        Assert.Equal(1, c.Count);
        Assert.Throws<InvalidOperationException>(() => c.Register(Car("indigo.duster/duster"), false));
    }

    [Fact]
    public void VanillaPrefix_ReservedForVanillaSource()
    {
        var c = new CarCatalog();
        Assert.False(c.TryRegister(Car("nfs2se/EVIL"), false, out var err));
        Assert.Contains("reserved", err);
        Assert.False(c.TryRegister(Car("a.b/c", 1), true, out _));
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void LegacyIndex_OnlyVanilla_InRange()
    {
        var c = new CarCatalog();
        Assert.False(c.TryRegister(Car("nfs2se/X"), true, out _));
        Assert.False(c.TryRegister(Car("nfs2se/X", 0x35), true, out _));
        Assert.False(c.TryRegister(Car("a.b/c", 3), false, out _));
        Assert.True(c.TryRegister(Car("nfs2se/X", 0x34), true, out _));
        Assert.False(c.TryRegister(Car("nfs2se/Y", 0x34), true, out _));
    }

    [Fact]
    public void Order_VanillaThenMods_ModNumbersFrom0x35()
    {
        var (c, diag) = CarCatalog.Build(
        [
            new Source("vanilla", true, Car("nfs2se/A", 0), Car("nfs2se/B", 1)),
            new Source("mods", false, Car("m.one/x"), Car("m.two/y")),
        ]);
        Assert.Empty(diag);
        Assert.Equal(["nfs2se/A", "nfs2se/B", "m.one/x", "m.two/y"], c.Cars.Select(x => x.Key.Value));
        Assert.Equal(0x35, c.NumberOf(CarKey.Parse("m.one/x")));
        Assert.Equal(0x36, c.NumberOf(CarKey.Parse("m.two/y")));
        Assert.True(c.TryGetByNumber(0x36, out var car));
        Assert.Equal("m.two/y", car.Key.Value);
        Assert.False(c.TryGetByNumber(7, out _));
    }

    [Fact]
    public void Build_CollectsDiagnostics_AndKeepsFirst()
    {
        var (c, diag) = CarCatalog.Build(
        [
            new Source("vanilla", true, Car("nfs2se/A", 0)),
            new Source("m1", false, Car("m.one/x", name: "first")),
            new Source("m2", false, Car("m.one/x", name: "second"), Car("nfs2se/Z")),
        ]);
        Assert.Equal(2, diag.Count);
        Assert.StartsWith("m2:", diag[0]);
        Assert.Equal("first", c.Get(CarKey.Parse("m.one/x")).DisplayName);
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Frozen_RejectsRegistration()
    {
        var (c, _) = CarCatalog.Build([new Source("v", true, Car("nfs2se/A", 0))]);
        Assert.True(c.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => c.TryRegister(Car("a.b/c"), false, out _));
        Assert.Throws<InvalidOperationException>(() => c.Register(Car("a.b/c"), false));
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void RawPhysics_ValidatesSizes_AndCopies()
    {
        Assert.Throws<ArgumentException>(() => new RawPhysics(new byte[10], new byte[RawPhysics.CarAiModelSize]));
        Assert.Throws<ArgumentException>(() => new RawPhysics(new byte[RawPhysics.CarSpecsSize], new byte[10]));
        var specs = new byte[RawPhysics.CarSpecsSize];
        var p = new RawPhysics(specs, new byte[RawPhysics.CarAiModelSize]);
        specs[0] = 9;
        Assert.Equal(0, p.CarSpecs.Span[0]);
    }
}
