namespace NFS.Net.Protocol;

/// <summary>
/// Header rules of the session layer, shared by its encoders and decoders:
/// <list type="bullet">
/// <item>session messages (before a game is chosen): <c>RaceId = 0</c> and <c>Epoch = 0</c>;</item>
/// <item>lobby/game messages: <c>RaceId = GameId</c> (positive, fits the 32-bit <see cref="NetHeader.RaceId"/>) and the
/// connection epoch assigned by the session server;</item>
/// <item>race messages keep <c>RaceId</c> of the round and its <c>Epoch</c> unchanged.</item>
/// </list>
/// The race code rejects a foreign race id or epoch; a session server checks these rules before routing a message.
/// </summary>
public static class NetSessionProtocol
{
    public const int SessionRaceId = 0;
    public const uint SessionEpoch = 0;

    public static NetHeader SessionHeader(NetMessageType type) => new(type, SessionRaceId, SessionEpoch);

    public static NetHeader GameHeader(NetMessageType type, int gameId, uint epoch)
    {
        if (!NetProtocol.IsGameId(gameId)) throw new ArgumentOutOfRangeException(nameof(gameId), "game id must be positive");
        if (epoch == 0) throw new ArgumentOutOfRangeException(nameof(epoch), "lobby/game messages require a connection epoch");
        return new NetHeader(type, gameId, epoch);
    }

    public static void RequireSession(in NetHeader h, NetMessageType type)
    {
        if (h.Type != type || h.RaceId != SessionRaceId || h.Epoch != SessionEpoch)
            throw new NetProtocolException($"not a {type} session message");
    }

    /// <summary>A lobby/game message carries a game id and the sender's connection epoch; which game and epoch are expected is
    /// checked by the receiver against its own registry.</summary>
    public static void RequireGame(in NetHeader h, NetMessageType type)
    {
        if (h.Type != type) throw new NetProtocolException($"not a {type} message");
        if (!NetProtocol.IsGameId(h.RaceId) || h.Epoch == 0) throw new NetProtocolException("invalid game header");
    }
}

/// <summary>One game on the games list.</summary>
public sealed record NetGameInfo(
    int GameId, string Name, NetGamePhase Phase, int Players, int MaxPlayers, int Track, int Laps, NetGameMode Mode,
    bool Backward = false, int Bots = NetProtocol.AutoBots, bool Traffic = false, int Skill = NetProtocol.DefaultSkill,
    bool CatchUp = NetProtocol.DefaultCatchUp, int Style = NetProtocol.DefaultStyle)
{
    public void Write(WireWriter w)
    {
        if (!NetProtocol.IsGameId(GameId)) throw new ArgumentOutOfRangeException(nameof(GameId), "game id must be positive");
        NetWire.ValidateName(Name, NetProtocol.MaxGameNameLength, nameof(Name));
        if (!Enum.IsDefined(Phase)) throw new ArgumentOutOfRangeException(nameof(Phase));
        if (MaxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats) throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
        if (Players is < 0 || Players > MaxPlayers) throw new ArgumentOutOfRangeException(nameof(Players));
        if (!NetProtocol.IsTrack(Track)) throw new ArgumentOutOfRangeException(nameof(Track));
        if (!NetProtocol.IsLaps(Laps)) throw new ArgumentOutOfRangeException(nameof(Laps));
        if (!NetProtocol.IsGameMode(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!NetProtocol.IsBotSetting(Bots)) throw new ArgumentOutOfRangeException(nameof(Bots));
        if (!NetProtocol.IsSkill(Skill)) throw new ArgumentOutOfRangeException(nameof(Skill));
        if (!NetProtocol.IsStyle(Style)) throw new ArgumentOutOfRangeException(nameof(Style));
        w.VarU(GameId);
        w.Ascii(Name, NetProtocol.MaxGameNameLength);
        w.U8((byte)Phase);
        w.U8(Players);
        w.U8(MaxPlayers);
        w.U8(Track);
        w.U8(Laps);
        w.U8((byte)Mode);
        w.Bool(Backward);
        w.I8(Bots);
        w.Bool(Traffic);
        w.U8(Skill);
        w.Bool(CatchUp);
        w.U8(Style);
    }

    public static NetGameInfo Read(ref WireReader r)
    {
        int gameId = (int)r.VarU(int.MaxValue);
        string name = NetWire.ReadName(ref r, NetProtocol.MaxGameNameLength);
        byte phase = r.U8();
        int players = r.U8(), maxPlayers = r.U8(), track = r.U8(), laps = r.U8(), mode = r.U8();
        bool backward = r.Bool();
        int bots = r.I8();
        bool traffic = r.Bool();
        int skill = r.U8();
        bool catchUp = r.Bool();
        int style = r.U8();
        if (gameId <= 0) throw new NetProtocolException("invalid game id");
        if (phase is < (byte)NetGamePhase.Lobby or > (byte)NetGamePhase.Race) throw new NetProtocolException("invalid game phase");
        if (maxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats || players > maxPlayers)
            throw new NetProtocolException("invalid game player counts");
        if (!NetProtocol.IsTrack(track) || !NetProtocol.IsLaps(laps) || !NetProtocol.IsGameMode((NetGameMode)mode))
            throw new NetProtocolException("invalid game options");
        if (!NetProtocol.IsBotSetting(bots)) throw new NetProtocolException("invalid bot count");
        if (!NetProtocol.IsSkill(skill) || !NetProtocol.IsStyle(style)) throw new NetProtocolException("invalid skill or style");
        return new NetGameInfo(gameId, name, (NetGamePhase)phase, players, maxPlayers, track, laps, (NetGameMode)mode, backward, bots, traffic,
            skill, catchUp, style);
    }
}

/// <summary>Client → server: ask for the current games list (session header).</summary>
public readonly record struct NetGameListRequest
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size);
        NetSessionProtocol.SessionHeader(NetMessageType.GameListRequest).Write(w);
        return w.ToArray();
    }

    public static NetGameListRequest Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.GameListRequest);
        r.End();
        return default;
    }
}

