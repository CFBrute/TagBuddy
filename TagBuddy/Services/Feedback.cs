namespace TagBuddy.Services;

/// <summary>The vibration and beep when a code is scanned or a tag is read or written, each as chosen on the
/// Settings tab (<see cref="AppSettings.Vibrate"/>, <see cref="AppSettings.Sound"/>).</summary>
public static class Feedback
{
#if ANDROID
    /// <summary>Kept for the app's lifetime: creating a tone generator takes a moment, too long for every scan.</summary>
    private static Android.Media.ToneGenerator? _tones;
#endif

    /// <summary>Something worked: a short buzz and a short beep.</summary>
    public static void Success(int vibrateMilliseconds = 60) => Signal(vibrateMilliseconds, ok: true);

    /// <summary>Something failed: a longer buzz and a lower double tone.</summary>
    public static void Failure() => Signal(200, ok: false);

    private static void Signal(int vibrateMilliseconds, bool ok)
    {
        if (AppSettings.Vibrate)
        {
            try { Vibration.Default.Vibrate(vibrateMilliseconds); } catch { /* not supported on every device */ }
        }
        if (AppSettings.Sound) Beep(ok);
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
