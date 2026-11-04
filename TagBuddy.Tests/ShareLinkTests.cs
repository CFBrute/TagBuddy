using System.IO.Compression;
using System.Text;
using TagBuddy.Models;
using TagBuddy.Services;
using Xunit.Abstractions;

namespace TagBuddy.Tests;

/// <summary>Share links: a saved item packed into a link and unpacked on another phone, and links that must be
/// refused because they are damaged or made up.</summary>
public class ShareLinkTests(ITestOutputHelper output)
{
    [Fact]
    public void A_QR_code_with_its_own_look_arrives_as_it_was()
    {
        var item = new SavedItem
        {
            Kind = SavedKind.Code, Format = "QrCode", Value = "https://example.com/ticket?id=42", Name = "Concert",
            QrErrorCorrection = "H", QrMargin = 4, Caption = "Row 7\nSeat 12", CaptionAlign = "Left", CaptionTextScale = 0.3,
        };

        var link = ShareLink.Create(item);
        output.WriteLine(link);
        Assert.StartsWith(ShareLink.PageUrl + "#1.", link);

        var back = ShareLink.Parse(link)!.ToSavedItem();
        Assert.NotEqual(item.Id, back.Id);
        Assert.Equal(SavedKind.Code, back.Kind);
        Assert.Equal("Concert", back.Name);
        Assert.True(back.IsSameCode(item));
        Assert.Empty(back.ImageDifferences(item));
        Assert.Equal("", back.Group);
    }

    [Fact]
    public void A_plain_barcode_link_is_short()
    {
        var link = ShareLink.Create(new SavedItem { Kind = SavedKind.Code, Format = "Code128", Value = "23943727" });
        output.WriteLine(link);
        Assert.True(link.Length < 100, $"{link.Length} characters");

        var back = ShareLink.Parse(link)!.ToSavedItem();
        Assert.Equal("Code128", back.Format);
        Assert.Equal("23943727", back.Value);
        Assert.Null(back.Caption);   // "same as the value", as it was
    }

    [Fact]
    public void A_text_note_arrives_as_a_text_note()
    {
        var link = ShareLink.Create(new SavedItem { Kind = SavedKind.Text, Value = "Door code 4711 — smörgås ✓" });
        var back = ShareLink.Parse(link)!;
        Assert.True(back.IsText);
        Assert.Null(back.Name);
        Assert.Equal("Door code 4711 — smörgås ✓", back.ToSavedItem().Value);
    }

    [Fact]
    public void The_app_link_from_the_share_page_holds_the_same_item()
    {
        var link = ShareLink.Create(new SavedItem { Kind = SavedKind.Text, Value = "hello" });
        var payload = link[(link.IndexOf('#') + 1)..];

        Assert.Equal("hello", ShareLink.Parse($"tagbuddy://import?d={payload}")!.Value);
        Assert.Equal("hello", ShareLink.Parse($"{ShareLink.PageUrl}?noapp#{payload}")!.Value);
    }

    [Fact]
    public void A_dump_carries_its_memory_and_its_read()
    {
        var memory = Enumerable.Range(0, 540).Select(i => (byte)i).ToArray();
        var read = TagReadFile.From(new NfcTagInfo("04:A2:B3:C4:D5:E6:F7", ["NfcA"], [], [])).ToJson();
        var item = new SavedItem { Kind = SavedKind.Dump, Name = "Fob", DumpChip = "NTAG215", DumpSerial = "04:A2:B3:C4:D5:E6:F7" };

        var link = ShareLink.CreateForDump(item, memory, read, out var readLeftOut)!;
        Assert.False(readLeftOut);

        var back = ShareLink.Parse(link)!;
        Assert.True(back.IsDump);
        Assert.Equal(memory, back.DumpBytes);
        Assert.Equal("NTAG215", back.DumpChip);
        Assert.Equal("04:A2:B3:C4:D5:E6:F7", back.DumpSerial);
        Assert.Equal(read, back.ReadJson);
    }

    [Fact]
    public void A_read_too_big_for_a_link_is_left_out_and_the_memory_still_goes()
    {
        var memory = new byte[1024];
        // Random text does not compress, so this read alone is far longer than a link may be.
        var random = new byte[40_000];
        new Random(1).NextBytes(random);
        var noise = Convert.ToBase64String(random);
        var read = TagReadFile.From(new NfcTagInfo("0102", ["NfcA"], [], [], MemoryDump: [noise])).ToJson();

        var link = ShareLink.CreateForDump(new SavedItem { Kind = SavedKind.Dump, DumpChip = "NTAG215" }, memory, read, out var readLeftOut)!;

        Assert.True(readLeftOut);
        Assert.True(link.Length <= ShareLink.MaxLinkLength);
        Assert.Null(ShareLink.Parse(link)!.ReadJson);
        Assert.Equal(memory, ShareLink.Parse(link)!.DumpBytes);
    }

