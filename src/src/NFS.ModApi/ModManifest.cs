namespace NFS.ModApi;

/// <summary>Kind of a mod. Only <see cref="Data"/> mods are loaded.</summary>
public enum ModKind { Data, Code }

/// <summary>Content of <c>mod.json</c>. <see cref="ApiRange"/> is e.g. <c>^1.0</c>.</summary>
public sealed record ModManifest(string Id, string Version, string ApiRange, ModKind Kind, IReadOnlyList<string> Capabilities);
