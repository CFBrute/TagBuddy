using System.Diagnostics.CodeAnalysis;
using System.Text;
#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

using static TagBuddy.Services.Type2Memory;

namespace TagBuddy.Services;

/// <summary>Reading: what a tag is (radio, chip, card family), its NDEF content, and its Type 2 memory.
/// Payment cards, passports and access cards are identified from public data only.</summary>
public sealed partial class NfcReader
{
#if ANDROID
    // ------------------------------------------------------------------------------------------------
    // Tag description
    // ------------------------------------------------------------------------------------------------

    private static NfcTagInfo Describe(Tag tag)
    {
        var techs = (tag.GetTechList() ?? [])
            .Select(t => t.Split('.').Last())
            .ToList();

        var details = new List<(string, string)>();
        var records = new List<NfcRecordInfo>();
        List<string>? memory = null;
        byte[]? rawMemory = null;
        string? chipName = null;
        byte[]? ndefBytes = null;

        using (var nfcA = NfcA.Get(tag))
        {
            if (nfcA is not null)
            {
                details.Add(("Radio", "ISO 14443-A (NfcA)"));
                details.Add(("ATQA / SAK", $"{ToHex(nfcA.GetAtqa())} / 0x{nfcA.Sak:X2}"));
            }
        }
        using (var nfcB = NfcB.Get(tag))
        {
            if (nfcB is not null) details.Add(("Radio", "ISO 14443-B (NfcB)"));
        }
        using (var nfcF = NfcF.Get(tag))
        {
            if (nfcF is not null) details.Add(("Radio", "FeliCa (NfcF), common on Japanese transit cards"));
        }
        using (var nfcV = NfcV.Get(tag))
        {
            if (nfcV is not null) details.Add(("Radio", "ISO 15693 (NfcV), typical for library and inventory labels"));
        }

        bool isClassic = false;
        using (var classic = MifareClassic.Get(tag))
        {
            if (classic is not null)
            {
                isClassic = true;
                details.Add(("Chip", $"MIFARE Classic {classic.Size * 8 / 1024}K, {classic.SectorCount} sectors"));
                details.Add(("Card", "Access card (MIFARE Classic)"));
                details.Add(("Access", "Secure item: data sectors are key-protected; only the serial number is readable"));
            }
        }

        // ISO-DEP smart cards: payment cards, passports, DESFire access and transit cards.
        bool isIsoDep = false;
        using (var isoDep = IsoDep.Get(tag))
        {
            if (isoDep is not null)
            {
                isIsoDep = true;
                details.Add(("Chip", "ISO 14443-4 smart card"));
                try
                {
                    isoDep.Timeout = 1500;
                    isoDep.Connect();
                    IdentifySmartCard(isoDep, details, out ndefBytes);
                }
                catch (Exception ex)
                {
                    details.Add(("Note", "Card moved away before it could be identified (" + ex.Message + ")"));
                }
                finally { try { isoDep.Close(); } catch { } }
            }
        }

        // Type 2 tags (NTAG / Ultralight stickers, Amiibo): read every page through plain READ commands.
        if (ndefBytes is null && !isIsoDep && !isClassic)
        {
            using var link = Type2Link.Open(tag);
            if (link is not null)
            {
                try
                {
                    link.Connect();
                    var chip = GetVersion(link);
                    if (chip is not null)
                    {
                        chipName = chip.Name;
                        details.Add(("Chip", chip.TotalPages > 0
                            ? $"{chip.Name}, {chip.TotalPages} pages, {(chip.UserEnd - 4) * 4} bytes user memory"
                            : chip.Name));
                    }

                    rawMemory = ReadMemory(link, chip);

                    // Read-protected pages (PROT=1) on an Amiibo-style tag: present the Nintendo key, then read
                    // again. Other protected tags are left alone: a wrong guess counts against their AUTHLIM.
                    if (chip is { TotalPages: > 0 } && rawMemory.Length < chip.TotalPages * 4
                        && Features.Amiibo && LooksAmiibo(rawMemory) && TryAuthenticate(link, AmiiboPassword(link.Uid)))
                        rawMemory = ReadMemory(link, chip);

                    if (rawMemory.Length >= 16)
                    {
                        ndefBytes = ExtractType2Ndef(rawMemory, details);
                        DescribeType2Layout(rawMemory, chip, details);
                    }
                    memory = FormatDump(rawMemory);
                }
                catch (Exception ex)
                {
                    details.Add(("Note", "Could not read the tag's memory (" + ex.Message + ")"));
                }
            }
        }

        // If Android did expose an NDEF tech (it will not with SkipNdefCheck, but be safe), use it.
        if (ndefBytes is null)
        {
            using var ndef = Ndef.Get(tag);
            if (ndef?.CachedNdefMessage is { } cached)
                foreach (var record in cached.GetRecords() ?? [])
                    records.Add(DescribeRecord(record));
        }

        if (ndefBytes is not null)
        {
            try
            {
                var message = new NdefMessage(ndefBytes);
                foreach (var record in message.GetRecords() ?? [])
                    records.Add(DescribeRecord(record));
            }
            catch (Exception ex)
            {
                details.Add(("NDEF", "Content present but could not be parsed (" + ex.Message + ")"));
            }
        }

        if (records.Count == 0 && !details.Any(d => d.Item1 is "Access" or "Card" or "Data"))
            details.Add(("Content", "No NDEF content found on this tag"));

        if (memory is { Count: > 0 })
            details.Add(("Memory", $"{memory.Count} pages readable ({memory.Count * 4} bytes)"));

        return new NfcTagInfo(ToHex(tag.GetId()), techs, details, records, memory, rawMemory, chipName);
    }

