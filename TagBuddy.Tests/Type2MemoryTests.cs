using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The byte rules that decide what gets written to a tag. A mistake here can lock or wreck a real tag,
/// so these are the most important tests in the project.</summary>
public class Type2MemoryTests
{
    // GET_VERSION answers: 00 04 <type> <subtype> <major> <minor> <size> 03
    private static readonly byte[] Ntag213Version = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x0F, 0x03];
    private static readonly byte[] Ntag215Version = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x11, 0x03];
    private static readonly byte[] Ntag216Version = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x13, 0x03];

    private static Type2Chip Ntag213 => Type2Memory.ChipFromVersion(Ntag213Version)!;
    private static Type2Chip Ntag215 => Type2Memory.ChipFromVersion(Ntag215Version)!;

    // ------------------------------------------------------------------ chip identification

    [Fact]
    public void ChipFromVersion_knows_the_NTAG_sizes()
    {
        Assert.Equal(new Type2Chip("NTAG213", 45, 40, true, 41), Type2Memory.ChipFromVersion(Ntag213Version));
        Assert.Equal(new Type2Chip("NTAG215", 135, 130, true, 131), Type2Memory.ChipFromVersion(Ntag215Version));
        Assert.Equal(new Type2Chip("NTAG216", 231, 226, true, 227), Type2Memory.ChipFromVersion(Ntag216Version));
    }

    [Fact]
    public void ChipFromVersion_knows_Ultralight_EV1()
    {
        var chip = Type2Memory.ChipFromVersion([0x00, 0x04, 0x03, 0x01, 0x01, 0x00, 0x0B, 0x03])!;
        Assert.Equal("MIFARE Ultralight EV1 (48 bytes)", chip.Name);
        Assert.Equal(20, chip.TotalPages);
        Assert.Equal(16, chip.UserEnd);
    }

    [Fact]
    public void ChipFromVersion_leaves_unknown_sizes_without_a_layout()
    {
        var chip = Type2Memory.ChipFromVersion([0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x99, 0x03])!;
        Assert.Equal(0, chip.UserEnd);    // the writers refuse a chip whose user pages they cannot tell
        Assert.Equal(-1, chip.ConfigPage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[] { 0x00, 0x04, 0x04 })]
    [InlineData(new byte[] { 0x0A, 0x04, 0x04, 0x02, 0x01, 0x00, 0x0F, 0x03 })]
    public void ChipFromVersion_rejects_other_answers(byte[]? answer) =>
        Assert.Null(Type2Memory.ChipFromVersion(answer));

    // ------------------------------------------------------------------ lock bits

    private static bool[] Locked(Type2Chip chip, Action<byte[]> setBits)
    {
        var memory = new byte[chip.TotalPages * 4];
        setBits(memory);
        return Type2Memory.LockedPages(memory, chip);
    }

    private static int[] LockedNumbers(bool[] locked) =>
        locked.Select((l, page) => (l, page)).Where(x => x.l).Select(x => x.page).ToArray();

    [Fact]
    public void LockedPages_is_empty_for_a_fresh_tag() =>
        Assert.Empty(LockedNumbers(Locked(Ntag215, _ => { })));

    [Fact]
    public void LockedPages_reads_the_Amiibo_lock_bytes()
    {
        // An Amiibo writer sets static 0F E0 and dynamic 01: pages 3, 13-15 and 16-31 are read-only for good.
        var locked = Locked(Ntag215, m => { m[10] = 0x0F; m[11] = 0xE0; m[130 * 4] = 0x01; });
        int[] expected = [3, 13, 14, 15, .. Enumerable.Range(16, 16)];
        Assert.Equal(expected, LockedNumbers(locked));
    }

    [Fact]
    public void LockedPages_static_bits_cover_pages_4_to_15()
    {
        var locked = Locked(Ntag213, m => { m[10] = 0xF0; m[11] = 0x01; });
        Assert.Equal([4, 5, 6, 7, 8], LockedNumbers(locked));
    }

    [Fact]
    public void LockedPages_ignores_the_block_lock_bits()
    {
        // Bits 0-2 of the first lock byte lock the lock bits themselves, not any page.
        Assert.Empty(LockedNumbers(Locked(Ntag213, m => m[10] = 0x07)));
    }

    [Fact]
    public void LockedPages_dynamic_bits_lock_two_pages_each_on_NTAG213()
    {
        var locked = Locked(Ntag213, m => { m[40 * 4] = 0x03; m[40 * 4 + 1] = 0x01; });
        Assert.Equal([16, 17, 18, 19, 32, 33], LockedNumbers(locked));
    }

    [Fact]
    public void LockedPages_never_goes_past_user_memory()
    {
        var locked = Locked(Ntag213, m => { m[40 * 4] = 0xFF; m[40 * 4 + 1] = 0xFF; });
        Assert.Equal(Enumerable.Range(16, 24), LockedNumbers(locked));   // pages 16-39; 40 is the lock page
    }

    // ------------------------------------------------------------------ NDEF TLV

    [Fact]
    public void NdefTlv_short_message_is_padded_to_whole_pages()
    {
        var ndef = Enumerable.Range(1, 10).Select(i => (byte)i).ToArray();
        var tlv = Type2Memory.NdefTlv(ndef);
        Assert.Equal(16, tlv.Length);
        Assert.Equal([0x03, 0x0A], tlv[..2]);
        Assert.Equal(ndef, tlv[2..12]);
        Assert.Equal(0xFE, tlv[12]);
        Assert.All(tlv[13..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void NdefTlv_uses_the_three_byte_length_from_255()
    {
        var tlv = Type2Memory.NdefTlv(new byte[300]);
        Assert.Equal([0x03, 0xFF, 0x01, 0x2C], tlv[..4]);
        Assert.Equal(0, tlv.Length % 4);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(700)]
    public void NdefTlv_reads_back(int length)
    {
        var ndef = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
        Assert.True(Type2Memory.TryExtractNdefTlv([.. Type2Memory.NdefTlv(ndef)], out var read, out var complete));
        Assert.True(complete);
        Assert.Equal(ndef, read);
    }

    [Fact]
    public void TryExtractNdefTlv_skips_null_and_lock_control_TLVs()
    {
        List<byte> data = [0x00, 0x00, 0x01, 0x03, 0xA0, 0x10, 0x44, 0x03, 0x02, 0xD1, 0x01, 0xFE];
        Assert.True(Type2Memory.TryExtractNdefTlv(data, out var ndef, out _));
        Assert.Equal([0xD1, 0x01], ndef);
    }

    [Fact]
    public void TryExtractNdefTlv_stops_at_a_terminator() =>
        Assert.False(Type2Memory.TryExtractNdefTlv([0xFE, 0x03, 0x01, 0xAA], out _, out _));

    [Fact]
    public void TryExtractNdefTlv_reports_a_cut_off_message()
    {
        Assert.True(Type2Memory.TryExtractNdefTlv([0x03, 0x0A, 0x01, 0x02, 0x03], out var ndef, out var complete));
        Assert.False(complete);
        Assert.Equal([0x01, 0x02, 0x03], ndef);
    }

    // ------------------------------------------------------------------ raw text

    /// <summary>A tag with a serial, NDEF page 3, old content in every user page, and no lock bits set.</summary>
    private static byte[] TagMemory(Type2Chip chip)
    {
        var memory = new byte[chip.TotalPages * 4];
        for (int i = 0; i < 10; i++) memory[i] = (byte)(0x40 + i);          // serial and check bytes (10-11: lock bits)
        memory[12] = 0xE1; memory[13] = 0x10; memory[14] = 0x12;           // capability container
        for (int i = 16; i < chip.UserEnd * 4; i++) memory[i] = 0x77;      // old content in the user pages
        return memory;
    }

    [Fact]
    public void PlaceRawText_keeps_serial_and_page_3_and_clears_the_rest()
    {
        var before = TagMemory(Ntag213);
        var (dump, error) = Type2Memory.PlaceRawText("Hello"u8.ToArray(), before, Ntag213);

        Assert.Null(error);
        Assert.Equal(40 * 4, dump!.Length);
        Assert.Equal(before[..16], dump[..16]);
        Assert.Equal("Hello"u8.ToArray(), dump[16..21]);
        Assert.All(dump[21..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void PlaceRawText_goes_around_locked_pages_and_keeps_their_content()
    {
        var before = TagMemory(Ntag213);
        before[10] = 0xF0;                                                  // pages 4-7 locked
        var (dump, _) = Type2Memory.PlaceRawText("Hello"u8.ToArray(), before, Ntag213);

        Assert.Equal(before[16..32], dump![16..32]);                        // locked pages untouched
        Assert.Equal("Hello"u8.ToArray(), dump[32..37]);                    // text starts at page 8
    }

    [Fact]
    public void PlaceRawText_refuses_text_longer_than_the_free_stretch()
    {
        var (dump, error) = Type2Memory.PlaceRawText(new byte[200], TagMemory(Ntag213), Ntag213);
        Assert.Null(dump);
        Assert.Contains("200 bytes", error);
    }

    // ------------------------------------------------------------------ Amiibo

    [Fact]
    public void AmiiboPassword_follows_the_serial_formula()
    {
        byte[] uid = [0x04, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];
        Assert.Equal([0xC8, 0x33, 0x8C, 0x77], Type2Memory.AmiiboPassword(uid));
    }

    [Fact]
    public void AmiiboPassword_of_a_short_serial_is_the_default() =>
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], Type2Memory.AmiiboPassword([0x04, 0x01]));

    [Fact]
    public void IsAmiiboDump_needs_the_Nintendo_page_3_and_NTAG215_size()
    {
        var dump = new byte[540];
        dump[12] = 0xF1; dump[13] = 0x10; dump[14] = 0xFF; dump[15] = 0xEE;
        Assert.True(Type2Memory.IsAmiiboDump(dump));
        Assert.True(Type2Memory.LooksAmiibo(dump[..16]));
        Assert.False(Type2Memory.IsAmiiboDump(dump[..400]));
        dump[12] = 0xE1;
        Assert.False(Type2Memory.IsAmiiboDump(dump));
    }

    // ------------------------------------------------------------------ text helpers

    [Fact]
    public void Ranges_joins_runs_of_pages() =>
        Assert.Equal("3, 13-31, 40", Type2Memory.Ranges([3, 13, 14, 15, .. Enumerable.Range(16, 16), 40]));

    [Fact]
    public void Pluralise_capitalises() =>
        Assert.Equal(("Page", "Pages"), (Type2Memory.Pluralise(1, "page"), Type2Memory.Pluralise(3, "page")));

    [Fact]
    public void FormatDump_lists_pages_as_hex_and_ascii()
    {
        Assert.Equal(["000: 41:42:00:7F  AB..", "001: 30:31:32:33  0123"],
            Type2Memory.FormatDump([0x41, 0x42, 0x00, 0x7F, 0x30, 0x31, 0x32, 0x33]));
        Assert.Null(Type2Memory.FormatDump([0x01, 0x02]));
    }

    [Fact]
    public void SamePage_compares_one_page() =>
        Assert.True(Type2Memory.SamePage([0, 0, 0, 0, 1, 2, 3, 4], 1, [1, 2, 3, 4]));
}
