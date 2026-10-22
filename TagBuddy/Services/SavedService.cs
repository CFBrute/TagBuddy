using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TagBuddy.Models;

namespace TagBuddy.Services;

/// <summary>Everything the person keeps: codes, text notes and tag dumps, in one list and one JSON file.
/// Dump bytes live as .bin files next to it. Older installs had a favorites file and a separate dump index;
/// both are folded in the first time this runs.</summary>
public class SavedService
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "saved.json");
    private readonly string _legacyFavorites = Path.Combine(FileSystem.AppDataDirectory, "favorites.json");
    private readonly string _dumpDir = Path.Combine(FileSystem.AppDataDirectory, "nfc-dumps");
    private string LegacyDumpIndex => Path.Combine(_dumpDir, "index.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _loaded;

    public ObservableCollection<SavedItem> Items { get; } = [];

    public IEnumerable<SavedItem> Dumps => Items.Where(i => i.IsDump);

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            Directory.CreateDirectory(_dumpDir);
            List<SavedItem> items;
            if (File.Exists(_path))
            {
                await using var stream = File.OpenRead(_path);
                items = await JsonSerializer.DeserializeAsync<List<SavedItem>>(stream) ?? [];
            }
            else
            {
                items = await MigrateAsync();
            }

            // A dump whose file is gone is useless; drop it quietly. The readable-text line is worked out
            // afresh each start, so improvements to the extraction reach dumps saved earlier.
            foreach (var item in items.Where(i => !i.IsDump || File.Exists(DumpPath(i))).OrderByDescending(i => i.CreatedAt))
            {
                if (item.IsDump)
                {
                    try { item.DumpText = DumpText.Describe(await File.ReadAllBytesAsync(DumpPath(item))); }
                    catch { /* keep what was stored */ }
                }
                Items.Add(item);
            }

            if (!File.Exists(_path) && Items.Count > 0) await SaveAsync();
            WarmPreviews();
        }
        catch
        {
            // A corrupt file should not prevent the app from starting.
        }
    }

    /// <summary>Loads the previews of saved links one after another in the background, so Saved is warm when it
    /// is opened without competing with the rows on screen (the request gate bounds it anyway).</summary>
    private void WarmPreviews()
    {
        if (!AppSettings.AutoPreviews) return;
        var values = Items.Where(i => i.IsLink).Select(i => i.Value).ToList();
        if (values.Count == 0) return;
        _ = Task.Run(async () =>
        {
            foreach (var value in values)
            {
                try
                {
                    if (!LinkPreviews.IsPreviewable(value, out var uri)) continue;
                    if (LinkPreviews.IsSingleUse(uri)) await Favicons.GetAsync(uri, null);
                    else await LinkPreviews.GetAsync(uri);
                }
                catch
                {
                    // One bad link must not stop the rest.
                }
            }
        });
    }

    /// <summary>Reads the old favorites file and the old dump index into one list. The old files are renamed,
    /// not deleted, so nothing is lost if this ever has to be repeated by hand.</summary>
    private async Task<List<SavedItem>> MigrateAsync()
    {
        var items = new List<SavedItem>();

        if (File.Exists(_legacyFavorites))
        {
            try
            {
                await using var stream = File.OpenRead(_legacyFavorites);
                items.AddRange(await JsonSerializer.DeserializeAsync<List<SavedItem>>(stream) ?? []);
            }
            catch { /* unreadable: leave it in place */ }
            TryRename(_legacyFavorites, _legacyFavorites + ".migrated");
        }

        if (File.Exists(LegacyDumpIndex))
        {
            try
            {
                await using var stream = File.OpenRead(LegacyDumpIndex);
                var dumps = await JsonSerializer.DeserializeAsync<List<LegacyDump>>(stream) ?? [];
                foreach (var d in dumps)
                {
                    var path = Path.Combine(_dumpDir, d.FileName);
                    if (!File.Exists(path)) continue;
                    string text = "";
                    try { text = DumpText.Describe(await File.ReadAllBytesAsync(path)); } catch { /* unreadable file */ }
                    items.Add(new SavedItem
                    {
                        Kind = SavedKind.Dump,
                        Name = d.Name,
                        DumpFile = d.FileName,
                        DumpSerial = d.Serial,
                        DumpChip = d.Chip,
                        DumpSize = d.SizeBytes,
                        DumpText = text,
                        CreatedAt = d.SavedAt,
                    });
                }
            }
            catch { /* unreadable: leave it in place */ }
            TryRename(LegacyDumpIndex, LegacyDumpIndex + ".migrated");
        }

        return items;
    }

    private sealed class LegacyDump
    {
        public string Name { get; set; } = "";
        public string Serial { get; set; } = "";
        public string Chip { get; set; } = "";
        public int SizeBytes { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.Now;
        public string FileName { get; set; } = "";
    }

    private static void TryRename(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- add / remove

    public async Task AddAsync(SavedItem item)
    {
        await LoadAsync();
        Items.Insert(0, item);
        await SaveAsync();
        LinkPreviews.Prefetch(item.Value);   // Saved then shows the preview and icon without waiting
    }

    public async Task<SavedItem> AddTextAsync(string name, string text)
    {
        var item = new SavedItem { Kind = SavedKind.Text, Name = name.Trim(), Value = text };
        await AddAsync(item);
        return item;
    }

    /// <summary>Stores a tag's memory as a .bin file and lists it.</summary>
    public async Task<SavedItem> AddDumpAsync(string name, string serial, string chip, byte[] data)
    {
        await LoadAsync();
        Directory.CreateDirectory(_dumpDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var item = new SavedItem
        {
            Kind = SavedKind.Dump,
            Name = name.Trim(),
            DumpSerial = serial,
            DumpChip = chip,
            DumpSize = data.Length,
            DumpText = DumpText.Describe(data),
            DumpFile = $"{Sanitize(chip)}_{serial.Replace(":", "")}_{stamp}.bin",
        };
        await File.WriteAllBytesAsync(DumpPath(item), data);
        Items.Insert(0, item);
        await SaveAsync();
        return item;
    }

    public Task<byte[]> ReadDumpAsync(SavedItem item) => File.ReadAllBytesAsync(DumpPath(item));

    public string DumpPath(SavedItem item) => Path.Combine(_dumpDir, item.DumpFile);

    public async Task RemoveAsync(SavedItem item)
    {
        await LoadAsync();
        Items.Remove(item);
        if (item.IsDump)
        {
            try { File.Delete(DumpPath(item)); } catch { /* already gone */ }
        }
        await SaveAsync();
    }

    /// <summary>Writes the list after an item was edited in place (renamed, text changed).</summary>
    public async Task UpdateAsync(SavedItem item)
    {
        await LoadAsync();
        // Re-insert so the list view notices the change.
        int index = Items.IndexOf(item);
        if (index >= 0)
        {
            Items.RemoveAt(index);
            Items.Insert(index, item);
        }
        await SaveAsync();
        LinkPreviews.Prefetch(item.Value);   // the text may now be a different link; a no-op when cached
    }

    public async Task SaveAsync()
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, Items.ToList(), JsonOptions);
    }

    public bool ContainsCode(string value, string format) =>
        Items.Any(i => i.IsCode && i.Value == value && i.Format == format);

    public bool ContainsText(string value) =>
        Items.Any(i => i.IsText && i.Value == value);

    /// <summary>Writes a dump to a temporary file so it can be shared or exported.</summary>
    public static async Task<string> ExportTempAsync(string fileName, byte[] data)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(path, data);
        return path;
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

/// <summary>Finds the obvious text in a tag dump: a decoded NDEF link or text when the tag is NDEF-formatted,
/// otherwise runs of printable ASCII. Amiibo data is encrypted and yields nothing, which is honest.</summary>
public static class DumpText
{
    public static string Describe(byte[] data)
    {
        var ndef = NfcReader.ContentFromMemory(data);
        if (!string.IsNullOrWhiteSpace(ndef)) return ndef.Trim();

        // Encrypted Amiibo content has no words in it, but a tag with the Amiibo page 3 may since have been
        // loaded with plain text, so look anyway and just demand longer runs there than random bytes produce.
        int minRun = NfcReader.IsAmiiboDump(data) ? 8 : 4;

        // Printable runs, skipping the serial/lock pages at the start, kept only when they look like words:
        // mostly letters and digits, with a few letters at least. Line breaks in the text separate runs.
        var runs = new List<string>();
        var current = new StringBuilder();
        int start = Math.Min(16, data.Length);
        for (int i = start; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;
            if (printable) { current.Append((char)data[i]); continue; }
            var run = current.ToString().Trim();
            if (LooksLikeWords(run, minRun)) runs.Add(run);
            current.Clear();
        }
        var text = string.Join("  ·  ", runs);
        return text.Length > 160 ? text[..157] + "…" : text;
    }

    /// <summary>A tag image holding <paramref name="text"/> as plain UTF-8 bytes from page 4 on, with pages
    /// 0-3 left zero, which the writer reads as "keep the tag's own serial and page 3". Padded to whole pages.</summary>
    public static byte[] FromText(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        int pages = (payload.Length + 3) / 4;
        var dump = new byte[16 + pages * 4];
        Array.Copy(payload, 0, dump, 16, payload.Length);
        return dump;
    }

    private static bool LooksLikeWords(string run, int minLength)
    {
        if (run.Length < minLength) return false;
        int letters = run.Count(char.IsLetter);
        int wordy = run.Count(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '/' or ':' or '@');
        return letters >= 3 && wordy * 10 >= run.Length * 9;
    }
}
