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

        public void Publish() =>
            _lastSegmentWrite = _before.Count == 0 ? null : new SegmentWriteRecord(tagId, layout, _before, _after);
    }

    private static (bool Ok, string Note) WriteSegments(Tag tag, SegmentWrite write)
    {
        _lastSegmentWrite = null;
        ClassicKeysSet = [];
        var log = new WriteLog(ToHex(tag.GetId()), write.Layout);
        try { return WriteSegments(tag, write, log); }
        finally { log.Publish(); }
    }

    private static (bool Ok, string Note) WriteSegments(Tag tag, SegmentWrite write, WriteLog log)
    {
        if (write.IsEmpty) return (false, "Nothing to write.");
        if (write.WrongTag(ToHex(tag.GetId())) is { } wrongTag) return (false, wrongTag);
        foreach (var segment in write.Writes.Keys)
            if (write.Layout.WhyNotWritable(segment) is { } why)
                return (false, $"{Pluralise(1, write.Layout.Noun)} {segment} {why}, so it is never written. Nothing was written.");

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
            var keyNotes = new List<string>();
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
                                classic.WriteBlock(block, data);
                                var check = classic.ReadBlock(block);
                                if (check is not { Length: >= 16 } || !check.AsSpan(0, 16).SequenceEqual(data)) break;
                                log.Written(block, current is { Length: >= 16 } ? current.AsSpan(0, 16) : [], data);
                                written++;
                                pending.RemoveAt(0);
                            }

                            if (pending.Count == 0 && wantsKey)
                            {
                                var (done, why, keyNow) = WriteClassicTrailer(classic, sectorNo, key, useB, change!);
                                if (done) keysSet.Add((sectorNo, MifareClassicKeys.KeyHex(keyNow)));
                                else if (why is not null) keyNotes.Add(why);
                                wantsKey = false;                           // one attempt: never retried with another key
                            }
                        }
                        catch
                        {
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
            var sectorsSet = keysSet.Select(k => k.Item1).Distinct().ToList();
            if (sectorsSet.Count > 0)
                keyNotes.Insert(0, $"Sector{(sectorsSet.Count == 1 ? "" : "s")} {Ranges(sectorsSet)} " +
                                   $"{(sectorsSet.Count == 1 ? "has its" : "have their")} new key or rules; the keys are remembered for this tag.");
            var (ok, note) = Result(write.Layout, written, same, [], refused,
                "were not changed: no known key opened their sector, its access bits refuse writing, or they could not be read to keep the bytes you did not type", keyNotes);
            return (ok && sectorsSet.Count == write.Sectors.Count, note);
        }
        finally { try { classic.Close(); } catch { } }
    }

    /// <summary>Writes a sector's trailer with its change: the new key as key A and key B (or the known key kept where
    /// no new one is given), and the new rules (or the current ones kept). The sector was just opened with a known key,
    /// which is what entitles a change; whether the card lets that key rewrite the trailer is up to its rules, and a
    /// refusal is reported. The sector is opened again afterwards to confirm the write; only then is it reported done.
    /// Returns the key to remember for the sector: the strongest one under its rules (one that reads and writes the data
    /// if there is one, else one that reads), as long as it opens the sector; else the other key that does.</summary>
    private static (bool Done, string? Why, byte[] KeyNow) WriteClassicTrailer(MifareClassic classic, int sector, byte[] openedWith, bool asKeyB, SectorChange change)
    {
        int trailer = classic.SectorToBlock(sector) + classic.GetBlockCountInSector(sector) - 1;
        byte[]? current;
        try { current = classic.ReadBlock(trailer); }
        catch { current = null; }
        if (current is not { Length: >= 16 })
            return (false, $"Sector {sector} did not take its change: its trailer could not be read.", openedWith);

        // A new key A and/or key B are written as given; the one not being changed must be kept, but keys read back as
        // zeros. The kept key is the one that opened the sector (key A when opened with A, key B when opened with B), or
        // a readable key B under factory rules. If the key to keep is not known, nothing is written to this sector.
        byte[] readableB = current[10..16];
        byte[]? keyA = change.KeyA ?? (asKeyB ? null : openedWith);
        byte[]? keyB = change.KeyB ?? (asKeyB ? openedWith : readableB.Any(b => b != 0) ? readableB : null);
        if (keyA is null || keyB is null)
            return (false, $"Sector {sector} did not take its change: key {(keyA is null ? 'A' : 'B')} could not be kept " +
                           $"(open the sector with key {(keyA is null ? 'A' : 'B')}, or set it too). Nothing was written to this sector.", openedWith);
        byte[] keyNow = change.KeyA ?? change.KeyB ?? openedWith;

        try
        {
            classic.WriteBlock(trailer, MifareClassicKeys.TrailerFor(keyA, keyB, current, change.Rules));
        }
        catch
        {
            try { classic.Close(); classic.Connect(); classic.Timeout = 1000; } catch { }
            return (false, $"Sector {sector} did not take its change: the card refused the write (its rules may not let " +
                           $"key {(asKeyB ? 'B' : 'A')} change the trailer) or it moved away.", openedWith);
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
                    return (true, null, useB ? keyB : keyA);
            }
            catch { }
        }
        return (false, $"Sector {sector}: the trailer was written but the sector did not open afterwards. Hold the card still and read it again before trying once more.", keyNow);
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
#endif
}
