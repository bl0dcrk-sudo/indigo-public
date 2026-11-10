using Android.Content;
using Android.OS;
using Android.Runtime;

namespace NFS.Android;

/// <summary>Unhandled exceptions → <c>indigo_crash.txt</c> in the game data folder (reachable with a file manager, unlike
/// Android/data on API 30+) and in the app's external files dir; plus logcat tag "Indigo". Without it a crash on the phone
/// leaves only "app has stopped".</summary>
static class CrashLog
{
    public const string FileName = "indigo_crash.txt";
    static readonly List<string> Dirs = [];
    static bool _hooked;

    public static void Install(Context ctx, string? gameDir = null)
    {
        lock (Dirs)
        {
            if (gameDir != null && !Dirs.Contains(gameDir)) Dirs.Insert(0, gameDir);
            if (ctx.GetExternalFilesDir(null)?.AbsolutePath is { } app && !Dirs.Contains(app)) Dirs.Add(app);
            if (_hooked) return;
            _hooked = true;
        }
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception);
    }

    static void Write(Exception? e)
    {
        string device = $"{Build.Manufacturer} {Build.Model} API {(int)Build.VERSION.SdkInt}";
        string text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {device}{System.Environment.NewLine}{e}{System.Environment.NewLine}{System.Environment.NewLine}";
        global::Android.Util.Log.Error("Indigo", text);
        string[] dirs;
        lock (Dirs) dirs = [.. Dirs];
        foreach (var d in dirs)
            try { File.AppendAllText(Path.Combine(d, FileName), text); }
            catch (Exception) { /* read-only volume: the next dir / logcat */ }
    }
}
