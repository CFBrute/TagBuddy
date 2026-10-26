using TagBuddy.Services;
using Microsoft.Maui.Controls.Shapes;

namespace TagBuddy.Controls;

/// <summary>A card with a web page's picture, title and description. Give it any value: it stays hidden unless
/// the value is a web link (or plain text with <see cref="ShowText"/> on), and loads the preview on its own as
/// soon as the value is set. Tapping the card opens the link. Single-use links are never requested. With automatic
/// previews switched off (<see cref="AppSettings.AutoPreviews"/>) only a preview already on the phone shows; otherwise
/// the card says "Tap to load preview" and the first tap loads it.</summary>
public sealed class LinkPreviewView : ContentView
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(string), typeof(LinkPreviewView), null,
        propertyChanged: (b, _, _) => ((LinkPreviewView)b).Reload());

    /// <summary>Compact: a one-line row with a small thumbnail, for lists. Otherwise a full card.</summary>
    public static readonly BindableProperty CompactProperty = BindableProperty.Create(
        nameof(Compact), typeof(bool), typeof(LinkPreviewView), false,
        propertyChanged: (b, _, _) => { var v = (LinkPreviewView)b; v.BuildLayout(); v.Reload(); });

    /// <summary>Whether a value that is not a web link is shown as a plain text card. Off by default: the pages
    /// that use this card already show the text itself.</summary>
    public static readonly BindableProperty ShowTextProperty = BindableProperty.Create(
        nameof(ShowText), typeof(bool), typeof(LinkPreviewView), false,
        propertyChanged: (b, _, _) => ((LinkPreviewView)b).Reload());

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

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    /// <summary>What the picture frame shows: the page's picture, the site icon on a neutral tile, the link glyph
    /// on a blue tile (compact rows only, so rows line up), or nothing.</summary>
    private enum FrameMode { None, Picture, Icon, Glyph }

    private readonly Border _card;
    private readonly Image _image;
    private readonly Image _frameIcon;
    private readonly Border _imageFrame;
    private readonly Label _title;
    private readonly Label _description;
    private readonly Label _host;
    private readonly Image _hostIcon;
    private readonly HorizontalStackLayout _hostLine;
    private readonly Grid _grid;
    private Uri? _uri;
    private int _generation;

    /// <summary>Automatic previews are off and this card waits for a tap before it loads anything.</summary>
    private bool _awaitingTap;

    public LinkPreviewView()
    {
        IsVisible = false;

        _image = new Image { Aspect = Aspect.AspectFill };
        _frameIcon = new Image { Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, IsVisible = false };
        var frameContent = new Grid();
        frameContent.Add(_image);
        frameContent.Add(_frameIcon);
        _imageFrame = new Border
        {
            Content = frameContent,
            StrokeThickness = 0,
            Padding = 0,
            IsVisible = false,
        };
        _title = new Label { FontFamily = "OpenSansSemibold", FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
        _description = new Label { FontSize = 13, TextColor = Colors.Gray, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 3 };
        _host = new Label { FontSize = 11, TextColor = Color.FromArgb("#C7780B"), LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
        _hostIcon = new Image { WidthRequest = 16, HeightRequest = 16, Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.Center, IsVisible = false };
        _hostLine = new HorizontalStackLayout { Spacing = 6 };
        _hostLine.Add(_hostIcon);
        _hostLine.Add(_host);

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
            // A plain text card has no link; the row around it handles its own taps.
            if (_uri is null) return;
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _description.Text = "Loading preview...";
                _host.Text = _uri.Host;
                await LoadAsync(_generation);
                return;
            }
            if (!await Pages.WebPage.ShowAsync(_uri.AbsoluteUri)) await Links.OpenAsync(_uri);
        };
        _card.GestureRecognizers.Add(tap);

        Content = _card;
        BuildLayout();
        LinkPreviews.PreviewUpdated += OnPreviewUpdated;
    }

    /// <summary>Something new was learned about this card's link (the person opened its page): show it.</summary>
    private void OnPreviewUpdated(object? sender, Uri uri)
    {
        if (_uri is not null && uri.AbsoluteUri == _uri.AbsoluteUri) Reload();
    }

    private void BuildLayout()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();

        // Compact is the Saved-row preview: no card border or fill of its own, so it blends into the row.
        _card.StrokeThickness = Compact ? 0 : 1;
        if (Compact) _card.BackgroundColor = Colors.Transparent;
        else _card.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#1F2937"));

        if (Compact)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _grid.ColumnSpacing = 10;
            _grid.Padding = new Thickness(0);

            _imageFrame.WidthRequest = 48;
            _imageFrame.HeightRequest = 48;
            _imageFrame.StrokeShape = new RoundRectangle { CornerRadius = 6 };
            _title.FontSize = 14;
            _title.MaxLines = 1;
            _grid.Add(_imageFrame, 0, 0);

            var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
            text.Add(_title);
            text.Add(_description);
            text.Add(_hostLine);
            _grid.Add(text, 1, 0);
            return;
        }

        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _grid.Padding = 0;
        _grid.ColumnSpacing = 0;
        _imageFrame.WidthRequest = -1;
        _imageFrame.HeightRequest = 160;
        _imageFrame.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10, 10, 0, 0) };
        _title.FontSize = 15;
        _title.MaxLines = 2;
        _grid.Add(_imageFrame, 0, 0);

        var body = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(12, 10) };
        body.Add(_title);
        body.Add(_description);
        body.Add(_hostLine);
        _grid.Add(body, 0, 1);
    }

    private async void Reload()
    {
        int generation = ++_generation;
        _awaitingTap = false;

        if (!LinkPreviews.IsPreviewable(Value, out var uri))
        {
            _uri = null;
            if (ShowText && !string.IsNullOrWhiteSpace(Value))
            {
                ShowTextCard(Value!);
                return;
            }
            IsVisible = false;
            return;
        }

        _uri = uri;
        _title.IsVisible = true;
        // Known from the address alone ("YouTube · Video", "Short link (bit.ly)"), until the page says more.
        _title.Text = KnownSites.Describe(uri) ?? uri.Host;
        _description.IsVisible = !Compact;
        _description.MaxLines = 3;
        _hostLine.IsVisible = true;
        _host.Text = uri.Host;
        _hostIcon.Source = null;
        _hostIcon.IsVisible = false;
        SetFrame(Compact ? FrameMode.Glyph : FrameMode.None);
        IsVisible = true;

        // A sign-in or one-time link must only be requested once, by the page itself: a preview fetch would use
        // the code up. Skip the preview and let the tap open the page directly. The site's icon only needs the
        // site's address, so it is still shown.
        if (LinkPreviews.IsSingleUse(uri))
        {
            _description.IsVisible = !Compact;
            _description.Text = "This link carries a sign-in or one-time code, so no preview is fetched. Tap to open the page.";
            _host.Text = uri.Host + "  ·  tap to open";
            var icon = await LinkPreviews.GetIconAsync(uri);
            if (generation != _generation) return;
            if (icon is not null) ShowIcon(icon, inFrame: Compact);
            return;
        }

        if (!AppSettings.AutoPreviews)
        {
            var cached = await LinkPreviews.GetCachedAsync(uri);
            if (generation != _generation) return;
            if (cached is not null)
            {
                Show(cached);
                return;
            }
            _awaitingTap = true;
            _description.Text = "Previews load only when asked (see About). Tap to load this one.";
            _host.Text = uri.Host + "  ·  tap to load preview";
            if (await LinkPreviews.GetIconAsync(uri) is { } known && generation == _generation)
                ShowIcon(known, inFrame: Compact);
            return;
        }

        _description.Text = "Loading preview...";
        await LoadAsync(generation);
    }

    /// <summary>The value is text, not a link: show it as is, with no picture, title or site line.</summary>
    private void ShowTextCard(string text)
    {
        SetFrame(FrameMode.None);
        _title.IsVisible = false;
        _hostLine.IsVisible = false;
        _description.IsVisible = true;
        _description.MaxLines = Compact ? 4 : 8;
        _description.Text = text;
        IsVisible = true;
    }

    private async Task LoadAsync(int generation)
    {
        if (_uri is null) return;

        LinkPreview? preview;
        try { preview = await LinkPreviews.GetAsync(_uri); }
        catch { preview = null; }
        if (generation != _generation) return;   // the value changed while loading (list row recycled, new scan)

        if (preview is null)
        {
            // No answer from the site: keep a quiet one-liner in the full card, nothing at all in lists.
            if (Compact) { IsVisible = false; return; }
            _description.Text = "The site did not answer and the page could not be rendered, so there is no preview. Tap to open the page.";
            return;
        }
        Show(preview);
    }

    private void Show(LinkPreview preview)
    {
        _title.Text = preview.TitleOrHost;
        _description.Text = preview.Text ?? preview.Description ?? "This page has no preview text. Tap to open it.";
        _description.IsVisible = !Compact;
        _host.Text = preview.Host + "  ·  tap to open";

        bool hasIcon = preview.HasIcon && File.Exists(preview.IconUrl);
        if (preview.HasImage)
        {
            _image.Source = LinkPreviews.IsLocalPath(preview.ImageUrl!)
                ? ImageSource.FromFile(preview.ImageUrl!)
                : ImageSource.FromUri(new Uri(preview.ImageUrl!));
            SetFrame(FrameMode.Picture);
            if (hasIcon) ShowIcon(preview.IconUrl!, inFrame: false);
        }
        else if (hasIcon)
        {
            ShowIcon(preview.IconUrl!, inFrame: true);
        }
        else
        {
            SetFrame(Compact ? FrameMode.Glyph : FrameMode.None);
        }
    }

    /// <summary>Shows the site icon next to the host name, and in the picture frame when there is no picture.</summary>
    private string? _frameIconPath;

    private void ShowIcon(string path, bool inFrame)
    {
        _hostIcon.Source = ImageSource.FromFile(path);
        _hostIcon.IsVisible = true;
        if (!inFrame) return;
        _frameIcon.Source = ImageSource.FromFile(path);
        _frameIconPath = path;
        SetFrame(FrameMode.Icon);
    }

    private void SetFrame(FrameMode mode)
    {
        _imageFrame.IsVisible = mode != FrameMode.None;
        _image.IsVisible = mode == FrameMode.Picture;
        _frameIcon.IsVisible = mode is FrameMode.Icon or FrameMode.Glyph;
        if (mode != FrameMode.Picture) _image.Source = null;

        switch (mode)
        {
            case FrameMode.Picture:
                _imageFrame.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#E5E7EB"), Color.FromArgb("#E5E7EB"));
                break;
            case FrameMode.Icon:
                _frameIcon.WidthRequest = _frameIcon.HeightRequest = _frameIconPath is null
                    ? (Compact ? 32 : 64)
                    : Favicons.DisplaySize(_frameIconPath, Compact ? 32 : 64);
                _imageFrame.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F4F6"), Color.FromArgb("#374151"));
                break;
            case FrameMode.Glyph:
                _frameIcon.Source = "ic_link.png";
                _frameIcon.WidthRequest = _frameIcon.HeightRequest = 28;
                _imageFrame.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#2563EB"), Color.FromArgb("#2563EB"));
                break;
        }
    }
}
