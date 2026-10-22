using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>What a web page says about itself: title, description and picture (Open Graph, Twitter cards, or
/// the plain title tag). Only the fields the page provides are set.</summary>
public sealed record LinkPreview(string Url, string Host, string? Title, string? Description, string? ImageUrl, DateTime FetchedAt)
{
    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
    public string TitleOrHost => string.IsNullOrWhiteSpace(Title) ? Host : Title;
}

/// <summary>Fetches link previews with an in-memory and on-disk cache, so the favorites list and repeated
/// scans of the same code do not hit the network again. Nothing but a plain GET of the page is sent.</summary>
public static class LinkPreviews
{
    private const int MaxHtmlBytes = 512 * 1024;
    private static readonly TimeSpan DiskCacheLifetime = TimeSpan.FromDays(7);

    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<LinkPreview?>> Memory = new(StringComparer.Ordinal);
    private static readonly string CacheDir = Path.Combine(FileSystem.CacheDirectory, "link-previews");

    /// <summary>True for values that are web links, the only kind a page preview makes sense for.</summary>
    public static bool IsPreviewable(string? value, out Uri uri) => Links.IsWebLink(value, out uri);

    /// <summary>The preview for a web link, or null when the page could not be reached or read.</summary>
    public static Task<LinkPreview?> GetAsync(Uri uri) =>
        Memory.GetOrAdd(uri.AbsoluteUri, _ => FetchAsync(uri));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Linux; Android 14; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36 TagBuddy/1.0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en,*;q=0.5");
        return client;
    }

    private static async Task<LinkPreview?> FetchAsync(Uri uri)
    {
        var cached = await ReadDiskAsync(uri);
        if (cached is not null && (!cached.HasImage || !IsLocalPath(cached.ImageUrl!) || File.Exists(cached.ImageUrl!)))
            return cached;

        // Video sites answer a plain request with a consent page in many countries; their oEmbed endpoint
        // hands out the title, channel and thumbnail without any of that.
        var video = await TryYouTubeAsync(uri);
        if (video is not null)
        {
            await WriteDiskAsync(uri, video);
            return video;
        }

        LinkPreview? preview;
        bool persist = true;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
            {
                // A plain request was refused, but the page may still render in a browser engine (bot
                // checks, login walls, odd servers). Not kept on disk: a 503 today says nothing about next week.
                persist = false;
                var status = $"The page answered {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                preview = new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, null, status, null, DateTime.UtcNow);
            }
            else if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                // Not a page but a file: say so, and never try to render it.
                var size = response.Content.Headers.ContentLength;
                preview = new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, Path.GetFileName(finalUri.AbsolutePath),
                    $"Download: {DescribeMediaType(mediaType)}{(size is > 0 ? $", {FormatSize(size.Value)}" : "")}",
                    mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? finalUri.AbsoluteUri : null,
                    DateTime.UtcNow);
                await WriteDiskAsync(uri, preview);
                return preview;
            }
            else
            {
                var html = await ReadHtmlAsync(response);
                preview = Parse(html, finalUri);
            }
        }
        catch
        {
            // Offline, blocked, timed out, or not a real site. A browser engine may still manage; if not,
            // there is simply no preview, and a later attempt may try again.
            persist = false;
            preview = new LinkPreview(uri.AbsoluteUri, uri.Host, null, null, null, DateTime.UtcNow);
        }

        if (!preview.HasImage)
        {
            var snapshot = await PageSnapshot.CaptureAsync(uri, CacheKey(uri));
            if (snapshot is not null)
                preview = preview with { ImageUrl = snapshot, Description = preview.Description ?? (preview.Title is null ? "Tap to open the page." : null) };
        }

        if (!persist)
        {
            Memory.TryRemove(uri.AbsoluteUri, out _);
            return preview.HasImage || preview.Description is not null ? preview : null;
        }

        await WriteDiskAsync(uri, preview);
        return preview;
    }

    /// <summary>True when the picture is a file this app rendered rather than a web address.</summary>
    public static bool IsLocalPath(string imageUrl) => imageUrl.StartsWith('/') || imageUrl.Contains(":\\");

    private static async Task<LinkPreview?> TryYouTubeAsync(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        bool isYouTube = host is "youtu.be" || host.EndsWith("youtube.com");
        if (!isYouTube) return null;
        try
        {
            var oembed = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(uri.AbsoluteUri);
            using var response = await Http.GetAsync(oembed);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string? title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? author = root.TryGetProperty("author_name", out var a) ? a.GetString() : null;
            string? thumb = root.TryGetProperty("thumbnail_url", out var th) ? th.GetString() : null;
            if (title is null && thumb is null) return null;
            return new LinkPreview(uri.AbsoluteUri, uri.Host, Clean(title),
                author is null ? "YouTube" : $"YouTube  ·  {author}", thumb, DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static string CacheKey(Uri uri) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32];

    private static async Task<string> ReadHtmlAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxHtmlBytes && (read = await stream.ReadAsync(chunk)) > 0)
            buffer.Write(chunk, 0, read);

        var bytes = buffer.ToArray();
        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(charset))
        {
            // Sniff a <meta charset> in the first bytes.
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*([a-zA-Z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success) charset = m.Groups[1].Value;
        }
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); } catch { /* unknown name: keep UTF-8 */ }
        }
        return encoding.GetString(bytes);
    }

    /// <summary>Picks the preview fields out of the page's head. Regex is enough here: only meta tags and the
    /// title are wanted, and a page that confuses it just gets a plainer preview.</summary>
    internal static LinkPreview Parse(string html, Uri pageUri)
    {
        int headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var head = headEnd > 0 ? html[..headEnd] : html;

        var title = Meta(head, "property", "og:title") ?? Meta(head, "name", "twitter:title") ?? TitleTag(head);
        var description = Meta(head, "property", "og:description") ?? Meta(head, "name", "description") ?? Meta(head, "name", "twitter:description");
        var image = Meta(head, "property", "og:image") ?? Meta(head, "property", "og:image:url") ?? Meta(head, "name", "twitter:image");

        if (description is { Length: > 220 }) description = description[..217].TrimEnd() + "...";
        if (title is { Length: > 120 }) title = title[..117].TrimEnd() + "...";

        string? imageUrl = null;
        if (!string.IsNullOrWhiteSpace(image) && Uri.TryCreate(pageUri, image.Trim(), out var abs) && abs.Scheme is "http" or "https")
            imageUrl = abs.AbsoluteUri;

        return new LinkPreview(pageUri.AbsoluteUri, pageUri.Host, Clean(title), Clean(description), imageUrl, DateTime.UtcNow);
    }

    private static string? Meta(string head, string attribute, string name)
    {
        // Both attribute orders: <meta property="og:title" content="..."> and <meta content="..." property="og:title">
        var escaped = Regex.Escape(name);
        var m = Regex.Match(head,
            $@"<meta\b[^>]*?\b{attribute}\s*=\s*([""'])\s*{escaped}\s*\1[^>]*?\bcontent\s*=\s*([""'])(?<v>.*?)\2",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(head,
                $@"<meta\b[^>]*?\bcontent\s*=\s*([""'])(?<v>.*?)\1[^>]*?\b{attribute}\s*=\s*([""'])\s*{escaped}\s*\2",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var value = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? TitleTag(string head)
    {
        var m = Regex.Match(head, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var value = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Regex.Replace(text, @"\s+", " ").Trim();

    private static string DescribeMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "application/pdf" => "PDF document",
        var t when t.StartsWith("image/") => "Image",
        var t when t.StartsWith("video/") => "Video",
        var t when t.StartsWith("audio/") => "Audio",
        "application/zip" or "application/x-zip-compressed" => "ZIP archive",
        "application/vnd.android.package-archive" => "Android app package",
        "" => "File",
        var t => t,
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    // ------------------------------------------------------------------------------------------------
    // Disk cache: one small JSON file per link, so favorites show their previews without a network round trip.
    // ------------------------------------------------------------------------------------------------

    private static string DiskPath(Uri uri) => Path.Combine(CacheDir, CacheKey(uri) + ".json");

    private static async Task<LinkPreview?> ReadDiskAsync(Uri uri)
    {
        try
        {
            var path = DiskPath(uri);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            var preview = await JsonSerializer.DeserializeAsync<LinkPreview>(stream);
            return preview is not null && DateTime.UtcNow - preview.FetchedAt < DiskCacheLifetime ? preview : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteDiskAsync(Uri uri, LinkPreview preview)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            await using var stream = File.Create(DiskPath(uri));
            await JsonSerializer.SerializeAsync(stream, preview);
        }
        catch
        {
            // The cache is a convenience only.
        }
    }
}
