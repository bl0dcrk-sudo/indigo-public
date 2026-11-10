using NFS.ModApi;
using NFS.Core.Constants;
using NFS.Data.FrontEnd;
using NFS.Game.Cars;
using NFS.Game.Race;

namespace NFS.Game.FrontEnd;

/// <summary>Tag/value dword stream (terminated by 0) consumed by the original race setup. [ref:1634]
/// Implemented for game type 0 (one player) and 1 (split screen, both humans); [ref:1635]
/// network game types (2..4),
/// graphics tags (0x15..0x1C) and the fixed keys 0x3B..0x44 are not emitted.</summary>
public sealed class FeRaceArgsBuilder
{
    private readonly ushort[] _trackMap;
    private readonly int[] _df, _tyre, _brake, _gear;
    private readonly byte[] _colours;

    /// <summary>Runtime personality rotation (added in); 0 after start. [ref:1636]</summary>
    public int PersonalityBase { get; set; }
    /// <summary>Byte: index into the byte table shared by the tournament [ref:1637]
    /// personality, the traffic model and the traffic colour. The original seeds it from [ref:1638]
    /// time(); the host starts at zero. Advanced by <see cref="Build"/> (two steps per traffic car).</summary>
    public byte SequenceIndex { get; set; }
    private readonly bool[] _trafficUsed = new bool[11]; // cleared at the start of the traffic block [ref:1639]
    /// <summary>For a single race / free run (race type 0 or 3): 12 = tables C (menu "class A", CarRaceClass.A), 13 = class B, 14 = tables A (menu "class C", CarRaceClass.C) [ref:1640]
    /// picks <c>TournamentOpponents{class}[min(i, 10)]</c> – a fixed table by opponent index, no RNG (the "random opponent" of the
    /// menu is a class, not a dice roll). Any other value (race type 3 with a plain car) behaves as 12 (tables C); the split-screen shift of
    /// <see cref="SeriesOpponent"/> does not apply to these race types.</summary>
    public int ClassOpponentCar(FeConfig c, int i)
    {
        if (CarRaceClasses.FromOpponentValue(c.OpponentCar) is { } low && LowPoolCar(low, i) is { } lowCar) return lowCar;
        int cls = c.OpponentCar == 14 ? 2 : c.OpponentCar == 13 ? 1 : 0;
        ReadOnlySpan<short> table = cls == 2 ? FrontEndConstants.TournamentOpponentsA : cls == 1 ? FrontEndConstants.TournamentOpponentsB : FrontEndConstants.TournamentOpponentsC;
        return PoolCar(table, cls == 2 ? CarRaceClass.C : cls == 1 ? CarRaceClass.B : CarRaceClass.A, i);   // table A = the slowest pool = menu class C
    }

    /// <summary>Opponent of a low class (D..G, no cars in the original): the mod cars of the class in catalog order, repeated cyclically for the
    /// remaining slots (the colour comes from <see cref="OpponentColour"/>, so copies differ by colour); null when the class has no car.</summary>
    public int? LowPoolCar(CarRaceClass raceClass, int slot)
    {
        var numbers = CarRaceClasses.CarNumbers(_cars, raceClass);
        return numbers.Count == 0 ? null : numbers[Math.Min(slot, 10) % numbers.Count];
    }

    /// <summary>Last opponent slot (0-based) that a mod car of the class takes in the 11-entry opponent tables; the next mod car of the
    /// class takes the slot before it, and so on down to slot 0 (openspec modding-api-cars 4.4). Slot 6 is the last of a 7-opponent
    /// race, so the first mod car is in every full grid and the vanilla order of the first slots is changed as little as possible.</summary>
    public const int ModPoolFirstSlot = 6;

