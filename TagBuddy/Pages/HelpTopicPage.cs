using TagBuddy.Models;

namespace TagBuddy.Pages;

/// <summary>One help topic: its blocks drawn top to bottom, with the in-depth parts in boxes that open on a tap.</summary>
public class HelpTopicPage : ContentPage, IQueryAttributable
{
    public const string Route = "helptopic";

    /// <summary>Opens a topic. <paramref name="replace"/> swaps the topic on show for the new one (See also links),
    /// so following links never builds a long trail of pages to go back through: back always leads to the list.</summary>
    public static Task ShowAsync(string topicId, bool replace = false) =>
        Shell.Current.GoToAsync(replace ? $"../{Route}" : Route, new Dictionary<string, object> { ["id"] = topicId });

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && HelpContent.Find(id as string) is { } topic)
            Show(topic);
    }

    private void Show(HelpTopic topic)
    {
        Title = topic.Title;
        var page = new VerticalStackLayout { Padding = new Thickness(16, 16, 16, 32), Spacing = 12 };

        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(52), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 12 };
        header.Add(HelpViews.IconBox(topic.Icon, 52));
        var titles = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        titles.Add(new Label { Text = topic.Title, FontFamily = "OpenSansSemibold", FontSize = 22 });
        titles.Add(new Label { Text = topic.Summary, FontSize = 13, TextColor = Colors.Gray });
        header.Add(titles, 1);
        page.Add(header);

        foreach (var block in topic.Blocks)
            page.Add(HelpViews.Block(block));

        Content = new ScrollView { Content = page };
    }
}

/// <summary>Draws help blocks. Shared by the topic list and the topic pages.</summary>
internal static class HelpViews
{
    public static View Block(HelpBlock block) => block switch
    {
        HelpText t => new Label { Text = t.Text, FontSize = 14, LineHeight = 1.15 },
        HelpHeading h => new Label { Text = h.Text, FontAttributes = FontAttributes.Bold, FontSize = 16, Margin = new Thickness(0, 6, 0, -4) },
        HelpList l => List(l),
        HelpTip tip => Tip(tip),
        HelpImage img => Image(img),
        HelpMore more => Expander(more),
        HelpSeeAlso see => SeeAlso(see),
        _ => new ContentView(),
    };

    private static View List(HelpList list)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        for (int i = 0; i < list.Items.Length; i++)
        {
            var row = new Grid { ColumnDefinitions = [new ColumnDefinition(list.Numbered ? 24 : 16), new ColumnDefinition(GridLength.Star)] };
            row.Add(new Label
            {
                Text = list.Numbered ? $"{i + 1}." : "•",
                FontSize = 14,
                FontAttributes = list.Numbered ? FontAttributes.Bold : FontAttributes.None,
                TextColor = Palette.Accent,
            });
            row.Add(new Label { Text = list.Items[i], FontSize = 14, LineHeight = 1.15 }, 1);
            stack.Add(row);
        }
        return stack;
    }

    /// <summary>A tinted box with a coloured edge: orange for a tip, red for a warning.</summary>
    private static View Tip(HelpTip tip)
    {
        var edge = tip.Warning ? Palette.Error : Palette.Accent;
        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(4), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 12 };
        grid.Add(new BoxView { Color = edge, CornerRadius = 2 });
        grid.Add(new Label
        {
            Text = (tip.Warning ? "Careful: " : "Tip: ") + tip.Text,
            FontSize = 14,
            LineHeight = 1.15,
            Margin = new Thickness(0, 10, 10, 10),
        }, 1);

        var box = new Border { StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = grid };
        box.SetAppThemeColor(VisualElement.BackgroundColorProperty,
            tip.Warning ? Color.FromArgb("#FDECEC") : Color.FromArgb("#FFF4E0"),
            tip.Warning ? Color.FromArgb("#3B1717") : Color.FromArgb("#33270F"));
        return box;
    }

    /// <summary>An illustration (drawn on its own light card, so it works in both themes) with its caption.</summary>
    private static View Image(HelpImage img)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Add(new Image { Source = img.File, HeightRequest = img.Height, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Center });
        stack.Add(new Label { Text = img.Caption, FontSize = 12, TextColor = Colors.Gray, LineHeight = 1.15 });
        return stack;
    }

    /// <summary>A card whose header opens and closes the details under it.</summary>
    private static View Expander(HelpMore more)
    {
        var chevron = new Label { Text = "▸", FontSize = 18, TextColor = Palette.Accent, VerticalOptions = LayoutOptions.Center };
        var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(new Label { Text = more.Title, FontFamily = "OpenSansSemibold", FontSize = 15, VerticalOptions = LayoutOptions.Center });
        header.Add(chevron, 1);

        var body = new VerticalStackLayout { Spacing = 10, IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var block in more.Blocks) body.Add(Block(block));

        var stack = new VerticalStackLayout();
        stack.Add(header);
        stack.Add(body);

        var card = Card(stack);
        SemanticProperties.SetDescription(header, $"{more.Title}, tap to open");
        header.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                body.IsVisible = !body.IsVisible;
                chevron.Text = body.IsVisible ? "▾" : "▸";
                SemanticProperties.SetDescription(header, $"{more.Title}, tap to {(body.IsVisible ? "close" : "open")}");
            }),
        });
        return card;
    }

    private static View SeeAlso(HelpSeeAlso see)
    {
        var stack = new VerticalStackLayout { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        stack.Add(new Label { Text = "See also", FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = Colors.Gray });
        foreach (var id in see.TopicIds)
        {
            if (HelpContent.Find(id) is not { } topic) continue;
            var link = new Label { Text = $"{topic.Title}  ›", FontSize = 15, Padding = new Thickness(0, 4) };
            link.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#C7780B"), Palette.Accent);
            link.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await HelpTopicPage.ShowAsync(topic.Id, replace: true)) });
            stack.Add(link);
        }
        return stack;
    }

    /// <summary>A rounded card in the app's list style.</summary>
    public static Border Card(View content)
    {
        var card = new Border
        {
            Padding = 14,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Content = content,
        };
        card.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#F3F4F6"), Color.FromArgb("#1F2937"));
        card.SetAppThemeColor(Border.StrokeProperty, Color.FromArgb("#E5E7EB"), Color.FromArgb("#374151"));
        return card;
    }

    /// <summary>A black glyph on the accent colour, readable in both themes (the glyphs are drawn black).</summary>
    public static View IconBox(string icon, double size) => new Border
    {
        WidthRequest = size,
        HeightRequest = size,
        BackgroundColor = Palette.Accent,
        StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = size / 4 },
        VerticalOptions = LayoutOptions.Center,
        Content = new Image { Source = icon, WidthRequest = size * 0.55, HeightRequest = size * 0.55, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
    };
}
