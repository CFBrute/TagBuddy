using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The segment editor: which segments may be written on each kind of tag, the hex and text views, what
/// counts as a change, and the checks that keep a write to the tag it was meant for.</summary>
public class TagEditTests
{
    private static SegmentLayout Layout(string name) => SegmentLayout.Known.First(l => l.ChipName == name);

    private static readonly SegmentLayout Ntag213 = Layout("NTAG213");
    private static readonly SegmentLayout Classic1K = Layout("MIFARE Classic 1K");
    private static readonly SegmentLayout Iso = Layout(SegmentLayout.Iso15693Name);

    /// <summary>An NTAG213 as read: page n holds n, n, n, n.</summary>
    private static SegmentRead ReadNtag213(Dictionary<int, string>? blocked = null) =>
        new(Ntag213, Enumerable.Range(0, 45).ToDictionary(p => p, p => new[] { (byte)p, (byte)p, (byte)p, (byte)p }),
            blocked ?? []);

    // ------------------------------------------------------------------------------------------------ layouts

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(40)]   // dynamic lock bits
    [InlineData(41)]   // CFG0
    [InlineData(43)]   // password
    public void Type2_never_writes_the_serial_page_3_locks_or_password_pages(int page) =>
        Assert.NotNull(Ntag213.WhyNotWritable(page));

    [Fact]
    public void Type2_user_pages_are_writable()
    {
        Assert.Null(Ntag213.WhyNotWritable(4));
        Assert.Null(Ntag213.WhyNotWritable(39));
        Assert.Equal((4, 39), (Ntag213.FirstWritable, Ntag213.LastWritable));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(63, true)]
    [InlineData(127, true)]
    [InlineData(143, true)]    // 4K: 16-block sectors from block 128
    [InlineData(255, true)]
    [InlineData(4, false)]
    [InlineData(131, false)]
    public void Classic_trailers_are_found_in_both_sector_sizes(int block, bool trailer) =>
        Assert.Equal(trailer, MifareClassicKeys.IsTrailer(block));

    [Fact]
    public void Classic_never_writes_block_0_or_a_trailer()
    {
        Assert.NotNull(Classic1K.WhyNotWritable(0));
        Assert.NotNull(Classic1K.WhyNotWritable(3));
        Assert.NotNull(Classic1K.WhyNotWritable(64));
        Assert.Null(Classic1K.WhyNotWritable(1));
        Assert.Equal(62, Classic1K.LastWritable);
    }

    [Theory]
    [InlineData(1024, "MIFARE Classic 1K")]
    [InlineData(4096, "MIFARE Classic 4K")]
    [InlineData(2048, "MIFARE Classic 2K")]
    [InlineData(320, "MIFARE Classic Mini")]
    public void Classic_cards_are_named_by_their_size(int bytes, string name) =>
        Assert.Equal(name, MifareClassicKeys.ChipName(bytes));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(127, 31)]
    [InlineData(128, 32)]
    [InlineData(255, 39)]
    public void Classic_blocks_map_to_their_sector(int block, int sector) =>
        Assert.Equal(sector, MifareClassicKeys.SectorOf(block));

    [Fact]
    public void Classic_segments_skip_the_trailers()
    {
        var edit = TagEdit.Blank(Classic1K);
        Assert.Equal([1, 2, 4, 5, 6, 8], edit.Segments.Take(6));
        Assert.Equal(62 - 1 + 1 - 15, edit.Segments.Count);   // blocks 1-62 without the 15 trailers among them
    }

    // ------------------------------------------------------------------------------------------------ from a read

    [Fact]
    public void An_edit_from_a_read_expects_that_tag_and_has_no_changes()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04:11:22:33:44:55:66");
        Assert.Equal("04:11:22:33:44:55:66", edit.ExpectedId);
        Assert.True(edit.FromRead);
        Assert.Empty(edit.Changes);
        Assert.Equal(36, edit.Segments.Count);   // pages 4-39
    }

    [Fact]
    public void Only_the_changed_page_is_written()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        var hex = edit.FormatHex().Replace("005: 05 05 05 05", "005: AA BB CC DD");
        Assert.Null(edit.ApplyHex(hex));
        var change = Assert.Single(edit.Changes);
        Assert.Equal(5, change.Key);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, change.Value);
    }

    [Fact]
    public void Locked_pages_from_the_read_are_not_offered()
    {
        var edit = TagEdit.FromTag(ReadNtag213(new() { [6] = "is locked for good" }), "04");
        Assert.DoesNotContain(6, edit.Segments);
        Assert.Contains("locked", edit.ApplyHex("006: 00 00 00 00"));
    }

    [Fact]
    public void A_committed_write_is_no_longer_a_change()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        edit.ApplyHex(edit.FormatHex().Replace("004: 04 04 04 04", "004: 01 02 03 04"));
        Assert.Single(edit.Changes);
        edit.Commit(edit.Changes);
        Assert.Empty(edit.Changes);
    }

    // ------------------------------------------------------------------------------------------------ hex view

    [Fact]
    public void A_blank_edit_writes_only_what_is_filled_in()
    {
        var edit = TagEdit.Blank(Ntag213);
        Assert.StartsWith("004:\n005:", edit.FormatHex());
        Assert.Empty(edit.Changes);
        Assert.Null(edit.ApplyHex("004:\n005: de:ad:be:ef\n006:"));
        Assert.Equal([5], edit.Changes.Keys);
    }

    [Theory]
    [InlineData("hello", "start it with the page number")]
    [InlineData("003: 00 00 00 00", "one-time")]
    [InlineData("041: 00 00 00 00", "password")]
    [InlineData("004: 00 00 00", "needs 4 bytes")]
    [InlineData("004: 00 00 00 0G", "only hex digits")]
    [InlineData("004: 00 00 00 00\n4: 11 11 11 11", "listed twice")]
    public void Bad_hex_lines_say_what_is_wrong_and_change_nothing(string text, string problem)
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.ApplyHex("010: 01 01 01 01");
        Assert.Contains(problem, edit.ApplyHex(text));
        Assert.Equal([10], edit.Changes.Keys);   // the earlier input stands
    }

    [Fact]
    public void Lines_outside_the_range_are_refused_and_only_the_range_is_written()
    {
        var edit = TagEdit.Blank(Ntag213);
        Assert.Null(edit.ApplyHex("020: 01 01 01 01"));
        Assert.Null(edit.SetRange(4, 10));
        Assert.Empty(edit.Changes);                         // page 20 is kept, but not written from 4-10
        Assert.Contains("outside", edit.ApplyHex("011: 00 00 00 00"));
        Assert.NotNull(edit.SetRange(4, 40));               // page 40 is the lock page
        Assert.NotNull(edit.SetRange(9, 5));
    }

    // ------------------------------------------------------------------------------------------------ text view

    [Fact]
    public void Text_runs_over_the_pages_and_pads_the_last_with_zeros()
    {
        var edit = TagEdit.Blank(Ntag213);
        Assert.Equal("", edit.FormatText());
        Assert.Null(edit.ApplyText("Hello"));
        Assert.Equal([4, 5], edit.Changes.Keys);
        Assert.Equal("Hell"u8.ToArray(), edit.DataFor(4));
        Assert.Equal(new byte[] { (byte)'o', 0, 0, 0 }, edit.DataFor(5));
        Assert.Equal("004: 48 65 6C 6C\n005: 6F 00 00 00\n006:", string.Join("\n", edit.FormatHex().Split('\n').Take(3)));
    }

    [Fact]
    public void Typing_and_deleting_leaves_untouched_pages_alone()
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.FormatText();
        edit.ApplyText("Hello there");
        Assert.Null(edit.ApplyText("Hi"));
        Assert.Equal([4], edit.Changes.Keys);   // pages 5-6 were never on the tag's side of the edit
    }

    [Fact]
    public void Shortening_text_from_a_read_clears_what_it_no_longer_covers()
    {
        var data = new Dictionary<int, byte[]>
        {
            [4] = "Hell"u8.ToArray(), [5] = "o wo"u8.ToArray(), [6] = "rld\0"u8.ToArray(), [7] = [0, 0, 0, 0],
        };
        var edit = TagEdit.FromTag(new SegmentRead(Ntag213, data, new Dictionary<int, string>()), "04");
        edit.SetRange(4, 7);
        Assert.Equal("Hello world", edit.FormatText());
        Assert.Null(edit.ApplyText("Hello world"));
        Assert.Empty(edit.Changes);                         // the same text gives the same bytes
        Assert.Null(edit.ApplyText("Hi"));
        Assert.Equal([4, 5, 6], edit.Changes.Keys);         // page 7 was zeros already
        Assert.Equal(new byte[4], edit.DataFor(6));
    }

    [Fact]
    public void Text_that_does_not_fit_is_refused()
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.SetRange(4, 5);
        Assert.Contains("needs 9 bytes", edit.ApplyText("123456789"));
    }

    [Fact]
    public void Classic_text_skips_the_trailer()
    {
        var edit = TagEdit.Blank(Classic1K);
        Assert.Null(edit.ApplyText(new string('a', 40)));
        Assert.Equal([1, 2, 4], edit.Changes.Keys);
    }

    [Theory]
    [InlineData(new byte[] { 0x48, 0x69 }, "Hi")]
    [InlineData(new byte[] { 0x03, 0x00, 0xFE }, @"\x03\x00\xFE")]
    [InlineData(new byte[] { 0x5C }, @"\\")]
    [InlineData(new byte[] { 0x61, 0x0A, 0x62 }, "a\nb")]
    [InlineData(new byte[] { 0xC3, 0xA5 }, "å")]
    [InlineData(new byte[] { 0xC3 }, @"\xC3")]   // half a UTF-8 character stays a byte
    public void Escaping_round_trips(byte[] bytes, string text)
    {
        Assert.Equal(text, TagEdit.Escape(bytes));
        Assert.True(TagEdit.TryUnescape(text, out var back, out _));
        Assert.Equal(bytes, back);
    }

    [Theory]
    [InlineData(@"a\q")]
    [InlineData(@"\x4")]
    [InlineData(@"end\")]
    public void A_stray_backslash_is_explained(string text)
    {
        Assert.False(TagEdit.TryUnescape(text, out _, out var error));
        Assert.Contains("backslash", error);
    }

    // ------------------------------------------------------------------------------------------------ checks at write time

    [Fact]
    public void The_expected_serial_must_match_however_it_is_written()
    {
        var write = new SegmentWrite(Ntag213, "04:A1:B2", new Dictionary<int, byte[]>());
        Assert.Null(write.WrongTag("04a1b2"));
        Assert.Contains("Nothing was written", write.WrongTag("04:A1:B3"));
        Assert.Null(write with { ExpectedId = null } is var any ? any.WrongTag("FF") : "");
    }

    [Fact]
    public void Another_chip_is_refused()
    {
        var write = new SegmentWrite(Ntag213, null, new Dictionary<int, byte[]>());
        Assert.Null(write.WrongChip(Ntag213 with { }));
        Assert.Contains("an NTAG215, but the edit is for an NTAG213", write.WrongChip(Layout("NTAG215")));
        Assert.Contains("a MIFARE Classic 1K", write.WrongChip(Classic1K));
    }

    [Fact]
    public void An_iso_label_is_checked_by_block_size_and_count()
    {
        var write = new SegmentWrite(Iso, null, new Dictionary<int, byte[]>());
        Assert.Null(write.WrongChip(Iso with { Count = 28 }));
        Assert.Contains("Set the bytes per block to 8", write.WrongChip(Iso with { SegmentSize = 8 }));
        var sized = new SegmentWrite(Iso with { Count = 28 }, null, new Dictionary<int, byte[]>());
        Assert.Contains("has 64 blocks", sized.WrongChip(Iso with { Count = 64 }));
    }

    [Theory]
    [InlineData("04:a1:b2", "04:A1:B2")]
    [InlineData("04 A1 B2", "04:A1:B2")]
    [InlineData("04A1B2", "04:A1:B2")]
    [InlineData("04A1B", null)]
    [InlineData("hello", null)]
    public void A_typed_serial_is_read_as_hex_bytes(string text, string? id) =>
        Assert.Equal(id, TagEdit.ParseId(text));

    [Fact]
    public void An_iso_label_of_unknown_size_starts_with_16_blocks_and_can_take_another_block_size()
    {
        var edit = TagEdit.Blank(Iso with { SegmentSize = 8 });
        Assert.Equal((0, 15), (edit.From, edit.To));
        Assert.Contains("needs 8 bytes", edit.ApplyHex("000: 00 00 00 00"));
        Assert.Null(edit.ApplyHex("000: 01 02 03 04 05 06 07 08"));
    }

    [Fact]
    public void The_summary_names_the_pages_and_bytes()
    {
        var write = new SegmentWrite(Ntag213, null, new Dictionary<int, byte[]> { [5] = new byte[4], [6] = new byte[4], [9] = new byte[4] });
        Assert.Equal("pages 5-6, 9 (12 bytes)", write.Summary);
    }
}
