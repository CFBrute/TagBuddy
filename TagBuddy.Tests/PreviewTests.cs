using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>Reading previews from HTML, and judging rendered pictures.</summary>
public class PreviewTests
{
    private static readonly Uri Page = new("https://example.com/articles/1");

    [Fact]
    public void ReadPreview_prefers_Open_Graph_and_resolves_the_picture()
    {
        var html = """
            <html><head>
            <title>Plain title</title>
            <meta property="og:title" content="Card title &amp; more">
            <meta content="A description." name="description">
            <meta property="og:image" content="/img/share.jpg">
            <meta property="og:site_name" content="Example">
            <link rel="alternate" type="application/json+oembed" href="https://example.com/oembed?u=1">
            <link rel="icon" href="/favicon-32.png" sizes="32x32">
            </head><body>ignored</body></html>
            """;
        var (preview, tags, oembed) = HtmlHead.ReadPreview(html, Page);

        Assert.Equal("Card title & more", preview.Title);
        Assert.Equal("A description.", preview.Description);
        Assert.Equal("https://example.com/img/share.jpg", preview.ImageUrl);
        Assert.Equal("Example", preview.SiteName);
        Assert.Equal("https://example.com/oembed?u=1", oembed);
        Assert.Contains(tags, t => HtmlHead.HasRel(t, "icon") && t.Sizes == "32x32");
    }

    [Fact]
    public void ReadPreview_falls_back_to_the_title_tag_and_shortens_long_text()
    {
        var html = $"<head><title> Just   a title </title><meta name=\"description\" content=\"{new string('x', 400)}\"></head>";
        var (preview, _, oembed) = HtmlHead.ReadPreview(html, Page);

        Assert.Equal("Just a title", preview.Title);
        Assert.Equal(220, preview.Description!.Length);
        Assert.EndsWith("...", preview.Description);
        Assert.Null(preview.ImageUrl);
        Assert.Null(oembed);
    }

    [Fact]
    public void ReadPreview_ignores_a_picture_that_is_not_on_the_web()
    {
        var (preview, _, _) = HtmlHead.ReadPreview("<meta property=\"og:image\" content=\"javascript:alert(1)\">", Page);
        Assert.Null(preview.ImageUrl);
    }

    [Fact]
    public void LinkTags_reads_any_attribute_order_and_quoting()
    {
        var tags = HtmlHead.LinkTags("<link href='/a.png' rel=\"apple-touch-icon\" sizes=180x180><link rel=icon>");
        var tag = Assert.Single(tags);   // the second has no href
        Assert.Equal(("apple-touch-icon", "/a.png", "180x180"), (tag.Rel, tag.Href, tag.Sizes));
    }

    [Fact]
    public void StripTags_leaves_readable_text() =>
        Assert.Equal("Hi there & you", HtmlHead.StripTags("<b>Hi</b><br>there &amp; <i>you</i>"));

    // ------------------------------------------------------------------ bot checks

    [Theory]
    [InlineData("Just a moment...", true)]
    [InlineData("Attention Required! | Cloudflare", true)]
    [InlineData("My holiday photos", false)]
    [InlineData(null, false)]
    public void Bot_check_pages_are_recognised(string? title, bool expected) =>
        Assert.Equal(expected, PageInfo.IsInterstitialTitle(title));

    // ------------------------------------------------------------------ judging a snapshot

    /// <summary>A white 160×160 picture with <paramref name="inked"/> of the 100 grid samples painted black.</summary>
    private static int[] Picture(int inked)
    {
        const int size = 160, white = unchecked((int)0xFFFFFFFF), black = unchecked((int)0xFF000000);
        var pixels = Enumerable.Repeat(white, size * size).ToArray();
        int n = 0;
        for (int y = 8; y < size && n < inked; y += 16)
            for (int x = 8; x < size && n < inked; x += 16, n++)
                pixels[y * size + x] = black;
        return pixels;
    }

    [Theory]
    [InlineData(0, SnapshotContent.Blank)]
    [InlineData(1, SnapshotContent.Blank)]
    [InlineData(5, SnapshotContent.Sparse)]
    [InlineData(30, SnapshotContent.Rich)]
    public void Classify_judges_how_much_is_on_the_picture(int inked, SnapshotContent expected) =>
        Assert.Equal(expected, PageSnapshot.Classify(Picture(inked), 160, 160));

    [Fact]
    public void ParseCssColor_reads_computed_styles()
    {
        Assert.Equal(((byte)255, (byte)0, (byte)10, (byte)255), PageSnapshot.ParseCssColor("rgb(255, 0, 10)"));
        Assert.Equal((byte)0, PageSnapshot.ParseCssColor("rgba(0, 0, 0, 0)")!.Value.A);
        Assert.Equal((byte)128, PageSnapshot.ParseCssColor("rgba(10, 20, 30, 50%)")!.Value.A);
        Assert.Null(PageSnapshot.ParseCssColor("red"));
    }
}
