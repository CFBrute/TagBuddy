using System.Text.Json;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>How much a rendered page shows: next to nothing, a little (a logo on an empty page), or real content.</summary>
public enum SnapshotContent { Blank, Sparse, Rich }

/// <summary>What a page says about itself once its scripts have run: where it ended up, its title, description,
/// preview picture and declared icons. Pages built by script (most "apps" on the web) only have these after
/// rendering, so a plain request misses them.</summary>
public sealed record PageInfo(Uri Url, string? Title, string? Description, string? ImageUrl, string? SiteName, IReadOnlyList<LinkTag> Icons)
{
    /// <summary>Titles of bot checks and block pages. Such a page is not the site's content: it is never kept as a
    /// preview, and the app does not try to get past it.</summary>
    private static readonly string[] InterstitialTitles =
        ["just a moment", "attention required", "access denied", "verifying you are human", "verify you are human",
         "one more step", "are you a robot", "security check", "please wait", "ddos-guard", "403 forbidden", "captcha"];

    public bool IsInterstitial => IsInterstitialTitle(Title);

    public static bool IsInterstitialTitle(string? title) =>
        title is { } t && InterstitialTitles.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What rendering a page produced: the saved picture (null when none), whether it is sparse, whether
/// the attempt was skipped because the app had no window, and what the rendered page said about itself.</summary>
public sealed record SnapshotResult(string? Path, bool Sparse, bool Skipped, PageInfo? Page = null)
{
    public static readonly SnapshotResult Nothing = new(null, false, false);
}

/// <summary>Renders a web page in a web view and saves a picture of it, for links whose page offers no preview
/// image of its own. It is the heavy last resort: one page at a time, with a hard time limit, and a picture
/// with (almost) nothing on it counts as no picture.
///
/// Why the web view is attached to the window: a web view that is not attached counts as hidden, so the engine
/// throttles or skips animation frames and lazy images, and pages that draw on an animation frame come out
/// white. It is added behind the app's own content, at a phone-like width in CSS pixels, so it is "visible" to
/// the page but never to the person.
///
/// To make the picture look like the page rather than its pop-ups, lazy images are loaded at once, web fonts
/// are waited for, and cookie banners and other overlays are hidden in this private copy before it is drawn.
/// Hiding is cosmetic: nothing is clicked, so no consent is given on the person's behalf. Bot checks are
/// recognised and never kept.</summary>
public static class PageSnapshot
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string Dir => Path.Combine(FileSystem.CacheDirectory, "link-snapshots");

    private const int CssWidth = 412;    // a common phone width in CSS pixels
    private const int CssHeight = 309;   // 4:3
    private const int SavedWidth = 720;
    private const int MinSettleMs = 800;
    private const int MaxSettleMs = 7000;

    /// <summary>The page's state as seen by script: ready state, text length, image counts, colours and whether
    /// its web fonts have loaded (text drawn before that comes out in a fallback font, or invisible).</summary>
    private const string ProbeScript =
        "(function(){var b=document.body;var cs=b?getComputedStyle(b):null;" +
        "var hs=document.documentElement?getComputedStyle(document.documentElement):null;" +
        "var imgs=[].slice.call(document.images||[]);" +
        "return JSON.stringify([document.readyState,b?(b.innerText||'').length:0,imgs.length," +
        "imgs.filter(function(i){return i.complete;}).length,cs?cs.backgroundColor:'',cs?cs.color:'',hs?hs.backgroundColor:''," +
        "document.fonts?document.fonts.status:'loaded']);})()";

    /// <summary>Loads lazy images now (the picture is taken without scrolling) and goes to the top.</summary>
    private const string PrepareScript =
        "(function(){[].forEach.call(document.querySelectorAll('img[loading=\"lazy\"]'),function(i){i.loading='eager';});" +
        "window.scrollTo(0,0);return 1;})()";

