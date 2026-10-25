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

    private static (bool Ok, string Note) WriteSegments(Tag tag, SegmentWrite write)
    {
        if (write.Writes.Count == 0) return (false, "Nothing to write.");
        if (write.WrongTag(ToHex(tag.GetId())) is { } wrongTag) return (false, wrongTag);
        foreach (var segment in write.Writes.Keys)
            if (write.Layout.WhyNotWritable(segment) is { } why)
                return (false, $"{Pluralise(1, write.Layout.Noun)} {segment} {why}, so it is never written. Nothing was written.");

        return write.Layout.Family switch
        {
            TagFamily.Type2 => WriteType2Segments(tag, write),
            TagFamily.Classic => WriteClassicSegments(tag, write),
            _ => WriteIso15693Segments(tag, write),
        };
    }

    // ------------------------------------------------------------------------------------------------ Type 2

    private static (bool Ok, string Note) WriteType2Segments(Tag tag, SegmentWrite write)
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

        foreach (var (page, data) in write.Writes.OrderBy(p => p.Key))
        {
            if (SamePage(before, page, data)) { same++; continue; }
            if (locked[page]) { lockedPages.Add(page); continue; }
            if (!WritePageWithRetry(link, page, data, pwd, ref authed, out var error))
            {
                if (link.Read(page) is not null) { lockedPages.Add(page); locked[page] = true; continue; }
                return (false, $"Write failed at page {page} ({error}) after {written} pages: the tag moved away. Hold it still and try again.");
            }
            written++;
        }

        var after = ReadMemory(link, chip);
        var wrong = write.Writes.Keys.Where(p => !locked[p] && !SamePage(after, p, write.Writes[p])).Order().ToList();
        return Result(write.Layout, written, same, wrong, lockedPages, "locked for good and kept their old content", notes);
    }

    // ------------------------------------------------------------------------------------------------ MIFARE Classic

    /// <summary>Writes data blocks sector by sector. Each sector is opened with the key the person entered or a public
    /// default, as key A and then key B (a sector's access bits often let only one of them write), and each block is
    /// read back after writing. A sector that no key opens, or that refuses the write, is reported and left as it was.</summary>
    private static (bool Ok, string Note) WriteClassicSegments(Tag tag, SegmentWrite write)
    {
        using var classic = MifareClassic.Get(tag);
        if (classic is null) return (false, $"This is not a MIFARE Classic card, but the edit is for {write.Layout.ChipName}. Nothing was written.");

        classic.Connect();
        try
        {
            classic.Timeout = 1000;
            var actual = new SegmentLayout(TagFamily.Classic, MifareClassicKeys.ChipName(classic.Size), 16, classic.BlockCount);
            if (write.WrongChip(actual) is { } wrongChip) return (false, wrongChip);

            var candidates = _manualKey is { } manual
                ? MifareClassicKeys.Defaults.Select(k => k.Key).Prepend(manual).ToList()
                : MifareClassicKeys.Defaults.Select(k => k.Key).ToList();

            int written = 0, same = 0;
            var refused = new List<int>();
            foreach (var sector in write.Writes.GroupBy(p => MifareClassicKeys.SectorOf(p.Key)).OrderBy(g => g.Key))
            {
                var pending = sector.OrderBy(p => p.Key).ToList();
                foreach (var key in candidates)
                {
                    foreach (bool useB in new[] { false, true })
                    {
                        if (pending.Count == 0) break;
                        try
                        {
                            bool opened = useB ? classic.AuthenticateSectorWithKeyB(sector.Key, key)
                                               : classic.AuthenticateSectorWithKeyA(sector.Key, key);
                            if (!opened) continue;
                            while (pending.Count > 0)
                            {
                                var (block, data) = pending[0];
                                var current = classic.ReadBlock(block);
                                if (current is { Length: >= 16 } && current.AsSpan(0, 16).SequenceEqual(data)) { same++; pending.RemoveAt(0); continue; }
                                classic.WriteBlock(block, data);
                                var check = classic.ReadBlock(block);
                                if (check is not { Length: >= 16 } || !check.AsSpan(0, 16).SequenceEqual(data)) break;
                                written++;
                                pending.RemoveAt(0);
                            }
                        }
                        catch
                        {
                            // Refused (this key may not write here) or a failed read: the card drops the session; open it again.
                            try { classic.Close(); classic.Connect(); classic.Timeout = 1000; }
                            catch { return (false, $"The card moved away after {written} blocks. Hold it still and try again."); }
                        }
                    }
                    if (pending.Count == 0) break;
                }
                refused.AddRange(pending.Select(p => p.Key));
            }

            return Result(write.Layout, written, same, [], refused,
                "were not changed: no known key opened their sector, or the sector's access bits refuse writing", []);
        }
        finally { try { classic.Close(); } catch { } }
    }

    // ------------------------------------------------------------------------------------------------ ISO 15693

    private static (bool Ok, string Note) WriteIso15693Segments(Tag tag, SegmentWrite write)
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
            foreach (var (block, data) in write.Writes.OrderBy(p => p.Key))
            {
                var current = Iso15693ReadBlock(nfcV, uid, block);
                if (current is not null && current.AsSpan().SequenceEqual(data)) { same++; continue; }
                if (Iso15693WriteBlock(nfcV, uid, block, data) && Iso15693ReadBlock(nfcV, uid, block) is { } check
                    && check.AsSpan().SequenceEqual(data)) { written++; continue; }
                // Refused but still answering: a locked or protected block. Gone: it moved away.
                if (Iso15693ReadBlock(nfcV, uid, block) is null && current is null)
                    return (false, $"Write failed at block {block} after {written} blocks: the label moved away. Hold it still and try again.");
                refused.Add(block);
            }
            return Result(write.Layout, written, same, [], refused, "refused the write (locked or protected) and kept their old content", []);
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
#endif
}
