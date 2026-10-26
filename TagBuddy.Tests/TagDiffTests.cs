using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Comparing two reads: only tags of the same layout, only the segments that differ, byte by byte.</summary>
public class TagDiffTests
{
    private static NfcTagInfo Ntag(string id, Action<Dictionary<int, byte[]>>? change = null)
    {
        var data = Enumerable.Range(0, 45).ToDictionary(p => p, p => new byte[] { (byte)p, 0, 0, 0 });
        change?.Invoke(data);
        return new NfcTagInfo(id, ["NfcA"], [], [], Segments: new SegmentRead(SegmentLayout.Known[0], data, new Dictionary<int, string>()));
    }

    [Fact]
    public void Identical_reads_have_no_differences()
    {
        var diff = TagDiff.Compare(Ntag("04:01"), Ntag("04:01"), out var why);
        Assert.Null(why);
        Assert.Empty(diff!.Differences);
        Assert.Equal(45, diff.Compared);
        Assert.StartsWith("The same", diff.Summary);
        Assert.True(diff.SameTag);
    }

    [Fact]
    public void Only_the_segments_that_differ_are_listed_with_their_changed_bytes()
    {
        var right = Ntag("04:02", d => { d[5] = [5, 0, 0xFF, 0]; d[9] = [0, 0, 0, 0]; });
        var diff = TagDiff.Compare(Ntag("04:01"), right, out _)!;

        Assert.Equal([5, 9], diff.Differences.Select(d => d.Segment));
        Assert.Equal([false, false, true, false], diff.Differences[0].Changed);
        Assert.Equal([true, false, false, false], diff.Differences[1].Changed);
        Assert.Equal("2 of 45 pages differ (2 bytes).", diff.Summary);
        Assert.False(diff.SameTag);
        Assert.Equal("005  05 00 00 00 | 05 00 FF 00", diff.Lines().Skip(1).First());
    }

    [Fact]
    public void A_segment_only_one_side_read_counts_as_different()
    {
        var right = Ntag("04:02", d => d.Remove(40));
        var diff = TagDiff.Compare(Ntag("04:01"), right, out _)!;
        var d = Assert.Single(diff.Differences);
        Assert.Equal(40, d.Segment);
        Assert.Null(d.Right);
        Assert.All(d.Changed, c => Assert.True(c));
        Assert.Contains("(not read)", diff.Lines().Last());
    }

    [Fact]
    public void Different_layouts_do_not_compare()
    {
        var classic = new NfcTagInfo("04:03", ["NfcA"], [], [],
            Segments: new SegmentRead(SegmentLayout.Known[5], new Dictionary<int, byte[]>(), new Dictionary<int, string>()));
        Assert.Null(TagDiff.Compare(Ntag("04:01"), classic, out var why));
        Assert.Contains("different layouts", why);
        Assert.Null(TagDiff.Compare(Ntag("04:01"), new NfcTagInfo("04:04", [], [], []), out why));
        Assert.Contains("no readable segments", why);
    }
}
