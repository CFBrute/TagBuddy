using System.Text;

#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

namespace TagBuddy.Services;

/// <summary>Reads NFC tags (13.56 MHz, the kind a phone reacts to) while a page is in the foreground.</summary>
///
/// <remarks>
/// <para><b>Type 2 tag memory (NTAG213/215/216, Ultralight EV1), which the write routines below depend on.</b>
/// Memory is 4-byte pages. Pages 0-1 and the first byte of page 2 hold the 7-byte serial (UID), burned at the
/// factory and never writable. Page 2 bytes 2-3 are the <i>static lock bits</i>. Page 3 is the <i>capability
/// container</i> (CC). Page 4 up to <c>UserEnd</c> is user memory. After it come the <i>dynamic lock bits</i>,
/// then the configuration pages CFG0 (holds AUTH0, the first password-protected page), CFG1 (AUTHLIM, the
/// wrong-password limit, and PROT), PWD and PACK.</para>
///
/// <para><b>Locks are one-time and never set by writing data.</b> Each lock bit starts clear and can be set
/// once; it can never be cleared, and the pages it covers are read-only for the life of the tag. Writing a
/// page's data does nothing to any lock bit, so a tag rewritten a hundred times with text stays unlocked.
/// This app never sets a lock bit: the dump writer skips page 2 and the dynamic lock page on purpose. Lock
/// bits only appear because another writer put them there, typically an Amiibo tool copying Nintendo's
/// image, whose lock bytes (0F E0 static, 01 dynamic) lock pages 3, 13-15 and 16-31. <see cref="LockedPages"/>
/// decodes both sets, and the writers leave locked pages alone and say so in their result.</para>
///
/// <para><b>Page 3 is one-time on its own.</b> The chip treats the CC as OTP: bits can be set, never cleared.
/// A blank tag has 00 00 00 00; NDEF formatting writes E1 10 xx 00 and an Amiibo image writes F1 10 FF EE.
/// Once a tag has had the Amiibo value, no phone will ever read it as a text or link tag again, so a tag's
/// first write decides what it is for. The raw-bytes writer exists for exactly that case: it stores text in
/// the user pages with no NDEF wrapper, which this app reads back but phones ignore.</para>
///
/// <para><b>Passwords.</b> An Amiibo tag is password-protected (AUTH0 = 4) with a key that is a fixed formula
/// of its serial (<see cref="AmiiboPassword"/>), so the app can always open one. Other protected tags are left
/// alone: a wrong guess counts against AUTHLIM, after which the tag locks for good. A failed handshake looks
/// the same for a wrong key and for a tag that moved during it, which is why it is retried once.</para>
/// </remarks>
public sealed class NfcReader
{
    public static NfcReader Instance { get; } = new();

    public event Action<NfcTagInfo>? TagRead;

    /// <summary>Success flag and a message, raised after an attempt to write a tag held to the phone.</summary>
    public event Action<bool, string>? TagWritten;

#if ANDROID
    private ReaderCallback? _callback;

    /// <summary>When set, the next tag held to the phone is handed to this operation instead of being read.</summary>
    private Func<Tag, (bool Ok, string Note)>? _pendingWrite;

    /// <summary>Arms writing an NDEF message (link, text, vCard) to the next tag; null disarms.</summary>
    public void ArmWrite(NdefMessage? message) =>
        _pendingWrite = message is null ? null : tag => WriteNdef(tag, message);

    /// <summary>Arms writing a raw page dump (Amiibo, NTAG backup) to the next Type 2 tag.
    /// <paramref name="dumpSerial"/> is the serial the dump was taken from, used only for the result note.</summary>
    public void ArmRawWrite(byte[] dump, string dumpSerial) =>
        _pendingWrite = tag => WriteRaw(tag, dump, dumpSerial);

    /// <summary>Arms writing bytes straight into the next Type 2 tag's user pages, with no NDEF wrapper: the
    /// rest of the user area is zeroed, the serial and page 3 are left as they are. Phones will not read such
    /// a tag as text, but this app shows the bytes as readable text, and it works on tags whose page 3 was
    /// burned for Amiibo use, where NDEF never can.</summary>
    public void ArmRawText(byte[] payload) =>
        _pendingWrite = tag => WriteRaw(tag, (before, chip) =>
        {
            // The text goes into the first stretch of unlocked pages that holds it in one piece, so it
            // reads back as one run; locked pages keep what they have, every other user page is zeroed.
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
        }, "");

