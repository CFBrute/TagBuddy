using System.Net;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>One <c>&lt;link&gt;</c> tag from a page head: what it is (rel), where it points, and the optional
/// type and sizes hints that icon links carry.</summary>
public readonly record struct LinkTag(string Rel, string Href, string? Type, string? Sizes);

/// <summary>Reads the few things a link preview needs out of a page's head. Regex is enough here: only meta
/// tags, link tags and the title are wanted, and a page that confuses it just gets a plainer preview. Pure
/// functions without MAUI dependencies, so a test project can cover them later.</summary>
internal static class HtmlHead
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static readonly Regex LinkTagRegex = new(@"<link\b[^>]*>", Options | RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(
        @"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))",
        Options | RegexOptions.Compiled);

    /// <summary>The text up to <c>&lt;/head&gt;</c>, or the whole document when there is no such tag.</summary>
    internal static string Head(string html)
    {
        int headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headEnd > 0 ? html[..headEnd] : html;
    }

    /// <summary>The content of a meta tag such as <c>&lt;meta property="og:title" content="..."&gt;</c>.</summary>
    internal static string? Meta(string head, string attribute, string name)
    {
        // Both attribute orders: <meta property="og:title" content="..."> and <meta content="..." property="og:title">
        var escaped = Regex.Escape(name);
        // The value stops at its own closing quote: with ".*?" a content-first tag could run on into the tag before.
        const string content = @"\bcontent\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')";
        var m = Regex.Match(head,
            $@"<meta\b[^>]*?\b{attribute}\s*=\s*([""'])\s*{escaped}\s*\1[^>]*?{content}", Options);
        if (!m.Success)
            m = Regex.Match(head,
                $@"<meta\b[^>]*?{content}[^>]*?\b{attribute}\s*=\s*([""'])\s*{escaped}\s*\1", Options);
        if (!m.Success) return null;
        var value = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>An Open Graph style tag under either attribute: many sites write <c>name="og:image"</c> instead of
    /// the specified <c>property="og:image"</c>, and some Twitter tags use <c>property</c>.</summary>
    internal static string? Property(string head, string name) =>
        Meta(head, "property", name) ?? Meta(head, "name", name);

    internal static string? TitleTag(string head)
    {
        var m = Regex.Match(head, @"<title[^>]*>(.*?)</title>", Options);
        if (!m.Success) return null;
        var value = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Every link tag in the head with an href, attributes in any order and any quoting.</summary>
    internal static IReadOnlyList<LinkTag> LinkTags(string head)
    {
        var tags = new List<LinkTag>();
        foreach (Match tag in LinkTagRegex.Matches(head))
        {
            string? rel = null, href = null, type = null, sizes = null;
            foreach (Match a in AttributeRegex.Matches(tag.Value))
            {
                var value = WebUtility.HtmlDecode(a.Groups["v"].Value).Trim();
                switch (a.Groups["name"].Value.ToLowerInvariant())
                {
                    case "rel": rel = value; break;
                    case "href": href = value; break;
                    case "type": type = value; break;
                    case "sizes": sizes = value; break;
                }
            }
            if (!string.IsNullOrEmpty(rel) && !string.IsNullOrEmpty(href))
                tags.Add(new LinkTag(rel, href, string.IsNullOrEmpty(type) ? null : type, string.IsNullOrEmpty(sizes) ? null : sizes));
        }
        return tags;
    }

    /// <summary>True when the space-separated rel list of a link tag contains the given token.</summary>
    internal static bool HasRel(LinkTag tag, string token) =>
        tag.Rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(r => r.Equals(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Collapses whitespace; null for empty text.</summary>
    internal static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The text of an HTML fragment with its tags removed and entities decoded.</summary>
    internal static string? StripTags(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Regex.Replace(html, @"<br\s*/?>", " ", Options);
        text = Regex.Replace(text, @"<[^>]+>", "", Options);
        return Clean(WebUtility.HtmlDecode(text));
    }

    /// <summary>Picks the preview fields out of the page's head, plus its link tags (for the icon) and the
    /// oEmbed link it advertises, if any.</summary>
    internal static (LinkPreview Preview, IReadOnlyList<LinkTag> LinkTags, string? OEmbedHref) ReadPreview(string html, Uri pageUri)
    {
        var head = Head(html);
        var tags = LinkTags(head);

        var title = Property(head, "og:title") ?? Property(head, "twitter:title") ?? TitleTag(head);
        var description = Property(head, "og:description") ?? Meta(head, "name", "description")
                          ?? Property(head, "twitter:description");
        var image = Property(head, "og:image:secure_url") ?? Property(head, "og:image")
                    ?? Property(head, "og:image:url") ?? Property(head, "twitter:image")
                    ?? Property(head, "twitter:image:src")
                    ?? tags.FirstOrDefault(t => HasRel(t, "image_src")).Href;
        var siteName = Property(head, "og:site_name") ?? Meta(head, "name", "application-name");
        var oembed = tags.FirstOrDefault(t => HasRel(t, "alternate")
                                              && string.Equals(t.Type, "application/json+oembed", StringComparison.OrdinalIgnoreCase)).Href;

        if (description is { Length: > 220 }) description = description[..217].TrimEnd() + "...";
        if (title is { Length: > 120 }) title = title[..117].TrimEnd() + "...";

        string? imageUrl = null;
        if (!string.IsNullOrWhiteSpace(image) && Uri.TryCreate(pageUri, image.Trim(), out var abs) && abs.Scheme is "http" or "https")
            imageUrl = abs.AbsoluteUri;

        var preview = new LinkPreview(pageUri.AbsoluteUri, pageUri.Host, Clean(title), Clean(description),
            imageUrl, DateTime.UtcNow, SiteName: Clean(siteName));
        return (preview, tags, string.IsNullOrWhiteSpace(oembed) ? null : oembed);
    }
}
