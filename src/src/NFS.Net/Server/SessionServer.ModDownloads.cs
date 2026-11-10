using NFS.Game.Mods;
using NFS.Net.Protocol;

namespace NFS.Net.Server;

/// <summary>Mod download connections of the session server (openspec modding-api-cars 5.4): a hello with the purpose
/// <see cref="NetHelloPurpose.ModDownload"/> opens a connection that is no player. The server offers its mods (id, version, hash, files,
/// bytes) and streams every mod the client asks for, one at a time, in small chunks that respect the reliable backlog. The
/// connection ends when the client leaves, on a protocol error or after <see cref="SessionServerOptions.ModDownloadIdleMicros"/> of silence.</summary>
public sealed partial class SessionServer
{
    const int ChunksPerUpdate = 32;
    const int MaxDownloadBacklog = 48;
    const int MaxDownloadBad = 3;

    sealed class ModDownload(int id, long now)
    {
        public int Id { get; } = id;
        public long LastActivity = now;
        public int Bad, Requests;
        public ModPack? Pack;
        public int FileIndex = -1;
        public FileStream? Stream;
        public long Offset;
        public bool NeedStart;

        public void CloseFile() { Stream?.Dispose(); Stream = null; }
    }

    readonly Dictionary<int, ModDownload> _downloads = [];
    Dictionary<(string Id, string Version), ModPack>? _packs;

    bool ModDownloadsActive => _downloads.Values.Any(d => d.Pack != null);

    /// <summary>The packs of every mod of <see cref="SessionServerOptions.Mods"/> (built once; mods are frozen), or null when one of them
    /// cannot be offered (the reason is logged), so a client never gets a partial set.</summary>
    Dictionary<(string Id, string Version), ModPack>? Packs()
    {
        if (_packs != null) return _packs.Count == 0 && _options.Mods.Entries.Count > 0 ? null : _packs;
        var packs = new Dictionary<(string, string), ModPack>();
        foreach (var entry in _options.Mods.Entries)
        {
            var source = _options.ModSources.FirstOrDefault(m => m.Manifest.Id == entry.Id && m.Manifest.Version == entry.Version);
            if (source == null) { Log($"mod downloads off: {entry.Display} has no source directory"); packs.Clear(); break; }
            if (!ModPack.TryCreate(source, out var pack, out string? error))
            {
                Log($"mod downloads off: {entry.Display} cannot be offered ({error})");
                packs.Clear();
                break;
            }
            packs[(entry.Id, entry.Version)] = pack!;
        }
        _packs = packs;
        return _packs.Count == 0 && _options.Mods.Entries.Count > 0 ? null : _packs;
    }

    /// <summary>Connection request of a download connection (the versions and the application were checked).</summary>
    byte[]? CheckModDownload()
    {
        if (_stopping || _stopped) return Reject(NetRejectReason.ServerShutdown);
        if (!_options.AllowModDownloads || _options.Mods.Entries.Count == 0 || _downloads.Count >= _options.MaxModDownloads || Packs() == null)
        {
            Stats.ModDownloadRefused++;
            return Reject(NetRejectReason.ModDownloadRefused);
        }
        return null;
    }

    void AcceptModDownload(int connection)
    {
        if (_stopping || _stopped || _downloads.ContainsKey(connection) || _connections.ContainsKey(connection) || Packs() == null)
        {
            RejectConnection(connection, NetRejectReason.ModDownloadRefused);
            return;
        }
        if (Clock != null) _now = Clock();
        var download = new ModDownload(connection, _now);
        _downloads[connection] = download;
        Stats.ModDownloadConnections++;
        var packs = Packs()!;
        var sizes = _options.Mods.Entries.Select(e => new NetModSize(packs[(e.Id, e.Version)].Files.Count, packs[(e.Id, e.Version)].TotalBytes)).ToArray();
        _transport.Send(connection, NetChannel.ReliableOrdered, new NetModOffer(_options.Mods, sizes).Encode());
        Log($"conn {connection}: mod download, offering {_options.Mods.Entries.Count} mods");
    }

