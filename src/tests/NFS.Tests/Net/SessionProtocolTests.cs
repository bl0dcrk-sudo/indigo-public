using NFS.Game.Cars;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Tests.Net;

/// <summary>Wire format of the session/lobby messages (protocol v8): every message round-trips with the documented header,
/// and invalid names, ids, options, members and foreign/malformed data are rejected before reaching session state.</summary>
public class SessionProtocolTests
{
    const uint Epoch = 0x1234ABCD;

    static NetGameListRequest ReadListRequest(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetGameListRequest.Decode(h, ref r);
    }

    static NetGameList ReadList(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetGameList.Decode(h, ref r);
    }

    static NetGameCreate ReadCreate(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetGameCreate.Decode(h, ref r);
    }

    static NetGameJoin ReadJoin(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetGameJoin.Decode(h, ref r);
    }

    static NetGameLeave ReadLeave(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetGameLeave.Decode(h, ref r);
    }

    static NetLobbyReady ReadReady(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyReady.Decode(h, ref r);
    }

    static NetLobbyCar ReadCar(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyCar.Decode(h, ref r);
    }

    static NetLobbyOptions ReadOptions(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyOptions.Decode(h, ref r);
    }

    static NetLobbyStart ReadStart(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyStart.Decode(h, ref r);
    }

    static NetLobbyReturn ReadReturn(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyReturn.Decode(h, ref r);
    }

    static NetLobbyState ReadState(byte[] data)
    {
        var r = new WireReader(data); var h = NetHeader.Read(ref r); return NetLobbyState.Decode(h, ref r);
    }

    static NetHeader Header(byte[] data)
    {
        var r = new WireReader(data); return NetHeader.Read(ref r);
    }

    [Fact]
    public void SessionAndLobbyMessages_RoundTripWithTheDocumentedHeaders()
    {
        var requestHeader = Header(new NetGameListRequest().Encode());
        Assert.Equal((NetMessageType.GameListRequest, NetSessionProtocol.SessionRaceId, NetSessionProtocol.SessionEpoch),
            (requestHeader.Type, requestHeader.RaceId, requestHeader.Epoch));
        ReadListRequest(new NetGameListRequest().Encode());

        var games = new NetGameList([
            new NetGameInfo(7, "Practice", NetGamePhase.Lobby, 1, 4, 3, 2, NetGameMode.SingleRace),
            new NetGameInfo(8, "Race A", NetGamePhase.Race, 2, 2, 7, 8, NetGameMode.SingleRace)]);
        var list = ReadList(games.Encode());
        Assert.Equal(games.Games, list.Games);
        Assert.Equal(0, Header(games.Encode()).RaceId);

        var create = new NetGameCreate("Friday night", 4, NetGameMode.Knockout);
        Assert.Equal(create, ReadCreate(create.Encode()));
        Assert.Equal(NetMessageType.GameCreate, Header(create.Encode()).Type);

        var join = new NetGameJoin(0x1020304);
        Assert.Equal(join, ReadJoin(join.Encode()));

        var leave = new NetGameLeave(7, Epoch);
        Assert.Equal(leave, ReadLeave(leave.Encode()));
        Assert.Equal((NetMessageType.GameLeave, 7, Epoch), (Header(leave.Encode()).Type, Header(leave.Encode()).RaceId, Header(leave.Encode()).Epoch));

        var ready = new NetLobbyReady(7, Epoch, true);
        Assert.Equal(ready, ReadReady(ready.Encode()));
        var car = new NetLobbyCar(7, Epoch, NetProtocol.MaxCarType);
        Assert.Equal(car, ReadCar(car.Encode()));
        var options = new NetLobbyOptions(7, Epoch, NetProtocol.MaxTrack, NetProtocol.MaxLaps);
        Assert.Equal(options, ReadOptions(options.Encode()));
        var start = new NetLobbyStart(7, Epoch);
        Assert.Equal(start, ReadStart(start.Encode()));
        var back = new NetLobbyReturn(7, Epoch);
        Assert.Equal(back, ReadReturn(back.Encode()));
        var cancel = new NetLobbyCancel(7, Epoch);
        var cr = new WireReader(cancel.Encode());
        var ch = NetHeader.Read(ref cr);
        Assert.Equal(cancel, NetLobbyCancel.Decode(ch, ref cr));

        var state = new NetLobbyState(7, Epoch, "Friday night", NetGamePhase.Countdown, 2, 3, 4, NetGameMode.SingleRace, 2500,
            [new NetLobbyMember(0, "alice", true, 3, true, true), new NetLobbyMember(2, "bob", false, NetProtocol.NoCarType, false, false)]);
        var stateBack = ReadState(state.Encode());
        Assert.Equal(state.GameName, stateBack.GameName);
        Assert.Equal((state.GameId, state.Epoch, state.Phase, state.Track, state.Laps, state.MaxPlayers, state.Mode, state.CountdownMillis),
            (stateBack.GameId, stateBack.Epoch, stateBack.Phase, stateBack.Track, stateBack.Laps, stateBack.MaxPlayers, stateBack.Mode, stateBack.CountdownMillis));
        Assert.Equal(state.Members, stateBack.Members);
        Assert.Equal((7, Epoch), (Header(state.Encode()).RaceId, Header(state.Encode()).Epoch));

        var lobby = state with { Phase = NetGamePhase.Lobby, CountdownMillis = 0 };
        Assert.Equal(lobby.Members, ReadState(lobby.Encode()).Members);

        // A full lobby with maximum-length game and player names must fit the encoder budget.
        var fullMembers = Enumerable.Range(0, NetProtocol.MaxSeats)
            .Select(i => new NetLobbyMember(i, ("p" + i.ToString("D2")).PadRight(NetProtocol.MaxNameLength), true, i,
                i == 0, true))
            .ToArray();
        var full = new NetLobbyState(9, Epoch, new string('G', NetProtocol.MaxGameNameLength), NetGamePhase.Countdown,
            NetProtocol.MaxTrack, NetProtocol.MaxLaps, NetProtocol.MaxSeats, NetGameMode.SingleRace,
            NetProtocol.MaxCountdownMillis, fullMembers);
        var fullBack = ReadState(full.Encode());
        Assert.Equal(full.Members, fullBack.Members);
        Assert.Equal((NetProtocol.MaxCountdownMillis, NetProtocol.MaxSeats), (fullBack.CountdownMillis, fullBack.MaxPlayers));
    }

