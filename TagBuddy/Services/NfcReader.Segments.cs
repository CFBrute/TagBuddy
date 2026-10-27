#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

using static TagBuddy.Services.Type2Memory;

namespace TagBuddy.Services;

/// <summary>Segment writes from the editor (<see cref="TagEdit"/>): chosen pages of a Type 2 tag, data blocks of a MIFARE
/// Classic card, blocks of an ISO 15693 label. The tag held to the phone is checked first (its serial when the edit
/// expects one, its chip), and only then are the segments written, each read back to confirm it. The serial, one-time
/// bits, lock bits, password settings and sector trailers are never written: the layout refuses them before this.</summary>
public sealed partial class NfcReader
{
#if ANDROID
    /// <summary>Arms writing the edit's segments to the next tag that matches it.</summary>
    public void ArmSegmentWrite(SegmentWrite write) => _pendingWrite = tag => WriteSegments(tag, write);

    private static SegmentWriteRecord? _lastSegmentWrite;

    /// <summary>The sector keys the last segment write set on a card, confirmed by opening the sector with them:
    /// (sector, 12-hex key). The page remembers them for the card, so later reads open it.</summary>
    public static IReadOnlyList<(int Sector, string Key)> ClassicKeysSet { get; private set; } = [];

    /// <summary>The sector keys the last write put on the card's trailer, whether or not re-opening the sector could
    /// confirm them afterwards. The key is on the card now, so it is remembered even when the confirming read failed
    /// (the card halted or moved) — otherwise the next read could not open the sector without retyping the key.</summary>
    public static IReadOnlyList<(int Sector, string Key)> ClassicKeysWritten { get; private set; } = [];

    /// <summary>What the last segment write changed (each segment's bytes before and after), for undoing it on the tag;
    /// null when it changed nothing. Taken once, by the page handling the write's result.</summary>
    public SegmentWriteRecord? TakeLastSegmentWrite()
    {
        var record = _lastSegmentWrite;
        _lastSegmentWrite = null;
        return record;
    }

    /// <summary>Collects, while a write runs, what each segment held before and holds after it, and leaves it as the
    /// record once the write ends (however it ends).</summary>
    private sealed class WriteLog(string tagId, SegmentLayout layout)
    {
        private readonly Dictionary<int, byte[]> _before = [], _after = [];

        /// <summary>A segment written; <paramref name="before"/> is empty when the tag could not be read first, and then
        /// it cannot be undone.</summary>
        public void Written(int segment, ReadOnlySpan<byte> before, byte[] after)
        {
            if (before.Length < after.Length) return;
            _before[segment] = before[..after.Length].ToArray();
            _after[segment] = after;
        }

        // A key-only change logs no data blocks but still writes the trailer; produce a record so the page settles the
        // key edit (and knows which tag it landed on), with the sectors whose keys changed for its undo/history.
        public void Publish() =>
            _lastSegmentWrite = _before.Count == 0 && ClassicKeysSet.Count == 0 ? null
                : new SegmentWriteRecord(tagId, layout, _before, _after) { KeySectors = ClassicKeysSet.Select(k => k.Sector).Distinct().Order().ToList() };
    }

    private static (bool Ok, string Note) WriteSegments(Tag tag, SegmentWrite write)
    {
        _lastSegmentWrite = null;
        ClassicKeysSet = [];
        ClassicKeysWritten = [];
        var log = new WriteLog(ToHex(tag.GetId()), write.Layout);
        try { return WriteSegments(tag, write, log); }
        finally { log.Publish(); }
    }

    private static (bool Ok, string Note) WriteSegments(Tag tag, SegmentWrite write, WriteLog log)
    {
        if (write.IsEmpty) return (false, "Nothing to write.");
        if (write.WrongTag(ToHex(tag.GetId())) is { } wrongTag) return (false, wrongTag);
        foreach (var segment in write.Writes.Keys)
        {
            // Block 0 is normally refused as a serial block; when the editor unlocked it (a magic card), let the write
            // attempt it so the card itself decides — the Classic path then reports exactly what happened.
            if (write.AllowBlockZero && write.Layout.Family == TagFamily.Classic && segment == 0) continue;
            if (write.Layout.WhyNotWritable(segment) is { } why)
                return (false, $"{Pluralise(1, write.Layout.Noun)} {segment} {why}, so it is never written. Nothing was written.");
        }

        return write.Layout.Family switch
        {
            TagFamily.Type2 => WriteType2Segments(tag, write, log),
            TagFamily.Classic => WriteClassicSegments(tag, write, log),
            _ => WriteIso15693Segments(tag, write, log),
        };
    }