    /// <summary>Arms erasing the next tag: user pages are zeroed, an empty NDEF message is left, and any
    /// password protection this app can open is removed.</summary>
    public void ArmErase() => _pendingWrite = EraseTag;

    public void Disarm() => _pendingWrite = null;
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
        if (_pendingWrite is { } operation)
        {
            _pendingWrite = null;
            bool ok;
            string note;
            try { (ok, note) = operation(tag); }
            catch (Exception ex)
            {
                ok = false;
                note = "Write failed, hold the tag still and try again. (" + ex.Message + ")";
            }
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
    private static (bool Ok, string Note) WriteNdef(Tag tag, NdefMessage message)
    {
        var bytes = message.ToByteArray();
        int size = bytes.Length;

        // Type 2 tags (NTAG, Ultralight): write the TLV through plain page writes. Android's own Ndef tech is
        // not offered in the reader mode this app uses, and this path also opens password-protected tags.
        if (IsType2(tag)) return WriteType2Ndef(tag, bytes);

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

    public static NdefMessage BuildEmpty() =>
        new([new NdefRecord(NdefRecord.TnfEmpty, [], [], [])]);

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
                        && LooksAmiibo(rawMemory) && TryAuthenticate(link, AmiiboPassword(link.Uid)))
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

    /// <summary>Formats page memory as "page: hex  ascii" lines, one per 4-byte page.</summary>
    public static List<string>? FormatDump(byte[] bytes)
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

    /// <summary>Adds what the page layout reveals: Amiibo content, lock bits, password protection.</summary>
    private static void DescribeType2Layout(byte[] bytes, Type2Chip? chip, List<(string, string)> details)
    {
        if (IsAmiiboDump(bytes))
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
                details.Add(("Password", $"Pages from {auth0} are {(readProtected ? "read- and write-" : "write-")}protected. " +
                    "Amiibo-style tags use a key derived from the serial, which this app knows."));
        }
    }

    /// <summary>A Type 2 dump with the Nintendo capability container and NTAG215-sized content.</summary>
    public static bool IsAmiiboDump(byte[] bytes) =>
        bytes.Length >= 520 && LooksAmiibo(bytes);

    /// <summary>The Nintendo capability container in page 3; enough to decide that the Amiibo key may be tried,
    /// even when the rest of the tag could not be read.</summary>
    private static bool LooksAmiibo(byte[] bytes) =>
        bytes.Length >= 16 && bytes[12] == 0xF1 && bytes[13] == 0x10 && bytes[14] == 0xFF && bytes[15] == 0xEE;

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
    // Type 2 (NTAG / Ultralight): READ (0x30) returns 16 bytes = 4 pages of 4 bytes, WRITE (0xA2) one page.
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
            var records = message.GetRecords()?.Select(DescribeRecord).ToList() ?? [];
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

    /// <summary>PWD_AUTH. True when the tag answered with a PACK; a wrong password drops the connection,
    /// which is restored here.</summary>
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

    /// <summary>"13-15, 16-31" from a list of page numbers.</summary>
    private static string Ranges(List<int> pages)
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

    private static string Pluralise(int n, string noun) => n == 1 ? noun[..1].ToUpperInvariant() + noun[1..] : $"{char.ToUpperInvariant(noun[0])}{noun[1..]}s";

    /// <summary>The password to use for a tag: the Nintendo key when the tag (or the dump going onto it) is
    /// Amiibo-style, otherwise none. Guessing on other tags would count against their failed-attempt limit.</summary>
    private static byte[]? PasswordFor(Type2Link link, byte[] tagMemory, bool amiiboDump = false) =>
        amiiboDump || LooksAmiibo(tagMemory) ? AmiiboPassword(link.Uid) : null;

