namespace TagBuddy.Services;

/// <summary>Names the chip and protocol behind a card from the small public identifiers every card answers with,
/// even one the app cannot read into: the SAK byte of an ISO 14443-A card, the manufacturer byte of an ISO 15693
/// label, and a DESFire's version frame. All pure, so it is unit-tested.</summary>
public static class CardTypes
{
    /// <summary>The chip an ISO 14443-A SAK byte points to, or null when it says nothing useful. SAK is a hint,
    /// not proof (clones copy it), so the words are hedged.</summary>
    public static string? ChipFromSak(int sak)
    {
        // The cascade bit (0x04) only means the UID is read in parts; clear it before matching.
        int s = sak & ~0x04;
        return s switch
        {
            0x00 => "MIFARE Ultralight or NTAG (Type 2 tag)",
            0x08 => "MIFARE Classic 1K (or compatible)",
            0x09 => "MIFARE Mini (0.3K)",
            0x10 => "MIFARE Plus 2K (security level 2)",
            0x11 => "MIFARE Plus 4K (security level 2)",
            0x18 => "MIFARE Classic 4K (or compatible)",
            0x19 => "MIFARE Classic 2K",
            0x28 => "SmartMX emulating MIFARE Classic 1K",
            0x38 => "SmartMX emulating MIFARE Classic 4K",
            0x20 => "ISO 14443-4 smart card (DESFire, MIFARE Plus SL3, or a secure element)",
            _ when (s & 0x20) != 0 => "ISO 14443-4 smart card",
            _ when (s & 0x08) != 0 => "MIFARE Classic compatible",
            _ => null,
        };
    }

    /// <summary>The chip maker behind an ISO 15693 label from the manufacturer byte of its UID (the byte next to
    /// the leading 0xE0). Unknown codes come back as "manufacturer 0xNN".</summary>
    public static string Iso15693Manufacturer(byte code) => code switch
    {
        0x01 => "Motorola",
        0x02 => "STMicroelectronics",
        0x03 => "Hitachi",
        0x04 => "NXP Semiconductors",
        0x05 => "Infineon",
        0x07 => "Texas Instruments",
        0x08 => "Fujitsu",
        0x09 => "Matsushita",
        0x0A => "NEC",
        0x16 => "EM Microelectronic",
        0x1F => "Melexis",
        _ => $"manufacturer 0x{code:X2}",
    };

    /// <summary>The NXP ICODE product of an ISO 15693 label from its UID type byte, when the maker is NXP (0x04),
    /// else null. The type byte is the one before the manufacturer byte.</summary>
    public static string? NxpIcodeProduct(byte type) => type switch
    {
        0x01 => "ICODE SLI",
        0x02 => "ICODE SLIX",
        0x03 => "ICODE SLIX2",
        0x04 => "ICODE SLIX-S",
        0x05 => "ICODE SLIX-L",
        _ => null,
    };

    /// <summary>What a DESFire GET_VERSION hardware block says: the generation and the storage size, like
    /// "MIFARE DESFire EV1, 4 KB". The block is 7 bytes: vendor, type, subtype, major, minor, storage, protocol.</summary>
    public static string? DesfireFromVersion(byte[] hardwareBlock)
    {
        if (hardwareBlock.Length < 7 || hardwareBlock[0] != 0x04) return null;   // 0x04 = NXP
        string generation = hardwareBlock[3] switch
        {
            0x00 => "MIFARE DESFire",
            0x01 => "MIFARE DESFire EV1",
            0x12 => "MIFARE DESFire EV2",
            0x33 => "MIFARE DESFire EV3",
            _ => "MIFARE DESFire",
        };
        return StorageSize(hardwareBlock[5]) is { } size ? $"{generation}, {size}" : generation;
    }

    /// <summary>The storage size a DESFire version byte encodes: 0xNN is 2^(NN>>1) bytes, and an odd low bit means
    /// "between this and the next", which a few chips use.</summary>
    private static string? StorageSize(byte code)
    {
        int exponent = code >> 1;
        if (exponent is < 8 or > 24) return null;
        long bytes = 1L << exponent;
        string size = bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} bytes";
        return (code & 1) != 0 ? $"over {size}" : size;
    }
}
