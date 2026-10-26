using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Draws a MIFARE Classic read (the "sector / block" lines from <see cref="NfcReader"/>) with its structure in
/// colour: sector headers dimmed, each sector's trailer with key A, the rule bytes and key B in their own colours, and the
/// rule lines under it in the rule colour. A legend above says which is which. Data blocks stay plain.</summary>
internal static class ClassicDumpView
{
    private static readonly Color KeyA = Color.FromArgb("#3B82F6");    // blue
    private static readonly Color Rules = Color.FromArgb("#A855F7");   // purple
    private static readonly Color KeyB = Color.FromArgb("#F59E0B");    // amber
    private static readonly Color Dim = Colors.Gray;

    /// <summary>Fills <paramref name="legend"/> and <paramref name="label"/> from the read's lines.</summary>
    public static void Show(Label legend, Label label, IReadOnlyList<string> lines)
    {
        var key = new FormattedString();
        key.Spans.Add(new Span { Text = "Each sector ends with its trailer: " });
        key.Spans.Add(new Span { Text = "key A", TextColor = KeyA, FontAttributes = FontAttributes.Bold });
        key.Spans.Add(new Span { Text = " · " });
        key.Spans.Add(new Span { Text = "rules", TextColor = Rules, FontAttributes = FontAttributes.Bold });
        key.Spans.Add(new Span { Text = " · " });
        key.Spans.Add(new Span { Text = "key B", TextColor = KeyB, FontAttributes = FontAttributes.Bold });
        key.Spans.Add(new Span { Text = ". Keys read back as zeros, except key B where the rules make it plain data. The rules are spelled out under each trailer." });
        legend.FormattedText = key;

        var text = new FormattedString();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i] + (i < lines.Count - 1 ? "\n" : "");
            if (line.StartsWith("Sector"))
                Add(text, label, line, Dim);
            else if (line.StartsWith("  rules:"))
                Add(text, label, line, Rules);
            else if (TrailerBlock(line))
                AddTrailer(text, label, line);
            else
                Add(text, label, line, null);
        }
        label.FormattedText = text;
    }

    /// <summary>"  NNN  ..." where block NNN is a sector trailer.</summary>
    private static bool TrailerBlock(string line) =>
        line.Length > 5 && int.TryParse(line.AsSpan(2, 3), out int block) && line[5] == ' ' && MifareClassicKeys.IsTrailer(block);

    /// <summary>The trailer line: "  NNN  AAAAAAAA AAAARRRR RR9BBBBB BBBBBBBB  ascii". The hex starts at column 7 and runs in
    /// four groups of eight digits with a space between; byte k is at group k/4, digit (k%4)*2.</summary>
    private static void AddTrailer(FormattedString text, Label label, string line)
    {
        const int hexStart = 7;
        Add(text, label, line[..hexStart], Dim);
        int pos = hexStart;
        for (int k = 0; k < 16; k++)
        {
            if (k > 0 && k % 4 == 0) { Add(text, label, " ", null); pos++; }
            if (pos + 2 > line.Length) break;
            var color = k < 6 ? KeyA : k < 9 ? Rules : k == 9 ? Dim : KeyB;
            Add(text, label, line.Substring(pos, 2), color);
            pos += 2;
        }
        if (pos < line.Length) Add(text, label, line[pos..], Dim);
    }

    private static void Add(FormattedString text, Label label, string value, Color? color) =>
        text.Spans.Add(new Span
        {
            Text = value,
            // Spans do not take the label's font on Android; without it the hex columns drift.
            FontFamily = label.FontFamily,
            FontSize = label.FontSize,
            TextColor = color,
        });
}
