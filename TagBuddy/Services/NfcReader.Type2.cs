using System.Text;
#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

namespace TagBuddy.Services;

/// <summary>Type 2 tag access (NTAG, Ultralight): READ (0x30) returns 16 bytes = 4 pages of 4 bytes, WRITE
/// (0xA2) one page. Chip identification, memory reads, passwords and lock bits; see the remarks on the class
/// for the memory layout.</summary>
public sealed partial class NfcReader
{
#if ANDROID
    /// <summary>What GET_VERSION tells about a Type 2 chip. UserEnd is the first page after user memory
    /// (lock and configuration pages follow); ConfigPage is CFG0, where AUTH0 lives, or -1 if none.</summary>
    private sealed record Type2Chip(string Name, int TotalPages, int UserEnd, bool HasPassword, int ConfigPage);

    private static Type2Chip? GetVersion(Type2Link link)
    {
        byte[]? v;
        try { v = link.Transceive([0x60]); }
        catch
        {
            // Older chips (Ultralight, NTAG203) answer with a NAK, which drops the connection.
            try { link.Reconnect(); } catch { }
            return null;
        }
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

    /// <summary>Reads every page the chip reports (or until the tag stops answering for unknown chips).</summary>
    private static byte[] ReadMemory(Type2Link link, Type2Chip? chip)
    {
        var buffer = new List<byte>();
        int total = chip is { TotalPages: > 0 } ? chip.TotalPages : 256;
        byte[]? previous = null;

        for (int page = 0; page < total; page += 4)
        {
            var chunk = link.Read(page);
            if (chunk is null) break;                 // past the end, or a protected page: the tag NAKs

            // Unknown size: READ wraps around at the end, so stop once the data repeats.
            if (chip is not { TotalPages: > 0 })
            {
                if (previous is not null && chunk.AsSpan(0, 16).SequenceEqual(previous)) break;
                previous = chunk[..16];
            }

            int take = Math.Min(16, (total - page) * 4);
            buffer.AddRange(chunk.Take(take));
        }

        // Unknown size: the last READ may already have wrapped to page 0; cut the copied pages off.
        if (chip is not { TotalPages: > 0 } && buffer.Count >= 32)
        {
            var all = buffer.ToArray();
            for (int page = all.Length / 4 - 4; page < all.Length / 4 - 1; page++)
            {
                if (page > 1 && all.AsSpan(page * 4, 8).SequenceEqual(all.AsSpan(0, 8)))
                    return all[..(page * 4)];
            }
            return all;
        }
        return buffer.ToArray();
    }

    /// <summary>The password Nintendo tags derive from their 7-byte serial; the PACK is 80:80.</summary>
    private static byte[] AmiiboPassword(byte[] uid) =>
        uid.Length < 7
            ? [0xFF, 0xFF, 0xFF, 0xFF]
            : [(byte)(0xAA ^ uid[1] ^ uid[3]), (byte)(0x55 ^ uid[2] ^ uid[4]),
               (byte)(0xAA ^ uid[3] ^ uid[5]), (byte)(0x55 ^ uid[4] ^ uid[6])];

    /// <summary>PWD_AUTH with the given password. A wrong password and a tag that moved away during the
    /// handshake both surface as a failed transceive, so one failure is retried once after reconnecting
    /// before it counts; the caller words its message accordingly.</summary>
    private static bool TryAuthenticate(Type2Link link, byte[] pwd)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var pack = link.Transceive([0x1B, pwd[0], pwd[1], pwd[2], pwd[3]]);
                if (pack is { Length: >= 2 }) return true;
            }
            catch
            {
                // NAK or lost tag: fall through to the retry.
            }
            try { link.Reconnect(); } catch { return false; }
        }
        return false;
    }

    /// <summary>AUTH0 from CFG0: the first page that needs the password (0xFF means none), or -1 if unknown.</summary>
    private static int ReadAuth0(Type2Link link, Type2Chip? chip)
    {
        if (chip is null || chip.ConfigPage < 0) return -1;
        var cfg = link.Read(chip.ConfigPage);
        return cfg is null ? -1 : cfg[3];
    }

    /// <summary>AUTHLIM from CFG1: how many wrong passwords the tag tolerates before it locks for good
    /// (0 means no limit), or 0 if unknown.</summary>
    private static int ReadAuthLimit(Type2Link link, Type2Chip? chip)
    {
        if (chip is null || chip.ConfigPage < 0) return 0;
        var cfg1 = link.Read(chip.ConfigPage + 1);
        return cfg1 is null ? 0 : cfg1[0] & 0x07;
    }

    private static bool IsType2(Tag tag)
    {
        var techs = tag.GetTechList() ?? [];
        return techs.Contains("android.nfc.tech.NfcA")
            && !techs.Contains("android.nfc.tech.IsoDep")
            && !techs.Contains("android.nfc.tech.MifareClassic");
    }

    private static bool SamePage(byte[] memory, int page, byte[] data) =>
        memory.Length >= (page + 1) * 4 && memory.AsSpan(page * 4, 4).SequenceEqual(data);

    /// <summary>Writes one page. On a failure the connection is restored and the page read back (some NFC
    /// stacks report the tag's 4-bit ACK as an error); then, once, the password is tried when one is known.
    /// <paramref name="pwd"/> is null for tags whose password this app must not guess.</summary>
    private static bool WritePageWithRetry(Type2Link link, int page, byte[] data, byte[]? pwd, ref bool authed, out string error)
    {
        error = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { link.WritePage(page, data); return true; }
            catch (Exception ex) { error = ex.Message; }

            try { link.Reconnect(); }
            catch (Exception ex) { error = "the tag moved away (" + ex.Message + ")"; return false; }

            // A reconnect ends the password session; open it again so later pages still go through.
            if (authed && pwd is not null && !TryAuthenticate(link, pwd))
            {
                error += ", and the password session was lost";
                return false;
            }

            var check = link.Read(page);
            if (check is not null && check.AsSpan(0, 4).SequenceEqual(data)) return true;

            if (authed || pwd is null || !TryAuthenticate(link, pwd)) return false;
            authed = true;
        }
        return false;
    }

    /// <summary>Which pages the tag's one-time lock bits mark as permanently read-only, by page number up to
    /// the end of user memory. Static lock bits (page 2, bytes 2-3) cover pages 3-15: byte 2 bit 3 is page 3 and
    /// bits 4-7 pages 4-7, byte 3 covers pages 8-15. Dynamic lock bits (the page after user memory) cover
    /// page 16 onwards in blocks: 2 pages per bit on NTAG213 and Ultralight EV1, 16 on NTAG215/216. An
    /// Amiibo writer typically sets 0F E0 and 01, locking pages 3, 13-15 and 16-31.</summary>
    private static bool[] LockedPages(byte[] memory, Type2Chip chip)
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

    /// <summary>Raw page access to a Type 2 tag. Prefers Android's MifareUltralight tech, whose WritePage
    /// understands the tag's 4-bit ACK, and falls back to plain NfcA commands.</summary>
    private sealed class Type2Link : IDisposable
    {
        private readonly MifareUltralight? _ul;
        private readonly NfcA? _nfcA;

        public byte[] Uid { get; }

        private Type2Link(Tag tag, MifareUltralight? ul, NfcA? nfcA)
        {
            _ul = ul;
            _nfcA = nfcA;
            Uid = tag.GetId() ?? [];
        }

        public static Type2Link? Open(Tag tag)
        {
            var ul = MifareUltralight.Get(tag);
            if (ul is not null) return new Type2Link(tag, ul, null);
            var nfcA = NfcA.Get(tag);
            return nfcA is null ? null : new Type2Link(tag, null, nfcA);
        }

        private BasicTagTechnology Tech => (BasicTagTechnology?)_ul ?? _nfcA!;

        public void Connect()
        {
            Tech.Connect();
            if (_ul is not null) _ul.Timeout = 1000;
            else _nfcA!.Timeout = 1000;
        }

        /// <summary>A NAK (wrong password, unknown command, locked page) puts the tag to sleep; wake it again.</summary>
        public void Reconnect()
        {
            try { Tech.Close(); } catch { }
            Tech.Connect();
        }

        public byte[]? Transceive(byte[] command) =>
            _ul is not null ? _ul.Transceive(command) : _nfcA!.Transceive(command);

        /// <summary>READ: 4 pages (16 bytes) from the given page, or null when the tag refuses.</summary>
        public byte[]? Read(int page)
        {
            try
            {
                var result = Transceive([0x30, (byte)page]);
                return result is { Length: >= 16 } ? result : null;
            }
            catch
            {
                try { Reconnect(); } catch { }
                return null;
            }
        }

        public void WritePage(int page, byte[] data)
        {
            if (_ul is not null) _ul.WritePage(page, data);
            else _nfcA!.Transceive([0xA2, (byte)page, data[0], data[1], data[2], data[3]]);
        }

        public void Dispose()
        {
            try { Tech.Close(); } catch { }
            Tech.Dispose();
        }
    }
#endif
}
