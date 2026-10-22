namespace TagBuddy;

/// <summary>What this build includes. A sideloaded build has everything; a Play Store build
/// (<c>-p:PlayStore=true</c>, which defines PLAY_STORE) leaves out what Google Play's policies object to.
/// Read-only fields rather than constants, so code behind a switched-off feature still compiles without
/// "unreachable code" warnings.</summary>
public static class Features
{
#if PLAY_STORE
    public const string Edition = "Play Store";
    public static readonly bool Amiibo = false;
#else
    public const string Edition = "Sideload";

    /// <summary>Amiibo support: opening Nintendo-protected tags with the key derived from their serial, writing
    /// Amiibo dumps with the Nintendo password, and naming Amiibo content in the app. Without it such tags are
    /// treated like any other password-protected tag (never guessed at) and Amiibo dumps are not written.</summary>
    public static readonly bool Amiibo = true;
#endif
}
