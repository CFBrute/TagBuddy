using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TagBuddy.Models;

namespace TagBuddy.Services;

/// <summary>A saved item as it travels in a share link: everything needed to save it again on another phone, and
/// nothing about this one (no group, no dates, no scanned picture). Short property names keep links short.</summary>
public sealed class SharedItem
{
    [JsonPropertyName("k")] public string Kind { get; set; } = "";
    [JsonPropertyName("n")] public string? Name { get; set; }

    // Codes and text
    [JsonPropertyName("t")] public string? Value { get; set; }

    // Codes
    [JsonPropertyName("f")] public string? Format { get; set; }
    [JsonPropertyName("qe")] public string? QrErrorCorrection { get; set; }
    [JsonPropertyName("qm")] public int? QrMargin { get; set; }
    [JsonPropertyName("c")] public string? Caption { get; set; }
    [JsonPropertyName("ca")] public string? CaptionAlign { get; set; }
    [JsonPropertyName("cf")] public string? CaptionFont { get; set; }
    [JsonPropertyName("cs")] public double? CaptionTextScale { get; set; }
    [JsonPropertyName("cg")] public double? CaptionGapScale { get; set; }
    [JsonPropertyName("cw")] public double? CaptionWidthScale { get; set; }

    // Tag dumps
    [JsonPropertyName("b")] public string? DumpBase64 { get; set; }
    [JsonPropertyName("ch")] public string? DumpChip { get; set; }
    [JsonPropertyName("s")] public string? DumpSerial { get; set; }
    /// <summary>The tag as it was read (a <see cref="TagReadFile"/> as JSON), when the dump was saved from a read
    /// and the link has room for it.</summary>
    [JsonPropertyName("r")] public string? ReadJson { get; set; }

    [JsonIgnore] public bool IsCode => Kind == ShareLink.KindCode;
    [JsonIgnore] public bool IsText => Kind == ShareLink.KindText;
    [JsonIgnore] public bool IsDump => Kind == ShareLink.KindDump;

    [JsonIgnore] public byte[] DumpBytes => DumpBase64 is { } b ? Convert.FromBase64String(b) : [];

    /// <summary>A code or text note as a new saved item (a new id, no group), ready for the save dialog.</summary>
    public SavedItem ToSavedItem()
    {
        var item = new SavedItem
        {
            Kind = IsText ? SavedKind.Text : SavedKind.Code,
            Name = Name ?? "",
            Value = Value ?? "",
        };
        if (!IsCode) return item;

        item.Format = Format!;
        if (QrErrorCorrection is { } level) item.QrErrorCorrection = level;
        if (QrMargin is { } margin) item.QrMargin = margin;
        item.Caption = Caption;
        if (CaptionAlign is { } align) item.CaptionAlign = align;
        if (CaptionFont is { } font) item.CaptionFont = font;
        if (CaptionTextScale is { } size) item.CaptionTextScale = size;
        if (CaptionGapScale is { } gap) item.CaptionGapScale = gap;
        if (CaptionWidthScale is { } width) item.CaptionWidthScale = width;
        return item;
    }
}

/// <summary>Share links: a saved item packed into a link that another Tag Buddy opens and offers to save.
///
/// <para>The link points at a small page on the app's website, with the item in the part after "#". Browsers
/// never send that part to the server, so the item goes only from phone to phone through whatever app it is sent
/// with. The page shows what the link holds, also to someone without the app, and its button hands the item to
/// Tag Buddy through a <c>tagbuddy://import?d=…</c> link that the app's intent filter catches.</para>
///
/// <para>The payload is "1." followed by the item as JSON, compressed with raw deflate and written in base64url,
/// so the page can unpack it with the browser's own DecompressionStream. A link from someone else is untrusted:
/// <see cref="Parse"/> checks every field and refuses anything that does not make a valid item.</para></summary>
public static class ShareLink
{
    public const string PageUrl = "https://cfbrute.github.io/TagBuddy/s/";
    public const string Scheme = "tagbuddy";
    public const string ImportHost = "import";

    public const string KindCode = "code", KindText = "text", KindDump = "dump";

    private const string VersionPrefix = "1.";

    /// <summary>A link longer than this is cut short or refused by some messaging apps. A dump's read details are
    /// left out when they would make the link longer; the memory itself always fits, see <see cref="MaxDumpBytes"/>.</summary>
    public const int MaxLinkLength = 30_000;

    /// <summary>The largest dump a link carries: a MIFARE Classic 4K and more, in well under <see cref="MaxLinkLength"/>.</summary>
    public const int MaxDumpBytes = 8 * 1024;

