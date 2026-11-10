using NFS.Game.Cars;
using NFS.ModApi;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Lobby car selection by key (modding-api-cars D9, 5.2): the server accepts the showcase cars and the mod cars of its
/// catalog and refuses unknown keys, the cheat cars 12..14 and the other original cars.</summary>
public class ModCarLobbyTests
{
    static readonly CarKey Duster = CarKey.Parse("indigo.duster/duster");

    static CarCatalog CatalogWithDuster()
    {
        var catalog = new CarCatalog();
        var vanilla = new VanillaCarSource();
        foreach (var car in vanilla.GetCars()) catalog.Register(car, fromVanillaSource: true);
        catalog.Register(new CarDefinition(Duster, "Dacia Duster", null, new NoPhysics(), new CarTraits())
        {
            Colors = [new CarColor(new Dictionary<string, string> { ["en"] = "White" }, 255, 255, 255),
                      new CarColor(new Dictionary<string, string> { ["en"] = "Grey" }, 128, 128, 128)],
        }, fromVanillaSource: false);
        catalog.Freeze();
        return catalog;
    }

    static (SessionHarness Harness, SessionTestClient Alice) Lobby(SessionServerOptions? options)
    {
        var h = new SessionHarness(61, options);
        var alice = h.Join("alice");
        Assert.True(h.RunUntil(() => alice.List != null, 1_000_000));
        alice.Create("game", 2, 1, 2);
        Assert.True(h.RunUntil(() => alice.InLobby, 1_000_000));
        return (h, alice);
    }

    static bool Refused(SessionHarness h, Action send)
    {
        long before = h.Server.Stats.RefusedLobby;
        send();
        return h.RunUntil(() => h.Server.Stats.RefusedLobby > before, 1_000_000);
    }

    [GameFact]
    public void TheServerAcceptsAModCarOfItsCatalogAndShowsItByKey()
    {
        var (h, alice) = Lobby(new SessionServerOptions { Cars = CatalogWithDuster() });
        using var _ = h;
        alice.SetCar(Duster, 1);
        Assert.True(h.RunUntil(() => alice.Me?.Car == Duster, 1_000_000));
        Assert.Equal(1, alice.Me!.Colour);
        alice.SetCar(CarKey.Parse(NetCars.VanillaKey(3)!.Value.Value));
        Assert.True(h.RunUntil(() => alice.Me?.CarType == 3, 1_000_000));
    }

    [GameFact]
    public void AnUnknownKeyAndTheCarsOutsideTheShowcaseAreRefusedAndTheChoiceIsKept()
    {
        var (h, alice) = Lobby(new SessionServerOptions { Cars = CatalogWithDuster() });
        using var _ = h;
        alice.SetCar(Duster);
        Assert.True(h.RunUntil(() => alice.Me?.Car == Duster, 1_000_000));
        Assert.True(Refused(h, () => alice.SetCar(CarKey.Parse("nobody.nothing/ghost"))));
        foreach (int number in new[] { 12, 13, 14, 15, 52 })
            Assert.True(Refused(h, () => alice.SetCar(NetCars.VanillaKey(number)!.Value)), $"original car {number}");
        Assert.True(Refused(h, () => alice.SetCar(Duster, 2)));   // the Duster has two colours: 0 and 1
        Assert.Equal(Duster, alice.Me!.Car);
        Assert.Equal(NetProtocol.NoColour, alice.Me.Colour);
    }

    [GameFact]
    public void WithoutModsTheDusterIsUnknown()
    {
        var (h, alice) = Lobby(null);
        using var _ = h;
        Assert.True(Refused(h, () => alice.SetCar(Duster)));
        alice.SetCar(5);
        Assert.True(h.RunUntil(() => alice.Me?.CarType == 5, 1_000_000));
    }

    [Fact]
    public void TheLobbyMessagesCarryTheKeyOnTheWire()
    {
        var car = new NetLobbyCar(3, 9, Duster, 1);
        var bytes = car.Encode();
        var r = new WireReader(bytes);
        var header = NetHeader.Read(ref r);
        Assert.Equal(car, NetLobbyCar.Decode(header, ref r));

        var member = new NetLobbyMember(0, "alice", true, Duster, true, true, 1);
        var state = new NetLobbyState(3, 9, "G", NetGamePhase.Lobby, 1, 2, 2, NetGameMode.SingleRace, 0, [member, member with { Slot = 1, Name = "bob", Operator = false, Car = null }]);
        var rs = new WireReader(state.Encode());
        var back = NetLobbyState.Decode(NetHeader.Read(ref rs), ref rs);
        Assert.Equal(Duster, back.Members[0].Car);
        Assert.Null(back.Members[1].Car);
        Assert.Equal(NetProtocol.NoCarType, back.Members[1].CarType);
    }

    [Fact]
    public void TheCatalogMapsKeysToNumbers()
    {
        var catalog = CatalogWithDuster();
        Assert.Equal(CarCatalog.VanillaCount, NetCars.NumberOf(Duster, catalog));
        Assert.Equal(Duster, NetCars.KeyOf(CarCatalog.VanillaCount, catalog));
        Assert.Equal(NetProtocol.NoCarType, NetCars.NumberOf(Duster, null));
        Assert.True(NetCars.IsSelectableNumber(CarCatalog.VanillaCount, catalog));
        Assert.False(NetCars.IsSelectableNumber(CarCatalog.VanillaCount, null));
        Assert.False(NetCars.IsSelectableNumber(12, catalog));
        Assert.Equal([.. Enumerable.Range(0, 12), CarCatalog.VanillaCount], NetCars.SelectableNumbers(catalog));
    }
}
