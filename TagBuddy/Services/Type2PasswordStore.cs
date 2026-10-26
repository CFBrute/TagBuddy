using System.Text.Json;

namespace TagBuddy.Services;

/// <summary>Passwords the app has learned open password-protected Type 2 tags (NTAG21x, Ultralight EV1), remembered by
/// the tag's serial so the same tag opens on its own next time, the way <see cref="MifareKeyStore"/> remembers MIFARE
/// Classic keys. A password is 4 bytes, kept as 8 uppercase hex digits. Plain files in the app's private data; a
/// directory may be given for tests.
///
/// <para>A password belongs to one tag: it is remembered under that tag's serial and only ever tried on that same tag,
/// never on another held to the phone, because a wrong attempt can count against a tag's wrong-attempt limit and lock
/// it for good. Only a password already seen to open the tag is stored, so trying it again authenticates rather than
/// spends an attempt. The Amiibo key is never stored: it is a formula of the serial the app always re-derives.</para></summary>
public sealed class Type2PasswordStore
{
    private readonly string _path;

    public Type2PasswordStore(string? directory = null) =>
        _path = Path.Combine(directory ?? FileSystem.AppDataDirectory, "type2-passwords.json");

    private static string Serial(string uid) => new string(uid.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static string Hex(byte[] password) => string.Concat(password.Select(b => b.ToString("X2")));

    /// <summary>Notes that <paramref name="password"/> (4 bytes) opens the tag with serial <paramref name="uid"/>, so
    /// later reads and writes of that tag try it. A tag has one password; a new one replaces what was stored.</summary>
    public void Remember(string uid, byte[] password)
    {
        if (password is not { Length: 4 }) return;
        var serial = Serial(uid);
        if (serial.Length == 0) return;
        var all = Load();
        if (all.TryGetValue(serial, out var existing) && existing == Hex(password)) return;
        all[serial] = Hex(password);
        Save(all);
    }

    /// <summary>The password remembered for a tag, as bytes, or null when none is.</summary>
    public byte[]? For(string uid) =>
        Load().TryGetValue(Serial(uid), out var hex) && hex.Length == 8 && TagPassword.TryParse(hex, out var pwd) ? pwd : null;

    /// <summary>Every remembered password keyed by serial, as bytes, for arming a read's password search.</summary>
    public IReadOnlyDictionary<string, byte[]> All() =>
        Load().Where(p => p.Value.Length == 8 && TagPassword.TryParse(p.Value, out _))
              .ToDictionary(p => p.Key, p => TagPassword.TryParse(p.Value, out var pwd) ? pwd : [], StringComparer.OrdinalIgnoreCase);

    public int CardCount() => Load().Count;

    public void Forget(string uid)
    {
        var all = Load();
        if (all.Remove(Serial(uid))) Save(all);
    }

    public void Clear()
    {
        try { File.Delete(_path); } catch { /* already gone */ }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
        }
        catch { /* unreadable: start fresh */ }
        return [];
    }

    private void Save(Dictionary<string, string> all)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(all)); } catch { /* best effort */ }
    }
}
