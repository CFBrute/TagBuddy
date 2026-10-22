using TagBuddy.Services;
using Microsoft.Maui.Controls.Shapes;

namespace TagBuddy.Controls;

/// <summary>A card with a web page's picture, title and description. Give it any value: it stays hidden unless
/// the value is a web link, and loads the preview on its own. Tapping the card opens the link.</summary>
public sealed class LinkPreviewView : ContentView
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(string), typeof(LinkPreviewView), null,
        propertyChanged: (b, _, _) => ((LinkPreviewView)b).Reload());

    /// <summary>Compact: a one-line row with a small thumbnail, for lists. Otherwise a full card.</summary>
    public static readonly BindableProperty CompactProperty = BindableProperty.Create(
        nameof(Compact), typeof(bool), typeof(LinkPreviewView), false,
        propertyChanged: (b, _, _) => { var v = (LinkPreviewView)b; v.BuildLayout(); v.Reload(); });

    /// <summary>Whether the page is fetched as soon as the value is set. Off for content that just arrived from
    /// a scanned code or a tag: the site is not contacted until the person taps the card.</summary>
    public static readonly BindableProperty AutoLoadProperty = BindableProperty.Create(
        nameof(AutoLoad), typeof(bool), typeof(LinkPreviewView), true);

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public bool AutoLoad
    {
        get => (bool)GetValue(AutoLoadProperty);
        set => SetValue(AutoLoadProperty, value);
    }

    private readonly Border _card;
    private readonly Image _image;
    private readonly Border _imageFrame;
    private readonly Label _title;
    private readonly Label _description;
    private readonly Label _host;
    private readonly Grid _grid;
    private Uri? _uri;
    private int _generation;
    private bool _loaded;

    public LinkPreviewView()
    {
        IsVisible = false;

        _image = new Image { Aspect = Aspect.AspectFill };
        _imageFrame = new Border
        {
            Content = _image,
            StrokeThickness = 0,
            Padding = 0,
            BackgroundColor = Color.FromArgb("#E5E7EB"),
            IsVisible = false,
        };
        _title = new Label { FontFamily = "OpenSansSemibold", FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
        _description = new Label { FontSize = 13, TextColor = Colors.Gray, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 3 };
        _host = new Label { FontSize = 11, TextColor = Color.FromArgb("#C7780B"), LineBreakMode = LineBreakMode.TailTruncation };

        _grid = new Grid();
        _card = new Border
        {
            Stroke = Color.FromArgb("#D1D5DB"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = 0,
            Content = _grid,
        };
        _card.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#1F2937"));
        _title.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#F9FAFB"));

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_uri is null) return;
            if (!_loaded) { await LoadAsync(); return; }   // first tap fetches when AutoLoad is off
            if (!await Pages.WebPage.ShowAsync(_uri.AbsoluteUri)) await Links.OpenAsync(_uri);
        };
        _card.GestureRecognizers.Add(tap);

        Content = _card;
        BuildLayout();
    }

    private void BuildLayout()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();

        if (Compact)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _grid.ColumnSpacing = 10;
            _grid.Padding = new Thickness(8);

            _imageFrame.WidthRequest = 48;
            _imageFrame.HeightRequest = 48;
            _imageFrame.StrokeShape = new RoundRectangle { CornerRadius = 6 };
            _title.FontSize = 14;
            _title.MaxLines = 1;
            _grid.Add(_imageFrame, 0, 0);

            var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            text.Add(_title);
            text.Add(_host);
            _grid.Add(text, 1, 0);
            return;
        }

        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _imageFrame.WidthRequest = -1;
        _imageFrame.HeightRequest = 160;
        _imageFrame.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10, 10, 0, 0) };
        _title.FontSize = 15;
        _title.MaxLines = 2;
        _grid.Add(_imageFrame, 0, 0);

        var body = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(12, 10) };
        body.Add(_title);
        body.Add(_description);
        body.Add(_host);
        _grid.Add(body, 0, 1);
    }

    private async void Reload()
    {
        ++_generation;
        _loaded = false;

        if (!LinkPreviews.IsPreviewable(Value, out var uri))
        {
            _uri = null;
            IsVisible = false;
            return;
        }

        _uri = uri;
        _title.Text = uri.Host;
        _description.IsVisible = !Compact;
        _host.Text = uri.Host;
        _image.Source = null;
        _imageFrame.IsVisible = false;
        IsVisible = true;

        // A sign-in or one-time link must only be requested once, by the page itself: a preview fetch would
        // use the code up. Skip the preview and let the tap open the page directly.
        if (LooksSingleUse(uri))
        {
            _loaded = true;
            _description.Text = "This link carries a sign-in or one-time code, so no preview is fetched. Tap to open the page.";
            _host.Text = uri.Host + "  ·  tap to open";
            return;
        }

        _description.Text = AutoLoad ? "Loading preview..." : "Tap to load a preview of this page";
        if (AutoLoad) await LoadAsync();
    }

    private static bool LooksSingleUse(Uri uri) =>
        System.Text.RegularExpressions.Regex.IsMatch(uri.Query + uri.Fragment,
            @"[?&#](code|token|otp|ticket|nonce|auth|session|sig|key)=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task LoadAsync()
    {
        if (_uri is null) return;
        int generation = _generation;
        _description.Text = "Loading preview...";

        var preview = await LinkPreviews.GetAsync(_uri);
        if (generation != _generation) return;   // the value changed while loading (list row recycled, new scan)

        if (preview is null)
        {
            // No answer from the site: keep a quiet one-liner in the full card, nothing at all in lists.
            if (Compact) { IsVisible = false; return; }
            _loaded = true;                              // let the next tap open the page anyway
            _description.Text = "The site did not answer and the page could not be rendered, so there is no preview. Tap to open the page.";
            return;
        }

        _loaded = true;
        _title.Text = preview.TitleOrHost;
        _description.Text = preview.Description ?? "This page has no preview text. Tap to open it.";
        _description.IsVisible = !Compact;
        _host.Text = preview.Host + "  ·  tap to open";

        if (preview.HasImage)
        {
            _image.Source = LinkPreviews.IsLocalPath(preview.ImageUrl!)
                ? ImageSource.FromFile(preview.ImageUrl!)
                : ImageSource.FromUri(new Uri(preview.ImageUrl!));
            _imageFrame.IsVisible = true;
        }
    }
}
