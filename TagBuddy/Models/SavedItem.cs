using System.Text.Json.Serialization;
using TagBuddy.Services;

namespace TagBuddy.Models;

/// <summary>What a saved item is: a code to show or print, a piece of text, or a binary copy of a tag.</summary>
public enum SavedKind
{
    Code,
    Text,
    Dump,
}

/// <summary>One entry in the Saved tab. Codes carry a value and a format; text notes carry just a value; tag
/// dumps point at a .bin file in the app's data folder and keep what is known about the tag they came from.</summary>
public class SavedItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;

    /// <summary>Picked in Saved's select mode (long press). Only on screen, never stored.</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>The group the item is filed under in Saved ("Work", "Home"), or empty for none. Groups exist only
    /// through their items: one with no items left is gone.</summary>
    public string Group { get; set; } = string.Empty;

    [JsonIgnore] public bool HasGroup => !string.IsNullOrWhiteSpace(Group);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SavedKind Kind { get; set; } = SavedKind.Code;

    /// <summary>The code's content, or the note's text. Empty for dumps.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Codes only: the barcode format key.</summary>
    public string Format { get; set; } = "Code128";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>When it was last shown, opened, shared, copied or written; null until then. Saved is sorted by it.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>The time Saved sorts by, newest first: last use, or when it was saved if it never was.</summary>
    [JsonIgnore] public DateTime SortTime => LastUsedAt ?? CreatedAt;

    // ---------------------------------------------------------------- QR options

    /// <summary>QR only: error-correction level L, M, Q or H.</summary>
    public string QrErrorCorrection { get; set; } = QrOptions.DefaultErrorCorrection;

    /// <summary>QR only: quiet-zone width in modules.</summary>
    public int QrMargin { get; set; } = QrOptions.DefaultMargin;

    // ---------------------------------------------------------------- image text (caption)

    /// <summary>Text printed under the code in shared and saved images. Null means "same as the value";
    /// an empty string means no text at all. May contain line breaks.</summary>
    public string? Caption { get; set; }

    /// <summary>Where the image text sits under the code: "Left", "Center" (default) or "Right".</summary>
    public string CaptionAlign { get; set; } = "Center";

    /// <summary>The image text's font, a key of <see cref="CaptionFonts"/>.</summary>
    public string CaptionFont { get; set; } = CaptionFonts.Default;

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

    [JsonIgnore] public string ImageText => Caption ?? Value;
    [JsonIgnore] public bool CaptionLeft => CaptionAlign == "Left";
    [JsonIgnore] public bool CaptionRight => CaptionAlign == "Right";

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

    // ---------------------------------------------------------------- tag dumps

    /// <summary>Dumps only: the .bin file name inside the dumps folder.</summary>
    public string DumpFile { get; set; } = string.Empty;

    /// <summary>Dumps only: serial of the tag the memory was read from ("" when imported from a file).</summary>
    public string DumpSerial { get; set; } = string.Empty;

    /// <summary>Dumps only: chip name as detected ("NTAG215", "Amiibo", "Type2", ...).</summary>
    public string DumpChip { get; set; } = string.Empty;

    public int DumpSize { get; set; }

    /// <summary>Dumps only: the file (in the reads folder) holding the tag exactly as it was read, when it was saved
    /// from a read rather than imported: every detail, the memory and what the segment editor needs. Empty for an
    /// imported or pasted dump.</summary>
    public string ReadFile { get; set; } = string.Empty;

    /// <summary>Whether the tag can be shown again as it was read, and edited for writing back.</summary>
    [JsonIgnore] public bool HasRead => IsDump && ReadFile.Length > 0;

    /// <summary>Whether the .bin is a Type 2 page image that "Write to a tag" can put on an NTAG/Ultralight. A MIFARE
    /// Classic or ISO 15693 read is saved as a read, not a page image, and is written through the editor instead.</summary>
    [JsonIgnore] public bool IsType2Dump => IsDump && !DumpChip.Contains("Classic", StringComparison.OrdinalIgnoreCase)
        && !DumpChip.Contains("15693", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dumps only: readable content found in the memory (an NDEF link or text, or plain ASCII runs),
    /// worked out when the dump is saved so the list can show it without opening the file.</summary>
    public string DumpText { get; set; } = string.Empty;

    // ---------------------------------------------------------------- derived
    // Worked out from the stored properties above, so [JsonIgnore] keeps them out of saved.json.

    [JsonIgnore] public bool IsCode => Kind == SavedKind.Code;
    [JsonIgnore] public bool IsText => Kind == SavedKind.Text;
    [JsonIgnore] public bool IsDump => Kind == SavedKind.Dump;
    [JsonIgnore] public bool IsQr => IsCode && Format == BarcodeTypes.QrCode.Key;

    /// <summary>The value is a web link (codes and text alike; dumps have no value).</summary>
    [JsonIgnore] public bool IsLink => !IsDump && Links.IsWebLink(Value, out _);

    /// <summary>A text note that is a web link: its row shows the site's icon instead of "Aa".</summary>
    [JsonIgnore] public bool IsLinkText => IsText && IsLink;

    /// <summary>A text note that is not a link: its row shows "Aa" and a few lines of the text.</summary>
    [JsonIgnore] public bool IsPlainText => IsText && !IsLink;

    [JsonIgnore] public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : IsDump ? $"{DumpChip} {DumpSerial}".Trim()
        : Value;

    /// <summary>Short kind label for the list: the barcode format, "Text", or the chip.</summary>
    [JsonIgnore] public string FormatLabel => Kind switch
    {
        SavedKind.Text => "Text",
        SavedKind.Dump => DumpChip.Length > 0 ? DumpChip : "Tag dump",
        _ => BarcodeTypes.LabelFor(Format),
    };

    /// <summary>Second line of the list row. A code with its own text under the image shows that text too, so
    /// versions of the same code can be told apart.</summary>
    [JsonIgnore] public string Subtitle => Kind switch
    {
        SavedKind.Dump => $"{DumpSize} bytes ({DumpSize / 4} pages)" + (DumpSerial.Length > 0 ? $"  ·  {DumpSerial}" : ""),
        SavedKind.Code when ImageText != Value => ImageText.Length == 0
            ? $"{Value}  ·  no text under the image"
            : $"{Value}  ·  text: {ImageText.Split('\n')[0].Trim()}",
        _ => Value,
    };

    // ---------------------------------------------------------------- comparing codes

    /// <summary>The same code for a scanner: the same content in the same format.</summary>
    public bool IsSameCode(SavedItem other) =>
        IsCode && other.IsCode && Value == other.Value && Format == other.Format;

    /// <summary>What would make this code's picture look different from <paramref name="other"/>'s, in words for
    /// the person ("the text under the image", "error correction", ...). Empty when the pictures are identical.
    /// Only meaningful for the same code (<see cref="IsSameCode"/>); the text's layout only counts when there is
    /// text.</summary>
    public IReadOnlyList<string> ImageDifferences(SavedItem other)
    {
        var differences = new List<string>();
        if (IsQr && other.IsQr)
        {
            if (QrErrorCorrection != other.QrErrorCorrection) differences.Add("error correction");
            if (QrMargin != other.QrMargin) differences.Add("margin");
        }
        if (ImageText != other.ImageText)
        {
            differences.Add("the text under the image");
        }
        else if (ImageText.Length > 0)
        {
            if (CaptionAlign != other.CaptionAlign) differences.Add("the text's position");
            if (CaptionFonts.Get(CaptionFont).Key != CaptionFonts.Get(other.CaptionFont).Key) differences.Add("the text's font");
            if (!Near(CaptionTextScale, other.CaptionTextScale)) differences.Add("the text's size");
            if (!Near(CaptionGapScale, other.CaptionGapScale)) differences.Add("the text's distance");
            if (!Near(CaptionWidthScale, other.CaptionWidthScale)) differences.Add("the text's max width");
        }
        return differences;
    }

    // The dials step by 0.01, so anything closer is the same setting.
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

    /// <summary>Dumps only: the readable text line, if any.</summary>
    [JsonIgnore] public bool HasDumpText => IsDump && DumpText.Length > 0;

    [JsonIgnore] public QrOptions Qr => new(QrErrorCorrection, QrMargin);
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