    /// <summary>Hides overlays in the rendered copy: fixed or sticky elements named like a cookie banner, consent
    /// dialog, modal or newsletter pop-up, and large fixed layers on top of the page with little text of their
    /// own. They are remembered so <see cref="RestoreScript"/> can bring them back if hiding left the page empty.
    /// Nothing is clicked or answered.</summary>
    private const string HideOverlaysScript =
        "(function(){var vw=innerWidth,vh=innerHeight,hidden=[];" +
        "var all=document.querySelectorAll('body *');" +
        "for(var i=0;i<all.length&&i<5000;i++){var e=all[i];var s=getComputedStyle(e);" +
        "if(s.position!=='fixed'&&s.position!=='sticky')continue;" +
        "var r=e.getBoundingClientRect();" +
        "var area=Math.max(0,Math.min(r.right,vw)-Math.max(r.left,0))*Math.max(0,Math.min(r.bottom,vh)-Math.max(r.top,0));" +
        "var id=((e.id||'')+' '+(typeof e.className==='string'?e.className:'')+' '+(e.getAttribute('aria-label')||'')).toLowerCase();" +
        "var named=/cookie|consent|gdpr|privacy|cmp|banner|modal|overlay|popup|pop-up|dialog|newsletter|subscribe|paywall|interstitial/.test(id)" +
        "||e.getAttribute('role')==='dialog'||e.getAttribute('role')==='alertdialog'||e.getAttribute('aria-modal')==='true';" +
        "var z=parseInt(s.zIndex,10)||0;var text=(e.innerText||'').length;" +
        "if((named&&area>vw*vh*0.04)||(area>vw*vh*0.4&&z>=100&&text<1500)){hidden.push([e,e.style.getPropertyValue('display'),e.style.getPropertyPriority('display')]);" +
        "e.style.setProperty('display','none','important');}}" +
        "if(hidden.length){[document.documentElement,document.body].forEach(function(x){if(x){" +
        "hidden.push([x,null,null,x.style.getPropertyValue('overflow'),x.style.getPropertyPriority('overflow')]);" +
        "x.style.setProperty('overflow','visible','important');}});}" +
        "window.__tagBuddyHidden=hidden;return hidden.length;})()";

    private const string RestoreScript =
        "(function(){var h=window.__tagBuddyHidden||[];h.forEach(function(x){" +
        "if(x[1]===null){x[0].style.setProperty('overflow',x[3]||'',x[4]||'');}else{x[0].style.setProperty('display',x[1]||'',x[2]||'');}});" +
        "window.__tagBuddyHidden=[];return h.length;})()";

    /// <summary>What the rendered page says about itself; see <see cref="PageInfo"/>.</summary>
    private const string InfoScript =
        "(function(){function m(s){var e=document.querySelector(s);return e?(e.getAttribute('content')||''):'';}" +
        "function abs(u){try{return u?new URL(u,location.href).href:'';}catch(x){return '';}}" +
        "var icons=[].slice.call(document.querySelectorAll('link[rel~=\"icon\"],link[rel~=\"apple-touch-icon\"],link[rel~=\"apple-touch-icon-precomposed\"]'))" +
        ".slice(0,16).map(function(l){return [l.getAttribute('rel')||'',l.href||'',l.getAttribute('type')||'',l.getAttribute('sizes')||''];});" +
        "return JSON.stringify([location.href,m('meta[property=\"og:title\"]')||document.title||''," +
        "m('meta[property=\"og:description\"]')||m('meta[name=\"description\"]')||m('meta[name=\"twitter:description\"]')," +
        "abs(m('meta[property=\"og:image\"]')||m('meta[property=\"og:image:url\"]')||m('meta[name=\"twitter:image\"]'))," +
        "m('meta[property=\"og:site_name\"]'),icons]);})()";

    /// <summary>Renders the page out of sight and saves a picture of its top. Also reports what the rendered
    /// page says about itself, even when no picture was kept.</summary>
    public static async Task<SnapshotResult> CaptureAsync(Uri uri, string key, int timeoutMs = 15000)
    {
#if ANDROID
        await Gate.WaitAsync();
        Android.Webkit.WebView? view = null;
        Android.Views.ViewGroup? host = null;
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, key + ".png");
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = new LoadState();
            int width = 0, height = 0;

            bool attached = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var activity = Platform.CurrentActivity;
                if (activity?.Window?.DecorView is not Android.Views.ViewGroup decor) return false;

                float density = activity.Resources?.DisplayMetrics?.Density ?? 1f;
                width = (int)Math.Round(CssWidth * density);
                height = (int)Math.Round(CssHeight * density);

