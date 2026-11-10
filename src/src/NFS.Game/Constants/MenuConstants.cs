using NFS.Game.FrontEnd;

namespace NFS.Game.Constants;

/// <summary>
/// Front-end menu tree as constants (<c>MenuTables.Load</c> builds the <see cref="MenuTree"/> from these; the texts are
/// added from TEXT.ENG at load time). Taken from the original game: screens with their items and handler names, plus
/// the 0-terminated u16 text-id lists read by the dynamic item code [ref:1558]
/// <c>MenuController.DynamicItem</c> and the kind 1 shape items drawn by <c>MenuScene.DrawShapeItem</c>.
/// Screen ids are names; an item id is <c>screen/index</c>.
/// </summary>
public static class MenuConstants
{
    /// <summary>Raw bytes of a kind 1 item (shape picture) at +2 (<see cref="Flags"/>), +4 (normal shape) and +8 (selected
    /// shape); the names are FSH shape names.</summary>
    /// <param name="Flags">raw byte +2 (low byte of the text id); bit 0 = not drawn</param>
    public readonly record struct ShapeItem(byte Flags, string Normal, string Selected);

    /// <summary>Handler names, indexed by <see cref="MenuScreen.Type"/>. [ref:1559]</summary>
    public static IReadOnlyList<string> Handlers { get; } =
    [
        "Generic", // 0
        "MainMenu", // 1
        "Setup", // 2
        "RaceResults", // 3
        "LapTimes", // 4
        "TournamentStandings", // 5
        "TournamentFinal", // 6
        "Showcase", // 7
        "History", // 8
        "LoadSave", // 9
        "TrackRecords", // 10
        "TrackInfo", // 11
        "SlideShow", // 12
        "NetLobby", // 13
        "Audio", // 14
        "KnockoutStandings", // 15
        "KnockoutFinal", // 16
        "JoyCalibration", // 17
        "Credits", // 18
        "SplitControls", // 19
        "ForceFeedback", // 20
        "PersonalStats", // 21
        "ViewRecords", // 22
    ];

    /// <summary>Item targets inside the region that are not screen structs (none in the original game).</summary>
    public static IReadOnlyList<string> NonScreenTargets { get; } = [];

