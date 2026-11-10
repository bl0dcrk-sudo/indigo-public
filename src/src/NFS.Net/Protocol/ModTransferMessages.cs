namespace NFS.Net.Protocol;

/// <summary>Files and bytes of one mod of a <see cref="NetModOffer"/> (the client shows the size before it asks for anything).</summary>
public readonly record struct NetModSize(int Files, long Bytes);

/// <summary>Server → client (mod download connection, session header): the mods the server plays with, with their content hashes
/// (<see cref="NetModSet"/>, the same as in the reject) and, in the same order, how many files and bytes each has. Sent once, right
/// after the connection is accepted.</summary>
public sealed record NetModOffer(NetModSet Mods, IReadOnlyList<NetModSize> Sizes)
{
    public byte[] Encode()
    {
        if (Sizes.Count != Mods.Entries.Count) throw new ArgumentException("one size per mod", nameof(Sizes));
        var w = new WireWriter(NetHeader.Size + NetModSet.MaxWireSize + NetModSet.MaxMods * 6);
        NetSessionProtocol.SessionHeader(NetMessageType.ModOffer).Write(w);
        Mods.Write(w);
        foreach (var s in Sizes) { w.U16(s.Files); w.U32(checked((uint)s.Bytes)); }
        return w.ToArray();
    }

    public static NetModOffer Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.ModOffer);
        var mods = NetModSet.Read(ref r);
        var sizes = new List<NetModSize>();
        foreach (var _ in mods.Entries)
        {
            int files = r.U16();
            long bytes = r.U32();
            if (files == 0 || files > NFS.Game.Mods.ModPaths.MaxFilesPerMod || bytes > NFS.Game.Mods.ModPaths.MaxModBytes)
                throw new NetProtocolException("mod offer over the transfer limits");
            sizes.Add(new NetModSize(files, bytes));
        }
        r.End();
        return new NetModOffer(mods, sizes);
    }
}

/// <summary>Client → server: send this mod version (one of the offer, one request at a time).</summary>
public sealed record NetModRequest(string Id, string Version)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size + 2 + NetModSet.MaxIdLength + NetModSet.MaxVersionLength);
        NetSessionProtocol.SessionHeader(NetMessageType.ModRequest).Write(w);
        w.Ascii(Id, NetModSet.MaxIdLength);
        w.Ascii(Version, NetModSet.MaxVersionLength);
        return w.ToArray();
    }

    public static NetModRequest Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.ModRequest);
        var m = new NetModRequest(r.Ascii(NetModSet.MaxIdLength), r.Ascii(NetModSet.MaxVersionLength));
        r.End();
        return m;
    }
}

/// <summary>Server → client: the next file of the requested mod (index in the order of the content hash) with its path and size.</summary>
public sealed record NetModFileStart(int Index, string Path, long Size)
{
    public const int MaxPathLength = 128;

    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size + 2 + 1 + MaxPathLength + 4);
        NetSessionProtocol.SessionHeader(NetMessageType.ModFileStart).Write(w);
        w.U16(Index);
        w.Ascii(Path, MaxPathLength);
        w.U32(checked((uint)Size));
        return w.ToArray();
    }

    public static NetModFileStart Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.ModFileStart);
        var m = new NetModFileStart(r.U16(), r.Ascii(MaxPathLength), r.U32());
        r.End();
        return m;
    }
}

/// <summary>Server → client: bytes of the current file at <see cref="Offset"/> (in order, up to <see cref="MaxData"/> per message).</summary>
public sealed record NetModChunk(int Index, long Offset, byte[] Data)
{
    public const int MaxData = 1024;

    public byte[] Encode()
    {
        if (Data.Length is 0 or > MaxData) throw new ArgumentException($"a chunk carries 1..{MaxData} bytes", nameof(Data));
        var w = new WireWriter(NetHeader.Size + 2 + 4 + 2 + MaxData);
        NetSessionProtocol.SessionHeader(NetMessageType.ModChunk).Write(w);
        w.U16(Index);
        w.U32(checked((uint)Offset));
        w.U16(Data.Length);
        w.Bytes(Data);
        return w.ToArray();
    }

    public static NetModChunk Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.ModChunk);
        int index = r.U16();
        long offset = r.U32();
        int n = r.U16();
        if (n is 0 or > MaxData) throw new NetProtocolException("bad chunk length");
        var m = new NetModChunk(index, offset, r.Bytes(n).ToArray());
        r.End();
        return m;
    }
}

/// <summary>Server → client: every file of the requested mod was sent.</summary>
public sealed record NetModEnd(string Id, string Version)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size + 2 + NetModSet.MaxIdLength + NetModSet.MaxVersionLength);
        NetSessionProtocol.SessionHeader(NetMessageType.ModEnd).Write(w);
        w.Ascii(Id, NetModSet.MaxIdLength);
        w.Ascii(Version, NetModSet.MaxVersionLength);
        return w.ToArray();
    }

    public static NetModEnd Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.ModEnd);
        var m = new NetModEnd(r.Ascii(NetModSet.MaxIdLength), r.Ascii(NetModSet.MaxVersionLength));
        r.End();
        return m;
    }
}
