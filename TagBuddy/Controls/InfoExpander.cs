using TagBuddy.Pages;

namespace TagBuddy.Controls;

/// <summary>A collapsible block of explanatory text: a tappable one-line header ("ⓘ What this means") with a chevron,
/// and the paragraph itself hidden until it is tapped. It keeps the long help text in the app out of the way of the
/// controls it describes, so a screen reads as a short list of settings rather than a wall of grey text, while the
/// explanation is still one tap away.
///
/// <para>Set <see cref="Text"/> to the paragraph and, optionally, <see cref="Title"/> to the header wording (the
/// default is "What this means"). Collapsed by default; set <see cref="Expanded"/> to start open.</para></summary>
public sealed class InfoExpander : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(InfoExpander), default(string),
        propertyChanged: (b, _, _) => ((InfoExpander)b).Apply());

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(InfoExpander), "What this means",
        propertyChanged: (b, _, _) => ((InfoExpander)b).Apply());

    public static readonly BindableProperty ExpandedProperty = BindableProperty.Create(
        nameof(Expanded), typeof(bool), typeof(InfoExpander), false,
        propertyChanged: (b, _, _) => ((InfoExpander)b).Apply());

    public static readonly BindableProperty FontSizeProperty = BindableProperty.Create(
        nameof(FontSize), typeof(double), typeof(InfoExpander), 12.0,
        propertyChanged: (b, _, _) => ((InfoExpander)b).Apply());

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool Expanded { get => (bool)GetValue(ExpandedProperty); set => SetValue(ExpandedProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private readonly Label _chevron;
    private readonly Label _header;
    private readonly Label _body;

    public InfoExpander()
    {
        _chevron = new Label { Text = "▸", FontSize = 12, TextColor = Palette.Neutral, VerticalOptions = LayoutOptions.Center };
        _header = new Label { FontSize = 12, TextColor = Palette.Neutral, VerticalOptions = LayoutOptions.Center };
        _body = new Label { FontSize = 12, TextColor = Colors.Gray, IsVisible = false, Margin = new Thickness(16, 2, 0, 0) };

        var head = new HorizontalStackLayout { Spacing = 6, Children = { _chevron, _header } };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Expanded = !Expanded;
        head.GestureRecognizers.Add(tap);

        Content = new VerticalStackLayout { Spacing = 2, Children = { head, _body } };
        Apply();
    }

    private void Apply()
    {
        _header.Text = Expanded ? "Hide" : Title;
        _header.FontSize = _chevron.FontSize = FontSize;
        _chevron.Text = Expanded ? "▾" : "▸";
        _body.Text = Text;
        _body.FontSize = FontSize;
        _body.IsVisible = Expanded;
    }
}
