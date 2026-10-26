namespace TagBuddy.Services;

/// <summary>One segment that differs between two reads of tags with the same layout: its bytes on each side (null
/// where that side could not read it), and which bytes differ.</summary>
public sealed record SegmentDiff(int Segment, byte[]? Left, byte[]? Right)
{
    /// <summary>Per byte, whether the two sides differ (true where only one side has the segment).</summary>
    public bool[] Changed
    {
        get
        {
            int n = Math.Max(Left?.Length ?? 0, Right?.Length ?? 0);
            var changed = new bool[n];
            for (int i = 0; i < n; i++)
                changed[i] = Left is null || Right is null || i >= Left.Length || i >= Right.Length || Left[i] != Right[i];
            return changed;
        }
    }

    public int ChangedBytes => Changed.Count(c => c);
}

/// <summary>Two reads of the same kind of tag, segment by segment: what differs, and nothing else. Only tags of the
/// same layout (chip, segment size) compare; anything else has no segments in common to speak of.</summary>
public sealed record TagDiff(NfcTagInfo Left, NfcTagInfo Right, IReadOnlyList<SegmentDiff> Differences, int Compared)
{
    public SegmentLayout Layout => Left.Segments!.Layout;

    public bool SameTag => TagEdit.SameId(Left.Id, Right.Id);

    /// <summary>Compares two reads, or returns null with why not when their layouts differ (or one has no segments).</summary>
    public static TagDiff? Compare(NfcTagInfo left, NfcTagInfo right, out string? whyNot)
    {
        whyNot = null;
        if (left.Segments is not { } a || right.Segments is not { } b)
        {
            whyNot = "One of the tags has no readable segments to compare.";
            return null;
        }
        if (!a.Layout.Matches(b.Layout))
        {
            whyNot = $"These tags have different layouts ({a.Layout.ChipName}, {a.Layout.SegmentSize}-byte {a.Layout.Noun}s, " +
                     $"against {b.Layout.ChipName}, {b.Layout.SegmentSize}-byte {b.Layout.Noun}s), so there is nothing to compare {a.Layout.Noun} by {a.Layout.Noun}.";
            return null;
        }

        var segments = a.Data.Keys.Union(b.Data.Keys).Order().ToList();
        var differences = new List<SegmentDiff>();
        foreach (int s in segments)
        {
            a.Data.TryGetValue(s, out var l);
            b.Data.TryGetValue(s, out var r);
            if (l is not null && r is not null && l.AsSpan().SequenceEqual(r)) continue;
            differences.Add(new SegmentDiff(s, l, r));
        }
        return new TagDiff(left, right, differences, segments.Count);
    }

    /// <summary>"3 of 45 pages differ (7 bytes)", or that they are the same.</summary>
    public string Summary
    {
        get
        {
            string noun = Layout.Noun;
            if (Differences.Count == 0) return $"The same: all {Compared} {noun}s match.";
            int bytes = Differences.Sum(d => d.ChangedBytes);
            return $"{Differences.Count} of {Compared} {noun}{(Compared == 1 ? "" : "s")} differ ({bytes} byte{(bytes == 1 ? "" : "s")}).";
        }
    }

    /// <summary>The differences as text lines for copying: "005  01 02 03 04 | 01 02 FF 04".</summary>
    public IEnumerable<string> Lines()
    {
        yield return $"{Left.Id} (left) against {Right.Id} (right): {Summary}";
        foreach (var d in Differences)
            yield return $"{d.Segment:D3}  {Hex(d.Left)} | {Hex(d.Right)}";
    }

    private static string Hex(byte[]? bytes) =>
        bytes is null ? "(not read)" : string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
