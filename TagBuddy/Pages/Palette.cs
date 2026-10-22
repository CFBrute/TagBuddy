namespace TagBuddy.Pages;

/// <summary>Colours set from code, the same values as in Resources/Styles/Colors.xaml.</summary>
internal static class Palette
{
    /// <summary>The golden orange "Primary": selected buttons, success text.</summary>
    public static readonly Color Accent = Color.FromArgb("#F5A623");

    /// <summary>Text on <see cref="Accent"/> ("PrimaryDarkText").</summary>
    public static readonly Color AccentText = Color.FromArgb("#1C1200");

    /// <summary>Buttons that are not selected, and secondary buttons.</summary>
    public static readonly Color Neutral = Color.FromArgb("#6B7280");

    public static readonly Color Error = Color.FromArgb("#DC2626");

    /// <summary>Colours a button of a choice row (filter, mode, alignment) as selected or not.</summary>
    public static void Select(Button button, bool selected)
    {
        button.BackgroundColor = selected ? Accent : Neutral;
        button.TextColor = selected ? AccentText : Colors.White;
    }
}
