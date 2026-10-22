using System.Text;

namespace TagBuddy.Services;

/// <summary>What GET_VERSION tells about a Type 2 chip. UserEnd is the first page after user memory
/// (lock and configuration pages follow); ConfigPage is CFG0, where AUTH0 lives, or -1 if none.</summary>
internal sealed record Type2Chip(string Name, int TotalPages, int UserEnd, bool HasPassword, int ConfigPage);

/// <summary>The byte-level rules of Type 2 tag memory (NTAG213/215/216, Ultralight EV1) that the NFC reader and
/// writer depend on, with no radio and no Android types, so they can be unit-tested: chip identification from
/// GET_VERSION, lock bits, the NDEF TLV, where raw text goes, the Amiibo key and how dumps are shown. See the
/// remarks on <see cref="NfcReader"/> for the memory layout.</summary>
internal static class Type2Memory
{
    /// <summary>The chip a GET_VERSION answer (8 bytes, starting 00) describes, or null for no or another answer.</summary>
    internal static Type2Chip? ChipFromVersion(byte[]? v)
    {
        if (v is not { Length: 8 } || v[0] != 0x00) return null;

        string family = v[2] switch { 0x03 => "MIFARE Ultralight EV1", 0x04 => "NTAG", _ => $"Type 2 chip (product {v[2]:X2})" };
        return (v[2], v[6]) switch
        {
            (0x04, 0x0F) => new("NTAG213", 45, 40, true, 41),
            (0x04, 0x11) => new("NTAG215", 135, 130, true, 131),
            (0x04, 0x13) => new("NTAG216", 231, 226, true, 227),
            (_, 0x0B) => new($"{family} (48 bytes)", 20, 16, true, 16),
            (_, 0x0E) => new($"{family} (128 bytes)", 41, 36, true, 37),
            _ => new($"{family}, size code {v[6]:X2}", 0, 0, false, -1),
        };
    }

    /// <summary>Which pages the tag's one-time lock bits mark as permanently read-only, by page number up to
    /// the end of user memory. Static lock bits (page 2, bytes 2-3) cover pages 3-15: byte 2 bit 3 is page 3 and
    /// bits 4-7 pages 4-7, byte 3 covers pages 8-15. Dynamic lock bits (the page after user memory) cover
    /// page 16 onwards in blocks: 2 pages per bit on NTAG213 and Ultralight EV1, 16 on NTAG215/216. An
    /// Amiibo writer typically sets 0F E0 and 01, locking pages 3, 13-15 and 16-31.</summary>
    internal static bool[] LockedPages(byte[] memory, Type2Chip chip)
    {
        var locked = new bool[Math.Max(chip.UserEnd, 16)];
        if (memory.Length >= 12)
        {
            byte s0 = memory[10], s1 = memory[11];
            if ((s0 & 0x08) != 0) locked[3] = true;
            for (int bit = 4; bit < 8; bit++) if ((s0 & (1 << bit)) != 0) locked[bit] = true;          // pages 4-7
            for (int bit = 0; bit < 8; bit++) if ((s1 & (1 << bit)) != 0) locked[8 + bit] = true;      // pages 8-15
        }
        int dynPage = chip.UserEnd;
        if (memory.Length >= dynPage * 4 + 2)
        {
            int block = chip.Name.Contains("215") || chip.Name.Contains("216") ? 16 : 2;
            int bits = (memory[dynPage * 4 + 1] << 8) | memory[dynPage * 4];
            for (int bit = 0; bit < 16; bit++)
            {
                if ((bits & (1 << bit)) == 0) continue;
                for (int page = 16 + bit * block; page < 16 + (bit + 1) * block && page < chip.UserEnd; page++)
                    locked[page] = true;
            }
        }
        return locked;
    }

    internal static bool SamePage(byte[] memory, int page, byte[] data) =>
        memory.Length >= (page + 1) * 4 && memory.AsSpan(page * 4, 4).SequenceEqual(data);

    /// <summary>An NDEF message as a Type 2 TLV: 03, the length (1 byte, or FF and 2 bytes from 255 on), the
    /// message, the FE terminator, padded with zeros to whole 4-byte pages.</summary>
    internal static byte[] NdefTlv(byte[] ndef)
    {
        var tlv = new List<byte> { 0x03 };
        if (ndef.Length < 0xFF) tlv.Add((byte)ndef.Length);
        else tlv.AddRange([0xFF, (byte)(ndef.Length >> 8), (byte)(ndef.Length & 0xFF)]);
        tlv.AddRange(ndef);
        tlv.Add(0xFE);
        while (tlv.Count % 4 != 0) tlv.Add(0x00);
        return tlv.ToArray();
    }