    /// <summary>Adds what the page layout reveals: Amiibo content, lock bits, password protection.</summary>
    private static void DescribeType2Layout(byte[] bytes, Type2Chip? chip, List<(string, string)> details)
    {
        if (Features.Amiibo && IsAmiiboDump(bytes))
            details.Add(("Data", "Amiibo-style content (NTAG215 layout with the Nintendo capability container)"));

        if (bytes[10] != 0 || bytes[11] != 0)
            details.Add(("Lock bits", $"Static lock bits set ({ToHex(bytes[10..12])}): some of pages 3-15 are permanently read-only"));

        if (chip is null || chip.ConfigPage < 0) return;

        if (chip.ConfigPage == chip.UserEnd + 1 && bytes.Length >= (chip.UserEnd + 1) * 4)
        {
            var dyn = bytes[(chip.UserEnd * 4)..(chip.UserEnd * 4 + 3)];
            if (dyn.Any(b => b != 0))
                details.Add(("Lock bits", $"Dynamic lock bits set ({ToHex(dyn)}): some pages from 16 up are permanently read-only"));
        }

        if (bytes.Length >= (chip.ConfigPage + 2) * 4)
        {
            int auth0 = bytes[chip.ConfigPage * 4 + 3];
            bool readProtected = (bytes[(chip.ConfigPage + 1) * 4] & 0x80) != 0;
            if (auth0 < chip.TotalPages)
                details.Add(("Password", $"Pages from {auth0} are {(readProtected ? "read- and write-" : "write-")}protected." +
                    (Features.Amiibo ? " Amiibo-style tags use a key derived from the serial, which this app knows." : "")));
        }
    }

    // ------------------------------------------------------------------------------------------------
    // ISO-DEP: identify card family from its public application directory, then try NDEF Type 4.
    // Only SELECT and READ BINARY commands on public files are used; no cardholder data is requested.
    // ------------------------------------------------------------------------------------------------

    private static readonly byte[] SelectPpse = Apdu(0x00, 0xA4, 0x04, 0x00, Encoding.ASCII.GetBytes("2PAY.SYS.DDF01"));
    private static readonly byte[] SelectPassport = Apdu(0x00, 0xA4, 0x04, 0x0C, [0xA0, 0x00, 0x00, 0x02, 0x47, 0x10, 0x01]);
    private static readonly byte[] SelectNdefApp = Apdu(0x00, 0xA4, 0x04, 0x00, [0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01]);

