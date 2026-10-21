using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BarcodeBuddy.Services;

/// <summary>A saved binary copy of a tag's page memory (NTAG, Ultralight, Amiibo and the like).</summary>
public sealed class NfcDump
{
    public string Name { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Chip { get; set; } = "";
    public int SizeBytes { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string FileName { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Chip} {Serial}".Trim() : Name;
    public string Summary => $"{SizeBytes} bytes ({SizeBytes / 4} pages), tag {Serial}, {SavedAt:yyyy-MM-dd HH:mm}";
}

/// <summary>Keeps tag dumps as .bin files inside the app's data folder, with a small JSON index next to them.</summary>
public sealed class NfcDumpStore
{
    private readonly string _dir = Path.Combine(FileSystem.AppDataDirectory, "nfc-dumps");
    private string IndexPath => Path.Combine(_dir, "index.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _loaded;

    public ObservableCollection<NfcDump> Items { get; } = [];

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Directory.CreateDirectory(_dir);
            if (!File.Exists(IndexPath)) return;
            await using var stream = File.OpenRead(IndexPath);
            var items = await JsonSerializer.DeserializeAsync<List<NfcDump>>(stream) ?? [];
            foreach (var item in items.OrderByDescending(i => i.SavedAt))
                if (File.Exists(PathOf(item))) Items.Add(item);
        }
        catch
        {
            // A damaged index must not stop the app; the .bin files are still on disk.
        }
    }

    public async Task<NfcDump> SaveAsync(string name, string serial, string chip, byte[] data)
    {
        await LoadAsync();
        Directory.CreateDirectory(_dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dump = new NfcDump
        {
            Name = name.Trim(),
            Serial = serial,
            Chip = chip,
            SizeBytes = data.Length,
            SavedAt = DateTime.Now,
            FileName = $"{Sanitize(chip)}_{serial.Replace(":", "")}_{stamp}.bin",
        };
        await File.WriteAllBytesAsync(PathOf(dump), data);
        Items.Insert(0, dump);
        await SaveIndexAsync();
        return dump;
    }

    public Task<byte[]> ReadAsync(NfcDump dump) => File.ReadAllBytesAsync(PathOf(dump));

    public async Task RemoveAsync(NfcDump dump)
    {
        await LoadAsync();
        Items.Remove(dump);
        try { File.Delete(PathOf(dump)); } catch { /* already gone */ }
        await SaveIndexAsync();
    }

    public string PathOf(NfcDump dump) => Path.Combine(_dir, dump.FileName);

    /// <summary>Writes a dump to a temporary file so it can be shared or exported.</summary>
    public async Task<string> ExportTempAsync(string fileName, byte[] data)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    private async Task SaveIndexAsync()
    {
        await using var stream = File.Create(IndexPath);
        await JsonSerializer.SerializeAsync(stream, Items.ToList(), JsonOptions);
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c);
        return sb.Length == 0 ? "tag" : sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------
    // Hex text: the format shown on the NFC page ("004: 03:00:FE:00  ....") and plain hex ("0300FE00").
    // ------------------------------------------------------------------------------------------------

    /// <summary>Parses pasted hex into bytes. Accepts the page listing format of this app, plain hex,
    /// and hex with spaces, colons or dashes between bytes. Returns null when nothing sensible is found.</summary>
    public static byte[]? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bytes = new List<byte>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // "004: AA:BB:CC:DD  ....": drop the page prefix (three digits, colon, space) and the ASCII column.
            // A line such as "04:1F:2A:3B" is plain colon-separated hex and keeps its first byte.
            if (line.Length > 5 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) && line[3] == ':' && line[4] == ' ')
                line = line[5..];
            int gap = line.IndexOf("  ", StringComparison.Ordinal);
            if (gap > 0) line = line[..gap];

            var hex = new string(line.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length % 2 != 0) return null;
            for (int i = 0; i < hex.Length; i += 2)
                bytes.Add(byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber));
        }
        return bytes.Count > 0 ? bytes.ToArray() : null;
    }
}
