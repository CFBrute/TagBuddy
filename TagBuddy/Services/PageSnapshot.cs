namespace TagBuddy.Services;

/// <summary>Renders a web page in an off-screen web view and saves a picture of it, for links whose page
/// offers no preview image of its own. One page at a time, with a hard time limit, and a picture that is
/// a single flat colour (nothing rendered) counts as no picture.</summary>
public static class PageSnapshot
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string Dir = Path.Combine(FileSystem.CacheDirectory, "link-snapshots");

    /// <summary>Path of the saved PNG, or null when the page could not be rendered in time.</summary>
    public static async Task<string?> CaptureAsync(Uri uri, string key, int width = 720, int height = 540, int timeoutMs = 12000)
    {
#if ANDROID
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, key + ".png");
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Android.Webkit.WebView? view = null;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var context = Platform.CurrentActivity ?? Platform.AppContext;
                view = new Android.Webkit.WebView(context);
                view.Settings.JavaScriptEnabled = true;
                view.Settings.DomStorageEnabled = true;
                view.Settings.LoadWithOverviewMode = true;
                view.Settings.UseWideViewPort = true;
                view.Settings.BlockNetworkImage = false;
                view.SetLayerType(Android.Views.LayerType.Software, null);   // drawable without a window
                view.SetWebViewClient(new Client(finished));
                view.Measure(
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(width, Android.Views.MeasureSpecMode.Exactly),
                    Android.Views.View.MeasureSpec.MakeMeasureSpec(height, Android.Views.MeasureSpecMode.Exactly));
                view.Layout(0, 0, width, height);
                view.LoadUrl(uri.AbsoluteUri);
            });

            bool loaded = true;
            try
            {
                loaded = await finished.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException) { /* take whatever has rendered by now */ }

            if (!loaded)
            {
                // The page itself failed (no such host, empty answer, refused): the engine would only show its
                // own error screen, which is no preview of anything.
                await MainThread.InvokeOnMainThreadAsync(() => { try { view?.StopLoading(); view?.Destroy(); } catch { } });
                return null;
            }

            // Give scripts and web fonts a moment after the load event before drawing.
            await Task.Delay(1200);

            bool saved = await MainThread.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    if (view is null) return false;
                    using var bitmap = Android.Graphics.Bitmap.CreateBitmap(width, height, Android.Graphics.Bitmap.Config.Argb8888!)!;
                    using var canvas = new Android.Graphics.Canvas(bitmap);
                    canvas.DrawColor(Android.Graphics.Color.White);
                    view.Draw(canvas);
                    if (IsFlat(bitmap)) return false;
                    using var stream = File.Create(path);
                    return bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 90, stream);
                }
                catch
                {
                    return false;
                }
                finally
                {
                    try { view?.StopLoading(); view?.Destroy(); } catch { /* already gone */ }
                }
            });

            return saved ? path : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
#else
        await Task.CompletedTask;
        return null;
#endif
    }

#if ANDROID
    /// <summary>True when the picture is (almost) one colour: the page drew nothing worth showing.</summary>
    private static bool IsFlat(Android.Graphics.Bitmap bitmap)
    {
        int first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        int same = 0, total = 0;
        for (int y = 8; y < bitmap.Height; y += 24)
            for (int x = 8; x < bitmap.Width; x += 24)
            {
                total++;
                if (Close(bitmap.GetPixel(x, y), first)) same++;
            }
        return total == 0 || same * 100 / total >= 97;

        static bool Close(int a, int b)
        {
            int dr = Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF));
            int dg = Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF));
            int db = Math.Abs((a & 0xFF) - (b & 0xFF));
            return dr + dg + db < 24;
        }
    }

    private sealed class Client(TaskCompletionSource<bool> finished) : Android.Webkit.WebViewClient
    {
        public override void OnPageFinished(Android.Webkit.WebView? view, string? url) => finished.TrySetResult(true);

        public override void OnReceivedError(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request, Android.Webkit.WebResourceError? error)
        {
            if (request?.IsForMainFrame == true) finished.TrySetResult(false);
        }

        public override void OnReceivedHttpError(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request, Android.Webkit.WebResourceResponse? errorResponse)
        {
            // An error page still renders something (a 404 page, a login wall); let the picture decide.
        }
    }
#endif
}