    // ------------------------------------------------------------------------------------------------ Type 2

    private static (bool Ok, string Note) WriteType2Segments(Tag tag, SegmentWrite write, WriteLog log)
    {
        if (!IsType2(tag)) return (false, $"This is not an NTAG or Ultralight tag, but the edit is for {write.Layout.ChipName}. Nothing was written.");
        using var link = Type2Link.Open(tag);
        if (link is null) return (false, "This tag cannot be written page by page.");

        link.Connect();
        var chip = GetVersion(link);
        if (chip is not { UserEnd: > 0 })
            return (false, "This chip could not be identified, so the app cannot tell its user pages from its lock and " +
                           "configuration pages. Nothing was written.");
        var actual = new SegmentLayout(TagFamily.Type2, chip.Name, 4, chip.TotalPages, chip.UserEnd);
        if (write.WrongChip(actual) is { } wrongChip) return (false, wrongChip);

        var before = ReadMemory(link, chip);
        if (before.Length < 16) return (false, "Could not read the tag. Hold it still and try again.");

        int last = write.Writes.Keys.Max();
        var pwd = PasswordFor(link, before);
        bool authed = false;
        if (OpenProtection(link, chip, last + 1, pwd, ref authed, out var protNote) is { } blocked) return (false, blocked);
        if (authed && before.Length < chip.TotalPages * 4) before = ReadMemory(link, chip);   // read-protected pages, now open

        var notes = new List<string>();
        if (protNote is not null) notes.Add(protNote);
        var locked = LockedPages(before, chip);
        var lockedPages = new List<int>();
        int written = 0, same = 0;

        var targets = new Dictionary<int, byte[]>();
        var unread = new List<int>();
        foreach (var (page, wanted) in write.Writes.OrderBy(p => p.Key))
        {
            var current = before.Length >= (page + 1) * 4 ? before.AsSpan(page * 4, 4) : [];
            if (SegmentWrite.Merge(wanted, current) is not { } data) { unread.Add(page); continue; }
            targets[page] = data;
            if (SamePage(before, page, data)) { same++; continue; }
            if (locked[page]) { lockedPages.Add(page); continue; }
            if (!WritePageWithRetry(link, page, data, pwd, ref authed, out var error))
            {
                if (link.Read(page) is not null) { lockedPages.Add(page); locked[page] = true; continue; }
                return (false, $"Write failed at page {page} ({error}) after {written} pages: the tag moved away. Hold it still and try again.");
            }
            log.Written(page, before.Length >= (page + 1) * 4 ? before.AsSpan(page * 4, 4) : [], data);
            written++;
        }

        var after = ReadMemory(link, chip);
        var wrong = targets.Keys.Where(p => !locked[p] && !SamePage(after, p, targets[p])).Order().ToList();
        if (unread.Count > 0)
            notes.Add($"{Pluralise(unread.Count, "page")} {Ranges(unread)} could not be read to keep the bytes you did not type, so " +
                      $"{(unread.Count == 1 ? "it was" : "they were")} not written.");
        var (ok, note) = Result(write.Layout, written, same, wrong, lockedPages, "locked for good and kept their old content", notes);
        return (ok && unread.Count == 0, note);
    }

    // ------------------------------------------------------------------------------------------------ MIFARE Classic

