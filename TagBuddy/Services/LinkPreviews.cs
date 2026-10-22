using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>What a web page says about itself: title, description and picture (oEmbed, Open Graph, Twitter
/// cards, or the plain title tag), plus the site's icon. Only the fields the page provides are set.
/// New fields go at the end with a default, so preview files written by an older version still read.</summary>
public sealed record LinkPreview(
    string Url,
    string Host,
    string? Title,
    string? Description,
    string? ImageUrl,
    DateTime FetchedAt,
    string? IconUrl = null,
    string? SiteName = null,
    string? Text = null,
    DateTime? RetryAfter = null)
{
    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconUrl);
    public string TitleOrHost => string.IsNullOrWhiteSpace(Title) ? Host : Title;
}

/// <summary>Fetches link previews with an in-memory and on-disk cache, so the Saved list and repeated scans of
/// the same code do not hit the network again. Sources, best first: a known site's oEmbed endpoint, the page's
/// own head tags (and the oEmbed link it advertises), and as a last resort a rendered picture of the page.
/// Links that look single-use are never requested.</summary>
public static class LinkPreviews
{
    private const int MaxHtmlBytes = 512 * 1024;
    private const int MaxTextBytes = 2 * 1024;
    private static readonly TimeSpan DiskCacheLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a preview without a picture is kept before it is tried again: the page may have been
    /// slow, blank or not yet rendered, which says little about the next attempt.</summary>
    private static readonly TimeSpan RetryLifetime = TimeSpan.FromHours(6);

    /// <summary>Sites whose rendered page is a login wall or an app shell, not a preview of anything.</summary>
    private static readonly string[] SnapshotExcludedHosts =
        ["instagram.com", "facebook.com", "fb.com", "fb.watch", "threads.net", "threads.com", "x.com", "twitter.com"];

    /// <summary>Every request says what it is: this app fetching a preview. No browser or crawler is imitated,
    /// so a site that only shows its preview to those gets an honest request and the app shows less.</summary>
    private static string UserAgent => $"TagBuddy/{BuildInfo.Version} (Android; link preview)";

    internal static readonly HttpClient Http = CreateClient();

    private static readonly WeakEventManager Events = new();

    /// <summary>Raised on the main thread when a link's stored preview or its site's icon changed outside a normal
    /// load (learned from a page the person opened), so cards on screen can show it. Weak: a card that is gone
    /// does not stay alive for it.</summary>
    public static event EventHandler<Uri> PreviewUpdated
    {
        add => Events.AddEventHandler(value);
        remove => Events.RemoveEventHandler(value);
    }

    private static void RaiseUpdated(Uri uri) =>
        MainThread.BeginInvokeOnMainThread(() => Events.HandleEvent(null!, uri, nameof(PreviewUpdated)));

    /// <summary>At most four requests at a time across previews, icons and oEmbed, so a Saved list with fifty
    /// links does not open fifty sockets at once.</summary>
    private static readonly SemaphoreSlim NetGate = new(4, 4);

    private static readonly ConcurrentDictionary<string, Task<LinkPreview?>> Memory = new(StringComparer.Ordinal);
    internal static string CacheDir => Path.Combine(FileSystem.CacheDirectory, "link-previews-v2");

    static LinkPreviews()
    {
        // Version 1 previews have no icons and may hold blank snapshots; drop them once so everything is
        // fetched afresh. In the background: it may be many small files and the first caller is the UI.
        _ = Task.Run(() =>
        {
            try
            {
                var old = Path.Combine(FileSystem.CacheDirectory, "link-previews");
                if (Directory.Exists(old)) Directory.Delete(old, true);
            }
            catch { /* best effort */ }
        });
    }

    /// <summary>True for values that are web links, the only kind a page preview makes sense for.</summary>
    public static bool IsPreviewable(string? value, out Uri uri) => Links.IsWebLink(value, out uri);

    /// <summary>A sign-in, one-time or side-effect link, never requested for a preview (<see cref="Links.IsSingleUse"/>).</summary>
    public static bool IsSingleUse(Uri uri) => Links.IsSingleUse(uri);

