using System.Text.Json;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>A site with a public oEmbed endpoint. <paramref name="Rewrite"/> adjusts the link before it is sent
/// (X only answers for twitter.com addresses).</summary>
internal sealed record OEmbedProvider(string Name, Func<string, bool> HostMatch, string Endpoint, bool HasThumbnail, Func<Uri, Uri>? Rewrite = null);

/// <summary>oEmbed: the JSON description that big sites hand out for embedding a post or video. It is the most
/// reliable source for those sites, because their pages answer a plain request with a consent page, a login
/// wall or an empty app shell. Instagram, Facebook and Threads need a Meta access token for this and are not in
/// the table; their pages are read with a crawler user agent instead (see <see cref="LinkPreviews"/>).</summary>
internal static class OEmbed
{
    internal static readonly OEmbedProvider[] Providers =
    [
        new("YouTube", h => HostIs(h, "youtube.com", "youtu.be"), "https://www.youtube.com/oembed", true),
        new("Vimeo", h => HostIs(h, "vimeo.com"), "https://vimeo.com/api/oembed.json", true),
        new("TikTok", h => HostIs(h, "tiktok.com"), "https://www.tiktok.com/oembed", true),
        new("X", h => HostIs(h, "x.com", "twitter.com"), "https://publish.twitter.com/oembed", false, ToTwitter),
        new("Reddit", h => HostIs(h, "reddit.com", "redd.it"), "https://www.reddit.com/oembed", false),
        new("Spotify", h => HostIs(h, "open.spotify.com", "spotify.link"), "https://open.spotify.com/oembed", true),
        new("SoundCloud", h => HostIs(h, "soundcloud.com"), "https://soundcloud.com/oembed", true),
        new("Flickr", h => HostIs(h, "flickr.com", "flic.kr"), "https://www.flickr.com/services/oembed/", true),
        new("Dailymotion", h => HostIs(h, "dailymotion.com", "dai.ly"), "https://www.dailymotion.com/services/oembed", true),
        new("Bluesky", h => HostIs(h, "bsky.app"), "https://embed.bsky.app/oembed", false),
    ];

    /// <summary>True when the host is one of the domains or a subdomain of one (music.youtube.com, m.vimeo.com).</summary>
    internal static bool HostIs(string host, params string[] domains)
    {
        host = host.ToLowerInvariant();
        return domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    internal static OEmbedProvider? Match(Uri uri) => Providers.FirstOrDefault(p => p.HostMatch(uri.Host));

    private static Uri ToTwitter(Uri uri)
    {
        var builder = new UriBuilder(uri) { Host = "twitter.com", Port = -1 };
        return builder.Uri;
    }

    /// <summary>The preview from a known provider's endpoint, or null when the link is not theirs or they did not
    /// answer usefully.</summary>
    public static async Task<LinkPreview?> TryProviderAsync(Uri uri)
    {
        var provider = Match(uri);
        if (provider is null) return null;
        var target = provider.Rewrite?.Invoke(uri) ?? uri;
        var endpoint = new Uri($"{provider.Endpoint}?format=json&url={Uri.EscapeDataString(target.AbsoluteUri)}");
        return await FetchAsync(endpoint, uri, provider.Name);
    }

    /// <summary>The preview from an oEmbed link that the page itself advertises in its head
    /// (<c>&lt;link rel="alternate" type="application/json+oembed" href="..."&gt;</c>).</summary>
    public static async Task<LinkPreview?> TryDiscoveredAsync(Uri pageUri, string href)
    {
        if (!Uri.TryCreate(pageUri, href.Trim(), out var endpoint) || endpoint.Scheme is not ("http" or "https")) return null;
        return await FetchAsync(endpoint, pageUri, null);
    }

    private static async Task<LinkPreview?> FetchAsync(Uri endpoint, Uri pageUri, string? providerName)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            var json = await LinkPreviews.SendAsync(request, async (response, token) =>
                response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null);
            return json is null ? null : Parse(json, pageUri, providerName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Turns an oEmbed answer into a preview. Posts without a thumbnail (X, Bluesky) carry their text in
    /// the embed HTML; the first paragraph of it becomes the description.</summary>
    internal static LinkPreview? Parse(string json, Uri pageUri, string? providerName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? title = HtmlHead.Clean(Str(root, "title"));
        string? author = HtmlHead.Clean(Str(root, "author_name"));
        string? provider = HtmlHead.Clean(Str(root, "provider_name")) ?? providerName;
        string? thumb = Str(root, "thumbnail_url");
        string? html = Str(root, "html");

        string? postText = null;
        if (string.IsNullOrWhiteSpace(thumb) && !string.IsNullOrWhiteSpace(html))
        {
            var p = Regex.Match(html, @"<p\b[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (p.Success) postText = HtmlHead.StripTags(p.Groups[1].Value);
        }

        string? imageUrl = null;
        if (!string.IsNullOrWhiteSpace(thumb) && Uri.TryCreate(pageUri, thumb.Trim(), out var abs) && abs.Scheme is "http" or "https")
            imageUrl = abs.AbsoluteUri;

        if (title is null && imageUrl is null && postText is null) return null;

        // A post has no title of its own: show who wrote it as the title and the text below.
        if (title is null && postText is not null && author is not null) title = author;
        if (title is { Length: > 120 }) title = title[..117].TrimEnd() + "...";
        if (postText is { Length: > 220 }) postText = postText[..217].TrimEnd() + "...";

        // Keeps the "YouTube  ·  channel" style when there is nothing better to say.
        string? byline = (provider, author) switch
        {
            (not null, not null) => $"{provider}  ·  {author}",
            (not null, null) => provider,
            (null, not null) => author,
            _ => null,
        };

        return new LinkPreview(pageUri.AbsoluteUri, pageUri.Host, title, postText ?? byline, imageUrl, DateTime.UtcNow,
            SiteName: provider);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
