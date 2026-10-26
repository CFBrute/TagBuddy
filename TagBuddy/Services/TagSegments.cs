using System.Text;

namespace TagBuddy.Services;

/// <summary>The kinds of tag whose memory the segment editor writes, each through its own commands.</summary>
public enum TagFamily { Type2, Classic, Iso15693 }

/// <summary>How one kind of tag divides its memory into segments (Type 2 pages, MIFARE Classic and ISO 15693 blocks),
/// and which of them the editor may write. <paramref name="Count"/> is the number of segments, 0 when unknown (an
/// ISO 15693 label before it is read); <paramref name="UserEnd"/> is a Type 2 chip's first page after user memory.</summary>
public sealed record SegmentLayout(TagFamily Family, string ChipName, int SegmentSize, int Count, int UserEnd = 0)
{
    /// <summary>The tag types the editor can be set up for without reading a tag first.</summary>
    public static readonly IReadOnlyList<SegmentLayout> Known =
    [
        new(TagFamily.Type2, "NTAG213", 4, 45, 40),
        new(TagFamily.Type2, "NTAG215", 4, 135, 130),
        new(TagFamily.Type2, "NTAG216", 4, 231, 226),
        new(TagFamily.Type2, "MIFARE Ultralight EV1 (48 bytes)", 4, 20, 16),
        new(TagFamily.Type2, "MIFARE Ultralight EV1 (128 bytes)", 4, 41, 36),
        new(TagFamily.Classic, "MIFARE Classic 1K", 16, 64),
        new(TagFamily.Classic, "MIFARE Classic 4K", 16, 256),
        new(TagFamily.Classic, "MIFARE Classic Mini", 16, 20),
        new(TagFamily.Iso15693, Iso15693Name, 4, 0),
    ];

    public const string Iso15693Name = "ISO 15693 label";

    /// <summary>The largest block an ISO 15693 label can have (the size field is 5 bits).</summary>
    public const int MaxIsoBlockSize = 32;

    public string Noun => Family == TagFamily.Type2 ? "page" : "block";

    /// <summary>The lowest segment the editor may write.</summary>
    public int FirstWritable => Family switch { TagFamily.Type2 => 4, TagFamily.Classic => 1, _ => 0 };

    /// <summary>The highest segment the editor may write (an ISO 15693 label of unknown size: the last block a
    /// one-byte address reaches). For MIFARE Classic this is the last sector's trailer: its keys can be typed over
    /// once the sector opened, so the range runs to the card's last block, as the read shows it.</summary>
    public int LastWritable => Family switch
    {
        TagFamily.Type2 => UserEnd - 1,
        TagFamily.Classic => Count - 1,
        _ => Count > 0 ? Count - 1 : 255,
    };

    /// <summary>Null when the segment holds data the editor may write, otherwise why it is left alone. The serial,
    /// one-time bits, lock bits, password settings and sector keys are never written, so an edit can neither lock a
    /// tag for good nor change how it is opened.</summary>
    public string? WhyNotWritable(int segment) => Family switch
    {
        _ when segment < 0 => "does not exist",
        TagFamily.Type2 => segment switch
        {
            0 or 1 => "holds the serial number",
            2 => "holds the end of the serial number and the lock bits",
            3 => "is page 3, which is one-time programmable",
            _ when segment >= UserEnd => "is a lock-bit or password-setting page",
            _ => null,
        },
        TagFamily.Classic => segment switch
        {
            _ when segment >= Count => "is past the end of this card",
            0 => "holds the serial number (manufacturer block)",
            _ when MifareClassicKeys.IsTrailer(segment) => "is a sector trailer (the sector's keys and access bits)",
            _ => null,
        },
        _ => segment > LastWritable ? "is past the end of this label" : null,
    };

    /// <summary>A short name for a segment that is never written, shown on its row in the editor ("trailer: keys").</summary>
    public string Role(int segment) => Family switch
    {
        TagFamily.Type2 => segment switch
        {
            0 or 1 => "serial",
            2 => "serial, lock bits",
            3 => "page 3, one-time",
            _ when segment == UserEnd => "lock bits",
            _ when segment >= UserEnd => "password settings",
            _ => "",
        },
        TagFamily.Classic => segment == 0 ? "serial" : MifareClassicKeys.IsTrailer(segment) ? "trailer (keys)" : "",
        _ => "",
    };

