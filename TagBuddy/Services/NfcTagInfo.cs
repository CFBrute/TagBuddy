namespace TagBuddy.Services;

/// <summary>One NDEF record on a tag, already decoded to text where possible. <paramref name="Sealed"/> holds the
/// encrypted bytes of a private-text record (<see cref="SealedText"/>), which only the passphrase opens.</summary>
public sealed record NfcRecordInfo(string Kind, string Text, byte[]? Sealed = null)
{
    public bool IsSealed => Sealed is not null;
}

/// <summary>Everything the app learned about a tag that was held to the phone.</summary>
public sealed record NfcTagInfo(
    string Id,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<(string Label, string Value)> Details,
    IReadOnlyList<NfcRecordInfo> Records,
    IReadOnlyList<string>? MemoryDump = null,
    byte[]? RawMemory = null,
    string? ChipName = null)
{
    /// <summary>The first link or text record: what a QR code with the same purpose would contain.</summary>
    public string? Content
    {
        get
        {
            var open = Records.Where(r => !r.IsSealed).ToList();
            return open.FirstOrDefault(r => r.Kind.StartsWith("Link"))?.Text
                ?? open.FirstOrDefault(r => r.Kind.StartsWith("Text"))?.Text
                ?? open.FirstOrDefault(r => r.Text.Length > 0)?.Text;
        }
    }

    /// <summary>A private-text record, if the tag has one.</summary>
    public NfcRecordInfo? SealedRecord => Records.FirstOrDefault(r => r.IsSealed);

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public bool HasMemoryDump => MemoryDump is { Count: > 0 };

    /// <summary>True when the full page memory was read and can be saved as a binary file.</summary>
    public bool HasRawMemory => RawMemory is { Length: > 0 };

    /// <summary>Serial number without separators, safe for a file name.</summary>
    public string SerialForFileName => Id.Replace(":", "");
}