    /// <summary>Writes data blocks sector by sector. Each sector is opened with the keys a read tries (the entered key,
    /// the public defaults, remembered keys), as key A and then key B (a sector's access bits often let only one of them write), and each block is
    /// read back after writing. A sector that no key opens, or that refuses the write, is reported and left as it was.</summary>
    private static (bool Ok, string Note) WriteClassicSegments(Tag tag, SegmentWrite write, WriteLog log)
    {
        // Block 0 (the UID) on a magic card: try the gen1a "backdoor" first — it needs no key and its own NfcA
        // connection, so it must run before MifareClassic opens. If the card is not gen1a (or the phone cannot send the
        // unlock frame) the attempt fails harmlessly and block 0 falls through to the normal write, where a gen2/CUID
        // card takes it with an ordinary authenticated write (and a plain card is refused with a clear reason).
        string? gen1aNote = null;
        bool gen1aWroteZero = false;
        if (write.AllowBlockZero && write.Writes.ContainsKey(0))
        {
            (gen1aWroteZero, gen1aNote) = TryGen1aWriteBlockZero(tag, write.Writes[0], log);
            if (gen1aWroteZero)
                write = write with { Writes = write.Writes.Where(p => p.Key != 0).ToDictionary(p => p.Key, p => p.Value) };
        }

        using var classic = MifareClassic.Get(tag);
        if (classic is null) return (false, $"This is not a MIFARE Classic card, but the edit is for {write.Layout.ChipName}. Nothing was written.");

        classic.Connect();
        try
        {
            classic.Timeout = 1000;
            var actual = new SegmentLayout(TagFamily.Classic, MifareClassicKeys.ChipName(classic.Size), 16, classic.BlockCount);
            if (write.WrongChip(actual) is { } wrongChip) return (false, wrongChip);

            // The same keys a read tries, in the same order: the entered key, the public defaults, keys remembered
            // for cards, and the imported dictionary when "try all" is on.
            var candidates = ClassicKeyCandidates().Select(k => k.Key).ToList();

            int written = 0, same = 0;
            var refused = new List<int>();
            var keysSet = new List<(int, string)>();
            var keysWritten = new List<(int, string)>();
            var keyNotes = new List<string>();
            string? blockZeroWhy = null;   // a precise reason block 0 (the UID) did not change, if it was tried and failed
            var sectors = write.Writes.Keys.Select(MifareClassicKeys.SectorOf).Concat(write.Sectors.Keys).Distinct().Order();
            foreach (int sectorNo in sectors)
            {
                var pending = write.Writes.Where(p => MifareClassicKeys.SectorOf(p.Key) == sectorNo).OrderBy(p => p.Key).ToList();
                // The trailer (a new key, new rules) is written only once the sector's data is, with the key that opened
                // the sector, and tried once: never with another key after a refusal.
                bool wantsKey = write.Sectors.TryGetValue(sectorNo, out var change);
                foreach (var key in candidates)
                {
                    foreach (bool useB in new[] { false, true })
                    {
                        if (pending.Count == 0 && !wantsKey) break;
                        int attempting = -1;   // the block being written when an exception is thrown (for block-0 diagnostics)
                        try
                        {
                            bool opened = useB ? classic.AuthenticateSectorWithKeyB(sectorNo, key)
                                               : classic.AuthenticateSectorWithKeyA(sectorNo, key);
                            if (!opened) continue;
                            while (pending.Count > 0)
                            {
                                var (block, wanted) = pending[0];
                                var current = classic.ReadBlock(block);
                                var data = SegmentWrite.Merge(wanted, current is { Length: >= 16 } ? current.AsSpan(0, 16) : []);
                                if (data is null) break;                    // cannot keep the bytes not typed: next key
                                if (current is { Length: >= 16 } && current.AsSpan(0, 16).SequenceEqual(data)) { same++; pending.RemoveAt(0); continue; }
                                attempting = block;
                                classic.WriteBlock(block, data);
                                var check = classic.ReadBlock(block);
                                if (check is not { Length: >= 16 } || !check.AsSpan(0, 16).SequenceEqual(data))
                                {
                                    // Block 0 (the UID) was sent and acknowledged but reads back unchanged: the card is not
                                    // one that takes a direct block-0 rewrite. Say so plainly — it is the card, not Android.
                                    if (block == 0 && blockZeroWhy is null)
                                        blockZeroWhy = "Block 0 (the UID) was written and the card acknowledged the command, but it reads " +
                                            "back unchanged — the card kept its old UID. So this card does not take a direct block-0 rewrite over " +
                                            "standard commands: a normal MIFARE Classic has a read-only block 0, and some \"magic\" cards need a gen1a " +
                                            "backdoor sequence this app does not send. This is the card refusing the change, not an Android or app fault.";
                                    break;
                                }
                                attempting = -1;
                                log.Written(block, current is { Length: >= 16 } ? current.AsSpan(0, 16) : [], data);
                                written++;
                                pending.RemoveAt(0);
                            }

                            if (pending.Count == 0 && wantsKey)
                            {
                                var (done, why, keyNow, wrote) = WriteClassicTrailer(classic, sectorNo, key, useB, change!);
                                if (wrote) keysWritten.Add((sectorNo, MifareClassicKeys.KeyHex(keyNow)));
                                if (done) keysSet.Add((sectorNo, MifareClassicKeys.KeyHex(keyNow)));
                                else if (why is not null) keyNotes.Add(why);
                                wantsKey = false;                           // one attempt: never retried with another key
                            }
                        }
                        catch (Exception ex)
                        {
                            // Block 0 threw: Android's NFC stack rejected the command outright (told apart from the silent
                            // no-op above). On a normal card block 0 is read-only, so this is still the card refusing it.
                            if (attempting == 0 && blockZeroWhy is null)
                                blockZeroWhy = $"Writing block 0 (the UID) failed with {ex.GetType().Name}" +
                                    (string.IsNullOrWhiteSpace(ex.Message) ? "" : $": {ex.Message}") +
                                    ". Android's NFC stack rejected the command — on a normal MIFARE Classic block 0 is read-only, so this is " +
                                    "the card refusing the UID change, not an app fault. A magic card that takes direct block-0 writes would accept it.";
                            // Refused (this key may not write here) or a failed read: the card drops the session; open it again.
                            try { classic.Close(); classic.Connect(); classic.Timeout = 1000; }
                            catch { return (false, $"The card moved away after {written} blocks. Hold it still and try again."); }
                        }
                    }
                    if (pending.Count == 0 && !wantsKey) break;
                }
                refused.AddRange(pending.Select(p => p.Key));
                if (wantsKey) keyNotes.Add($"Sector {sectorNo} did not take its change: no known key opened it.");
            }

            ClassicKeysSet = keysSet;
            ClassicKeysWritten = keysWritten;
            // Block 0 has its own precise reason; drop it from the generic "no key opened the sector" list and append that.
            if (blockZeroWhy is not null) { refused.Remove(0); keyNotes.Add(blockZeroWhy); }
            var sectorsSet = keysSet.Select(k => k.Item1).Distinct().ToList();
            if (sectorsSet.Count > 0)
                keyNotes.Insert(0, $"Sector{(sectorsSet.Count == 1 ? "" : "s")} {Ranges(sectorsSet)} " +
                                   $"{(sectorsSet.Count == 1 ? "has its" : "have their")} new key or rules; the keys are remembered for this tag.");
            var (ok, note) = Result(write.Layout, written, same, [], refused,
                "were not changed: no known key opened their sector, its access bits refuse writing, or they could not be read to keep the bytes you did not type", keyNotes);
            if (gen1aWroteZero) note = gen1aNote + " " + note;                                   // backdoor UID write succeeded
            else if (gen1aNote is not null && blockZeroWhy is not null)                           // backdoor tried, block 0 still failed
                note += " The gen1a backdoor was also tried for block 0: " + gen1aNote;
            if (blockZeroWhy is not null) return (false, note);   // a block-0 that did not take is a failure, whatever else wrote
            return (ok && sectorsSet.Count == write.Sectors.Count, note);
        }
        finally { try { classic.Close(); } catch { } }
    }

