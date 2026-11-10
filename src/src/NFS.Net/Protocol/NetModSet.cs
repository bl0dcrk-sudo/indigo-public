using System.Security.Cryptography;
using System.Text;
using NFS.Game.Mods;

namespace NFS.Net.Protocol;

/// <summary>One mod of a <see cref="NetModSet"/>: id, exact version (<c>mods/&lt;id&gt;/&lt;version&gt;/</c>) and SHA-256 of its content
/// (<see cref="ModFingerprint"/>).</summary>
public sealed record NetModEntry(string Id, string Version, byte[] Hash)
{
    public string Display => $"{Id} {Version}";
    public bool Equals(NetModEntry? o) => o != null && Id == o.Id && Version == o.Version && Hash.AsSpan().SequenceEqual(o.Hash);
    public override int GetHashCode() => HashCode.Combine(Id, Version, Hash.Length > 0 ? Hash[0] : 0);
}

/// <summary>The data mods that take part in the simulation of a peer (openspec modding-api-cars D9): sorted by id, then version. The
/// handshake carries <see cref="Fingerprint"/> (SHA-256 over the sorted <c>id@version:hash</c> lines; for no mods a constant) and the
/// list itself, so a mismatch can name the missing or different mod on both sides.</summary>
public sealed class NetModSet
{
    public const int MaxMods = 32, MaxIdLength = 64, MaxVersionLength = 24;

    public static NetModSet Empty { get; } = new([]);

    public IReadOnlyList<NetModEntry> Entries { get; }
    public byte[] Fingerprint { get; }

    public NetModSet(IEnumerable<NetModEntry> entries)
    {
        Entries = entries.OrderBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Version, StringComparer.Ordinal).ToList();
        if (Entries.Count > MaxMods) throw new ArgumentException($"at most {MaxMods} mods", nameof(entries));
        foreach (var e in Entries)
            if (e.Hash.Length != 32 || e.Id.Length is 0 or > MaxIdLength || e.Version.Length is 0 or > MaxVersionLength)
                throw new ArgumentException($"invalid mod entry '{e.Id}'", nameof(entries));
        var sb = new StringBuilder();
        foreach (var e in Entries) sb.Append(e.Id).Append('@').Append(e.Version).Append(':').Append(Convert.ToHexString(e.Hash)).Append('\n');
        Fingerprint = SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>Data mods of a loaded session (mods are frozen after startup, hashes are cached).</summary>
    public static NetModSet FromMods(IEnumerable<LoadedMod> mods) =>
        new(mods.Select(m => new NetModEntry(m.Manifest.Id, m.Manifest.Version, Convert.FromHexString(ModFingerprint.Of(m)))));

    public bool SameAs(NetModSet o) => Fingerprint.AsSpan().SequenceEqual(o.Fingerprint);

    /// <summary>Human-readable difference between this (local) set and <paramref name="remote"/>; empty when equal. Names id and version
    /// of every mod that the remote side has and this one lacks (or has in another version/content) and vice versa.</summary>
    public string Diff(NetModSet remote, string localName, string remoteName)
    {
        var parts = new List<string>();
        foreach (var r in remote.Entries.Where(r => !Entries.Contains(r)))
            parts.Add(Entries.Any(l => l.Id == r.Id && l.Version == r.Version) ? $"{r.Display} has different content on {remoteName}"
                : Entries.Any(l => l.Id == r.Id) ? $"{r.Display} missing on {localName} (it has {string.Join("/", Entries.Where(l => l.Id == r.Id).Select(l => l.Version))})"
                : $"{r.Display} missing on {localName}");
        foreach (var l in Entries.Where(l => !remote.Entries.Contains(l) && remote.Entries.All(r => r.Id != l.Id)))
            parts.Add($"{l.Display} not on {remoteName}");
        return parts.Count == 0 ? "" : $"mods differ: {string.Join("; ", parts)}";
    }

    public void Write(WireWriter w)
    {
        w.Bytes(Fingerprint);
        w.U8(Entries.Count);
        foreach (var e in Entries) { w.Ascii(e.Id, MaxIdLength); w.Ascii(e.Version, MaxVersionLength); w.Bytes(e.Hash); }
    }

    public static int MaxWireSize => 32 + 1 + MaxMods * (1 + MaxIdLength + 1 + MaxVersionLength + 32);

    /// <summary>Reads a set; the transmitted fingerprint must match the list, otherwise the message is malformed.</summary>
    public static NetModSet Read(ref WireReader r)
    {
        var fingerprint = r.Bytes(32).ToArray();
        int n = r.U8();
        if (n > MaxMods) throw new NetProtocolException("too many mods");
        var list = new List<NetModEntry>(n);
        for (int i = 0; i < n; i++)
        {
            string id = r.Ascii(MaxIdLength), version = r.Ascii(MaxVersionLength);
            if (id.Length == 0 || version.Length == 0) throw new NetProtocolException("empty mod id or version");
            list.Add(new NetModEntry(id, version, r.Bytes(32).ToArray()));
        }
        var set = new NetModSet(list);
        if (!set.Fingerprint.AsSpan().SequenceEqual(fingerprint)) throw new NetProtocolException("mods fingerprint does not match the list");
        return set;
    }
}