    /// <summary>Whether a tag found at write time is the kind this layout was set up for. ISO 15693 labels are told
    /// apart by their block size and count, not their name.</summary>
    public bool Matches(SegmentLayout actual) =>
        actual.Family == Family && actual.SegmentSize == SegmentSize && (Family == TagFamily.Iso15693
            ? Count == 0 || actual.Count == 0 || actual.Count == Count
            : string.Equals(actual.ChipName, ChipName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a read learned for the editor: the tag's layout, the content of each segment it could read, and the
/// segments it must not write (with why: a page locked for good, a sector no known key opened).</summary>
public sealed record SegmentRead(SegmentLayout Layout, IReadOnlyDictionary<int, byte[]> Data, IReadOnlyDictionary<int, string> Blocked)
{
    /// <summary>MIFARE Classic sectors a known key opened (a public default, one entered or remembered, or one from the
    /// dictionary). Knowing a sector's key is what lets the editor give it a new one.</summary>
    public IReadOnlySet<int> OpenedSectors { get; init; } = new HashSet<int>();
}

/// <summary>A change to a MIFARE Classic sector's trailer: a new key A, a new key B, new rules, or a mix; null for a
/// field keeps what the sector has. Setting key A and key B to the same value is a new key for both.</summary>
public sealed record SectorChange(byte[]? KeyA, byte[]? KeyB, SectorRules? Rules)
{
    public bool IsEmpty => KeyA is null && KeyB is null && Rules is null;

    /// <summary>Key A and key B are both set to the same value: one new key for the sector.</summary>
    public bool SameKey => KeyA is { } a && KeyB is { } b && a.AsSpan().SequenceEqual(b);
}

/// <summary>An armed segment write: for each segment the bytes to put there, where null keeps the byte the tag has
/// (read just before writing), and the tag they are meant for. Built from a <see cref="TagEdit"/>, checked against the
/// tag held to the phone before anything is written.</summary>
public sealed record SegmentWrite(SegmentLayout Layout, string? ExpectedId, IReadOnlyDictionary<int, byte?[]> Writes)
{
    /// <summary>MIFARE Classic sectors whose trailer is to change (a new key, new rules, or both), after their data is
    /// written. A sector takes a change when a known key opens it; what is not changed is kept as it is.</summary>
    public IReadOnlyDictionary<int, SectorChange> Sectors { get; init; } = new Dictionary<int, SectorChange>();

    public bool IsEmpty => Writes.Count == 0 && Sectors.Count == 0;

    /// <summary>Why the tag with this serial is not the one the edit is for, or null when it is (or any tag will do).</summary>
    public string? WrongTag(string id) =>
        ExpectedId is null || TagEdit.SameId(ExpectedId, id) ? null
            : $"This is tag {id}, but the edit is for tag {ExpectedId}. Nothing was written. " +
              "Hold the expected tag, or clear the expected serial to write any tag.";

    /// <summary>Why the tag found is not the kind the edit is for, or null when it is.</summary>
    public string? WrongChip(SegmentLayout actual)
    {
        if (Layout.Matches(actual)) return null;
        if (actual.Family == TagFamily.Iso15693 && Layout.Family == TagFamily.Iso15693 && actual.SegmentSize != Layout.SegmentSize)
            return $"This label stores {actual.SegmentSize} bytes per block, but the edit uses {Layout.SegmentSize}. " +
                   $"Set the bytes per block to {actual.SegmentSize}. Nothing was written.";
        if (actual.Family == TagFamily.Iso15693 && Layout.Family == TagFamily.Iso15693)
            return $"This label has {actual.Count} blocks, but the edit is for one with {Layout.Count}. Nothing was written.";
        return $"This is {Article(actual.ChipName)} {actual.ChipName}, but the edit is for {Article(Layout.ChipName)} {Layout.ChipName}. Nothing was written.";
    }

    /// <summary>The bytes to write to a segment: the edited bytes, and the tag's own <paramref name="current"/> bytes for
    /// the rest. Null when some bytes are to be kept but the segment could not be read to know them.</summary>
    public static byte[]? Merge(byte?[] wanted, ReadOnlySpan<byte> current)
    {
        var bytes = new byte[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            if (wanted[i] is { } b) bytes[i] = b;
            else if (i < current.Length) bytes[i] = current[i];
            else return null;
        }
        return bytes;
    }

    /// <summary>"pages 5-6, 9 (7 bytes)": the segments written and how many of their bytes were edited.</summary>
    public string Summary
    {
        get
        {
            int bytes = Writes.Values.Sum(w => w.Count(b => b is not null));
            var parts = new List<string>();
            if (Writes.Count > 0)
                parts.Add($"{Layout.Noun}{(Writes.Count == 1 ? "" : "s")} {Type2Memory.Ranges(Writes.Keys.Order().ToList())} " +
                          $"({bytes} byte{(bytes == 1 ? "" : "s")})");
            var keyed = Sectors.Where(s => s.Value.SameKey).Select(s => s.Key).Order().ToList();
            var aOnly = Sectors.Where(s => s.Value.KeyA is not null && !s.Value.SameKey).Select(s => s.Key).Order().ToList();
            var bOnly = Sectors.Where(s => s.Value.KeyB is not null && !s.Value.SameKey).Select(s => s.Key).Order().ToList();
            var ruled = Sectors.Where(s => s.Value.Rules is not null).Select(s => s.Key).Order().ToList();
            if (keyed.Count > 0) parts.Add($"a new key for sector{(keyed.Count == 1 ? "" : "s")} {Type2Memory.Ranges(keyed)}");
            if (aOnly.Count > 0) parts.Add($"key A for sector{(aOnly.Count == 1 ? "" : "s")} {Type2Memory.Ranges(aOnly)}");
            if (bOnly.Count > 0) parts.Add($"key B for sector{(bOnly.Count == 1 ? "" : "s")} {Type2Memory.Ranges(bOnly)}");
            if (ruled.Count > 0) parts.Add($"new rules for sector{(ruled.Count == 1 ? "" : "s")} {Type2Memory.Ranges(ruled)}");
            return string.Join(" and ", parts);
        }
    }

    /// <summary>"an" before a vowel sound: a vowel, or NTAG ("en-tag").</summary>
    private static string Article(string name) =>
        name.StartsWith("NTAG", StringComparison.OrdinalIgnoreCase) || (name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(name[0]))) ? "an" : "a";
}

/// <summary>What one segment write changed on a tag: for each segment written, the bytes it held just before and the
/// bytes it holds now. That is enough to undo the write on the tag (write <see cref="Before"/> back, to that same tag
/// only) and to redo it again.</summary>
public sealed record SegmentWriteRecord(string TagId, SegmentLayout Layout, IReadOnlyDictionary<int, byte[]> Before,
    IReadOnlyDictionary<int, byte[]> After)
{
    /// <summary>The tag's serial once the write is on it: <see cref="TagId"/>, unless the write gave a MIFARE Classic
    /// magic card a new UID in block 0 (its first 4 or 7 bytes, as long as the serial it had).</summary>
    public string IdAfter
    {
        get
        {
            if (Layout.Family != TagFamily.Classic || !After.TryGetValue(0, out var block0)) return TagId;
            var hex = new string(TagId.Where(Uri.IsHexDigit).ToArray());
            int length = hex.Length / 2;
            if (length is not (4 or 7) || block0.Length < length) return TagId;
            var id = string.Join(":", block0.Take(length).Select(b => b.ToString("X2")));
            return TagEdit.SameId(id, TagId) ? TagId : id;
        }
    }

    /// <summary>Puts the segments back as they were, on the tag they were written to (which, after a new UID, now
    /// answers to <see cref="IdAfter"/>).</summary>
    public SegmentWrite UndoWrite => new(Layout, IdAfter, Whole(Before));

    /// <summary>Writes the same change again, after it was undone.</summary>
    public SegmentWrite RedoWrite => new(Layout, TagId, Whole(After));

    /// <summary>"pages 5-6": which segments the write changed.</summary>
    public string Segments => $"{Layout.Noun}{(Before.Count == 1 ? "" : "s")} {Type2Memory.Ranges(Before.Keys.Order().ToList())}";

    private static Dictionary<int, byte?[]> Whole(IReadOnlyDictionary<int, byte[]> bytes) =>
        bytes.ToDictionary(p => p.Key, p => p.Value.Select(b => (byte?)b).ToArray());
}

/// <summary>The segment editor's state: the tag it expects, the range of segments it covers, what the tag held (after
/// a read), the bytes the person changed, and a cursor that types over them.
///
/// <para>Editing works like overwrite mode in a hex editor. The writable bytes of the range form one run, segment after
/// segment; the cursor sits on one of them, in the hex column (typing hex digits sets it a nibble at a time) or the
/// text column (typing characters sets their UTF-8 bytes), and moves on as it types, into the next segment. Backspace
/// steps back and puts the byte back as it was. A byte nobody typed is kept as the tag has it: after a read that is the
/// byte read, set up by hand it is unknown (shown as ··) and the tag's own byte is kept when writing.</para>
///
/// <para>Every keystroke (a hex digit, a character, a backspace) is one step for <see cref="Undo"/> and
/// <see cref="Redo"/>, as are a paste, a fill with zeros and a revert.</para></summary>
public sealed class TagEdit
{
    /// <summary>What the tag held, per segment (null bytes: not known). Every segment the read reached, writable or not.</summary>
    private readonly Dictionary<int, byte?[]> _original;
    private readonly Dictionary<int, string> _blocked;

    /// <summary>The bytes typed, per segment (null: not typed, the tag's byte stays).</summary>
    private Dictionary<int, byte?[]> _edits = [];

    private readonly HashSet<int> _openedSectors;

    /// <summary>Pending rule changes for MIFARE Classic sectors, by sector. Keys are edited as bytes in the trailer row
    /// (so they are part of the typing history); only the rules, picked not typed, live here.</summary>
    private readonly Dictionary<int, SectorRules> _sectorRules = [];

    /// <summary>The trailer block of a MIFARE Classic sector (its last block).</summary>
    public static int TrailerOf(int sector) => sector < 32 ? sector * 4 + 3 : 128 + (sector - 32) * 16 + 15;

    private readonly Stack<Snapshot> _undo = new(), _redo = new();
    private sealed record Snapshot(Dictionary<int, byte?[]> Edits, int Cursor, int Nibble, bool InText);

    public SegmentLayout Layout { get; }

    /// <summary>The serial the tag must have, or null for any tag of the layout's type. Never written to the tag.</summary>
    public string? ExpectedId { get; set; }

    /// <summary>Whether the content came from reading a tag rather than being set up by hand.</summary>
    public bool FromRead { get; }

    public int From { get; private set; }
    public int To { get; private set; }

    /// <summary>The cursor: a byte in the run of writable bytes (<see cref="Length"/> once past the last), which half of
    /// it the next hex digit sets (0 the high nibble, 1 the low), and whether typing goes to the text column.</summary>
    public int Cursor { get; private set; }
    public int Nibble { get; private set; }
    public bool InText { get; private set; }

    private TagEdit(SegmentLayout layout, Dictionary<int, byte?[]> original, Dictionary<int, string> blocked, bool fromRead,
        IEnumerable<int>? openedSectors = null)
    {
        Layout = layout;
        _original = original;
        _blocked = blocked;
        _openedSectors = [.. openedSectors ?? []];
        FromRead = fromRead;
        From = layout.FirstWritable;
        To = layout.Count > 0 || layout.Family != TagFamily.Iso15693 ? layout.LastWritable : layout.FirstWritable + 15;
        RecomputeSegments();
    }

    /// <summary>An edit of the tag just read, expecting that same tag (its serial and chip).</summary>
    public static TagEdit FromTag(SegmentRead read, string id) =>
        new(read.Layout,
            read.Data.Where(p => p.Value.Length == read.Layout.SegmentSize)
                     .ToDictionary(p => p.Key, p => p.Value.Select(b => (byte?)b).ToArray()),
            read.Blocked.ToDictionary(p => p.Key, p => p.Value), fromRead: true, read.OpenedSectors)
        { ExpectedId = id };

    /// <summary>An empty edit for a tag type chosen by hand: every byte unknown, nothing written until typed.</summary>
    public static TagEdit Blank(SegmentLayout layout) => new(layout, [], [], fromRead: false);

    /// <summary>Whether block 0 (the serial/manufacturer block) may be edited on this card. Off by default: it is only
    /// unlocked by hand, for a magic/clone card whose block 0 is writable. See <see cref="SetBlockZeroEditable"/>.</summary>
    public bool AllowBlockZero { get; private set; }

    /// <summary>Whether block 0 could be offered for editing at all: a MIFARE Classic card that was read and whose block 0
    /// was read (sector 0 opened). Whether the card actually lets block 0 be written is only known at write time.</summary>
    public bool CanEditBlockZero =>
        Layout.Family == TagFamily.Classic && FromRead &&
        _original.TryGetValue(0, out var o) && o.Any(b => b is not null) && Layout.WhyNotWritable(0) is not null;

    /// <summary>Unlocks (or locks) editing of block 0. Only a card that was read and opened block 0 can take it; turning it
    /// off drops any block-0 edits. On a normal card the write simply fails; on a magic/clone card it rewrites the UID.</summary>
    public void SetBlockZeroEditable(bool on)
    {
        if (on && !CanEditBlockZero) on = false;
        if (AllowBlockZero == on) return;
        AllowBlockZero = on;
        if (!on) _edits.Remove(0);
        RecomputeSegments();
        Place(Math.Clamp(Cursor, 0, Math.Max(0, Length - 1)), InText);
    }

    public string? WhyNotWritable(int segment)
    {
        if (_blocked.TryGetValue(segment, out var blocked)) return blocked;   // a page/sector the read could not reach
        if (AllowBlockZero && Layout.Family == TagFamily.Classic && segment == 0) return null;
        return Layout.WhyNotWritable(segment);
    }

    /// <summary>The writable segments in the range, in order: together they make the run the cursor moves over. Cached
    /// because the drawing, cursor maths and write all read it many times; it changes only when the range does, so it
    /// is rebuilt in the constructor and in <see cref="SetRange"/>, not on every read (that made the editor crawl).</summary>
    private List<int> _segments = [];
    private Dictionary<int, int> _segmentIndex = [];

    /// <summary>The run of writable bytes the cursor moves over, as (segment, index) cells. Usually every byte of each
    /// writable segment, but block 0's check byte is skipped: it is derived from the UID, never typed.</summary>
    private List<(int Segment, int Index)> _cells = [];
    private Dictionary<long, int> _cellPos = [];

    public IReadOnlyList<int> Segments => _segments;

    private static long CellKey(int segment, int index) => (long)segment * 64 + index;

    /// <summary>Whether a byte may be typed over. Most of a writable segment is, with MIFARE Classic exceptions: block 0's
    /// check byte (BCC) is derived from the UID, not typed; and a sector trailer, though "not writable" as a block, lets
    /// key A (bytes 0-5) and key B (bytes 10-15) be typed once the sector opened — its access bits (6-8) and spare byte
    /// (9) are set through the rule pickers, never as raw bytes.</summary>
    public bool WritableByte(int segment, int index)
    {
        if (Layout.Family == TagFamily.Classic)
        {
            if (AllowBlockZero && segment == 0) return index != 4;
            if (MifareClassicKeys.IsTrailer(segment) && CanChangeKey(MifareClassicKeys.SectorOf(segment)))
                return index < 6 || index >= 10;
        }
        return WhyNotWritable(segment) is null;
    }

    private void RecomputeSegments()
    {
        var segs = Enumerable.Range(From, Math.Max(0, To - From + 1))
            .Where(s => Enumerable.Range(0, Layout.SegmentSize).Any(i => WritableByte(s, i))).ToList();
        // Block 0 sits before the writable range (From is 1 for Classic); when unlocked it leads the run.
        if (AllowBlockZero && Layout.Family == TagFamily.Classic && From > 0 && WhyNotWritable(0) is null)
            segs.Insert(0, 0);
        _segments = segs;
        _segmentIndex = new Dictionary<int, int>(_segments.Count);
        for (int i = 0; i < _segments.Count; i++) _segmentIndex[_segments[i]] = i;

        _cells = [];
        _cellPos = new Dictionary<long, int>(_segments.Count * Layout.SegmentSize);
        foreach (var s in _segments)
            for (int i = 0; i < Layout.SegmentSize; i++)
                if (WritableByte(s, i)) { _cellPos[CellKey(s, i)] = _cells.Count; _cells.Add((s, i)); }
    }

    /// <summary>How many writable bytes the range has.</summary>
    public int Length => _cells.Count;

    /// <summary>Limits the edit to segments <paramref name="from"/>-<paramref name="to"/>. Returns why not, or null.</summary>
    public string? SetRange(int from, int to)
    {
        int first = Layout.FirstWritable, last = Layout.LastWritable;
        if (from > to) return $"The range starts after it ends ({from}-{to}).";
        if (from < first || to > last)
            return $"{Capital(Layout.Noun)}s {first}-{last} can be edited on {(Layout.Family == TagFamily.Iso15693 && Layout.Count == 0 ? "a label" : "this tag")}.";
        From = from;
        To = to;
        RecomputeSegments();
        Place(0, InText);
        return null;
    }

    // ------------------------------------------------------------------------------------------------ what it holds

    /// <summary>The byte shown for a segment: typed, else as the tag held it, else null (not known).</summary>
    public byte? ValueAt(int segment, int index) =>
        _edits.TryGetValue(segment, out var e) && e[index] is { } typed ? typed
            : _original.TryGetValue(segment, out var o) ? o[index] : null;

    /// <summary>Whether the byte was typed (and differs from what the tag held).</summary>
    public bool IsEdited(int segment, int index) => _edits.TryGetValue(segment, out var e) && e[index] is not null;

    /// <summary>A short name for a segment the editor leaves alone, for its row: what it holds, or why it is closed.</summary>
    public string RoleOf(int segment) =>
        Layout.Role(segment) is { Length: > 0 } role ? role
            : !_blocked.ContainsKey(segment) ? ""
            : Layout.Family == TagFamily.Classic ? "sector not opened" : "locked for good";

    /// <summary>The segment and byte at a position of the run.</summary>
    public (int Segment, int Index) At(int position) =>
        _cells.Count == 0 ? (From, 0) : _cells[Math.Clamp(position, 0, _cells.Count - 1)];

    /// <summary>The run position of a byte, or -1 when it is not a writable byte of the range (a locked segment, a byte
    /// outside the range, or block 0's derived check byte).</summary>
    public int PositionOf(int segment, int index) =>
        _cellPos.TryGetValue(CellKey(segment, Math.Clamp(index, 0, Layout.SegmentSize - 1)), out var k) ? k : -1;

    private bool IsTrailerSeg(int segment) => Layout.Family == TagFamily.Classic && MifareClassicKeys.IsTrailer(segment);

    /// <summary>The data segments to write, with the typed bytes (null for those kept as the tag has them). Trailers are
    /// left out: their key bytes are written through the trailer path (see <see cref="Sectors"/>), never as a data block,
    /// because key bytes read back as zeros and writing them straight would wipe the keys.</summary>
    public IReadOnlyDictionary<int, byte?[]> Changes =>
        Segments.Where(s => !IsTrailerSeg(s) && _edits.TryGetValue(s, out var e) && e.Any(b => b is not null))
                .ToDictionary(s => s, s => _edits[s].ToArray());

    public SegmentWrite ToWrite() => new(Layout, ExpectedId, Changes) { Sectors = Sectors.ToDictionary(p => p.Key, p => p.Value) };

    /// <summary>Whether anything would be written: typed data bytes, or a sector's new key(s) or rules.</summary>
    public bool HasChanges => Changes.Count > 0 || Sectors.Count > 0;

    // ------------------------------------------------------------------------------------------------ sector keys

    /// <summary>Whether a MIFARE Classic sector may be given a new key: the read opened it, so its key is known (a public
    /// default, or one the person entered, imported or had remembered from setting it before).</summary>
    public bool CanChangeKey(int sector) => Layout.Family == TagFamily.Classic && FromRead && _openedSectors.Contains(sector);

    /// <summary>Sets (or, with null, clears) a sector's key A — or, with <paramref name="keyB"/>, key B — as six bytes in
    /// the trailer row, exactly as typing them there would. Setting both A and B to the same value is a new key for the
    /// sector. False when the sector cannot be changed. One undo step.</summary>
    public bool SetSectorKey(int sector, bool keyB, byte[]? key)
    {
        if (!CanChangeKey(sector)) return false;
        int trailer = TrailerOf(sector), off = keyB ? 10 : 0;
        Begin();
        for (int i = 0; i < 6; i++)
            if (key is { Length: 6 }) Set(trailer, off + i, key[i]);
            else Clear(trailer, off + i);
        return true;
    }

    /// <summary>The six bytes pending for a sector's key A — or, with <paramref name="keyB"/>, key B — or null when none
    /// are typed (the key is kept as it is). Reads from the trailer row, so inline edits and the key fields agree.</summary>
    public byte[]? PendingKey(int sector, bool keyB)
    {
        int trailer = TrailerOf(sector), off = keyB ? 10 : 0;
        if (!_edits.TryGetValue(trailer, out var e) || !Enumerable.Range(off, 6).Any(i => e[i] is not null)) return null;
        return Enumerable.Range(off, 6).Select(i => ValueAt(trailer, i) ?? 0).ToArray();
    }

    /// <summary>Sets (or, with null, drops) new rules for a sector. False when the sector cannot take them.</summary>
    public bool SetSectorRules(int sector, SectorRules? rules)
    {
        if (!CanChangeKey(sector)) return false;
        if (rules is null) _sectorRules.Remove(sector); else _sectorRules[sector] = rules;
        return true;
    }

    /// <summary>The sectors whose keys are known (the read opened them), so their keys and rules can be changed.</summary>
    public IReadOnlyList<int> ChangeableSectors =>
        Layout.Family == TagFamily.Classic && FromRead ? _openedSectors.Order().ToList() : [];

    /// <summary>Sets the same rule on all three data groups of a sector, keeping its trailer rule. When that matches
    /// the sector's current rules, the change is dropped. False when the sector cannot be changed.</summary>
    public bool SetAllDataRules(int sector, int bits)
    {
        if (!CanChangeKey(sector)) return false;
        var basis = SectorChangeFor(sector)?.Rules ?? CurrentRules(sector) ?? SectorRules.Transport;
        var next = basis with { Data0 = bits, Data1 = bits, Data2 = bits };
        var read = CurrentRules(sector);
        return SetSectorRules(sector, read is not null && next == read ? null : next);
    }

    /// <summary>Sets the same data-block rule on every changeable sector at once. Returns how many sectors took it.</summary>
    public int SetAllSectorsDataRules(int bits) => ChangeableSectors.Count(s => SetAllDataRules(s, bits));

    /// <summary>Whether a sector's keys may be typed over: it opened, and its rules (as pending, else as read) still
    /// let the keys change. Unknown rules do not block it; the tag has the last word at write time.</summary>
    public bool KeysChangeable(int sector)
    {
        if (!CanChangeKey(sector)) return false;
        var rules = (_sectorRules.TryGetValue(sector, out var r) ? r : null) ?? CurrentRules(sector);
        return rules is null || MifareClassicKeys.KeysChangeable(rules.Trailer);
    }

    /// <summary>Sets key A and/or key B (null leaves that key as it is) on every sector whose keys can change, as if
    /// typed in each trailer row. One undo step. Returns how many sectors took them.</summary>
    public int SetAllSectorsKeys(byte[]? keyA, byte[]? keyB)
    {
        if (keyA is not { Length: 6 } && keyB is not { Length: 6 }) return 0;
        var sectors = ChangeableSectors.Where(KeysChangeable).ToList();
        if (sectors.Count == 0) return 0;
        Begin();
        foreach (var sector in sectors)
        {
            int trailer = TrailerOf(sector);
            for (int i = 0; i < 6; i++)
            {
                if (keyA is { Length: 6 }) Set(trailer, i, keyA[i]);
                if (keyB is { Length: 6 }) Set(trailer, 10 + i, keyB[i]);
            }
        }
        return sectors.Count;
    }

    /// <summary>The change waiting for a sector — new key A and/or key B (from the trailer row) and new rules — or null
    /// when nothing is pending for it.</summary>
    public SectorChange? SectorChangeFor(int sector)
    {
        var change = new SectorChange(PendingKey(sector, false), PendingKey(sector, true),
            _sectorRules.TryGetValue(sector, out var r) ? r : null);
        return change.IsEmpty ? null : change;
    }

    /// <summary>Every sector with a change waiting, built from the trailer-row key edits and the pending rules.</summary>
    public IReadOnlyDictionary<int, SectorChange> Sectors =>
        ChangeableSectors.Select(s => (s, c: SectorChangeFor(s))).Where(x => x.c is not null)
                         .ToDictionary(x => x.s, x => x.c!);

    /// <summary>The rules a sector's trailer holds as read, or null when it was not read or its bits are corrupt.</summary>
    public SectorRules? CurrentRules(int sector)
    {
        int trailer = sector < 32 ? sector * 4 + 3 : 128 + (sector - 32) * 16 + 15;
        return _original.TryGetValue(trailer, out var t) && t.All(b => b is not null)
            ? MifareClassicKeys.DecodeRules(t.Select(b => b!.Value).ToArray()) : null;
    }

    /// <summary>After the tag took a sector's trailer change: clear its pending rules and key edits (the key is known,
    /// so it can be changed again).</summary>
    public void SectorChanged(int sector)
    {
        _sectorRules.Remove(sector);
        int trailer = TrailerOf(sector);
        if (_edits.TryGetValue(trailer, out var e))
        {
            for (int i = 0; i < 6; i++) { e[i] = null; e[10 + i] = null; }
            if (e.All(b => b is null)) _edits.Remove(trailer);
        }
    }

    /// <summary>After a successful write: the tag now holds these bytes, so they are no longer changes. The history is
    /// cleared, since undoing would now mean changing the tag back.</summary>
    public void Commit(IReadOnlyDictionary<int, byte?[]> written)
    {
        foreach (var (segment, bytes) in written)
        {
            var original = _original.TryGetValue(segment, out var o) ? o : new byte?[Layout.SegmentSize];
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] is { } b) original[i] = b;
            _original[segment] = original;
            _edits.Remove(segment);
        }
        _undo.Clear();
        _redo.Clear();
    }