    /// <summary>Writes block 0 (the UID) to a gen1a "backdoor" magic card: unlock with the 0x40 then 0x43 short frames
    /// (no key, no auth), then write block 0 raw and read it back to confirm. Works only on a gen1a card, and only if
    /// the phone's NFC controller will send the unlock frame — otherwise the unlock transceive fails and this reports
    /// why, leaving block 0 to the normal write. The maker bytes not typed are kept from the card's current block 0.</summary>
    private static (bool Ok, string Note) TryGen1aWriteBlockZero(Tag tag, byte?[] wanted, WriteLog log)
    {
        using var nfcA = NfcA.Get(tag);
        if (nfcA is null) return (false, "the card has no NfcA layer for the backdoor.");
        try
        {
            nfcA.Connect();
            nfcA.Timeout = 1500;

            // The gen1a unlock: a 0x40 short frame, then 0x43. A gen1a answers; anything else — or a phone that cannot
            // emit the frame — fails here, and block 0 falls through to the normal (gen2/plain) write.
            try
            {
                nfcA.Transceive([0x40]);
                nfcA.Transceive([0x43]);
            }
            catch (Exception ex)
            {
                return (false, $"the backdoor unlock (40/43) was refused ({ex.GetType().Name}) — not a gen1a card, or this phone's NFC chip cannot send the unlock frame.");
            }

            // Read the current block 0 so the maker bytes the person did not type are kept.
            byte[]? current;
            try { current = nfcA.Transceive([0x30, 0x00]); }
            catch (Exception ex) { return (false, $"block 0 could not be read after the unlock ({ex.GetType().Name})."); }
            if (current is not { Length: >= 16 }) return (false, "block 0 could not be read after the unlock.");

            var data = SegmentWrite.Merge(wanted, current.AsSpan(0, 16));
            if (data is null) return (false, "block 0 could not be merged to keep the bytes not typed.");

            // Write block 0: the write request 0xA0 0x00, then the 16 bytes; the card ACKs each.
            try
            {
                nfcA.Transceive([0xA0, 0x00]);
                nfcA.Transceive(data);
            }
            catch (Exception ex)
            {
                return (false, $"the backdoor write of block 0 failed ({ex.GetType().Name}: {ex.Message}).");
            }

            // Confirm by reading it back.
            byte[]? check;
            try { check = nfcA.Transceive([0x30, 0x00]); }
            catch { check = null; }
            if (check is not { Length: >= 16 } || !check.AsSpan(0, 16).SequenceEqual(data))
                return (false, "the backdoor write was sent but block 0 did not read back as written.");

            log.Written(0, current.AsSpan(0, 16), data);
            return (true, "Block 0 (the UID) was written via the gen1a backdoor.");
        }
        catch (Exception ex)
        {
            return (false, $"the gen1a backdoor failed ({ex.GetType().Name}: {ex.Message}).");
        }
        finally { try { nfcA.Close(); } catch { } }
    }

