namespace TagBuddy.Services;

/// <summary>The 4-byte password of a Type 2 tag (NTAG213/215/216, Ultralight EV1), as the person types it. A
/// protected tag needs it to read its locked pages or to write; this app never guesses one, so it is only ever
/// the password the person entered.
///
/// <para>A tag can be configured to lock itself for good after a few wrong attempts, so a typo is not harmless.
/// The app warns, and tries a typed password only when the person asks.</para></summary>
public static class TagPassword
{
    /// <summary>The factory password of a fresh NTAG/Ultralight, and a common choice when one is set by hand.</summary>
    public static readonly byte[] FactoryDefault = [0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Reads 8 hex digits (spaces, colons and dashes between them are ignored) into the 4 password bytes.
    /// Anything else is rejected, so a half-typed password is never sent to a tag.</summary>
    public static bool TryParse(string? text, out byte[] password)
    {
        password = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 8) return false;

        var bytes = new byte[4];
        for (int i = 0; i < 4; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        password = bytes;
        return true;
    }

    /// <summary>"FF:FF:FF:FF" for showing a password back; empty for none.</summary>
    public static string Format(byte[]? password) =>
        password is { Length: 4 } ? string.Join(":", password.Select(b => b.ToString("X2"))) : "";
}
