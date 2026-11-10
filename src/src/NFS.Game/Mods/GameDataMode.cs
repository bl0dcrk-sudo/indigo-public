using NFS.Game.Cars;

namespace NFS.Game.Mods;

/// <summary>Per-race data mode (openspec modding-api-cars D8, spec mod-loading "Game data mode"): <see cref="Original"/> when every
/// participating car is an original one, <see cref="Modded"/> when at least one is a mod car. In <see cref="Modded"/> the race
/// is not comparable with the original game: its records go to <c>records_mods.json</c> and its port replay carries a mods header.
/// The original <c>.STF/.SSF</c> files and <c>REPLAY.RPY</c> are never touched in that mode.</summary>
public enum GameDataMode { Original, Modded }

public static class GameDataModes
{
    /// <summary>Mode of a race with the given in-memory car numbers (vanilla = LegacyIndex 0..0x34, mod cars = 0x35..).</summary>
    public static GameDataMode Of(IEnumerable<int> carNumbers)
        => carNumbers.Any(n => n >= CarCatalog.VanillaCount) ? GameDataMode.Modded : GameDataMode.Original;
}