    void ReceiveModDownload(ModDownload d, ReadOnlySpan<byte> data)
    {
        if (Clock != null) _now = Clock();
        d.LastActivity = _now;
        Stats.Messages++;
        try
        {
            if (data.Length > NetProtocol.MaxReliableMessage) throw new NetProtocolException("oversized message");
            var r = new WireReader(data);
            var h = NetHeader.Read(ref r);
            if (h.Type != NetMessageType.ModRequest) throw new NetProtocolException($"message {h.Type} is not allowed on a download connection");
            var request = NetModRequest.Decode(h, ref r);
            if (d.Pack != null) throw new NetProtocolException("a mod is still being sent");
            if (++d.Requests > NetModSet.MaxMods * 2) throw new NetProtocolException("too many requests");
            if (Packs() is not { } packs || !packs.TryGetValue((request.Id, request.Version), out var pack))
                throw new NetProtocolException($"unknown mod {request.Id} {request.Version}");
            d.Pack = pack;
            d.FileIndex = -1;
            d.NeedStart = true;
            Log($"conn {d.Id}: sending {request.Id} {request.Version} ({pack.Files.Count} files, {pack.TotalBytes} B)");
        }
        catch (NetProtocolException e)
        {
            Stats.Malformed++;
            Log($"conn {d.Id}: malformed download message ({e.Message})");
            if (++d.Bad >= MaxDownloadBad) CloseModDownload(d, NetRejectReason.Malformed);
        }
    }

    void UpdateModDownloads()
    {
        if (_downloads.Count == 0) return;
        foreach (var d in _downloads.Values.ToArray())
        {
            if (_now - d.LastActivity > _options.ModDownloadIdleMicros) { CloseModDownload(d, NetRejectReason.Timeout); continue; }
            if (d.Pack == null) continue;
            try { Pump(d); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log($"conn {d.Id}: cannot read the mod ({e.Message})");
                CloseModDownload(d, NetRejectReason.RaceClosed);
            }
        }
    }

    void Pump(ModDownload d)
    {
        var pack = d.Pack!;
        for (int sent = 0; sent < ChunksPerUpdate && _downloads.ContainsKey(d.Id); sent++)
        {
            if (_transport.PendingReliable(d.Id) > MaxDownloadBacklog) return;
            if (d.Stream == null)
            {
                if (++d.FileIndex >= pack.Files.Count) { FinishMod(d); return; }
                var file = pack.Files[d.FileIndex];
                string full = Path.Combine(pack.Mod.Dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                d.Stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (d.Stream.Length != file.Size) throw new IOException($"'{file.Path}' changed size");
                d.Offset = 0;
                _transport.Send(d.Id, NetChannel.ReliableOrdered, new NetModFileStart(d.FileIndex, file.Path, file.Size).Encode());
                d.LastActivity = _now;
                continue;
            }
            long left = d.Stream.Length - d.Offset;
            if (left <= 0) { d.CloseFile(); continue; }
            var buffer = new byte[(int)Math.Min(left, NetModChunk.MaxData)];
            d.Stream.ReadExactly(buffer);
            _transport.Send(d.Id, NetChannel.ReliableOrdered, new NetModChunk(d.FileIndex, d.Offset, buffer).Encode());
            d.Offset += buffer.Length;
            Stats.ModBytesSent += buffer.Length;
            d.LastActivity = _now;
        }
    }

    void FinishMod(ModDownload d)
    {
        var mod = d.Pack!.Mod.Manifest;
        d.Pack = null;
        d.CloseFile();
        d.LastActivity = _now;
        _transport.Send(d.Id, NetChannel.ReliableOrdered, new NetModEnd(mod.Id, mod.Version).Encode());
        Stats.ModsSent++;
        Log($"conn {d.Id}: sent {mod.Id} {mod.Version}");
    }

    void EndModDownload(int connection)
    {
        if (!_downloads.Remove(connection, out var d)) return;
        d.CloseFile();
        Log($"conn {connection}: mod download ended");
    }

    void CloseModDownload(ModDownload d, NetRejectReason reason)
    {
        if (!_downloads.Remove(d.Id)) return;
        d.CloseFile();
        _transport.Disconnect(d.Id, new NetReject(reason).Encode());
        Stats.Disconnects++;
        Log($"conn {d.Id}: mod download closed ({reason})");
    }

    void CloseModDownloads(NetRejectReason reason)
    {
        foreach (var d in _downloads.Values.ToArray()) CloseModDownload(d, reason);
    }

    /// <summary>Number of open download connections (tests, diagnostics).</summary>
    public int ModDownloadCount => _downloads.Count;
}
