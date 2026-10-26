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
    public void A_sector_the_read_opened_can_take_a_new_key()
    {
        var data = Enumerable.Range(0, 64).ToDictionary(b => b, _ => new byte[16]);
        var read = new SegmentRead(Classic1K, data, new Dictionary<int, string>()) { OpenedSectors = new HashSet<int> { 1 } };
        var edit = TagEdit.FromTag(read, "04:A1:B2:C3");
        byte[] key = [1, 2, 3, 4, 5, 6];

        Assert.True(edit.CanChangeKey(1));
        Assert.False(edit.CanChangeKey(2));                     // did not open: its key is not known
        Assert.True(edit.SetSectorKey(1, keyB: false, key));    // a new key = key A and key B both set to it
        Assert.True(edit.SetSectorKey(1, keyB: true, key));
        Assert.False(edit.SetSectorKey(2, keyB: false, key));
        Assert.True(edit.HasChanges);
        Assert.Empty(edit.Changes);                             // the trailer is written through the trailer path, not as data

        var write = edit.ToWrite();
        Assert.False(write.IsEmpty);
        Assert.Equal(key, write.Sectors[1].KeyA);
        Assert.Equal(key, write.Sectors[1].KeyB);
        Assert.True(write.Sectors[1].SameKey);
        Assert.Equal("a new key for sector 1", write.Summary);

        edit.SectorChanged(1);
        Assert.True(edit.CanChangeKey(1));                      // the new key is known too, so it can be changed again
        Assert.Empty(edit.Sectors);
    }

    [Fact]
    public void Rules_can_be_changed_for_an_opened_sector_and_equal_rules_are_no_change()
    {
        var data = Enumerable.Range(0, 64).ToDictionary(b => b, _ => new byte[16]);
        data[7] = [0, 0, 0, 0, 0, 0, 0xFF, 0x07, 0x80, 0x69, 0, 0, 0, 0, 0, 0];   // sector 1's trailer: factory rules
        var read = new SegmentRead(Classic1K, data, new Dictionary<int, string>()) { OpenedSectors = new HashSet<int> { 1 } };
        var edit = TagEdit.FromTag(read, "04");

        Assert.Equal(SectorRules.Transport, edit.CurrentRules(1));
        Assert.Null(edit.CurrentRules(2));                      // all zeros: corrupt, not decodable

        var readOnly = SectorRules.Transport.WithData(0, 0b010);
        Assert.True(edit.SetSectorRules(1, readOnly));
        Assert.Equal(readOnly, edit.SectorChangeFor(1)!.Rules);
        Assert.Null(edit.SectorChangeFor(1)!.KeyA);
        Assert.Equal("new rules for sector 1", edit.ToWrite().Summary);
        Assert.False(edit.SetSectorRules(2, readOnly));

        edit.SetSectorRules(1, null);
        Assert.Empty(edit.Sectors);
    }

    [Fact]
    public void A_blank_edit_or_a_type2_tag_never_offers_a_key()
    {
        Assert.False(TagEdit.Blank(Classic1K).CanChangeKey(1));
        Assert.False(TagEdit.FromTag(ReadNtag213(), "04").CanChangeKey(1));
    }

    [Fact]
    public void Default_keys_are_told_from_others()
    {
        Assert.True(MifareClassicKeys.IsDefault([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));
        Assert.False(MifareClassicKeys.IsDefault([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
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

    [Fact]
    public void Applied_bytes_become_ordinary_changes_and_skip_what_cannot_be_written()
    {
        var edit = TagEdit.FromTag(ReadNtag213(new() { [6] = "is locked for good" }), "04");
        var (applied, skipped) = edit.Apply(new Dictionary<int, byte[]>
        {
            [5] = [5, 5, 5, 5],          // the same as the tag has: no change
            [7] = [1, 2, 3, 4],
            [6] = [9, 9, 9, 9],          // locked: skipped
            [3] = [0, 0, 0, 0],          // never written: skipped
            [8] = [1, 2],                // not a whole page: skipped
        });
        Assert.Equal((2, 3), (applied, skipped));
        Assert.Equal([7], edit.Changes.Keys);
        Assert.Equal(new byte?[] { 1, 2, 3, 4 }, edit.Changes[7]);
        Assert.True(edit.Undo());
        Assert.Empty(edit.Changes);
    }

    [Fact]
    public void Reset_puts_the_byte_under_the_cursor_back_and_walks_left()
    {
        var edit = TagEdit.FromTag(ReadNtag213(), "04");
        At(edit, 5, 0, text: true);
        edit.TypeText("abcd");                                   // page 5 fully changed, cursor now at page 6 byte 0
        At(edit, 5, 2, text: true);                              // put the cursor in the middle of the change

        edit.ResetAtCursor();
        Assert.Equal((byte)5, edit.ValueAt(5, 2));               // back as read
        Assert.Equal((byte)'d', edit.ValueAt(5, 3));             // untouched, after the cursor
        Assert.Equal(edit.PositionOf(5, 1), edit.Cursor);

        edit.ResetAtCursor();
        edit.ResetAtCursor();
        Assert.Equal(new byte?[] { null, null, null, (byte)'d' }, edit.Changes[5]);
        Assert.Equal(edit.PositionOf(4, 3), edit.Cursor);       // walked into the page before

        edit.ResetAtCursor();                                    // page 4 byte 3 was never changed: just steps left
        Assert.Equal(edit.PositionOf(4, 2), edit.Cursor);
        Assert.True(edit.Undo());                                // the last reset that changed something
        Assert.Equal((byte)'a', edit.ValueAt(5, 0));
    }

    [Fact]
    public void Reset_at_the_start_stays_put()
    {
        var edit = TagEdit.Blank(Ntag213);
        edit.ResetAtCursor();
        Assert.Equal(0, edit.Cursor);
        Assert.False(edit.CanUndo);
    }

    // ------------------------------------------------------------------------------------------------ block 0 and key A

    private static SegmentRead ReadClassic(params int[] openedSectors)
    {
        // As a real read: only the blocks of sectors a key opened are in the data (1K: four blocks per sector).
        var data = new Dictionary<int, byte[]>();
        foreach (var s in openedSectors)
            for (int b = 0; b < 4; b++) { int block = s * 4 + b; data[block] = Enumerable.Repeat((byte)block, 16).ToArray(); }
        return new SegmentRead(Classic1K, data, new Dictionary<int, string>()) { OpenedSectors = new HashSet<int>(openedSectors) };
    }

    [Fact]
    public void Block_0_is_locked_by_default_but_can_be_unlocked_and_edited_for_a_magic_card()
    {
        var edit = TagEdit.FromTag(ReadClassic(0), "04");
        Assert.True(edit.CanEditBlockZero);
        Assert.NotNull(edit.WhyNotWritable(0));            // locked unless unlocked by hand
        Assert.DoesNotContain(0, edit.Segments);

        edit.SetBlockZeroEditable(true);
        Assert.True(edit.AllowBlockZero);
        Assert.Null(edit.WhyNotWritable(0));
        Assert.Equal(0, edit.Segments[0]);                 // leads the writable run
        At(edit, 0, 0);
        Hex(edit, "AA");
        Assert.Equal((byte)0xAA, edit.ValueAt(0, 0));
        Assert.Contains(0, edit.Changes.Keys);

        edit.SetBlockZeroEditable(false);                  // locking it again drops the edit
        Assert.NotNull(edit.WhyNotWritable(0));
        Assert.DoesNotContain(0, edit.Segments);
        Assert.DoesNotContain(0, edit.Changes.Keys);
    }

    [Fact]
    public void Block_0_check_byte_is_locked_and_follows_the_UID()
    {
        // Block 0 starts 04 11 22 33 with BCC = 04^11^22^33, then the maker bytes.
        byte bcc = 0x04 ^ 0x11 ^ 0x22 ^ 0x33;
        var data = new Dictionary<int, byte[]> { [0] = [0x04, 0x11, 0x22, 0x33, bcc, 0x08, .. new byte[10]] };
        for (int b = 1; b < 4; b++) data[b] = new byte[16];
        var edit = TagEdit.FromTag(new SegmentRead(Classic1K, data, new Dictionary<int, string>()) { OpenedSectors = new HashSet<int> { 0 } }, "04");
        edit.SetBlockZeroEditable(true);

        Assert.Equal(-1, edit.PositionOf(0, 4));              // the check byte is never a place the cursor can edit
        Assert.True(edit.PositionOf(0, 3) >= 0 && edit.PositionOf(0, 5) >= 0);   // but the bytes either side are

        At(edit, 0, 0);
        Hex(edit, "AA");                                       // change the first UID byte to AA
        Assert.Equal((byte)0xAA, edit.ValueAt(0, 0));
        Assert.Equal((byte)(0xAA ^ 0x11 ^ 0x22 ^ 0x33), edit.ValueAt(0, 4));   // check byte recomputed
        Assert.Contains(0, edit.Changes.Keys);
        Assert.Equal((byte)(0xAA ^ 0x11 ^ 0x22 ^ 0x33), edit.Changes[0][4]);   // and written

        // Put the UID byte back; the check byte returns to the card's own and is no longer a change.
        At(edit, 0, 0);
        Hex(edit, "04");
        Assert.Equal(bcc, edit.ValueAt(0, 4));
        Assert.DoesNotContain(0, edit.Changes.Keys);
    }

    [Fact]
    public void Block_0_cannot_be_unlocked_when_it_was_not_read_or_set_up_by_hand()
    {
        Assert.False(TagEdit.FromTag(ReadClassic(1), "04").CanEditBlockZero);   // sector 0 not opened: no block 0
        var blank = TagEdit.Blank(Classic1K);
        Assert.False(blank.CanEditBlockZero);
        blank.SetBlockZeroEditable(true);
        Assert.False(blank.AllowBlockZero);
    }

    [Fact]
    public void Key_A_and_key_B_can_be_set_on_their_own_and_show_in_the_trailer_row()
    {
        var edit = TagEdit.FromTag(ReadClassic(1), "04");
        byte[] keyA = [0xDA, 0x7A, 0x00, 0x00, 0x00, 0x01];

        Assert.True(edit.SetSectorKey(1, keyB: false, keyA));   // key A only — key B kept
        Assert.False(edit.SetSectorKey(2, keyB: false, keyA));  // sector 2 did not open
        Assert.Equal(keyA, edit.SectorChangeFor(1)!.KeyA);
        Assert.Null(edit.SectorChangeFor(1)!.KeyB);
        Assert.Equal(keyA, edit.PendingKey(1, keyB: false));
        Assert.False(edit.SectorChangeFor(1)!.SameKey);
        Assert.Equal("key A for sector 1", edit.ToWrite().Summary);

        // The key bytes are edited in the trailer row (sector 1's trailer is block 7), so inline and field agree.
        int trailer = TagEdit.TrailerOf(1);
        Assert.Equal(7, trailer);
        Assert.Equal((byte)0xDA, edit.ValueAt(trailer, 0));
        Assert.True(edit.IsEdited(trailer, 0));

        byte[] keyB = [9, 9, 9, 9, 9, 9];
        edit.SetSectorKey(1, keyB: true, keyB);
        Assert.Equal(keyB, edit.SectorChangeFor(1)!.KeyB);
        Assert.Equal(keyA, edit.SectorChangeFor(1)!.KeyA);      // key A still pending

        edit.SetSectorKey(1, keyB: false, null);
        edit.SetSectorKey(1, keyB: true, null);
        Assert.Empty(edit.Sectors);
    }
}
