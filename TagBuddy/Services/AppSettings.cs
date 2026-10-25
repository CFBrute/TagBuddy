namespace TagBuddy.Services;

/// <summary>The person's choices from the Settings tab, kept in the app's preferences.</summary>
public static class AppSettings
{
    private const string AutoPreviewsKey = "auto-previews";
    private const string VibrateKey = "vibrate";
    private const string SoundKey = "sound";
    private const string MaxBrightnessKey = "max-brightness";
    private const string NfcSoundKey = "nfc-sound";

    /// <summary>Whether Android plays its own sound when the NFC tab finds a tag. Off silences it while the app reads
    /// or writes; a private tag that opens the app from outside still gets Android's sound, before the app is involved.</summary>
    public static bool NfcSound
    {
        get => Preferences.Default.Get(NfcSoundKey, true);
        set => Preferences.Default.Set(NfcSoundKey, value);
    }

    /// <summary>Whether link previews and site icons load by themselves as soon as a link is shown or saved. Off,
    /// nothing goes over the network until the person taps "Tap to load preview"; what is already on the phone
    /// still shows.</summary>
    public static bool AutoPreviews
    {
        get => Preferences.Default.Get(AutoPreviewsKey, true);
        set => Preferences.Default.Set(AutoPreviewsKey, value);
    }

    /// <summary>Whether the phone vibrates when a code is scanned or a tag is read or written.</summary>
    public static bool Vibrate
    {
        get => Preferences.Default.Get(VibrateKey, true);
        set => Preferences.Default.Set(VibrateKey, value);
    }

    /// <summary>Whether the phone beeps when a code is scanned with the camera. The beep plays as a notification
    /// sound, so silent and vibrate mode keep it quiet. Tags have Android's own sound instead (<see cref="NfcSound"/>).</summary>
    public static bool Sound
    {
        get => Preferences.Default.Get(SoundKey, false);
        set => Preferences.Default.Set(SoundKey, value);
    }

    /// <summary>Whether a code shown full screen starts at maximum brightness instead of double the phone's own
    /// level. The slider still turns it down.</summary>
    public static bool MaxBrightness
    {
        get => Preferences.Default.Get(MaxBrightnessKey, false);
        set => Preferences.Default.Set(MaxBrightnessKey, value);
    }
}