                view = new InertWebView(activity);
                var settings = view.Settings;
                settings.JavaScriptEnabled = true;
                settings.DomStorageEnabled = true;
                settings.LoadWithOverviewMode = true;
                settings.UseWideViewPort = true;
                settings.BlockNetworkImage = false;
                settings.MediaPlaybackRequiresUserGesture = true;   // no sound from a page nobody sees

                // Light rendering: the engine's own darkening would turn a light page into a dim picture. Pages
                // that are dark by design are handled by the background fill below.
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    settings.AlgorithmicDarkeningAllowed = false;
                else if (OperatingSystem.IsAndroidVersionAtLeast(29))
#pragma warning disable CA1422 // deprecated from API 33, which takes the branch above
                    settings.ForceDark = Android.Webkit.ForceDarkMode.Off;
#pragma warning restore CA1422

                view.Focusable = false;
                view.FocusableInTouchMode = false;
                view.DescendantFocusability = Android.Views.DescendantFocusability.BlockDescendants;
                view.Clickable = false;
                view.ImportantForAccessibility = Android.Views.ImportantForAccessibility.NoHideDescendants;
                view.Alpha = 1f;
                view.Visibility = Android.Views.ViewStates.Visible;
                view.SetWebViewClient(new Client(finished, state));
                view.SetWebChromeClient(new Chrome(state));

                // Index 0: behind the app's content, which is opaque and covers it.
                decor.AddView(view, 0, new Android.Widget.FrameLayout.LayoutParams(width, height));
                host = decor;
                view.LoadUrl(uri.AbsoluteUri);
                return true;
            });
            if (!attached) return new SnapshotResult(null, false, true);

            bool loaded = true;
            try
            {
                loaded = await finished.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException) { /* take whatever has rendered by now */ }

            // The page itself failed (no such host, empty answer, refused): the engine would only show its own
            // error screen, which is no preview of anything.
            if (!loaded || state.MainFrameFailed) return SnapshotResult.Nothing;

            await EvaluateAsync(view!, PrepareScript);
            var probe = await WaitForSettledAsync(view!, state);
            if (state.MainFrameFailed) return SnapshotResult.Nothing;

            var page = await ReadInfoAsync(view!);
            if (page is { IsInterstitial: true }) return new SnapshotResult(null, false, false, page);

            var (bitmap, content) = await DrawWithoutOverlaysAsync(view!, width, height, BackgroundFill(probe));
            if (content == SnapshotContent.Blank)
            {
                // Late scripts or images: give the page a little longer.
                bitmap?.Dispose();
                await Task.Delay(2500);
                (bitmap, content) = await DrawWithoutOverlaysAsync(view!, width, height, BackgroundFill(probe));
            }
            if (content == SnapshotContent.Blank)
            {
                // Some web view versions draw a hardware layer into a bitmap as nothing at all; software works there.
                bitmap?.Dispose();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    view!.SetLayerType(Android.Views.LayerType.Software, null);
                    view.Invalidate();
                });
                await Task.Delay(300);
                (bitmap, content) = await DrawWithoutOverlaysAsync(view!, width, height, BackgroundFill(probe));
            }
            return Save(bitmap, content, path, page);
        }
        catch
        {
            return SnapshotResult.Nothing;
        }
        finally
        {
            var v = view;
            var h = host;
            if (v is not null)
            {
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        // The activity may have been recreated meanwhile (rotation, theme change); still destroy.
                        try { v.StopLoading(); } catch { }
                        try { h?.RemoveView(v); } catch { }
                        try { v.Destroy(); } catch { }
                    });
                }
                catch { /* already gone */ }
            }
            Gate.Release();
        }
#else
        await Task.CompletedTask;
        return new SnapshotResult(null, false, true);
