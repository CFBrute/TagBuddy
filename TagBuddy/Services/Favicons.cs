using System.Collections.Concurrent;

namespace TagBuddy.Services;

/// <summary>A bank of site icons: one small PNG per site, fetched once and reused for every link to that site.
/// Only the site's origin is ever requested (its icon files), never the link itself, so this is safe even for
/// one-time links whose page must not be opened by a preview.</summary>
public static class Favicons
{
    /// <summary>Whether Google's favicon service is asked as a last resort when the site itself offers no usable
    /// icon. It sends the site's host name to Google; the person agreed to this. Set to false to keep icon
    /// requests between the phone and the site only.</summary>
    public const bool UseGoogleFallback = true;

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
            foreach (var candidate in Candidates(pageUri, linkTags))
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

    /// <summary>Where to look, best first: the touch icon (usually 180 px and opaque), the largest declared
    /// icon, the two well-known file names, and finally Google's service. SVG icons are skipped, because the
    /// image control cannot draw a remote SVG.</summary>
    internal static IEnumerable<Uri> Candidates(Uri pageUri, IReadOnlyList<LinkTag>? linkTags)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var origin = new Uri(pageUri.GetLeftPart(UriPartial.Authority) + "/");

        if (linkTags is { Count: > 0 })
        {
            var touch = linkTags
                .Where(t => HtmlHead.HasRel(t, "apple-touch-icon") || HtmlHead.HasRel(t, "apple-touch-icon-precomposed"))
                .Where(t => !IsSvg(t))
                .OrderByDescending(t => LargestSize(t.Sizes));
            var icons = linkTags
                .Where(t => HtmlHead.HasRel(t, "icon"))
                .Where(t => !IsSvg(t))
                .OrderByDescending(t => LargestSize(t.Sizes));

            foreach (var tag in touch.Concat(icons))
            {
                if (!Uri.TryCreate(pageUri, tag.Href, out var u) || u.Scheme is not ("http" or "https")) continue;
                if (seen.Add(u.AbsoluteUri)) yield return u;
            }
        }

        foreach (var u in new[] { new Uri(origin, "apple-touch-icon.png"), new Uri(origin, "favicon.ico") })
            if (seen.Add(u.AbsoluteUri)) yield return u;

        if (UseGoogleFallback)
            yield return new Uri($"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(pageUri.Host)}&sz=128");
    }

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
