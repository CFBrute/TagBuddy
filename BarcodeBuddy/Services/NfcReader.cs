using System.Text;

#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

namespace BarcodeBuddy.Services;

/// <summary>Reads NFC tags (13.56 MHz, the kind a phone reacts to) while a page is in the foreground.</summary>
public sealed class NfcReader
{
    public static NfcReader Instance { get; } = new();

    public event Action<NfcTagInfo>? TagRead;

    /// <summary>Success flag and a message, raised after an attempt to write a tag held to the phone.</summary>
    public event Action<bool, string>? TagWritten;

#if ANDROID
    private ReaderCallback? _callback;

    /// <summary>When set, the next tag held to the phone is written with this message instead of being read.</summary>
    private NdefMessage? _pendingWrite;

    public void ArmWrite(NdefMessage? message) => _pendingWrite = message;
    public bool IsArmedToWrite => _pendingWrite is not null;

    private static NfcAdapter? Adapter =>
        NfcAdapter.GetDefaultAdapter(Platform.AppContext);

    public bool IsSupported => Adapter is not null;
    public bool IsEnabled => Adapter?.IsEnabled == true;

    public void Start()
    {
        var adapter = Adapter;
        var activity = Platform.CurrentActivity;
        if (adapter is null || activity is null || _callback is not null) return;

        _callback = new ReaderCallback(this);
        var extras = new Android.OS.Bundle();
        extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 250);

