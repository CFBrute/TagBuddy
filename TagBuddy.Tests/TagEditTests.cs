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

    /// <summary>Puts the cursor on a byte of a segment, in hex (or text).</summary>
    private static void At(TagEdit edit, int segment, int index, bool text = false) =>
        edit.Place(edit.PositionOf(segment, index), text);

    private static void Hex(TagEdit edit, string digits) { foreach (var c in digits) edit.TypeHex(c); }

    [Fact]
    public void Only_the_typed_bytes_are_written()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 1);
        Hex(edit, "AABB");
        var change = Assert.Single(edit.Changes);
        Assert.Equal(5, change.Key);
        Assert.Equal(new byte?[] { null, 0xAA, 0xBB, null }, change.Value);
        Assert.Equal((byte)0xAA, edit.ValueAt(5, 1));
        Assert.Equal((byte)0x05, edit.ValueAt(5, 0));          // untouched: as read
    }

    [Fact]
    public void Typing_runs_on_into_the_next_segment()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 3);
        Hex(edit, "1122");
        Assert.Equal([5, 6], edit.Changes.Keys);
        Assert.Equal((byte)0x22, edit.ValueAt(6, 0));
    }

    [Fact]
    public void Typing_what_the_tag_already_has_is_no_change()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 0);
        Hex(edit, "05");
        Assert.Empty(edit.Changes);
    }

    [Fact]
    public void One_hex_digit_sets_half_a_byte()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 7, 0);
        edit.TypeHex('A');
        Assert.Equal((byte)0xA7, edit.ValueAt(7, 0));
        Assert.Equal(1, edit.Nibble);
        Assert.False(edit.TypeHex('G'));
    }

    [Fact]
    public void Text_overwrites_from_the_cursor_and_skips_classic_trailers()
    {
        var edit = TagEdit.Blank(Classic1K);
        At(edit, 2, 14, text: true);
        Assert.Equal(4, edit.TypeText("Hiya"));
        Assert.Equal((byte)'H', edit.ValueAt(2, 14));
        Assert.Equal((byte)'y', edit.ValueAt(4, 0));            // block 3 is the trailer
        Assert.Equal([2, 4], edit.Changes.Keys);
    }

    [Fact]
    public void A_blank_segment_keeps_the_tags_bytes_where_nothing_was_typed()
    {
        var edit = TagEdit.Blank(Ntag213);
        Assert.Null(edit.ValueAt(4, 0));
        At(edit, 4, 2, text: true);
        edit.TypeText("é");                                      // two UTF-8 bytes
        var change = Assert.Single(edit.Changes);
        Assert.Equal(new byte?[] { null, null, 0xC3, 0xA9 }, change.Value);
    }

    [Fact]
    public void Text_stops_at_the_end_of_the_range()
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.SetRange(4, 4);
        At(edit, 4, 2, text: true);
        Assert.Equal(2, edit.TypeText("abc"));
        Assert.Equal(0, edit.TypeText("d"));
        Assert.False(edit.TypeHex('1'));
    }

    [Fact]
    public void Backspace_puts_the_byte_back()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 0, text: true);
        edit.TypeText("xy");
        edit.Backspace();
        Assert.Equal((byte)0x05, edit.ValueAt(5, 1));
        Assert.Equal((byte)'x', edit.ValueAt(5, 0));
        edit.Backspace();
        Assert.Empty(edit.Changes);
    }

    [Fact]
    public void Backspace_in_the_middle_of_a_hex_byte_takes_back_that_byte()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 0);
        edit.TypeHex('F');
        edit.Backspace();
        Assert.Equal((byte)0x05, edit.ValueAt(5, 0));
        Assert.Equal(0, edit.Nibble);
        Assert.Equal(edit.PositionOf(5, 0), edit.Cursor);
    }

    [Fact]
    public void Undo_takes_back_one_keystroke_at_a_time_and_redo_puts_it_back()
    {
        var edit = TagEdit.Blank(Ntag213);
        At(edit, 4, 0, text: true);
        edit.TypeText("a");
        edit.TypeText("b");

        Assert.True(edit.Undo());
        Assert.Null(edit.ValueAt(4, 1));
        Assert.Equal((byte)'a', edit.ValueAt(4, 0));
        Assert.Equal(edit.PositionOf(4, 1), edit.Cursor);       // the cursor goes back with it
        Assert.True(edit.Undo());
        Assert.Empty(edit.Changes);
        Assert.False(edit.Undo());

        Assert.True(edit.Redo());
        Assert.True(edit.Redo());
        Assert.Equal((byte)'b', edit.ValueAt(4, 1));
        Assert.False(edit.CanRedo);
    }

    [Fact]
    public void Each_hex_digit_and_backspace_is_its_own_step()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 0);
        Hex(edit, "AB");
        edit.Backspace();
        edit.Undo();                                             // takes back the backspace
        Assert.Equal((byte)0xAB, edit.ValueAt(5, 0));
        edit.Undo();                                             // takes back the "B"
        Assert.Equal((byte)0xA5, edit.ValueAt(5, 0));
    }

    [Fact]
    public void A_row_or_everything_can_be_set_to_zeros_in_one_step()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        edit.Fill(0x00, 5);
        Assert.Equal([5], edit.Changes.Keys);
        Assert.Equal(new byte?[] { 0, 0, 0, 0 }, edit.Changes[5]);

        edit.Fill(0x00);
        Assert.Equal(36, edit.Changes.Count);                   // pages 4-39: page 4 held 04, so it changes too
        Assert.All(edit.Segments, s => Assert.Equal((byte)0, edit.ValueAt(s, 0)));
        edit.Undo();
        Assert.Equal([5], edit.Changes.Keys);
    }

    [Fact]
    public void Zeros_over_zeros_are_no_change_and_a_closed_row_is_left_alone()
    {
        var data = Enumerable.Range(0, 45).ToDictionary(p => p, _ => new byte[4]);
        var edit = TagEdit.FromTag(new SegmentRead(Ntag213, data, new Dictionary<int, string> { [6] = "locked" }), "04");
        edit.Fill(0x00);
        Assert.Empty(edit.Changes);
        edit.Fill(0xFF, 6);
        Assert.Empty(edit.Changes);
        Assert.False(edit.CanUndo);
    }

    [Fact]
    public void Typing_after_an_undo_drops_the_redo()
    {
        var edit = TagEdit.Blank(Ntag213);
        At(edit, 4, 0, text: true);
        edit.TypeText("a");
        edit.Undo();
        edit.TypeText("z");
        Assert.False(edit.CanRedo);
    }

    [Fact]
    public void Revert_is_one_step_that_undo_takes_back()
    {
        var edit = TagEdit.Blank(Ntag213);
        At(edit, 4, 0);
        Hex(edit, "0102");
        edit.RevertAll();
        Assert.Empty(edit.Changes);
        edit.Undo();
        Assert.Equal([4], edit.Changes.Keys);
    }

    [Fact]
    public void Locked_pages_from_the_read_are_not_in_the_run()
    {
        var edit = TagEdit.FromTag(ReadNtag213(new() { [6] = "is locked for good" }), "04");
        Assert.DoesNotContain(6, edit.Segments);
        Assert.Equal(-1, edit.PositionOf(6, 0));
        Assert.Equal("locked for good", edit.RoleOf(6));
        Assert.Equal((byte)6, edit.ValueAt(6, 0));              // still shown as read
        At(edit, 5, 3);
        Hex(edit, "AABB");
        Assert.Equal((byte)0xBB, edit.ValueAt(7, 0));           // runs on past the locked page
    }

    [Fact]
    public void A_committed_write_is_no_longer_a_change()
    {
        var edit = TagEdit.Blank(Ntag213);
        At(edit, 4, 0);
        Hex(edit, "01");
        edit.Commit(edit.Changes);
        Assert.Empty(edit.Changes);
        Assert.Equal((byte)1, edit.ValueAt(4, 0));
        Assert.False(edit.CanUndo);
    }

    [Fact]
    public void Only_the_range_is_written()
    {
        var edit = TagEdit.Blank(Ntag213);
        At(edit, 20, 0);
        Hex(edit, "01");
        Assert.Null(edit.SetRange(4, 10));
        Assert.Empty(edit.Changes);
        Assert.NotNull(edit.SetRange(4, 40));                   // page 40 is the lock page
        Assert.NotNull(edit.SetRange(9, 5));
    }

    [Fact]
    public void Moving_stays_inside_the_run()
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.Move(-5);
        Assert.Equal(0, edit.Cursor);
        edit.Move(1000);
        Assert.Equal(edit.Length - 1, edit.Cursor);
    }

    [Theory]
    [InlineData(new int[] { 0x11, -1, 0x33, -1 }, new byte[] { 1, 2, 3, 4 }, new byte[] { 0x11, 2, 0x33, 4 })]
    [InlineData(new int[] { 0x11, 0x22, 0x33, 0x44 }, new byte[0], new byte[] { 0x11, 0x22, 0x33, 0x44 })]
    public void Merge_keeps_the_tags_bytes_that_were_not_typed(int[] wanted, byte[] current, byte[] expected) =>
        Assert.Equal(expected, SegmentWrite.Merge(wanted.Select(b => b < 0 ? (byte?)null : (byte)b).ToArray(), current));

    [Fact]
    public void Merge_needs_the_tags_bytes_when_some_are_kept() =>
        Assert.Null(SegmentWrite.Merge([0x11, null], []));

    // ------------------------------------------------------------------------------------------------ checks at write time

    [Fact]
    public void The_expected_serial_must_match_however_it_is_written()
    {
        var write = new SegmentWrite(Ntag213, "04:A1:B2", new Dictionary<int, byte?[]>());
        Assert.Null(write.WrongTag("04a1b2"));
        Assert.Contains("Nothing was written", write.WrongTag("04:A1:B3"));
        Assert.Null(write with { ExpectedId = null } is var any ? any.WrongTag("FF") : "");
    }

    [Fact]
    public void Another_chip_is_refused()
    {
        var write = new SegmentWrite(Ntag213, null, new Dictionary<int, byte?[]>());
        Assert.Null(write.WrongChip(Ntag213 with { }));
        Assert.Contains("an NTAG215, but the edit is for an NTAG213", write.WrongChip(Layout("NTAG215")));
        Assert.Contains("a MIFARE Classic 1K", write.WrongChip(Classic1K));
    }

    [Fact]
    public void An_iso_label_is_checked_by_block_size_and_count()
    {
        var write = new SegmentWrite(Iso, null, new Dictionary<int, byte?[]>());
        Assert.Null(write.WrongChip(Iso with { Count = 28 }));
        Assert.Contains("Set the bytes per block to 8", write.WrongChip(Iso with { SegmentSize = 8 }));
        var sized = new SegmentWrite(Iso with { Count = 28 }, null, new Dictionary<int, byte?[]>());
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
        Assert.Equal(16 * 8, edit.Length);
        edit.Place(7, inText: false);
        Hex(edit, "AB");
        Assert.Equal(new byte?[] { null, null, null, null, null, null, null, 0xAB }, edit.Changes[0]);
    }

    [Fact]
    public void A_write_record_undoes_to_the_old_bytes_on_that_tag_only()
    {
        var record = new SegmentWriteRecord("04:A1", Ntag213,
            new Dictionary<int, byte[]> { [5] = [1, 2, 3, 4], [6] = [5, 6, 7, 8] },
            new Dictionary<int, byte[]> { [5] = [9, 2, 3, 4], [6] = [5, 6, 7, 9] });
        Assert.Equal("pages 5-6", record.Segments);

        var undo = record.UndoWrite;
        Assert.Equal("04:A1", undo.ExpectedId);
        Assert.Equal(new byte?[] { 1, 2, 3, 4 }, undo.Writes[5]);
        Assert.NotNull(undo.WrongTag("04:A2"));

        Assert.Equal(new byte?[] { 5, 6, 7, 9 }, record.RedoWrite.Writes[6]);
    }

    [Fact]
    public void The_summary_names_the_pages_and_bytes()
    {
        var write = new SegmentWrite(Ntag213, null, new Dictionary<int, byte?[]>
        {
            [5] = [1, 2, 3, 4], [6] = [null, 7, null, null], [9] = [8, null, null, null],
        });
        Assert.Equal("pages 5-6, 9 (6 bytes)", write.Summary);
    }
}
