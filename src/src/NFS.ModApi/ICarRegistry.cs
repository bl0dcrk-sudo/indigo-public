namespace NFS.ModApi;

/// <summary>Registry a mod uses to add cars. Rejections are explained in <paramref name="error"/>.</summary>
public interface ICarRegistry
{
    /// <summary>Registers a car; false (with a message) when the key is taken, reserved or the registry is frozen.</summary>
    bool TryRegister(CarDefinition car, out string? error);
}
