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
    /// one-byte address reaches).</summary>
    public int LastWritable => Family switch
    {
        TagFamily.Type2 => UserEnd - 1,
        TagFamily.Classic => Count - 2,                  // the last block is the last sector's trailer
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
public sealed record SegmentRead(SegmentLayout Layout, IReadOnlyDictionary<int, byte[]> Data, IReadOnlyDictionary<int, string> Blocked);

/// <summary>An armed segment write: for each segment the bytes to put there, where null keeps the byte the tag has
/// (read just before writing), and the tag they are meant for. Built from a <see cref="TagEdit"/>, checked against the
/// tag held to the phone before anything is written.</summary>
public sealed record SegmentWrite(SegmentLayout Layout, string? ExpectedId, IReadOnlyDictionary<int, byte?[]> Writes)
{
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
            return $"{Layout.Noun}{(Writes.Count == 1 ? "" : "s")} {Type2Memory.Ranges(Writes.Keys.Order().ToList())} " +
                   $"({bytes} byte{(bytes == 1 ? "" : "s")})";
        }
    }

    /// <summary>"an" before a vowel sound: a vowel, or NTAG ("en-tag").</summary>
    private static string Article(string name) =>
        name.StartsWith("NTAG", StringComparison.OrdinalIgnoreCase) || (name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(name[0]))) ? "an" : "a";
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
/// <para>Each run of typing or deleting is one step for <see cref="Undo"/> and <see cref="Redo"/>; placing the cursor
/// elsewhere starts a new one.</para></summary>
public sealed class TagEdit
{
    /// <summary>What the tag held, per segment (null bytes: not known). Every segment the read reached, writable or not.</summary>
    private readonly Dictionary<int, byte?[]> _original;
    private readonly Dictionary<int, string> _blocked;

    /// <summary>The bytes typed, per segment (null: not typed, the tag's byte stays).</summary>
    private Dictionary<int, byte?[]> _edits = [];

    private readonly Stack<Snapshot> _undo = new(), _redo = new();
    private EditKind? _run;

    private enum EditKind { Type, Delete }
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

    private TagEdit(SegmentLayout layout, Dictionary<int, byte?[]> original, Dictionary<int, string> blocked, bool fromRead)
    {
        Layout = layout;
        _original = original;
        _blocked = blocked;
        FromRead = fromRead;
        From = layout.FirstWritable;
        To = layout.Count > 0 || layout.Family != TagFamily.Iso15693 ? layout.LastWritable : layout.FirstWritable + 15;
    }

    /// <summary>An edit of the tag just read, expecting that same tag (its serial and chip).</summary>
    public static TagEdit FromTag(SegmentRead read, string id) =>
        new(read.Layout,
            read.Data.Where(p => p.Value.Length == read.Layout.SegmentSize)
                     .ToDictionary(p => p.Key, p => p.Value.Select(b => (byte?)b).ToArray()),
            read.Blocked.ToDictionary(p => p.Key, p => p.Value), fromRead: true)
        { ExpectedId = id };

    /// <summary>An empty edit for a tag type chosen by hand: every byte unknown, nothing written until typed.</summary>
    public static TagEdit Blank(SegmentLayout layout) => new(layout, [], [], fromRead: false);

    public string? WhyNotWritable(int segment) =>
        Layout.WhyNotWritable(segment) ?? (_blocked.TryGetValue(segment, out var why) ? why : null);

    /// <summary>The writable segments in the range, in order: together they make the run the cursor moves over.</summary>
    public IReadOnlyList<int> Segments =>
        Enumerable.Range(From, Math.Max(0, To - From + 1)).Where(s => WhyNotWritable(s) is null).ToList();

    /// <summary>How many writable bytes the range has.</summary>
    public int Length => Segments.Count * Layout.SegmentSize;

    /// <summary>Limits the edit to segments <paramref name="from"/>-<paramref name="to"/>. Returns why not, or null.</summary>
    public string? SetRange(int from, int to)
    {
        int first = Layout.FirstWritable, last = Layout.LastWritable;
        if (from > to) return $"The range starts after it ends ({from}-{to}).";
        if (from < first || to > last)
            return $"{Capital(Layout.Noun)}s {first}-{last} can be edited on {(Layout.Family == TagFamily.Iso15693 && Layout.Count == 0 ? "a label" : "this tag")}.";
        From = from;
        To = to;
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
    public (int Segment, int Index) At(int position)
    {
        var segments = Segments;
        int size = Layout.SegmentSize;
        position = Math.Clamp(position, 0, Math.Max(0, segments.Count * size - 1));
        return (segments.Count == 0 ? From : segments[position / size], position % size);
    }

    /// <summary>The run position of a byte, or -1 when its segment is not writable or not in the range.</summary>
    public int PositionOf(int segment, int index)
    {
        int k = Segments.ToList().IndexOf(segment);
        return k < 0 ? -1 : k * Layout.SegmentSize + Math.Clamp(index, 0, Layout.SegmentSize - 1);
    }

    /// <summary>The segments to write, with the typed bytes (null for those kept as the tag has them).</summary>
    public IReadOnlyDictionary<int, byte?[]> Changes =>
        Segments.Where(s => _edits.TryGetValue(s, out var e) && e.Any(b => b is not null))
                .ToDictionary(s => s, s => _edits[s].ToArray());

    public SegmentWrite ToWrite() => new(Layout, ExpectedId, Changes);

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
        _run = null;
    }

    // ------------------------------------------------------------------------------------------------ cursor and typing

    /// <summary>Puts the cursor on a byte of the run, in the hex or the text column. Starts a new undo step.</summary>
    public void Place(int position, bool inText, int nibble = 0)
    {
        Cursor = Math.Clamp(position, 0, Length);
        InText = inText;
        Nibble = inText ? 0 : Math.Clamp(nibble, 0, 1);
        _run = null;
    }

    /// <summary>Moves the cursor by a number of bytes (a row is one segment).</summary>
    public void Move(int bytes) => Place(Math.Clamp(Cursor + bytes, 0, Math.Max(0, Length - 1)), InText);

    /// <summary>Types one hex digit over the cursor's nibble and moves on. False when it is not a hex digit or the
    /// cursor is past the end of the range.</summary>
    public bool TypeHex(char digit)
    {
        if (!Uri.IsHexDigit(digit) || Cursor >= Length) return false;
        Begin(EditKind.Type);
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
        Begin(EditKind.Type);
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
        Begin(EditKind.Delete);
        var (segment, index) = At(Cursor);
        Clear(segment, index);
        Nibble = 0;
    }

    /// <summary>Puts every byte back as the tag had it. One undo step.</summary>
    public void RevertAll()
    {
        if (_edits.Count == 0) return;
        _run = null;
        Begin(EditKind.Delete);
        _edits.Clear();
        _run = null;
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
        _run = null;
        return true;
    }

    /// <summary>Starts an undo step unless this edit continues the current run of the same kind.</summary>
    private void Begin(EditKind kind)
    {
        if (_run == kind) return;
        _undo.Push(Take());
        _redo.Clear();
        _run = kind;
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
    }

    private void Clear(int segment, int index)
    {
        if (!_edits.TryGetValue(segment, out var e)) return;
        e[index] = null;
        if (e.All(b => b is null)) _edits.Remove(segment);
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