    // ------------------------------------------------------------------------------------------------ cursor and typing

    /// <summary>Puts the cursor on a byte of the run, in the hex or the text column.</summary>
    public void Place(int position, bool inText, int nibble = 0)
    {
        Cursor = Math.Clamp(position, 0, Length);
        InText = inText;
        Nibble = inText ? 0 : Math.Clamp(nibble, 0, 1);
    }

    /// <summary>Moves the cursor by a number of bytes (a row is one segment).</summary>
    public void Move(int bytes) => Place(Math.Clamp(Cursor + bytes, 0, Math.Max(0, Length - 1)), InText);

    /// <summary>Types one hex digit over the cursor's nibble and moves on. False when it is not a hex digit or the
    /// cursor is past the end of the range.</summary>
    public bool TypeHex(char digit)
    {
        if (!Uri.IsHexDigit(digit) || Cursor >= Length) return false;
        Begin();
        var (segment, index) = At(Cursor);
        int value = Convert.ToInt32(digit.ToString(), 16);
        int current = ValueAt(segment, index) ?? 0;
        Set(segment, index, (byte)(Nibble == 0 ? (value << 4) | (current & 0x0F) : (current & 0xF0) | value));
        if (Nibble == 0) Nibble = 1;
        else { Nibble = 0; Cursor++; }
        return true;
    }