    [Fact]
    public void InvalidFieldsAreRejectedBeforeEncoding()
    {
        var create = new NetGameCreate("Game", 2);
        Assert.Throws<ArgumentException>(() => (create with { Name = "" }).Encode());
        Assert.Throws<ArgumentException>(() => (create with { Name = new string('x', NetProtocol.MaxGameNameLength + 1) }).Encode());
        Assert.Throws<ArgumentException>(() => (create with { Name = "bad\nname" }).Encode());
        foreach (var bad in new[] { 0, NetProtocol.MaxSeats + 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => (create with { MaxPlayers = bad }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (create with { Mode = (NetGameMode)0 }).Encode());

        var info = new NetGameInfo(1, "G", NetGamePhase.Lobby, 1, 2, 0, 1, NetGameMode.SingleRace);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { GameId = 0 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Players = 3 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { MaxPlayers = 0 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Track = 8 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Laps = 9 }]).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameList([info with { Phase = (NetGamePhase)0 }]).Encode());
        Assert.Throws<ArgumentException>(() => new NetGameList([info, info]).Encode());
        Assert.Throws<ArgumentException>(() => new NetGameList([info, info with { GameId = 2 }]).Encode());

        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameJoin(0).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameLeave(0, Epoch).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetGameLeave(1, 0).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyReady(0, Epoch, false).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyCar(1, Epoch, CarCatalog.VanillaCount).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyCar(1, Epoch, NetProtocol.NoCarType).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyOptions(1, Epoch, NetProtocol.MaxTrack + 1, 1).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyOptions(1, Epoch, 0, NetProtocol.MaxLaps + 1).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyStart(0, Epoch).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NetLobbyReturn(1, 0).Encode());

