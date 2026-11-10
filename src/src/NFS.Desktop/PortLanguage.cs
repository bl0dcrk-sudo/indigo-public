namespace NFS.Desktop;

/// <summary>The menu language chosen for this run (0 english, 1 german, 2 french, 3 spanish, 4 italian, 5 swedish). [ref:1326]
/// The original reads it once from install.win; the port reads [ref:1327]
/// port.json <c>language</c> (<see cref="PortSettings.LanguageIndex"/>) in the host and publishes it here for the menu and the network screens.</summary>
internal static class PortLanguage
{
    public static int Current { get; set; }
}
