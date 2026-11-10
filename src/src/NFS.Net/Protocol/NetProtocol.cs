namespace NFS.Net.Protocol;

/// <summary>
/// Constants of the race protocol (network stage 5a.3). Version numbers are part of the handshake: a peer with another
/// <see cref="Version"/> (wire format) or <see cref="PhysicsVersion"/> (simulation rules that change results) is rejected before
/// it gets a seat. The application version (<see cref="BuildInfo.AppVersion"/>, four assembly components) is checked as well,
/// so two builds of the same wire version cannot join each other by accident. Bump <see cref="PhysicsVersion"/> whenever a
/// change of NFS.Core / RaceRunner alters the simulation.
/// </summary>
public static class NetProtocol
{
    /// <summary>"NFS2" little-endian; first field of the hello and of every reject.</summary>
    public const uint Magic = 0x3253464E;
    /// <summary>Version 19: the hello carries its purpose (<see cref="NetHelloPurpose"/>); a <see cref="NetHelloPurpose.ModDownload"/> connection is no player: the server answers with a <see cref="NetModOffer"/> and streams the mods the client asks for (<see cref="NetModRequest"/>, <see cref="NetModFileStart"/>, <see cref="NetModChunk"/>, <see cref="NetModEnd"/>); <see cref="NetRejectReason.ModDownloadRefused"/>. Version 18: <see cref="NetLobbyCar"/> and <see cref="NetLobbyMember"/> carry the car as a car key (text, <c>author.mod/car</c> or <c>nfs2se/NAME</c>) instead of a number, so mod cars are selectable; the server accepts the cars of its catalog (showcase cars 0..11 and mod cars). Version 17: the hello ends with the data mods of the sender (<see cref="NetModSet": fingerprint + list of id, version, hash), the reject <see cref="NetRejectReason.ModsMismatch"/> carries the mods of the server. Version 16: <see cref="NetLobbyCancel"/> lets the operator abort the start countdown (the game returns to the lobby phase, ready flags stay). Version 15: <see cref="NetGameCreate"/> shrinks to name + player limit + mode (the server gives the other options their defaults); <see cref="NetLobbyOptions"/> carries bots, traffic, bot skill, catch-up and physics style next to track/laps/direction; <see cref="NetGameInfo"/> and <see cref="NetLobbyState"/> carry skill, catch-up and style; <see cref="NetLobbyCar"/> and <see cref="NetLobbyMember"/> carry the car colour; only the showcase cars 0..11 are selectable (<see cref="MaxCarType"/>). Version 14: every row of the result carries the car model type (<see cref="NFS.Game.Race.RacerResult.CarType"/>), so the result screen of a client can show the brand logo of the winner. Version 13: the welcome carries a spectator flag (<see cref="NetWelcome.Spectator"/>): a member of a game that does not race the round (an eliminated knockout player) receives the same welcome, baseline, snapshot, event, durable and result stream as the racers, has no seat of its own and sends no controls. Version 12: knockout game mode (<see cref="NetGameMode.Knockout"/>), the series state and the straggler countdown
    /// at the end of <see cref="NetLobbyState"/>, <see cref="NetFinishReason.StragglerLimit"/>. Version 11: chat (<see cref="NetMessageType.ChatSend"/> / <see cref="NetMessageType.ChatMessage"/>). Version 10: game create/info and lobby state carry the number of bots (-1 = auto, 0..7) and the traffic flag, fixed at game creation. Version 9: game create/info, lobby options and lobby state carry the track direction (backward). Version 8: the hello carries the four-component application version and an optional 16-byte resume token; the
    /// reject carries the server application version. Session/lobby message types are defined as well. Version 7: the hello
    /// carries the car model type the player selected in the menu (a server composes the race with that model for the human
    /// car). Version 6: the client acknowledges a received result (<see cref="NetMessageType.ResultsAck"/>), so a graceful
    /// server stop can drain the result before closing the connection. Version 5: an interactive start may re-welcome the same
    /// connection before the countdown with a new race id and epoch (a host composed for exactly the connected humans).
    /// Version 4 servers froze the lineup; version 3 accepted exactly two seats.</summary>
    public const ushort Version = 19;
    public const ushort PhysicsVersion = 1;

