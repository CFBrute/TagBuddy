using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.Nfc;
using Android.OS;
using Proxmark3;
using TagBuddy.Services;

namespace TagBuddy;

[Activity(Theme = "@style/TagBuddy.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// Android opens the app for its own private-text tags (no other app knows the type), even when it is closed.
[IntentFilter([NfcAdapter.ActionNdefDiscovered], Categories = [Intent.CategoryDefault], DataMimeType = SealedText.MimeType)]
// And when a Proxmark (USB vendor 0x9AC4, see Resources/xml/proxmark_usb_filter.xml) is plugged in over OTG.
[IntentFilter([UsbManager.ActionUsbDeviceAttached])]
// A saved item sent from another Tag Buddy (ShareLink): the share page's "Open in Tag Buddy" button hands it over as
// tagbuddy://import?d=…, which no other app claims.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = ShareLink.Scheme, DataHost = ShareLink.ImportHost)]
// The share page's own address too. Android only opens it here by itself once the site proves the app is its own
// (an assetlinks.json at the site's root), or the person allows it under the app's "Open by default" settings;
// until then the link opens the page in the browser, whose button opens the app instead.
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https", DataHost = "cfbrute.github.io", DataPathPrefix = "/TagBuddy/s")]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/proxmark_usb_filter")]
public class MainActivity : MauiAppCompatActivity
{
    private UsbPlugReceiver? _usbReceiver;

    /// <summary>Every touch passes through here before any view: note the finger's screen position for
    /// <see cref="Controls.ScreenTouch"/>, so a drag can follow the finger exactly.</summary>
    public override bool DispatchTouchEvent(Android.Views.MotionEvent? e)
    {
        if (e is not null) Controls.ScreenTouch.Track(e);
        return base.DispatchTouchEvent(e);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Not after a restart (rotation, low memory): that intent was handled the first time.
        if (savedInstanceState is null) HandleIntent(Intent);

        // Attach/detach while the app is running, so the RF tab can appear and disappear live. The launch-time
        // attach also comes through the intent filter above; this covers plugging in with the app already open.
        _usbReceiver = new UsbPlugReceiver();
        var filter = new IntentFilter();
        filter.AddAction(UsbManager.ActionUsbDeviceAttached);
        filter.AddAction(UsbManager.ActionUsbDeviceDetached);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_usbReceiver, filter, ReceiverFlags.NotExported);
        else
            RegisterReceiver(_usbReceiver, filter);
    }

    protected override void OnDestroy()
    {
        if (_usbReceiver is not null) { try { UnregisterReceiver(_usbReceiver); } catch { /* already gone */ } }
        base.OnDestroy();
    }

    /// <summary>The app was already running (single top): a private tag, an attached Proxmark or a share link arrives here.</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleIntent(intent);
    }

    private static void HandleIntent(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && IncomingShare.Offer(intent.DataString)) return;
        NfcReader.Instance.HandleIntent(intent);
        if (intent?.Action == UsbManager.ActionUsbDeviceAttached) ProxmarkUsb.NoteAttached();
    }

    /// <summary>Tells <see cref="ProxmarkUsb"/> when a Proxmark is plugged in or out, so the RF tab tracks it.</summary>
    private sealed class UsbPlugReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == UsbManager.ActionUsbDeviceAttached) ProxmarkUsb.NoteAttached();
            else if (intent?.Action == UsbManager.ActionUsbDeviceDetached) ProxmarkUsb.NoteDetached();
        }
    }
}
