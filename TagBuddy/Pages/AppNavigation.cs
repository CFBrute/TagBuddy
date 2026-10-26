using TagBuddy.Models;
using TagBuddy.Services;

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
    public const string Apply = "apply";

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

    /// <summary>Opens the NFC tab's segment editor on <paramref name="target"/> (a read, expecting that tag), with
    /// <paramref name="bytes"/> typed over its segments as pending changes: the other side of a comparison.</summary>
    public static Task ToNfcApplyAsync(NfcTagInfo target, string targetName, IReadOnlyDictionary<int, byte[]> bytes, string sourceName) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [Apply] = new DiffApply(target, targetName, bytes, sourceName) });

    /// <summary>Opens the NFC tab ready to write a saved dump.</summary>
    public static Task ToNfcWriteDumpAsync(SavedItem dump) =>
        Shell.Current.GoToAsync("//nfc", new Dictionary<string, object> { [Dump] = dump.Id.ToString() });
}

/// <summary>What "Apply to left/right" on the diff page hands the NFC tab: the read to edit (and where it came from), and
/// the bytes to type over its differing segments (and where those came from).</summary>
public sealed record DiffApply(NfcTagInfo Target, string TargetName, IReadOnlyDictionary<int, byte[]> Bytes, string SourceName);