    /// <summary>Host step = one whole RaceRunner.Step (two 64 Hz ticks), 32 per second.</summary>
    public const int StepHz = 32;
    public const long StepMicros = 1_000_000 / StepHz;

    /// <summary>Budget of every unreliable datagram (payload). LiteNetLib 2.1.4 starts each peer at NetConstants.InitialMtu = 1024
    /// (1023 bytes unreliable / 1020 bytes sequenced payload); the budget stays below both so that no snapshot depends on MTU
    /// discovery or fragmentation.</summary>
    public const int MaxDatagram = 1000;
    /// <summary>Budget of one reliable message (handshake, durable, events, results). Reliable channels may fragment, but the
    /// per-step streams stay inside one datagram; only the one-time welcome/baseline may be larger.</summary>
    public const int MaxReliableMessage = 8192;

    /// <summary>Input samples repeated in every input datagram (loss recovery without retransmission).</summary>
    public const int InputRedundancy = 8;
    public const int MaxActionsPerPacket = 4;
    /// <summary>Accepted input targets: [next step, next step + MaxFutureSteps). Later targets are rejected as "future".</summary>
    public const int MaxFutureSteps = 64;
    /// <summary>Steps a missing sample is replaced by the last continuous controls (steer/gas/brake/handbrake/horn) before the
    /// seat drives neutral. One-shot actions are never held.</summary>
    public const int HoldSteps = 8;
    /// <summary>A gear/reset action not applied within this many steps after its target is dropped (counted, ACKed).</summary>
    public const int ActionExpirySteps = 32;
    /// <summary>Client: one-shot sound events older than this behind the presented frame are not played.</summary>
    public const int EventExpiryTicks64 = 128;
    /// <summary>Input datagrams accepted per connection and second; more are dropped (flooding).</summary>
    public const int MaxInputPacketsPerSecond = 128;
    /// <summary>Reliable packets queued for one connection before the server drops it as too slow (bounded memory).</summary>
    public const int MaxReliableBacklog = 512;
    /// <summary>Server: at most this many steps are caught up in one update; a longer stall shifts the clock (no endless catch-up).</summary>
    public const int MaxCatchUpSteps = 4;
    /// <summary>Delay between the start message and the first host step.</summary>
    public const long StartLeadMicros = 500_000;

    public const int MaxNameLength = 12;
    public const int MaxSetupBlock = 4096;
    public const int MaxCars = 32;
    public const int MaxLapTimes = 16;
    /// <summary>Human seats the protocol can describe (one byte of seat indices and a byte of DNF bits).</summary>
    public const int MaxSeats = 8;
    /// <summary>Bot count meaning "fill the grid up to eight cars" (equals <c>ServerRaceConfig.AutoBots</c>).</summary>
    public const int AutoBots = -1;
    /// <summary>A dedicated race can start with a single human; the other grid slots are real AI opponents.</summary>
    public const int MinSeats = 1;

    /// <summary>Largest car model type a player can select in a network game: 0..11 are the showcase cars; the cheat cars 12..14
    /// of the offline menu are not selectable (the server refuses them).</summary>
    public const int MaxCarType = 11;
    /// <summary>Hello car type meaning "no explicit selection" (legacy/headless callers): the server keeps the frozen menu
    /// model of that seat. Explicit menu choices are <c>0..MaxCarType</c>.</summary>
    public const int NoCarType = -1;
    /// <summary>Largest in-memory car number the wire can carry in a hello or a result row (one signed byte): 0..0x34 original cars,
    /// 0x35.. mod cars of the catalog (the same mods on both sides, see <see cref="NetModSet"/>).</summary>
    public const int MaxCarNumber = sbyte.MaxValue;
    /// <summary>Longest car key on the wire (<c>author.mod/car</c>; the mod id alone is at most 64 characters).</summary>
    public const int MaxCarKeyLength = 128;

    /// <summary>Length of the optional resume token carried by the hello. An empty token is encoded as length 0.</summary>
    public const int ResumeTokenLength = 16;

    /// <summary>Longest game (lobby) name; shorter than a message, longer than a player name.</summary>
    public const int MaxGameNameLength = 24;
    /// <summary>Games described in one list response (the server enforces its own, usually smaller, game limit).</summary>
    public const int MaxGameListEntries = 64;
    /// <summary>Menu track index range accepted in game options (the same 0..7 as the server console and the menu).</summary>
    public const int MinTrack = 0, MaxTrack = 7;
    public const int MinLaps = 1, MaxLaps = 8;
    /// <summary>Countdown is 5 s; the protocol allows slower configurations, but bounds a malformed remaining time.</summary>
    public const int MaxCountdownMillis = 60_000;

