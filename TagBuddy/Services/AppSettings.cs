namespace TagBuddy.Services;

/// <summary>The person's choices from the About tab, kept in the app's preferences.</summary>
public static class AppSettings
{
    private const string AutoPreviewsKey = "auto-previews";

    /// <summary>Whether link previews and site icons load by themselves as soon as a link is shown or saved. Off,
    /// nothing goes over the network until the person taps "Tap to load preview"; what is already on the phone
    /// still shows.</summary>
    public static bool AutoPreviews
    {
        get => Preferences.Default.Get(AutoPreviewsKey, true);
        set => Preferences.Default.Set(AutoPreviewsKey, value);
    }
}
