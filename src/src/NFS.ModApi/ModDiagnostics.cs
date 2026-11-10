namespace NFS.ModApi;

public enum ModDiagnosticSeverity { Warning, Error }

/// <summary>One message about a mod.</summary>
public sealed record ModDiagnostic(ModDiagnosticSeverity Severity, string ModId, string Message);

/// <summary>Collects warnings and errors raised while loading mods.</summary>
public sealed class ModDiagnostics
{
    private readonly List<ModDiagnostic> _entries = [];

    public IReadOnlyList<ModDiagnostic> Entries => _entries;

    public bool HasErrors => _entries.Exists(e => e.Severity == ModDiagnosticSeverity.Error);

    public void Warning(string modId, string message) => _entries.Add(new(ModDiagnosticSeverity.Warning, modId, message));

    public void Error(string modId, string message) => _entries.Add(new(ModDiagnosticSeverity.Error, modId, message));
}