/// <summary>Server → client: the current games (session header). Names and ids are unique.</summary>
public sealed record NetGameList(IReadOnlyList<NetGameInfo> Games)
{
    public byte[] Encode()
    {
        if (Games.Count > NetProtocol.MaxGameListEntries) throw new ArgumentOutOfRangeException(nameof(Games), "too many games");
        var w = new WireWriter(NetProtocol.MaxReliableMessage);
        NetSessionProtocol.SessionHeader(NetMessageType.GameList).Write(w);
        w.U8(Games.Count);
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in Games)
        {
            if (!ids.Add(game.GameId)) throw new ArgumentException($"duplicate game id {game.GameId:X8}", nameof(Games));
            if (!names.Add(game.Name)) throw new ArgumentException($"duplicate game name '{game.Name}'", nameof(Games));
            game.Write(w);
        }
        return w.ToArray();
    }

    public static NetGameList Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.GameList);
        int count = r.U8();
        if (count > NetProtocol.MaxGameListEntries) throw new NetProtocolException("game list too large");
        var games = new NetGameInfo[count];
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            games[i] = NetGameInfo.Read(ref r);
            if (!ids.Add(games[i].GameId) || !names.Add(games[i].Name))
                throw new NetProtocolException("duplicate game id or name");
        }
        r.End();
        return new NetGameList(Array.AsReadOnly(games));
    }
}

/// <summary>Client → server: create a game; the creator becomes its operator (session header). Only the name, the player
/// limit and the mode are chosen here; the server gives every other option its default (<see cref="NetProtocol.DefaultTrack"/>
/// etc.) and the operator changes them in the lobby.</summary>
public sealed record NetGameCreate(string Name, int MaxPlayers, NetGameMode Mode = NetGameMode.SingleRace)
{
    public byte[] Encode()
    {
        NetWire.ValidateName(Name, NetProtocol.MaxGameNameLength, nameof(Name));
        if (MaxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats) throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
        if (!NetProtocol.IsGameMode(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        var w = new WireWriter(NetHeader.Size + 1 + NetProtocol.MaxGameNameLength + 2);
        NetSessionProtocol.SessionHeader(NetMessageType.GameCreate).Write(w);
        w.Ascii(Name, NetProtocol.MaxGameNameLength);
        w.U8(MaxPlayers);
        w.U8((byte)Mode);
        return w.ToArray();
    }

    public static NetGameCreate Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.GameCreate);
        string name = NetWire.ReadName(ref r, NetProtocol.MaxGameNameLength);
        int maxPlayers = r.U8(), mode = r.U8();
        r.End();
        if (maxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats) throw new NetProtocolException("invalid player limit");
        if (!NetProtocol.IsGameMode((NetGameMode)mode)) throw new NetProtocolException("invalid game options");
        return new NetGameCreate(name, maxPlayers, (NetGameMode)mode);
    }
}