    /// <summary>Types text over the bytes from the cursor on (UTF-8), moving on as it goes. Returns how many bytes it
    /// wrote, fewer than the text needs when the range ends first.</summary>
    public int TypeText(string text)
    {
        if (text.Length == 0 || Cursor >= Length) return 0;
        Begin();
        int written = 0;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (Cursor >= Length) break;
            var (segment, index) = At(Cursor);
            Set(segment, index, b);
            Cursor++;
            written++;
        }
        Nibble = 0;
        return written;
    }

    /// <summary>Steps back one byte and puts it back as the tag had it (in hex, half a typed byte is taken back first).</summary>
    public void Backspace()
    {
        if (!InText && Nibble == 1) { Nibble = 0; }           // the cursor is inside the byte: go back to its start
        else if (Cursor > 0) Cursor--;
        else return;
        Begin();
        var (segment, index) = At(Cursor);
        Clear(segment, index);
        Nibble = 0;
    }

    /// <summary>Puts the byte under the cursor back as the tag had it and steps one byte left, so pressing it again and
    /// again walks back over a run of changes wherever the cursor was put, unlike Undo, which goes by time. A byte
    /// already as the tag had it just steps left. One undo step when something changed.</summary>
    public void ResetAtCursor()
    {
        if (Length == 0) return;
        int at = Math.Min(Cursor, Length - 1);
        var (segment, index) = At(at);
        if (IsEdited(segment, index))
        {
            Begin();
            Clear(segment, index);
        }
        Cursor = Math.Max(0, at - 1);
        Nibble = 0;
    }

    /// <summary>Puts every byte back as the tag had it. One undo step.</summary>
    public void RevertAll()
    {
        bool hadRules = _sectorRules.Count > 0;
        _sectorRules.Clear();
        if (_edits.Count == 0) { if (hadRules) Begin(); return; }
        Begin();
        _edits.Clear();
    }

    /// <summary>Sets every byte of one writable segment, or (null) of every writable segment in the range, to
    /// <paramref name="value"/>: a quick way to clear a block or the whole tag to 00. One undo step.</summary>
    public void Fill(byte value, int? segment = null)
    {
        // "Everything" means the data blocks only: filling all segments leaves a MIFARE Classic card's block 0 (the UID)
        // and its sector trailers (the keys) alone, even when block 0 is unlocked. One chosen segment is filled as asked.
        List<int> targets = segment is { } one
            ? (_segmentIndex.ContainsKey(one) ? [one] : [])
            : _segments.Where(s => !(Layout.Family == TagFamily.Classic && s == 0) && !IsTrailerSeg(s)).ToList();
        if (!targets.Any(s => Enumerable.Range(0, Layout.SegmentSize).Any(i => WritableByte(s, i) && ValueAt(s, i) != value))) return;   // already so
        Begin();
        foreach (var s in targets)
            for (int i = 0; i < Layout.SegmentSize; i++) if (WritableByte(s, i)) Set(s, i, value);
    }

    /// <summary>Types the given bytes over whole segments, as if entered by hand (so they show as changes and are written
    /// like any other): the other side of a comparison, say. Segments that are not writable here, or whose bytes are not
    /// a whole segment, are skipped. One undo step. Returns how many segments were taken and how many skipped.</summary>
    public (int Applied, int Skipped) Apply(IReadOnlyDictionary<int, byte[]> bytes)
    {
        var usable = bytes.Where(p => _segmentIndex.ContainsKey(p.Key) && p.Value.Length == Layout.SegmentSize).ToList();
        int skipped = bytes.Count - usable.Count;
        if (usable.Count == 0) return (0, skipped);
        Begin();
        foreach (var (segment, data) in usable)
            for (int i = 0; i < data.Length; i++) if (WritableByte(segment, i)) Set(segment, i, data[i]);
        return (usable.Count, skipped);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Undo() => Step(_undo, _redo);
    public bool Redo() => Step(_redo, _undo);

    private bool Step(Stack<Snapshot> from, Stack<Snapshot> to)
    {
        if (from.Count == 0) return false;
        to.Push(Take());
        var s = from.Pop();
        _edits = Copy(s.Edits);
        Cursor = Math.Clamp(s.Cursor, 0, Length);
        Nibble = s.Nibble;
        InText = s.InText;
        return true;
    }

    /// <summary>Starts an undo step: what the edit is now, to come back to.</summary>
    private void Begin()
    {
        _undo.Push(Take());
        _redo.Clear();
    }

    private Snapshot Take() => new(Copy(_edits), Cursor, Nibble, InText);

    private static Dictionary<int, byte?[]> Copy(Dictionary<int, byte?[]> edits) =>
        edits.ToDictionary(p => p.Key, p => p.Value.ToArray());

    /// <summary>Sets a typed byte; typing what the tag already held is no change.</summary>
    private void Set(int segment, int index, byte value)
    {
        if (_original.TryGetValue(segment, out var o) && o[index] == value) { Clear(segment, index); return; }
        if (!_edits.TryGetValue(segment, out var e)) _edits[segment] = e = new byte?[Layout.SegmentSize];
        e[index] = value;
        if (segment == 0 && index < 4) DeriveBcc();   // a UID byte changed: keep the check byte valid
    }

    private void Clear(int segment, int index)
    {
        if (_edits.TryGetValue(segment, out var e))
        {
            e[index] = null;
            if (e.All(b => b is null)) _edits.Remove(segment);
        }
        if (segment == 0 && index < 4) DeriveBcc();
    }

    /// <summary>Block 0's check byte (BCC) is the XOR of the four UID bytes. It is never typed; whenever a UID byte
    /// changes while block 0 is editable, this recomputes it so the written block 0 always carries a valid check byte.</summary>
    private void DeriveBcc()
    {
        if (!AllowBlockZero || Layout.Family != TagFamily.Classic) return;
        byte bcc = 0;
        for (int i = 0; i < 4; i++)
        {
            if (ValueAt(0, i) is not { } b) return;   // UID not fully known: leave the check byte as read
            bcc ^= b;
        }
        byte? original = _original.TryGetValue(0, out var o) ? o[4] : null;
        if (original == bcc)                           // back to the card's own check byte: no edit needed
        {
            if (_edits.TryGetValue(0, out var e0)) { e0[4] = null; if (e0.All(x => x is null)) _edits.Remove(0); }
            return;
        }
        if (!_edits.TryGetValue(0, out var e)) _edits[0] = e = new byte?[Layout.SegmentSize];
        e[4] = bcc;
    }

    // ------------------------------------------------------------------------------------------------ serials

    /// <summary>Whether two serials are the same, however they are written (colons, spaces, case).</summary>
    public static bool SameId(string a, string b) => NormalizeId(a) == NormalizeId(b);

    /// <summary>A serial typed by hand in the app's form, "04:A1:B2", or null when it is not whole bytes of hex.</summary>
    public static string? ParseId(string? text)
    {
        var hex = NormalizeId(text ?? "");
        if (hex.Length == 0 || hex.Length % 2 != 0 || (text ?? "").Any(c => !Uri.IsHexDigit(c) && c is not (' ' or ':' or '-'))) return null;
        return string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    private static string NormalizeId(string id) => new string(id.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static string Capital(string word) => char.ToUpperInvariant(word[0]) + word[1..];
}
