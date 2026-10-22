using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>A plain in-app view of a web page, for links that came from a code or a tag, with a way out to the
/// full browser. Once the page has loaded, the app keeps what it showed as the link's preview when it has no
/// better one (<see cref="LinkPreviews.LearnFromOpenedPageAsync"/>): its title, picture and icon, or a picture of
/// the top of the page. Only the page the link led to, never later pages the person goes on to.</summary>
public partial class WebPage : ContentPage, IQueryAttributable
{
    public const string Route = "web";

    private Uri? _uri;

    /// <summary>The first page that loaded from the link (after redirects); learning is done once per opening.</summary>
    private Uri? _landed;

    /// <summary>The page on show now, which may be further along than the link the person came in with.</summary>
    private Uri? _current;
    private bool _visible;

    public WebPage()
    {
        InitializeComponent();
        UrlLabel.HandlerChanged += (_, _) => AttachCopyOnLongPress();
    }

    /// <summary>A long press on the address copies it: the page on show now, which is also what the open button
    /// opens. MAUI has no long-press gesture, so this uses Android's own on the label.</summary>
    private void AttachCopyOnLongPress()
    {
#if ANDROID
        if (UrlLabel.Handler?.PlatformView is not Android.Views.View view) return;
        view.LongClickable = true;
        view.LongClick += async (_, _) =>
        {
            if ((_current ?? _uri) is { } page) await CodeActions.CopyTextAsync(this, page.AbsoluteUri);
        };
#endif
    }

    /// <summary>Opens the viewer for a web link. Values that are not web links open nothing and return false.</summary>
    public static async Task<bool> ShowAsync(string? value)
    {
        if (!Links.IsWebLink(value, out var uri)) return false;
        await Shell.Current.GoToAsync(Route, new Dictionary<string, object> { ["url"] = uri.AbsoluteUri });
        return true;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("url", out var obj) && obj is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _uri = uri;
            _landed = null;
            _current = uri;
            Title = uri.Host;
            UrlLabel.Text = uri.AbsoluteUri;
            Web.Source = new UrlWebViewSource { Url = uri.AbsoluteUri };
            ShowOpenExternalLabel(uri);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
    }

    protected override bool OnBackButtonPressed()
    {
        if (Web.CanGoBack)
        {
            Web.GoBack();
            return true;
        }
        return base.OnBackButtonPressed();
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        Progress.IsVisible = true;
        UrlLabel.Text = e.Url;
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        Progress.IsVisible = false;
        if (e.Result != WebNavigationResult.Success)
        {
            UrlLabel.Text = $"{e.Url}  ({e.Result})";
            return;
        }
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var shown) && shown.Scheme is "http" or "https")
        {
            _current = shown;
            ShowOpenExternalLabel(shown);
        }
        if (_landed is null && _uri is { } link && Uri.TryCreate(e.Url, UriKind.Absolute, out var landed))
        {
            _landed = landed;
            LearnFromPage(link, landed);
        }
    }

    /// <summary>Gives the page a moment to draw, then keeps what it shows for the link's preview card, provided
    /// the person is still looking at the same site.</summary>
    private async void LearnFromPage(Uri link, Uri landed)
    {
        try
        {
            await Task.Delay(1500);
            if (!_visible || _uri != link) return;
            var capture = await PageSnapshot.CaptureVisibleAsync(Web.Handler?.PlatformView, LinkPreviews.OpenedSnapshotKey(link));
            if (capture.Page is { } page && KnownSites.IconHost(page.Url) != KnownSites.IconHost(landed))
            {
                // The person has gone on to another site meanwhile: that page is not what the link shows.
                if (capture.Path is not null) File.Delete(capture.Path);
                return;
            }
            await LinkPreviews.LearnFromOpenedPageAsync(link, capture);
        }
        catch
        {
            // Learning is a bonus; the page is open either way.
        }
    }

    /// <summary>Names the app the page will open in, since that is not always a browser: a YouTube link goes to
    /// the YouTube app. When the app cannot tell (Android picks, or the site's app is not in the manifest queries), it says either may open.</summary>
    private void ShowOpenExternalLabel(Uri page) =>
        OpenExternalButton.Text = Links.HandlerName(page) is { } app ? $"Open in {app}" : "Open in app or browser";

    private async void OnOpenExternal(object? sender, EventArgs e)
    {
        if ((_current ?? _uri) is { } current && !await Links.OpenAsync(current))
            await DisplayAlertAsync("No app", "No app on this phone can open this page.", "OK");
    }
}
