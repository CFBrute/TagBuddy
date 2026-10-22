namespace TagBuddy.Services;

/// <summary>What can be told about a Type 2 tag dump (a memory image, as in a .bin file) from its bytes alone,
/// before it goes near a tag.</summary>
public static class Type2Dump
{
    /// <summary>Why the bytes cannot be a Type 2 dump, or null when they can.</summary>
    public static string? Problem(byte[] data) =>
        data.Length < 16 ? $"Only {data.Length} bytes; a tag dump is at least 16 bytes (4 pages)."
        : data.Length % 4 != 0 ? $"{data.Length} bytes is not a whole number of 4-byte pages."
        : data.Length > 4096 ? $"{data.Length} bytes is far more than any Type 2 tag holds."
        : null;

    /// <summary>The 7-byte serial stored in pages 0-2 (bytes 3 and 8 are check bytes), as "04:A1:...", or ""
    /// when the dump has none (made from text, or too short).</summary>
    public static string Serial(byte[] data)
    {
        if (data.Length < 9 || data[0] == 0 && data[1] == 0) return "";
        byte[] uid = [data[0], data[1], data[2], data[4], data[5], data[6], data[7]];
        return string.Join(":", uid.Select(b => b.ToString("X2")));
    }

    /// <summary>The chip label a dump is saved under when no tag was read: "Amiibo" or "Type2".</summary>
    public static string ChipLabel(byte[] data) => NfcReader.IsAmiiboDump(data) ? "Amiibo" : "Type2";
}
