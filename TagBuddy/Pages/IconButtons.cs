namespace TagBuddy.Pages;

internal static class IconButtons
{
    /// <summary>Updates the long-press tooltip and accessibility label of an icon-only button.</summary>
    public static void SetLabel(Button button, string label)
    {
        ToolTipProperties.SetText(button, label);
        SemanticProperties.SetDescription(button, label);
    }
}