/// <summary>Client → server: join an existing game by id (session header; the game id is in the payload, not in the header).</summary>
public sealed record NetGameJoin(int GameId)
{
    public byte[] Encode()
    {
        if (!NetProtocol.IsGameId(GameId)) throw new ArgumentOutOfRangeException(nameof(GameId), "game id must be positive");
        var w = new WireWriter(NetHeader.Size + 5);
        NetSessionProtocol.SessionHeader(NetMessageType.GameJoin).Write(w);
        w.VarU(GameId);
        return w.ToArray();
    }

    public static NetGameJoin Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireSession(h, NetMessageType.GameJoin);
        int gameId = (int)r.VarU(int.MaxValue);
        r.End();
        if (!NetProtocol.IsGameId(gameId)) throw new NetProtocolException("invalid game id");
        return new NetGameJoin(gameId);
    }
}

/// <summary>Client → server: leave the current game and return to the games list (game header).</summary>
public sealed record NetGameLeave(int GameId, uint Epoch)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size);
        NetSessionProtocol.GameHeader(NetMessageType.GameLeave, GameId, Epoch).Write(w);
        return w.ToArray();
    }

    public static NetGameLeave Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.GameLeave);
        r.End();
        return new NetGameLeave(h.RaceId, h.Epoch);
    }
}

/// <summary>Client → server: set the sender's own lobby readiness (game header). The server ignores the readiness of
/// another player; the message carries no seat, so the connection identity decides whose flag changes.</summary>
public sealed record NetLobbyReady(int GameId, uint Epoch, bool Ready)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size + 1);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyReady, GameId, Epoch).Write(w);
        w.Bool(Ready);
        return w.ToArray();
    }

    public static NetLobbyReady Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyReady);
        bool ready = r.Bool();
        r.End();
        return new NetLobbyReady(h.RaceId, h.Epoch, ready);
    }
}

/// <summary>Client → server: select the sender's own car model and colour for the next race (game header).
/// <see cref="Colour"/> is a palette entry of the model or <see cref="NetProtocol.NoColour"/> (the model's default); the server
/// checks it against the palette of the model.</summary>
public sealed record NetLobbyCar(int GameId, uint Epoch, NFS.ModApi.CarKey Car, int Colour = NetProtocol.NoColour)
{
    /// <summary>Selects an original car by its number (0..0x34); the server accepts only the showcase cars 0..11.</summary>
    public NetLobbyCar(int gameId, uint epoch, int carType, int colour = NetProtocol.NoColour)
        : this(gameId, epoch, NetCars.VanillaKey(carType) ?? throw new ArgumentOutOfRangeException(nameof(carType)), colour) { }

    /// <summary>Original car number of <see cref="Car"/>, <see cref="NetProtocol.NoCarType"/> for a mod car.</summary>
    public int CarType { get => NetCars.VanillaNumber(Car); init => Car = NetCars.VanillaKey(value) ?? throw new ArgumentOutOfRangeException(nameof(value)); }

    public byte[] Encode()
    {
        if (!NetProtocol.IsColourOrNone(Colour)) throw new ArgumentOutOfRangeException(nameof(Colour));
        var w = new WireWriter(NetHeader.Size + 2 + NetProtocol.MaxCarKeyLength);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyCar, GameId, Epoch).Write(w);
        w.Ascii(Car.Value, NetProtocol.MaxCarKeyLength);
        w.I8(Colour);
        return w.ToArray();
    }

    public static NetLobbyCar Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyCar);
        string text = r.Ascii(NetProtocol.MaxCarKeyLength);
        int colour = r.I8();
        r.End();
        if (!NFS.ModApi.CarKey.TryParse(text, out var key)) throw new NetProtocolException("invalid car key");
        if (!NetProtocol.IsColourOrNone(colour)) throw new NetProtocolException("invalid car colour");
        return new NetLobbyCar(h.RaceId, h.Epoch, key, colour);
    }
}

