using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Nfc;
using Android.OS;
using Android.Views;
using TagBuddy.Pages;
using TagBuddy.Services;

namespace TagBuddy;

[Activity(Theme = "@style/TagBuddy.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Android opens the app for its own private-text tags (no other app knows the type), even when it is closed.
[IntentFilter([NfcAdapter.ActionNdefDiscovered], Categories = [Intent.CategoryDefault], DataMimeType = SealedText.MimeType)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Not after a restart (rotation, low memory): that intent was handled the first time.
        if (savedInstanceState is null) NfcReader.Instance.HandleIntent(Intent);
    }

    /// <summary>Undo, redo and cursor keys from a hardware keyboard go to the editor on screen first, if one takes them
    /// (the NFC tab's segment editor), before the text field that has the focus sees them.</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is { Action: KeyEventActions.Down } && EditorKeyOf(e) is { } key && HardwareKeys.Handle(key)) return true;
        return base.DispatchKeyEvent(e);
    }

    private static EditorKey? EditorKeyOf(KeyEvent e) => e.KeyCode switch
    {
        Keycode.Z when e.IsCtrlPressed => e.IsShiftPressed ? EditorKey.Redo : EditorKey.Undo,
        Keycode.Y when e.IsCtrlPressed => EditorKey.Redo,
        Keycode.DpadLeft => EditorKey.Left,
        Keycode.DpadRight => EditorKey.Right,
        Keycode.DpadUp => EditorKey.Up,
        Keycode.DpadDown => EditorKey.Down,
        Keycode.MoveHome => EditorKey.Home,
        Keycode.MoveEnd => EditorKey.End,
        _ => null,
    };

    /// <summary>The app was already running (single top): a private tag held to the phone arrives here.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        NfcReader.Instance.HandleIntent(intent);
    }
}
