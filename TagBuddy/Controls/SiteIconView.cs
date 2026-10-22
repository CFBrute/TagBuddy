using Microsoft.Maui.Controls.Shapes;
using TagBuddy.Services;

namespace TagBuddy.Controls;

/// <summary>The 56 px tile of a saved link: the site's icon on white once it is known, the blue link glyph until
/// then (and for sites without a usable icon). Works for single-use links too, because the icon only needs the
/// site's address, never the link itself.</summary>
public sealed class SiteIconView : ContentView
{
    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value), typeof(string), typeof(SiteIconView), null,
        propertyChanged: (b, _, _) => ((SiteIconView)b).Reload());

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static readonly Color GlyphBackground = Color.FromArgb("#2563EB");

    private readonly Border _tile;
    private readonly Image _glyph;
    private readonly Image _icon;
    private int _generation;

    public SiteIconView()
    {
        _glyph = new Image
        {
            Source = "ic_link.png",
            WidthRequest = 28,
            HeightRequest = 28,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _icon = new Image
        {
            Aspect = Aspect.AspectFit,
            WidthRequest = 36,
            HeightRequest = 36,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        var grid = new Grid();
        grid.Add(_glyph);
        grid.Add(_icon);

        _tile = new Border
        {
            WidthRequest = 56,
            HeightRequest = 56,
            StrokeThickness = 0,
            Stroke = Color.FromArgb("#D1D5DB"),
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = GlyphBackground,
            VerticalOptions = LayoutOptions.Center,
            Content = grid,
        };
        Content = _tile;
        InputTransparent = true;   // the row handles taps
        LinkPreviews.PreviewUpdated += OnPreviewUpdated;
    }

    /// <summary>An icon was learned for some link: reload when it is this tile's site.</summary>
    private void OnPreviewUpdated(object? sender, Uri uri)
    {
        if (Links.IsWebLink(Value, out var mine) && KnownSites.IconHost(mine) == KnownSites.IconHost(uri)) Reload();
    }

    private async void Reload()
    {
        int generation = ++_generation;   // a recycled list row must not show the previous item's icon
        ShowGlyph();

        if (!Links.IsWebLink(Value, out var uri)) return;
        var path = await LinkPreviews.GetIconAsync(uri);
        if (generation != _generation || path is null || !File.Exists(path)) return;

        _icon.Source = ImageSource.FromFile(path);
        _icon.WidthRequest = _icon.HeightRequest = Favicons.DisplaySize(path, 36);
        _icon.IsVisible = true;
        _glyph.IsVisible = false;
        _tile.BackgroundColor = Colors.White;
        _tile.StrokeThickness = 1;   // a white icon tile needs an edge on a white card, like the code thumbnails
    }

    private void ShowGlyph()
    {
        _icon.Source = null;
        _icon.IsVisible = false;
        _glyph.IsVisible = true;
        _tile.BackgroundColor = GlyphBackground;
        _tile.StrokeThickness = 0;
    }
}
