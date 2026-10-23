namespace TagBuddy.Services;

/// <summary>The vibration and beep when a code is scanned, and the vibration when a tag is read or written, each as
/// chosen on the Settings tab (<see cref="AppSettings.Vibrate"/>, <see cref="AppSettings.Sound"/>). Tags get no beep
/// from the app: Android plays its own NFC sound for them, or not (<see cref="AppSettings.NfcSound"/>).</summary>
public static class Feedback
{
#if ANDROID
    /// <summary>Kept for the app's lifetime: creating a tone generator takes a moment, too long for every scan.</summary>
    private static Android.Media.ToneGenerator? _tones;
#endif

    /// <summary>A code was read: a short buzz and a short beep.</summary>
    public static void Success(int vibrateMilliseconds = 60) => Signal(vibrateMilliseconds, ok: true, beep: true);

    /// <summary>Something failed: a longer buzz and a lower double tone.</summary>
    public static void Failure() => Signal(200, ok: false, beep: true);

    /// <summary>A tag was read or written (or failed to be): a buzz only, since Android has its own NFC sound.</summary>
    public static void Tag(bool ok) => Signal(ok ? 60 : 200, ok, beep: false);

    private static void Signal(int vibrateMilliseconds, bool ok, bool beep)
    {
        if (AppSettings.Vibrate) Vibrate(vibrateMilliseconds);
        if (beep && AppSettings.Sound) Beep(ok);
    }

    /// <summary>Buzzes as a notification. MAUI's Vibration files the buzz as touch feedback, which Android drops
    /// whenever the person has switched off touch vibration ("ignored_for_settings"), so a scan would go unfelt.
    /// A notification buzz follows the phone's notification vibration setting instead.</summary>
    private static void Vibrate(int milliseconds)
    {
#if ANDROID
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var context = Platform.AppContext;
                var vibrator = OperatingSystem.IsAndroidVersionAtLeast(31)
                    ? (context.GetSystemService(Android.Content.Context.VibratorManagerService) as Android.OS.VibratorManager)?.DefaultVibrator
#pragma warning disable CA1422 // the vibrator manager above replaces this from Android 12
                    : context.GetSystemService(Android.Content.Context.VibratorService) as Android.OS.Vibrator;
#pragma warning restore CA1422
                if (vibrator is null || !vibrator.HasVibrator) return;

                var effect = Android.OS.VibrationEffect.CreateOneShot(milliseconds, Android.OS.VibrationEffect.DefaultAmplitude)!;
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    vibrator.Vibrate(effect, Android.OS.VibrationAttributes.CreateForUsage((int)Android.OS.VibrationAttributesUsageType.Notification));
                else
#pragma warning disable CA1422 // the VibrationAttributes overload above replaces this from Android 13
                    vibrator.Vibrate(effect, new Android.Media.AudioAttributes.Builder()!.SetUsage(Android.Media.AudioUsageKind.NotificationEvent)!.Build());
#pragma warning restore CA1422
                return;
            }
        }
        catch
        {
            // Fall back to MAUI's vibration below.
        }
#endif
        try { Vibration.Default.Vibrate(milliseconds); } catch { /* not supported on every device */ }
    }

    private static void Beep(bool ok)
    {
#if ANDROID
        try
        {
            // The notification stream follows the phone's ring mode, so silent and vibrate mode stay quiet.
            _tones ??= new Android.Media.ToneGenerator(Android.Media.Stream.Notification, 80);
            _tones.StartTone(ok ? Android.Media.Tone.PropBeep : Android.Media.Tone.PropNack, ok ? 120 : 300);
        }
        catch
        {
            // No audio available right now; the vibration (if on) still says it.
            _tones = null;
        }
#endif
    }
}