        var member = new NetLobbyMember(0, "alice", false, 1, true, true);
        var state = new NetLobbyState(1, Epoch, "G", NetGamePhase.Lobby, 0, 1, 2, NetGameMode.SingleRace, 0, [member]);
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Members = [] }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Members = [member with { Slot = 2 }] }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { Members = [member with { CarType = CarCatalog.VanillaCount }] }).Encode());
        Assert.Throws<ArgumentException>(() => (state with { Members = [member, member with { Slot = 1, Name = "ALICE" }] }).Encode());
        Assert.Throws<ArgumentException>(() => (state with { Members = [member with { Operator = false }] }).Encode());
        Assert.Throws<ArgumentException>(() => (state with { Members = [member, member with { Slot = 1, Name = "bob" }] }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() => (state with { CountdownMillis = 1 }).Encode());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (state with { Phase = NetGamePhase.Countdown, CountdownMillis = NetProtocol.MaxCountdownMillis + 1 }).Encode());
        Assert.Throws<ArgumentException>(() => (state with { GameName = "" }).Encode());
    }

    static byte[] RawGameInfo(int id, string name, int phase, int players, int maxPlayers, int track, int laps, int mode)
    {
        var w = new WireWriter(64);
        w.VarU(id); w.Ascii(name, NetProtocol.MaxGameNameLength);
        w.U8(phase); w.U8(players); w.U8(maxPlayers); w.U8(track); w.U8(laps); w.U8(mode); w.U8(0); w.I8(-1); w.U8(0); w.U8(1); w.U8(1); w.U8(1); // direction: forward, bots: auto, traffic: off, skill: expert, catch-up: on, style: Wild
        return w.ToArray();
    }

    static byte[] RawGameList(params byte[][] games)
    {
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        NetSessionProtocol.SessionHeader(NetMessageType.GameList).Write(w);
        w.U8(games.Length);
        foreach (var game in games) w.Bytes(game);
        return w.ToArray();
    }

    static byte[] RawLobbyState(int gameId, uint epoch, string gameName, int phase, int track, int laps, int maxPlayers, int mode,
        int countdown, params (int Slot, string Name, int Car, int Flags)[] members)
    {
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyState, gameId, epoch).Write(w);
        w.Ascii(gameName, NetProtocol.MaxGameNameLength);
        w.U8(phase); w.U8(track); w.U8(laps); w.U8(maxPlayers); w.U8(mode); w.U8(0); w.I8(-1); w.U8(0); w.U8(1); w.U8(1); w.U8(1); w.VarU(countdown); w.U8(members.Length);
        foreach (var m in members) { w.U8(m.Slot); w.Ascii(m.Name, NetProtocol.MaxNameLength); w.Ascii(m.Car >= 1000 ? "bad key!" : NetCars.VanillaKey(m.Car)?.Value ?? "", NetProtocol.MaxCarKeyLength); w.I8(-1); w.U8(m.Flags); }
        w.U8(0); // resume token (none; the recipient's own token comes after the members)
        w.VarU(0); w.U8(0); // v12 tail: no straggler countdown, no knockout series
        return w.ToArray();
    }

    [Fact]
    public void MalformedOrOutOfRangeWireDataIsRejected()
    {
        var tooMany = new WireWriter(16);
        NetSessionProtocol.SessionHeader(NetMessageType.GameList).Write(tooMany);
        tooMany.U8(NetProtocol.MaxGameListEntries + 1);
        Assert.Throws<NetProtocolException>(() => ReadList(tooMany.ToArray()));

        byte[] good = RawGameInfo(1, "A", (byte)NetGamePhase.Lobby, 0, 2, 0, 1, (byte)NetGameMode.SingleRace);
        Assert.Single(ReadList(RawGameList(good)).Games);
        foreach (var bad in new[]
                 {
                     RawGameInfo(0, "A", 1, 0, 2, 0, 1, 1), RawGameInfo(1, "", 1, 0, 2, 0, 1, 1),
                     RawGameInfo(1, "A", 0, 0, 2, 0, 1, 1), RawGameInfo(1, "A", 4, 0, 2, 0, 1, 1),
                     RawGameInfo(1, "A", 1, 3, 2, 0, 1, 1), RawGameInfo(1, "A", 1, 0, 0, 0, 1, 1),
                     RawGameInfo(1, "A", 1, 0, 2, 8, 1, 1), RawGameInfo(1, "A", 1, 0, 2, 0, 9, 1),
                     RawGameInfo(1, "A", 1, 0, 2, 0, 1, 0),
                 })
            Assert.Throws<NetProtocolException>(() => ReadList(RawGameList(bad)));
        Assert.Throws<NetProtocolException>(() =>
            ReadList(RawGameList(RawGameInfo(1, "A", 1, 0, 2, 0, 1, 1), RawGameInfo(1, "B", 1, 0, 2, 0, 1, 1))));
        Assert.Throws<NetProtocolException>(() =>
            ReadList(RawGameList(RawGameInfo(1, "A", 1, 0, 2, 0, 1, 1), RawGameInfo(2, "a", 1, 0, 2, 0, 1, 1))));

        var create = new NetGameCreate("Game", 2);
        var bytes = create.Encode();
        Assert.Equal(create, ReadCreate(bytes));
        int nameLength = NetHeader.Size;
        int maxPlayers = nameLength + 1 + create.Name.Length;
        Assert.Throws<NetProtocolException>(() => ReadCreate([.. bytes[..nameLength], 0, .. bytes[(nameLength + 1)..]]));
        var nonPrintable = (byte[])bytes.Clone();
        nonPrintable[nameLength + 1] = 0x01;
        Assert.Throws<NetProtocolException>(() => ReadCreate(nonPrintable));
        foreach (var (offset, value) in new[] { (maxPlayers, (byte)0), (maxPlayers, (byte)9), (maxPlayers + 1, (byte)0), (maxPlayers + 1, (byte)3) })
        {
            var bad = (byte[])bytes.Clone();
            bad[offset] = value;
            Assert.Throws<NetProtocolException>(() => ReadCreate(bad));
        }
        Assert.Throws<NetProtocolException>(() => ReadCreate([.. bytes, 0]));
        var foreignSession = (byte[])bytes.Clone();
        foreignSession[1] = 1; // session race id must be 0
        Assert.Throws<NetProtocolException>(() => ReadCreate(foreignSession));

        Assert.Throws<NetProtocolException>(() => ReadJoin([.. new NetGameJoin(1).Encode()[..NetHeader.Size], 0]));

        var member = (Slot: 0, Name: "alice", Car: 1, Flags: 3); // ready + operator + connected
        var state = RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member);
        Assert.Single(ReadState(state).Members);
        foreach (var bad in new[]
                 {
                     RawLobbyState(1, Epoch, "G", 0, 0, 1, 2, 1, 0, member),
                     RawLobbyState(1, Epoch, "G", 1, 8, 1, 2, 1, 0, member),
                     RawLobbyState(1, Epoch, "G", 1, 0, 0, 2, 1, 0, member),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 0, 1, 0, member),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 0, 0, member),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 1, member),         // countdown outside Countdown
                     RawLobbyState(1, Epoch, "G", 2, 0, 1, 2, 1, NetProtocol.MaxCountdownMillis + 1, member),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0),                // no members
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member with { Flags = 0 }), // no operator
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member with { Flags = 8 }), // unknown flag
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member with { Car = 1000 }),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member, member with { Slot = 1 }),
                     RawLobbyState(1, Epoch, "G", 1, 0, 1, 2, 1, 0, member, member with { Slot = 1, Name = "ALICE" }),
                 })
            Assert.Throws<NetProtocolException>(() => ReadState(bad));
        Assert.Throws<NetProtocolException>(() => ReadState([.. state, 0]));
        var zeroRace = (byte[])state.Clone();
        zeroRace[1] = 0; // lobby/game RaceId = GameId, never 0
        Assert.Throws<NetProtocolException>(() => ReadState(zeroRace));
    }

    [Fact]
    public void UnknownAndForeignMessageTypesAreDroppedWithoutThrowing()
    {
        var client = new RaceClient(null, new byte[32], "alice");
        client.OnReceive(NetChannel.ReliableOrdered, [250]);                       // type outside the enum
        Assert.Equal(1, client.Stats.Malformed);
        client.OnReceive(NetChannel.ReliableOrdered, new NetLobbyStart(1, Epoch).Encode()); // known type, wrong place
        Assert.Equal(1, client.Stats.WrongSession);
        client.OnReceive(NetChannel.ReliableOrdered, []);
        Assert.Equal(2, client.Stats.Malformed);
        Assert.Throws<NetProtocolException>(() => ReadHeader([]));
        Assert.Throws<NetProtocolException>(() => ReadHeader([(byte)NetMessageType.LobbyStart]));

        static void ReadHeader(byte[] data)
        {
            var reader = new WireReader(data);
            NetHeader.Read(ref reader);
        }
    }

    [Fact]
    public void LobbyMessageWithASessionHeaderIsRejected()
    {
        foreach (var type in new[] { NetMessageType.GameLeave, NetMessageType.LobbyReady, NetMessageType.LobbyStart,
                     NetMessageType.LobbyReturn, NetMessageType.LobbyState, NetMessageType.LobbyCancel })
        {
            var w = new WireWriter(16);
            NetSessionProtocol.SessionHeader(type).Write(w);
            var data = w.ToArray();
            Assert.Throws<NetProtocolException>(() =>
            {
                var reader = new WireReader(data);
                var header = NetHeader.Read(ref reader);
                switch (type)
                {
                    case NetMessageType.GameLeave: NetGameLeave.Decode(header, ref reader); break;
                    case NetMessageType.LobbyReady: NetLobbyReady.Decode(header, ref reader); break;
                    case NetMessageType.LobbyStart: NetLobbyStart.Decode(header, ref reader); break;
                    case NetMessageType.LobbyReturn: NetLobbyReturn.Decode(header, ref reader); break;
                    case NetMessageType.LobbyCancel: NetLobbyCancel.Decode(header, ref reader); break;
                    default: NetLobbyState.Decode(header, ref reader); break;
                }
            });
        }
    }

    [Fact]
    public void EmptyResumeTokenMeansNoToken()
    {
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, new byte[32], "bob") with { ResumeToken = [] };
        Assert.Null(hello.ResumeToken);
        Assert.Null(NetHello.Decode(hello.Encode()).ResumeToken);

        var state = new NetLobbyState(5, Epoch, "G", NetGamePhase.Lobby, 1, 2, 4, NetGameMode.SingleRace, 0,
            [new NetLobbyMember(0, "bob", false, NetProtocol.NoCarType, true, true)], []);
        var data = state.Encode();
        var reader = new WireReader(data);
        var header = NetHeader.Read(ref reader);
        Assert.Null(NetLobbyState.Decode(header, ref reader).ResumeToken);
    }
}
