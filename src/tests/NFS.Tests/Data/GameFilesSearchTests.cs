using NFS.Data;

namespace NFS.Tests.Data;

/// <summary>Search order of <see cref="GameFiles.FindIn"/> on temporary directories. No env vars and no real game
/// are needed, so these run everywhere. A game directory is recognised by its GAMEDATA and FEDATA folders; the
/// executable is not needed.</summary>
public sealed class GameFilesSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nfs2se-search-" + Guid.NewGuid().ToString("N"));
    private readonly string _app;

    public GameFilesSearchTests()
    {
        _app = Path.Combine(_root, "app", "bin");
        Directory.CreateDirectory(_app);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Exe directory, with a trailing separator as AppContext.BaseDirectory has it.</summary>
    private string AppBase => _app + Path.DirectorySeparatorChar;

    /// <summary>A directory with the game data folders (no executable). <paramref name="fedata"/> false leaves out FEDATA.</summary>
    private string Game(string dir, bool gamedata = true, bool fedata = true)
    {
        Directory.CreateDirectory(dir);
        if (gamedata) Directory.CreateDirectory(Path.Combine(dir, GameFiles.DataDirName));
        if (fedata) Directory.CreateDirectory(Path.Combine(dir, GameFiles.FeDirName));
        return dir;
    }

    private static string Full(string p) => Path.GetFullPath(p);

    [Fact]
    public void ExplicitDirWithDataAndNoExeIsAccepted()
    {
        var explicitDir = Game(Path.Combine(_root, "explicit"));
        Assert.False(File.Exists(Path.Combine(explicitDir, "NFS2SEN.EXE")));

        Assert.Equal(Full(explicitDir), GameFiles.FindIn(explicitDir, null, AppBase));
    }

    [Fact]
    public void ExplicitDirWinsOverEnvAndExeDirectory()
    {
        var explicitDir = Game(Path.Combine(_root, "explicit"));
        var env = Game(Path.Combine(_root, "env"));
        Game(_app);

        Assert.Equal(Full(explicitDir), GameFiles.FindIn(explicitDir, env, AppBase));
    }

    [Fact]
    public void EnvVarIsUsedBeforeExeDirectory()
    {
        var env = Game(Path.Combine(_root, "env"));
        Game(_app);

        Assert.Equal(Full(env), GameFiles.FindIn(null, env, AppBase));
    }

    [Fact]
    public void ExplicitDirWithoutDataIsSkipped()
    {
        var explicitDir = Directory.CreateDirectory(Path.Combine(_root, "not-a-game")).FullName;
        var env = Game(Path.Combine(_root, "env"));

        Assert.Equal(Full(env), GameFiles.FindIn(explicitDir, env, AppBase));
    }

    [Fact]
    public void ExplicitDirWithExeButNoDataIsRejected()
    {
        var dir = Path.Combine(_root, "exe-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "NFS2SEN.EXE"), "");

        Assert.Null(GameFiles.FindIn(dir, null, AppBase));
    }

    [Fact]
    public void DirWithGameDataButNoFeDataIsRejected()
    {
        var dir = Game(Path.Combine(_root, "no-fedata"), fedata: false);

        Assert.Null(GameFiles.FindIn(dir, null, AppBase));
    }

    [Fact]
    public void DirWithFeDataButNoGameDataIsRejected()
    {
        var dir = Game(Path.Combine(_root, "no-gamedata"), gamedata: false);

        Assert.Null(GameFiles.FindIn(dir, null, AppBase));
    }

    [Fact]
    public void ExeDirectoryWithDataIsUsedWithoutExe()
    {
        Game(_app);

        Assert.Equal(Full(_app), GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void ExeDirectoryWithoutDataIsNotUsed()
    {
        File.WriteAllText(Path.Combine(_app, "NFS2SEN.EXE"), "");

        Assert.Null(GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void ExeSubfolderNfs2seIsUsedWhenExeDirectoryHasNoData()
    {
        var sub = Game(Path.Combine(_app, "NFS2SE"));

        Assert.Equal(Full(sub), GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void ExeDirectoryDataBeatsExeSubfolder()
    {
        Game(_app);
        Game(Path.Combine(_app, "NFS2SE"));

        Assert.Equal(Full(_app), GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void ExeSubfolderBeatsWalkUpGameSrc()
    {
        Game(Path.Combine(_root, "game_src", "NFS2SE"));
        var sub = Game(Path.Combine(_app, "NFS2SE"));

        Assert.Equal(Full(sub), GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void WalkUpFindsGameSrcNfs2se()
    {
        var src = Game(Path.Combine(_root, "game_src", "NFS2SE"));

        Assert.Equal(Full(src), GameFiles.FindIn(null, null, AppBase));
    }

    [Fact]
    public void NothingFoundReturnsNull()
    {
        Assert.Null(GameFiles.FindIn(Path.Combine(_root, "missing"), Path.Combine(_root, "missing2"), AppBase));
    }
}