    /// <summary>All screens of the front end, one block per screen (in the original's order).</summary>
    public static IReadOnlyList<MenuScreen> Screens { get; } =
    [
        new MenuScreen("serial-link", "main", "blk4", 2, 0,
            [
                new MenuItem("serial-link/0", 2, 33, null, null, 4, 0, 0x1A4, null, [36, 37, 38, 39], null, null),
                new MenuItem("serial-link/1", 2, 34, null, "net-lobby", 2, 8, null, null, [], null, null),
                new MenuItem("serial-link/2", 2, 35, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("serial-link/3", 0, 32, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("modem", "main", "blk4", 0, 0,
            [
                new MenuItem("modem/0", 2, 41, null, null, 4, 2, 0x1A8, null, [], null, null),
                new MenuItem("modem/1", 2, 42, null, null, 4, 3, 0x1BE, null, [], null, null),
                new MenuItem("modem/2", 2, 43, null, null, 0, 4, null, null, [], null, null),
                new MenuItem("modem/3", 2, 44, null, "net-lobby", 2, 8, null, null, [], null, null),
                new MenuItem("modem/4", 2, 45, null, "net-lobby", 2, 9, null, null, [], null, null),
                new MenuItem("modem/5", 2, 46, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("modem/6", 0, 40, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("network", "main", "blk4", 2, 0,
            [
                new MenuItem("network/0", 2, 49, null, null, 4, 5, null, null, [], null, null),
                new MenuItem("network/1", 2, 50, null, "net-lobby", 0, 7, null, null, [], null, null),
                new MenuItem("network/2", 2, 51, null, "net-lobby", 0, 6, null, null, [], null, null),
                new MenuItem("network/3", 2, 53, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("network/4", 0, 47, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("game-setup", "main", "blk4", 2, 0,
            [
                new MenuItem("game-setup/0", 2, 9, null, null, 4, 0, 0x9, null, [17, 18, 21], null, null),   // one player, split screen, network (modem 19 / serial 20 dropped: network-game-sessions 5.4)
                new MenuItem("game-setup/1", 2, 10, null, null, 4, 0, 0xA, null, [22, 23, 24], null, null),
                new MenuItem("game-setup/2", 2, 11, null, null, 4, 0, 0xB, null, [25, 27, 26], null, null),
                new MenuItem("game-setup/3", 2, 12, null, null, 4, 0, 0xE, null, [216, 215], null, null),
                new MenuItem("game-setup/4", 2, 13, null, null, 4, 0, 0x1A, null, [216, 215], null, null),
                new MenuItem("game-setup/5", 2, 14, null, null, 4, 0, 0x1B, null, [216, 215], null, null),
                new MenuItem("game-setup/6", 2, 15, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("game-setup/7", 2, 16, null, "back", 16, 0, null, null, [], null, null),
            ]),
        new MenuScreen("personal-stats", null, "back", 21, 0,
            [
                new MenuItem("personal-stats/0", 2, 188, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("personal-stats/1", 2, 189, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("personal-stats/2", 2, 190, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("personal-stats/3", 2, 191, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("personal-stats/4", 2, 192, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("track-info", null, "back", 11, 0,
            [
                new MenuItem("track-info/0", 2, 155, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("track-info/1", 2, 156, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("track-records", null, "back", 10, 0,
            [
                new MenuItem("track-records/0", 2, 169, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("track-records/1", 2, 170, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("track-records/2", 2, 172, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("track-records/3", 2, 173, null, "personal-stats", 0, 0, null, null, [], null, null),
                new MenuItem("track-records/4", 2, 174, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("location", "main", "blk4", 2, 0,
            [
                new MenuItem("location/0", 2, 54, null, null, 4, 0, 0x10, null, [61, 62, 63, 64, 65, 66, 67], null, null),
                new MenuItem("location/1", 2, 55, null, null, 4, 22, 0x19, null, [69, 70, 71], null, null),
                new MenuItem("location/2", 2, 56, null, "track-info", 2, 0, null, null, [], null, null),
                new MenuItem("location/3", 2, 57, null, "track-records", 2, 0, null, null, [], null, null),
                new MenuItem("location/4", 2, 58, null, "personal-stats", 2, 0, null, null, [], null, null),
                new MenuItem("location/5", 2, 59, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("location/6", 2, 60, null, "back", 16, 0, null, null, [], null, null),
            ]),
        new MenuScreen("car-settings-p1", "main", "blk4", 0, 0,
            [
                new MenuItem("car-settings-p1/0", 2, 208, null, null, 4, 0, 0x33, null, [217, 361, 218], null, null),
                new MenuItem("car-settings-p1/1", 2, 209, null, null, 4, 0, 0x34, null, [217, 361, 218], null, null),
                new MenuItem("car-settings-p1/2", 2, 210, null, null, 4, 0, 0x37, null, [222, 223, 224], null, null),
                new MenuItem("car-settings-p1/3", 2, 211, null, null, 4, 0, 0x35, null, [221, 217, 220], null, null),
                new MenuItem("car-settings-p1/4", 2, 212, null, null, 0, 10, null, null, [], null, null),
                new MenuItem("car-settings-p1/5", 2, 213, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("car-settings-p1/6", 2, 214, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("car-settings-p1/7", 0, 204, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("car-settings-p1/8", 0, 206, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("car-settings-p2", "main", "blk4", 0, 0,
            [
                new MenuItem("car-settings-p2/0", 2, 208, null, null, 4, 0, 0x4E, null, [217, 361, 218], null, null),
                new MenuItem("car-settings-p2/1", 2, 209, null, null, 4, 0, 0x4F, null, [217, 361, 218], null, null),
                new MenuItem("car-settings-p2/2", 2, 210, null, null, 4, 0, 0x51, null, [222, 223, 224], null, null),
                new MenuItem("car-settings-p2/3", 2, 211, null, null, 4, 0, 0x50, null, [221, 217, 220], null, null),
                new MenuItem("car-settings-p2/4", 2, 212, null, null, 0, 10, null, null, [], null, null),
                new MenuItem("car-settings-p2/5", 2, 213, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("car-settings-p2/6", 2, 214, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("car-settings-p2/7", 0, 205, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("car-settings-p2/8", 0, 206, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("slide-show", null, "", 12, 0,
            [
                new MenuItem("slide-show/0", 2, 231, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("slide-show/1", 2, 232, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("slide-show/2", 2, 233, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("credits", "credits", null, 18, 0,
            [
                new MenuItem("credits/0", 0, 382, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("history", null, null, 8, 0,
            [
                new MenuItem("history/0", 2, 781, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("history/1", 2, 782, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("showcase", null, null, 7, 0,
            [
                new MenuItem("showcase/0", 2, 225, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("showcase/1", 2, 226, null, null, 0, 16, null, null, [], null, null),
                new MenuItem("showcase/2", 2, 227, null, "history", 2, 0, null, null, [], null, null),
                new MenuItem("showcase/3", 2, 228, null, "slide-show", 2, 0, null, null, [], null, null),
                new MenuItem("showcase/4", 2, 229, null, null, 2, 1, null, null, [], null, null),
                new MenuItem("showcase/5", 2, 230, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("showcase/6", 0, 381, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/7", 0, 382, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/8", 0, 383, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/9", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/10", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/11", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/12", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/13", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/14", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/15", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/16", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/17", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/18", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/19", 0, 343, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("showcase/20", 0, 343, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("player-one", "main", "blk4", 2, 0,
            [
                new MenuItem("player-one/0", 2, 74, null, null, 4, 0, 0x1F, null, [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104], null, null),
                new MenuItem("player-one/1", 2, 75, null, null, 4, 0, 0x20, null, [92, 91], null, null),
                new MenuItem("player-one/2", 2, 76, null, null, 4, 15, null, 0x1, [], null, null),
                new MenuItem("player-one/3", 2, 77, null, null, 4, 0, 0x31, null, [216, 215], null, null),
                new MenuItem("player-one/4", 2, 78, null, "car-settings-p1", 2, 0, null, null, [], null, null),
                new MenuItem("player-one/5", 2, 79, null, "showcase", 2, 0, null, null, [], null, null),
                new MenuItem("player-one/6", 2, 80, null, null, 4, 0, 0x30, null, [216, 215], null, null),
                new MenuItem("player-one/7", 2, 81, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("player-one/8", 2, 83, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("player-one/9", 0, 72, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("player-two", "main", "blk4", 2, 0,
            [
                new MenuItem("player-two/0", 2, 74, null, null, 4, 0, 0x3A, null, [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104], null, null),
                new MenuItem("player-two/1", 2, 75, null, null, 4, 0, 0x3B, null, [92, 91], null, null),
                new MenuItem("player-two/2", 2, 76, null, null, 4, 15, null, 0x2, [], null, null),
                new MenuItem("player-two/3", 2, 77, null, null, 4, 0, 0x31, null, [216, 215], null, null),
                new MenuItem("player-two/4", 2, 78, null, "car-settings-p2", 2, 0, null, null, [], null, null),
                new MenuItem("player-two/5", 2, 79, null, "showcase", 2, 0, null, null, [], null, null),
                new MenuItem("player-two/6", 2, 80, null, null, 4, 0, 0x4B, null, [216, 215], null, null),
                new MenuItem("player-two/7", 2, 81, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("player-two/8", 2, 83, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("player-two/9", 0, 73, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("opponents", "main", "blk4", 2, 0,
            [
                new MenuItem("opponents/0", 2, 110, null, null, 4, 0, 0x55, null, [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 121, 122, 123, 117], null, null),
                new MenuItem("opponents/1", 2, 111, null, null, 4, 0, 0x56, null, [118, 119, 120], null, null),
                new MenuItem("opponents/2", 2, 112, null, null, 4, 0, 0xD, null, [28, 29], null, null),
                new MenuItem("opponents/3", 2, 113, null, null, 4, 23, 0x58, null, [215, 216], null, null),
                new MenuItem("opponents/4", 2, 114, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("opponents/5", 2, 115, null, "back", 16, 0, null, null, [], null, null),
            ]),
        new MenuScreen("audio", "main", "blk4", 14, 0,
            [
                new MenuItem("audio/0", 2, 235, null, null, 4, 18, 0x5C, null, [], null, null),
                new MenuItem("audio/1", 2, 236, null, null, 4, 18, 0x5D, null, [], null, null),
                new MenuItem("audio/2", 2, 237, null, null, 4, 18, 0x5E, null, [], null, null),
                new MenuItem("audio/3", 2, 238, null, null, 4, 0, 0x19F, null, [216, 215], null, null),
                new MenuItem("audio/4", 2, 239, null, null, 4, 0, 0x1A0, null, [253, 254, 255, 256, 257, 258, 259, 260, 261, 262, 263, 264, 265, 266, 267, 268, 269, 270], null, null),
                new MenuItem("audio/5", 2, 240, null, null, 4, 0, 0x19E, null, [249, 250], null, null),
                new MenuItem("audio/6", 2, 241, null, null, 4, 0, 0x5F, null, [247, 248], null, null),
                new MenuItem("audio/7", 2, 242, null, null, 4, 0, 0x1A1, null, [245, 246], null, null),
                new MenuItem("audio/8", 2, 243, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("audio/9", 2, 244, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("audio/10", 0, 234, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("controls-setup", "main", "blk4", 0, 0,
            [
                new MenuItem("controls-setup/0", 2, 272, null, null, 4, 11, 0x6C, null, [], null, null),
                new MenuItem("controls-setup/1", 2, 273, null, null, 4, 11, 0x70, null, [], null, null),
                new MenuItem("controls-setup/2", 2, 274, null, null, 4, 11, 0x74, null, [], null, null),
                new MenuItem("controls-setup/3", 2, 275, null, null, 4, 11, 0x78, null, [], null, null),
                new MenuItem("controls-setup/4", 2, 276, null, null, 4, 11, 0x84, null, [], null, null),
                new MenuItem("controls-setup/5", 2, 277, null, null, 4, 11, 0x8C, null, [], null, null),
                new MenuItem("controls-setup/6", 2, 278, null, null, 4, 11, 0x90, null, [], null, null),
                new MenuItem("controls-setup/7", 2, 279, null, null, 4, 11, 0x80, null, [], null, null),
                new MenuItem("controls-setup/8", 2, 280, null, null, 4, 11, 0x7C, null, [], null, null),
                new MenuItem("controls-setup/9", 2, 281, null, null, 4, 11, 0x88, null, [], null, null),
                new MenuItem("controls-setup/10", 2, 284, null, null, 0, 19, null, null, [], null, null),
                new MenuItem("controls-setup/11", 2, 282, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("controls-setup/12", 2, 283, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("controls-setup/13", 0, 271, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("split-controls", "main", "blk4", 19, 0,
            [
                new MenuItem("split-controls/0", 2, 312, null, null, 4, 12, 0x94, null, [], null, null),
                new MenuItem("split-controls/1", 2, 313, null, null, 4, 12, 0x9C, null, [], null, null),
                new MenuItem("split-controls/2", 2, 314, null, null, 4, 12, 0xA4, null, [], null, null),
                new MenuItem("split-controls/3", 2, 315, null, null, 4, 12, 0xAC, null, [], null, null),
                new MenuItem("split-controls/4", 2, 316, null, null, 4, 12, 0xC4, null, [], null, null),
                new MenuItem("split-controls/5", 2, 317, null, null, 4, 12, 0xD4, null, [], null, null),
                new MenuItem("split-controls/6", 2, 318, null, null, 4, 12, 0xDC, null, [], null, null),
                new MenuItem("split-controls/7", 2, 319, null, null, 4, 12, 0xBC, null, [], null, null),
                new MenuItem("split-controls/8", 2, 320, null, null, 4, 12, 0xB4, null, [], null, null),
                new MenuItem("split-controls/9", 2, 321, null, null, 4, 12, 0xCC, null, [], null, null),
                new MenuItem("split-controls/10", 2, 284, null, null, 0, 19, null, null, [], null, null),
                new MenuItem("split-controls/11", 2, 282, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("split-controls/12", 2, 283, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("split-controls/13", 0, 309, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("split-controls/14", 0, 310, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("split-controls/15", 0, 311, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("steering-sensitivity", "main", "blk4", 0, 0,
            [
                new MenuItem("steering-sensitivity/0", 2, 336, null, null, 4, 0, 0x60, null, [339, 340, 341, 342], null, null),
                new MenuItem("steering-sensitivity/1", 2, 337, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("steering-sensitivity/2", 2, 338, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("steering-sensitivity/3", 0, 335, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("force-feedback", "main", "blk4", 20, 0,
            [
                new MenuItem("force-feedback/0", 2, 347, null, null, 4, 18, 0x134, null, [], null, null),
                new MenuItem("force-feedback/1", 2, 348, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("force-feedback/2", 2, 349, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("force-feedback/3", 0, 344, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("force-feedback/4", 0, 345, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("controls", "main", "blk4", 0, 0,
            [
                new MenuItem("controls/0", 2, 325, null, "controls-setup", 2, 0, null, null, [], null, null),
                new MenuItem("controls/1", 2, 326, null, "steering-sensitivity", 2, 0, null, null, [], null, null),
                new MenuItem("controls/2", 2, 327, null, "force-feedback", 2, 21, null, null, [], null, null),
                new MenuItem("controls/3", 2, 328, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("controls/4", 0, 322, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("graphics", "main", "blk4", 0, 0,
            [
                new MenuItem("graphics/0", 2, 352, null, null, 4, 0, 0x12E, null, [360, 361, 362], null, null),
                new MenuItem("graphics/1", 2, 354, null, null, 4, 0, 0x12F, null, [363, 364, 366, 365, 367], null, null),
                new MenuItem("graphics/2", 2, 355, null, null, 4, 0, 0x130, null, [368, 364, 369], null, null),
                new MenuItem("graphics/3", 2, 356, null, null, 4, 0, 0x132, null, [215, 216], null, null),
                new MenuItem("graphics/4", 2, 357, null, null, 0, 20, null, null, [], null, null),
                new MenuItem("graphics/5", 2, 358, null, "back", 8, 0, null, null, [], null, null),
                new MenuItem("graphics/6", 2, 359, null, "back", 16, 0, null, null, [], null, null),
                new MenuItem("graphics/7", 0, 350, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("load-save", "main", "blk4", 9, 0,
            [
                new MenuItem("load-save/0", 2, 373, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/1", 2, 374, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/2", 2, 375, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/3", 2, 376, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/4", 2, 377, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/5", 2, 378, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("load-save/6", 2, 379, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("load-save/7", 0, 372, null, null, 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("options", "main", "blk4", 2, 0,
            [
                new MenuItem("options/0", 2, 124, null, null, 4, 0, 0x5A, null, [132, 133], null, null),
                new MenuItem("options/1", 2, 125, null, "audio", 2, 0, null, null, [], null, null),
                new MenuItem("options/2", 2, 126, null, "controls", 2, 0, null, null, [], null, null),
                new MenuItem("options/3", 2, 127, null, "load-save", 2, 0, null, null, [], null, null),
                new MenuItem("options/4", 2, 128, null, "graphics", 2, 0, null, null, [], null, null),
                new MenuItem("options/5", 2, 129, null, null, 4, 0, 0xF, null, [216, 215], null, null),
                new MenuItem("options/6", 2, 130, null, "credits", 2, 0, null, null, [], null, null),
                new MenuItem("options/7", 2, 131, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("knockout-final", "post", "back", 16, 0,
            [
                new MenuItem("knockout-final/0", 2, 727, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("knockout-final/1", 2, 731, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("knockout-final/2", 2, 733, null, "race", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("knockout-standings", "post", "back", 15, 0,
            [
                new MenuItem("knockout-standings/0", 2, 727, null, "knockout-final", 0, 0, null, null, [], null, null),
                new MenuItem("knockout-standings/1", 2, 731, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("knockout-standings/2", 2, 733, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("knockout-standings/3", 2, 734, null, "quit", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("tournament-final", "post", "back", 6, 0,
            [
                new MenuItem("tournament-final/0", 2, 713, null, "knockout-standings", 0, 0, null, null, [], null, null),
                new MenuItem("tournament-final/1", 2, 715, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("tournament-final/2", 2, 716, null, "race", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("tournament-standings", "post", "back", 5, 0,
            [
                new MenuItem("tournament-standings/0", 2, 700, null, "tournament-final", 0, 0, null, null, [], null, null),
                new MenuItem("tournament-standings/1", 2, 703, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("tournament-standings/2", 2, 704, null, "race", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("lap-times", "post", "back", 4, 0,
            [
                new MenuItem("lap-times/0", 2, 680, null, "tournament-standings", 0, 0, null, null, [], null, null),
                new MenuItem("lap-times/1", 2, 683, null, "back", 0, 0, null, null, [], null, null),
                new MenuItem("lap-times/2", 2, 684, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("lap-times/3", 2, 686, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("lap-times/4", 2, 687, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("lap-times/5", 1, 0, null, "shape:twlf", 116, 121, null, 0x1, [], null, "twrt"),
                new MenuItem("lap-times/6", 1, 0, null, "shape:twrt", 116, 121, null, 0x2B00002, [], null, "...."),
            ]),
        new MenuScreen("race-results", "post", "back", 3, 0,
            [
                new MenuItem("race-results/0", 2, 635, null, "lap-times", 0, 0, null, null, [], null, null),
                new MenuItem("race-results/1", 2, 636, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("race-results/2", 2, 637, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("race-results/3", 2, 639, null, null, 0, 0, null, null, [], null, null),
                new MenuItem("race-results/4", 1, 0, null, "shape:bwlf", 98, 121, null, 0x1, [], null, "bwrt"),
                new MenuItem("race-results/5", 1, 0, null, "shape:bwrt", 98, 121, null, null, [24930, 27491], null, null),
            ]),
        new MenuScreen("main", "main", "blk6", 1, 0,
            [
                new MenuItem("main/0", 2, 0, null, "game-setup", 0, 0, null, null, [], null, null),
                new MenuItem("main/1", 2, 1, null, "location", 0, 0, null, null, [], null, null),
                new MenuItem("main/2", 2, 2, null, "player-one", 0, 0, null, null, [], null, null),
                new MenuItem("main/3", 2, 3, null, "player-two", 0, 0, null, null, [], null, null),
                new MenuItem("main/4", 2, 4, null, "opponents", 0, 0, null, null, [], null, null),
                new MenuItem("main/5", 2, 5, null, "race", 0, 0, null, null, [], null, null),
                new MenuItem("main/6", 2, 7, null, "options", 0, 0, null, null, [], null, null),
                new MenuItem("main/7", 2, 8, null, "back", 0, 0, null, null, [], null, null),
            ]),
        new MenuScreen("net-lobby", null, "back", 13, 6,
            [
                new MenuItem("net-lobby/0", 2, 134, null, null, 4, 0, 0xA, null, [22, 23, 24], null, null),
                new MenuItem("net-lobby/1", 2, 138, null, null, 4, 0, 0xB, null, [25, 27, 26], null, null),
                new MenuItem("net-lobby/2", 2, 140, null, null, 4, 0, 0x10, null, [61, 62, 63, 64, 65, 66, 67, 68], null, null),
                new MenuItem("net-lobby/3", 2, 141, null, null, 4, 0, 0x19, null, [69, 70, 71], null, null),
                new MenuItem("net-lobby/4", 2, 142, null, null, 4, 0, 0x1A3, null, [152, 153, 154], null, null),
                new MenuItem("net-lobby/5", 2, 143, null, null, 4, 0, 0x1A2, null, [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 121, 122, 123, 108], null, null),
                new MenuItem("net-lobby/6", 2, 144, null, null, 4, 0, 0x58, null, [215, 216], null, null),
                new MenuItem("net-lobby/7", 2, 145, null, null, 4, 0, 0x1F, null, [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104], null, null),
                new MenuItem("net-lobby/8", 2, 146, null, null, 4, 0, 0x20, null, [92, 91], null, null),
                new MenuItem("net-lobby/9", 2, 147, null, null, 4, 15, null, 0x1, [], null, null),
                new MenuItem("net-lobby/10", 2, 148, null, null, 4, 0, 0x1A5, null, [343, 343], null, null),
                new MenuItem("net-lobby/11", 2, 151, null, "back", 0, 0, null, null, [], null, null),
            ]),
    ];

    /// <summary>Kind 1 items (shape pictures) keyed by item id.</summary>
    public static IReadOnlyDictionary<string, ShapeItem> ShapeItems { get; } = new Dictionary<string, ShapeItem>
    {
        ["lap-times/5"] = new(0, "twlf", "tylf"), // screen [ref:1594]
        ["lap-times/6"] = new(0, "twrt", "tyrt"), // screen [ref:1595]
        ["race-results/4"] = new(0, "bwlf", "bylf"), // screen [ref:1596]
        ["race-results/5"] = new(0, "bwrt", "byrt"), // screen [ref:1597]
    };

    /// <summary>0-terminated u16 text-id lists read by <c>MenuController.DynamicItem</c>, keyed by list name.</summary>
    public static IReadOnlyDictionary<string, int[]> DynamicValueTextIds { get; } = new Dictionary<string, int[]>
    {
        ["laps-12"] = [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104],
        ["laps-13"] = [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105],
        ["laps-14"] = [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106],
        ["laps-15"] = [93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107],
        ["tracks-8"] = [61, 62, 63, 64, 65, 66, 67, 68],
        ["tracks-7"] = [61, 62, 63, 64, 65, 66, 67],
    };
}
