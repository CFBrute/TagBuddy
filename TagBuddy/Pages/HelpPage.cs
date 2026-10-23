using TagBuddy.Models;

namespace TagBuddy.Pages;

/// <summary>The Help tab under More: one card per topic of <see cref="HelpContent"/>; a tap opens the topic.</summary>
public class HelpPage : ContentPage
{
    public HelpPage()
    {
        Title = "Help";
        var list = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        list.Add(new Label
        {
            Text = "How Tag Buddy works. Pick a topic; the details are in boxes that open when tapped.",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var topic in HelpContent.Topics)
            list.Add(TopicCard(topic));
        Content = new ScrollView { Content = list };
    }

    /// <summary>A card with the topic's icon, title and one-line summary.</summary>
    internal static View TopicCard(HelpTopic topic)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(44), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 12,
        };
        grid.Add(HelpViews.IconBox(topic.Icon, 44));

        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
        text.Add(new Label { Text = topic.Title, FontFamily = "OpenSansSemibold", FontSize = 16 });
        text.Add(new Label { Text = topic.Summary, FontSize = 13, TextColor = Colors.Gray });
        grid.Add(text, 1);
        grid.Add(new Label { Text = "›", FontSize = 26, TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Center }, 2);

        var card = HelpViews.Card(grid);
        card.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () => await HelpTopicPage.ShowAsync(topic.Id)),
        });
        SemanticProperties.SetDescription(card, $"{topic.Title}. {topic.Summary}");
        return card;
    }
}
