using Android;
using Microsoft.Xna.Framework;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.OS.Storage;
using Android.Provider;
using Android.Views;
using NFS.Desktop;

namespace NFS.Android;

/// <summary>Launcher activity: the "Using data dir" screen / folder picker (<see cref="DataDirScreen"/>). The game reads plain
/// files, so the storage access is "All files access" (MANAGE_EXTERNAL_STORAGE, sideloaded APK) and a folder browser drawn by
/// the game instead of the system picker (SAF content:// URIs cannot be opened with File IO). The choice is saved in port.json
/// (<see cref="PortSettings.GameDir"/>); then <see cref="MainActivity"/> starts the game.</summary>
[Activity(
    Label = "Indigo (NFS2SE)",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize)]
public class DataDirActivity : AndroidGameActivity, IStorageAccess
{
    DataDirGame? _game;

    public bool HasAccess =>
        OperatingSystem.IsAndroidVersionAtLeast(30)
            ? global::Android.OS.Environment.IsExternalStorageManager
            : CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted;

    public void Request()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission, global::Android.Net.Uri.Parse("package:" + PackageName)));
        else
            RequestPermissions([Manifest.Permission.ReadExternalStorage], 1);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CrashLog.Install(this);
        var appData = Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, MainActivity.GameFolder);
        Directory.CreateDirectory(appData);
        var settings = PortSettings.Load(PortSettings.DefaultPath);
        var screen = new DataDirScreen(settings, PortSettings.DefaultPath, DataDirResolver.DiscoverRoots(appData, volumes: Volumes()), appData, this);
        _game = new DataDirGame(screen, mouseAsTouch: false);
        _game.Finished += (outcome, dir) =>
        {
            if (outcome == DataDirScreen.Outcome.Accepted)
                StartActivity(new Intent(this, typeof(MainActivity)));
            Finish();
        };
        SetContentView((View)_game.Services.GetService(typeof(View))!);
        _game.Run();
    }

    /// <summary>Mounted volumes (internal, SD cards, USB): StorageManager on API 30+, plus the per-volume app dirs
    /// (<c>/storage/XXXX-XXXX/Android/data/…</c>) that also work on older versions.</summary>
    List<string> Volumes()
    {
        var list = new List<string>();
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && GetSystemService(StorageService) is StorageManager sm)
            foreach (var v in sm.StorageVolumes)
                if (v.Directory?.AbsolutePath is { } p) list.Add(p);
        foreach (var d in GetExternalFilesDirs(null) ?? [])
            if (d?.AbsolutePath is { } p) list.Add(p);
        return list;
    }

#pragma warning disable CA1422 // deprecated on new API levels, still the most compatible way for API 24..36
    public override void OnBackPressed() => PlatformBack.Request();
#pragma warning restore CA1422

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back) { PlatformBack.Request(); return true; }
        return base.OnKeyDown(keyCode, e);
    }

    protected override void OnDestroy()
    {
        _game?.Dispose();
        _game = null;
        base.OnDestroy();
    }
}