    /// <summary>Writes a sector's trailer with its change: the new key as key A and key B (or the known key kept where
    /// no new one is given), and the new rules (or the current ones kept). The sector was just opened with a known key,
    /// which is what entitles a change; whether the card lets that key rewrite the trailer is up to its rules, and a
    /// refusal is reported. The sector is opened again afterwards to confirm the write; only then is it reported done.
    /// Returns the key to remember for the sector: the strongest one under its rules (one that reads and writes the data
    /// if there is one, else one that reads), as long as it opens the sector; else the other key that does.</summary>
    private static (bool Done, string? Why, byte[] KeyNow, bool Written) WriteClassicTrailer(MifareClassic classic, int sector, byte[] openedWith, bool asKeyB, SectorChange change)
    {
        int trailer = classic.SectorToBlock(sector) + classic.GetBlockCountInSector(sector) - 1;
        byte[]? current;
        try { current = classic.ReadBlock(trailer); }
        catch { current = null; }
        if (current is not { Length: >= 16 })
            return (false, $"Sector {sector} did not take its change: its trailer could not be read.", openedWith, false);

        // A new key A and/or key B are written as given; the one not being changed must be kept, but keys read back as
        // zeros. The kept key is the one that opened the sector (key A when opened with A, key B when opened with B), or
        // a readable key B under factory rules. If the key to keep is not known, nothing is written to this sector.
        byte[] readableB = current[10..16];
        byte[]? keyA = change.KeyA ?? (asKeyB ? null : openedWith);
        byte[]? keyB = change.KeyB ?? (asKeyB ? openedWith : readableB.Any(b => b != 0) ? readableB : null);
        if (keyA is null || keyB is null)
            return (false, $"Sector {sector} did not take its change: key {(keyA is null ? 'A' : 'B')} could not be kept " +
                           $"(open the sector with key {(keyA is null ? 'A' : 'B')}, or set it too). Nothing was written to this sector.", openedWith, false);
        byte[] keyNow = change.KeyA ?? change.KeyB ?? openedWith;

        try
        {
            classic.WriteBlock(trailer, MifareClassicKeys.TrailerFor(keyA, keyB, current, change.Rules));
        }
        catch
        {
            try { classic.Close(); classic.Connect(); classic.Timeout = 1000; } catch { }
            return (false, $"Sector {sector} did not take its change: the card refused the write (its rules may not let " +
                           $"key {(asKeyB ? 'B' : 'A')} change the trailer) or it moved away.", openedWith, false);
        }

        // Writing the trailer ends the session; the proof is opening the sector again, with the stronger key first so
        // that is the one remembered (unknown rules: key A first, as before).
        var rules = change.Rules ?? MifareClassicKeys.DecodeRules(current);
        bool bFirst = rules is not null && MifareClassicKeys.KeyStrength(rules, keyB: true) > MifareClassicKeys.KeyStrength(rules, keyB: false);
        foreach (bool useB in bFirst ? new[] { true, false } : new[] { false, true })
        {
            try
            {
                classic.Close(); classic.Connect(); classic.Timeout = 1000;   // a failed try halts the card: start afresh
                if (useB ? classic.AuthenticateSectorWithKeyB(sector, keyB) : classic.AuthenticateSectorWithKeyA(sector, keyA))
                    return (true, null, useB ? keyB : keyA, true);
            }
            catch { }
        }
        return (false, $"Sector {sector}: the trailer was written but the sector did not open afterwards. Its new key is " +
                       "remembered anyway; hold the card still and read it again before trying once more.", keyNow, true);
    }

