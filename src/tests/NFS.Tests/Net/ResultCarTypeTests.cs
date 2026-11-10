using NFS.Game.Race;
using NFS.Net.Protocol;

namespace NFS.Tests.Net;

/// <summary>Protocol v14: every row of the network result carries the car model type, so the client result screen shows the brand
/// logo of the winner (models below 0xC) instead of the default one.</summary>
public class ResultCarTypeTests
{
    static RacerResult Row(int place, int car, int type) => new(place, car, "mod", true, false, 1000 * place, [100, 200], 100, 2, "DRV", 4096, type);

    static NetResults Decode(byte[] bytes)
    {
        var r = new NFS.Net.Protocol.WireReader(bytes);
        return NetResults.Decode(NetHeader.Read(ref r), ref r);
    }

    [Fact]
    public void TheCarTypeOfEveryResultRowSurvivesTheWire()
    {
        var results = new RaceResults([Row(1, 0, 7), Row(2, 1, 0), Row(3, 2, NetProtocol.MaxCarType)]);
        var msg = new NetResults(5, 6, NetFinishReason.Finished, 10, 20, 0, results);
        var back = Decode(msg.Encode());
        Assert.Equal([7, 0, NetProtocol.MaxCarType], back.Results.Racers.Select(r => r.CarType));
        Assert.Equal(4096, back.Results.Racers[0].TopSpeedFx);
    }

    [Fact]
    public void AnOutOfRangeCarTypeIsClampedOnTheWireAndRefusedWhenReceived()
    {
        var msg = new NetResults(5, 6, NetFinishReason.Finished, 10, 20, 0, new RaceResults([Row(1, 0, 200)]));
        Assert.Equal(NetProtocol.MaxCarNumber, Decode(msg.Encode()).Results.Racers[0].CarType);
        var bytes = new NetResults(5, 6, NetFinishReason.Finished, 10, 20, 0, new RaceResults([Row(1, 0, 3)])).Encode();
        bytes[^1] = 200; // the car type is the last byte of the last row
        Assert.Throws<NetProtocolException>(() => Decode(bytes));
    }
}
