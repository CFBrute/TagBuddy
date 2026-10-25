using System.Text;
#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

using static TagBuddy.Services.Type2Memory;

namespace TagBuddy.Services;

/// <summary>Writing: NDEF content, raw page dumps and erasing. Serial numbers are never written and lock bits
/// are never set.</summary>
public sealed partial class NfcReader
{
#if ANDROID
    /// <summary>Writes an NDEF message to a writable tag, or formats a blank one first. Nothing else about the
    /// tag is changed: its serial number is fixed and is never touched.</summary>
    private static (bool Ok, string Note) WriteNdef(Tag tag, NdefMessage message)
    {
        var bytes = message.ToByteArray() ?? [];
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
        new([NdefRecord.CreateUri(uri)!]);

    public static NdefMessage BuildText(string text, string language = "en") =>
        new([NdefRecord.CreateTextRecord(language, text)!]);

    public static NdefMessage BuildVCard(string vcard) =>
        new([NdefRecord.CreateMime("text/vcard", Encoding.UTF8.GetBytes(vcard))!]);

    /// <summary>A private-text record: bytes from <see cref="SealedText.Seal"/>, under the app's own type.</summary>
    public static NdefMessage BuildSealed(byte[] sealedBytes) =>
        new([NdefRecord.CreateMime(SealedText.MimeType, sealedBytes)!]);

    public static NdefMessage BuildEmpty() =>
        new([new NdefRecord(NdefRecord.TnfEmpty, [], [], [])]);

    // ------------------------------------------------------------------------------------------------
    // Type 2 writes
    // ------------------------------------------------------------------------------------------------

    /// <summary>The password to use for a tag: the Nintendo key when the tag (or the dump going onto it) is
    /// Amiibo-style, otherwise the password the person entered for THIS tag, or the factory default FFFFFFFF as a
    /// single well-known fallback. A password entered for another tag is never tried here.</summary>
    private static byte[]? PasswordFor(Type2Link link, byte[] tagMemory, bool amiiboDump = false)
    {
        if (Features.Amiibo && (amiiboDump || LooksAmiibo(tagMemory))) return AmiiboPassword(link.Uid);
        string serial = ToHex(link.Uid);
        return ManualPasswordFor(serial) ?? (ShouldTryDefault(serial) ? TagPassword.FactoryDefault : null);
    }

    /// <summary>Opens the password session when the tag protects pages this operation touches. Returns a
    /// message when the operation cannot proceed.</summary>
    private static string? OpenProtection(Type2Link link, Type2Chip? chip, int lastPageTouched, byte[]? pwd, ref bool authed, out string? note)
    {
        note = null;
        int auth0 = ReadAuth0(link, chip);
        if (auth0 < 0 || auth0 >= lastPageTouched) return null;

        string serial = ToHex(link.Uid);
        bool manualBound = ManualPasswordFor(serial) is not null;
        if (pwd is null)
            return $"This tag is password-protected from page {auth0}. Enter its password and hold it again. " +
                   "Nothing was changed.";

        bool amiibo = Features.Amiibo && !manualBound && pwd.SequenceEqual(AmiiboPassword(link.Uid));
        bool usingDefault = !manualBound && !amiibo;   // the factory default fallback: an automatic guess
        bool knownDefault = usingDefault && DefaultKnownWorks(serial);   // seen on an earlier read this session

        // A wrong automatic guess (the default) ticks the tag's limit, so spend at most one attempt on it.
        authed = TryAuthenticate(link, pwd, singleAttempt: usingDefault);
        if (usingDefault) RecordDefault(serial, authed);
        if (authed)
        {
            if (usingDefault)
                note = knownDefault
                    ? "This tag was read with the factory default password (FFFFFFFF), so it was written with it."
                    : "Written with the tag's factory default password (FFFFFFFF).";
            return null;
        }

        // The handshake fails the same way for a wrong password and for a tag that moved during it.
        string which = amiibo ? "Amiibo key" : manualBound ? "password" : "factory default password (FFFFFFFF)";
        string next = manualBound || amiibo ? "Hold it still and try again." : "Enter the tag's password and hold it again.";
        int limit = ReadAuthLimit(link, chip);
        var limitNote = limit > 0
            ? $" This tag locks for good after {limit} wrong attempts, so do not keep retrying if it fails again."
            : "";
        return $"This tag is password-protected and did not accept the {which}, or it moved away during the handshake. " +
               next + " Nothing was changed." + limitNote;
    }

    /// <summary>Writes an NDEF TLV into the user pages of a Type 2 tag, formatting a blank one first.</summary>
    private static (bool Ok, string Note) WriteType2Ndef(Tag tag, byte[] ndef)
    {
        using var link = Type2Link.Open(tag);
        if (link is null) return (false, "This tag cannot be written page by page.");

        var data = NdefTlv(ndef);

        link.Connect();
        var chip = GetVersion(link);
        var before = ReadMemory(link, chip);
        if (before.Length < 16) return (false, "Could not read the tag. Hold it still and try again.");

        int userEnd = chip is { UserEnd: > 0 } ? chip.UserEnd : before.Length / 4;
        var pwd = PasswordFor(link, before);
        bool authed = false;
        if (OpenProtection(link, chip, userEnd, pwd, ref authed, out var protNote) is { } blocked) return (false, blocked);

        var notes = new List<string>();
        if (protNote is not null) notes.Add(protNote);
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
            return (false, (Features.Amiibo && LooksAmiibo(before)
                ? "This tag has been an Amiibo, which changes its page 3 for good, so phones will never read it as a text or link tag. "
                : $"This tag is not NDEF-formatted (page 3 is {ToHex(cc)}) and cannot be formatted, so phones will never read it as a text or link tag. ")
                + RawBytesTip);
        }
        else if ((cc[3] & 0x0F) != 0)
        {
            return (false, "This tag is marked read-only in its capability container and cannot be changed.");
        }

        int capacity = Math.Min(cc[2] * 8, (userEnd - 4) * 4);
        if (data.Length > capacity)
            return (false, $"The content needs {data.Length} bytes but this tag holds only {capacity}.");

        int written = 0;
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
        if (link is null) return (false, "Binary dumps can only be written to Type 2 tags (NTAG, Ultralight).");

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
        if (amiibo && !Features.Amiibo)
            return (false, "This dump holds game-figure data, which this version of the app does not write. Nothing was changed.");
        if (amiibo && chip.UserEnd < 130)
            return (false, $"An Amiibo dump needs 130 pages of user memory (NTAG215); this {chip.Name} has {chip.UserEnd - 4}.");

        var pwd = PasswordFor(link, before, amiibo);
        bool authed = false;
        if (OpenProtection(link, chip, endPage, pwd, ref authed, out var protNote) is { } blocked) return (false, blocked);

        var notes = new List<string>();
        if (protNote is not null) notes.Add(protNote);
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
        if (amiibo && dumpSerial.Length > 0 && !string.Equals(dumpSerial, serial, StringComparison.OrdinalIgnoreCase))
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
        if (OpenProtection(link, chip, userEnd, pwd, ref authed, out var protNote) is { } blocked) return (false, blocked);

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
        if (protNote is not null) notes.Add(protNote);
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
            notes.Add($"Page 3 keeps {(Features.Amiibo ? "the Amiibo" : "a non-NDEF")} capability container (one-time programmable), " +
                      "so phones will not treat this tag as a text or link tag; it can still hold another binary dump.");

        return (true, $"Tag erased: {written} pages cleared. {string.Join(" ", notes)}".TrimEnd());
    }
#endif
}