    // Limits for incoming links, so a crafted one cannot make the app unpack or store something huge.
    private const int MaxPayloadLength = 64 * 1024;
    private const int MaxUnpackedBytes = 256 * 1024;
    private const int MaxValueLength = 4000;
    private const int MaxNameLength = 60;
    private const int MaxCaptionLength = 500;
    private const int MaxChipLength = 40;
    private const int MaxSerialLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------------------------------------------------------- making a link

    /// <summary>The share link for a code or a text note.</summary>
    public static string Create(SavedItem item)
    {
        if (item.IsDump) throw new ArgumentException("A dump needs its memory: use CreateForDump.", nameof(item));
        var shared = new SharedItem
        {
            Kind = item.IsCode ? KindCode : KindText,
            Name = NullIfEmpty(item.Name),
            Value = item.Value,
        };
        if (item.IsCode)
        {
            shared.Format = item.Format;
            if (item.IsQr)
            {
                shared.QrErrorCorrection = item.QrErrorCorrection;
                shared.QrMargin = item.QrMargin;
            }
            // The look of the image only travels when it differs from a plain code, to keep links short.
            var plain = new SavedItem { Kind = SavedKind.Code, Value = item.Value, Format = item.Format };
            if (item.ImageDifferences(plain).Count > 0 || item.Caption is not null)
            {
                shared.Caption = item.Caption;
                shared.CaptionAlign = item.CaptionAlign;
                shared.CaptionFont = item.CaptionFont;
                shared.CaptionTextScale = item.CaptionTextScale;
                shared.CaptionGapScale = item.CaptionGapScale;
                shared.CaptionWidthScale = item.CaptionWidthScale;
            }
        }
        return Link(shared);
    }

    /// <summary>The share link for a tag dump, with the tag as it was read when <paramref name="readJson"/> is given
    /// and still fits (<paramref name="readLeftOut"/> says when it did not). Null when the memory itself is too big.</summary>
    public static string? CreateForDump(SavedItem item, byte[] memory, string? readJson, out bool readLeftOut)
    {
        readLeftOut = false;
        if (memory.Length == 0 || memory.Length > MaxDumpBytes) return null;
        var shared = new SharedItem
        {
            Kind = KindDump,
            Name = NullIfEmpty(item.Name),
            DumpBase64 = Convert.ToBase64String(memory),
            DumpChip = NullIfEmpty(item.DumpChip),
            DumpSerial = NullIfEmpty(item.DumpSerial),
            ReadJson = readJson,
        };
        var link = Link(shared);
        if (readJson is not null && link.Length > MaxLinkLength)
        {
            shared.ReadJson = null;
            readLeftOut = true;
            link = Link(shared);
        }
        return link.Length <= MaxLinkLength ? link : null;
    }

    private static string Link(SharedItem shared) => PageUrl + "#" + Encode(shared);

