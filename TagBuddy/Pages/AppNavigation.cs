using TagBuddy.Models;

namespace TagBuddy.Pages;

/// <summary>Hands content from one tab to another: to a generator, or to the NFC tab for writing. The keys are the
/// query parameters the receiving pages declare with <see cref="QueryPropertyAttribute"/>.</summary>
public static class AppNavigation
{
    public const string Value = "value";
    public const string Format = "format";
    public const string ErrorCorrection = "ecc";
    public const string Margin = "margin";
    public const string Text = "text";
    public const string Dump = "dump";
    public const string ViewRead = "view-read";
    public const string EditRead = "edit-read";

    /// <summary>Opens the Barcode tab with a value and a linear format key.</summary>
    public static Task ToBarcodeGeneratorAsync(string value, string format) =>
        Shell.Current.GoToAsync("//generate/barcode", new Dictionary<string, object>
        {
            [Value] = value,
            [Format] = format,
        });

    /// <summary>Opens the QR tab with a value and, optionally, its error-correction level and quiet zone.</summary>
    public static Task ToQrGeneratorAsync(string value, string errorCorrection, int? margin = null)
    {
        var query = new Dictionary<string, object> { [Value] = value, [ErrorCorrection] = errorCorrection };
        if (margin is not null) query[Margin] = margin.Value.ToString();
        return Shell.Current.GoToAsync("//generate/qr", query);
    }

    /// <summary>Opens the NFC tab ready to write a text or link.</summary>
    public static Task ToNfcWriteTextAsync(string text) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [Text] = text });

    /// <summary>Opens the NFC tab showing a saved read as it was read.</summary>
    public static Task ToNfcViewReadAsync(SavedItem dump) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [ViewRead] = dump.Id.ToString() });

    /// <summary>Opens the NFC tab's segment editor on a saved read, expecting the tag it came from.</summary>
    public static Task ToNfcEditReadAsync(SavedItem dump) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [EditRead] = dump.Id.ToString() });

    /// <summary>Opens the NFC tab ready to write a saved dump.</summary>
    public static Task ToNfcWriteDumpAsync(SavedItem dump) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [Dump] = dump.Id.ToString() });
}
