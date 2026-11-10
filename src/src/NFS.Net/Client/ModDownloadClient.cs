using NFS.Game.Mods;
using NFS.ModApi;
using NFS.Net.Protocol;
using NFS.Net.Transport;

namespace NFS.Net.Client;

/// <summary>One mod the server offers and this client lacks: what will be downloaded (shown to the user before anything is asked for).</summary>
public sealed record ModDownloadItem(string Id, string Version, int Files, long Bytes, byte[] Hash)
{
    public string Display => $"{Id} {Version} ({FormatBytes(Bytes)}, {Files} files)";

    public static string FormatBytes(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";
}

public enum ModDownloadPhase
{
    Idle,
    /// <summary>The download connection is being opened; waiting for the offer of the server.</summary>
    Connecting,
    /// <summary>The offer is known; <see cref="ModDownloadClient.Items"/> says what would be downloaded. <see cref="ModDownloadClient.Accept"/> or <see cref="ModDownloadClient.Decline"/>.</summary>
    AwaitingConsent,
    Downloading,
    /// <summary>Everything the server plays with is installed (or was already).</summary>
    Done,
    /// <summary>The user declined (<see cref="ModDownloadClient.Error"/> says so); nothing was installed.</summary>
    Declined,
    Failed,
}

public sealed record ModDownloadOptions
{
    /// <summary>Decides at once when the offer arrives (true = download, false = decline). Null = the owner calls
    /// <see cref="ModDownloadClient.Accept"/> / <see cref="ModDownloadClient.Decline"/> later (a menu asks the user).</summary>
    public Func<IReadOnlyList<ModDownloadItem>, bool>? Consent { get; init; }
    /// <summary>No message from the server for this long (while connecting or downloading) fails the download.</summary>
    public long TimeoutMicros { get; init; } = 30_000_000;
}

/// <summary>
/// Client of the mod download (openspec modding-api-cars 5.4): opens a connection with the hello purpose
/// <see cref="NetHelloPurpose.ModDownload"/>, receives the offer, works out which mods are missing locally, asks the user (callback or
/// <see cref="Accept"/>), requests them one at a time and installs each with <see cref="ModStaging"/> (limits and validation of a local mod,
/// hash of the offer, atomic move to <c>mods/&lt;id&gt;/&lt;version&gt;/</c>, an installed version is never overwritten). Headless like
/// <see cref="SessionClient"/>: the caller feeds the transport and calls <see cref="Update"/>.
/// </summary>
public sealed class ModDownloadClient : INetClientHandler
{
    readonly byte[] _fingerprint;
    readonly string _name, _modsDir;
    readonly NetModSet? _expected;
    readonly SessionClient.OpenTransport _open;
    readonly Func<long> _clock;
    readonly ModDownloadOptions _options;
    readonly Action<string>? _log;
    INetClientTransport? _transport;
    readonly List<ModDownloadItem> _items = [];
    int _next;
    ModStaging? _staging;
    ModDownloadItem? _current;
    int _fileIndex;
    long _fileOffset;
    long _lastActivity;
    bool _offerSeen;

    /// <param name="expected">The mods of the server as the reject of the session connection listed them; the offer must equal it
    /// (null = accept the offer as it is).</param>
    public ModDownloadClient(byte[] dataFingerprint, string name, string modsDir, NetModSet? expected, SessionClient.OpenTransport open,
        Func<long> clock, ModDownloadOptions? options = null, Action<string>? log = null)
    {
        _fingerprint = dataFingerprint;
        _name = name;
        _modsDir = modsDir;
        _expected = expected;
        _open = open;
        _clock = clock;
        _options = options ?? new ModDownloadOptions();
        _log = log;
    }

    public ModDownloadPhase Phase { get; private set; }
    public bool Finished => Phase is ModDownloadPhase.Done or ModDownloadPhase.Declined or ModDownloadPhase.Failed;
    /// <summary>The mods that are missing here (known from the offer, empty before it).</summary>
    public IReadOnlyList<ModDownloadItem> Items => _items;
    /// <summary>The server's mods from the offer (null before it).</summary>
    public NetModSet? ServerMods { get; private set; }
    /// <summary>Readable reason of <see cref="ModDownloadPhase.Failed"/> / <see cref="ModDownloadPhase.Declined"/>.</summary>
    public string? Error { get; private set; }
    public NetReject? Reject { get; private set; }
    public long BytesReceived { get; private set; }
    public int Installed { get; private set; }

    public void Start()
    {
        if (Phase != ModDownloadPhase.Idle) return;
        Phase = ModDownloadPhase.Connecting;
        _lastActivity = _clock();
        var hello = new NetHello(NetProtocol.Version, NetProtocol.PhysicsVersion, _fingerprint, _name) { Purpose = NetHelloPurpose.ModDownload }.Encode();
        _transport = _open(this, hello);
    }

