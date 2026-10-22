namespace TagBuddy.Pages;

/// <summary>The "label  value" rows under a scan result or a tag read.</summary>
internal static class DetailRows
{
    public static void Add(Layout list, string label, string value, double labelWidth)
    {
        var row = new Grid { ColumnDefinitions = [new ColumnDefinition(labelWidth), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
        row.Add(new Label { Text = label, FontSize = 13, TextColor = Colors.Gray });
        var v = new Label { Text = value, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
        Grid.SetColumn(v, 1);
        row.Add(v);
        list.Children.Add(row);
    }
}