    /// <summary>Longest chat text (printable ASCII).</summary>
    public const int MaxChatLength = 80;
    /// <summary>Sender name the server puts on messages from its console; players cannot take it.</summary>
    public const string ServerChatName = "SERVER";
    public static bool IsChatText(string value) =>
        value.Length is >= 1 and <= MaxChatLength && value.All(c => c is >= ' ' and < (char)0x7F);

    /// <summary>Reduces user input to a chat line: non-printable or non-ASCII characters are dropped, the ends are trimmed and the
    /// result is cut to <see cref="MaxChatLength"/>.</summary>
    public static string SanitizeChat(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new string(value.Where(c => c is >= ' ' and < (char)0x7F).ToArray()).Trim();
        return text.Length > MaxChatLength ? text[..MaxChatLength].TrimEnd() : text;
    }

    /// <summary>Game ids travel in the 32-bit <see cref="NetHeader.RaceId"/> and must be positive (0 means "session").</summary>
    public static bool IsGameId(int value) => value > 0;
    /// <summary>Bot count of a game: <see cref="AutoBots"/> (fill the grid) or 0..<see cref="MaxSeats"/> − 1 (at least one car is human).</summary>
    public static bool IsBotSetting(int value) => value is >= AutoBots and <= MaxSeats - 1;
    public static bool IsTrack(int value) => value is >= MinTrack and <= MaxTrack;
    public static bool IsLaps(int value) => value is >= MinLaps and <= MaxLaps;
    public static bool IsGameMode(NetGameMode value) => value is NetGameMode.SingleRace or NetGameMode.Knockout;
    public static bool IsGameName(string value) =>
        value.Length is >= 1 and <= MaxGameNameLength && value.All(c => c is >= ' ' and < (char)0x7F);

    /// <summary>Entrants of a knockout series (humans + AI) and its rounds (one track per round).</summary>
    public const int KnockoutEntrants = 9, KnockoutRounds = 8;
    /// <summary>Longest straggler countdown a lobby state may carry (one hour).</summary>
    public const int MaxCutoffMillis = 3_600_000;

    /// <summary>Bot skill (setup +0x08): 0 = beginner, 1 = expert.</summary>
    public const int MinSkill = 0, MaxSkill = 1;
    /// <summary>Physics style (<c>FeConfig.Style</c>): 0 = Simulation, 1 = Wild, 2 = Arcade.</summary>
    public const int MinStyle = 0, MaxStyle = 2;
    /// <summary>Car colour meaning "no explicit selection": the colour of the frozen menu record is kept. Explicit colours are
    /// palette entries of the model (<c>FrontEndConstants.ColourTable</c>), one signed byte on the wire.</summary>
    public const int NoColour = -1;
    /// <summary>Defaults the server gives a new game (the create message carries only name, player limit and mode).</summary>
    public const int DefaultTrack = 0, DefaultLaps = 2, DefaultSkill = 1, DefaultStyle = 1;
    public const bool DefaultBackward = false, DefaultTraffic = false, DefaultCatchUp = true;

    public static bool IsCarType(int value) => value is >= 0 and <= MaxCarType;
    public static bool IsSkill(int value) => value is >= MinSkill and <= MaxSkill;
    public static bool IsStyle(int value) => value is >= MinStyle and <= MaxStyle;
    public static bool IsColourOrNone(int value) => value is >= NoColour and <= sbyte.MaxValue;
    public static bool IsCarTypeOrNone(int value) => value == NoCarType || IsCarType(value);
    /// <summary>An in-memory car number of a catalog (original or mod); whether the number exists is the receiver's catalog to say.</summary>
    public static bool IsCarNumber(int value) => value is >= 0 and <= MaxCarNumber;
    public static bool IsCarNumberOrNone(int value) => value == NoCarType || IsCarNumber(value);

    /// <summary>The last defined <see cref="NetMessageType"/>; appended values never renumber existing messages.</summary>
    public const NetMessageType LastMessageType = NetMessageType.ModEnd;
}