    public void Update(long now)
    {
        if (Phase is ModDownloadPhase.Connecting or ModDownloadPhase.Downloading && now - _lastActivity > _options.TimeoutMicros)
            Fail("the server did not answer in time");
    }

    public void Accept()
    {
        if (Phase != ModDownloadPhase.AwaitingConsent) return;
        Phase = ModDownloadPhase.Downloading;
        _lastActivity = _clock();
        RequestNext();
    }

    public void Decline()
    {
        if (Phase != ModDownloadPhase.AwaitingConsent) return;
        Phase = ModDownloadPhase.Declined;
        Error = $"downloading {string.Join(", ", _items.Select(i => i.Display))} was declined";
        _log?.Invoke($"mod download declined: {Error}");
        _transport?.Disconnect();
    }

    // ---------------- transport ----------------

    public void OnConnected() { }

    public void OnDisconnected(byte[]? reason)
    {
        Reject = reason == null ? null : NetReject.TryDecode(reason);
        if (Finished) return;
        string why = Reject?.Reason switch
        {
            NetRejectReason.ModDownloadRefused => "the server does not offer its mods for download",
            NetRejectReason.ModsMismatch => "the server does not support mod downloads",
            { } r when r != NetRejectReason.None => $"the server closed the connection ({r})",
            _ => "the connection to the server was lost",
        };
        Fail(why);
    }

