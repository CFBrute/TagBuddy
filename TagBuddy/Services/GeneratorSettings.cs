using TagBuddy.Models;

namespace TagBuddy.Services;

/// <summary>How the person last set up each generator, so the QR and Barcode tabs open the way they left them: the
/// text-under-the-image settings (kept apart for each tab, since a QR code and a barcode have different shapes),
/// the barcode type, and the QR options. The text itself is not kept; it follows each new code's content.</summary>
public static class GeneratorSettings
{
    public const string Qr = "qr";
    public const string Barcode = "barcode";

    /// <summary>The image-text settings of one generator.</summary>
    public sealed record Caption(string Align, double TextScale, double GapScale, double WidthScale, string Font)
    {
        public static Caption Default { get; } = new("Center", SavedItem.DefaultCaptionTextScale,
            SavedItem.DefaultCaptionGapScale, SavedItem.DefaultCaptionWidthScale, CaptionFonts.Default);

        /// <summary>The same settings brought inside the ranges the dials allow, with an unknown position or font
        /// replaced by the default, so a value from an older or damaged store cannot break the section.</summary>
        public Caption Sanitized() => new(
            Align is "Left" or "Center" or "Right" ? Align : "Center",
            Clamp(TextScale, SavedItem.MinCaptionTextScale, SavedItem.MaxCaptionTextScale, SavedItem.DefaultCaptionTextScale),
            Clamp(GapScale, SavedItem.MinCaptionGapScale, SavedItem.MaxCaptionGapScale, SavedItem.DefaultCaptionGapScale),
            Clamp(WidthScale, SavedItem.MinCaptionWidthScale, SavedItem.MaxCaptionWidthScale, SavedItem.DefaultCaptionWidthScale),
            CaptionFonts.Get(Font).Key);

        private static double Clamp(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    public static Caption LoadCaption(string generator)
    {
        var d = Caption.Default;
        return new Caption(
            Preferences.Default.Get($"{generator}-caption-align", d.Align),
            Preferences.Default.Get($"{generator}-caption-size", d.TextScale),
            Preferences.Default.Get($"{generator}-caption-distance", d.GapScale),
            Preferences.Default.Get($"{generator}-caption-width", d.WidthScale),
            Preferences.Default.Get($"{generator}-caption-font", d.Font)).Sanitized();
    }

    public static void SaveCaption(string generator, Caption caption)
    {
        Preferences.Default.Set($"{generator}-caption-align", caption.Align);
        Preferences.Default.Set($"{generator}-caption-size", caption.TextScale);
        Preferences.Default.Set($"{generator}-caption-distance", caption.GapScale);
        Preferences.Default.Set($"{generator}-caption-width", caption.WidthScale);
        Preferences.Default.Set($"{generator}-caption-font", caption.Font);
    }

    /// <summary>The barcode type last picked on the Barcode tab, as a format key.</summary>
    public static string BarcodeFormat
    {
        get => Preferences.Default.Get("barcode-format", BarcodeTypes.Linear[0].Key);
        set => Preferences.Default.Set("barcode-format", value);
    }

    /// <summary>The QR error-correction level last picked: L, M, Q or H.</summary>
    public static string QrErrorCorrection
    {
        get => Preferences.Default.Get("qr-error-correction", QrOptions.DefaultErrorCorrection);
        set => Preferences.Default.Set("qr-error-correction", value);
    }

    /// <summary>The QR quiet-zone margin last picked, in modules.</summary>
    public static int QrMargin
    {
        get => Preferences.Default.Get("qr-margin", QrOptions.DefaultMargin);
        set => Preferences.Default.Set("qr-margin", value);
    }
}
