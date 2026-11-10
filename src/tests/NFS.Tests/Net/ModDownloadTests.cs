using System.Text;
using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Net.Client;
using NFS.Net.Protocol;
using NFS.Net.Server;
using NFS.Net.Transport;
using NFS.Tests.Data;

namespace NFS.Tests.Net;

/// <summary>Mod download (modding-api-cars 5.4): a client that lacks a mod of the server (or has another version) downloads exactly the
/// server's version next to what it has, after consent, through limits and validation of a local mod and the hash of the handshake.
/// Every directory is a temporary one (never the real mods/ or %APPDATA%).</summary>
public sealed class ModDownloadTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "nfs-moddl-" + Guid.NewGuid().ToString("N"));

    public ModDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    static string ManifestJson(string id, string version) =>
        "{ \"id\": \"" + id + "\", \"version\": \"" + version + "\", \"api\": \"^1.0\", \"kind\": \"data\", \"capabilities\": [\"cars\"] }";

    /// <summary>mods/&lt;id&gt;/&lt;version&gt; with a manifest, a small and a multi-chunk file and an empty one.</summary>
    string MakeMod(string modsName, string id, string version, int blob = 5000, byte seed = 1)
    {
        string d = Path.Combine(_root, modsName, id, version);
        Directory.CreateDirectory(Path.Combine(d, "data"));
        File.WriteAllText(Path.Combine(d, "mod.json"), ManifestJson(id, version));
        var bytes = new byte[blob];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + seed);
        File.WriteAllBytes(Path.Combine(d, "data", "blob.bin"), bytes);
        File.WriteAllBytes(Path.Combine(d, "data", "empty.txt"), []);
        return d;
    }

    string Dir(string name) { string d = Path.Combine(_root, name); Directory.CreateDirectory(d); return d; }

    static IReadOnlyList<LoadedMod> Load(string dir) => ModLoader.Load(dir, new ModDiagnostics());

    sealed class Rig : IDisposable
    {
        public SessionHarness H { get; }
        public NetModSet ServerSet { get; }
        public Rig(string serverDir, SessionServerOptions? extra = null, NetModSet? announce = null)
        {
            var mods = Load(serverDir);
            ServerSet = announce ?? NetModSet.FromMods(mods);
            H = new SessionHarness(21, (extra ?? new SessionServerOptions()) with { Mods = ServerSet, ModSources = mods });
        }

        public ModDownloadClient Client(string clientDir, bool? consent, NetModSet? expected = null, LinkProfile? link = null)
        {
            ModDownloadClient c = null!;
            INetClientTransport Open(INetClientHandler handler, byte[] hello)
            {
                var l = H.Net.AddClient(handler, link ?? new LinkProfile(10), link ?? new LinkProfile(10));
                l.Connect(hello);
                return l;
            }
            c = new ModDownloadClient(SessionHarness.Fingerprint, "dl", clientDir, expected ?? ServerSet, Open, () => H.Net.Now,
                new ModDownloadOptions { Consent = consent is { } v ? _ => v : null });
            c.Start();
            return c;
        }

        public bool RunUntilFinished(ModDownloadClient c, long micros = 30_000_000) => H.RunUntil(() => { c.Update(H.Net.Now); return c.Finished; }, micros);
        public void Dispose() => H.Dispose();
    }

    static string HashOf(string dir) => ModFingerprint.OfDirectory(dir);

    // ---------------- download ----------------

    [GameFact]
    public void MissingMod_IsDownloadedAndTheClientThenJoinsTheServer()
    {
        string serverDir = Dir("server");
        string src = MakeMod("server", "test.dl", "1.0.0", blob: 600_000);
        string clientDir = Dir("client");
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, true);

        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Done, dl.Phase);
        Assert.Null(dl.Error);
        string installed = Path.Combine(clientDir, "test.dl", "1.0.0");
        Assert.Equal(HashOf(src), HashOf(installed));
        Assert.Equal(1, dl.Installed);
        Assert.Equal(1, rig.H.Server.Stats.ModsSent);
        Assert.False(Directory.Exists(Path.Combine(clientDir, ".incoming")));

        // with the downloaded mod the client's hello is accepted (a player connection now)
        var mine = NetModSet.FromMods(Load(clientDir));
        Assert.True(mine.SameAs(rig.ServerSet));
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, "carol") { Mods = mine }.Encode();
        var carol = rig.H.Join("carol", hello: hello);
        Assert.True(rig.H.RunUntil(() => carol.Connected && carol.List != null, 1_000_000));
        Assert.Null(carol.Reject);
        Assert.True(rig.H.RunUntil(() => rig.H.Server.ModDownloadCount == 0, 1_000_000));
    }

    [GameFact]
    public void OtherVersion_IsDownloadedNextToTheOldOneWhichStaysUntouched()
    {
        string serverDir = Dir("server");
        string newSrc = MakeMod("server", "test.dl", "1.1.0", seed: 9);
        string clientDir = Dir("client");
        string old = MakeMod("client", "test.dl", "1.0.0", seed: 2);
        string oldHash = HashOf(old);
        string newer = MakeMod("client", "test.dl", "2.0.0", seed: 3);   // newer than the server's: the single-player game would take it
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, true);

        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Done, dl.Phase);
        Assert.Equal(HashOf(newSrc), HashOf(Path.Combine(clientDir, "test.dl", "1.1.0")));
        Assert.Equal(oldHash, HashOf(old));
        Assert.True(Directory.Exists(newer));
        // the newest version is still 2.0.0 for a local game; the network game loads exactly the server's version
        Assert.Equal("2.0.0", Assert.Single(Load(clientDir)).Manifest.Version);
        var session = ModSession.CreateExact(clientDir, rig.ServerSet.Entries.Select(e => (e.Id, e.Version)));
        Assert.False(session.Diagnostics.HasErrors);
        Assert.Equal("1.1.0", Assert.Single(session.Mods).Manifest.Version);
        Assert.True(NetModSet.FromMods(session.Mods).SameAs(rig.ServerSet));
        Assert.Null(ModSync.LoadServerSet(rig.ServerSet, clientDir, out _));
    }

    [GameFact]
    public void TheOfferIsShownBeforeAnythingIsRequested_AndTheOwnerDecidesLater()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0", blob: 4000);
        MakeMod("server", "test.two", "0.2.0");
        string clientDir = Dir("client");
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, null);   // no consent callback: the owner (a menu) answers

        Assert.True(rig.H.RunUntil(() => dl.Phase == ModDownloadPhase.AwaitingConsent, 5_000_000));
        Assert.Equal(["test.dl", "test.two"], dl.Items.Select(i => i.Id));
        Assert.All(dl.Items, i => Assert.True(i.Bytes > 0 && i.Files == 3));
        Assert.Contains("test.dl 1.0.0", dl.Items[0].Display);
        rig.H.Run(500_000);
        Assert.Equal(ModDownloadPhase.AwaitingConsent, dl.Phase);
        Assert.Equal(0, rig.H.Server.Stats.ModsSent);
        Assert.False(Directory.Exists(Path.Combine(clientDir, "test.dl")));

        dl.Accept();
        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Done, dl.Phase);
        Assert.Equal(2, dl.Installed);
        Assert.Equal(2, rig.H.Server.Stats.ModsSent);
    }

    [GameFact]
    public void DecliningInstallsNothingAndSaysWhatWasRefused()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        string clientDir = Dir("client");
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, false);

        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Declined, dl.Phase);
        Assert.Contains("test.dl 1.0.0", dl.Error);
        Assert.Empty(Directory.GetFileSystemEntries(clientDir));
        Assert.Equal(0, rig.H.Server.Stats.ModsSent);
        Assert.True(rig.H.RunUntil(() => rig.H.Server.ModDownloadCount == 0, 1_000_000));
    }

    [GameFact]
    public void ContentThatDoesNotMatchTheAnnouncedHashIsRejectedAndLeavesNothing()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        string clientDir = Dir("client");
        var lie = new NetModSet([new NetModEntry("test.dl", "1.0.0", Enumerable.Repeat((byte)7, 32).ToArray())]);   // the hello promised other content
        using var rig = new Rig(serverDir, announce: lie);
        var dl = rig.Client(clientDir, true);

        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Failed, dl.Phase);
        Assert.Contains("hash", dl.Error);
        Assert.Empty(Directory.GetFileSystemEntries(clientDir));
    }

    [GameFact]
    public void AnInstalledVersionWithOtherContentIsNeverOverwritten()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0", seed: 1);
        string clientDir = Dir("client");
        string mine = MakeMod("client", "test.dl", "1.0.0", seed: 5);
        string before = HashOf(mine);
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, true);

        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Failed, dl.Phase);
        Assert.Contains("different content", dl.Error);
        Assert.Equal(before, HashOf(mine));
        Assert.Equal(0, rig.H.Server.Stats.ModsSent);
    }

    [GameFact]
    public void AClientThatHasEverythingNeedsNoDownload()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        string clientDir = Dir("client");
        MakeMod("client", "test.dl", "1.0.0");
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, false);   // would decline, but there is nothing to ask
        Assert.True(rig.RunUntilFinished(dl));
        Assert.Equal(ModDownloadPhase.Done, dl.Phase);
        Assert.Empty(dl.Items);
    }

    [GameFact]
    public void ADownloadOverALinkWithLatencyCompletes()
    {
        string serverDir = Dir("server");
        string src = MakeMod("server", "test.dl", "1.0.0", blob: 200_000);
        string clientDir = Dir("client");
        using var rig = new Rig(serverDir);
        var dl = rig.Client(clientDir, true, link: new LinkProfile(40, 10));
        Assert.True(rig.RunUntilFinished(dl, 120_000_000));
        Assert.Equal(ModDownloadPhase.Done, dl.Phase);
        Assert.Equal(HashOf(src), HashOf(Path.Combine(clientDir, "test.dl", "1.0.0")));
    }

    // ---------------- server limits ----------------

    sealed class RawClient : INetClientHandler
    {
        public bool Connected, Disconnected;
        public NetReject? Reject;
        public readonly List<byte[]> Received = [];
        public INetClientTransport Link = null!;
        public void OnConnected() => Connected = true;
        public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data) => Received.Add(data.ToArray());
        public void OnDisconnected(byte[]? reason) { Disconnected = true; Reject = reason == null ? null : NetReject.TryDecode(reason); }
        public int Count(NetMessageType type) => Received.Count(b => NetHeader.Peek(b) == type);
    }

    static RawClient OpenRaw(SessionHarness h, string name = "raw", NetHelloPurpose purpose = NetHelloPurpose.ModDownload)
    {
        var c = new RawClient();
        var link = h.Net.AddClient(c, new LinkProfile(), new LinkProfile());
        c.Link = link;
        link.Connect(new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, name) { Purpose = purpose }.Encode());
        return c;
    }

    [GameFact]
    public void ServerWithoutDownloadableModsOrWithDownloadsOffRefuses()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        using (var none = new SessionHarness(1))
        {
            var c = OpenRaw(none);
            Assert.True(none.RunUntil(() => c.Disconnected, 1_000_000));
            Assert.Equal(NetRejectReason.ModDownloadRefused, c.Reject?.Reason);
        }
        using var off = new Rig(serverDir, new SessionServerOptions { AllowModDownloads = false });
        var c2 = OpenRaw(off.H);
        Assert.True(off.H.RunUntil(() => c2.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.ModDownloadRefused, c2.Reject?.Reason);
        Assert.Equal(0, off.H.Server.ConnectionCount);
    }

    [GameFact]
    public void DownloadConnectionsAreNoPlayersAndAreLimited()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        using var rig = new Rig(serverDir, new SessionServerOptions { MaxModDownloads = 2 });
        var a = OpenRaw(rig.H, "same");
        var b = OpenRaw(rig.H, "same");   // the same name is fine: it is no player
        var c = OpenRaw(rig.H, "third");
        Assert.True(rig.H.RunUntil(() => c.Disconnected && a.Connected && b.Connected, 1_000_000));
        Assert.Equal(NetRejectReason.ModDownloadRefused, c.Reject?.Reason);
        Assert.Equal(0, rig.H.Server.ConnectionCount);
        Assert.Equal(2, rig.H.Server.ModDownloadCount);
        Assert.Equal(1, a.Count(NetMessageType.ModOffer));
        // a normal player with the same name still joins
        var player = rig.H.Join("same", hello: new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, SessionHarness.Fingerprint, "same") { Mods = rig.ServerSet }.Encode());
        Assert.True(rig.H.RunUntil(() => player.Connected && player.List != null, 1_000_000));
    }

    [GameFact]
    public void UnknownRequestsAndForeignMessagesEndTheDownloadConnection()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        using var rig = new Rig(serverDir);
        var raw = OpenRaw(rig.H);
        Assert.True(rig.H.RunUntil(() => raw.Count(NetMessageType.ModOffer) == 1, 1_000_000));
        for (int i = 0; i < 3; i++) raw.Link.Send(NetChannel.ReliableOrdered, new NetModRequest("nobody.nothing", "9.9.9").Encode());
        Assert.True(rig.H.RunUntil(() => raw.Disconnected, 1_000_000));
        Assert.Equal(NetRejectReason.Malformed, raw.Reject?.Reason);
        Assert.True(rig.H.RunUntil(() => rig.H.Server.ModDownloadCount == 0, 1_000_000));

        var lobby = OpenRaw(rig.H, "x");
        Assert.True(rig.H.RunUntil(() => lobby.Count(NetMessageType.ModOffer) == 1, 1_000_000));
        for (int i = 0; i < 3; i++) lobby.Link.Send(NetChannel.ReliableOrdered, new NetGameListRequest().Encode());   // a session message of a player
        Assert.True(rig.H.RunUntil(() => lobby.Disconnected, 1_000_000));
        Assert.Equal(0, rig.H.Server.GameCount);
    }

    [GameFact]
    public void ASilentDownloadConnectionTimesOut()
    {
        string serverDir = Dir("server");
        MakeMod("server", "test.dl", "1.0.0");
        using var rig = new Rig(serverDir, new SessionServerOptions { ModDownloadIdleMicros = 2_000_000 });
        var raw = OpenRaw(rig.H);
        Assert.True(rig.H.RunUntil(() => raw.Disconnected, 5_000_000));
        Assert.Equal(NetRejectReason.Timeout, raw.Reject?.Reason);
    }

    // ---------------- a hostile server ----------------

    sealed class FakeTransport : INetClientTransport
    {
        public readonly List<byte[]> Sent = [];
        public bool Disconnected;
        public void Send(NetChannel channel, byte[] data) => Sent.Add(data);
        public void Disconnect() => Disconnected = true;
        public long RoundTripMicros => 0;
    }

    (ModDownloadClient Client, FakeTransport Transport, string Dir, byte[] Hash) Hostile(string[] files, NetModSize size)
    {
        string clientDir = Dir("hostile-client-" + Guid.NewGuid().ToString("N")[..6]);
        var transport = new FakeTransport();
        var entry = new NetModEntry("evil.mod", "1.0.0", Enumerable.Repeat((byte)3, 32).ToArray());
        var set = new NetModSet([entry]);
        var client = new ModDownloadClient(new byte[32], "dl", clientDir, set, (_, _) => transport, () => 0, new ModDownloadOptions { Consent = _ => true });
        client.Start();
        client.OnReceive(NetChannel.ReliableOrdered, new NetModOffer(set, [size]).Encode());
        return (client, transport, clientDir, entry.Hash);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/evil.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("con.txt")]
    [InlineData(".hidden")]
    [InlineData("a//b")]
    [InlineData("a/b/c/d/e/f/g/h/i.txt")]
    public void ADownloadedFileWithAnUnsafePathFailsTheDownload(string path)
    {
        var (client, _, dir, _) = Hostile([], new NetModSize(1, 5));
        Assert.Equal(ModDownloadPhase.Downloading, client.Phase);
        client.OnReceive(NetChannel.ReliableOrdered, new NetModFileStart(0, path, 5).Encode());
        Assert.Equal(ModDownloadPhase.Failed, client.Phase);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void DataBeyondTheDeclaredSizeAndOversizedOffersFailTheDownload()
    {
        var (client, _, dir, _) = Hostile([], new NetModSize(1, 5));
        client.OnReceive(NetChannel.ReliableOrdered, new NetModFileStart(0, "a.bin", 5).Encode());
        client.OnReceive(NetChannel.ReliableOrdered, new NetModChunk(0, 0, new byte[10]).Encode());
        Assert.Equal(ModDownloadPhase.Failed, client.Phase);
        Assert.Contains("longer than declared", client.Error);
        Assert.Empty(Directory.GetFileSystemEntries(dir));

        // a file over the 16 MB limit and a mod over the total limit are refused before any byte is stored
        var (big, _, bigDir, _) = Hostile([], new NetModSize(1, 5));
        big.OnReceive(NetChannel.ReliableOrdered, new NetModFileStart(0, "big.bin", ModPaths.MaxFileBytes + 1).Encode());
        Assert.Equal(ModDownloadPhase.Failed, big.Phase);
        Assert.Empty(Directory.GetFileSystemEntries(bigDir));

        var set = new NetModSet([new NetModEntry("evil.mod", "1.0.0", new byte[32])]);
        var tooBig = new NetModOffer(set, [new NetModSize(1, 5)]).Encode();
        // total above the limit: patch the last four bytes (bytes of the only mod)
        BitConverter.GetBytes((uint)(ModPaths.MaxModBytes + 1)).CopyTo(tooBig, tooBig.Length - 4);
        var t = new FakeTransport();
        var c2 = new ModDownloadClient(new byte[32], "dl", Dir("c2"), null, (_, _) => t, () => 0);
        c2.Start();
        c2.OnReceive(NetChannel.ReliableOrdered, tooBig);
        Assert.Equal(ModDownloadPhase.Failed, c2.Phase);
    }

    [Fact]
    public void ChunksOutOfOrderAndAFinishedModWithWrongContentFail()
    {
        var (client, _, dir, _) = Hostile([], new NetModSize(1, 5));
        client.OnReceive(NetChannel.ReliableOrdered, new NetModFileStart(0, "mod.json", 5).Encode());
        client.OnReceive(NetChannel.ReliableOrdered, new NetModChunk(0, 2, new byte[3]).Encode());   // offset gap
        Assert.Equal(ModDownloadPhase.Failed, client.Phase);

        var (c2, _, dir2, _) = Hostile([], new NetModSize(1, 5));
        c2.OnReceive(NetChannel.ReliableOrdered, new NetModFileStart(0, "mod.json", 5).Encode());
        c2.OnReceive(NetChannel.ReliableOrdered, new NetModChunk(0, 0, Encoding.ASCII.GetBytes("hello")).Encode());
        c2.OnReceive(NetChannel.ReliableOrdered, new NetModEnd("evil.mod", "1.0.0").Encode());
        Assert.Equal(ModDownloadPhase.Failed, c2.Phase);
        Assert.Contains("hash", c2.Error);
        Assert.Empty(Directory.GetFileSystemEntries(dir2));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    // ---------------- staging ----------------

    [Fact]
    public void Staging_InstallsAValidModAtomicallyAndNeverOverwrites()
    {
        string mods = Dir("stage");
        string content = ManifestJson("test.stage", "1.0.0");
        string Receive(ModStaging s, string json)
        {
            s.BeginFile("mod.json", json.Length);
            s.Write(Encoding.UTF8.GetBytes(json));
            s.EndFile();
            return Directory.GetDirectories(Path.Combine(mods, ".incoming")).Single();
        }
        string hash;
        using (var probe = new ModStaging(mods, "test.stage", "1.0.0"))
        {
            string tmp = Receive(probe, content);
            hash = ModFingerprint.OfDirectory(tmp);
        }
        Assert.False(Directory.Exists(Path.Combine(mods, ".incoming")));   // disposed without commit: removed

        var s1 = new ModStaging(mods, "test.stage", "1.0.0");
        Receive(s1, content);
        Assert.Null(s1.Commit(hash, 1, content.Length));
        Assert.True(File.Exists(Path.Combine(mods, "test.stage", "1.0.0", "mod.json")));
        Assert.False(Directory.Exists(Path.Combine(mods, ".incoming")));
        Assert.Equal("test.stage", Assert.Single(Load(mods)).Manifest.Id);

        var s2 = new ModStaging(mods, "test.stage", "1.0.0");
        Receive(s2, content);
        Assert.Contains("never overwritten", s2.Commit(hash, 1, content.Length));
        Assert.Equal(hash, HashOf(Path.Combine(mods, "test.stage", "1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(mods, ".incoming")));
    }

    [Fact]
    public void Staging_RejectsInvalidModsLimitsAndRepeatedFiles()
    {
        string mods = Dir("stage2");
        // an invalid manifest passes the hash but not the validation of a local mod
        using (var s = new ModStaging(mods, "test.bad", "1.0.0"))
        {
            s.BeginFile("mod.json", 5);
            s.Write(Encoding.ASCII.GetBytes("{ bad"));
            s.EndFile();
            string tmp = Directory.GetDirectories(Path.Combine(mods, ".incoming")).Single();
            Assert.Contains("invalid mod", s.Commit(ModFingerprint.OfDirectory(tmp), 1, 5));
        }
        Assert.False(Directory.Exists(Path.Combine(mods, "test.bad")));

        // the manifest of another id or version is no excuse
        string other = ManifestJson("other.id", "1.0.0");
        using (var s = new ModStaging(mods, "test.bad", "1.0.0"))
        {
            s.BeginFile("mod.json", other.Length);
            s.Write(Encoding.UTF8.GetBytes(other));
            s.EndFile();
            string tmp = Directory.GetDirectories(Path.Combine(mods, ".incoming")).Single();
            Assert.Contains("invalid mod", s.Commit(ModFingerprint.OfDirectory(tmp), 1, other.Length));
        }

        using var t = new ModStaging(mods, "test.limits", "1.0.0");
        Assert.Throws<InvalidDataException>(() => t.BeginFile("big.bin", ModPaths.MaxFileBytes + 1));
        t.BeginFile("a.txt", 1);
        Assert.Throws<InvalidDataException>(() => t.BeginFile("b.txt", 1));   // previous file still open
        t.Write([1]);
        t.EndFile();
        Assert.Throws<InvalidDataException>(() => t.BeginFile("A.TXT", 1));   // repeated (case-insensitive)
        for (int i = 1; i < ModPaths.MaxFilesPerMod; i++) { t.BeginFile($"f{i}.txt", 0); t.EndFile(); }
        Assert.Throws<InvalidDataException>(() => t.BeginFile("one-too-many.txt", 0));
        Assert.Throws<ArgumentException>(() => new ModStaging(mods, "../x", "1.0.0"));
        Assert.Throws<ArgumentException>(() => new ModStaging(mods, "a/b", "1.0.0"));
    }

    [Fact]
    public void ModLoader_IgnoresTheIncomingDirectoryAndLoadsExactVersions()
    {
        string mods = Dir("loader");
        MakeMod("loader", "test.ver", "1.0.0");
        MakeMod("loader", "test.ver", "2.0.0");
        Directory.CreateDirectory(Path.Combine(mods, ".incoming", "abc"));
        File.WriteAllText(Path.Combine(mods, ".incoming", "abc", "mod.json"), ManifestJson("x.y", "1.0.0"));
        var diag = new ModDiagnostics();
        Assert.Equal("2.0.0", Assert.Single(ModLoader.Load(mods, diag)).Manifest.Version);
        Assert.Empty(diag.Entries);
        Assert.Equal("1.0.0", ModLoader.LoadExact(mods, "test.ver", "1.0.0", diag)!.Manifest.Version);
        Assert.Null(ModLoader.LoadExact(mods, "test.ver", "3.0.0", diag));
        Assert.Null(ModLoader.LoadExact(mods, "..", "1.0.0", diag));
    }

    [Fact]
    public void Messages_RoundTripAndBadFieldsAreRejected()
    {
        var set = new NetModSet([new NetModEntry("a.b", "1.2.3", Enumerable.Repeat((byte)1, 32).ToArray())]);
        var hello = NetHello.Decode(new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, new byte[32], "x") { Mods = set, Purpose = NetHelloPurpose.ModDownload }.Encode());
        Assert.Equal(NetHelloPurpose.ModDownload, hello.Purpose);
        Assert.Equal(NetHelloPurpose.Play, NetHello.Decode(new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, new byte[32], "x").Encode()).Purpose);

        var chunk = new NetModChunk(3, 2048, [1, 2, 3]).Encode();
        var r = new WireReader(chunk);
        var decoded = NetModChunk.Decode(NetHeader.Read(ref r), ref r);
        Assert.Equal((3, 2048L), (decoded.Index, decoded.Offset));
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Data);
        Assert.Throws<ArgumentException>(() => new NetModChunk(0, 0, []).Encode());
        Assert.Throws<ArgumentException>(() => new NetModChunk(0, 0, new byte[NetModChunk.MaxData + 1]).Encode());
        var cut = chunk[..^1];
        Assert.Throws<NetProtocolException>(() => { var rr = new WireReader(cut); NetModChunk.Decode(NetHeader.Read(ref rr), ref rr); });
    }
}
