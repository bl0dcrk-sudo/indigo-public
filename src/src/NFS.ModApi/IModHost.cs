namespace NFS.ModApi;

/// <summary>What the game gives to a mod while it is loaded.</summary>
public interface IModHost
{
    /// <summary>Game's <see cref="ModApiInfo.Version"/>.</summary>
    string ApiVersion { get; }

    ICarRegistry Cars { get; }

    ModDiagnostics Diagnostics { get; }
}
