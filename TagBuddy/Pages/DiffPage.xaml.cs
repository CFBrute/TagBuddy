using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>Two reads of the same kind of tag side by side: only the pages or blocks that differ, the differing bytes
/// in colour. Opened from the NFC tab's Compare button.</summary>
public partial class DiffPage : ContentPage, IQueryAttributable
{
    public const string Route = "diff";

    private static readonly Color LeftColor = Color.FromArgb("#3B82F6");    // blue
    private static readonly Color RightColor = Color.FromArgb("#F59E0B");   // amber
    private TagDiff? _diff;
    private string _leftName = "", _rightName = "";

    public DiffPage()
    {
        InitializeComponent();
    }

    /// <summary><paramref name="leftName"/> and <paramref name="rightName"/> say where each side came from
    /// ("read just now", "saved: Front door").</summary>
    public static Task ShowAsync(TagDiff diff, string leftName, string rightName) =>
        Shell.Current.GoToAsync(Route, new Dictionary<string, object>
        {
            ["diff"] = diff, ["left"] = leftName, ["right"] = rightName,
        });

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("diff", out var obj) || obj is not TagDiff diff) return;
        _diff = diff;
        string left = _leftName = query.TryGetValue("left", out var l) ? l.ToString() ?? "" : "";
        string right = _rightName = query.TryGetValue("right", out var r) ? r.ToString() ?? "" : "";
        bool any = diff.Differences.Count > 0;
        ApplyHint.IsVisible = ApplyLeftButton.IsVisible = ApplyRightButton.IsVisible = any;
        ApplyHint.Text = ApplyHint.Text.Replace("pages", diff.Layout.Noun + "s");

        LeftLabel.FormattedText = Side("Left:  ", LeftColor, $"{left}, tag {diff.Left.Id}");
        RightLabel.FormattedText = Side("Right: ", RightColor, $"{right}, tag {diff.Right.Id}");
        SummaryLabel.Text = diff.Summary + (diff.SameTag ? "" : " (two different tags of the same kind)");
        string noun = diff.Layout.Noun;
        Legend.Text = diff.Differences.Count == 0
            ? $"Every {noun} both sides read holds the same bytes."
            : $"Each {noun} that differs is shown twice, left above right, with the bytes that differ in colour. A {noun} only one side could read shows \"not read\" on the other.";

        var text = new FormattedString();
        foreach (var d in diff.Differences)
        {
            AddRow(text, d.Segment, d.Left, d.Changed, LeftColor);
            AddRow(text, d.Segment, d.Right, d.Changed, RightColor);
            Add(text, "\n", null);
        }
        DiffLabel.FormattedText = text;
    }

    private static FormattedString Side(string label, Color color, string text)
    {
        var f = new FormattedString();
        f.Spans.Add(new Span { Text = label, TextColor = color, FontAttributes = FontAttributes.Bold, FontFamily = "monospace" });
        f.Spans.Add(new Span { Text = text });
        return f;
    }

    private void AddRow(FormattedString text, int segment, byte[]? bytes, bool[] changed, Color color)
    {
        Add(text, $"{segment:D3}  ", Colors.Gray);
        if (bytes is null)
        {
            Add(text, "not read\n", Colors.Gray);
            return;
        }
        for (int i = 0; i < bytes.Length; i++)
        {
            Add(text, bytes[i].ToString("X2"), changed[i] ? color : null);
            Add(text, i % 4 == 3 ? "  " : " ", null);
        }
        Add(text, " " + Type2Memory.Ascii(bytes) + "\n", Colors.Gray);
    }

    private void Add(FormattedString text, string value, Color? color) =>
        text.Spans.Add(new Span
        {
            Text = value,
            // Spans do not take the label's font on Android; without it the hex columns drift.
            FontFamily = DiffLabel.FontFamily,
            FontSize = DiffLabel.FontSize,
            TextColor = color,
            FontAttributes = color is not null && color != Colors.Gray ? FontAttributes.Bold : FontAttributes.None,
        });

    private void OnApplyLeft(object? sender, EventArgs e) => Apply(toLeft: true);
    private void OnApplyRight(object? sender, EventArgs e) => Apply(toLeft: false);

    /// <summary>Opens the editor on one side's tag with the other side's bytes, for the segments that differ and the other
    /// side read, typed in as changes. Segments the other side could not read are left as they are.</summary>
    private async void Apply(bool toLeft)
    {
        if (_diff is null) return;
        var bytes = _diff.Differences
            .Where(d => (toLeft ? d.Right : d.Left) is not null)
            .ToDictionary(d => d.Segment, d => (toLeft ? d.Right : d.Left)!);
        var (target, targetName, sourceName) = toLeft ? (_diff.Left, _leftName, _rightName) : (_diff.Right, _rightName, _leftName);
        await AppNavigation.ToNfcApplyAsync(target, targetName, bytes, sourceName);
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (_diff is null) return;
        await Clipboard.Default.SetTextAsync(string.Join("\n", _diff.Lines()));
        Notice.Show("Copied");
    }
}
