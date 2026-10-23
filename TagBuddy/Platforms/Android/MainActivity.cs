using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Nfc;
using Android.OS;
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

    /// <summary>The app was already running (single top): a private tag held to the phone arrives here.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        NfcReader.Instance.HandleIntent(intent);
    }
}
