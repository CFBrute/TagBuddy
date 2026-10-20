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