    /// <summary>Opens the password session when the tag protects pages this operation touches. Returns a
    /// message when the operation cannot proceed.</summary>
    private static string? OpenProtection(Type2Link link, Type2Chip? chip, int lastPageTouched, byte[]? pwd, ref bool authed)
    {
        int auth0 = ReadAuth0(link, chip);
        if (auth0 < 0 || auth0 >= lastPageTouched) return null;
        if (pwd is null)
            return $"This tag is password-protected from page {auth0} and is not Amiibo-style, so this app will not guess " +
                   "its password. Nothing was changed.";
        authed = TryAuthenticate(link, pwd);
        if (authed) return null;

        // The handshake fails the same way for a wrong key and for a tag that moved during it, and the
        // Amiibo key is a fixed formula of the serial, so a slip of the hand is the likelier cause.
        int limit = ReadAuthLimit(link, chip);
        var limitNote = limit > 0
            ? $" This tag locks for good after {limit} wrong attempts, so do not keep retrying if it fails again."
            : "";
        return "This tag is password-protected and did not accept the Amiibo key, or it moved away during the " +
               "handshake. Hold it still and try again. Nothing was changed." + limitNote;
    }

    /// <summary>Writes an NDEF TLV into the user pages of a Type 2 tag, formatting a blank one first.</summary>
    private static (bool Ok, string Note) WriteType2Ndef(Tag tag, byte[] ndef)
    {
        using var link = Type2Link.Open(tag);
        if (link is null) return (false, "This tag cannot be written page by page.");

        // TLV: 03, length (1 or 3 bytes), message, FE terminator, padded to whole pages.
        var tlv = new List<byte> { 0x03 };
        if (ndef.Length < 0xFF) tlv.Add((byte)ndef.Length);
        else tlv.AddRange([0xFF, (byte)(ndef.Length >> 8), (byte)(ndef.Length & 0xFF)]);
        tlv.AddRange(ndef);
        tlv.Add(0xFE);
        while (tlv.Count % 4 != 0) tlv.Add(0x00);

        link.Connect();
        var chip = GetVersion(link);
        var before = ReadMemory(link, chip);
        if (before.Length < 16) return (false, "Could not read the tag. Hold it still and try again.");

        int userEnd = chip is { UserEnd: > 0 } ? chip.UserEnd : before.Length / 4;
        var pwd = PasswordFor(link, before);
        bool authed = false;
        if (OpenProtection(link, chip, userEnd, pwd, ref authed) is { } blocked) return (false, blocked);

        var notes = new List<string>();
        var cc = before[12..16];
        if (cc.All(b => b == 0))
        {
            // Blank, never formatted: write the capability container (one-time programmable page).
            byte[] newCc = [0xE1, 0x10, (byte)Math.Min(0xFF, (userEnd - 4) * 4 / 8), 0x00];
            if (!WritePageWithRetry(link, 3, newCc, pwd, ref authed, out var ccError))
                return (false, "Could not format the blank tag (" + ccError + ").");
            cc = newCc;
            notes.Add("Blank tag formatted for NDEF.");
        }
        else if (cc[0] != 0xE1)
        {
            return (false, IsAmiiboDump(before)
                ? "This tag carries Amiibo data and its capability container (page 3) is one-time programmable, so phones " +
                  "will never read it as a text or link tag. It can still be erased or loaded with another binary dump."
                : $"This tag is not NDEF-formatted (capability container {ToHex(cc)}) and cannot be formatted, so text and links cannot be written to it.");
        }
        else if ((cc[3] & 0x0F) != 0)
        {
            return (false, "This tag is marked read-only in its capability container and cannot be changed.");
        }

        int capacity = Math.Min(cc[2] * 8, (userEnd - 4) * 4);
        if (tlv.Count > capacity)
            return (false, $"The content needs {tlv.Count} bytes but this tag holds only {capacity}.");

        int written = 0;
        var data = tlv.ToArray();
        for (int i = 0; i < data.Length; i += 4)
        {
            int page = 4 + i / 4;
            var pageData = data[i..(i + 4)];
            if (SamePage(before, page, pageData)) continue;
            if (!WritePageWithRetry(link, page, pageData, pwd, ref authed, out var error))
                return (false, $"Write failed at page {page} ({error}). The tag may be locked, or it moved away.");
            written++;
        }

        var after = ReadMemory(link, chip);
        for (int i = 0; i < data.Length; i += 4)
            if (!SamePage(after, 4 + i / 4, data[i..(i + 4)]))
                return (false, "The tag did not keep the new content. It may be locked; hold it still and try again.");

        return (true, $"Written to the tag ({ndef.Length} bytes, {written} pages changed). {string.Join(" ", notes)}".TrimEnd());
    }

