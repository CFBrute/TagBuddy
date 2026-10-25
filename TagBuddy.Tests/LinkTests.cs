using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>What the app treats as a link, which links it must never request on its own, and how it recognises
/// sites from their address.</summary>
public class LinkTests
{
    [Theory]
    [InlineData("example.com/page", "https://example.com/page")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("  https://example.com  ", "https://example.com/")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("tel:+46700000000", "tel:+46700000000")]
    [InlineData("geo:59.33,18.06", "geo:59.33,18.06")]
    public void TryGetOpenable_accepts_what_the_phone_can_open(string value, string expected)
    {
        Assert.True(Links.TryGetOpenable(value, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hello world")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///sdcard/secret.txt")]
    [InlineData("ftp://example.com/file")]
    [InlineData("WIFI:S:home;T:WPA;P:secret;;")]
    public void TryGetOpenable_rejects_everything_else(string? value) =>
        Assert.False(Links.TryGetOpenable(value, out _));

    [Fact]
    public void IsWebLink_is_only_http_and_https()
    {
        Assert.True(Links.IsWebLink("http://example.com", out _));
        Assert.False(Links.IsWebLink("tel:123", out _));
    }

    // ------------------------------------------------------------------ single-use links (never requested)

    [Theory]
    [InlineData("https://example.com/login?token=abc123")]
    [InlineData("https://example.com/auth?code=xyz&state=1")]
    [InlineData("https://example.com/cb#access_token=abc")]
    [InlineData("https://example.com/reset/abc")]
    [InlineData("https://example.com/account/verify")]
    [InlineData("https://news.example.com/unsubscribe?u=1")]
    [InlineData("https://example.com/invite/accept/42")]
    public void IsSingleUse_spots_sign_in_and_one_time_links(string link) =>
        Assert.True(Links.IsSingleUse(new Uri(link)));

    [Theory]
    [InlineData("https://example.com/news?id=5")]
    [InlineData("https://example.com/resettlement-programme")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42")]           // a timestamp, not a token
    [InlineData("https://example.com/verifying-sources")]
    public void IsSingleUse_leaves_ordinary_links_alone(string link) =>
        Assert.False(Links.IsSingleUse(new Uri(link)));

    // ------------------------------------------------------------------ known sites

    [Theory]
    [InlineData("https://youtu.be/abc", "YouTube · Video")]
    [InlineData("https://www.youtube.com/shorts/abc", "YouTube · Short")]
    [InlineData("https://m.youtube.com/watch?v=abc", "YouTube · Video")]
    [InlineData("https://www.youtube.com/", "YouTube")]
    [InlineData("https://open.spotify.com/track/abc", "Spotify · Song")]
    [InlineData("https://wa.me/46700000000", "WhatsApp · Chat")]
    [InlineData("https://www.amazon.se/dp/B000", "Amazon · Product")]
    [InlineData("https://x.com/someone/status/1", "X · Post")]
    public void KnownSites_Describe_names_site_and_kind(string link, string expected) =>
        Assert.Equal(expected, KnownSites.Describe(new Uri(link)));

    [Fact]
    public void KnownSites_Describe_says_a_short_link_leads_elsewhere() =>
        Assert.StartsWith("Short link (bit.ly)", KnownSites.Describe(new Uri("https://bit.ly/3xyz")));

    [Fact]
    public void KnownSites_Describe_knows_nothing_of_other_sites() =>
        Assert.Null(KnownSites.Describe(new Uri("https://example.com/")));

    [Theory]
    [InlineData("https://youtu.be/abc", "youtube.com")]
    [InlineData("https://m.youtube.com/", "youtube.com")]
    [InlineData("https://redd.it/abc", "reddit.com")]
    [InlineData("https://amzn.to/abc", "amazon.com")]
    [InlineData("https://www.example.com/", "example.com")]
    [InlineData("https://m.example.com/", "example.com")]
    [InlineData("https://amp.example.co.uk/", "example.co.uk")]
    [InlineData("https://shop.example.com/", "shop.example.com")]
    [InlineData("https://m.co/", "m.co")]
    public void KnownSites_IconHost_shares_one_icon_between_addresses(string link, string expected) =>
        Assert.Equal(expected, KnownSites.IconHost(new Uri(link)));

    [Fact]
    public void KnownSites_IsShortener_is_by_host()
    {
        Assert.True(KnownSites.IsShortener(new Uri("https://t.co/abc")));
        Assert.False(KnownSites.IsShortener(new Uri("https://microsoft.com/")));   // ends with "t.co" but is not it
    }

    // ------------------------------------------------------------------ icons

    [Theory]
    [InlineData("https://shop.example.com/x", "https://example.com/")]
    [InlineData("https://m.example.co.uk/", "https://example.co.uk/")]
    [InlineData("https://a.b.example.com/", "https://b.example.com/")]
    [InlineData("https://news.bbc.co.uk/", "https://bbc.co.uk/")]
    public void ParentSite_is_one_level_up(string link, string expected) =>
        Assert.Equal(expected, Favicons.ParentSite(new Uri(link))?.ToString());

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://www.example.com/")]
    [InlineData("https://example.co.uk/")]          // its parent would be the suffix co.uk
    [InlineData("https://192.168.1.1/")]
    public void ParentSite_is_none_for_a_site_of_its_own(string link) =>
        Assert.Null(Favicons.ParentSite(new Uri(link)));

    [Theory]
    [InlineData("16x16 32x32", 32)]
    [InlineData("180x180", 180)]
    [InlineData("any", 0)]
    [InlineData(null, 0)]
    public void LargestSize_reads_the_sizes_attribute(string? sizes, int expected) =>
        Assert.Equal(expected, Favicons.LargestSize(sizes));

    [Fact]
    public void HandlerName_is_unknown_off_Android() =>
        Assert.Null(Links.HandlerName(new Uri("https://example.com")));
}