    private static void IdentifySmartCard(IsoDep isoDep, List<(string, string)> details, out byte[]? ndef)
    {
        ndef = null;

        var hist = isoDep.GetHistoricalBytes();
        if (hist is { Length: > 0 }) details.Add(("Historical bytes", ToHex(hist)));

        // Payment card? The PPSE lists the payment applications (brand), nothing personal.
        var ppse = isoDep.Transceive(SelectPpse);
        if (IsOk(ppse))
        {
            var brands = PaymentBrands(ppse);
            details.Add(("Card", $"Payment card{(brands.Count > 0 ? " (" + string.Join(", ", brands) + ")" : "")}"));
            details.Add(("Access", "Secure item: card number and other data are protected and not read by this app"));
            return;
        }

        // Passport or ID document (ICAO 9303)?
        var pass = isoDep.Transceive(SelectPassport);
        if (IsOk(pass))
        {
            details.Add(("Card", "Passport or ID document (ICAO 9303)"));
            details.Add(("Access", "Secure item: data is locked behind the printed document key and not read by this app"));
            return;
        }

        // NDEF Type 4 tag?
        var app = isoDep.Transceive(SelectNdefApp);
        if (IsOk(app))
        {
            ndef = ReadType4Ndef(isoDep, details);
            return;
        }

        details.Add(("Card", "Access, transit or company card (DESFire or similar)"));
        details.Add(("Access", "Secure item: content is protected and not read by this app"));
    }

    private static byte[]? ReadType4Ndef(IsoDep isoDep, List<(string, string)> details)
    {
        // Capability container: file E103
        if (!IsOk(isoDep.Transceive(Apdu(0x00, 0xA4, 0x00, 0x0C, [0xE1, 0x03])))) return null;
        var cc = isoDep.Transceive([0x00, 0xB0, 0x00, 0x00, 0x0F]);
        if (!IsOk(cc) || cc.Length < 17) return null;

        int mle = (cc[3] << 8) | cc[4];
        byte[] fileId = [cc[9], cc[10]];
        int maxSize = (cc[11] << 8) | cc[12];
        bool writable = cc[14] == 0x00;
        details.Add(("NDEF", $"Type 4 tag, {maxSize} bytes capacity, {(writable ? "writable" : "read-only")}"));

        if (!IsOk(isoDep.Transceive(Apdu(0x00, 0xA4, 0x00, 0x0C, fileId)))) return null;
        var len = isoDep.Transceive([0x00, 0xB0, 0x00, 0x00, 0x02]);
        if (!IsOk(len) || len.Length < 4) return null;
        int nlen = (len[0] << 8) | len[1];
        if (nlen == 0) return [];

        var data = new byte[nlen];
        int offset = 0;
        int chunk = Math.Clamp(mle - 2, 16, 0xF0);
        while (offset < nlen)
        {
            int want = Math.Min(chunk, nlen - offset);
            int fileOffset = offset + 2;
            var part = isoDep.Transceive([0x00, 0xB0, (byte)(fileOffset >> 8), (byte)(fileOffset & 0xFF), (byte)want]);
            if (!IsOk(part) || part.Length < 2) return null;
            Array.Copy(part, 0, data, offset, part.Length - 2);
            offset += part.Length - 2;
        }
        return data;
    }

    private static List<string> PaymentBrands(byte[] fci)
    {
        // Scan the FCI for AID entries (tag 4F) and map the registered application provider id.
        var brands = new List<string>();
        for (int i = 0; i + 1 < fci.Length - 2; i++)
        {
            if (fci[i] != 0x4F) continue;
            int len = fci[i + 1];
            if (len < 5 || i + 2 + len > fci.Length) continue;
            var rid = ToHex(fci[(i + 2)..(i + 7)]).Replace(":", "");
            var brand = rid switch
            {
                "A000000003" => "Visa",
                "A000000004" => "Mastercard",
                "A000000005" => "Maestro",
                "A000000025" => "American Express",
                "A000000065" => "JCB",
                "A000000152" => "Discover / Diners",
                "A000000324" => "Discover",
                "A000000333" => "UnionPay",
                "A000000277" => "Interac",
                "A000000121" => "Dankort",
                "A000000042" => "Cartes Bancaires",
                _ => null,
            };
            if (brand is not null && !brands.Contains(brand)) brands.Add(brand);
            i += 1 + len;
        }
        return brands;
    }

    private static byte[] Apdu(byte cla, byte ins, byte p1, byte p2, byte[] data)
    {
        var apdu = new byte[5 + data.Length + 1];
        apdu[0] = cla; apdu[1] = ins; apdu[2] = p1; apdu[3] = p2; apdu[4] = (byte)data.Length;
        Array.Copy(data, 0, apdu, 5, data.Length);
        apdu[^1] = 0x00; // Le
        return apdu;
    }

