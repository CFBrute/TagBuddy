namespace TagBuddy.Services;

/// <summary>Reading a MIFARE Classic card with the keys everyone already knows. Each sector of a Classic card is
/// locked behind two 6-byte keys; a card that was never given custom keys still carries the factory and other
/// well-known public keys, so a reader can open it. This app only ever tries that public list: it never recovers
/// an unknown key, so a real access card (custom keys) stays closed and shows only its serial.
///
/// <para>Trying keys is safe: unlike a password-protected NTAG, a Classic sector has no wrong-key lockout, so a
/// failed attempt costs nothing. Reading is read-only; the segment editor (<see cref="TagEdit"/>) writes data blocks
/// of sectors these keys or the person's own key open, never block 0 and never a sector trailer.</para></summary>
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

    /// <summary>Reads a key the person typed: 12 hex digits, with spaces, colons and dashes between them ignored.
    /// Anything else is rejected. A Classic sector has no wrong-key lockout, so trying a wrong key does no harm.</summary>
    public static bool TryParseKey(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return false;
        key = Hex(hex);
        return true;
    }

    /// <summary>Whether a key is one of the built-in public defaults (the sign of a blank or unsecured sector).</summary>
    public static bool IsDefault(byte[] key) => Defaults.Any(d => d.Key.AsSpan().SequenceEqual(key));

    /// <summary>The access bits of a sector trailer in transport configuration (as a blank card leaves the factory):
    /// key A reads and writes every block and both keys, key B is a plain data key. Bytes 6-8 of the trailer.</summary>
    public static readonly byte[] TransportAccessBits = [0xFF, 0x07, 0x80];

    /// <summary>A sector trailer giving the sector <paramref name="key"/> as both key A and key B. The access bits and
    /// byte 9 are kept from <paramref name="current"/> (the transport ones when the trailer could not be read), so what
    /// the key may do in the sector does not change; only the key does.</summary>
    public static byte[] TrailerFor(byte[] key, byte[] current) =>
        [.. key, .. (current.Length >= 10 ? current[6..10] : [.. TransportAccessBits, (byte)0x69]), .. key];

    /// <summary>What a sector's access bits allow, in words, one line per rule that differs from "key A does
    /// everything". The three bytes (6-8 of the trailer) hold, for each of the four blocks, three bits C1 C2 C3, each
    /// stored twice (plain and inverted) as a check. Blocks 0-2 are the data blocks (on a 16-block sector, each of the
    /// three bits covers five blocks), block 3 is the trailer: the keys and these very bits.
    /// Returns null when the bits are corrupt (plain and inverted halves disagree): such a sector is dead for good.</summary>
    public static IReadOnlyList<string>? DescribeAccess(byte[] trailer)
    {
        if (trailer.Length < 9) return [];
        byte b6 = trailer[6], b7 = trailer[7], b8 = trailer[8];
        // Plain bits: C1 in b7 high nibble, C2 in b8 low nibble, C3 in b8 high nibble; inverted in b6 and b7 low.
        int c1 = b7 >> 4, c2 = b8 & 0x0F, c3 = b8 >> 4;
        int nc1 = b6 & 0x0F, nc2 = b6 >> 4, nc3 = b7 & 0x0F;
        if (((c1 ^ nc1) & 0x0F) != 0x0F || ((c2 ^ nc2) & 0x0F) != 0x0F || ((c3 ^ nc3) & 0x0F) != 0x0F) return null;

        var lines = new List<string>();
        for (int block = 0; block < 3; block++)
        {
            int bits = (((c1 >> block) & 1) << 2) | (((c2 >> block) & 1) << 1) | ((c3 >> block) & 1);
            var rule = DataRule(bits);
            if (bits != 0) lines.Add($"block {block}: {rule}");
        }
        int t = (((c1 >> 3) & 1) << 2) | (((c2 >> 3) & 1) << 1) | ((c3 >> 3) & 1);
        lines.Add("keys: " + TrailerRule(t));
        return lines;
    }

    /// <summary>The access-bit combination's meaning for a data block.</summary>
    private static string DataRule(int bits) => bits switch
    {
        0b000 => "read and write with A or B",
        0b010 => "read with A or B, never written",
        0b100 => "read with A or B, write with B",
        0b110 => "read with A or B, write and increment with B, decrement with A or B (value block)",
        0b001 => "read with A or B, decrement with A or B, never written (value block)",
        0b011 => "read and write with B only",
        0b101 => "read with B only, never written",
        _ => "never read or written",
    };

    /// <summary>The access-bit combination's meaning for the trailer: who may change the keys and the rules.</summary>
    private static string TrailerRule(int bits) => bits switch
    {
        0b000 => "A changes both keys; the rules can never be changed; B is plain data",
        0b010 => "keys and rules can never be changed; B is plain data",
        0b100 => "B changes both keys; the rules can never be changed",
        0b110 => "keys and rules can never be changed",
        0b001 => "A changes both keys and the rules; B is plain data (factory setting)",
        0b011 => "B changes both keys and the rules",
        0b101 => "B changes the rules; the keys can never be changed",
        _ => "keys and rules can never be changed",
    };

    /// <summary>The access rules under a sector header: "  rules: ..." lines, or a warning when the bits are corrupt.</summary>
    public static IEnumerable<string> AccessLines(byte[] trailer)
    {
        var rules = DescribeAccess(trailer);
        if (rules is null) return ["  rules: corrupt access bits (plain and inverted halves disagree); this sector may be unusable"];
        return rules.Select(r => "  rules: " + r);
    }

    /// <summary>Whether a trailer's access bits are the transport ones (a sector whose rules were never changed).</summary>
    public static bool HasTransportAccessBits(byte[] trailer) =>
        trailer.Length >= 9 && trailer.AsSpan(6, 3).SequenceEqual(TransportAccessBits);

    /// <summary>The card's name from its memory size in bytes: "MIFARE Classic 1K" (1024), "4K", "2K" or "Mini" (320).</summary>
    public static string ChipName(int sizeBytes) => sizeBytes switch
    {
        320 => "MIFARE Classic Mini",
        _ => $"MIFARE Classic {sizeBytes / 1024}K",
    };

    /// <summary>Whether a block is its sector's trailer (keys A and B and the access bits): the last block of each
    /// 4-block sector, and from block 128 on (4K cards) of each 16-block sector.</summary>
    public static bool IsTrailer(int block) => block < 128 ? block % 4 == 3 : (block - 128) % 16 == 15;

    /// <summary>The sector a block belongs to.</summary>
    public static int SectorOf(int block) => block < 128 ? block / 4 : 32 + (block - 128) / 16;

    /// <summary>"FFFFFFFFFFFF" from a 6-byte key, the way keys are usually written.</summary>
    public static string KeyHex(byte[] key) => string.Concat(key.Select(b => b.ToString("X2")));

    /// <summary>The header line above a sector's blocks: which key opened it, and what that key is known as.</summary>
    public static string SectorHeader(int sector, byte[] key, char slot, string name) =>
        $"Sector {sector:D2} · key {slot} {KeyHex(key)} ({name})";

    /// <summary>A sector the public keys did not open.</summary>
    public static string LockedSector(int sector) => $"Sector {sector:D2} · locked (custom key, not read)";

    /// <summary>One block as "block  hex  ascii", the 16 hex bytes in four-byte groups so the eye can scan them,
    /// and the readable characters after. No per-byte colons: they only widen the line.</summary>
    public static string FormatBlock(int block, byte[] data)
    {
        var groups = Enumerable.Range(0, (data.Length + 3) / 4)
            .Select(g => string.Concat(data.Skip(g * 4).Take(4).Select(b => b.ToString("X2"))));
        return $"  {block:D3}  {string.Join(" ", groups)}  {Ascii(data)}";
    }

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
