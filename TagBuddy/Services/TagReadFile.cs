using System.Text.Json;
using System.Text.Json.Serialization;

namespace TagBuddy.Services;

/// <summary>A tag read as a file, so Saved can keep a tag exactly as it was read (every detail line, the records, the
/// memory, and what the segment editor needs: the layout, each segment's bytes, which were closed, which Classic
/// sectors opened) and show it again or edit it later. Plain JSON in the app's data folder next to the dump files.
/// Private text stays encrypted: a sealed record is kept as its bytes, opened only with the passphrase.</summary>
public sealed class TagReadFile
{
    public string Id { get; set; } = "";
    public List<string> Technologies { get; set; } = [];
    public List<Detail> Details { get; set; } = [];
    public List<Record> Records { get; set; } = [];
    public List<string>? MemoryDump { get; set; }
    public byte[]? RawMemory { get; set; }
    public string? ChipName { get; set; }
    public bool NeedsPassword { get; set; }
    public bool NeedsClassicKey { get; set; }
    public Segments? Layout { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.Now;

    public sealed class Detail { public string Label { get; set; } = ""; public string Value { get; set; } = ""; }
    public sealed class Record { public string Kind { get; set; } = ""; public string Text { get; set; } = ""; public byte[]? Sealed { get; set; } }

    public sealed class Segments
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public TagFamily Family { get; set; }
        public string ChipName { get; set; } = "";
        public int SegmentSize { get; set; }
        public int Count { get; set; }
        public int UserEnd { get; set; }
        public Dictionary<int, byte[]> Data { get; set; } = [];
        public Dictionary<int, string> Blocked { get; set; } = [];
        public List<int> OpenedSectors { get; set; } = [];
        public Dictionary<int, SectorKey> SectorKeys { get; set; } = [];
    }

    /// <summary>The key that opened a sector, kept so a saved MIFARE Classic tag shows the real key in its trailer (the
    /// chip returns 00 for keys on a read) and so the key list for the tag survives.</summary>
    public sealed class SectorKey { public byte[] Key { get; set; } = []; public bool KeyB { get; set; } }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static TagReadFile From(NfcTagInfo tag) => new()
    {
        Id = tag.Id,
        Technologies = [.. tag.Technologies],
        Details = tag.Details.Select(d => new Detail { Label = d.Label, Value = d.Value }).ToList(),
        Records = tag.Records.Select(r => new Record { Kind = r.Kind, Text = r.Text, Sealed = r.Sealed }).ToList(),
        MemoryDump = tag.MemoryDump?.ToList(),
        RawMemory = tag.RawMemory,
        ChipName = tag.ChipName,
        NeedsPassword = tag.NeedsPassword,
        NeedsClassicKey = tag.NeedsClassicKey,
        Layout = tag.Segments is { } s ? new Segments
        {
            Family = s.Layout.Family,
            ChipName = s.Layout.ChipName,
            SegmentSize = s.Layout.SegmentSize,
            Count = s.Layout.Count,
            UserEnd = s.Layout.UserEnd,
            Data = s.Data.ToDictionary(p => p.Key, p => p.Value),
            Blocked = s.Blocked.ToDictionary(p => p.Key, p => p.Value),
            OpenedSectors = s.OpenedSectors.Order().ToList(),
            SectorKeys = s.SectorKeys.ToDictionary(p => p.Key, p => new SectorKey { Key = p.Value.Key, KeyB = p.Value.KeyB }),
        } : null,
    };

    public NfcTagInfo ToTag() => new(Id, Technologies,
        Details.Select(d => (d.Label, d.Value)).ToList(),
        Records.Select(r => new NfcRecordInfo(r.Kind, r.Text, r.Sealed)).ToList(),
        MemoryDump, RawMemory, ChipName, NeedsPassword, NeedsClassicKey,
        Layout is { } l ? new SegmentRead(new SegmentLayout(l.Family, l.ChipName, l.SegmentSize, l.Count, l.UserEnd), l.Data, l.Blocked)
            {
                OpenedSectors = new HashSet<int>(l.OpenedSectors),
                SectorKeys = l.SectorKeys.ToDictionary(p => p.Key, p => (p.Value.Key, p.Value.KeyB)),
            } : null);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static TagReadFile? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TagReadFile>(json, Options); }
        catch { return null; }
    }
}
