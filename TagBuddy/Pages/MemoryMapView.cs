using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Draws a tag's memory as a map, for the NFC read result and a saved dump: an overview of which pages are
/// writable, behind the password, locked for good, one-time or fixed, and every page in the colour of its access,
/// with the special pages named on their line.</summary>
internal static class MemoryMapView
{
    private static Color ColorOf(PageAccess access) => access switch
    {
        PageAccess.Writable => Color.FromArgb("#22C55E"),
        PageAccess.Password => Color.FromArgb("#3B82F6"),
        PageAccess.Locked => Color.FromArgb("#EF4444"),
        PageAccess.OneTime => Color.FromArgb("#F59E0B"),
        PageAccess.Fixed => Color.FromArgb("#9CA3AF"),
        PageAccess.Config => Color.FromArgb("#A855F7"),
        PageAccess.Hidden => Color.FromArgb("#6B7280"),
        _ => Colors.Gray,
    };

    /// <summary>Fills <paramref name="overview"/> with the access groups and <paramref name="pagesLabel"/> with one
    /// line per page. Without a known chip the pages are shown plain and the overview says why.</summary>
    public static void Show(Label overview, Label pagesLabel, byte[] memory, string? chipName)
    {
        var chip = Type2Memory.ChipFor(chipName, memory.Length);
        var map = Type2Memory.MapPages(memory, chip, Features.Amiibo && Type2Memory.LooksAmiibo(memory));
        var lines = Type2Memory.FormatDump(memory) ?? [];

        var summary = new FormattedString();
        if (chip is null)
        {
            summary.Spans.Add(new Span { Text = "Which page is what depends on the chip, and this one is not known.", TextColor = Colors.Gray });
        }
        else
        {
            summary.Spans.Add(new Span { Text = chip.Name + "\n", FontAttributes = FontAttributes.Bold });
            var groups = Type2Memory.SummarizePages(map);
            for (int i = 0; i < groups.Count; i++)
            {
                var (access, pages) = groups[i];
                summary.Spans.Add(new Span { Text = "■ ", TextColor = ColorOf(access) });
                summary.Spans.Add(new Span { Text = $"{Type2Memory.Describe(access)}: pages {pages}" + (i < groups.Count - 1 ? "\n" : "") });
            }
        }
        overview.FormattedText = summary;

        var text = new FormattedString();
        for (int i = 0; i < lines.Count; i++)
        {
            var page = i < map.Count ? map[i] : null;
            var line = lines[i] + (page is { Role.Length: > 0 } ? "  " + page.Role : "") + (i < lines.Count - 1 ? "\n" : "");
            text.Spans.Add(new Span
            {
                Text = line,
                // Spans do not take the label's font on Android; without it the hex columns drift.
                FontFamily = pagesLabel.FontFamily,
                FontSize = pagesLabel.FontSize,
                TextColor = page is null || page.Access == PageAccess.Unknown ? null : ColorOf(page.Access),
            });
        }
        pagesLabel.FormattedText = text;
    }
}