    /// <summary>Writes a page dump back to a Type 2 tag: page 3 and every user page, never the serial
    /// (pages 0-2) and never the permanent lock bits. An Amiibo dump also gets the Nintendo password set
    /// so a console can write to it; that password is derived from this tag's serial.</summary>
    private static (bool Ok, string Note) WriteRaw(Tag tag, byte[] dump, string dumpSerial) =>
        WriteRaw(tag, (_, _) => (dump, null), dumpSerial);

    /// <summary>The dump to write is built once the tag is known (its chip and current memory), so a text
    /// payload can be sized to the tag and keep its page 3. The builder returns the dump, or an error.</summary>
    private static (bool Ok, string Note) WriteRaw(Tag tag, Func<byte[], Type2Chip, (byte[]? Dump, string? Error)> build, string dumpSerial)
    {
        using var link = Type2Link.Open(tag);
        if (link is null) return (false, "Binary dumps can only be written to Type 2 tags (NTAG, Ultralight, Amiibo).");

        link.Connect();
        var chip = GetVersion(link);
        if (chip is not { UserEnd: > 0 })
            return (false, $"This chip could not be identified{(chip is null ? "" : " (" + chip.Name + ")")}, so the app cannot tell " +
                           "its user pages from its lock and configuration pages. Dumps are written to NTAG213/215/216 and Ultralight EV1 only.");

        var before = ReadMemory(link, chip);
        if (before.Length < 16) return (false, "Could not read the tag. Hold it still and try again.");

        var (dump, buildError) = build(before, chip);
        if (dump is null) return (false, buildError ?? "Nothing to write.");
        if (dump.Length < 16 || dump.Length % 4 != 0)
            return (false, $"The dump is {dump.Length} bytes; a tag dump is a whole number of 4-byte pages, at least 16 bytes.");

        // Only the user area is written; a dump may carry more (an Amiibo .bin often ends with a signature).
        int dumpPages = dump.Length / 4;
        int endPage = Math.Min(dumpPages, chip.UserEnd);
        bool amiibo = IsAmiiboDump(dump);
        if (amiibo && chip.UserEnd < 130)
            return (false, $"An Amiibo dump needs 130 pages of user memory (NTAG215); this {chip.Name} has {chip.UserEnd - 4}.");

        var pwd = PasswordFor(link, before, amiibo);
        bool authed = false;
        if (OpenProtection(link, chip, endPage, pwd, ref authed) is { } blocked) return (false, blocked);

        var notes = new List<string>();
        var oldCc = before[12..16];
        var newCc = dump[12..16];
        // A dump with an all-zero page 3 (made from text, not read from a tag) means "leave page 3 alone":
        // it is one-time programmable, so zeros could never be written there anyway.
        bool keepCc = newCc.All(b => b == 0);
        if (!keepCc && oldCc.Zip(newCc).Any(p => (p.First | p.Second) != p.Second))
            notes.Add($"Page 3 is one-time programmable and keeps its old bits ({ToHex(oldCc)} cannot become {ToHex(newCc)}).");

        // Pages the lock bits mark as permanently read-only are left alone rather than failing the write:
        // an Amiibo writer locks a handful of them, and a tag can still take text around them.
        var locked = LockedPages(before, chip);
        var lockedPages = new List<int>();

        int written = 0, skipped = 0;
        for (int page = 3; page < endPage; page++)
        {
            var data = dump[(page * 4)..(page * 4 + 4)];
            if (page == 3 && keepCc) { skipped++; continue; }
            if (SamePage(before, page, data)) { skipped++; continue; }
            if (locked[page]) { lockedPages.Add(page); continue; }
            if (!WritePageWithRetry(link, page, data, pwd, ref authed, out var error))
            {
                // Refused but still answering: a lock the lock bits did not reveal. Gone: it moved away.
                if (link.Read(page) is not null) { lockedPages.Add(page); locked[page] = true; continue; }
                return (false, $"Write failed at page {page} ({error}) after {written} pages: the tag moved away. Hold it still and try again.");
            }
            written++;
        }
        if (lockedPages.Count > 0)
            notes.Add($"{Pluralise(lockedPages.Count, "page")} {Ranges(lockedPages)} {(lockedPages.Count == 1 ? "is" : "are")} permanently locked and kept {(lockedPages.Count == 1 ? "its" : "their")} old content.");

        if (amiibo && pwd is not null && chip is { HasPassword: true, ConfigPage: > 0 })
            notes.Add(SetAmiiboPassword(link, chip, pwd, before, ref authed));

        var after = ReadMemory(link, chip);
        int mismatched = 0;
        for (int page = 4; page < endPage; page++)
            if (!locked[page] && !SamePage(after, page, dump[(page * 4)..(page * 4 + 4)])) mismatched++;

        var serial = ToHex(link.Uid);
        if (dumpSerial.Length > 0 && !string.Equals(dumpSerial, serial, StringComparison.OrdinalIgnoreCase))
            notes.Add($"The dump came from tag {dumpSerial} and this tag is {serial}; Amiibo data is bound to its original serial, so a console may reject it.");

        if (mismatched > 0)
            return (false, $"{written} pages written, but {mismatched} read back differently (locked pages?). {string.Join(" ", notes)}".TrimEnd());

        return (true, $"Dump written: {written} pages changed, {skipped} already matched. {string.Join(" ", notes)}".TrimEnd());
    }

