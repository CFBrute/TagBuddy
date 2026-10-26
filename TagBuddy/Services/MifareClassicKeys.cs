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

    /// <summary>The parts of block 0 (the manufacturer block) worth labelling, other than the UID — which is already
    /// shown as the serial number, so it is not repeated here: the check byte (BCC) and the manufacturer bytes.</summary>
    public static IReadOnlyList<(string Label, string Value)> ManufacturerBlock(byte[]? block0)
    {
        if (block0 is not { Length: 16 }) return [];
        byte bcc = block0[4];
        byte expected = (byte)(block0[0] ^ block0[1] ^ block0[2] ^ block0[3]);
        return
        [
            ("Check byte (BCC)", bcc == expected ? $"{bcc:X2} — valid" : $"{bcc:X2} — should be {expected:X2} (does not match the UID)"),
            ("Manufacturer bytes", string.Join(" ", block0[5..].Select(b => b.ToString("X2")))),
        ];
    }

    /// <summary>One verdict on whether the chip looks like a genuine NXP MIFARE Classic, from the originality signature
    /// (<paramref name="signature"/>, null if the chip did not answer) and block 0's UID. A heuristic, not proof:
    /// only verifying the signature against NXP's key would be proof.</summary>
    public static string GenuineVerdict(byte[]? block0, byte[]? signature)
    {
        string sig = signature is { Length: > 0 }
            ? $"It returned a {signature.Length}-byte originality signature, which genuine NXP EV1 chips carry (not yet checked against NXP's key)."
            : "It returned no originality signature; genuine NXP EV1 chips do, while older genuine Classic chips and most clones do not.";

        if (block0 is not { Length: 16 }) return sig;

        byte m = block0[0];
        bool nxpByte = m == 0x04;
        string uid = nxpByte
            ? "The UID begins 04, NXP's manufacturer code; on a genuine card block 0 is locked at the factory and cannot be rewritten."
            : $"The UID begins {m:X2}, not NXP's 04, so this is most likely a \"magic\"/compatible clone (or a random-UID card); block 0 on such cards is usually writable, so it cannot be relied on as fixed.";
        if ((byte)(block0[0] ^ block0[1] ^ block0[2] ^ block0[3]) != block0[4])
            uid += " Its check byte does not match the UID, which a normal card never has.";

        string verdict = (signature is { Length: > 0 }, nxpByte) switch
        {
            (true, true) => "Likely genuine (not cryptographically verified). ",
            (false, false) => "Likely a clone or compatible chip, not a genuine NXP card. ",
            _ => "Mixed signals, so not certain. ",
        };
        return verdict + sig + " " + uid;
    }

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

    /// <summary>A sector trailer with the given keys, and either the given rules or (null) the rules kept from
    /// <paramref name="current"/> (the transport ones when the trailer could not be read). Byte 9 is kept.</summary>
    public static byte[] TrailerFor(byte[] keyA, byte[] keyB, byte[] current, SectorRules? rules)
    {
        byte[] bits = rules is { } r ? r.Encode() : current.Length >= 9 ? current[6..9] : TransportAccessBits;
        byte extra = current.Length >= 10 ? current[9] : (byte)0x69;
        return [.. keyA, .. bits, extra, .. keyB];
    }

    /// <summary>The rules a trailer holds, or null when its access bits are corrupt (plain and inverted halves disagree).</summary>
    public static SectorRules? DecodeRules(byte[] trailer)
    {
        if (trailer.Length < 9) return null;
        byte b6 = trailer[6], b7 = trailer[7], b8 = trailer[8];
        int c1 = b7 >> 4, c2 = b8 & 0x0F, c3 = b8 >> 4;
        int nc1 = b6 & 0x0F, nc2 = b6 >> 4, nc3 = b7 & 0x0F;
        if (((c1 ^ nc1) & 0x0F) != 0x0F || ((c2 ^ nc2) & 0x0F) != 0x0F || ((c3 ^ nc3) & 0x0F) != 0x0F) return null;
        var bits = new int[4];
        for (int block = 0; block < 4; block++)
            bits[block] = (((c1 >> block) & 1) << 2) | (((c2 >> block) & 1) << 1) | ((c3 >> block) & 1);
        return new SectorRules(bits[0], bits[1], bits[2], bits[3]);
    }

    /// <summary>The eight rules a data block can have, by their 3-bit code (C1 C2 C3), in words.</summary>
    public static readonly IReadOnlyList<(int Bits, string Text)> DataRules =
    [
        (0b000, "read and write with A or B"),
        (0b010, "read with A or B, never written"),
        (0b100, "read with A or B, write with B"),
        (0b110, "read with A or B, write and increment with B, decrement with A or B (value block)"),
        (0b001, "read with A or B, decrement with A or B, never written (value block)"),
        (0b011, "read and write with B only"),
        (0b101, "read with B only, never written"),
        (0b111, "never read or written"),
    ];

    /// <summary>The eight rules for the trailer (who may change the keys and the rules). <c>Permanent</c> marks the four
    /// that freeze the rules for the life of the sector: once written, nothing in the sector can ever be changed again.</summary>
    public static readonly IReadOnlyList<(int Bits, string Text, bool Permanent)> TrailerRules =
    [
        (0b001, "A changes both keys and the rules; B is plain data (factory setting)", false),
        (0b000, "A changes both keys; the rules can never be changed; B is plain data", true),
        (0b011, "B changes both keys and the rules", false),
        (0b100, "B changes both keys; the rules can never be changed", true),
        (0b101, "B changes the rules; the keys can never be changed", false),
        (0b010, "keys and rules can never be changed; B is plain data", true),
        (0b110, "keys and rules can never be changed", true),
        (0b111, "keys and rules can never be changed (and the keys are unreadable)", true),
    ];

    /// <summary>What a sector's access bits allow, in words, one line per rule that differs from "key A does
    /// everything". The three bytes (6-8 of the trailer) hold, for each of the four blocks, three bits C1 C2 C3, each
    /// stored twice (plain and inverted) as a check. Blocks 0-2 are the data blocks (on a 16-block sector, each of the
    /// three bits covers five blocks), block 3 is the trailer: the keys and these very bits.
    /// Returns null when the bits are corrupt (plain and inverted halves disagree): such a sector is dead for good.</summary>
    public static IReadOnlyList<string>? DescribeAccess(byte[] trailer)
    {
        if (trailer.Length < 9) return [];
        if (DecodeRules(trailer) is not { } rules) return null;
        var lines = new List<string>();
        for (int block = 0; block < 3; block++)
            if (rules.Data[block] != 0) lines.Add($"block {block}: {DataRule(rules.Data[block])}");
        lines.Add("keys: " + TrailerRule(rules.Trailer));
        return lines;
    }

    public static string DataRule(int bits) => DataRules.First(r => r.Bits == bits).Text;
    public static string TrailerRule(int bits) => TrailerRules.First(r => r.Bits == bits).Text;

    /// <summary>The access rules under a sector header: "  rules: ..." lines, or a warning when the bits are corrupt.</summary>
    public static IEnumerable<string> AccessLines(byte[] trailer)
    {
        var rules = DescribeAccess(trailer);
        if (rules is null) return ["  rules: corrupt access bits (plain and inverted halves disagree); this sector may be unusable"];
        return rules.Select(r => "  rules: " + r);
    }

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