    // ------------------------------------------------------------------------------------------------ ISO 15693

    private static (bool Ok, string Note) WriteIso15693Segments(Tag tag, SegmentWrite write, WriteLog log)
    {
        using var nfcV = NfcV.Get(tag);
        if (nfcV is null) return (false, "This is not an ISO 15693 label, but the edit is for one. Nothing was written.");
        var uid = tag.GetId() ?? [];
        if (uid.Length != 8) return (false, "This label's serial is not the 8 bytes ISO 15693 uses. Nothing was written.");

        nfcV.Connect();
        try
        {
            var (count, size) = Iso15693SystemInfo(nfcV, uid);
            int blockSize = size ?? Iso15693ReadBlock(nfcV, uid, 0)?.Length ?? write.Layout.SegmentSize;
            var actual = new SegmentLayout(TagFamily.Iso15693, SegmentLayout.Iso15693Name, blockSize, count ?? 0);
            if (write.WrongChip(actual) is { } wrongChip) return (false, wrongChip);
            if (count is { } c && write.Writes.Keys.Max() >= c)
                return (false, $"This label has {c} blocks (0-{c - 1}), but the edit writes block {write.Writes.Keys.Max()}. Nothing was written.");

            int written = 0, same = 0;
            var refused = new List<int>();
            foreach (var (block, wanted) in write.Writes.OrderBy(p => p.Key))
            {
                var current = Iso15693ReadBlock(nfcV, uid, block);
                if (SegmentWrite.Merge(wanted, current) is not { } data) { refused.Add(block); continue; }
                if (current is not null && current.AsSpan().SequenceEqual(data)) { same++; continue; }
                if (Iso15693WriteBlock(nfcV, uid, block, data) && Iso15693ReadBlock(nfcV, uid, block) is { } check
                    && check.AsSpan().SequenceEqual(data))
                {
                    log.Written(block, current, data);
                    written++;
                    continue;
                }
                // Refused but still answering: a locked or protected block. Gone: it moved away.
                if (Iso15693ReadBlock(nfcV, uid, block) is null && current is null)
                    return (false, $"Write failed at block {block} after {written} blocks: the label moved away. Hold it still and try again.");
                refused.Add(block);
            }
            return Result(write.Layout, written, same, [], refused, "were not changed: locked or protected, or not readable to keep the bytes you did not type", []);
        }
        finally { try { nfcV.Close(); } catch { } }
    }

