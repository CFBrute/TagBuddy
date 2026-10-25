using System.Text;
using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Dumps coming in from files, the clipboard or a text note, before they go near a tag.</summary>
public class DumpTests
{
    [Theory]
    [InlineData(15, "Only 15 bytes")]
    [InlineData(18, "not a whole number")]
    [InlineData(4100, "far more")]
    public void Type2Dump_Problem_explains_a_wrong_size(int size, string expected) =>
        Assert.Contains(expected, Type2Dump.Problem(new byte[size]));

    [Theory]
    [InlineData(16)]
    [InlineData(540)]
    public void Type2Dump_Problem_accepts_whole_pages(int size) =>
        Assert.Null(Type2Dump.Problem(new byte[size]));

    [Fact]
    public void Type2Dump_Serial_skips_the_check_bytes()
    {
        byte[] data = [0x04, 0xA1, 0xB2, 0x99, 0xC3, 0xD4, 0xE5, 0xF6, 0x88, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal("04:A1:B2:C3:D4:E5:F6", Type2Dump.Serial(data));
    }

    [Fact]
    public void Type2Dump_Serial_is_empty_for_a_dump_made_from_text() =>
        Assert.Equal("", Type2Dump.Serial(DumpText.FromText("hello")));

    [Fact]
    public void DumpText_FromText_leaves_pages_0_to_3_zero_and_pads()
    {
        var dump = DumpText.FromText("Hi!!x");
        Assert.Equal(16 + 8, dump.Length);
        Assert.All(dump[..16], b => Assert.Equal(0, b));
        Assert.Equal("Hi!!x"u8.ToArray(), dump[16..21]);
    }

    [Fact]
    public void DumpText_Describe_finds_words_written_as_raw_bytes() =>
        Assert.Equal("Hello world here", DumpText.Describe(DumpText.FromText("Hello world here")));

    [Fact]
    public void DumpText_Describe_ignores_random_bytes()
    {
        var noise = new byte[64];
        new Random(7).NextBytes(noise);
        for (int i = 0; i < noise.Length; i++) if (noise[i] is >= 0x20 and < 0x7F && i % 3 != 0) noise[i] = 0x01;
        Assert.Equal("", DumpText.Describe(noise));
    }

    // ------------------------------------------------------------------ hex from the clipboard

    [Fact]
    public void ParseHex_reads_the_apps_own_page_listing()
    {
        var text = "000: 04:A1:B2:99  ....\n001: C3:D4:E5:F6  ....\n";
        Assert.Equal([0x04, 0xA1, 0xB2, 0x99, 0xC3, 0xD4, 0xE5, 0xF6], SavedService.ParseHex(text));
    }

    [Fact]
    public void ParseHex_keeps_the_first_byte_of_plain_colon_hex() =>
        Assert.Equal([0x04, 0x1F, 0x2A, 0x3B], SavedService.ParseHex("04:1F:2A:3B"));

    [Theory]
    [InlineData("0300FE00")]
    [InlineData("03 00 fe 00")]
    [InlineData("03-00-FE-00")]
    public void ParseHex_reads_plain_hex(string text) =>
        Assert.Equal([0x03, 0x00, 0xFE, 0x00], SavedService.ParseHex(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("not hex at all")]
    public void ParseHex_rejects_what_is_not_hex(string? text) =>
        Assert.Null(SavedService.ParseHex(text));

    /// <summary>An NTAG215 image with the Amiibo page 3: 135 pages, F1 10 FF EE in page 3.</summary>
    private static byte[] AmiiboShaped()
    {
        var data = new byte[540];
        data[12] = 0xF1; data[13] = 0x10; data[14] = 0xFF; data[15] = 0xEE;
        return data;
    }

    [Fact]
    public void A_short_name_written_raw_on_a_former_Amiibo_tag_is_read()
    {
        // Raw text in page 4, the pages an Amiibo writer locked (13-31) still holding old game bytes, the rest zero.
        var data = AmiiboShaped();
        new Random(1).NextBytes(data.AsSpan(13 * 4, 19 * 4));
        Encoding.UTF8.GetBytes("Carlos").CopyTo(data, 16);
        Assert.Equal("Carlos", DumpText.Describe(data));
    }

    [Fact]
    public void Game_data_does_not_make_up_short_words()
    {
        // Filled with random-looking bytes, as encrypted Amiibo data is, with a six-letter run inside it.
        var data = AmiiboShaped();
        new Random(2).NextBytes(data.AsSpan(16, 504));
        for (int i = 16; i < 520; i++) if (data[i] is >= 0x20 and < 0x7F) data[i] = 0x80;   // no accidental runs
        Encoding.ASCII.GetBytes("Qwerty").CopyTo(data, 200);
        Assert.Equal("", DumpText.Describe(data));
    }
}
