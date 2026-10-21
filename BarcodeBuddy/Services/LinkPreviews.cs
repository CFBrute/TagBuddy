using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BarcodeBuddy.Services;

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
        if (cached is not null) return cached;

        LinkPreview? preview;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
            {
                // Shown now, but not kept on disk: a 503 today says nothing about next week.
                Memory.TryRemove(uri.AbsoluteUri, out _);
                return new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, null,
                    $"The page answered {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), null, DateTime.UtcNow);
            }
            else if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var size = response.Content.Headers.ContentLength;
                preview = new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, Path.GetFileName(finalUri.AbsolutePath),
                    $"{DescribeMediaType(mediaType)}{(size is > 0 ? $", {FormatSize(size.Value)}" : "")}",
                    mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? finalUri.AbsoluteUri : null,
                    DateTime.UtcNow);
            }
            else
            {
                var html = await ReadHtmlAsync(response);
                preview = Parse(html, finalUri);
            }
        }
        catch
        {
            // Offline, blocked, timed out, or not a real site: there is simply no preview.
            Memory.TryRemove(uri.AbsoluteUri, out _);   // let a later attempt try again
            return null;
        }

        await WriteDiskAsync(uri, preview);
        return preview;
    }

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

    private static string DiskPath(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..32];
        return Path.Combine(CacheDir, hash + ".json");
    }

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
