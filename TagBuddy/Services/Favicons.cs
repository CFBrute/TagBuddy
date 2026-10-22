using System.Collections.Concurrent;

namespace TagBuddy.Services;

/// <summary>A bank of site icons: one small PNG per site, fetched once and reused for every link to that site.
/// Only the site itself is asked (its homepage and icon files, and those of its parent domain), never the link,
/// so this is safe even for one-time links whose page must not be opened by a preview. No third-party icon
/// service is used: a site without an icon of its own gets the app's link glyph.</summary>
public static class Favicons
{
    /// <summary>Second-level labels that are part of a country's suffix (co.uk, com.au, ne.jp): the parent of
    /// shop.example.co.uk is example.co.uk, never co.uk.</summary>
    private static readonly HashSet<string> SuffixLabels =
        new(StringComparer.OrdinalIgnoreCase) { "co", "com", "net", "org", "gov", "edu", "ac", "ne", "or", "go", "gob", "nic" };

    private const int MaxBytes = 256 * 1024;
    private const int MinSize = 16;
    private const int MaxSize = 128;

    /// <summary>An icon this big or bigger ends the search; smaller ones are kept only until a larger one turns
    /// up, because a 16 px favicon.ico looks blurry in a 56 px tile.</summary>
    private const int GoodSize = 64;
    private static readonly TimeSpan NegativeLifetime = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<string, Task<string?>> Memory = new(StringComparer.Ordinal);

    private static string Dir => Path.Combine(LinkPreviews.CacheDir, "icons");

    /// <summary>Path of the site's icon as a PNG of at most 128 px, or null when it has none that can be shown.
    /// <paramref name="linkTags"/> are the page's link tags when the page was read; without them only the usual
    /// file locations are tried.</summary>
    public static Task<string?> GetAsync(Uri pageUri, IReadOnlyList<LinkTag>? linkTags)
    {
        if (pageUri.Scheme is not ("http" or "https")) return Task.FromResult<string?>(null);
        var key = SiteKey(pageUri);
        return Memory.GetOrAdd(key, k => FetchAsync(pageUri, linkTags, k));
    }

    /// <summary>One entry per site: www.example.com and example.com share their icon, and so do http and https.</summary>
    internal static string SiteKey(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return LinkPreviews.CacheKey(host);
    }

    private static async Task<string?> FetchAsync(Uri pageUri, IReadOnlyList<LinkTag>? linkTags, string key)
    {
        try
        {
            // The file name carries the icon's pixel size: the image control caches by path, so a better icon
            // fetched later must get a new name to show up.
            var none = Path.Combine(Dir, key + ".none");
            var existing = Directory.Exists(Dir) ? Directory.GetFiles(Dir, key + ".*.png").FirstOrDefault() : null;
            if (existing is not null && new FileInfo(existing).Length > 0) return existing;
            if (File.Exists(none))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < NegativeLifetime) return null;
                TryDelete(none);
            }

            bool reachedServer = false;
            int bestSide = 0, n = 0;
            string? best = null;
            await foreach (var candidate in CandidatesAsync(pageUri, linkTags))
            {
                var (bytes, answered) = await DownloadAsync(candidate);
                reachedServer |= answered;
                if (bytes is null) continue;

                var temp = Path.Combine(Dir, $"{key}.{++n}.candidate");
                int side = Normalize(bytes, temp);
                if (side > bestSide)
                {
                    if (best is not null) TryDelete(best);
                    bestSide = side;
                    best = temp;
                }
                else TryDelete(temp);

                if (bestSide >= GoodSize) break;
            }

            if (best is not null)
            {
                var png = Path.Combine(Dir, $"{key}.{bestSide}.png");
                File.Move(best, png, overwrite: true);
                return png;
            }

