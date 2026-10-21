namespace BarcodeBuddy.Models;

public class BarcodeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Format { get; set; } = "Code128";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>QR only: error-correction level L, M, Q or H.</summary>
    public string QrErrorCorrection { get; set; } = QrOptions.DefaultErrorCorrection;

    /// <summary>QR only: quiet-zone width in modules.</summary>
    public int QrMargin { get; set; } = QrOptions.DefaultMargin;

    /// <summary>Text printed under the code in shared and saved images. Null means "same as the value";
    /// an empty string means no text at all. May contain line breaks.</summary>
    public string? Caption { get; set; }

    /// <summary>Where the image text sits under the code: "Left", "Center" (default) or "Right".</summary>
    public string CaptionAlign { get; set; } = "Center";

    // The three layout settings are ratios of the code itself, so an image looks the same whatever the code's
    // pixel size: the code is the unit.

    /// <summary>Height of the image text as a fraction of the code's height (the dark part, for a QR code).</summary>
    public double CaptionTextScale { get; set; } = DefaultCaptionTextScale;

    public const double DefaultCaptionTextScale = 0.15;
    public const double MinCaptionTextScale = 0.05;
    public const double MaxCaptionTextScale = 1.0;

    /// <summary>Distance from the dark edge of the code to the top of the text as a fraction of the code's
    /// height. 0 means the text touches the code.</summary>
    public double CaptionGapScale { get; set; } = DefaultCaptionGapScale;

    public const double DefaultCaptionGapScale = 0.15;
    public const double MinCaptionGapScale = 0.0;
    public const double MaxCaptionGapScale = 1.0;

    /// <summary>How wide a line of text may be, in code widths. The image grows sideways to fit the text up
    /// to this; a longer line is broken. The code stays centred.</summary>
    public double CaptionWidthScale { get; set; } = DefaultCaptionWidthScale;

    public const double DefaultCaptionWidthScale = 2.0;
    public const double MinCaptionWidthScale = 1.0;
    public const double MaxCaptionWidthScale = 8.0;

    public string ImageText => Caption ?? Value;
    public bool CaptionLeft => CaptionAlign == "Left";
    public bool CaptionRight => CaptionAlign == "Right";

    public int CaptionTextPx(int codeHeightPx) =>
        Math.Max(8, (int)Math.Round(Math.Clamp(CaptionTextScale, MinCaptionTextScale, MaxCaptionTextScale) * codeHeightPx));

    /// <summary>Space between the bottom edge of the code matrix and the top of the text, in image pixels.
    /// A QR matrix ends with its white quiet zone, so the distance is measured from the dark modules and the
    /// quiet zone is subtracted; small distances put the text inside the quiet zone. Linear codes have no
    /// quiet zone below the bars.</summary>
    public int CaptionGapPx(int codeHeightPx, int quietZonePx) =>
        (int)Math.Round(Math.Clamp(CaptionGapScale, MinCaptionGapScale, MaxCaptionGapScale) * codeHeightPx) - quietZonePx;

    public int CaptionMaxTextWidthPx(int codeWidthPx) =>
        (int)Math.Round(Math.Clamp(CaptionWidthScale, MinCaptionWidthScale, MaxCaptionWidthScale) * codeWidthPx);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Value : Name;
    public string FormatLabel => BarcodeTypes.LabelFor(Format);
    public bool IsQr => Format == BarcodeTypes.QrCode.Key;

    public QrOptions Qr => new(QrErrorCorrection, QrMargin);
}

/// <summary>Options that change how a QR code is generated.</summary>
public record QrOptions(string ErrorCorrection = QrOptions.DefaultErrorCorrection, int Margin = QrOptions.DefaultMargin)
{
    public const string DefaultErrorCorrection = "M";
    public const int DefaultMargin = 2;

    public static readonly IReadOnlyList<(string Level, string Label)> Levels =
    [
        ("L", "L - low, recovers up to 7% damage (smallest code)"),
        ("M", "M - medium, recovers up to 15% (default)"),
        ("Q", "Q - quartile, recovers up to 25%"),
        ("H", "H - high, recovers up to 30% (largest code)"),
    ];

    public static string DescribeLevel(string? level) => level switch
    {
        "L" => "L (up to 7% of the code can be damaged)",
        "M" => "M (up to 15% of the code can be damaged)",
        "Q" => "Q (up to 25% of the code can be damaged)",
        "H" => "H (up to 30% of the code can be damaged)",
        _ => level ?? "unknown",
    };
}
