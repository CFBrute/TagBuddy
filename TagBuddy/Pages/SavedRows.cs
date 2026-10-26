using TagBuddy.Models;

namespace TagBuddy.Pages;

/// <summary>A collapsible group heading in the Saved list: its name, how many items it holds, and whether it is open.
/// Tapping it shows or hides those items, so groups keep the list short until you open the one you want.</summary>
public sealed class GroupRow(string key, string name, int count, bool expanded, bool alt)
{
    /// <summary>The group's value ("" for the default "No group"); what the expanded-state set is keyed by.</summary>
    public string Key { get; } = key;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public bool Expanded { get; } = expanded;

    /// <summary>Every other group heading, for an alternating background.</summary>
    public bool Alt { get; } = alt;

    public string Chevron => Expanded ? "▾" : "▸";
    public string CountLabel => $"{Count}";
}

/// <summary>Picks the Saved list's row template: a group heading or a saved item.</summary>
public sealed class SavedRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate Item { get; set; } = null!;
    public DataTemplate Group { get; set; } = null!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        item is GroupRow ? Group : Item;
}
