namespace TagBuddy.Services;

/// <summary>Reading a MIFARE Classic card with the keys everyone already knows. Each sector of a Classic card is
/// locked behind two 6-byte keys; a card that was never given custom keys still carries the factory and other
/// well-known public keys, so a reader can open it. This app only ever tries that public list: it never recovers
/// an unknown key, so a real access card (custom keys) stays closed and shows only its serial.
///
/// <para>Trying keys is safe: unlike a password-protected NTAG, a Classic sector has no wrong-key lockout, so a
/// failed attempt costs nothing. Everything here is read-only; the app does not write MIFARE Classic.</para></summary>
public static class MifareClassicKeys
{
    /// <summary>The public default keys, tried in this order for key A and then key B of each sector. These are
    /// published in NXP's documentation and shipped by every RFID tool; they are the baseline a blank or test
    /// card carries, not a way past a card that was secured.</summary>
    public static readonly IReadOnlyList<(byte[] Key, string Name)> Defaults =
    [
        (Hex("FFFFFFFFFFFF"), "factory default"),
        (Hex("A0A1A2A3A4A5"), "MAD"),
        (Hex("D3F7D3F7D3F7"), "NDEF / NFC Forum"),
        (Hex("000000000000"), "all zero"),
        (Hex("B0B1B2B3B4B5"), "common"),
        (Hex("4D3A99C351DD"), "common"),
        (Hex("1A982C7E459A"), "common"),
        (Hex("AABBCCDDEEFF"), "common"),
    ];

    /// <summary>"FFFFFFFFFFFF" from a 6-byte key, the way keys are usually written.</summary>
    public static string KeyHex(byte[] key) => string.Concat(key.Select(b => b.ToString("X2")));

    /// <summary>The header line above a sector's blocks: which key opened it, and what that key is known as.</summary>
    public static string SectorHeader(int sector, byte[] key, char slot, string name) =>
        $"Sector {sector:D2}  ·  key {slot} {KeyHex(key)} ({name})";

    /// <summary>A sector the public keys did not open.</summary>
    public static string LockedSector(int sector) => $"Sector {sector:D2}  ·  locked (custom key, not read)";

    /// <summary>One block as "block: hex  ascii".</summary>
    public static string FormatBlock(int block, byte[] data) =>
        $"  {block:D3}: {string.Join(":", data.Select(b => b.ToString("X2")))}  {Ascii(data)}";

    /// <summary>What the Access detail says after a read, given how many of the sectors opened.</summary>
    public static string AccessSummary(int opened, int total) => opened switch
    {
        0 => "Secure item: every sector uses a custom key, so only the serial number is readable.",
        _ when opened == total => $"All {total} sectors opened with common default keys, so this card is not secured with custom keys.",
        _ => $"{opened} of {total} sectors opened with common default keys; the rest use custom keys and were not read.",
    };

    private static string Ascii(byte[] bytes) =>
        new(bytes.Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());

    private static byte[] Hex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
