namespace TagBuddy.Services;

/// <summary>Pushes the screen up so scanners can read the code reliably, without the jump from a dimmed
/// phone to a blinding screen: the page starts at double the phone's own level (or at maximum, if the person
/// chose that on the Settings tab) and a slider does the rest.</summary>
public static class ScreenBrightness
{
    /// <summary>Where the page starts for a given phone brightness: double it, but never under 20 % and
    /// never over 100 %. So 0 → 20, 20 → 40, 40 → 80, and 50 or more → 100. With <paramref name="alwaysMax"/>
    /// (<see cref="AppSettings.MaxBrightness"/>) always 100.</summary>
    public static float StartLevel(float current, bool alwaysMax) => alwaysMax ? 1f : Math.Clamp(current * 2f, 0.2f, 1f);

    /// <summary>The phone's current screen brightness, 0..1, or 1 when it cannot be read.</summary>
    public static float Current()
    {
#if ANDROID
        try
        {
            var resolver = Platform.AppContext.ContentResolver;
            int value = Android.Provider.Settings.System.GetInt(resolver, Android.Provider.Settings.System.ScreenBrightness, 255);
            return Math.Clamp(value / 255f, 0f, 1f);
        }
        catch
        {
            return 1f;
        }
#else
        return 1f;
#endif
    }

    // -1 means "follow the system brightness setting".
    public static void Restore() => Set(-1f);

    public static void Set(float brightness)
    {
#if ANDROID
        var window = Platform.CurrentActivity?.Window;
        if (window?.Attributes is null) return;
        var attrs = window.Attributes;
        attrs.ScreenBrightness = brightness;
        window.Attributes = attrs;
#endif
    }
}