        // SkipNdefCheck: Android's own NDEF probe makes payment and access cards drop before the app
        // ever sees them. The app reads NDEF itself instead (Type 2 and Type 4 tags).
        adapter.EnableReaderMode(activity, _callback,
            NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.NfcF | NfcReaderFlags.NfcV |
            NfcReaderFlags.NfcBarcode | NfcReaderFlags.SkipNdefCheck,
            extras);
    }

    public void Stop()
    {
        var adapter = Adapter;
        var activity = Platform.CurrentActivity;
        if (adapter is not null && activity is not null && _callback is not null)
            adapter.DisableReaderMode(activity);
        _callback = null;
    }

    public void OpenSettings()
    {
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionNfcSettings);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.AppContext.StartActivity(intent);
    }

    private void OnTag(Tag tag)
    {
        if (_pendingWrite is { } message)
        {
            _pendingWrite = null;
            var (ok, note) = WriteTag(tag, message);
            MainThread.BeginInvokeOnMainThread(() => TagWritten?.Invoke(ok, note));
            return;
        }

        NfcTagInfo info;
        try
        {
            info = Describe(tag);
        }
        catch (Exception ex)
        {
            info = new NfcTagInfo(ToHex(tag.GetId()), tag.GetTechList()?.ToList() ?? [],
                [("Error", ex.Message)], []);
        }
        MainThread.BeginInvokeOnMainThread(() => TagRead?.Invoke(info));
    }

    /// <summary>Writes an NDEF message to a writable tag, or formats a blank one first. Nothing else about the
    /// tag is changed: its serial number is fixed and is never touched.</summary>
    private static (bool Ok, string Note) WriteTag(Tag tag, NdefMessage message)
    {
        int size = message.ToByteArray().Length;
        try
        {
            using var ndef = Ndef.Get(tag);
            if (ndef is not null)
            {
                ndef.Connect();
                try
                {
                    if (!ndef.IsWritable)
                        return (false, "This tag is read-only and cannot be changed.");
                    if (ndef.MaxSize < size)
                        return (false, $"The content needs {size} bytes but this tag holds only {ndef.MaxSize}.");
                    ndef.WriteNdefMessage(message);
                    return (true, $"Written to the tag ({size} bytes).");
                }
                finally { try { ndef.Close(); } catch { } }
            }

            using var formatable = NdefFormatable.Get(tag);
            if (formatable is not null)
            {
                formatable.Connect();
                try
                {
                    formatable.Format(message);
                    return (true, $"Blank tag formatted and written ({size} bytes).");
                }
                finally { try { formatable.Close(); } catch { } }
            }

            return (false, "This tag cannot hold NDEF content (it may be a locked card).");
        }
        catch (Exception ex)
        {
            return (false, "Write failed, hold the tag still and try again. (" + ex.Message + ")");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Building NDEF messages to write
    // ------------------------------------------------------------------------------------------------

    public static NdefMessage BuildUri(string uri) =>
        new([NdefRecord.CreateUri(uri)]);

    public static NdefMessage BuildText(string text, string language = "en") =>
        new([NdefRecord.CreateTextRecord(language, text)]);

    public static NdefMessage BuildVCard(string vcard) =>
        new([NdefRecord.CreateMime("text/vcard", Encoding.UTF8.GetBytes(vcard))]);

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

        // Type 2 tags (NTAG / Ultralight stickers): read NDEF through plain READ commands.
        if (ndefBytes is null && !isIsoDep && !isClassic)
        {
            using var nfcA = NfcA.Get(tag);
            if (nfcA is not null)
            {
                try
                {
                    nfcA.Connect();
                    ndefBytes = ReadType2Ndef(nfcA, details);
                    memory = DumpType2Memory(nfcA);
                }
                catch (Exception ex)
                {
                    details.Add(("Note", "Could not read the tag's memory (" + ex.Message + ")"));
                }
                finally { try { nfcA.Close(); } catch { } }
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

        if (records.Count == 0 && !details.Any(d => d.Item1 is "Access" or "Card"))
            details.Add(("Content", "No NDEF content found on this tag"));

        if (memory is { Count: > 0 })
            details.Add(("Memory", $"{memory.Count} pages readable ({memory.Count * 4} bytes)"));

        return new NfcTagInfo(ToHex(tag.GetId()), techs, details, records, memory);
    }

    /// <summary>Dumps every readable page of a Type 2 tag as "page: hex  ascii". These pages carry no keys,
    /// so this is the full openly-readable memory of the tag.</summary>
    private static List<string>? DumpType2Memory(NfcA nfcA)
    {
        var lines = new List<string>();
        int page = 0;
        byte[]? previous = null;

        while (page < 256)
        {
            byte[] chunk;
            try { chunk = nfcA.Transceive([0x30, (byte)page]); }
            catch { break; }                              // past the end of memory: the tag NAKs the READ
            if (chunk is null || chunk.Length < 16) break;

            // READ returns 4 pages at a time and wraps around at the end; stop once it repeats.
            if (previous is not null && chunk.AsSpan(0, 16).SequenceEqual(previous)) break;
            previous = chunk[..16];

            for (int i = 0; i < 4; i++)
            {
                var row = chunk[(i * 4)..(i * 4 + 4)];
                lines.Add($"{page + i:D3}: {ToHex(row)}  {Ascii(row)}");
            }
            page += 4;
        }
        return lines.Count > 0 ? lines : null;
    }

    private static string Ascii(byte[] bytes) =>
        new(bytes.Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());

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

    // ------------------------------------------------------------------------------------------------
    // Type 2 (NTAG / Ultralight): READ (0x30) returns 16 bytes = 4 pages of 4 bytes.
    // ------------------------------------------------------------------------------------------------

    private static byte[]? ReadType2Ndef(NfcA nfcA, List<(string, string)> details)
    {
        var first = nfcA.Transceive([0x30, 0x00]);
        if (first is null || first.Length < 16) return null;

        // Capability container lives in page 3
        if (first[12] != 0xE1)
        {
            details.Add(("Chip", "Type 2 tag without NDEF formatting"));
            return null;
        }
        int dataBytes = first[14] * 8;
        bool writable = (first[15] & 0x0F) == 0;
        details.Add(("NDEF", $"Type 2 tag (NTAG/Ultralight), {dataBytes} bytes capacity, {(writable ? "writable" : "read-only")}"));

        // Read the data area page by page (4 pages per READ) until the NDEF TLV is complete.
        var buffer = new List<byte>();
        int page = 4;
        int lastPage = 4 + dataBytes / 4;
        while (page < lastPage)
        {
            var chunk = nfcA.Transceive([0x30, (byte)page]);
            if (chunk is null || chunk.Length < 16) break;
            buffer.AddRange(chunk);
            page += 4;

            // Stop early once the NDEF TLV is fully present.
            if (TryExtractNdefTlv(buffer, out var ndef, out var complete) && complete)
                return ndef;
        }

        TryExtractNdefTlv(buffer, out var result, out _);
        return result;
    }

    /// <summary>Walks the TLV blocks of a Type 2 data area and returns the NDEF message (type 0x03).</summary>
    private static bool TryExtractNdefTlv(List<byte> data, out byte[]? ndef, out bool complete)
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

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private static byte[] Apdu(byte cla, byte ins, byte p1, byte p2, byte[] data)
    {
        var apdu = new byte[5 + data.Length + 1];
        apdu[0] = cla; apdu[1] = ins; apdu[2] = p1; apdu[3] = p2; apdu[4] = (byte)data.Length;
        Array.Copy(data, 0, apdu, 5, data.Length);
        apdu[^1] = 0x00; // Le
        return apdu;
    }

    private static bool IsOk(byte[]? response) =>
        response is { Length: >= 2 } && response[^2] == 0x90 && response[^1] == 0x00;

    private static string TryText(byte[] payload)
    {
        if (payload.Length == 0) return "";
        var text = Encoding.UTF8.GetString(payload);
        return text.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)) ? text : ToHex(payload);
    }

    private static string ToHex(byte[]? bytes) =>
        bytes is null ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));

    private sealed class ReaderCallback(NfcReader owner) : Java.Lang.Object, NfcAdapter.IReaderCallback
    {
        public void OnTagDiscovered(Tag? tag)
        {
            if (tag is not null) owner.OnTag(tag);
        }
    }
#else
    public bool IsSupported => false;
    public bool IsEnabled => false;
    public void Start() { }
    public void Stop() { }
    public void OpenSettings() { }
#endif
}