    /// <summary>Entry <paramref name="slot"/> (clamped to 10) of an opponent table: the table value, or – when a mod car of class
    /// <paramref name="raceClass"/> is in the catalog – the in-memory number of that mod car for the slots in
    /// <see cref="ModPoolFirstSlot"/> downwards. Without mod cars (the catalog of the original) this is exactly the table value.</summary>
    public int PoolCar(ReadOnlySpan<short> table, CarRaceClass raceClass, int slot)
    {
        slot = Math.Min(slot, 10);
        if (!_cars.HasModCars) return table[slot];
        int j = ModPoolFirstSlot - slot;
        if (j >= 0)
        {
            foreach (var car in _cars.Cars)
                if (car.LegacyIndex is null && car.Traits.RaceClass == raceClass && j-- == 0) return _cars.NumberOf(car.Key);
        }
        return table[slot];
    }
    /// <summary>Best lap from the .STF record (tag 0x1D); 0 when unknown. [ref:1641]</summary>
    public int TrackBest { get; set; }
    /// <summary>Cheat effects from the menu (<see cref="CheatCodes"/>): forced player/traffic car model, traffic crowd, flags of tag 0x14.</summary>
    public CheatEffects Cheats { get; set; } = new();

    private readonly CarCatalog _cars;

    /// <summary>Colour index of a mod car chosen by player 0 / 1 (the menu keeps it per car; the original config has no room for it); null = 0.</summary>
    public Func<int, int, int>? ModColourOf { get; set; }

    public FeRaceArgsBuilder(CarCatalog? cars = null)
    {
        _cars = cars ?? CarCatalog.VanillaTables;
        _trackMap = Array.ConvertAll(FrontEndConstants.TrackMap.ToArray(), v => (ushort)v);
        _df = FrontEndConstants.Downforce.ToArray();
        _tyre = FrontEndConstants.Tyre.ToArray();
        _brake = FrontEndConstants.Brake.ToArray();
        _gear = FrontEndConstants.Gear.ToArray();
        _colours = FrontEndConstants.ColourTable.ToArray();
    }

    /// <summary>menu track index → race track number ({0,2,4,5,3,6,7,8}). The original accepts 0..8, but the local copy [ref:1642]
    /// has 8 entries (index 8 reads past it); here 8 is treated as out of range → 0.</summary>
    public int MapTrack(int t) => (short)_trackMap[t is < 0 or > 7 ? 0 : t];

    /// <summary>Single race / split screen branch. [ref:1643]</summary>
    public int Personality(int i, FeConfig c)
    {
        int p = i + (c.RaceType == 1 ? c.TournamentOrder : PersonalityBase);
        while (p > 10) p -= 11;
        return p;
    }

    // Knockout: car class checks; KO model tables are s16[11]. [ref:1644]
    public int KnockoutOpponent(int playerModel, int opponent) => SeriesOpponent(true, playerModel, null, opponent);

    // non-KO single-player series branch: model determined by the player's class. [ref:1645]
    public int TournamentOpponent(int playerModel, int opponent) => SeriesOpponent(false, playerModel, null, opponent);

    /// <summary>For tournament / knockout. In split screen (<c>1</c>) the class follows both humans [ref:1646]
    /// (class A of either, else class B of either, else C) and the table index is shifted by one [ref:1647]
    /// (<c>opponent + 1</c>); one player reads only the player 1 car. <paramref name="player2Model"/> null = one player.</summary>
    public int SeriesOpponent(bool knockout, int player1Model, int? player2Model, int opponent)
    {
        CarRaceClass ClassOf(int m) => _cars.TryGetByNumber(m, out var car) ? car.Traits.RaceClass : CarRaceClass.A;
        // the tables named A are the slowest pool of the original, which the menu labels class C (CarRaceClass.C)
        // a low class (D..G, mods only) of a player: the slowest class of the players wins, as in the original (A of either, else B, else C)
        CarRaceClass? slowLow = null;
        foreach (int m in PlayerModels(player1Model, player2Model))
            if (ClassOf(m) is var k && CarRaceClasses.IsLow(k) && (slowLow is null || CarRaceClasses.SlowRank(k) < CarRaceClasses.SlowRank(slowLow.Value))) slowLow = k;
        if (slowLow is { } low && LowPoolCar(low, opponent + (player2Model != null ? 1 : 0)) is { } lowCar) return lowCar;
        bool ClassA(int m) => ClassOf(m) == CarRaceClass.C;
        bool ClassB(int m) => ClassOf(m) == CarRaceClass.B;
        bool a = ClassA(player1Model) || player2Model is { } p2a && ClassA(p2a);
        bool b = ClassB(player1Model) || player2Model is { } p2b && ClassB(p2b);
        ReadOnlySpan<short> table = knockout
            ? (a ? FrontEndConstants.KnockoutOpponentsA : b ? FrontEndConstants.KnockoutOpponentsB : FrontEndConstants.KnockoutOpponentsC)
            : (a ? FrontEndConstants.TournamentOpponentsA : b ? FrontEndConstants.TournamentOpponentsB : FrontEndConstants.TournamentOpponentsC);
        return PoolCar(table, a ? CarRaceClass.C : b ? CarRaceClass.B : CarRaceClass.A, opponent + (player2Model != null ? 1 : 0));
    }

