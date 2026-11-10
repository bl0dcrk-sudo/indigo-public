using Xunit;

namespace NFS.Tests.Data;

/// <summary>Repo <c>mods/</c> directory – the private mod repository (<c>mods/indigo.duster</c>, openspec modding-api-cars).
/// It lives next to <c>src/</c>, so the public source repo does not have it; tests that need the Duster mod use
/// <see cref="ModFactAttribute"/>/<see cref="ModTheoryAttribute"/> and are skipped there instead of failing (the same way the
/// data tests skip without <c>game_src/NFS2SE</c>, see <see cref="GameData"/>).</summary>
internal static class ModRepo
{
    public const string SkipReason = "mods/indigo.duster (private mod repository, next to src/) not found";

    /// <summary><c>mods/</c> of the repo, or null when the repo mod is not there (e.g. a public source checkout).</summary>
    public static string? Dir { get; } = Find();

    static string? Find()
    {
        // Both repos keep mods/ next to src/, so walking up from the test binaries finds it.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "mods");
            if (Directory.Exists(Path.Combine(candidate, "indigo.duster", "1.0.0"))) return candidate;
        }
        return null;
    }

    /// <summary>File under <c>mods/indigo.duster/1.0.0/</c> (tests are guarded by <see cref="ModFactAttribute"/>).</summary>
    public static string Duster(string relative) =>
        Path.Combine(Dir ?? throw new InvalidOperationException(SkipReason), "indigo.duster", "1.0.0", relative);
}

public sealed class ModFactAttribute : FactAttribute
{
    public ModFactAttribute() { if (ModRepo.Dir == null) Skip = ModRepo.SkipReason; }
}

public sealed class ModTheoryAttribute : TheoryAttribute
{
    public ModTheoryAttribute() { if (ModRepo.Dir == null) Skip = ModRepo.SkipReason; }
}