            if (reachedServer)
            {
                // The site answered but has nothing usable: do not ask again for a day.
                Directory.CreateDirectory(Dir);
                await File.WriteAllBytesAsync(none, []);
            }
            else
            {
                Memory.TryRemove(key, out _);   // offline: a later view may try again
            }
            return null;
        }
        catch
        {
            Memory.TryRemove(key, out _);
            return null;
        }
    }

    /// <summary>The site's icon if it is already on the phone, without any request; null otherwise.</summary>
    public static string? GetCached(Uri pageUri)
    {
        try
        {
            if (pageUri.Scheme is not ("http" or "https") || !Directory.Exists(Dir)) return null;
            var existing = Directory.GetFiles(Dir, SiteKey(pageUri) + ".*.png").FirstOrDefault();
            return existing is not null && new FileInfo(existing).Length > 0 ? existing : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops the remembered lookups, for when the cache folder is cleared.</summary>
    internal static void ForgetAll() => Memory.Clear();

    /// <summary>Where to look, best first, asking for more only while nothing good enough was found: the icons the
    /// page declares (touch icon first, usually 180 px and opaque), the two well-known file names, the icons the
    /// site's homepage declares when the page itself was not read, and last the parent domain's well-known
    /// files (m.example.com and shop.example.com usually share example.com's icon). SVG icons are skipped,
    /// because the image control cannot draw a remote SVG.</summary>
    private static async IAsyncEnumerable<Uri> CandidatesAsync(Uri pageUri, IReadOnlyList<LinkTag>? linkTags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var origin = new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/");

        foreach (var u in Declared(pageUri, linkTags))
            if (seen.Add(u.AbsoluteUri)) yield return u;

        foreach (var u in WellKnown(origin))
            if (seen.Add(u.AbsoluteUri)) yield return u;

        if (linkTags is not { Count: > 0 })
        {
            var (homepage, tags) = await HomepageLinkTagsAsync(origin);
            foreach (var u in Declared(homepage, tags))
                if (seen.Add(u.AbsoluteUri)) yield return u;
        }

        if (ParentSite(pageUri) is { } parent)
            foreach (var u in WellKnown(parent))
                if (seen.Add(u.AbsoluteUri)) yield return u;
    }

    private static IEnumerable<Uri> Declared(Uri pageUri, IReadOnlyList<LinkTag>? linkTags)
    {
        if (linkTags is not { Count: > 0 }) yield break;
        var touch = linkTags
            .Where(t => HtmlHead.HasRel(t, "apple-touch-icon") || HtmlHead.HasRel(t, "apple-touch-icon-precomposed"))
            .Where(t => !IsSvg(t))
            .OrderByDescending(t => LargestSize(t.Sizes));
        var icons = linkTags
            .Where(t => HtmlHead.HasRel(t, "icon"))
            .Where(t => !IsSvg(t))
            .OrderByDescending(t => LargestSize(t.Sizes));

        foreach (var tag in touch.Concat(icons))
            if (Uri.TryCreate(pageUri, tag.Href, out var u) && u.Scheme is "http" or "https")
                yield return u;
    }

    private static Uri[] WellKnown(Uri origin) => [new Uri(origin, "apple-touch-icon.png"), new Uri(origin, "favicon.ico")];

    /// <summary>The origin of the domain one level up (shop.example.com → example.com), or null when the host is
    /// already a site of its own (example.com, www.example.com, example.co.uk) or an IP address.</summary>
    internal static Uri? ParentSite(Uri uri)
    {
        if (uri.HostNameType != UriHostNameType.Dns) return null;
        var labels = uri.Host.ToLowerInvariant().Split('.');
        if (labels.Length < 3 || labels[0] == "www") return null;
        var parent = labels[1..];
        // example.co.uk has three labels but is a site of its own: its parent would be the suffix co.uk.
        if (parent.Length == 2 && parent[1].Length == 2 && SuffixLabels.Contains(parent[0])) return null;
        return new Uri($"{uri.Scheme}://{string.Join('.', parent)}/");
    }

    /// <summary>The link tags of the site's homepage and the address they are relative to (after redirects), or
    /// none. Only the origin is requested, so this is safe for single-use links too.</summary>
    private static async Task<(Uri BaseUri, IReadOnlyList<LinkTag>? Tags)> HomepageLinkTagsAsync(Uri origin)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, origin);
            return await LinkPreviews.SendAsync(request, async (response, token) =>
            {
                var finalUri = response.RequestMessage?.RequestUri ?? origin;
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!response.IsSuccessStatusCode || !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return (finalUri, (IReadOnlyList<LinkTag>?)null);
                var html = await LinkPreviews.ReadTextAsync(response, HomepageBytes, token);
                return (finalUri, HtmlHead.LinkTags(HtmlHead.Head(html)));
            });
        }
        catch
        {
            return (origin, null);
        }
    }

    /// <summary>Icon links sit in the head, near the top of the page; this much is plenty.</summary>
    private const int HomepageBytes = 128 * 1024;

    private static bool IsSvg(LinkTag tag) =>
        (tag.Type?.Contains("svg", StringComparison.OrdinalIgnoreCase) ?? false)
        || tag.Href.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>The largest side in a sizes attribute such as "16x16 32x32"; 0 for none or "any".</summary>
    internal static int LargestSize(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes)) return 0;
        int best = 0;
        foreach (var part in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = part.Split('x', 'X');
            if (xy.Length == 2 && int.TryParse(xy[0], out var w)) best = Math.Max(best, w);
        }
        return best;
    }

    /// <summary>The icon's bytes, or null. Answered tells whether the server replied at all, to tell "this site
    /// has no icon" apart from "offline".</summary>
    private static async Task<(byte[]? Bytes, bool Answered)> DownloadAsync(Uri uri)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "image/png,image/x-icon,image/*;q=0.9,*/*;q=0.5");
            var bytes = await LinkPreviews.SendAsync(request, async (response, token) =>
            {
                if (!response.IsSuccessStatusCode) return null;
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                // An HTML answer is a soft 404 page, not an icon.
                if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return null;
                if (response.Content.Headers.ContentLength is > MaxBytes) return null;
                return await LinkPreviews.ReadCappedAsync(response, MaxBytes, token);
            });
            return (bytes, true);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>The longer side of a stored icon in pixels, read from the PNG header, or 0.</summary>
    public static int SideOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, 24) < 24) return 0;
            int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return Math.Max(w, h);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>How large to draw a stored icon in a slot of <paramref name="slot"/> dp: full size when the icon
    /// is sharp enough, otherwise at most twice its pixel size, so a 16 px icon stays a small crisp mark instead
    /// of a blur.</summary>
    public static double DisplaySize(string path, double slot)
    {
        int side = SideOf(path);
        return side <= 0 ? slot : Math.Min(slot, Math.Max(16, side * 2));
    }

    /// <summary>Decodes the icon with Android's own codecs (which read ICO, PNG, JPEG, WebP and GIF), rejects
    /// tiny ones, and stores it as a PNG of at most 128 px, so every icon shows the same way. Returns the
    /// icon's longer side in pixels, or 0 when it was unusable.</summary>
    private static int Normalize(byte[] bytes, string path)
    {
#if ANDROID
        try
        {
            using var bitmap = Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            if (bitmap is null || bitmap.Width < MinSize || bitmap.Height < MinSize) return 0;
            int originalSide = Math.Max(bitmap.Width, bitmap.Height);

            var output = bitmap;
            int side = Math.Max(bitmap.Width, bitmap.Height);
            if (side > MaxSize)
            {
                double scale = (double)MaxSize / side;
                output = Android.Graphics.Bitmap.CreateScaledBitmap(bitmap,
                    Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), true)!;
            }
            try
            {
                Directory.CreateDirectory(Dir);
                var temp = path + ".tmp";
                using (var stream = File.Create(temp))
                {
                    if (!output.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, stream)) return 0;
                }
                File.Move(temp, path, overwrite: true);
                return Math.Min(originalSide, MaxSize);
            }
            finally
            {
                if (!ReferenceEquals(output, bitmap)) output.Dispose();
            }
        }
        catch
        {
            return 0;
        }
#else
        return 0;
#endif
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