#endif
    }

    /// <summary>Takes a picture of a page the person opened in the in-app viewer, as they see it (its top 4:3
    /// part), and reads what the page says about itself. <paramref name="platformView"/> is the viewer's
    /// Android web view. Nothing is loaded: this only looks at what is already on screen.</summary>
    public static async Task<SnapshotResult> CaptureVisibleAsync(object? platformView, string key)
    {
#if ANDROID
        if (platformView is not Android.Webkit.WebView view) return SnapshotResult.Nothing;
        try
        {
            var page = await ReadInfoAsync(view);
            if (page is { IsInterstitial: true }) return new SnapshotResult(null, false, false, page);

            var (width, height) = await MainThread.InvokeOnMainThreadAsync(() =>
                (view.Width, Math.Min(view.Height, view.Width * CssHeight / CssWidth)));
            if (width <= 0 || height <= 0) return new SnapshotResult(null, false, false, page);

            var (bitmap, content) = await DrawAsync(view, width, height, Android.Graphics.Color.White);
            Directory.CreateDirectory(Dir);
            return Save(bitmap, content, Path.Combine(Dir, key + ".png"), page);
        }
        catch
        {
            return SnapshotResult.Nothing;
        }
#else
        await Task.CompletedTask;
        return SnapshotResult.Nothing;
#endif
    }