    /// <summary>The payload: "1." and the compressed JSON in base64url.</summary>
    internal static string Encode(SharedItem shared)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(shared, JsonOptions);
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);
        return VersionPrefix + Base64Url(packed.ToArray());
    }

    // ---------------------------------------------------------------- reading a link

    /// <summary>Whether <paramref name="uri"/> is meant for this app: a share-page link or a tagbuddy:// import link.
    /// Says nothing about whether the item in it is valid; <see cref="Parse"/> does that.</summary>
    public static bool IsShareLink(string? uri) =>
        uri is not null
        && (uri.StartsWith($"{Scheme}://{ImportHost}", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(PageUrl, StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith(PageUrl.TrimEnd('/') + "#", StringComparison.OrdinalIgnoreCase));

    /// <summary>The item in a share link (the page link with the item after "#", or tagbuddy://import?d=…), or null
    /// when the link holds no valid item: damaged, cut short, from a newer app, or made up.</summary>
    public static SharedItem? Parse(string? uri)
    {
        if (!IsShareLink(uri)) return null;
        string? payload = null;
        int hash = uri!.IndexOf('#');
        if (hash >= 0) payload = uri[(hash + 1)..];
        int query = uri.IndexOf("?d=", StringComparison.Ordinal) is var q and >= 0 ? q : uri.IndexOf("&d=", StringComparison.Ordinal);
        if (query >= 0 && (hash < 0 || query < hash))
        {
            int end = uri.IndexOfAny(['&', '#'], query + 3);
            payload = uri[(query + 3)..(end < 0 ? uri.Length : end)];
        }
        return payload is null ? null : Decode(Uri.UnescapeDataString(payload));
    }

    /// <summary>Unpacks and checks a payload; null for anything that is not a valid item.</summary>
    internal static SharedItem? Decode(string payload)
    {
        if (payload.Length > MaxPayloadLength || !payload.StartsWith(VersionPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var packed = FromBase64Url(payload[VersionPrefix.Length..]);
            if (packed is null) return null;
            using var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var json = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = deflate.Read(buffer)) > 0)
            {
                json.Write(buffer, 0, read);
                if (json.Length > MaxUnpackedBytes) return null;   // a "zip bomb"
            }
            var shared = JsonSerializer.Deserialize<SharedItem>(json.ToArray(), JsonOptions);
            return shared is not null && Validate(shared) ? shared : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Checks a received item field by field, clamping the look of a code into the ranges the app's own
    /// controls allow and dropping a read that does not parse. False when it cannot become a valid saved item.</summary>
    private static bool Validate(SharedItem s)
    {
        if (s.Name is { Length: > MaxNameLength }) s.Name = s.Name[..MaxNameLength];
        s.Name = s.Name?.Trim();

        switch (s.Kind)
        {
            case KindText:
                return s.Value is { Length: > 0 and <= MaxValueLength } && s.Value.Trim().Length > 0;

            case KindCode:
                if (s.Value is not { Length: > 0 and <= MaxValueLength }) return false;
                if (s.Format is null || !BarcodeTypes.All.Any(f => f.Key == s.Format)) return false;
                if (BarcodeTypes.Validate(s.Format, s.Value) is not null) return false;   // the code could not be drawn
                if (s.QrErrorCorrection is not (null or "L" or "M" or "Q" or "H")) return false;
                if (s.QrMargin is { } margin) s.QrMargin = Math.Clamp(margin, 0, 4);
                if (s.Caption is { Length: > MaxCaptionLength }) return false;
                if (s.CaptionAlign is not (null or "Left" or "Center" or "Right")) s.CaptionAlign = "Center";
                if (s.CaptionFont is { } font) s.CaptionFont = CaptionFonts.Get(font).Key;
                s.CaptionTextScale = Clamp(s.CaptionTextScale, SavedItem.MinCaptionTextScale, SavedItem.MaxCaptionTextScale);
                s.CaptionGapScale = Clamp(s.CaptionGapScale, SavedItem.MinCaptionGapScale, SavedItem.MaxCaptionGapScale);
                s.CaptionWidthScale = Clamp(s.CaptionWidthScale, SavedItem.MinCaptionWidthScale, SavedItem.MaxCaptionWidthScale);
                return true;

            case KindDump:
                if (s.DumpBase64 is null) return false;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(s.DumpBase64); } catch { return false; }
                if (bytes.Length is 0 or > MaxDumpBytes) return false;
                if (s.DumpChip is { Length: > MaxChipLength }) return false;
                if (s.DumpSerial is { } serial && (serial.Length > MaxSerialLength || serial.Any(c => !(Uri.IsHexDigit(c) || c == ':'))))
                    return false;
                // The read details are a bonus: one that does not parse is dropped and the memory still saved.
                if (s.ReadJson is not null && TagReadFile.FromJson(s.ReadJson) is null) s.ReadJson = null;
                return true;

            default:
                return false;   // a kind from a newer app
        }
    }

    // ---------------------------------------------------------------- helpers

    private static double? Clamp(double? value, double min, double max) =>
        value is { } v ? (double.IsFinite(v) ? Math.Clamp(v, min, max) : null) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        // A length of 1 more than a multiple of 4 cannot come from base64.
        if (text.Length % 4 == 1 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
    }
}

/// <summary>A share link the app was opened with, waiting for the shell to show its save dialog. Android delivers
/// the link to the activity (perhaps before the shell exists); the shell takes it when it can show a dialog.</summary>
public static class IncomingShare
{
    private static readonly object Gate = new();
    private static SharedItem? _item;
    private static bool _damaged;

    /// <summary>A link arrived. Raised on the thread that delivered it.</summary>
    public static event Action? Arrived;

    public static bool HasPending { get { lock (Gate) return _item is not null || _damaged; } }

    /// <summary>Takes a link the app was opened with. False when it is not a share link at all (the intent was for
    /// something else); a share link that holds no valid item is kept as "damaged", so the person is told.</summary>
    public static bool Offer(string? uri)
    {
        if (!ShareLink.IsShareLink(uri)) return false;
        var item = ShareLink.Parse(uri);
        lock (Gate)
        {
            _item = item;
            _damaged = item is null;
        }
        Arrived?.Invoke();
        return true;
    }

    /// <summary>The waiting item, once: (item, false), (null, true) for a damaged link, or (null, false) for none.</summary>
    public static (SharedItem? Item, bool Damaged) Take()
    {
        lock (Gate)
        {
            var result = (_item, _damaged);
            _item = null;
            _damaged = false;
            return result;
        }
    }
}