/// <summary>Client → server: operator changes the options of the next race (game header): track, direction, laps, bots, traffic,
/// bot skill (0 = beginner, 1 = expert), catch-up and physics style (0 = Simulation, 1 = Wild, 2 = Arcade). The server applies
/// only the options that matter for the mode of the game.</summary>
public sealed record NetLobbyOptions(int GameId, uint Epoch, int Track, int Laps, bool Backward = false,
    int Bots = NetProtocol.AutoBots, bool Traffic = false, int Skill = NetProtocol.DefaultSkill,
    bool CatchUp = NetProtocol.DefaultCatchUp, int Style = NetProtocol.DefaultStyle)
{
    public byte[] Encode()
    {
        if (!NetProtocol.IsTrack(Track)) throw new ArgumentOutOfRangeException(nameof(Track));
        if (!NetProtocol.IsLaps(Laps)) throw new ArgumentOutOfRangeException(nameof(Laps));
        if (!NetProtocol.IsBotSetting(Bots)) throw new ArgumentOutOfRangeException(nameof(Bots));
        if (!NetProtocol.IsSkill(Skill)) throw new ArgumentOutOfRangeException(nameof(Skill));
        if (!NetProtocol.IsStyle(Style)) throw new ArgumentOutOfRangeException(nameof(Style));
        var w = new WireWriter(NetHeader.Size + 8);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyOptions, GameId, Epoch).Write(w);
        w.U8(Track);
        w.U8(Laps);
        w.Bool(Backward);
        w.I8(Bots);
        w.Bool(Traffic);
        w.U8(Skill);
        w.Bool(CatchUp);
        w.U8(Style);
        return w.ToArray();
    }

    public static NetLobbyOptions Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyOptions);
        int track = r.U8(), laps = r.U8();
        bool backward = r.Bool();
        int bots = r.I8();
        bool traffic = r.Bool();
        int skill = r.U8();
        bool catchUp = r.Bool();
        int style = r.U8();
        r.End();
        if (!NetProtocol.IsTrack(track) || !NetProtocol.IsLaps(laps)) throw new NetProtocolException("invalid game options");
        if (!NetProtocol.IsBotSetting(bots)) throw new NetProtocolException("invalid bot count");
        if (!NetProtocol.IsSkill(skill) || !NetProtocol.IsStyle(style)) throw new NetProtocolException("invalid skill or style");
        return new NetLobbyOptions(h.RaceId, h.Epoch, track, laps, backward, bots, traffic, skill, catchUp, style);
    }
}

/// <summary>Client → server: operator starts the 5-second countdown (game header).</summary>
public sealed record NetLobbyStart(int GameId, uint Epoch)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyStart, GameId, Epoch).Write(w);
        return w.ToArray();
    }

    public static NetLobbyStart Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyStart);
        r.End();
        return new NetLobbyStart(h.RaceId, h.Epoch);
    }
}

/// <summary>Client → server: operator aborts the start countdown; the game returns to the lobby phase (game header).</summary>
public sealed record NetLobbyCancel(int GameId, uint Epoch)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyCancel, GameId, Epoch).Write(w);
        return w.ToArray();
    }

    public static NetLobbyCancel Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyCancel);
        r.End();
        return new NetLobbyCancel(h.RaceId, h.Epoch);
    }
}

/// <summary>Client → server: the player acknowledged the result and returns to the game lobby (game header).</summary>
public sealed record NetLobbyReturn(int GameId, uint Epoch)
{
    public byte[] Encode()
    {
        var w = new WireWriter(NetHeader.Size);
        NetSessionProtocol.GameHeader(NetMessageType.LobbyReturn, GameId, Epoch).Write(w);
        return w.ToArray();
    }

    public static NetLobbyReturn Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyReturn);
        r.End();
        return new NetLobbyReturn(h.RaceId, h.Epoch);
    }
}

/// <summary>Client → server: a chat line for the sender's game (game header). The text is 1..<see cref="NetProtocol.MaxChatLength"/>
/// printable ASCII characters; the sender is never part of the message (the server uses the connection identity).</summary>
public sealed record NetChatSend(int GameId, uint Epoch, string Text)
{
    public byte[] Encode()
    {
        if (!NetProtocol.IsChatText(Text)) throw new ArgumentOutOfRangeException(nameof(Text), "invalid chat text");
        var w = new WireWriter(NetHeader.Size + 1 + Text.Length);
        NetSessionProtocol.GameHeader(NetMessageType.ChatSend, GameId, Epoch).Write(w);
        w.Ascii(Text, NetProtocol.MaxChatLength);
        return w.ToArray();
    }