#if ANDROID
    private static SnapshotResult Save(Android.Graphics.Bitmap? bitmap, SnapshotContent content, string path, PageInfo? page)
    {
        if (bitmap is null || content == SnapshotContent.Blank)
        {
            bitmap?.Dispose();
            return new SnapshotResult(null, false, false, page);
        }
        try
        {
            using (var stream = File.Create(path))
                if (!bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 90, stream))
                    return new SnapshotResult(null, false, false, page);
            return new SnapshotResult(path, content == SnapshotContent.Sparse, false, page);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>Draws with overlays hidden; if that leaves the picture blank (the "overlay" was the page), the
    /// overlays come back and it is drawn as is.</summary>
    private static async Task<(Android.Graphics.Bitmap? Bitmap, SnapshotContent Content)> DrawWithoutOverlaysAsync(
        Android.Webkit.WebView view, int width, int height, Android.Graphics.Color fill)
    {
        var hiddenRaw = await EvaluateAsync(view, HideOverlaysScript);
        bool hidSomething = int.TryParse(hiddenRaw, out var hidden) && hidden > 0;
        if (hidSomething) await Task.Delay(150);   // let the page lay itself out without them

        var (bitmap, content) = await DrawAsync(view, width, height, fill);
        if (content != SnapshotContent.Blank || !hidSomething) return (bitmap, content);

        bitmap?.Dispose();
        await EvaluateAsync(view, RestoreScript);
        await Task.Delay(150);
        return await DrawAsync(view, width, height, fill);
    }

    /// <summary>Waits until the page stops changing: loaded completely, fonts in, and the same text length and
    /// number of loaded images on two polls in a row, between 0.8 and 7 seconds after the first load finished.
    /// A fixed wait was too short for pages built by script and needlessly long for simple ones.</summary>
    private static async Task<Probe?> WaitForSettledAsync(Android.Webkit.WebView view, LoadState state)
    {
        var started = DateTime.UtcNow;
        Probe? previous = null;
        while (!state.MainFrameFailed)
        {
            await Task.Delay(500);
            var probe = await ProbeAsync(view);
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            bool settled = probe is not null && previous is not null
                           && state.Progress >= 100
                           && probe.ReadyState == "complete"
                           && probe.FontsStatus != "loading"
                           && probe.TextLength == previous.TextLength
                           && probe.ImagesLoaded == previous.ImagesLoaded;
            if (probe is not null) previous = probe;
            if ((settled && elapsed >= MinSettleMs) || elapsed >= MaxSettleMs) break;
        }
        return previous;
    }

    private sealed record Probe(string ReadyState, int TextLength, int Images, int ImagesLoaded,
        string BodyBackground, string BodyColor, string RootBackground, string FontsStatus);

    private static async Task<Probe?> ProbeAsync(Android.Webkit.WebView view)
    {
        try
        {
            var json = await EvaluateAsync(view, ProbeScript);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var a = doc.RootElement;
            return new Probe(a[0].GetString() ?? "", a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32(),
                a[4].GetString() ?? "", a[5].GetString() ?? "", a[6].GetString() ?? "", a[7].GetString() ?? "");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PageInfo?> ReadInfoAsync(Android.Webkit.WebView view)
    {
        try
        {
            var json = await EvaluateAsync(view, InfoScript);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var a = doc.RootElement;
            if (!Uri.TryCreate(a[0].GetString(), UriKind.Absolute, out var url)) return null;
            static string? Text(JsonElement e) => HtmlHead.Clean(e.GetString()) is { Length: > 0 } t ? t : null;
            var image = Text(a[3]);
            var icons = a[5].EnumerateArray()
                .Select(i => new LinkTag(i[0].GetString() ?? "", i[1].GetString() ?? "", NullIfEmpty(i[2].GetString()), NullIfEmpty(i[3].GetString())))
                .Where(t => t.Href.Length > 0)
                .ToList();
            return new PageInfo(url, Text(a[1]), Text(a[2]),
                image is not null && Uri.TryCreate(image, UriKind.Absolute, out var img) && img.Scheme is "http" or "https" ? img.AbsoluteUri : null,
                Text(a[4]), icons);
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Runs a script in the page and returns its value: a string result comes back unwrapped, anything
    /// else as JSON text. Null when the page did not answer within a second.</summary>
    private static async Task<string?> EvaluateAsync(Android.Webkit.WebView view, string script)
    {
        try
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThread.InvokeOnMainThreadAsync(() => view.EvaluateJavascript(script, new JsCallback(result)));
            var raw = await result.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;
            // The callback hands over the script's value JSON-encoded: a string arrives quoted.
            return raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw) : raw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>What to paint under the page. White under a transparent page with light text would make white on
    /// white (a dark-scheme page), so that gets a dark background instead.</summary>
    private static Android.Graphics.Color BackgroundFill(Probe? probe)
    {
        if (probe is null) return Android.Graphics.Color.White;
        var body = ParseCssColor(probe.BodyBackground);
        if (body is { A: > 0 } b) return Android.Graphics.Color.Argb(255, b.R, b.G, b.B);
        var root = ParseCssColor(probe.RootBackground);
        if (root is { A: > 0 } r) return Android.Graphics.Color.Argb(255, r.R, r.G, r.B);
        var text = ParseCssColor(probe.BodyColor);
        if (text is { } t && Luminance(t.R, t.G, t.B) > 0.6) return Android.Graphics.Color.ParseColor("#121212");
        return Android.Graphics.Color.White;
    }

    private static async Task<(Android.Graphics.Bitmap? Bitmap, SnapshotContent Content)> DrawAsync(
        Android.Webkit.WebView view, int width, int height, Android.Graphics.Color fill)
    {
        try
        {
            var full = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var bitmap = Android.Graphics.Bitmap.CreateBitmap(width, height, Android.Graphics.Bitmap.Config.Argb8888!)!;
                using var canvas = new Android.Graphics.Canvas(bitmap);
                canvas.DrawColor(fill);
                // A web view draws its whole content; move it so the part scrolled into view lands on the bitmap.
                canvas.Translate(-view.ScrollX, -view.ScrollY);
                view.Draw(canvas);
                return bitmap;
            });

            // Saved and judged at the stored size: fewer pixels to look at, and the same picture the card shows.
            var scaled = full;
            if (full.Width > SavedWidth)
            {
                int scaledHeight = (int)Math.Round(full.Height * (double)SavedWidth / full.Width);
                scaled = Android.Graphics.Bitmap.CreateScaledBitmap(full, SavedWidth, scaledHeight, true)!;
                full.Dispose();
            }

            var pixels = new int[scaled.Width * scaled.Height];
            scaled.GetPixels(pixels, 0, scaled.Width, 0, 0, scaled.Width, scaled.Height);
            return (scaled, Classify(pixels, scaled.Width, scaled.Height));
        }
        catch
        {
            return (null, SnapshotContent.Blank);
        }
    }

    /// <summary>A web view that ignores all input. It sits behind the app's content, and Android hands touches
    /// the content does not consume to the sibling beneath; Clickable = false does not stop OnTouchEvent.</summary>
    private sealed class InertWebView(Android.Content.Context context) : Android.Webkit.WebView(context)
    {
        public override bool DispatchTouchEvent(Android.Views.MotionEvent? e) => false;
        public override bool OnTouchEvent(Android.Views.MotionEvent? e) => false;
        public override bool OnGenericMotionEvent(Android.Views.MotionEvent? e) => false;
    }

    private sealed class LoadState
    {
        public volatile int Progress;
        public volatile bool MainFrameFailed;
    }

    private sealed class Client(TaskCompletionSource<bool> finished, LoadState state) : Android.Webkit.WebViewClient
    {
        public override void OnPageFinished(Android.Webkit.WebView? view, string? url) => finished.TrySetResult(true);

        public override void OnReceivedError(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request, Android.Webkit.WebResourceError? error)
        {
            if (request?.IsForMainFrame != true) return;
            state.MainFrameFailed = true;
            finished.TrySetResult(false);
        }

        public override void OnReceivedHttpError(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request, Android.Webkit.WebResourceResponse? errorResponse)
        {
            // An error page still renders something (a 404 page, a login wall); let the picture decide.
        }
    }

    private sealed class Chrome(LoadState state) : Android.Webkit.WebChromeClient
    {
        public override void OnProgressChanged(Android.Webkit.WebView? view, int newProgress) => state.Progress = newProgress;
    }

    private sealed class JsCallback(TaskCompletionSource<string?> result) : Java.Lang.Object, Android.Webkit.IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => result.TrySetResult(value?.ToString());
    }
#endif

    // ------------------------------------------------------------------------------------------------
    // Pure helpers (no Android types), so a test project can cover them later.
    // ------------------------------------------------------------------------------------------------

    /// <summary>Judges a picture by how much of it differs from its most common colour, sampled on a 16 px grid:
    /// under 2% is blank, under 8% sparse, more is real content. The old check only rejected pictures that were
    /// 97% one colour, so a white page with a small logo passed and showed up as "white".</summary>
    internal static SnapshotContent Classify(int[] argb, int width, int height, int step = 16)
    {
        var counts = new Dictionary<int, int>();
        var samples = new List<int>();
        for (int y = step / 2; y < height; y += step)
            for (int x = step / 2; x < width; x += step)
            {
                int c = argb[y * width + x];
                samples.Add(c);
                int bucket = ((c >> 20) & 0xF) << 8 | ((c >> 12) & 0xF) << 4 | ((c >> 4) & 0xF);   // 4 bits per channel
                counts[bucket] = counts.GetValueOrDefault(bucket) + 1;
            }
        if (samples.Count == 0) return SnapshotContent.Blank;

        int dominantBucket = counts.MaxBy(kv => kv.Value).Key;
        int dominant = samples.First(c => (((c >> 20) & 0xF) << 8 | ((c >> 12) & 0xF) << 4 | ((c >> 4) & 0xF)) == dominantBucket);
        int ink = samples.Count(c => !Close(c, dominant));
        double share = (double)ink / samples.Count;
        return share < 0.02 ? SnapshotContent.Blank : share < 0.08 ? SnapshotContent.Sparse : SnapshotContent.Rich;
    }

    internal static bool Close(int a, int b)
    {
        int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
        int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
        int db = Math.Abs((a & 0xFF) - (b & 0xFF));
        return dr + dg + db < 24;
    }

    /// <summary>Reads "rgb(r, g, b)" and "rgba(r, g, b, a)" as computed styles report them.</summary>
    internal static (byte R, byte G, byte B, byte A)? ParseCssColor(string? css)
    {
        if (string.IsNullOrWhiteSpace(css)) return null;
        var m = Regex.Match(css, @"rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)(?:[,\s/]+([\d.]+%?))?\s*\)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        static byte Channel(string s) => (byte)Math.Clamp((int)Math.Round(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)), 0, 255);
        byte alpha = 255;
        if (m.Groups[4].Success)
        {
            var s = m.Groups[4].Value;
            double a = s.EndsWith('%')
                ? double.Parse(s.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100
                : double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            alpha = (byte)Math.Clamp((int)Math.Round(a * 255), 0, 255);
        }
        return (Channel(m.Groups[1].Value), Channel(m.Groups[2].Value), Channel(m.Groups[3].Value), alpha);
    }

    /// <summary>Relative luminance from 0 (black) to 1 (white).</summary>
    internal static double Luminance(byte r, byte g, byte b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
}
