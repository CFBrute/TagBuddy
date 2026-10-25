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
        // GetTechList can list the same tech twice on some phones; show each once.
        var techs = (tag.GetTechList() ?? [])
            .Select(t => t.Split('.').Last())
            .Distinct()
            .ToList();

        var details = new List<(string, string)>();
        var records = new List<NfcRecordInfo>();
        List<string>? memory = null;
        byte[]? rawMemory = null;
        bool needsPassword = false;
        bool needsClassicKey = false;
        string? chipName = null;
        byte[]? ndefBytes = null;
        int? sak = null;
        SegmentRead? segments = null;

        using (var nfcA = NfcA.Get(tag))
        {
            if (nfcA is not null)
            {
                sak = nfcA.Sak;
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
            if (nfcF is not null)
            {
                details.Add(("Radio", "FeliCa (NfcF), common on Japanese transit cards"));
                var systemCode = nfcF.GetSystemCode();
                if (systemCode is { Length: > 0 }) details.Add(("System code", ToHex(systemCode)));
            }
        }
        using (var nfcV = NfcV.Get(tag))
        {
            if (nfcV is not null)
            {
                details.Add(("Radio", "ISO 15693 (NfcV), typical for library and inventory labels"));
                DescribeIso15693(tag.GetId(), details);
                (memory, segments) = ReadIso15693(nfcV, tag.GetId() ?? [], details);
            }
        }

        bool isClassic = false;
        using (var classic = MifareClassic.Get(tag))
        {
            if (classic is not null)
            {
                isClassic = true;
                details.Add(("Chip", $"{MifareClassicKeys.ChipName(classic.Size)}, {classic.SectorCount} sectors"));
                details.Add(("Card", "MIFARE Classic"));
                // Try the entered key and the public defaults; show whatever opens. Sectors with another custom key stay closed.
                memory = ReadClassic(classic, details, out needsClassicKey, out segments);
            }
        }

        // ISO-DEP smart cards: payment cards, passports, DESFire access and transit cards.
        bool isIsoDep = false;
        using (var isoDep = IsoDep.Get(tag))
        {
            if (isoDep is not null)
            {
                isIsoDep = true;
                details.Add(("Chip", (sak is { } s ? CardTypes.ChipFromSak(s) : null) ?? "ISO 14443-4 smart card"));
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

                    // Read-protected pages (PROT=1): present a password and read again. The Amiibo key is a formula
                    // of the serial, so it is always tried; any other tag is tried only with a password the person
                    // typed, because a wrong guess counts against the tag's wrong-attempt limit.
                    // Read-protected pages (PROT=1): try a password and read again. For an Amiibo the key is a
                    // formula of the serial, tried on its own. For any other tag, the password the person entered
                    // for THIS tag is tried, or, if none, the factory default FFFFFFFF once (a single well-known
                    // guess, as for MIFARE Classic). A password entered for a different tag is never tried here.
                    if (chip is { TotalPages: > 0 } && rawMemory.Length < chip.TotalPages * 4)
                    {
                        string serial = ToHex(link.Uid);
                        bool amiiboAuto = Features.Amiibo && LooksAmiibo(rawMemory);
                        byte[]? manual = ManualPasswordFor(serial);
                        // Try the default only when it is not already known to fail on this tag (and the setting is
                        // on), and only when the tag confirms it has no wrong-attempt lock-out (AUTHLIM == 0); if that
                        // cannot be read, do not risk an automatic attempt.
                        bool autoDefault = !amiiboAuto && manual is null && ShouldTryDefault(serial)
                            && ReadAuthLimit(link, chip) == 0;
                        byte[]? pwd = amiiboAuto ? AmiiboPassword(link.Uid) : manual ?? (autoDefault ? TagPassword.FactoryDefault : null);

                        if (pwd is not null && TryAuthenticate(link, pwd, singleAttempt: autoDefault))
                        {
                            rawMemory = ReadMemory(link, chip);
                            if (autoDefault && rawMemory.Length >= chip.TotalPages * 4)
                            {
                                RecordDefault(serial, true);
                                details.Add(("Password", "Read with the factory default password (FFFFFFFF); this tag still has it."));
                            }
                        }
                        else if (autoDefault)
                        {
                            RecordDefault(serial, false);   // not the default: do not auto-try it on this tag again
                        }

                        // Still short, and not an Amiibo we open on our own: the person must supply the password.
                        if (rawMemory.Length < chip.TotalPages * 4 && !amiiboAuto)
                        {
                            needsPassword = true;
                            details.Add(("Password", manual is not null
                                ? "That password did not open the protected pages."
                                : DefaultKnownFailed(serial)
                                    ? "Some pages are password-protected (not the default). Enter the password to read them."
                                    : "Some pages are password-protected. Enter the password to read them."));
                        }
                    }

                    if (rawMemory.Length >= 16)
                    {
                        ndefBytes = ExtractType2Ndef(rawMemory, details);
                        DescribeType2Layout(rawMemory, chip, details);
                        if (chip is { UserEnd: > 0 }) segments = Type2Segments(rawMemory, chip);
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

        // Nothing identified the chip (an unreadable or unusual card): fall back to what the SAK byte suggests,
        // so the person still sees what kind of card it is.
        if (sak is { } sakValue && CardTypes.ChipFromSak(sakValue) is { } sakChip && !details.Any(d => d.Item1 == "Chip"))
            details.Add(("Chip", sakChip));

        if (records.Count == 0 && !details.Any(d => d.Item1 is "Access" or "Card" or "Data"))
            details.Add(("Content", "No NDEF content found on this tag"));

        if (rawMemory is not null && memory is { Count: > 0 })
            details.Add(("Memory", $"{memory.Count} pages readable ({memory.Count * 4} bytes)"));

        return new NfcTagInfo(ToHex(tag.GetId()), techs, details, records, memory, rawMemory, chipName, needsPassword, needsClassicKey,
            segments);
    }

    /// <summary>A Type 2 read for the segment editor: every page read, with the pages the lock bits lock for good
    /// left out of what may be written. Pages the read could not reach (behind a password) start empty.</summary>
    private static SegmentRead Type2Segments(byte[] memory, Type2Chip chip)
    {
        var layout = new SegmentLayout(TagFamily.Type2, chip.Name, 4, chip.TotalPages, chip.UserEnd);
        var data = new Dictionary<int, byte[]>();
        for (int page = 0; page * 4 + 4 <= memory.Length; page++) data[page] = memory[(page * 4)..(page * 4 + 4)];
        var locked = LockedPages(memory, chip);
        var blocked = Enumerable.Range(4, Math.Max(0, chip.UserEnd - 4)).Where(p => locked[p])
            .ToDictionary(p => p, _ => "is locked for good by the tag's lock bits");
        return new SegmentRead(layout, data, blocked);
    }

    /// <summary>Opens as many sectors of a MIFARE Classic card as the public default keys allow and returns their
    /// blocks as "sector / block" lines, or null when nothing opened. Only the keys everyone knows are tried
    /// (<see cref="MifareClassicKeys"/>); a card with custom keys stays closed. Read-only.</summary>
    private static List<string>? ReadClassic(MifareClassic classic, List<(string, string)> details, out bool anyLocked,
        out SegmentRead? segments)
    {
        anyLocked = false;
        segments = null;
        var blocks = new Dictionary<int, byte[]>();
        var closed = new Dictionary<int, string>();
        var openedSectors = new HashSet<int>();
        try
        {
            classic.Connect();
            classic.Timeout = 1000;
            int sectors = classic.SectorCount;
            var lines = new List<string>();
            var found = new List<(int Sector, string Key)>();
            int opened = 0;

            for (int sector = 0; sector < sectors; sector++)
            {
                var key = AuthenticateClassicSector(classic, sector);
                if (key is null)
                {
                    anyLocked = true;
                    lines.Add(MifareClassicKeys.LockedSector(sector));
                    for (int b = 0; b < classic.GetBlockCountInSector(sector); b++)
                        closed[classic.SectorToBlock(sector) + b] = "is in a sector no known key opened (enter its key and read the card again)";
                    continue;
                }

                opened++;
                found.Add((sector, MifareClassicKeys.KeyHex(key.Value.Key)));
                openedSectors.Add(sector);
                lines.Add(MifareClassicKeys.SectorHeader(sector, key.Value.Key, key.Value.Slot, key.Value.Name));
                int first = classic.SectorToBlock(sector);
                int count = classic.GetBlockCountInSector(sector);
                for (int b = 0; b < count; b++)
                {
                    int block = first + b;
                    try
                    {
                        var data = classic.ReadBlock(block);
                        if (data is { Length: >= 16 })
                        {
                            lines.Add(MifareClassicKeys.FormatBlock(block, data[..16]));
                            blocks[block] = data[..16];
                        }
                    }
                    catch
                    {
                        lines.Add($"  {block:D3}: could not be read");
                    }
                }
            }

            ClassicKeysFound = found;
            details.Add(("Access", MifareClassicKeys.AccessSummary(opened, sectors)));
            if (opened > 0)
                segments = new SegmentRead(new SegmentLayout(TagFamily.Classic, MifareClassicKeys.ChipName(classic.Size), 16,
                    classic.BlockCount), blocks, closed) { OpenedSectors = openedSectors };
            return opened > 0 ? lines : null;
        }
        catch (Exception ex)
        {
            details.Add(("Access", "Secure item: data sectors are key-protected. (" + ex.Message + ")"));
            return null;
        }
        finally
        {
            try { classic.Close(); } catch { }
        }
    }

    /// <summary>The first key that opens a sector, as key A or key B, or null. The key the person typed is tried
    /// first, then the public defaults.</summary>
    private static (byte[] Key, char Slot, string Name)? AuthenticateClassicSector(MifareClassic classic, int sector)
    {
        foreach (var (key, name) in ClassicKeyCandidates())
        {
            try { if (classic.AuthenticateSectorWithKeyA(sector, key)) return (key, 'A', name); } catch { }
            try { if (classic.AuthenticateSectorWithKeyB(sector, key)) return (key, 'B', name); } catch { }
        }
        return null;
    }

    /// <summary>The keys to try for a sector, in order and without repeats: the entered key first, then the built-in
    /// public defaults, then keys remembered from earlier reads, and last — only when "try all" is on — the whole
    /// imported dictionary.</summary>
    private static IEnumerable<(byte[] Key, string Name)> ClassicKeyCandidates()
    {
        var seen = new HashSet<string>();
        bool fresh(byte[] k) => seen.Add(Convert.ToHexString(k));

        if (_manualKey is { } manual && fresh(manual)) yield return (manual, "entered");
        foreach (var d in MifareClassicKeys.Defaults) if (fresh(d.Key)) yield return d;
        foreach (var k in _rememberedClassicKeys) if (fresh(k)) yield return (k, "remembered");
        if (_tryAllClassicKeys)
            foreach (var k in _dictionaryClassicKeys) if (fresh(k)) yield return (k, "dictionary");
    }

    /// <summary>Adds what the page layout reveals: Amiibo content, lock bits, password protection.</summary>
    private static void DescribeType2Layout(byte[] bytes, Type2Chip? chip, List<(string, string)> details)
    {
        if (Features.Amiibo && IsAmiiboDump(bytes))
        {
            details.Add(("Data", "Amiibo-style content (NTAG215 layout with the Nintendo capability container)"));
            // The rest of the game data is encrypted with Nintendo's keys, but the figure ID is not.
            if (AmiiboFigure(bytes) is { } figure)
                details.Add(("Figure ID", figure));
        }

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

        // DESFire or another ISO 14443-4 card: its version is public manufacturing data (no application keys).
        if (TryDesfireVersion(isoDep) is { } desfire)
        {
            details.Add(("Card", desfire));
            details.Add(("Access", "Secure item: application data is key-protected and not read by this app"));
            return;
        }

        details.Add(("Card", "Access, transit or company card (DESFire or similar)"));
        details.Add(("Access", "Secure item: content is protected and not read by this app"));
    }

    /// <summary>A DESFire's generation and storage size from its GET_VERSION frame, or null. The command returns
    /// the chip's public manufacturing data; no key is used and nothing on the card is changed.</summary>
    private static string? TryDesfireVersion(IsoDep isoDep)
    {
        try
        {
            // Native DESFire GetVersion, wrapped in an ISO 7816 APDU. The first frame is the 7-byte hardware block,
            // followed by the status 91 AF ("more frames"); the later frames are not needed to name the chip.
            var resp = isoDep.Transceive([0x90, 0x60, 0x00, 0x00, 0x00]);
            if (resp is { Length: >= 9 } && resp[^2] == 0x91)
                return CardTypes.DesfireFromVersion(resp[..7]);
        }
        catch
        {
            // Not a DESFire, or it moved away: the caller falls back to a generic description.
        }
        return null;
    }

    /// <summary>Adds the chip maker, and the NXP ICODE product when it is one, of an ISO 15693 label, read from the
    /// manufacturer byte next to the 0xE0 at the end of its UID.</summary>
    private static void DescribeIso15693(byte[]? uid, List<(string, string)> details)
    {
        if (uid is not { Length: >= 3 }) return;
        byte maker, type;
        if (uid[0] == 0xE0) { maker = uid[1]; type = uid.Length > 2 ? uid[2] : (byte)0; }
        else if (uid[^1] == 0xE0 && uid.Length >= 3) { maker = uid[^2]; type = uid[^3]; }
        else return;

        var name = CardTypes.Iso15693Manufacturer(maker);
        var product = maker == 0x04 ? CardTypes.NxpIcodeProduct(type) : null;
        details.Add(("Chip", product is not null ? $"{name} {product}" : $"{name} ISO 15693 label"));
    }

    /// <summary>Reads every block of an ISO 15693 label (as many as it reports, or until it stops answering) for the
    /// memory view and the segment editor. Blocks a label keeps private are simply not shown. Read-only.</summary>
    private static (List<string>? Lines, SegmentRead? Segments) ReadIso15693(NfcV nfcV, byte[] uid, List<(string, string)> details)
    {
        if (uid.Length != 8) return (null, null);
        try
        {
            nfcV.Connect();
            var (count, size) = Iso15693SystemInfo(nfcV, uid);
            var blocks = new Dictionary<int, byte[]>();
            var lines = new List<string>();
            int limit = count ?? 256;
            for (int block = 0; block < limit; block++)
            {
                var data = Iso15693ReadBlock(nfcV, uid, block);
                if (data is null) break;
                if (size is { } s && data.Length != s) break;
                blocks[block] = data;
                lines.Add($"{block:D3}: {ToHex(data)}  {Ascii(data)}");
            }
            if (blocks.Count == 0) return (null, null);

            int blockSize = size ?? blocks[0].Length;
            details.Add(("Memory", $"{blocks.Count} blocks of {blockSize} bytes readable" +
                (count is { } c && c > blocks.Count ? $" (the label reports {c})" : "")));
            var layout = new SegmentLayout(TagFamily.Iso15693, SegmentLayout.Iso15693Name, blockSize, count ?? blocks.Count);
            return (lines, new SegmentRead(layout, blocks, new Dictionary<int, string>()));
        }
        catch (Exception ex)
        {
            details.Add(("Note", "Could not read the label's memory (" + ex.Message + ")"));
            return (null, null);
        }
        finally { try { nfcV.Close(); } catch { } }
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
