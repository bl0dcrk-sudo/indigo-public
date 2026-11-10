using NFS.Game.Race;
using NFS.Net.Client;
using NFS.Net.Protocol;

namespace NFS.Desktop;

/// <summary>Thin view of a session client for the network menu (change network-game-sessions, task 5.1): the state the menu
/// reads every frame and the operations it triggers. <see cref="SessionClientView"/> adapts a real <see cref="SessionClient"/>;
/// tests supply a fake state.</summary>
internal interface INetSessionView
{
    SessionClientPhase Phase { get; }
    string Name { get; }
    IReadOnlyList<NetGameInfo> Games { get; }
    NetLobbyState? Lobby { get; }
    /// <summary>A create/join request waits for the server's answer.</summary>
    bool Pending { get; }
    SessionError? LastError { get; }
    NetReject? LastReject { get; }
    /// <summary>Non-zero while the finished round of the current game has results to show (its race id); 0 otherwise.</summary>
    int ResultsId { get; }
    /// <summary>Results of the finished round (for the summary screen); null when none (default for views without them).</summary>
    RaceResults? Results => null;
    /// <summary>Winner row for which the original shows the victory screen (the local player won the finished round); null otherwise.</summary>
    RacerResult? LocalWinner => null;
    /// <summary>Race car index of the local player (null for a spectator or without a round): its result row is the "me" row.</summary>
    int? LocalCarIndex => null;
    /// <summary>Microseconds left of the reconnect window while the client reconnects; null when not reconnecting or not known.</summary>
    long? ReconnectRemainingMicros => null;

    bool RequestGames();
    bool CreateGame(NetGameCreate request);
    bool JoinGame(int gameId);
    bool LeaveGame();
    bool SetReady(bool ready);
    bool SetCar(int carType, int colour);
    bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style);
    bool StartRace();
    /// <summary>Operator only: aborts the start countdown (back to the lobby).</summary>
    bool CancelStart();
    bool ReturnToLobby();
    void Disconnect();
}

/// <summary><see cref="INetSessionView"/> over a real <see cref="SessionClient"/>.</summary>
internal sealed class SessionClientView(SessionClient client) : INetSessionView
{
    public SessionClient Client { get; } = client;
    public SessionClientPhase Phase => Client.Phase;
    public string Name => Client.Name;
    public IReadOnlyList<NetGameInfo> Games => Client.Games;
    public NetLobbyState? Lobby => Client.Lobby;
    public bool Pending => Client.Pending;
    public SessionError? LastError => Client.LastError;
    public NetReject? LastReject => Client.LastReject;
    public RaceResults? Results => Client.Race?.Results?.Results;
    public RacerResult? LocalWinner => Client.Race?.LocalWinner;
    public int? LocalCarIndex => Client.Race?.LocalCarIndex;
    public long? ReconnectRemainingMicros => Client.ReconnectRemainingMicros;
    public int ResultsId => Client.Race is { Results: not null } race ? race.Welcome?.RaceId ?? 1 : 0;

    public bool RequestGames() => Client.RequestGames();
    public bool CreateGame(NetGameCreate request) => Client.CreateGame(request);
    public bool JoinGame(int gameId) => Client.JoinGame(gameId);
    public bool LeaveGame() => Client.LeaveGame();
    public bool SetReady(bool ready) => Client.SetReady(ready);
    public bool SetCar(int carType, int colour) => Client.SetCar(carType, colour);
    public bool SetOptions(int track, int laps, bool backward, int bots, bool traffic, int skill, bool catchUp, int style) =>
        Client.SetOptions(track, laps, backward, bots, traffic, skill, catchUp, style);
    public bool StartRace() => Client.StartRace();
    public bool CancelStart() => Client.CancelStart();
    public bool ReturnToLobby() => Client.ReturnToLobby();
    public void Disconnect() => Client.Disconnect();
}