    private static bool IsOk([NotNullWhen(true)] byte[]? response) =>
        response is { Length: >= 2 } && response[^2] == 0x90 && response[^1] == 0x00;

    // ------------------------------------------------------------------------------------------------
    // NDEF in Type 2 memory: the capability container in page 3 and the TLV blocks from page 4.
    // ------------------------------------------------------------------------------------------------

    /// <summary>The link or text an NDEF-formatted Type 2 memory image carries, or null. Used to label saved
    /// dumps; it never touches a tag.</summary>
    public static string? ContentFromMemory(byte[] memory)
    {
        try
        {
            if (memory.Length < 16) return null;
            var ndefBytes = ExtractType2Ndef(memory, []);
            if (ndefBytes is null || ndefBytes.Length == 0) return null;
            var message = new NdefMessage(ndefBytes);
            var records = message.GetRecords()?.Select(DescribeRecord).Where(r => !r.IsSealed).ToList() ?? [];
            return records.FirstOrDefault(r => r.Kind.StartsWith("Link"))?.Text
                ?? records.FirstOrDefault(r => r.Kind.StartsWith("Text"))?.Text
                ?? records.FirstOrDefault(r => r.Text.Length > 0)?.Text;
        }
        catch
        {
            return null;   // malformed NDEF: not readable content
        }
    }

    private static byte[]? ExtractType2Ndef(byte[] memory, List<(string, string)> details)
    {
        // Capability container lives in page 3
        if (memory[12] != 0xE1)
        {
            if (!IsAmiiboDump(memory))
                details.Add(("Chip", "Type 2 tag without NDEF formatting"));
            return null;
        }
        int dataBytes = memory[14] * 8;
        bool writable = (memory[15] & 0x0F) == 0;
        details.Add(("NDEF", $"Type 2 tag (NTAG/Ultralight), {dataBytes} bytes capacity, {(writable ? "writable" : "read-only")}"));

        int end = Math.Min(memory.Length, 16 + dataBytes);
        var buffer = new List<byte>(memory[16..end]);
        TryExtractNdefTlv(buffer, out var result, out _);
        return result;
    }

    // ------------------------------------------------------------------------------------------------
    // NDEF record decoding
    // ------------------------------------------------------------------------------------------------

    private static NfcRecordInfo DescribeRecord(NdefRecord record)
    {
        var payload = record.GetPayload() ?? [];
        var type = record.GetTypeInfo() ?? [];

        if (record.Tnf == NdefRecord.TnfWellKnown)
        {
            if (type.SequenceEqual(NdefRecord.RtdText ?? []))
            {
                if (payload.Length == 0) return new("Text", "");
                int status = payload[0];
                bool utf16 = (status & 0x80) != 0;
                int langLength = status & 0x3F;
                var lang = Encoding.ASCII.GetString(payload, 1, Math.Min(langLength, payload.Length - 1));
                var text = (utf16 ? Encoding.BigEndianUnicode : Encoding.UTF8)
                    .GetString(payload, 1 + langLength, payload.Length - 1 - langLength);
                return new($"Text ({lang})", text);
            }

            var uri = record.ToUri();
            if (uri is not null) return new("Link", uri.ToString()!);

            if (type.SequenceEqual(NdefRecord.RtdSmartPoster ?? []))
                return new("Smart poster", Encoding.UTF8.GetString(payload));
        }

        if (record.Tnf == NdefRecord.TnfAbsoluteUri)
            return new("Link", Encoding.UTF8.GetString(payload));

        if (record.Tnf == NdefRecord.TnfMimeMedia)
        {
            var mime = record.ToMimeType() ?? Encoding.ASCII.GetString(type);
            if (mime == SealedText.MimeType && SealedText.LooksSealed(payload))
                return new("Private text", "Encrypted with a passphrase", payload);
            var text = mime.StartsWith("text/") || mime.Contains("json") || mime.Contains("xml") || mime.Contains("vcard")
                ? Encoding.UTF8.GetString(payload)
                : $"{payload.Length} bytes of binary data";
            return new($"MIME {mime}", text);
        }

        if (record.Tnf == NdefRecord.TnfExternalType)
            return new($"External {Encoding.ASCII.GetString(type)}", TryText(payload));

        if (record.Tnf == NdefRecord.TnfEmpty)
            return new("Empty record", "");

        return new($"Record (TNF {record.Tnf})", TryText(payload));
    }
#endif
}
