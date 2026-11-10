namespace NFS.Net.Protocol;

/// <summary>
/// Application version shared by the client and the server. It comes from the assembly info of NFS.Net
/// (<see cref="System.Reflection.AssemblyName.Version"/>), not from a hand-maintained protocol constant, so a release build
/// can be identified before gameplay starts. The protocol version (<see cref="NetProtocol.Version"/>) stays separate: a
/// wire-compatible fix keeps the same protocol number but changes the application version.
/// </summary>
public static class BuildInfo
{
    /// <summary>Four-component version of the NFS.Net assembly (build/revision default to 0).</summary>
    public static Version AppVersion { get; } = Normalize(typeof(BuildInfo).Assembly.GetName().Version);

    /// <summary>Version with all four components defined and negative build/revision replaced by 0.</summary>
    public static Version Normalize(Version? version) => new(
        version?.Major ?? 0, version?.Minor ?? 0, Math.Max(version?.Build ?? 0, 0), Math.Max(version?.Revision ?? 0, 0));

    /// <summary>Writes four little-endian 16-bit components. Components above 65535 do not fit a valid assembly version.</summary>
    public static void Write(WireWriter w, Version? version = null)
    {
        var v = Normalize(version ?? AppVersion);
        if (v.Major > ushort.MaxValue || v.Minor > ushort.MaxValue || v.Build > ushort.MaxValue || v.Revision > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(version), "application version components must fit 16 bits");
        w.U16(v.Major); w.U16(v.Minor); w.U16(v.Build); w.U16(v.Revision);
    }

    public static Version Read(ref WireReader r) => new(r.U16(), r.U16(), r.U16(), r.U16());
}