    private static IEnumerable<int> PlayerModels(int p1, int? p2) => p2 is { } v ? [p1, v] : [p1];

    /// <summary>traffic model for the menu track (1..7; elsewhere 0x12). picks [ref:1648]
    /// <c>table[seq++] % count</c> and walks to the next unused pool slot (each slot once per race); the used set is cleared [ref:1649]
    /// before the four cars. [ref:1650]</summary>
    public int PickTrafficModel(int menuTrack)
    {
        if ((byte)(menuTrack - 1) >= 7) return 0x12;
        byte[] pool = FrontEndConstants.TrafficPools[menuTrack - 1];
        int count = pool[0];
        int i = FrontEndConstants.SequenceTable[SequenceIndex++] % count;
        while (_trafficUsed[i]) i = (i + 1) % count;
        _trafficUsed[i] = true;
        return pool[1 + i];
    }

    /// <summary>traffic colour = byte <c>table[seq++] % 16</c> of the 16 traffic colours. [ref:1651]</summary>
    public int PickTrafficColour()
        => FrontEndConstants.TrafficColours[FrontEndConstants.SequenceTable[SequenceIndex++] % 0x10];

    /// <summary>colour = first (personality+1) % count. [ref:1652]</summary>
    public int OpponentColour(int car, int personality)
    {
        if (car >= CarCatalog.VanillaCount) return _cars.TryGetByNumber(car, out var mod) && mod.Colors.Count > 0 ? (personality + 1) % mod.Colors.Count : 0;   // mod car: index into car.json "colors"
        int n = _colours[car * 2 + 1];
        if (n == 0) n = 1;
        return _colours[car * 2] + (personality + 1) % n;
    }

    /// <summary>tournament 4 laps, knockout 2, otherwise cfg+0x18. [ref:1653]</summary>
    public static int Laps(FeConfig c) => c.RaceType switch { 1 => 4, 2 => 2, _ => c.Laps };

