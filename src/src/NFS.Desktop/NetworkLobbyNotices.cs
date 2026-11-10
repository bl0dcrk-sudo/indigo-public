using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>
/// Chat lines of the SERVER about the people in the game, worked out on the client from two consecutive lobby states (the server
/// sends none, the protocol stays as it is): <c>Bob joined</c>, <c>Bob left</c>, <c>Bob lost connection</c>, <c>Bob is back</c>.
/// Nothing is said about the player itself, nor for the first state of a game.
/// </summary>
internal static class NetworkLobbyNotices
{
    /// <summary>The lines for the change from <paramref name="previous"/> to <paramref name="next"/>; empty when the states belong to
    /// different games or there is no previous state.</summary>
    public static IReadOnlyList<string> Diff(NetLobbyState? previous, NetLobbyState? next, string me)
    {
        if (previous == null || next == null || previous.GameId != next.GameId) return [];
        var lines = new List<string>();
        foreach (var now in next.Members)
        {
            if (Is(now.Name, me)) continue;
            var before = Find(previous, now.Name);
            if (before == null) lines.Add($"{now.Name} joined");
            else if (before.Connected && !now.Connected) lines.Add($"{now.Name} lost connection");
            else if (!before.Connected && now.Connected) lines.Add($"{now.Name} is back");
        }
        foreach (var was in previous.Members)
            if (!Is(was.Name, me) && Find(next, was.Name) == null) lines.Add($"{was.Name} left");
        return lines;
    }

    static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static NetLobbyMember? Find(NetLobbyState state, string name)
    {
        foreach (var m in state.Members)
            if (Is(m.Name, name)) return m;
        return null;
    }
}
