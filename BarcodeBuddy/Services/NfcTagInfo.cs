namespace BarcodeBuddy.Services;

/// <summary>One NDEF record on a tag, already decoded to text where possible.</summary>
public sealed record NfcRecordInfo(string Kind, string Text);

/// <summary>Everything the app learned about a tag that was held to the phone.</summary>
public sealed record NfcTagInfo(
    string Id,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<(string Label, string Value)> Details,
    IReadOnlyList<NfcRecordInfo> Records)
{
    /// <summary>The first link or text record: what a QR code with the same purpose would contain.</summary>
    public string? Content =>
        Records.FirstOrDefault(r => r.Kind.StartsWith("Link"))?.Text
        ?? Records.FirstOrDefault(r => r.Kind.StartsWith("Text"))?.Text
        ?? Records.FirstOrDefault(r => r.Text.Length > 0)?.Text;

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
}
