using NFS.Render;

namespace NFS.Desktop.Tests;

/// <summary>Game data folder choice (Android first run): validation, saved choice, browser navigation, screen flow.</summary>
public sealed class DataDirTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "nfs-datadir-" + Guid.NewGuid().ToString("N"));

    public DataDirTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    string Dir(params string[] parts) { var p = Path.Combine([_tmp, .. parts]); Directory.CreateDirectory(p); return p; }
    string Game(params string[] parts) { var p = Dir(parts); Directory.CreateDirectory(Path.Combine(p, "GAMEDATA")); Directory.CreateDirectory(Path.Combine(p, "fedata")); return p; }
    string Cfg => Path.Combine(_tmp, "cfg", "port.json");

    // ---- validation ----

    [Fact]
    public void ResolveAcceptsGameDirParentWithNfs2seAndRejectsOthers()
    {
        var game = Game("a", "NFS2SE");
        Assert.Equal(game, DataDirResolver.Resolve(game));
        Assert.Equal(game, DataDirResolver.Resolve(Path.Combine(_tmp, "a")));           // parent holding NFS2SE
        Assert.Equal(game, DataDirResolver.Resolve(game + Path.DirectorySeparatorChar)); // trailing separator
        Assert.Null(DataDirResolver.Resolve(Dir("empty")));
        Assert.Null(DataDirResolver.Resolve(Path.Combine(_tmp, "missing")));
        Assert.Null(DataDirResolver.Resolve(null));
        Assert.Null(DataDirResolver.Resolve(""));
        var half = Dir("half"); Directory.CreateDirectory(Path.Combine(half, "GAMEDATA"));   // FEDATA missing
        Assert.Null(DataDirResolver.Resolve(half));
    }

    [Fact]
    public void RootsListAppDirSharedStorageAndSdCardsOnly()
    {
        var storage = Dir("storage");
        Dir("storage", "emulated", "0"); Dir("storage", "self"); Dir("storage", "1234-ABCD");
        var app = Dir("app");
        var roots = DataDirResolver.DiscoverRoots(app, storage);
        Assert.Equal([app, Path.Combine(storage, "emulated", "0"), Path.Combine(storage, "1234-ABCD")], roots);
        Assert.Equal([app], DataDirResolver.DiscoverRoots(app, Path.Combine(_tmp, "nostorage")));
        Assert.Empty(DataDirResolver.DiscoverRoots(null, Path.Combine(_tmp, "nostorage")));
    }

    [Fact]
    public void PlatformVolumesAreAddedEvenWhenStorageCannotBeListed()
    {
        var sd = Dir("sd");                                         // /storage not listable: the SD card comes from the platform
        var roots = DataDirResolver.DiscoverRoots(null, Path.Combine(_tmp, "nostorage"), [sd + "/Android/data/pkg/files", Path.Combine(_tmp, "gone")]);
        Assert.Equal([sd], roots);
        Assert.Equal("/storage/1234-ABCD", DataDirResolver.VolumeRoot("/storage/1234-ABCD/Android/data/com.indigo.nfs2se/files"));
        Assert.Equal("/storage/1234-ABCD", DataDirResolver.VolumeRoot("/storage/1234-ABCD/"));
    }

    // ---- saved choice ----

    [Fact]
    public void GameDirIsSavedInPortJsonAndKeepsOtherKeys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Cfg)!);
        File.WriteAllText(Cfg, "{ \"fullScreen\": true, \"future\": 5 }");
        var s = PortSettings.Load(Cfg);
        s.GameDir = "/sdcard/NFS2SE";
        Assert.Null(s.Save(Cfg));
        var back = PortSettings.Load(Cfg);
        Assert.Equal("/sdcard/NFS2SE", back.GameDir);
        Assert.True(back.FullScreen);
        Assert.Contains("future", File.ReadAllText(Cfg));
    }

    // ---- browser ----

    [Fact]
    public void BrowserListsSortedVisibleDirectoriesAndNavigatesUpToTheStartPoints()
    {
        var root = Dir("root");
        Dir("root", "beta"); Dir("root", "Alpha"); Dir("root", ".hidden"); File.WriteAllText(Path.Combine(root, "file.txt"), "x");
        Dir("root", "beta", "inner");
        var b = new DirBrowser([root]);
        Assert.Null(b.Current);
        Assert.Equal([root], b.Entries);
        b.Enter(0);
        Assert.Equal(root, b.Current);
        Assert.Equal(["Alpha", "beta"], b.Names);
        b.Enter(1);
        Assert.Equal(["inner"], b.Names);
        Assert.True(b.Up());
        Assert.Equal(root, b.Current);
        Assert.True(b.Up());   // from the start point: the list of start points
        Assert.Null(b.Current);
        Assert.False(b.Up());
    }

    [Fact]
    public void BrowserShowsValidWhenFolderOrItsNfs2seChildIsAGameDir()
    {
        var game = Game("x", "NFS2SE");
        var b = new DirBrowser([Path.Combine(_tmp, "x")], game);
        Assert.True(b.IsValidHere);
        Assert.Equal(game, b.ResolvedHere);
        b.Open(Path.Combine(_tmp, "x"));
        Assert.Equal(game, b.ResolvedHere);
        b.Open(Dir("y"));
        Assert.False(b.IsValidHere);
    }

    // ---- screen flow ----

    static void TapIn(DataDirScreen s, (int X, int Y, int W, int H) r) => s.Tap(r.X + r.W / 2, r.Y + r.H / 2);

    [Fact]
    public void FirstRunGoesToBrowserPicksAFolderAndSavesIt()
    {
        var game = Game("sd", "NFS2SE");
        var settings = new PortSettings();
        var s = new DataDirScreen(settings, Cfg, [Path.Combine(_tmp, "sd")]);
        Assert.Equal(DataDirScreen.Mode.Browse, s.Current);
        // row 0 = the start point; open it, then the NFS2SE folder
        s.Tap(100, DataDirScreen.ListTop + 10);
        Assert.Equal(Path.Combine(_tmp, "sd"), s.Browser!.Current);
        TapIn(s, DataDirScreen.SelectButton);   // the parent of NFS2SE is accepted and resolved to NFS2SE
        Assert.Equal(DataDirScreen.Outcome.Accepted, s.Result);
        Assert.Equal(game, s.ChosenDir);
        Assert.Equal(game, PortSettings.Load(Cfg).GameDir);
    }

    [Fact]
    public void SelectDoesNothingWhereThereIsNoGameData()
    {
        var empty = Dir("e");
        var s = new DataDirScreen(new PortSettings(), Cfg, [empty]);
        s.Tap(100, DataDirScreen.ListTop + 10);
        TapIn(s, DataDirScreen.SelectButton);
        Assert.Equal(DataDirScreen.Outcome.None, s.Result);
        Assert.False(File.Exists(Cfg));
    }

    [Fact]
    public void LaterRunShowsTheSavedDirWithOkAndChooseAnother()
    {
        var game = Game("g");
        var s = new DataDirScreen(new PortSettings { GameDir = game }, Cfg, [_tmp]);
        Assert.Equal(DataDirScreen.Mode.Confirm, s.Current);
        Assert.Equal(game, s.KnownDir);
        TapIn(s, DataDirScreen.OkButton);
        Assert.Equal(DataDirScreen.Outcome.Accepted, s.Result);
        Assert.Equal(game, s.ChosenDir);

        var s2 = new DataDirScreen(new PortSettings { GameDir = game }, Cfg, [_tmp]);
        TapIn(s2, DataDirScreen.AnotherButton);
        Assert.Equal(DataDirScreen.Mode.Browse, s2.Current);
        s2.Back();   // at the start points: cancel -> back to the confirm screen
        Assert.Equal(DataDirScreen.Mode.Confirm, s2.Current);
        s2.Back();   // quit
        Assert.Equal(DataDirScreen.Outcome.Quit, s2.Result);
    }

    [Fact]
    public void MissingSavedDirGoesStraightToTheBrowserAndAppDirIsOfferedAsDefault()
    {
        var s = new DataDirScreen(new PortSettings { GameDir = Path.Combine(_tmp, "gone") }, Cfg, [_tmp]);
        Assert.Equal(DataDirScreen.Mode.Browse, s.Current);
        Assert.Null(s.KnownDir);
        var app = Game("app", "NFS2SE");
        var s2 = new DataDirScreen(new PortSettings { GameDir = Path.Combine(_tmp, "gone") }, Cfg, [_tmp], defaultDir: Path.Combine(_tmp, "app"));
        Assert.Equal(DataDirScreen.Mode.Confirm, s2.Current);
        Assert.Equal(app, s2.KnownDir);
    }

    [Fact]
    public void BackInTheBrowserGoesUpAndQuitsWhenNothingIsKnown()
    {
        Dir("r", "sub");
        var s = new DataDirScreen(new PortSettings(), Cfg, [Path.Combine(_tmp, "r")]);
        s.Tap(100, DataDirScreen.ListTop + 10);   // open start point r
        s.Tap(100, DataDirScreen.ListTop + 10 + DataDirScreen.RowH);   // row 1 = sub (row 0 is ".. (UP)")
        Assert.Equal(Path.Combine(_tmp, "r", "sub"), s.Browser!.Current);
        s.Back();
        Assert.Equal(Path.Combine(_tmp, "r"), s.Browser.Current);
        s.Tap(100, DataDirScreen.ListTop + 10);   // the ".. (UP)" row
        Assert.Null(s.Browser.Current);
        s.Back();
        Assert.Equal(DataDirScreen.Outcome.Quit, s.Result);
    }

    [Fact]
    public void PagingScrollsLongLists()
    {
        for (int i = 0; i < 30; i++) Dir("big", $"d{i:00}");
        var s = new DataDirScreen(new PortSettings(), Cfg, [Path.Combine(_tmp, "big")]);
        s.Tap(100, DataDirScreen.ListTop + 10);
        Assert.Equal(0, s.Scroll);
        TapIn(s, DataDirScreen.ScrollDown);
        Assert.Equal(DataDirScreen.Rows - 1, s.Scroll);
        for (int i = 0; i < 10; i++) TapIn(s, DataDirScreen.ScrollDown);
        Assert.Equal(31 - DataDirScreen.Rows, s.Scroll);   // 30 folders + the ".. (UP)" row
        TapIn(s, DataDirScreen.ScrollUp);
        Assert.True(s.Scroll < 31 - DataDirScreen.Rows);
        // tapping the first visible row enters the folder at that scroll position
        s.Tap(100, DataDirScreen.ListTop + 5);
        Assert.StartsWith(Path.Combine(_tmp, "big", "d"), s.Browser!.Current);
        Assert.Equal(0, s.Scroll);
    }

    class Access : IStorageAccess { public bool HasAccess { get; set; } public int Requests; public void Request() => Requests++; }

    [Fact]
    public void WithoutStorageAccessThePermissionScreenLeadsToTheBrowserOnceGranted()
    {
        var access = new Access();
        var s = new DataDirScreen(new PortSettings(), Cfg, [_tmp], access: access);
        Assert.Equal(DataDirScreen.Mode.NeedAccess, s.Current);
        TapIn(s, DataDirScreen.GrantButton);
        Assert.Equal(1, access.Requests);
        s.Update();
        Assert.Equal(DataDirScreen.Mode.NeedAccess, s.Current);
        access.HasAccess = true;
        s.Update();
        Assert.Equal(DataDirScreen.Mode.Browse, s.Current);
    }

    [Fact]
    public void WrapBreaksLongPathsAfterSlashes()
    {
        var lines = DataDirScreen.Wrap("/storage/1234-ABCD/Games/Need For Speed 2 SE/NFS2SE", 24);
        Assert.All(lines, l => Assert.True(l.Length <= 25, l));
        Assert.Equal("/storage/1234-ABCD/Games/Need For Speed 2 SE/NFS2SE".Replace("/", ""), string.Concat(lines).Replace("/", ""));
    }

    [Fact]
    public void ScreenPicturesForReview()
    {
        if (Environment.GetEnvironmentVariable("NFS_TOUCH_REVIEW_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var sd = Dir("storage", "1234-ABCD");
        var game = Game("storage", "1234-ABCD", "Games", "Need For Speed 2 SE", "NFS2SE");
        Dir("storage", "1234-ABCD", "Music"); Dir("storage", "1234-ABCD", "DCIM"); Dir("storage", "1234-ABCD", "Android");
        for (int i = 0; i < 6; i++) Dir("storage", "1234-ABCD", $"Folder_{i}");
        Dir("storage", "emulated", "0");
        var roots = DataDirResolver.DiscoverRoots(null, Path.Combine(_tmp, "storage"));
        var fb = new FrameBuffer();
        void Save(string name, DataDirScreen s) { s.Render(fb); fb.SavePng(Path.Combine(dir, name)); }

        var access = new Access();
        Save("datadir_access.png", new DataDirScreen(new PortSettings(), Cfg, roots, access: access));
        var first = new DataDirScreen(new PortSettings(), Cfg, roots);
        Save("datadir_roots.png", first);
        first.Tap(100, DataDirScreen.ListTop + DataDirScreen.RowH * 0 + 10);   // 1234-ABCD (sorted after emulated/0? first root wins)
        Save("datadir_browse.png", first);
        var deep = new DataDirScreen(new PortSettings(), Cfg, roots);
        deep.Browser!.Open(Path.Combine(sd, "Games", "Need For Speed 2 SE"));
        Save("datadir_found.png", deep);
        Save("datadir_confirm.png", new DataDirScreen(new PortSettings { GameDir = game }, Cfg, roots));
    }
}
