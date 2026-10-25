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

/// <summary>An armed segment write: the bytes per segment, and the tag they are meant for. Built from a
/// <see cref="TagEdit"/>, checked against the tag held to the phone before anything is written.</summary>
public sealed record SegmentWrite(SegmentLayout Layout, string? ExpectedId, IReadOnlyDictionary<int, byte[]> Writes)
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

    /// <summary>"pages 5-6, 9 (12 bytes)": what will be written, for the status line.</summary>
    public string Summary =>
        $"{Layout.Noun}{(Writes.Count == 1 ? "" : "s")} {Type2Memory.Ranges(Writes.Keys.Order().ToList())} ({Writes.Count * Layout.SegmentSize} bytes)";

    /// <summary>"an" before a vowel sound: a vowel, or NTAG ("en-tag").</summary>
    private static string Article(string name) =>
        name.StartsWith("NTAG", StringComparison.OrdinalIgnoreCase) || (name.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(name[0]))) ? "an" : "a";
}

/// <summary>The segment editor's state: the tag it expects, the range of segments it covers, the bytes the person wants
/// in each, and (after a read) what the tag held, so that only what was changed is written.
///
/// <para>A segment without bytes is left as the tag has it. After a read every readable segment starts with the tag's
/// content; set up by hand, every segment starts empty and only those filled in are written.</para>
///
/// <para>Two views edit the same bytes. Hex: one line per segment, "005: 01 02 03 04". Text: one run of text laid over
/// the segments from the first one on, where \xHH is a byte in hex and \\ a backslash; shortening the text clears the
/// segments it no longer covers to zeros, up to where the text view started.</para></summary>
public sealed class TagEdit
{
    private readonly Dictionary<int, byte[]> _original;
    private readonly Dictionary<int, string> _blocked;
    private Dictionary<int, byte[]> _data;

    /// <summary>The bytes when the text view was opened; text typed since is laid over these.</summary>
    private Dictionary<int, byte[]>? _textBase;

    public SegmentLayout Layout { get; }

    /// <summary>The serial the tag must have, or null for any tag of the layout's type. Never written to the tag.</summary>
    public string? ExpectedId { get; set; }

    /// <summary>Whether the content came from reading a tag (only changes are written) rather than being set up by hand.</summary>
    public bool FromRead { get; }

    public int From { get; private set; }
    public int To { get; private set; }

    private TagEdit(SegmentLayout layout, Dictionary<int, byte[]> original, Dictionary<int, string> blocked, bool fromRead)
    {
        Layout = layout;
        _original = original;
        _blocked = blocked;
        _data = original.ToDictionary(p => p.Key, p => p.Value.ToArray());
        FromRead = fromRead;
        From = layout.FirstWritable;
        To = layout.Count > 0 || layout.Family != TagFamily.Iso15693 ? layout.LastWritable : layout.FirstWritable + 15;
    }

    /// <summary>An edit of the tag just read, expecting that same tag (its serial and chip).</summary>
    public static TagEdit FromTag(SegmentRead read, string id) =>
        new(read.Layout,
            read.Data.Where(p => p.Value.Length == read.Layout.SegmentSize && read.Layout.WhyNotWritable(p.Key) is null)
                     .ToDictionary(p => p.Key, p => p.Value.ToArray()),
            read.Blocked.ToDictionary(p => p.Key, p => p.Value), fromRead: true)
        { ExpectedId = id };

    /// <summary>An empty edit for a tag type chosen by hand: nothing is written until segments are filled in.</summary>
    public static TagEdit Blank(SegmentLayout layout) => new(layout, [], [], fromRead: false);

    public string? WhyNotWritable(int segment) =>
        Layout.WhyNotWritable(segment) ?? (_blocked.TryGetValue(segment, out var why) ? why : null);

    /// <summary>The writable segments in the range, in order.</summary>
    public IReadOnlyList<int> Segments =>
        Enumerable.Range(From, Math.Max(0, To - From + 1)).Where(s => WhyNotWritable(s) is null).ToList();

    /// <summary>Limits the edit to segments <paramref name="from"/>-<paramref name="to"/>. Returns why not, or null.</summary>
    public string? SetRange(int from, int to)
    {
        int first = Layout.FirstWritable, last = Layout.LastWritable;
        if (from > to) return $"The range starts after it ends ({from}-{to}).";
        if (from < first || to > last)
            return $"{Capital(Layout.Noun)}s {first}-{last} can be edited on {(Layout.Family == TagFamily.Iso15693 && Layout.Count == 0 ? "a label" : "this tag")}.";
        From = from;
        To = to;
        _textBase = null;
        return null;
    }

    /// <summary>The segments to write: those in the range with bytes that the tag (as read) does not already have.</summary>
    public IReadOnlyDictionary<int, byte[]> Changes =>
        Segments.Where(s => _data.TryGetValue(s, out var d) && !(_original.TryGetValue(s, out var o) && o.AsSpan().SequenceEqual(d)))
                .ToDictionary(s => s, s => _data[s].ToArray());

    public SegmentWrite ToWrite() => new(Layout, ExpectedId, Changes);

    /// <summary>After a successful write: the tag now holds these bytes, so they are no longer changes.</summary>
    public void Commit(IReadOnlyDictionary<int, byte[]> written)
    {
        foreach (var (segment, data) in written) _original[segment] = data.ToArray();
    }

    /// <summary>The bytes the editor holds for a segment, or null when it is left as the tag has it.</summary>
    public byte[]? DataFor(int segment) => _data.TryGetValue(segment, out var d) ? d.ToArray() : null;

    // ------------------------------------------------------------------------------------------------ hex view