/// <summary>A sector's access rules: the 3-bit code of each of the three data groups (blocks 0-2 of a 4-block sector;
/// on a 16-block sector each covers five blocks) and of the trailer. Built from a choice, never from loose bytes, so the
/// encoded bits always carry their inverted halves and can never corrupt a sector.</summary>
public sealed record SectorRules(int Data0, int Data1, int Data2, int Trailer)
{
    public static readonly SectorRules Transport = new(0, 0, 0, 0b001);

    public int[] Data => [Data0, Data1, Data2];

    /// <summary>Whether the trailer rule freezes the sector's rules for good.</summary>
    public bool IsPermanent => MifareClassicKeys.TrailerRules.First(r => r.Bits == Trailer).Permanent;

    public SectorRules WithData(int group, int bits) => group switch
    {
        0 => this with { Data0 = bits },
        1 => this with { Data1 = bits },
        _ => this with { Data2 = bits },
    };

    /// <summary>Bytes 6-8 of the trailer: C1 C2 C3 of the four blocks, each stored plain and inverted.</summary>
    public byte[] Encode()
    {
        int c1 = 0, c2 = 0, c3 = 0;
        int[] all = [Data0, Data1, Data2, Trailer];
        for (int block = 0; block < 4; block++)
        {
            if ((all[block] & 0b100) != 0) c1 |= 1 << block;
            if ((all[block] & 0b010) != 0) c2 |= 1 << block;
            if ((all[block] & 0b001) != 0) c3 |= 1 << block;
        }
        return [(byte)(((~c2 & 0x0F) << 4) | (~c1 & 0x0F)), (byte)((c1 << 4) | (~c3 & 0x0F)), (byte)((c3 << 4) | c2)];
    }
}
