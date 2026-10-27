namespace TagBuddy.Services;

/// <summary>Turns a MIFARE Classic read over a Proxmark3 into the same <see cref="NfcTagInfo"/> a phone read builds,
/// so a Proxmark read shows in the normal result view and can be saved, edited and compared. A gen1a magic card is
/// read in full through the backdoor (every block, no keys); any other card gives only its 14443-A identity.</summary>
public static class ProxmarkClassic
{
    /// <summary>The block that starts sector <paramref name="sector"/> (4-block sectors up to 32, then 16-block).</summary>
    private static int FirstBlock(int sector) => sector < 32 ? sector * 4 : 128 + (sector - 32) * 16;
    private static int BlocksInSector(int sector) => sector < 32 ? 4 : 16;

    /// <summary>Builds the tag from a full (or partial) block dump. Blocks not read are shown as "not read".</summary>
    public static NfcTagInfo FromDump(byte[] uid, byte[] atqa, byte sak, IReadOnlyDictionary<int, byte[]> blocks, string source)
    {
        int blockCount = blocks.Keys.DefaultIfEmpty(0).Max() >= 64 ? 256 : 64;   // 4K vs 1K
        int sizeBytes = blockCount * 16;
        int sectors = blockCount <= 64 ? 16 : 40;
        string chip = MifareClassicKeys.ChipName(sizeBytes);

        var techs = new List<string> { CardTypes.ChipFromSak(sak) ?? chip, "ISO 14443-3A" };
        var details = Identity(atqa, sak, source);

        var lines = new List<string>();
        for (int s = 0; s < sectors; s++)
        {
            lines.Add($"Sector {s:D2} · read through the magic backdoor");
            int first = FirstBlock(s), count = BlocksInSector(s);
            for (int i = 0; i < count; i++)
            {
                int block = first + i;
                if (blocks.TryGetValue(block, out var data) && data.Length >= 16)
                {
                    lines.Add(MifareClassicKeys.FormatBlock(block, data));
                    if (i == count - 1) lines.AddRange(MifareClassicKeys.AccessLines(data));
                }
                else lines.Add($"  {block:D3}: not read");
            }
        }

        // Block 0's manufacturer parts, shown as details like a phone read does.
        if (blocks.TryGetValue(0, out var block0))
            details.AddRange(MifareClassicKeys.ManufacturerBlock(block0));

        // Classic uses the sector dump and the segment editor, not the Type 2 page-memory map, so no RawMemory.
        var data16 = blocks.Where(kv => kv.Value.Length >= 16).ToDictionary(kv => kv.Key, kv => kv.Value[..16]);
        var opened = Enumerable.Range(0, sectors).Where(s => blocks.ContainsKey(FirstBlock(s))).ToHashSet();
        var segments = new SegmentRead(new SegmentLayout(TagFamily.Classic, chip, 16, blockCount),
            data16, new Dictionary<int, string>()) { OpenedSectors = opened };

        return new NfcTagInfo(Id(uid), techs, details, [], lines, ChipName: chip, Segments: segments);
    }

    /// <summary>Just the card's identity (for a non-magic card that can't be dumped without its keys).</summary>
    public static NfcTagInfo IdentityOnly(byte[] uid, byte[] atqa, byte sak, string source) =>
        new(Id(uid), [CardTypes.ChipFromSak(sak) ?? "MIFARE Classic", "ISO 14443-3A"], Identity(atqa, sak, source), []);

    private static List<(string, string)> Identity(byte[] atqa, byte sak, string source) =>
    [
        ("Read with", source),
        ("ATQA", string.Concat(atqa.Select(b => b.ToString("X2")))),
        ("SAK", sak.ToString("X2")),
    ];

    private static string Id(byte[] uid) => string.Join(":", uid.Select(b => b.ToString("X2")));
}
