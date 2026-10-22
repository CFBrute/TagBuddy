using System.Text.Json;
using System.Text.RegularExpressions;

namespace TagBuddy.Services;

/// <summary>How much a rendered page shows: next to nothing, a little (a logo on an empty page), or real content.</summary>
public enum SnapshotContent { Blank, Sparse, Rich }

/// <summary>Renders a web page in a web view and saves a picture of it, for links whose page offers no preview
/// image of its own. It is the heavy last resort: one page at a time, with a hard time limit, and a picture
/// with (almost) nothing on it counts as no picture.
///
/// Why the web view is attached to the window: a web view that is not attached counts as hidden, so the engine
/// throttles or skips animation frames and lazy images, and pages that draw on an animation frame come out
/// white. It is added behind the app's own content, at a phone-like width in CSS pixels, so it is "visible" to
/// the page but never to the person.</summary>
public static class PageSnapshot
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string Dir = Path.Combine(FileSystem.CacheDirectory, "link-snapshots");

    private const int CssWidth = 412;    // a common phone width in CSS pixels
    private const int CssHeight = 309;   // 4:3
    private const int SavedWidth = 720;
    private const int MinSettleMs = 800;
    private const int MaxSettleMs = 6000;

    /// <summary>The page's state as seen by script: ready state, text length, image counts and colours.</summary>
    private const string ProbeScript =
        "(function(){var b=document.body;var cs=b?getComputedStyle(b):null;" +
        "var hs=document.documentElement?getComputedStyle(document.documentElement):null;" +
        "var imgs=[].slice.call(document.images||[]);" +
        "return JSON.stringify([document.readyState,b?(b.innerText||'').length:0,imgs.length," +
        "imgs.filter(function(i){return i.complete;}).length,cs?cs.backgroundColor:'',cs?cs.color:'',hs?hs.backgroundColor:'']);})()";

    /// <summary>The saved picture's path (null when there is none), whether the picture is sparse, and whether
    /// the attempt was skipped because the app has no window right now (in the background).</summary>
    public static async Task<(string? Path, bool Sparse, bool Skipped)> CaptureAsync(Uri uri, string key, int timeoutMs = 15000)
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
            if (!attached) return (null, false, true);

            bool loaded = true;
            try
            {
                loaded = await finished.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException) { /* take whatever has rendered by now */ }

            // The page itself failed (no such host, empty answer, refused): the engine would only show its own
            // error screen, which is no preview of anything.
            if (!loaded || state.MainFrameFailed) return (null, false, false);

            var probe = await WaitForSettledAsync(view!, state);
            if (state.MainFrameFailed) return (null, false, false);

            var fill = BackgroundFill(probe);
            var (bitmap, content) = await DrawAsync(view!, width, height, fill);
            if (content == SnapshotContent.Blank)
            {
                // Late scripts or images: give the page a little longer.
                bitmap?.Dispose();
                await Task.Delay(2500);
                (bitmap, content) = await DrawAsync(view!, width, height, fill);
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
                (bitmap, content) = await DrawAsync(view!, width, height, fill);
            }
            if (bitmap is null || content == SnapshotContent.Blank)
            {
                bitmap?.Dispose();
                return (null, false, false);
            }

            try
            {
                using (var stream = File.Create(path))
                    if (!bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 90, stream)) return (null, false, false);
                return (path, content == SnapshotContent.Sparse, false);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        catch
        {
            return (null, false, false);
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
                        System.Diagnostics.Debug.WriteLine($"PageSnapshot: web view removed, attached={v.IsAttachedToWindow}");
                    });
                }
                catch { /* already gone */ }
            }
            Gate.Release();
        }
#else
        await Task.CompletedTask;
        return (null, false, true);
#endif
    }

#if ANDROID
    /// <summary>Waits until the page stops changing: loaded completely, and the same text length and number of
    /// loaded images on two polls in a row, between 0.8 and 6 seconds after the first load finished. A fixed
    /// wait was too short for pages built by script and needlessly long for simple ones.</summary>
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
                           && probe.TextLength == previous.TextLength
                           && probe.ImagesLoaded == previous.ImagesLoaded;
            if (probe is not null) previous = probe;
            if ((settled && elapsed >= MinSettleMs) || elapsed >= MaxSettleMs) break;
        }
        return previous;
    }

    private sealed record Probe(string ReadyState, int TextLength, int Images, int ImagesLoaded, string BodyBackground, string BodyColor, string RootBackground);

    private static async Task<Probe?> ProbeAsync(Android.Webkit.WebView view)
    {
        try
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MainThread.InvokeOnMainThreadAsync(() => view.EvaluateJavascript(ProbeScript, new JsCallback(result)));
            var raw = await result.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (string.IsNullOrEmpty(raw) || raw == "null") return null;

            // The callback hands over the script's value JSON-encoded: a string holding our JSON array.
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null) return null;
            using var doc = JsonDocument.Parse(json);
            var a = doc.RootElement;
            return new Probe(a[0].GetString() ?? "", a[1].GetInt32(), a[2].GetInt32(), a[3].GetInt32(),
                a[4].GetString() ?? "", a[5].GetString() ?? "", a[6].GetString() ?? "");
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
