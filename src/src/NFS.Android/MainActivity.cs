using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Microsoft.Xna.Framework;
using NFS.Data;
using NFS.Desktop;

namespace NFS.Android;

/// <summary>Android entry point (the counterpart of NFS.Desktop/Program.cs): resolves the game data directory, starts
/// <see cref="Game1"/> in landscape, full screen, and forwards the system Back button to <see cref="PlatformBack"/>
/// (touch layer: pause in a race, Esc in the menus). Text fields (name editor, file name, network menu, chat) open the system
/// on-screen keyboard through <see cref="SoftKeyboard.Platform"/>; its keys go to <see cref="SoftKeyboard.Type"/>.
/// Game data (GAMEDATA, FEDATA of the original game) is NOT in the APK: the folder is chosen in <see cref="DataDirActivity"/>
/// (saved in port.json); this activity only starts the game with it, or goes back to the chooser when the folder is gone.
/// The default offered there is <c>/sdcard/Android/data/com.indigo.nfs2se/files/NFS2SE/</c> (adb push target).
/// Own process (":game"): MonoGame keeps one static Game (<c>Game.Instance</c>, <c>Game.Activity</c>, the sound system) and
/// <c>Game.Dispose</c> clears it unconditionally. In one process the chooser's <c>DataDirGame</c>, disposed in its
/// <c>OnDestroy</c> only after this activity has started <see cref="Game1"/>, nulled the running game's instance →
/// NullReferenceException in <c>Threading.Run</c> / <c>MakeCurrent</c> a moment after the menu appeared.</summary>
[Activity(
    Process = ":game",
    Label = "Indigo (NFS2SE)",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize,
    LaunchMode = LaunchMode.SingleInstance,
    AlwaysRetainTaskState = true)]
public class MainActivity : AndroidGameActivity
{
    /// <summary>Folder name under the app's external files dir.</summary>
    public const string GameFolder = "NFS2SE";

    Game1? _game;
    /// <summary>The on-screen keyboard is open (<see cref="ShowKeyboard"/>); its keys are queued instead of the polled keyboard state.</summary>
    volatile bool _keyboard;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var port = PortSettings.Load(PortSettings.DefaultPath);
        var dir = DataDirResolver.Resolve(port.GameDir);
        if (dir == null)
        {
            StartActivity(new Intent(this, typeof(DataDirActivity)));   // not chosen yet, or the folder disappeared
            Finish();
            return;
        }
        System.Environment.SetEnvironmentVariable(GameFiles.EnvVar, dir);
        CrashLog.Install(this, dir);

        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        // Phone CPU: Enhanced (auto scale + SSAA) stutters on a full-screen software renderer → Vanilla unless port.json says otherwise.
        var opt = new DesktopOptions { GameDir = dir, FullScreen = true, Touch = true,
            Gfx = port.GraphicsModeText == null ? NFS.Render.GraphicsMode.Vanilla : null };
        _game = new Game1(opt);
        SetContentView((View)_game.Services.GetService(typeof(View))!);
        SoftKeyboard.Platform = ShowKeyboard;
        _game.Run();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemBars();
    }

#pragma warning disable CA1422, CS0618 // deprecated on new API levels, still the most compatible way for API 24..36
    void HideSystemBars()
    {
        if (Window?.DecorView is { } v)
            v.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.HideNavigation | SystemUiFlags.Fullscreen
                | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutFullscreen);
    }

    public override void OnBackPressed() => PlatformBack.Request();   // no base call: Back must not close the game
#pragma warning restore CA1422, CS0618

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back) { PlatformBack.Request(); return true; }
        if (_keyboard && e != null && SoftKey(keyCode, e)) return true;
        return base.OnKeyDown(keyCode, e);
    }

    /// <summary>Keyboards that commit whole words send ACTION_MULTIPLE with the text in <see cref="KeyEvent.Characters"/>.</summary>
    public override bool OnKeyMultiple(Keycode keyCode, int repeatCount, KeyEvent? e)
    {
        if (_keyboard && keyCode == Keycode.Unknown && e?.Characters is { } text)
        {
            foreach (char c in text) SoftKeyboard.Type(c);
            return true;
        }
        return base.OnKeyMultiple(keyCode, repeatCount, e);
    }

    /// <summary>Enter / Done → Enter, Del → Backspace (8), printable keys → their character; false = leave it to MonoGame.</summary>
    static bool SoftKey(Keycode keyCode, KeyEvent e)
    {
        if (keyCode is Keycode.Enter or Keycode.NumpadEnter) { SoftKeyboard.Type('\r'); return true; }
        if (keyCode == Keycode.Del) { SoftKeyboard.Type('\b'); return true; }
        int c = e.UnicodeChar;
        if (c < 0x20 || c > 0xFF) return false;   // the game's fonts are Latin-1
        SoftKeyboard.Type((char)c);
        return true;
    }

    /// <summary><see cref="SoftKeyboard.Platform"/>: called on the game thread, the IME is driven on the UI thread.</summary>
    void ShowKeyboard(bool show)
    {
        _keyboard = show;
        RunOnUiThread(() =>
        {
            var view = (View?)_game?.Services.GetService(typeof(View));
            if (view == null || GetSystemService(InputMethodService) is not InputMethodManager imm) return;
            if (show)
            {
                view.FocusableInTouchMode = true;
                view.RequestFocus();
#pragma warning disable CA1422, CS0618 // ShowForced: the game view is not a text editor, the implicit request is ignored there
                imm.ShowSoftInput(view, ShowFlags.Forced);
#pragma warning restore CA1422, CS0618
            }
            else
            {
                imm.HideSoftInputFromWindow(view.WindowToken, HideSoftInputFlags.None);
                HideSystemBars();
            }
        });
    }

    protected override void OnDestroy()
    {
        if (SoftKeyboard.Platform == ShowKeyboard) SoftKeyboard.Platform = null;
        _game?.Dispose();
        _game = null;
        base.OnDestroy();
    }
}