    public int[] Build(FeConfig c, KnockoutSeries? knockout = null, TournamentSeries? tournament = null)
    {
        if (c.GameType > 1) throw new NotSupportedException("network game types are not ported");
        var s = new List<int>();
        void E(int tag, int v) { s.Add(tag); s.Add(v); }

        E(7, 1); E(0xC, c.Seed); E(0xE, 0); E(0xF, 3); E(0x13, c.Language); E(0x1E, c.Units);
        // a split knockout with only one human left runs as a one-view race (game type 0); the human flags come from the active mask. [ref:1654]
        bool human0 = knockout?.HumanActive(0) ?? true;
        bool human1 = c.GameType == 1 && (knockout?.HumanActive(1) ?? true);
        if (c.GameType == 0) human0 = true;
        E(6, c.GameType == 1 && c.RaceType == 2 && !(human0 && human1) ? 0 : c.GameType);
        E(2, c.RaceType == 3 ? 0 : c.RaceType);
        if (c.Style == 2) { E(5, 1); E(0x21, 0); } else { E(5, c.Style); E(0x21, c.Style); }
        bool forceSkill = c.RaceType is 1 or 2 || c.P1SlotCar != 0 || (c[0x4B] != 0 && c.GameType == 1);
        E(4, forceSkill ? 1 : c.Skill);
        E(9, c.CatchUp);
        E(0xA, MapTrack(c.Track));
        E(0x1D, TrackBest);
        E(3, Laps(c));
        E(0x1F, c.Backward);
        E(0x20, c.Mirrored);
        s.Add(0x22);
        if (knockout is { Stage: > 0 })
        {
            s.Add(knockout.RaceIdentities.Count);
            foreach (int id in knockout.RaceIdentities) s.Add(knockout.GridSlot(id));
        }
        else if (tournament != null)
        { s.Add(8); for (int i = 0; i < 8; i++) s.Add(tournament.GridSlot(i)); }
        else { s.Add(0xD); for (int i = 0; i < 13; i++) s.Add(i); } // first knockout / single race [ref:1655]

        int index = 0;
        int humans = c.GameType == 0 ? 1 : 2;
        if (human0) HumanBlock(s, c, index++, 0x1F, P1Tune);
        if (human1) HumanBlock(s, c, index++, 0x3A, P2Tune);
        E(0x10, index); E(8, 0);

        int opponents = c.RaceType switch
        {
            0 or 3 => c.GameType == 1 ? (c.OpponentCount == 2 ? 6 : 0) : (c.OpponentCount == 2 ? 8 - humans : c.OpponentCount),
            1 => 8 - humans,
            _ => 9 - humans,
        };
        int emittedOpponents = 0;
        for (int i = 0; i < opponents; i++)
        {
            if (knockout != null && !knockout.RaceIdentities.Contains(i + humans)) continue;
            emittedOpponents++;
            int? p2 = c.GameType == 1 ? _cars.ToNumber(c.P2Car) ?? c.P2Car : null;
            int p1 = _cars.ToNumber(c.P1Car) ?? c.P1Car;
            int car = c.RaceType == 2 ? SeriesOpponent(true, p1, p2, i)
                : c.RaceType == 1 ? SeriesOpponent(false, p1, p2, i)
                : c.RaceType == 0 && (c.OpponentCar < 12 || c.OpponentCar >= CarCatalog.VanillaCount) ? c.OpponentCar : ClassOpponentCar(c, i);
            int pers = Personality(i, c);
            E(0x7B, index); s.Add(car);
            E(0x7D, index); s.Add(1);
            E(0x7E, index); s.Add(OpponentColour(car, pers));
            E(0x7C, index); s.Add(2);
            E(0x87, index); s.Add(pers);
            index++;
        }
        int oppCar = c.OpponentCount == 2 && c.OpponentCar is > 11 and < CarCatalog.VanillaCount ? Math.Min((int)c.OpponentCar, 14) : c.OpponentCar;
        E(0x12, oppCar); E(0x11, emittedOpponents);

        if (c.TrafficOff == 0 && c.OpponentCount != 2 && c.RaceType == 0 && c.Track != 0)
        {
            Array.Clear(_trafficUsed);
            if (!Cheats.Crowd)
            {
                for (int i = 0; i < 4; i++)
                {
                    int car = PickTrafficModel(c.Track);
                    E(0x7B, index); s.Add(car);
                    E(0x7D, index); s.Add(1);
                    E(0x7E, index); s.Add(PickTrafficColour());
                    E(0x7C, index); s.Add(4);
                    index++;
                }
            }
            else
            {
                // (cheat): 11 cars (10 when +0x56 != 0, minus 1 in split screen); first 4 model/colour pairs [ref:1656]
                // (model from the picker, or the forced model), then each car takes pair i % 4. [ref:1657]
                int n = c.OpponentCount != 0 ? 10 : 11;
                if (c.GameType == 1) n--;
                var models = new int[4]; var colours = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    models[i] = Cheats.ForcedModel != 0 ? Cheats.ForcedModel : PickTrafficModel(c.Track);
                    colours[i] = PickTrafficColour();
                }
                for (int i = 0; i < n; i++)
                {
                    E(0x7B, index); s.Add(models[i % 4]);
                    E(0x7D, index); s.Add(1);
                    E(0x7E, index); s.Add(colours[i % 4]);
                    E(0x7C, index); s.Add(4);
                    index++;
                }
            }
        }
        E(0xD, index);