    public static NetChatSend Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.ChatSend);
        string text = r.Ascii(NetProtocol.MaxChatLength);
        r.End();
        if (text.Length == 0) throw new NetProtocolException("empty chat text");
        return new NetChatSend(h.RaceId, h.Epoch, text);
    }
}

/// <summary>Server → client: a chat line with the sender name set by the server (game header with the recipient's epoch).
/// The sender is a player name or <see cref="NetProtocol.ServerChatName"/>.</summary>
public sealed record NetChatMessage(int GameId, uint Epoch, string Sender, string Text)
{
    public byte[] Encode()
    {
        NetWire.ValidateName(Sender, NetProtocol.MaxNameLength, nameof(Sender));
        if (!NetProtocol.IsChatText(Text)) throw new ArgumentOutOfRangeException(nameof(Text), "invalid chat text");
        var w = new WireWriter(NetHeader.Size + 2 + Sender.Length + Text.Length);
        NetSessionProtocol.GameHeader(NetMessageType.ChatMessage, GameId, Epoch).Write(w);
        w.Ascii(Sender, NetProtocol.MaxNameLength);
        w.Ascii(Text, NetProtocol.MaxChatLength);
        return w.ToArray();
    }

    public static NetChatMessage Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.ChatMessage);
        string sender = NetWire.ReadName(ref r, NetProtocol.MaxNameLength);
        string text = r.Ascii(NetProtocol.MaxChatLength);
        r.End();
        if (text.Length == 0) throw new NetProtocolException("empty chat text");
        return new NetChatMessage(h.RaceId, h.Epoch, sender, text);
    }
}

/// <summary>One entrant of a knockout series. Identities 0..humans-1 are the humans (<see cref="Slot"/> = their lobby slot), the
/// rest are AI cars (<see cref="Slot"/> = -1). <see cref="Place"/> is the place in the last completed round (0 before the
/// first), <see cref="EliminatedRound"/> the round of the elimination (-1 = still in).</summary>
public sealed record NetKnockoutEntry(string Name, int Slot, bool Active, int Place, int EliminatedRound);

/// <summary>State of the knockout series of a game (<see cref="NetGameMode.Knockout"/>), computed by the server only: the
/// clients show it and never derive eliminations. <see cref="CompletedRounds"/> rounds are over; while the series is running
/// the current or next round is <c>CompletedRounds</c> (0-based, also its track index). A finished series has
/// <see cref="Finished"/> set and the next start begins a new one. <see cref="Winner"/> is an identity, -1 when no one won
/// (every human was eliminated).</summary>
public sealed record NetKnockoutState(int CompletedRounds, bool Finished, int Winner, IReadOnlyList<NetKnockoutEntry> Entries)
{
    internal void Write(WireWriter w)
    {
        if (CompletedRounds is < 0 or > NetProtocol.KnockoutRounds) throw new ArgumentOutOfRangeException(nameof(CompletedRounds));
        if (Entries.Count != NetProtocol.KnockoutEntrants) throw new ArgumentException("a series has nine entrants", nameof(Entries));
        if (Winner is < -1 or >= NetProtocol.KnockoutEntrants) throw new ArgumentOutOfRangeException(nameof(Winner));
        w.U8(CompletedRounds);
        w.Bool(Finished);
        w.I8(Winner);
        foreach (var e in Entries)
        {
            NetWire.ValidateName(e.Name, NetProtocol.MaxNameLength, nameof(Entries));
            if (e.Slot is < -1 or >= NetProtocol.MaxSeats || e.Place is < 0 or > NetProtocol.KnockoutEntrants ||
                e.EliminatedRound is < -1 or >= NetProtocol.KnockoutRounds)
                throw new ArgumentOutOfRangeException(nameof(Entries), "invalid entrant");
            w.Ascii(e.Name, NetProtocol.MaxNameLength);
            w.I8(e.Slot);
            w.Bool(e.Active);
            w.U8(e.Place);
            w.I8(e.EliminatedRound);
        }
    }

