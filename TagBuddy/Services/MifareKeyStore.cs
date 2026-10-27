using System.Text.Json;

namespace TagBuddy.Services;

/// <summary>MIFARE Classic keys the app knows beyond its small built-in default set: a dictionary the person imports
/// (a .dic/.txt of 12-hex-digit keys, such as the one shipped with Proxmark tools), and the keys found to open each
/// card, remembered by the card's serial so the same card opens quickly next time. Every key is 12 uppercase hex
/// digits. Kept as plain files in the app's private data; a directory may be given for tests.
///
/// <para>Only reading is involved: a MIFARE Classic sector has no wrong-key lockout, so trying many keys never harms
/// a card. A sector with a genuinely custom (non-dictionary) key stays closed; only its real key opens it.</para></summary>
public sealed class MifareKeyStore
{
    private readonly string _importedPath;
    private readonly string _rememberedPath;

    private readonly string _usagePath;

    public MifareKeyStore(string? directory = null)
    {
        var dir = directory ?? FileSystem.AppDataDirectory;
        _importedPath = Path.Combine(dir, "mifare-imported-keys.txt");
        _rememberedPath = Path.Combine(dir, "mifare-remembered-keys.json");
        _usagePath = Path.Combine(dir, "mifare-key-usage.json");
    }

    /// <summary>The 12-digit uppercase hex keys in arbitrary text: one per line, '#' starts a comment, and spaces,
    /// ':' and '-' inside a key are ignored. A line that is not exactly 6 bytes is skipped. Order kept, duplicates
    /// removed.</summary>
    public static IReadOnlyList<string> ParseKeys(string? text)
    {
        var seen = new HashSet<string>();
        var keys = new List<string>();
        if (string.IsNullOrEmpty(text)) return keys;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var hex = new string(line.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (hex.Length == 12 && seen.Add(hex)) keys.Add(hex);
        }
        return keys;
    }

    internal static byte[] ToBytes(string hex12)
    {
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++) bytes[i] = Convert.ToByte(hex12.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string Serial(string uid) => new string(uid.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    // ---------------------------------------------------------------- imported dictionary

    /// <summary>Merges the keys found in <paramref name="text"/> into the imported dictionary and returns how many
    /// were new.</summary>
    public int Import(string text)
    {
        var current = ReadLines(_importedPath);
        var set = new HashSet<string>(current);
        int added = 0;
        foreach (var key in ParseKeys(text))
            if (set.Add(key)) { current.Add(key); added++; }
        if (added > 0) WriteLines(_importedPath, current);
        return added;
    }

    /// <summary>Every imported key as bytes, in the order they were added.</summary>
    public IReadOnlyList<byte[]> ImportedKeys() => ReadLines(_importedPath).Select(ToBytes).ToList();

    public int ImportedCount() => ReadLines(_importedPath).Count;

    public void ClearImported()
    {
        try { File.Delete(_importedPath); } catch { /* already gone */ }
    }

    // ---------------------------------------------------------------- keys remembered per card

    /// <summary>Notes that each of <paramref name="keys"/> (12-hex strings) opened a sector of the card with serial
    /// <paramref name="uid"/>, so later reads of that card try them first.</summary>
    public void Remember(string uid, IEnumerable<string> keys)
    {
        var serial = Serial(uid);
        if (serial.Length == 0) return;
        var all = LoadRemembered();
        if (!all.TryGetValue(serial, out var list)) all[serial] = list = [];
        bool changed = false;
        foreach (var key in keys.Select(k => k.ToUpperInvariant()).Where(k => k.Length == 12))
            if (!list.Contains(key)) { list.Add(key); changed = true; }
        if (changed) SaveRemembered(all);
    }

    /// <summary>A card that took a new serial (a magic card's block 0 rewritten) keeps its keys: the keys remembered
    /// for <paramref name="fromUid"/> are remembered for <paramref name="toUid"/> too. The old entry stays, so the
    /// card still opens quickly if its serial is put back.</summary>
    public void CarryOver(string fromUid, string toUid)
    {
        if (Serial(fromUid) == Serial(toUid)) return;
        var all = LoadRemembered();
        if (all.TryGetValue(Serial(fromUid), out var keys) && keys.Count > 0) Remember(toUid, keys);
    }

    /// <summary>The keys remembered for one card, as bytes.</summary>
    public IReadOnlyList<byte[]> RememberedFor(string uid) =>
        LoadRemembered().TryGetValue(Serial(uid), out var list) ? list.Select(ToBytes).ToList() : [];

    /// <summary>Every remembered key across all cards, deduplicated, as bytes. Small, so reads try them all quickly:
    /// a key found for one card often opens another from the same batch.</summary>
    public IReadOnlyList<byte[]> AllRemembered() =>
        LoadRemembered().Values.SelectMany(l => l).Distinct().Select(ToBytes).ToList();

    public int RememberedCardCount() => LoadRemembered().Count;

    public void ClearRemembered()
    {
        try { File.Delete(_rememberedPath); } catch { /* already gone */ }
        try { File.Delete(_usagePath); } catch { /* already gone */ }
    }

    // ---------------------------------------------------------------- keys used, with when (for the "keys used" list)

    /// <summary>Notes that each key (12-hex) was used just now — found on a read or written to a card — so the "keys
    /// used" list can show them newest first. A key already there just has its time moved to now.</summary>
    public void MarkUsed(IEnumerable<string> keys)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var all = LoadUsage();
        bool changed = false;
        foreach (var key in keys.Select(k => k.ToUpperInvariant()).Where(k => k.Length == 12))
        {
            all[key] = now;
            changed = true;
        }
        if (changed) SaveUsage(all);
    }

    /// <summary>Every key ever used, with the time it was last used, newest first.</summary>
    public IReadOnlyList<(string Hex, DateTimeOffset When)> UsedKeys() =>
        LoadUsage().Select(kv => (kv.Key, DateTimeOffset.FromUnixTimeSeconds(kv.Value)))
                   .OrderByDescending(x => x.Item2)
                   .ToList();

    private Dictionary<string, long> LoadUsage()
    {
        try
        {
            if (File.Exists(_usagePath))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_usagePath)) ?? [];
        }
        catch { /* unreadable: start fresh */ }
        return [];
    }

    private void SaveUsage(Dictionary<string, long> all) =>
        File.WriteAllText(_usagePath, JsonSerializer.Serialize(all));

    private Dictionary<string, List<string>> LoadRemembered()
    {
        try
        {
            if (File.Exists(_rememberedPath))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_rememberedPath)) ?? [];
        }
        catch { /* unreadable: start fresh */ }
        return [];
    }

    private void SaveRemembered(Dictionary<string, List<string>> all) =>
        File.WriteAllText(_rememberedPath, JsonSerializer.Serialize(all));

    private static List<string> ReadLines(string path)
    {
        try { return File.Exists(path) ? ParseKeys(File.ReadAllText(path)).ToList() : []; }
        catch { return []; }
    }

    private static void WriteLines(string path, IEnumerable<string> keys) => File.WriteAllLines(path, keys);
}