    /// <summary>A preview already on the phone, or null; never touches the network. Used while automatic previews
    /// are switched off.</summary>
    public static async Task<LinkPreview?> GetCachedAsync(Uri uri)
    {
        try
        {
            if (Memory.TryGetValue(uri.AbsoluteUri, out var task) && task.IsCompletedSuccessfully && task.Result is { } known)
                return known;
            var cached = await ReadDiskAsync(uri);
            return cached is not null && FilesPresent(cached) ? cached : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forgets every preview and icon, in memory and on disk. Settings offers it.</summary>
    public static void ClearCache()
    {
        Memory.Clear();
        Favicons.ForgetAll();
        try { if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true); } catch { /* best effort */ }
    }

    /// <summary>The preview for a web link, or null when the page could not be reached or read, or when the link
    /// is single-use (never requested).</summary>
    public static Task<LinkPreview?> GetAsync(Uri uri)
    {
        if (IsSingleUse(uri)) return Task.FromResult<LinkPreview?>(null);
        return Memory.GetOrAdd(uri.AbsoluteUri, _ => FetchAsync(uri));
    }

    /// <summary>Starts loading the preview and icon of a value that was just stored, so Saved shows them without
    /// waiting. For a single-use link only the site's icon is fetched (origin only).</summary>
    public static void Prefetch(string? value)
    {
        try
        {
            if (!AppSettings.AutoPreviews || !IsPreviewable(value, out var uri)) return;
            Forget(IsSingleUse(uri) ? Favicons.GetAsync(uri, null) : GetAsync(uri));
        }
        catch
        {
            // Prefetching is a convenience only.
        }
    }

    /// <summary>The site icon for a link: the one found while reading its page when there is a preview,
    /// otherwise the site's usual icon files. With automatic previews off, only an icon already on the phone.</summary>
    public static async Task<string?> GetIconAsync(Uri uri)
    {
        try
        {
            if (!AppSettings.AutoPreviews)
            {
                var cached = IsSingleUse(uri) ? null : await GetCachedAsync(uri);
                return cached is { HasIcon: true } && File.Exists(cached.IconUrl) ? cached.IconUrl : Favicons.GetCached(uri);
            }
            if (!IsSingleUse(uri))
            {
                var preview = await GetAsync(uri);
                if (preview is { HasIcon: true } && File.Exists(preview.IconUrl)) return preview.IconUrl;
            }
            return await Favicons.GetAsync(uri, null);
        }
        catch
        {
            return null;
        }
    }

    private static void Forget(Task task) =>
        task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

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
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en,*;q=0.5");
        return client;
    }