    [Fact]
    public void A_dump_bigger_than_a_link_carries_gets_no_link()
    {
        var item = new SavedItem { Kind = SavedKind.Dump, DumpChip = "Huge" };
        Assert.Null(ShareLink.CreateForDump(item, new byte[ShareLink.MaxDumpBytes + 1], null, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/#1.abc")]                         // not the share page
    [InlineData("https://cfbrute.github.io/TagBuddy/s/")]              // no item
    [InlineData("https://cfbrute.github.io/TagBuddy/s/#2.abc")]        // a newer version
    [InlineData("https://cfbrute.github.io/TagBuddy/s/#1.!!!")]        // not base64url
    [InlineData("https://cfbrute.github.io/TagBuddy/s/#1.q2V")]        // cut short
    [InlineData("tagbuddy://import")]
    public void Links_without_a_valid_item_give_nothing(string? link) =>
        Assert.Null(ShareLink.Parse(link));

    [Fact]
    public void A_link_cut_off_in_the_middle_gives_nothing()
    {
        var link = ShareLink.Create(new SavedItem { Kind = SavedKind.Text, Value = string.Join(" ", Enumerable.Range(0, 200)) });
        Assert.Null(ShareLink.Parse(link[..(link.Length * 2 / 3)]));
    }

    private static string Craft(string json)
    {
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(json));
        return ShareLink.PageUrl + "#1." + Convert.ToBase64String(packed.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Theory]
    [InlineData("""{"k":"code","f":"NoSuchFormat","t":"x"}""")]          // a format the app does not know
    [InlineData("""{"k":"code","f":"Ean13","t":"not digits"}""")]        // a code that cannot be drawn
    [InlineData("""{"k":"code","f":"QrCode","t":"x","qe":"Z"}""")]       // no such error correction
    [InlineData("""{"k":"text","t":"   "}""")]                           // nothing in it
    [InlineData("""{"k":"text"}""")]
    [InlineData("""{"k":"wifi","t":"x"}""")]                             // a kind from a newer app
    [InlineData("""{"k":"dump","b":"not base64!"}""")]
    [InlineData("""{"k":"dump","b":"AAAA","s":"<script>"}""")]           // a serial that is not hex
    [InlineData("""not json""")]
    public void Made_up_items_are_refused(string json) =>
        Assert.Null(ShareLink.Parse(Craft(json)));

    [Fact]
    public void A_made_up_look_is_brought_into_range()
    {
        var shared = ShareLink.Parse(Craft("""{"k":"code","f":"QrCode","t":"x","qm":99,"ca":"Upside","cf":"comic","cs":50,"cw":-3}"""))!;
        var item = shared.ToSavedItem();

        Assert.Equal(4, item.QrMargin);
        Assert.Equal("Center", item.CaptionAlign);
        Assert.Equal(CaptionFonts.Default, item.CaptionFont);
        Assert.Equal(SavedItem.MaxCaptionTextScale, item.CaptionTextScale);
        Assert.Equal(SavedItem.MinCaptionWidthScale, item.CaptionWidthScale);
    }

    [Fact]
    public void A_long_name_is_cut_and_a_broken_read_is_dropped()
    {
        var shared = ShareLink.Parse(Craft($$"""{"k":"dump","b":"AAECAw==","n":"{{new string('a', 100)}}","r":"{broken"}"""))!;
        Assert.Equal(60, shared.Name!.Length);
        Assert.Null(shared.ReadJson);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, shared.DumpBytes);
    }

    [Fact]
    public void A_zip_bomb_is_refused()
    {
        // A megabyte of spaces packs into about a kilobyte.
        Assert.Null(ShareLink.Parse(Craft("""{"k":"text","t":"x","n":"a"}""" + new string(' ', 1_000_000))));
    }

    [Fact]
    public void An_incoming_link_waits_once_and_a_damaged_one_is_reported()
    {
        IncomingShare.Take();   // nothing left over from another test
        Assert.False(IncomingShare.Offer("https://example.com/"));
        Assert.False(IncomingShare.HasPending);

        Assert.True(IncomingShare.Offer(ShareLink.Create(new SavedItem { Kind = SavedKind.Text, Value = "hi" })));
        Assert.Equal("hi", IncomingShare.Take().Item!.Value);
        Assert.Equal((null, false), IncomingShare.Take());

        Assert.True(IncomingShare.Offer(ShareLink.PageUrl + "#1.garbage"));
        Assert.Equal((null, true), IncomingShare.Take());
    }

    [Fact]
    public void The_save_dialog_says_what_arrived()
    {
        var (what, content) = CodeActions.Describe(ShareLink.Parse(ShareLink.Create(
            new SavedItem { Kind = SavedKind.Code, Format = "Ean13", Value = "4006381333931" }))!);
        Assert.Equal("EAN-13", what);
        Assert.Equal("4006381333931", content);

        var dump = ShareLink.Parse(Craft("""{"k":"dump","b":"AAECAw==","ch":"NTAG215","s":"04A2"}"""))!;
        Assert.Equal(("NTAG215 backup", "4 bytes, serial 04A2."), CodeActions.Describe(dump));
    }
}