    /// <summary>Sets the Nintendo password on an NTAG so a console can authenticate: PWD, PACK 80:80,
    /// CFG1 with plain access, CFG0 with AUTH0 = 4 (writes need the password, reads stay open).
    /// The permanent lock bits are left alone, so this app can rewrite the tag later.</summary>
    private static string SetAmiiboPassword(Type2Link link, Type2Chip chip, byte[] pwd, byte[] before, ref bool authed)
    {
        int cfg0 = chip.ConfigPage;
        var oldCfg0 = before.Length >= (cfg0 + 1) * 4 ? before[(cfg0 * 4)..(cfg0 * 4 + 4)] : new byte[4];
        var oldCfg1 = before.Length >= (cfg0 + 2) * 4 ? before[((cfg0 + 1) * 4)..((cfg0 + 1) * 4 + 4)] : new byte[4];

        // PWD and PACK can always be written (the pages read back as zeros, so they are never "already set").
        if (!WritePageWithRetry(link, cfg0 + 2, pwd, pwd, ref authed, out var pwdError))
            return $"The data is in place but the Amiibo password could not be written (page {cfg0 + 2}: {pwdError}).";
        if (!WritePageWithRetry(link, cfg0 + 3, [0x80, 0x80, 0x00, 0x00], pwd, ref authed, out var packError))
            return $"The data is in place and the password written, but its PACK could not be set (page {cfg0 + 3}: {packError}).";

        // Configuration: writes need the password from page 4 on, reads stay open. Skip what is already so,
        // and accept a configuration that the original writer locked (CFGLCK), as Amiibo tools usually do.
        bool protectedFromStart = oldCfg0[3] <= 4;
        bool readsOpen = (oldCfg1[0] & 0x80) == 0;
        bool configLocked = (oldCfg1[0] & 0x40) != 0;

        if (protectedFromStart && readsOpen)
            return "Amiibo password set from this tag's serial. The tag was already configured for it" +
                   (configLocked ? " (configuration locked by the original writer)." : ".");

        if (configLocked)
            return "Amiibo password set, but the tag's configuration is permanently locked by its original writer, so " +
                   (protectedFromStart ? "its read protection stays as it is." : "the password cannot be made to apply from page 4.");

        if (!WritePageWithRetry(link, cfg0 + 1, [0x00, 0x00, 0x00, 0x00], pwd, ref authed, out var cfg1Error))
            return $"Amiibo password set, but its configuration could not be written (page {cfg0 + 1}: {cfg1Error}).";
        if (!WritePageWithRetry(link, cfg0, [oldCfg0[0], oldCfg0[1], oldCfg0[2], 0x04], pwd, ref authed, out var cfg0Error))
            return $"Amiibo password set, but its configuration could not be written (page {cfg0}: {cfg0Error}).";

        authed = true;
        return "Amiibo password set from this tag's serial; the lock bits were not set, so the tag stays rewritable.";
    }

