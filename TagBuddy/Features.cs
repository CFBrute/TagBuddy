namespace TagBuddy;

/// <summary>What this build includes. A sideloaded build has everything; a Play Store build
/// (<c>-p:PlayStore=true</c>, which defines PLAY_STORE) leaves out what Google Play's policies object to.
/// Read-only fields rather than constants, so code behind a switched-off feature still compiles without
/// "unreachable code" warnings.</summary>
public static class Features
{
    /// <summary>Importing a MIFARE Classic key dictionary and running it against a card's locked sectors, with the
    /// keys that opened a card remembered for next time. In both editions: the app tries only keys that are either
    /// public defaults or supplied by the person (nothing is bundled), reads the person's own cards, and a Classic
    /// sector has no wrong-key lockout, so this is the same kind of operation other Play Store tag tools offer.</summary>
    public static readonly bool MifareKeyTools = true;

    /// <summary>Experimental: a Proxmark3 plugged in over USB-OTG as the RF tab's other reader. The app opens the
    /// Proxmark's own USB serial interface and speaks its command protocol: it reads MIFARE Classic gen1a magic
    /// cards in full and writes them back, and captures 125 kHz to read an EM410x tag or clone one onto a T5577 —
    /// what the phone's own radio can't reach. Reads and writes the person's own hardware and tags.</summary>
    public static readonly bool ProxmarkUsb = true;

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
