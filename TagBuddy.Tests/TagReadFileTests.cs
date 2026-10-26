using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>A tag read saved as a file comes back exactly as it was read.</summary>
public class TagReadFileTests
{
    [Fact]
    public void A_classic_read_round_trips_with_its_segments_and_opened_sectors()
    {
        var layout = SegmentLayout.Known.First(l => l.ChipName == "MIFARE Classic 1K");
        var read = new SegmentRead(layout,
            new Dictionary<int, byte[]> { [1] = [1, 2, 3], [4] = [4] },
            new Dictionary<int, string> { [8] = "is in a sector no known key opened" })
            { OpenedSectors = new HashSet<int> { 0, 1 } };
        var tag = new NfcTagInfo("04:A1:B2", ["NfcA", "MifareClassic"],
            [("Chip", "MIFARE Classic 1K, 16 sectors"), ("Access", "2 of 16 sectors opened")],
            [new NfcRecordInfo("Text (en)", "hello"), new NfcRecordInfo("Private text", "Encrypted", [9, 9, 9])],
            ["Sector 00 · key A", "  000  ..."], null, null, false, true, read);

        var back = TagReadFile.FromJson(TagReadFile.From(tag).ToJson())!.ToTag();

        Assert.Equal(tag.Id, back.Id);
        Assert.Equal(tag.Technologies, back.Technologies);
        Assert.Equal(tag.Details, back.Details);
        Assert.Equal(tag.Records.Select(r => (r.Kind, r.Text)), back.Records.Select(r => (r.Kind, r.Text)));
        Assert.Equal(new byte[] { 9, 9, 9 }, back.Records[1].Sealed);
        Assert.Equal(tag.MemoryDump, back.MemoryDump);
        Assert.True(back.NeedsClassicKey);
        Assert.Equal(layout, back.Segments!.Layout);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Segments.Data[1]);
        Assert.Equal("is in a sector no known key opened", back.Segments.Blocked[8]);
        Assert.Equal(new HashSet<int> { 0, 1 }, back.Segments.OpenedSectors);
    }

    [Fact]
    public void A_type2_read_keeps_its_raw_memory_and_can_edit()
    {
        var layout = SegmentLayout.Known[0];
        var memory = Enumerable.Range(0, 45 * 4).Select(i => (byte)i).ToArray();
        var data = Enumerable.Range(0, 45).ToDictionary(p => p, p => memory[(p * 4)..(p * 4 + 4)]);
        var tag = new NfcTagInfo("04:11", ["NfcA"], [], [], Type2Memory.FormatDump(memory), memory, "NTAG213",
            Segments: new SegmentRead(layout, data, new Dictionary<int, string>()));

        var back = TagReadFile.FromJson(TagReadFile.From(tag).ToJson())!.ToTag();
        Assert.Equal(memory, back.RawMemory);
        Assert.Equal("NTAG213", back.ChipName);
        Assert.True(back.CanEdit);
    }

    [Fact]
    public void Bad_json_gives_null() => Assert.Null(TagReadFile.FromJson("{not json"));
}