    /// <summary>Zeroes the user pages, leaves an empty NDEF TLV, and lifts password protection when the
    /// Amiibo key opens it. Page 3 is one-time programmable and stays as it is.</summary>
    private static (bool Ok, string Note) EraseTag(Tag tag)
    {
        if (!IsType2(tag))
        {
            var (ok, note) = WriteNdef(tag, BuildEmpty());
            return (ok, ok ? "Content cleared (empty NDEF message written)." : note);
        }

        using var link = Type2Link.Open(tag);
        if (link is null) return (false, "This tag cannot be erased page by page.");

        link.Connect();
        var chip = GetVersion(link);
        if (chip is not { UserEnd: > 0 })
            return (false, $"This chip could not be identified{(chip is null ? "" : " (" + chip.Name + ")")}, so the app cannot tell " +
                           "its user pages from its lock and configuration pages. Erase works on NTAG213/215/216 and Ultralight EV1 only.");

        var before = ReadMemory(link, chip);
        if (before.Length < 16) return (false, "Could not read the tag. Hold it still and try again.");

        int userEnd = chip.UserEnd;
        var pwd = PasswordFor(link, before);
        bool authed = false;
        int auth0 = ReadAuth0(link, chip);
        if (OpenProtection(link, chip, userEnd, pwd, ref authed) is { } blocked) return (false, blocked);

        int written = 0;
        // Permanently locked pages (set by an Amiibo writer, say) cannot be cleared; they are left alone.
        var locked = LockedPages(before, chip);
        var lockedPages = new List<int>();
        for (int page = 4; page < userEnd; page++)
        {
            byte[] data = page == 4 ? [0x03, 0x00, 0xFE, 0x00] : [0x00, 0x00, 0x00, 0x00];
            if (SamePage(before, page, data)) continue;
            if (locked[page]) { lockedPages.Add(page); continue; }
            if (!WritePageWithRetry(link, page, data, pwd, ref authed, out var error))
            {
                if (link.Read(page) is not null) { lockedPages.Add(page); continue; }
                return (false, $"Erase stopped at page {page} ({error}) after clearing {written} pages: the tag moved away. Hold it still and try again.");
            }
            written++;
        }

        var notes = new List<string>();
        if (lockedPages.Count > 0)
            notes.Add($"{Pluralise(lockedPages.Count, "page")} {Ranges(lockedPages)} {(lockedPages.Count == 1 ? "is" : "are")} permanently locked and could not be cleared.");
        if (chip is { HasPassword: true, ConfigPage: > 0 } && auth0 >= 0 && auth0 < chip.TotalPages && authed)
        {
            // Switch protection off (AUTH0 = FF) first; only then reset the password. The other way round, a
            // failed configuration write would leave the tag locked behind a password nobody knows.
            int cfg0 = chip.ConfigPage;
            var cfgRead = link.Read(cfg0);
            var oldCfg0 = cfgRead is not null ? cfgRead[..4] : new byte[] { 0x00, 0x00, 0x00, 0xFF };
            bool lifted = WritePageWithRetry(link, cfg0, [oldCfg0[0], oldCfg0[1], oldCfg0[2], 0xFF], pwd, ref authed, out _);
            if (lifted)
            {
                WritePageWithRetry(link, cfg0 + 2, [0xFF, 0xFF, 0xFF, 0xFF], pwd, ref authed, out _);
                WritePageWithRetry(link, cfg0 + 3, [0x00, 0x00, 0x00, 0x00], pwd, ref authed, out _);
            }
            notes.Add(lifted
                ? "Password protection removed."
                : "Password protection stays (its configuration is locked); the Amiibo key still opens it for this app.");
        }

        if (before[12] == 0xF1)
            notes.Add("Page 3 keeps the Amiibo capability container (one-time programmable), so phones will not treat this " +
                      "tag as a text or link tag; it can still hold another binary dump.");

        return (true, $"Tag erased: {written} pages cleared. {string.Join(" ", notes)}".TrimEnd());
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
    public bool IsArmedToWrite => false;
    public void Start() { }
    public void Stop() { }
    public void OpenSettings() { }
    public void ArmRawWrite(byte[] dump, string dumpSerial) { }
    public void ArmRawText(byte[] payload) { }
    public void ArmErase() { }
    public void Disarm() { }
    public static bool IsAmiiboDump(byte[] bytes) => false;
#endif
}