    /// <summary>Sends a request through the shared client under the request gate and lets <paramref name="read"/>
    /// consume the answer before the slot is given back. The body read has its own time limit, because the
    /// client's timeout stops at the headers.</summary>
    internal static async Task<T> SendAsync<T>(HttpRequestMessage request, Func<HttpResponseMessage, CancellationToken, Task<T>> read)
    {
        await NetGate.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using (request)
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                return await read(response, cts.Token);
        }
        finally
        {
            NetGate.Release();
        }
    }

    /// <summary>Reads at most <paramref name="max"/> bytes of the body; null when it is larger.</summary>
    internal static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, int max, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) return null;
        }
        return buffer.ToArray();
    }

    // ------------------------------------------------------------------------------------------------
    // Fetching
    // ------------------------------------------------------------------------------------------------

    private static async Task<LinkPreview?> FetchAsync(Uri uri)
    {
        var cached = await ReadDiskAsync(uri);
        if (cached is not null && FilesPresent(cached))
        {
            // Stale while revalidate: an old preview shows at once (Saved never flashes "Loading"), and a fresh
            // one replaces it in memory for the next time the row is shown.
            if (!IsFresh(cached)) Forget(RefreshAsync(uri));
            return cached;
        }

        var (preview, persisted) = await FetchNetworkAsync(uri);
        if (!persisted) Memory.TryRemove(uri.AbsoluteUri, out _);   // a later view may try again
        return preview;
    }

    private static async Task RefreshAsync(Uri uri)
    {
        try
        {
            var (fresh, persisted) = await FetchNetworkAsync(uri);
            // Only a real answer replaces the old preview; an offline attempt keeps what was there.
            if (fresh is not null && persisted) Memory[uri.AbsoluteUri] = Task.FromResult<LinkPreview?>(fresh);
        }
        catch
        {
            // Keep the stale preview.
        }
    }

    private static bool IsFresh(LinkPreview preview) =>
        DateTime.UtcNow - preview.FetchedAt < DiskCacheLifetime
        && (preview.RetryAfter is null || preview.RetryAfter > DateTime.UtcNow);

    private static bool FilesPresent(LinkPreview preview) =>
        (!preview.HasImage || !IsLocalPath(preview.ImageUrl!) || File.Exists(preview.ImageUrl!))
        && (!preview.HasIcon || File.Exists(preview.IconUrl!));

    /// <summary>The preview from the network, and whether it was written to disk (false after a failure, so the
    /// next view tries again).</summary>
    private static async Task<(LinkPreview? Preview, bool Persisted)> FetchNetworkAsync(Uri uri)
    {
        // 1. Big sites answer a plain request with a consent page or an app shell; their oEmbed endpoint hands out
        //    the title, author and thumbnail without any of that. A rendered picture is never used for them.
        var provided = await OEmbed.TryProviderAsync(uri);
        if (provided is not null)
        {
            var icon = await Favicons.GetAsync(uri, null);
            provided = provided with { IconUrl = icon };
            if (!provided.HasImage) provided = provided with { RetryAfter = DateTime.UtcNow + RetryLifetime };
            await WriteDiskAsync(uri, provided);
            return (provided, true);
        }

        // 2. The page itself.
        LinkPreview preview;
        IReadOnlyList<LinkTag>? linkTags = null;
        string? oembedHref = null;
        Uri finalUri = uri;
        bool persist = true;
        try
        {
            var page = await GetPageAsync(uri);

            finalUri = page.FinalUri;
            preview = page.Preview;
            linkTags = page.LinkTags;
            oembedHref = page.OEmbedHref;
            if (page.Kind == PageKind.Failed) persist = false;

            if (page.Kind is PageKind.File or PageKind.Text)
            {
                // Not a page but a file or plain text: say so, and never try to render it.
                preview = preview with { IconUrl = await Favicons.GetAsync(finalUri, null) };
                await WriteDiskAsync(uri, preview);
                return (preview, true);
            }
        }
        catch
        {
            // Offline, blocked, timed out, or not a real site. A browser engine may still manage; if not, there is
            // simply no preview, and a later attempt may try again.
            persist = false;
            preview = new LinkPreview(uri.AbsoluteUri, uri.Host, null, null, null, DateTime.UtcNow);
        }

        // 3. No picture, but the page advertises its own oEmbed: its thumbnail is the next best thing.
        if (!preview.HasImage && oembedHref is not null)
        {
            var discovered = await OEmbed.TryDiscoveredAsync(finalUri, oembedHref);
            if (discovered is not null)
                preview = preview with
                {
                    ImageUrl = discovered.ImageUrl,
                    Title = preview.Title ?? discovered.Title,
                    Description = preview.Description ?? discovered.Description,
                    SiteName = preview.SiteName ?? discovered.SiteName,
                };
        }

        // A bot check answered the plain request: its title says so, and it is not the page's.
        if (PageInfo.IsInterstitialTitle(preview.Title)) preview = preview with { Title = null, Description = null };

        // 4. The icon, and when there is still no picture a rendered snapshot, at the same time.
        var iconTask = Favicons.GetAsync(finalUri, linkTags);
        bool trySnapshot = !preview.HasImage && !IsSnapshotExcluded(finalUri) && !IsSnapshotExcluded(uri);
        var snapshotTask = trySnapshot ? CaptureSnapshotAsync(uri) : Task.FromResult(SnapshotResult.Nothing);
        await Task.WhenAll(iconTask, snapshotTask);
        var iconPath = await iconTask;
        var snapshot = await snapshotTask;

        // 5. What the rendered page says about itself fills in what the plain request could not see: pages built
        //    by script, and pages that refused the plain request but render fine. Bot checks are left out.
        if (snapshot.Page is { IsInterstitial: false } rendered)
        {
            preview = preview with
            {
                Title = preview.Title ?? rendered.Title,
                Description = preview.Description ?? rendered.Description,
                SiteName = preview.SiteName ?? rendered.SiteName,
            };
            if (!preview.HasImage && rendered.ImageUrl is not null)
            {
                // The page's own preview picture beats a picture of the page.
                preview = preview with { ImageUrl = rendered.ImageUrl };
                if (snapshot.Path is not null) TryDelete(snapshot.Path);
                snapshot = snapshot with { Path = null };
            }
            if (iconPath is null || Favicons.SideOf(iconPath) < 64)
                iconPath = await Favicons.LearnAsync(rendered.Url, rendered.Icons) ?? iconPath;
            if (rendered.Title is not null) persist = true;   // a browser got through where the plain request did not
        }
        else if (snapshot.Page is { IsInterstitial: true } && snapshot.Path is not null)
        {
            TryDelete(snapshot.Path);
            snapshot = snapshot with { Path = null };
        }

        // The icon belongs to the address the link was known by too (a redirect to another domain).
        if (iconPath is not null)
        {
            Favicons.Remember(uri, iconPath);
            Favicons.Remember(finalUri, iconPath);
        }
        preview = preview with { IconUrl = iconPath };

        if (snapshot.Path is not null)
        {
            // A sparse picture (a logo on an empty page) only beats having nothing: with an icon, the card shows
            // the icon instead.
            if (!snapshot.Sparse || iconPath is null)
                preview = preview with
                {
                    ImageUrl = snapshot.Path,
                    Description = preview.Description ?? (preview.Title is null ? "Tap to open the page." : null),
                };
            else
                TryDelete(snapshot.Path);
        }
        if (snapshot.Skipped) return (Shows(preview) ? preview : null, false);   // in the background: try again when shown

        // Something was learned (an icon, a title) even if the page itself failed: keep it, so the card shows it
        // next time without asking again, and try for the full preview after a few hours. Offline, nothing was
        // learned and nothing is kept.
        bool learned = preview.HasIcon || preview.HasImage || preview.Title is not null;
        if (!persist && !learned)
            return (preview.Description is not null ? preview : null, false);

        // 6. A full preview lasts a week; one without a real picture (blank, slow, sparse or failed page) is tried
        //    again after a few hours.
        bool complete = preview.HasImage && !(snapshot.Path is not null && snapshot.Sparse);
        if (!complete) preview = preview with { RetryAfter = DateTime.UtcNow + RetryLifetime };
        await WriteDiskAsync(uri, preview);
        return (preview, true);
    }

    private static bool Shows(LinkPreview preview) =>
        preview.HasImage || preview.Description is not null || preview.HasIcon;

    private static Task<SnapshotResult> CaptureSnapshotAsync(Uri uri) =>
        PageSnapshot.CaptureAsync(uri, CacheKey(uri));

    /// <summary>The file name for a picture taken in the in-app viewer: new each time, because the image control
    /// caches pictures by path.</summary>
    public static string OpenedSnapshotKey(Uri link) => $"{CacheKey(link)}-opened-{DateTime.UtcNow.Ticks}";

    /// <summary>The person opened a link in the in-app viewer: keep what the page showed them as the link's
    /// preview, when nothing better is stored. Its title, description and preview picture, else the picture
    /// <paramref name="capture"/> took of the top of the page; and its icon, under the site's address and the
    /// link's. A sign-in or one-time link only gives its icon: its page may show personal things. Bot checks
    /// are never kept. Works with automatic previews off too: the person asked for this page.</summary>
    public static async Task LearnFromOpenedPageAsync(Uri link, SnapshotResult capture)
    {
        var page = capture.Page;
        try
        {
            if (page is null || page.IsInterstitial)
            {
                if (capture.Path is not null) TryDelete(capture.Path);
                return;
            }

            var icon = await Favicons.LearnAsync(page.Url, page.Icons);
            if (icon is not null) Favicons.Remember(link, icon);

            if (IsSingleUse(link))
            {
                if (capture.Path is not null) TryDelete(capture.Path);
                RaiseUpdated(link);
                return;
            }

            var existing = await ReadDiskAsync(link);
            bool keepExisting = existing is { HasImage: true, RetryAfter: null } && FilesPresent(existing) && IsFresh(existing);
            if (keepExisting)
            {
                if (capture.Path is not null) TryDelete(capture.Path);
                if (icon is not null && !existing!.HasIcon)
                {
                    var withIcon = existing with { IconUrl = icon };
                    await WriteDiskAsync(link, withIcon);
                    Memory[link.AbsoluteUri] = Task.FromResult<LinkPreview?>(withIcon);
                }
                RaiseUpdated(link);
                return;
            }

            string? image = page.ImageUrl ?? capture.Path;
            if (page.ImageUrl is not null && capture.Path is not null) TryDelete(capture.Path);
            // A picture taken earlier is replaced, not left behind.
            if (existing?.ImageUrl is { } old && IsLocalPath(old) && old != image && image is not null) TryDelete(old);
            bool complete = page.ImageUrl is not null || (capture.Path is not null && !capture.Sparse);

            var preview = new LinkPreview(link.AbsoluteUri, page.Url.Host,
                page.Title ?? existing?.Title, page.Description ?? existing?.Description,
                image ?? existing?.ImageUrl, DateTime.UtcNow,
                icon ?? existing?.IconUrl, page.SiteName ?? existing?.SiteName, existing?.Text,
                complete ? null : DateTime.UtcNow + RetryLifetime);
            await WriteDiskAsync(link, preview);
            Memory[link.AbsoluteUri] = Task.FromResult<LinkPreview?>(preview);
            RaiseUpdated(link);
        }
        catch
        {
            // Learning is a bonus; the page itself is open either way.
        }
    }

    private static bool IsSnapshotExcluded(Uri uri) => OEmbed.HostIs(uri.Host, SnapshotExcludedHosts);

    private enum PageKind { Html, Text, File, Failed }

    private sealed record PageResult(PageKind Kind, Uri FinalUri, LinkPreview Preview, IReadOnlyList<LinkTag>? LinkTags, string? OEmbedHref);

    private static Task<PageResult> GetPageAsync(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        return SendAsync(request, async (response, token) =>
        {
            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
            {
                // A plain request was refused, but the page may still render in a browser engine (bot checks,
                // login walls, odd servers). Not kept on disk: a 503 today says nothing about next week.
                var status = $"The page answered {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                return new PageResult(PageKind.Failed, finalUri,
                    new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, null, status, null, DateTime.UtcNow), null, null);
            }

            if (mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                // Plain text: its first lines are the best description there is.
                var text = await ReadTextAsync(response, MaxTextBytes, token);
                var snippet = HtmlHead.Clean(text.Length > 300 ? text[..300] : text);
                var name = Path.GetFileName(finalUri.AbsolutePath);
                return new PageResult(PageKind.Text, finalUri,
                    new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, string.IsNullOrEmpty(name) ? null : name,
                        snippet ?? "Plain text", null, DateTime.UtcNow, Text: snippet), null, null);
            }

            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var size = response.Content.Headers.ContentLength;
                return new PageResult(PageKind.File, finalUri,
                    new LinkPreview(finalUri.AbsoluteUri, finalUri.Host, Path.GetFileName(finalUri.AbsolutePath),
                        $"Download: {DescribeMediaType(mediaType)}{(size is > 0 ? $", {FormatSize(size.Value)}" : "")}",
                        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? finalUri.AbsoluteUri : null,
                        DateTime.UtcNow), null, null);
            }

            var html = await ReadTextAsync(response, MaxHtmlBytes, token);
            var (preview, tags, oembed) = HtmlHead.ReadPreview(html, finalUri);
            return new PageResult(PageKind.Html, finalUri, preview, tags, oembed);
        });
    }

    /// <summary>True when the picture is a file this app rendered rather than a web address.</summary>
    public static bool IsLocalPath(string imageUrl) => imageUrl.StartsWith('/') || imageUrl.Contains(":\\");

    internal static string CacheKey(Uri uri) => CacheKey(uri.AbsoluteUri);

    internal static string CacheKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    internal static async Task<string> ReadTextAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < maxBytes && (read = await stream.ReadAsync(chunk, token)) > 0)
            buffer.Write(chunk, 0, read);

        var bytes = buffer.ToArray();
        if (bytes.Length > maxBytes) Array.Resize(ref bytes, maxBytes);
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------------------------------------
    // Disk cache: one small JSON file per link, so Saved shows its previews without a network round trip.
    // ------------------------------------------------------------------------------------------------

    private static string DiskPath(Uri uri) => Path.Combine(CacheDir, CacheKey(uri) + ".json");

    /// <summary>The stored preview whatever its age; <see cref="IsFresh"/> decides whether it is refreshed.</summary>
    private static async Task<LinkPreview?> ReadDiskAsync(Uri uri)
    {
        try
        {
            var path = DiskPath(uri);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LinkPreview>(stream);
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