    internal static NetKnockoutState Read(ref WireReader r)
    {
        int completed = r.U8();
        bool finished = r.Bool();
        int winner = r.I8();
        if (completed > NetProtocol.KnockoutRounds || winner is < -1 or >= NetProtocol.KnockoutEntrants)
            throw new NetProtocolException("invalid series state");
        var entries = new NetKnockoutEntry[NetProtocol.KnockoutEntrants];
        for (int i = 0; i < entries.Length; i++)
        {
            string name = NetWire.ReadName(ref r, NetProtocol.MaxNameLength);
            int slot = r.I8();
            bool active = r.Bool();
            int place = r.U8(), eliminated = r.I8();
            if (slot is < -1 or >= NetProtocol.MaxSeats || place > NetProtocol.KnockoutEntrants || eliminated is < -1 or >= NetProtocol.KnockoutRounds)
                throw new NetProtocolException("invalid series entrant");
            entries[i] = new NetKnockoutEntry(name, slot, active, place, eliminated);
        }
        return new NetKnockoutState(completed, finished, winner, Array.AsReadOnly(entries));
    }
}

/// <summary>One member of a lobby, ordered by join slot (the first member is the operator).</summary>
/// <summary><see cref="Colour"/> is the selected car colour or <see cref="NetProtocol.NoColour"/> (the model's default).</summary>
/// <summary><see cref="Car"/> is the selected car (null = none yet); on the wire a car key, empty for none.</summary>
public sealed record NetLobbyMember(int Slot, string Name, bool Ready, NFS.ModApi.CarKey? Car, bool Operator, bool Connected,
    int Colour = NetProtocol.NoColour)
{
    /// <summary>Member with an original car given by its number (<see cref="NetProtocol.NoCarType"/> = none).</summary>
    public NetLobbyMember(int slot, string name, bool ready, int carType, bool @operator, bool connected, int colour = NetProtocol.NoColour)
        : this(slot, name, ready, NetCars.VanillaKey(carType), @operator, connected, colour) { }

    /// <summary>Original car number of <see cref="Car"/>, <see cref="NetProtocol.NoCarType"/> for none or a mod car (use <see cref="NetCars.NumberOf"/> with a catalog).</summary>
    public int CarType { get => NetCars.VanillaNumber(Car); init => Car = NetCars.VanillaKey(value); }
}