    /// <summary>Walks the TLV blocks of a Type 2 data area and returns the NDEF message (type 0x03).</summary>
    internal static bool TryExtractNdefTlv(List<byte> data, out byte[]? ndef, out bool complete)
    {
        ndef = null;
        complete = false;
        int i = 0;
        while (i < data.Count)
        {
            byte t = data[i];
            if (t == 0x00) { i++; continue; }          // NULL TLV
            if (t == 0xFE) return false;               // terminator before any NDEF
            if (i + 1 >= data.Count) return false;

            int len = data[i + 1];
            int header = 2;
            if (len == 0xFF)
            {
                if (i + 3 >= data.Count) return false;
                len = (data[i + 2] << 8) | data[i + 3];
                header = 4;
            }

            if (t == 0x03)
            {
                int available = Math.Min(len, data.Count - i - header);
                ndef = data.GetRange(i + header, Math.Max(0, available)).ToArray();
                complete = available >= len;
                return true;
            }
            i += header + len;                         // skip lock/memory control and proprietary TLVs
        }
        return false;
    }

    /// <summary>The memory image that stores <paramref name="payload"/> as raw bytes on a tag whose current memory
    /// is <paramref name="before"/>: the text goes into the first stretch of unlocked pages that holds it in one
    /// piece, so it reads back as one run; locked pages keep what they have, every other user page is zeroed, and
    /// the serial and page 3 stay as they are. Returns the image, or an error when no stretch is long enough.</summary>
    internal static (byte[]? Dump, string? Error) PlaceRawText(byte[] payload, byte[] before, Type2Chip chip)
    {
        var locked = LockedPages(before, chip);
        int need = (payload.Length + 3) / 4, bestStart = -1, bestLength = 0;
        for (int page = 4; page < chip.UserEnd;)
        {
            if (locked[page]) { page++; continue; }
            int start = page;
            while (page < chip.UserEnd && !locked[page]) page++;
            int length = page - start;
            if (length > bestLength) { bestLength = length; bestStart = start; }
            if (length >= need) { bestStart = start; bestLength = length; break; }
        }
        if (bestStart < 0 || bestLength < need)
            return (null, $"The text needs {payload.Length} bytes in one piece, but the longest unlocked stretch on this " +
                          $"{chip.Name} is {bestLength * 4} bytes (some pages are permanently locked).");

        var dump = new byte[chip.UserEnd * 4];
        Array.Copy(before, 0, dump, 0, Math.Min(16, before.Length));   // serial and page 3 stay as they are
        for (int page = 4; page < chip.UserEnd; page++)
            if (locked[page] && before.Length >= page * 4 + 4) Array.Copy(before, page * 4, dump, page * 4, 4);
        Array.Copy(payload, 0, dump, bestStart * 4, payload.Length);
        return (dump, null);
    }

    /// <summary>The password Nintendo tags derive from their 7-byte serial; the PACK is 80:80.</summary>
    internal static byte[] AmiiboPassword(byte[] uid) =>
        uid.Length < 7
            ? [0xFF, 0xFF, 0xFF, 0xFF]
            : [(byte)(0xAA ^ uid[1] ^ uid[3]), (byte)(0x55 ^ uid[2] ^ uid[4]),
               (byte)(0xAA ^ uid[3] ^ uid[5]), (byte)(0x55 ^ uid[4] ^ uid[6])];

    /// <summary>A Type 2 dump with the Nintendo capability container and NTAG215-sized content.</summary>
    internal static bool IsAmiiboDump(byte[] bytes) =>
        bytes.Length >= 520 && LooksAmiibo(bytes);

    /// <summary>The Nintendo capability container in page 3; enough to decide that the Amiibo key may be tried,
    /// even when the rest of the tag could not be read.</summary>
    internal static bool LooksAmiibo(byte[] bytes) =>
        bytes.Length >= 16 && bytes[12] == 0xF1 && bytes[13] == 0x10 && bytes[14] == 0xFF && bytes[15] == 0xEE;

    internal static string Ascii(byte[] bytes) =>
        new(bytes.Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());

    /// <summary>Formats page memory as "page: hex  ascii" lines, one per 4-byte page.</summary>
    internal static List<string>? FormatDump(byte[] bytes)
    {
        if (bytes.Length < 4) return null;
        var lines = new List<string>();
        for (int page = 0; page * 4 + 4 <= bytes.Length; page++)
        {
            var row = bytes[(page * 4)..(page * 4 + 4)];
            lines.Add($"{page:D3}: {ToHex(row)}  {Ascii(row)}");
        }
        return lines;
    }

    /// <summary>"13-15, 16-31" from a list of page numbers.</summary>
    internal static string Ranges(List<int> pages)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < pages.Count)
        {
            int start = pages[i], end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1) { end = pages[++i]; }
            parts.Add(start == end ? start.ToString() : $"{start}-{end}");
            i++;
        }
        return string.Join(", ", parts);
    }

    internal static string Pluralise(int n, string noun) => n == 1 ? noun[..1].ToUpperInvariant() + noun[1..] : $"{char.ToUpperInvariant(noun[0])}{noun[1..]}s";

    internal static string ToHex(byte[]? bytes) =>
        bytes is null ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
