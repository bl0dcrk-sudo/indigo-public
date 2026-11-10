using NFS.Net.Client;
using NFS.Net.Protocol;
using Xunit;

namespace NFS.Tests.Net;

public class HeadlessOptionsTests
{
    static HeadlessOptions Ok(params string[] args)
    {
        Assert.True(HeadlessOptions.TryParse(args, out var o, out string error), error);
        return o;
    }

    static string Fail(params string[] args)
    {
        Assert.False(HeadlessOptions.TryParse(args, out _, out string error));
        return error;
    }

    [Fact]
    public void ModDownloadsAreOffUnlessTheFlagIsGiven()
    {
        Assert.False(Ok("--server", "127.0.0.1:27100").AcceptModDownloads);
        Assert.True(Ok("--server", "127.0.0.1:27100", "--accept-mod-downloads").AcceptModDownloads);
    }

    [Fact]
    public void WithoutAnActionTheClientListsTheGames()
    {
        var o = Ok("--server", "127.0.0.1:27100");
        Assert.True(o.List);
        Assert.Equal("headless", o.Name);
        Assert.Equal(60, o.Seconds);
    }

    [Fact]
    public void ChatIsCleanedAndMustNotBeEmpty()
    {
        Assert.Null(Ok("--server", "127.0.0.1:27100").Chat);
        Assert.Equal("hi there", Ok("--server", "127.0.0.1:27100", "--chat", "  hi there \t").Chat);
        Assert.Contains("--chat", Fail("--server", "127.0.0.1:27100", "--chat", "   "));
    }

    [Fact]
    public void CreateTakesAllGameOptions()
    {
        var o = Ok("--port-file", "p.txt", "--name", "alice", "--create", "smoke", "--max-players", "3", "--track", "5", "--laps", "2",
            "--bots", "1", "--backward", "--traffic", "--car", "4", "--start-when", "2");
        Assert.False(o.List);
        Assert.Equal(("smoke", 3, 5, 2, 1), (o.Create, o.MaxPlayers, o.Track, o.Laps, o.Bots));
        Assert.True(o.Backward && o.Traffic);
        Assert.Equal(4, o.Car);
        Assert.Equal(2, o.StartWhen);
    }

    [Fact]
    public void BotsDefaultToAutoAndAcceptTheWordAuto()
    {
        Assert.Equal(NetProtocol.AutoBots, Ok("--server", "127.0.0.1:1", "--create", "g").Bots);
        Assert.Equal(NetProtocol.AutoBots, Ok("--server", "127.0.0.1:1", "--create", "g", "--bots", "AUTO").Bots);
    }

    [Fact]
    public void JoinVariantsAreExclusiveAndRecognised()
    {
        Assert.Equal("g", Ok("--server", "127.0.0.1:1", "--join", "g").Join);
        Assert.True(Ok("--server", "127.0.0.1:1", "--join-first").JoinFirst);
        Assert.Equal(7, Ok("--server", "127.0.0.1:1", "--join-id", "7").JoinId);
        Assert.Contains("one of", Fail("--server", "127.0.0.1:1", "--join", "g", "--create", "h"));
    }

    [Theory]
    [InlineData("--track", "8")]
    [InlineData("--laps", "0")]
    [InlineData("--max-players", "9")]
    [InlineData("--car", "15")]
    [InlineData("--bots", "8")]
    [InlineData("--seconds", "0")]
    [InlineData("--steer", "200")]
    [InlineData("--name", "waytoolongnickname")]
    public void OutOfRangeValuesAreRefused(string flag, string value) =>
        Assert.Contains(flag, Fail("--server", "127.0.0.1:1", "--create", "g", flag, value));

    [Fact]
    public void MissingServerUnknownFlagsAndMissingValuesAreRefused()
    {
        Assert.Contains("--server or --port-file", Fail("--list"));
        Assert.Contains("unknown flag", Fail("--server", "127.0.0.1:1", "--bogus"));
        Assert.Contains("needs a value", Fail("--server", "127.0.0.1:1", "--create"));
        Assert.Contains("IP:PORT", Fail("--server", "nonsense"));
    }

    [Fact]
    public void UsageDescribesEveryFlag()
    {
        foreach (var flag in new[] { "--list", "--create", "--join", "--join-first", "--join-id", "--max-players", "--track", "--laps",
                     "--bots", "--backward", "--traffic", "--car", "--no-ready", "--start-when", "--steer", "--reset-at", "--leave-at" })
            Assert.Contains(flag, HeadlessOptions.Usage);
    }
}