/// <summary>Server → every member: the complete, idempotent lobby/game state (game header) after every change. The first
/// member is the operator; <see cref="CountdownMillis"/> is non-zero only in <see cref="NetGamePhase.Countdown"/>.
/// <see cref="ResumeToken"/> is the 16-byte token of the recipient's own seat (null when the server has none) and is appended
/// after the members; every member receives only its own token. After the token (v12): <see cref="CutoffMillis"/> = time left
/// for the stragglers of the running round (0 = no limit running; counted down by the client from the receipt of the state)
/// and the knockout <see cref="Series"/> (null outside <see cref="NetGameMode.Knockout"/>).</summary>
public sealed record NetLobbyState(
    int GameId, uint Epoch, string GameName, NetGamePhase Phase, int Track, int Laps, int MaxPlayers, NetGameMode Mode,
    int CountdownMillis, IReadOnlyList<NetLobbyMember> Members, byte[]? ResumeToken = null, bool Backward = false,
    int Bots = NetProtocol.AutoBots, bool Traffic = false, int CutoffMillis = 0, NetKnockoutState? Series = null,
    int Skill = NetProtocol.DefaultSkill, bool CatchUp = NetProtocol.DefaultCatchUp, int Style = NetProtocol.DefaultStyle)
{
    const byte ReadyFlag = 1, OperatorFlag = 2, ConnectedFlag = 4, KnownFlags = ReadyFlag | OperatorFlag | ConnectedFlag;

    public byte[] Encode()
    {
        NetWire.ValidateName(GameName, NetProtocol.MaxGameNameLength, nameof(GameName));
        ValidateOptions();
        if (ResumeToken is { Length: not (0 or NetProtocol.ResumeTokenLength) })
            throw new ArgumentException($"resume token must be {NetProtocol.ResumeTokenLength} bytes or empty", nameof(ResumeToken));
        var w = new WireWriter(NetHeader.Size + 1 + NetProtocol.MaxGameNameLength + 7 + 10 +
            NetProtocol.MaxSeats * (6 + NetProtocol.MaxNameLength + NetProtocol.MaxCarKeyLength) + 1 + NetProtocol.ResumeTokenLength + 8 + 4 +
            NetProtocol.KnockoutEntrants * (5 + NetProtocol.MaxNameLength));
        NetSessionProtocol.GameHeader(NetMessageType.LobbyState, GameId, Epoch).Write(w);
        w.Ascii(GameName, NetProtocol.MaxGameNameLength);
        w.U8((byte)Phase);
        w.U8(Track);
        w.U8(Laps);
        w.U8(MaxPlayers);
        w.U8((byte)Mode);
        w.Bool(Backward);
        w.I8(Bots);
        w.Bool(Traffic);
        w.U8(Skill);
        w.Bool(CatchUp);
        w.U8(Style);
        w.VarU(CountdownMillis);
        w.U8(Members.Count);
        var slots = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int operators = 0;
        foreach (var m in Members)
        {
            if (m.Slot < 0 || m.Slot >= MaxPlayers) throw new ArgumentOutOfRangeException(nameof(Members), "member slot outside the player limit");
            if (!slots.Add(m.Slot)) throw new ArgumentException($"duplicate member slot {m.Slot}", nameof(Members));
            NetWire.ValidateName(m.Name, NetProtocol.MaxNameLength, nameof(Members));
            if (!names.Add(m.Name)) throw new ArgumentException($"duplicate member name '{m.Name}'", nameof(Members));
            
            if (!NetProtocol.IsColourOrNone(m.Colour)) throw new ArgumentOutOfRangeException(nameof(Members), "invalid member colour");
            if (m.Operator) operators++;
            w.U8(m.Slot);
            w.Ascii(m.Name, NetProtocol.MaxNameLength);
            w.Ascii(m.Car?.Value ?? "", NetProtocol.MaxCarKeyLength);
            w.I8(m.Colour);
            w.U8((m.Ready ? ReadyFlag : 0) | (m.Operator ? OperatorFlag : 0) | (m.Connected ? ConnectedFlag : 0));
        }
        if (operators != 1) throw new ArgumentException("a lobby must have exactly one operator", nameof(Members));
        w.U8(checked((byte)(ResumeToken?.Length ?? 0)));
        if (ResumeToken is { } tokenBytes) w.Bytes(tokenBytes);
        w.VarU(CutoffMillis);
        w.Bool(Series != null);
        Series?.Write(w);
        return w.ToArray();
    }

    static bool TryCar(string text, out NFS.ModApi.CarKey? car)
    {
        bool ok = NFS.ModApi.CarKey.TryParse(text, out var key);
        car = ok ? key : null;
        return ok;
    }

    public static NetLobbyState Decode(NetHeader h, ref WireReader r)
    {
        NetSessionProtocol.RequireGame(h, NetMessageType.LobbyState);
        string gameName = NetWire.ReadName(ref r, NetProtocol.MaxGameNameLength);
        byte phase = r.U8();
        int track = r.U8(), laps = r.U8(), maxPlayers = r.U8(), mode = r.U8();
        bool backward = r.Bool();
        int bots = r.I8();
        bool traffic = r.Bool();
        int skill = r.U8();
        bool catchUp = r.Bool();
        int style = r.U8();
        int countdown = (int)r.VarU(NetProtocol.MaxCountdownMillis);
        int count = r.U8();
        if (count is < 1 or > NetProtocol.MaxSeats) throw new NetProtocolException("invalid lobby member count");
        var members = new NetLobbyMember[count];
        var slots = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int operators = 0;
        for (int i = 0; i < count; i++)
        {
            int slot = r.U8();
            string name = NetWire.ReadName(ref r, NetProtocol.MaxNameLength);
            string carText = r.Ascii(NetProtocol.MaxCarKeyLength);
            NFS.ModApi.CarKey? car = null;
            int colour = r.I8();
            int flags = r.U8();
            if ((flags & ~KnownFlags) != 0) throw new NetProtocolException("unknown lobby member flags");
            if (slot >= maxPlayers || !slots.Add(slot) || !names.Add(name))
                throw new NetProtocolException("invalid lobby member");
            if (carText.Length > 0 && !TryCar(carText, out car)) throw new NetProtocolException("invalid member car");
            if (!NetProtocol.IsColourOrNone(colour)) throw new NetProtocolException("invalid member colour");
            bool op = (flags & OperatorFlag) != 0;
            if (op) operators++;
            members[i] = new NetLobbyMember(slot, name, (flags & ReadyFlag) != 0, car, op, (flags & ConnectedFlag) != 0, colour);
        }
        int tokenLength = r.U8();
        if (tokenLength is not (0 or NetProtocol.ResumeTokenLength)) throw new NetProtocolException("invalid resume token length");
        var resumeToken = tokenLength == 0 ? null : r.Bytes(tokenLength).ToArray();
        int cutoff = (int)r.VarU(NetProtocol.MaxCutoffMillis);
        var series = r.Bool() ? NetKnockoutState.Read(ref r) : null;
        r.End();
        if (series != null && (NetGameMode)mode != NetGameMode.Knockout) throw new NetProtocolException("series state outside a knockout game");
        if (phase is < (byte)NetGamePhase.Lobby or > (byte)NetGamePhase.Race) throw new NetProtocolException("invalid game phase");
        if (maxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats || !NetProtocol.IsTrack(track) ||
            !NetProtocol.IsLaps(laps) || !NetProtocol.IsGameMode((NetGameMode)mode))
            throw new NetProtocolException("invalid game options");
        if (!NetProtocol.IsBotSetting(bots)) throw new NetProtocolException("invalid bot count");
        if (!NetProtocol.IsSkill(skill) || !NetProtocol.IsStyle(style)) throw new NetProtocolException("invalid skill or style");
        if (operators != 1) throw new NetProtocolException("a lobby must have exactly one operator");
        if ((NetGamePhase)phase == NetGamePhase.Countdown ? countdown is < 0 or > NetProtocol.MaxCountdownMillis : countdown != 0)
            throw new NetProtocolException("invalid countdown time");
        return new NetLobbyState(h.RaceId, h.Epoch, gameName, (NetGamePhase)phase, track, laps, maxPlayers,
            (NetGameMode)mode, countdown, Array.AsReadOnly(members), resumeToken, backward, bots, traffic, cutoff, series, skill, catchUp, style);
    }

    void ValidateOptions()
    {
        if (!NetProtocol.IsGameId(GameId) || Epoch == 0) throw new ArgumentOutOfRangeException(nameof(GameId), "invalid game header");
        if (!Enum.IsDefined(Phase)) throw new ArgumentOutOfRangeException(nameof(Phase));
        if (!NetProtocol.IsTrack(Track)) throw new ArgumentOutOfRangeException(nameof(Track));
        if (!NetProtocol.IsLaps(Laps)) throw new ArgumentOutOfRangeException(nameof(Laps));
        if (MaxPlayers is < NetProtocol.MinSeats or > NetProtocol.MaxSeats) throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
        if (!NetProtocol.IsGameMode(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!NetProtocol.IsBotSetting(Bots)) throw new ArgumentOutOfRangeException(nameof(Bots));
        if (!NetProtocol.IsSkill(Skill)) throw new ArgumentOutOfRangeException(nameof(Skill));
        if (!NetProtocol.IsStyle(Style)) throw new ArgumentOutOfRangeException(nameof(Style));
        if (Members.Count is < 1 or > NetProtocol.MaxSeats) throw new ArgumentOutOfRangeException(nameof(Members), "invalid member count");
        if (CutoffMillis is < 0 or > NetProtocol.MaxCutoffMillis) throw new ArgumentOutOfRangeException(nameof(CutoffMillis));
        if (Series != null && Mode != NetGameMode.Knockout) throw new ArgumentException("only a knockout game has a series", nameof(Series));
        if (Phase == NetGamePhase.Countdown ? CountdownMillis is < 0 or > NetProtocol.MaxCountdownMillis : CountdownMillis != 0)
            throw new ArgumentOutOfRangeException(nameof(CountdownMillis));
    }
}

internal static class NetWire
{
    internal static void ValidateName(string value, int max, string param)
    {
        if (value.Length < 1 || value.Length > max || value.Any(c => c is < ' ' or >= (char)0x7F))
            throw new ArgumentException($"name must be 1..{max} printable ASCII characters", param);
    }

    internal static string ReadName(ref WireReader r, int max)
    {
        string value = r.Ascii(max);
        if (value.Length == 0) throw new NetProtocolException("empty name");
        return value;
    }
}
