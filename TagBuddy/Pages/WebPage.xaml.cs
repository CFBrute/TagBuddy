using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>A plain in-app view of a web page, for links that came from a code or a tag. Nothing is stored:
/// the view is a window onto the page, with a way out to the full browser.</summary>
public partial class WebPage : ContentPage, IQueryAttributable
{
    public const string Route = "web";

    private Uri? _uri;

    public WebPage()
    {
        InitializeComponent();
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
            Title = uri.Host;
            UrlLabel.Text = uri.AbsoluteUri;
            Web.Source = new UrlWebViewSource { Url = uri.AbsoluteUri };
        }
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
            UrlLabel.Text = $"{e.Url}  ({e.Result})";
    }

    private void OnReload(object? sender, EventArgs e) => Web.Reload();

    private async void OnOpenExternal(object? sender, EventArgs e)
    {
        if (_uri is not null) await Links.OpenAsync(_uri);
    }
}
