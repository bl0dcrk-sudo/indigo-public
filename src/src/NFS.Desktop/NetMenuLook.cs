namespace NFS.Desktop;

/// <summary>Look of the network menu screens (change network-menu-look): <see cref="Original"/> draws them with the fonts and
/// windows of the game's front end, <see cref="Classic"/> keeps the first look of the port (bitmap font, dark chequered panels).
/// Chosen by <c>netMenuLook</c> in <c>port.json</c> (<see cref="PortSettings.NetMenuLook"/>).</summary>
public enum NetMenuLook { Original, Classic }

public static class NetMenuLooks
{
    /// <summary>The look called <paramref name="text"/> (<c>original</c> or <c>classic</c>, any case); null for a missing or unknown name.</summary>
    public static NetMenuLook? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "original" => NetMenuLook.Original,
        "classic" => NetMenuLook.Classic,
        _ => null,
    };

    public static string Name(NetMenuLook look) => look == NetMenuLook.Classic ? "classic" : "original";
}
