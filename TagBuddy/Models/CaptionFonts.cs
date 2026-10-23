namespace TagBuddy.Models;

/// <summary>A font for the text under a code: an Android system font family, regular or bold.</summary>
public sealed record CaptionFontInfo(string Key, string Label, string Family, bool Bold);

/// <summary>The fonts offered for the text under the image. Only Android's own families, which every phone has, so
/// a saved code looks the same wherever it is drawn again. The key is what is stored.</summary>
public static class CaptionFonts
{
    public const string Default = "sans";

    public static IReadOnlyList<CaptionFontInfo> All { get; } =
    [
        new("sans", "Sans (default)", "sans-serif", false),
        new("sans-bold", "Sans bold", "sans-serif", true),
        new("condensed", "Condensed", "sans-serif-condensed", false),
        new("condensed-bold", "Condensed bold", "sans-serif-condensed", true),
        new("serif", "Serif", "serif", false),
        new("serif-bold", "Serif bold", "serif", true),
        new("mono", "Monospace", "monospace", false),
    ];

    /// <summary>The font for a stored key; an unknown or missing key (older saves) gets the default.</summary>
    public static CaptionFontInfo Get(string? key) => All.FirstOrDefault(f => f.Key == key) ?? All[0];
}