    /// <summary>One line per writable segment in the range: "005: 01 02 03 04", or "005:" when left as it is.</summary>
    public string FormatHex() =>
        string.Join("\n", Segments.Select(s =>
            _data.TryGetValue(s, out var d) ? $"{s:D3}: {string.Join(" ", d.Select(b => b.ToString("X2")))}" : $"{s:D3}:"));

    /// <summary>Takes the hex view's lines as the new content of the range. A segment without a line, or with no bytes
    /// after its number, is left as the tag has it. Returns what is wrong (nothing changes then), or null.</summary>
    public string? ApplyHex(string? text)
    {
        var next = _data.Where(p => p.Key < From || p.Key > To).ToDictionary(p => p.Key, p => p.Value);
        var seen = new HashSet<int>();
        var lines = (text ?? "").Replace("\r", "").Split('\n');
        string noun = Capital(Layout.Noun);

        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0 || !int.TryParse(line[..colon].Trim(), out int segment))
                return $"Line {n + 1}: start it with the {Layout.Noun} number, like \"{From:D3}: …\".";
            if (WhyNotWritable(segment) is { } why) return $"{noun} {segment} {why}, so it cannot be edited.";
            if (segment < From || segment > To) return $"{noun} {segment} is outside the range {From}-{To}.";
            if (!seen.Add(segment)) return $"{noun} {segment} is listed twice.";

            var rest = line[(colon + 1)..];
            if (rest.Any(c => !Uri.IsHexDigit(c) && c is not (' ' or ':' or '-' or '\t')))
                return $"{noun} {segment}: only hex digits (0-9, A-F) can follow the number.";
            var hex = new string(rest.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length == 0) continue;                       // no bytes: leave the segment as it is
            if (hex.Length != Layout.SegmentSize * 2)
                return $"{noun} {segment} needs {Layout.SegmentSize} bytes ({Layout.SegmentSize * 2} hex digits); it has {hex.Length} digit{(hex.Length == 1 ? "" : "s")}.";
            next[segment] = Convert.FromHexString(hex);
        }

        _data = next;
        _textBase = null;
        return null;
    }

    // ------------------------------------------------------------------------------------------------ text view

    /// <summary>The range as one run of text: the segments from the first on that hold bytes, up to the first one
    /// left empty, with trailing zeros dropped. Opening the text view starts from here.</summary>
    public string FormatText()
    {
        _textBase = _data.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var bytes = LeadingRun(_textBase).SelectMany(s => _textBase[s]).ToArray();
        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return Escape(bytes.AsSpan(0, end));
    }

    /// <summary>Lays the text over the segments from the first one on: each segment it reaches gets its bytes (the last
    /// one padded with zeros), and the segments the text view started with but the text no longer reaches are cleared
    /// to zeros. Segments beyond both stay as they are. Returns what is wrong (nothing changes then), or null.</summary>
    public string? ApplyText(string? text)
    {
        _textBase ??= _data.ToDictionary(p => p.Key, p => p.Value.ToArray());
        if (!TryUnescape(text ?? "", out var bytes, out var error)) return error;

        var segments = Segments;
        int size = Layout.SegmentSize;
        int capacity = segments.Count * size;
        if (bytes.Length > capacity)
            return $"The text needs {bytes.Length} bytes, but {Layout.Noun}s {From}-{To} hold {capacity}.";

        int covered = Math.Max((bytes.Length + size - 1) / size, LeadingRun(_textBase).Count);
        var next = _textBase.ToDictionary(p => p.Key, p => p.Value.ToArray());
        for (int k = 0; k < covered; k++)
        {
            var chunk = new byte[size];
            if (k * size < bytes.Length) Array.Copy(bytes, k * size, chunk, 0, Math.Min(size, bytes.Length - k * size));
            next[segments[k]] = chunk;
        }
        _data = next;
        return null;
    }

    /// <summary>The writable segments in the range, from the first, that hold bytes, up to the first left empty.</summary>
    private List<int> LeadingRun(Dictionary<int, byte[]> data) => Segments.TakeWhile(data.ContainsKey).ToList();

    /// <summary>Bytes as editable text: printable ASCII and UTF-8 as themselves, a newline as a line break, a backslash
    /// as \\ and any other byte as \xHH, so that <see cref="TryUnescape"/> gives back exactly the same bytes.</summary>
    public static string Escape(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length);
        for (int i = 0; i < bytes.Length;)
        {
            byte b = bytes[i];
            if (b == (byte)'\\') { text.Append(@"\\"); i++; continue; }
            if (b == 0x0A || b is >= 0x20 and < 0x7F) { text.Append((char)b); i++; continue; }
            if (b >= 0x80 && System.Text.Rune.DecodeFromUtf8(bytes[i..], out var rune, out int used) == System.Buffers.OperationStatus.Done)
            {
                text.Append(rune.ToString());
                i += used;
                continue;
            }
            text.Append($"\\x{b:X2}");
            i++;
        }
        return text.ToString();
    }

    /// <summary>The bytes text in the editor stands for: UTF-8, with \xHH a byte in hex and \\ a backslash.</summary>
    public static bool TryUnescape(string text, out byte[] bytes, out string? error)
    {
        var result = new List<byte>(text.Length);
        error = null;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                int length = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
                result.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, length)));
                i += length - 1;
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == '\\') { result.Add((byte)'\\'); i++; continue; }
            if (i + 3 < text.Length && text[i + 1] == 'x' && Uri.IsHexDigit(text[i + 2]) && Uri.IsHexDigit(text[i + 3]))
            {
                result.Add(Convert.ToByte(text.Substring(i + 2, 2), 16));
                i += 3;
                continue;
            }
            bytes = [];
            error = @"A backslash starts \xHH (one byte in hex, like \x00) or \\ (a backslash itself).";
            return false;
        }
        bytes = result.ToArray();
        return true;
    }

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