    public void OnReceive(NetChannel channel, ReadOnlySpan<byte> data)
    {
        if (Finished) return;
        _lastActivity = _clock();
        try
        {
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            switch (h.Type)
            {
                case NetMessageType.ModOffer: OnOffer(NetModOffer.Decode(h, ref r)); break;
                case NetMessageType.ModFileStart: OnFileStart(NetModFileStart.Decode(h, ref r)); break;
                case NetMessageType.ModChunk: OnChunk(NetModChunk.Decode(h, ref r)); break;
                case NetMessageType.ModEnd: OnEnd(NetModEnd.Decode(h, ref r)); break;
                default: throw new NetProtocolException($"unexpected message {h.Type}");
            }
        }
        catch (NetProtocolException e) { Fail($"bad message from the server: {e.Message}"); }
        catch (ArgumentException e) { Fail($"unusable mod name: {e.Message}"); }
        catch (InvalidDataException e) { Fail($"the server sent unacceptable mod data: {e.Message}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail($"cannot store the mod: {e.Message}"); }
    }

    // ---------------- protocol ----------------

    void OnOffer(NetModOffer offer)
    {
        if (_offerSeen || Phase != ModDownloadPhase.Connecting) throw new NetProtocolException("unexpected offer");
        _offerSeen = true;
        if (_expected != null && !_expected.SameAs(offer.Mods)) { Fail("the mods of the server changed in the meantime"); return; }
        ServerMods = offer.Mods;
        var conflicts = new List<string>();
        for (int i = 0; i < offer.Mods.Entries.Count; i++)
        {
            var e = offer.Mods.Entries[i];
            if (!ModSync.IsSafeEntry(e)) { Fail($"the server offers a mod with an unusable name ({e.Id} {e.Version})"); return; }
            string dir = Path.Combine(_modsDir, e.Id, e.Version);
            if (!Directory.Exists(dir))
            {
                _items.Add(new ModDownloadItem(e.Id, e.Version, offer.Sizes[i].Files, offer.Sizes[i].Bytes, e.Hash));
                continue;
            }
            // an installed version is never overwritten: it must already be the server's content
            if (!string.Equals(ModFingerprint.OfDirectory(dir), Convert.ToHexString(e.Hash), StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"{e.Id} {e.Version} is installed with different content than on the server (remove or rename '{dir}')");
        }
        if (conflicts.Count > 0) { Fail(string.Join("; ", conflicts)); return; }
        if (_items.Count == 0)
        {
            Phase = ModDownloadPhase.Done;
            _transport?.Disconnect();
            return;
        }
        Phase = ModDownloadPhase.AwaitingConsent;
        _log?.Invoke($"mod download: the server offers {string.Join(", ", _items.Select(i => i.Display))}");
        if (_options.Consent is { } consent)
        {
            if (consent(_items)) Accept();
            else Decline();
        }
    }

    void RequestNext()
    {
        if (_next >= _items.Count)
        {
            Phase = ModDownloadPhase.Done;
            _log?.Invoke($"mod download: {Installed} mods installed");
            _transport?.Disconnect();
            return;
        }
        _current = _items[_next++];
        _staging?.Dispose();
        _staging = new ModStaging(_modsDir, _current.Id, _current.Version);
        _fileIndex = -1;
        _transport!.Send(NetChannel.ReliableOrdered, new NetModRequest(_current.Id, _current.Version).Encode());
        _log?.Invoke($"mod download: requesting {_current.Display}");
    }

    void OnFileStart(NetModFileStart m)
    {
        if (Phase != ModDownloadPhase.Downloading || _staging == null || _current == null) throw new NetProtocolException("unexpected file");
        if (_staging.FileInProgress) throw new InvalidDataException("the previous file is incomplete");
        if (m.Index != _fileIndex + 1 || m.Index >= _current.Files) throw new InvalidDataException("file out of order");
        _fileIndex = m.Index;
        _fileOffset = 0;
        _staging.BeginFile(m.Path, m.Size);
        if (m.Size == 0) _staging.EndFile();
    }

    void OnChunk(NetModChunk m)
    {
        if (Phase != ModDownloadPhase.Downloading || _staging is not { FileInProgress: true } || m.Index != _fileIndex || m.Offset != _fileOffset)
            throw new InvalidDataException("chunk out of order");
        _staging.Write(m.Data);
        _fileOffset += m.Data.Length;
        BytesReceived += m.Data.Length;
        if (_staging.FileComplete) _staging.EndFile();
    }

    void OnEnd(NetModEnd m)
    {
        if (Phase != ModDownloadPhase.Downloading || _staging == null || _current == null || m.Id != _current.Id || m.Version != _current.Version)
            throw new InvalidDataException("unexpected end of a mod");
        string? error = _staging.Commit(Convert.ToHexString(_current.Hash), _current.Files, _current.Bytes);
        _staging = null;
        if (error != null) { Fail($"{_current.Id} {_current.Version}: {error}"); return; }
        Installed++;
        _log?.Invoke($"mod download: installed {_current.Id} {_current.Version}");
        RequestNext();
    }

    void Fail(string message)
    {
        if (Finished) return;
        Phase = ModDownloadPhase.Failed;
        Error = message;
        _staging?.Dispose();
        _staging = null;
        _log?.Invoke($"mod download failed: {message}");
        _transport?.Disconnect();
    }
}

/// <summary>What a client does with the mod set of a server it cannot join as it is (openspec modding-api-cars 5.4): find what is
/// missing locally, switch the running session to exactly the server's versions.</summary>
public static class ModSync
{
    /// <summary>Directory mods are installed to: the one of the running session, else the default one (<c>mods/</c> next to the exe or of the repo).</summary>
    public static string ModsDir() =>
        ModSession.Current.ModsDir is { Length: > 0 } dir ? dir : ModPaths.ResolveModsDir(null, AppContext.BaseDirectory);

    /// <summary>Id and version of a mod that comes from the network become directory names: only plain names are accepted.</summary>
    public static bool IsSafeEntry(NetModEntry e) =>
        !e.Id.Contains('/') && !e.Version.Contains('/') && ModPaths.IsSafeRelativeName(e.Id, out _) && ModPaths.IsSafeRelativeName(e.Version, out _);

    /// <summary>Mods of <paramref name="server"/> that are not installed in <paramref name="modsDir"/> (same id and version), and the
    /// ones installed with other content (cannot be fixed by a download: an installed version is never overwritten).</summary>
    public static (List<NetModEntry> Missing, List<string> Conflicts) Plan(NetModSet server, string modsDir)
    {
        var missing = new List<NetModEntry>();
        var conflicts = new List<string>();
        foreach (var e in server.Entries)
        {
            if (!IsSafeEntry(e)) { conflicts.Add($"{e.Id} {e.Version} has an unusable name"); continue; }
            string dir = Path.Combine(modsDir, e.Id, e.Version);
            if (!Directory.Exists(dir)) { missing.Add(e); continue; }
            if (!string.Equals(ModFingerprint.OfDirectory(dir), Convert.ToHexString(e.Hash), StringComparison.OrdinalIgnoreCase))
                conflicts.Add($"{e.Id} {e.Version} is installed with different content than on the server");
        }
        return (missing, conflicts);
    }

    /// <summary>Loads exactly the server's versions from <paramref name="modsDir"/> (each must hash like the server's). Returns an error
    /// text or null; <paramref name="session"/> is the result on success.</summary>
    public static string? LoadServerSet(NetModSet server, string modsDir, out ModSession? session)
    {
        session = null;
        var loaded = ModSession.CreateExact(modsDir, server.Entries.Select(e => (e.Id, e.Version)));
        if (loaded.Diagnostics.HasErrors)
            return string.Join("; ", loaded.Diagnostics.Entries.Where(d => d.Severity == ModDiagnosticSeverity.Error).Select(d => $"[{d.ModId}] {d.Message}"));
        var mine = NetModSet.FromMods(loaded.Mods);
        if (!mine.SameAs(server)) return mine.Diff(server, "you", "the server") is { Length: > 0 } diff ? diff : "mods differ from the server's";
        session = loaded;
        return null;
    }

    /// <summary>Makes the server's versions (<see cref="LoadServerSet"/>) the mods of the running session for the network game; the set
    /// of the start returns with <see cref="ModSession.RestoreStartup"/>. Returns an error text or null; on an error nothing changes.</summary>
    public static string? UseServerSet(NetModSet server, string modsDir)
    {
        if (LoadServerSet(server, modsDir, out var session) is { } error) return error;
        ModSession.UseForNetwork(session!);
        return null;
    }
}