public enum NetMessageType : byte
{
    Welcome = 1, Ready = 2, Start = 3, Input = 4, Snapshot = 5, Durable = 6, Events = 7, Results = 8, PlayerLeft = 9, Leave = 10, Roster = 11,
    ResultsAck = 12,
    // Session messages use RaceId = 0 and Epoch = 0. Lobby/game messages use RaceId = GameId and the connection epoch.
    GameListRequest = 13, GameList = 14, GameCreate = 15, GameJoin = 16, GameLeave = 17,
    LobbyReady = 18, LobbyCar = 19, LobbyOptions = 20, LobbyStart = 21, LobbyReturn = 22, LobbyState = 23,
    // Chat (v11), game header: the client sends the text, the server adds the sender name and broadcasts within the game.
    ChatSend = 24, ChatMessage = 25,
    // Countdown abort (v16), game header: operator only.
    LobbyCancel = 26,
    // Mod download (v19): session header (RaceId 0, Epoch 0), only on a connection whose hello has the purpose ModDownload.
    ModOffer = 27, ModRequest = 28, ModFileStart = 29, ModChunk = 30, ModEnd = 31,
}

/// <summary>Game modes advertised by the session layer: a single race per start, or a knockout series (nine entrants, one
/// eliminated per round, eight tracks) run by the server in the same game.</summary>
public enum NetGameMode : byte { SingleRace = 1, Knockout = 2 }

/// <summary>Status of a game on the games list; a race result returns the game to <see cref="Lobby"/>.</summary>
public enum NetGamePhase : byte { Lobby = 1, Countdown = 2, Race = 3 }

/// <summary>Delivery class requested from the transport (LiteNetLib DeliveryMethod semantics).</summary>
public enum NetChannel : byte
{
    /// <summary>Welcome, baseline, start, durable state, results, player left.</summary>
    ReliableOrdered,
    /// <summary>One-shot events (deduplicated and expired by the client).</summary>
    ReliableUnordered,
    /// <summary>Transport-sequenced datagrams (newest wins). Not used for snapshot parts: jitter between the parts of one
    /// snapshot would make the transport drop a sibling part; snapshots are sequenced by SnapshotSequence instead.</summary>
    Sequenced,
    /// <summary>Inputs (redundant, deduplicated by the server) and snapshot parts (deduplicated, stale ones dropped by the client).</summary>
    Unreliable,
}

public enum NetRejectReason : byte
{
    None = 0, Malformed = 1, ProtocolMismatch = 2, PhysicsMismatch = 3, DataMismatch = 4, RaceFull = 5, RaceInProgress = 6,
    ConfigMismatch = 7, SlowClient = 8, Left = 9, RaceClosed = 10, ServerShutdown = 11, Timeout = 12, AppVersionMismatch = 13,
    /// <summary>The requested player name is already used by a connected client (released when that client disconnects).</summary>
    NameTaken = 14,
    /// <summary>The resume token of the hello is unknown, expired, belongs to another player or the seat can no longer be
    /// resumed (the game is gone or the member is already connected).</summary>
    ResumeTokenInvalid = 15,
    /// <summary>The hello's mod set differs from the server's (<see cref="NetReject.ServerMods"/> lists the server's mods).</summary>
    ModsMismatch = 16,
    /// <summary>The server does not offer mods for download (none to offer, disabled, or too many downloads at once).</summary>
    ModDownloadRefused = 17,
}

/// <summary>Server race phases: WaitingForPlayers → Loading (seats taken, waiting for Ready) → Countdown [ref:3433]
/// (host steps, start lights) → Racing → Finished → Closed; Aborted when the race ends without its natural finish.</summary>
public enum NetRacePhase : byte { WaitingForPlayers, Loading, Countdown, Racing, Finished, Closed, Aborted }

/// <summary>Why the race result was produced.</summary>
public enum NetFinishReason : byte { Finished = 1, AllHumansLeft = 2, RemainingHumansFinished = 3, TimeLimit = 4, ServerStop = 5,
    /// <summary>The time allowed for the stragglers after the first finisher ran out; the unfinished cars are DNF.</summary>
    StragglerLimit = 6 }

/// <summary>One-shot driver actions; applied by the server at most once each (id + ACK), never repeated by holding a sample.</summary>
public enum NetActionKind : byte { GearUp = 1, GearDown = 2, Reset = 3 }
