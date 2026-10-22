using TagBuddy.Models;
using ZXing.Net.Maui;

namespace TagBuddy.Services;

/// <summary>A decoded code, whether it came from the camera view or from a picture.</summary>
public sealed record ScanResult(
    string Value,
    BarcodeFormat Format,
    string FormatName,
    byte[]? Raw,
    IReadOnlyDictionary<string, object> Metadata)
{
    public FormatInfo? KnownFormat => BarcodeTypes.FromZxing(Format);
    public string FormatLabel => KnownFormat?.Label ?? FormatName;

    public string? ErrorCorrectionLevel =>
        Metadata.TryGetValue("errorcorrectionlevel", out var v) ? v?.ToString() : null;

    public static ScanResult From(BarcodeResult result)
    {
        var metadata = new Dictionary<string, object>();
        if (result.Metadata is not null)
            foreach (var (key, value) in result.Metadata)
                if (value is not null) metadata[Normalize(key.ToString())] = value;

        return new ScanResult(result.Value, result.Format, result.Format.ToString(), result.Raw, metadata);
    }

    public static ScanResult From(ZXing.Result result)
    {
        var metadata = new Dictionary<string, object>();
        if (result.ResultMetadata is not null)
            foreach (var (key, value) in result.ResultMetadata)
                if (value is not null) metadata[Normalize(key.ToString())] = value;

        // Map the core format to the MAUI enum: first via the app's own table, then by name.
        var known = BarcodeTypes.All.FirstOrDefault(f => f.CoreFormat == result.BarcodeFormat);
        var format = known?.ZxingFormat
            ?? (Enum.TryParse<BarcodeFormat>(Normalize(result.BarcodeFormat.ToString()), ignoreCase: true, out var parsed) ? parsed : default);

        return new ScanResult(result.Text, format, PrettyName(result.BarcodeFormat.ToString()), result.RawBytes, metadata);
    }

    /// <summary>ERROR_CORRECTION_LEVEL and ErrorCorrectionLevel both become "errorcorrectionlevel".</summary>
    public static string Normalize(string key) => key.Replace("_", "").ToLowerInvariant();

    private static string PrettyName(string coreName) =>
        string.Join(' ', coreName.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length <= 3 ? p : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
}