    /// <summary>Addressed ISO 15693 command: flags (high data rate, addressed, plus <paramref name="option"/>), the
    /// command code, the label's UID as Android gives it (least significant byte first, as sent on air), then data.</summary>
    private static byte[] Iso15693Command(byte command, byte[] uid, bool option, params byte[] data)
    {
        var frame = new byte[2 + uid.Length + data.Length];
        frame[0] = (byte)(0x22 | (option ? 0x40 : 0));
        frame[1] = command;
        uid.CopyTo(frame, 2);
        data.CopyTo(frame, 2 + uid.Length);
        return frame;
    }

    /// <summary>GET SYSTEM INFORMATION: the label's block count and block size, when it reports them.</summary>
    private static (int? Count, int? Size) Iso15693SystemInfo(NfcV nfcV, byte[] uid)
    {
        try
        {
            var r = nfcV.Transceive(Iso15693Command(0x2B, uid, false));
            if (r is not { Length: >= 10 } || (r[0] & 0x01) != 0) return (null, null);
            int info = r[1], i = 10;
            if ((info & 0x01) != 0) i++;                   // DSFID
            if ((info & 0x02) != 0) i++;                   // AFI
            if ((info & 0x04) == 0 || r.Length < i + 2) return (null, null);
            return (r[i] + 1, (r[i + 1] & 0x1F) + 1);
        }
        catch { return (null, null); }
    }

    /// <summary>READ SINGLE BLOCK, or null when the label refuses or does not answer.</summary>
    private static byte[]? Iso15693ReadBlock(NfcV nfcV, byte[] uid, int block)
    {
        try
        {
            var r = nfcV.Transceive(Iso15693Command(0x20, uid, false, (byte)block));
            return r is { Length: >= 2 } && (r[0] & 0x01) == 0 ? r[1..] : null;
        }
        catch { return null; }
    }

    /// <summary>WRITE SINGLE BLOCK. Some labels (TI Tag-it) only take it with the option flag, so that is tried second.</summary>
    private static bool Iso15693WriteBlock(NfcV nfcV, byte[] uid, int block, byte[] data)
    {
        foreach (bool option in new[] { false, true })
        {
            try
            {
                var r = nfcV.Transceive(Iso15693Command(0x21, uid, option, [(byte)block, .. data]));
                if (r is { Length: >= 1 } && (r[0] & 0x01) == 0) return true;
            }
            catch
            {
                // No answer or an error frame: try the other way, then give up.
            }
        }
        return false;
    }

    // ------------------------------------------------------------------------------------------------ result

    /// <summary>The result message: what was written and already matched, segments that read back differently, and the
    /// segments left alone (<paramref name="keptWhy"/> says why). Succeeds only when every segment holds its new bytes.</summary>
    private static (bool Ok, string Note) Result(SegmentLayout layout, int written, int same, List<int> mismatched,
        List<int> kept, string keptWhy, List<string> notes)
    {
        string noun = layout.Noun;
        var text = new List<string> { $"{written} {noun}{(written == 1 ? "" : "s")} written, {same} already matched." };
        if (kept.Count > 0) text.Add($"{Pluralise(kept.Count, noun)} {Ranges(kept)} {keptWhy}.");
        if (mismatched.Count > 0) text.Add($"{Pluralise(mismatched.Count, noun)} {Ranges(mismatched)} read back differently.");
        text.AddRange(notes);
        return (kept.Count == 0 && mismatched.Count == 0, string.Join(" ", text));
    }
#else
    public void ArmSegmentWrite(SegmentWrite write) { }
    public SegmentWriteRecord? TakeLastSegmentWrite() => null;
    public static IReadOnlyList<(int Sector, string Key)> ClassicKeysSet => [];
    public static IReadOnlyList<(int Sector, string Key)> ClassicKeysWritten => [];
#endif
}