        // Keys (raw dwords, only rewrites joystick axes so keyboard words pass unchanged); ←/→ swapped when mirrored. [ref:1658]
        // One player: table cfg +0x6C. Split screen (case 1): player 1 from +0x94 + 8·i, player 2 from +0x98 + 8·i [ref:1659]
        // (tags 0x2F..0x3A, 0x54, 0x55); F11 (0x57) = tag 0x28 and F12 (0x58) = tag 0x34 are the reset-car buttons of 1 and 2.
        int[] keyTags = [0x25, 0x26, 0x52, 0x53, 0x2B, 0x2C, 0x2D, 0x2E];
        if (c.GameType == 1)
        {
            // Only player 2 left (index = 1): his keys fill the player 1 tags, no player 2 tags follow. [ref:1660]
            int owner = human0 ? 0 : 1;
            E(c.Mirrored == 0 ? 0x23 : 0x24, c.SplitKey(owner, 0));
            E(c.Mirrored == 0 ? 0x24 : 0x23, c.SplitKey(owner, 1));
            for (int k = 0; k < keyTags.Length; k++) E(keyTags[k], c.SplitKey(owner, 2 + k));
            E(0x28, KeyBindings.Word(0x57, 0));
            if (human0)
            {
                E(c.Mirrored == 0 ? 0x2F : 0x30, c.SplitKey(1, 0));
                E(c.Mirrored == 0 ? 0x30 : 0x2F, c.SplitKey(1, 1));
                int[] p2Tags = [0x31, 0x32, 0x54, 0x55, 0x37, 0x38, 0x39, 0x3A];
                for (int k = 0; k < p2Tags.Length; k++) E(p2Tags[k], c.SplitKey(1, 2 + k));
                E(0x34, KeyBindings.Word(0x58, 0));
            }
        }
        else
        {
            E(c.Mirrored == 0 ? 0x23 : 0x24, c.P1Key(0));
            E(c.Mirrored == 0 ? 0x24 : 0x23, c.P1Key(1));
            for (int k = 0; k < keyTags.Length; k++) E(keyTags[k], c.P1Key(2 + k));
        }
        E(0x14, Cheats.Flags(c));
        s.Add(0); s.Add(0);
        return [.. s];
    }

    /// <summary>human block: car, transmission, colour, class 1, tuning (single race only), digital flags. [ref:1661]</summary>
    // cfg offsets: tyre(0x84), df front, df rear, gear(0x86), brake(0x85), digital steer/gas/brake (0x7F..0x81)
    private static readonly int[] P1Tune = [0x32, 0x33, 0x34, 0x35, 0x37, 0x36, 0x38, 0x39];
    private static readonly int[] P2Tune = [0x4C, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x54];

    private void HumanBlock(List<int> s, FeConfig c, int idx, int b, int[] t)
    {
        void E(int tag, int v) { s.Add(tag); s.Add(idx); s.Add(v); }
        int car = c[b];
        int model = _cars.ToNumber(car) ?? car; // FeConfig stores the original index; the race args carry the in-memory number
        E(0x7B, Cheats.ForcedModel != 0 ? Cheats.ForcedModel : model); // model tag for player 1 or player 2 [ref:1662]
        E(0x7D, c[b + 1]);
        E(0x7E, car >= CarCatalog.VanillaCount ? ModColourOf?.Invoke(b == 0x1F ? 0 : 1, car) ?? 0 : c[b + 2 + car]);   // mod car: index into car.json "colors" (kept by the menu, not in the original config bytes)
        E(0x7C, 1);
        bool single = c.RaceType == 0;
        E(0x82, single ? _df[c[t[1]]] : _df[0]);
        E(0x83, single ? _df[c[t[2]]] : _df[0]);
        E(0x84, single ? _tyre[c[t[0]]] : _tyre[1]);
        E(0x85, single ? _brake[c[t[4]]] : _brake[1]);
        E(0x86, single ? _gear[c[t[3]]] : _gear[1]);
        E(0x7F, c[t[5]]);
        E(0x80, c[t[6]]);
        E(0x81, c[t[7]]);
    }
}
