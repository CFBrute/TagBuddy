using System.Text;

#if ANDROID
using Android.Nfc;
using Android.Nfc.Tech;
#endif

using static TagBuddy.Services.Type2Memory;

namespace TagBuddy.Services;

/// <summary>Reads NFC tags (13.56 MHz, the kind a phone reacts to) while a page is in the foreground.</summary>
///
/// <remarks>
/// <para><b>Type 2 tag memory (NTAG213/215/216, Ultralight EV1), which the write routines below depend on.</b>
/// Memory is 4-byte pages. Pages 0-1 and the first byte of page 2 hold the 7-byte serial (UID), burned at the
/// factory and never writable. Page 2 bytes 2-3 are the <i>static lock bits</i>. Page 3 is the <i>capability
/// container</i> (CC). Page 4 up to <c>UserEnd</c> is user memory. After it come the <i>dynamic lock bits</i>,
/// then the configuration pages CFG0 (holds AUTH0, the first password-protected page), CFG1 (AUTHLIM, the
/// wrong-password limit, and PROT), PWD and PACK.</para>
///
/// <para><b>Locks are one-time and never set by writing data.</b> Each lock bit starts clear and can be set
/// once; it can never be cleared, and the pages it covers are read-only for the life of the tag. Writing a
/// page's data does nothing to any lock bit, so a tag rewritten a hundred times with text stays unlocked.
/// This app never sets a lock bit: the dump writer skips page 2 and the dynamic lock page on purpose. Lock
/// bits only appear because another writer put them there, typically an Amiibo tool copying Nintendo's
/// image, whose lock bytes (0F E0 static, 01 dynamic) lock pages 3, 13-15 and 16-31. <see cref="LockedPages"/>
/// decodes both sets, and the writers leave locked pages alone and say so in their result.</para>
///
/// <para><b>Page 3 is one-time on its own.</b> The chip treats the CC as OTP: bits can be set, never cleared.
/// A blank tag has 00 00 00 00; NDEF formatting writes E1 10 xx 00 and an Amiibo image writes F1 10 FF EE.
/// Once a tag has had the Amiibo value, no phone will ever read it as a text or link tag again, so a tag's
/// first write decides what it is for. The raw-bytes writer exists for exactly that case: it stores text in
/// the user pages with no NDEF wrapper, which this app reads back but phones ignore.</para>
///
/// <para><b>Passwords.</b> An Amiibo tag is password-protected (AUTH0 = 4) with a key that is a fixed formula
/// of its serial (<see cref="AmiiboPassword"/>), so the app can always open one. Other protected tags are left
/// alone: a wrong guess counts against AUTHLIM, after which the tag locks for good. A failed handshake looks
/// the same for a wrong key and for a tag that moved during it, which is why it is retried once.</para>
/// </remarks>
public sealed partial class NfcReader
{
    public static NfcReader Instance { get; } = new();

    public event Action<NfcTagInfo>? TagRead;

    /// <summary>Success flag and a message, raised after an attempt to write a tag held to the phone.</summary>
    public event Action<bool, string>? TagWritten;

    /// <summary>Ends the message of a text write refused because the tag can never hold NDEF; the NFC tab looks for
    /// it to offer writing raw bytes instead.</summary>
    public const string RawBytesTip = "\"Raw bytes (no NDEF)\" can still store text on it for this app.";

    /// <summary>The 4-byte password the person entered, and the serial of the tag they entered it for. A password
    /// belongs to one tag: it is tried only on that tag, never on a different one held to the phone, because a
    /// wrong attempt can count against the new tag's wrong-attempt limit.</summary>
    private static byte[]? _manualPassword;
    private static string? _manualPasswordSerial;

    /// <summary>Sets (or clears, with null) the password a read or write uses, bound to the tag <paramref
    /// name="serial"/> it is for.</summary>
    public void SetManualPassword(byte[]? password, string? serial)
    {
        _manualPassword = password is { Length: 4 } ? password : null;
        _manualPasswordSerial = _manualPassword is null ? null : serial;
    }

    /// <summary>The entered password, but only when it was entered for the tag with this serial.</summary>
    private static byte[]? ManualPasswordFor(string serial) =>
        _manualPassword is not null && string.Equals(serial, _manualPasswordSerial, StringComparison.OrdinalIgnoreCase)
            ? _manualPassword : null;

    /// <summary>Passwords the app has seen open protected Type 2 tags, by tag serial, so a tag opens on its own next
    /// time (<see cref="Type2PasswordStore"/>). A remembered password is tried only on its own tag, like a typed one;
    /// because it already opened that tag, trying it again authenticates rather than spending a wrong attempt.</summary>
    private static IReadOnlyDictionary<string, byte[]> _rememberedPasswords = new Dictionary<string, byte[]>();

    /// <summary>Sets the remembered Type 2 passwords a read and write may try, keyed by tag serial.</summary>
    public void SetPasswordSearch(IReadOnlyDictionary<string, byte[]> remembered) =>
        _rememberedPasswords = remembered ?? new Dictionary<string, byte[]>();

    /// <summary>The remembered password for a tag. The store keys serials as bare hex ("04112233"), while the reader
    /// formats them with colons ("04:11:22:33"), so the serial is reduced to its hex digits before the lookup.</summary>
    private static byte[]? RememberedPasswordFor(string serial)
    {
        var key = new string(serial.Where(Uri.IsHexDigit).ToArray());
        return _rememberedPasswords.TryGetValue(key, out var pwd) && pwd is { Length: 4 } ? pwd : null;
    }

    /// <summary>The password that opened the last Type 2 tag read or written, with that tag's serial, when it is one
    /// worth remembering (the person's typed password, a remembered one, or the factory default — never the Amiibo
    /// key, which is re-derived). The page stores it so the tag opens on its own next time. Reset at each read/write.</summary>
    public static (string Serial, byte[] Password)? Type2PasswordFound { get; private set; }

    private static void RecordType2Password(string serial, byte[] password, bool amiibo)
    {
        if (!amiibo && password is { Length: 4 } && serial.Length > 0)
            Type2PasswordFound = (serial, password);
    }

    /// <summary>What this run has learned about the factory default password per tag serial: that it opened the
    /// tag, or that it did not. A tag whose default already failed is not tried with it again, so the counter is
    /// not ticked a second time; a tag read as default can then be written with it.</summary>
    private static readonly HashSet<string> _defaultWorks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _defaultFailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Armed by the "try the default on the next read" button: one forced factory-default attempt even
    /// when the setting is off and even if it failed before. The read path still skips it unless the tag confirms
    /// no wrong-attempt lock-out. Cleared after the read that uses it.</summary>
    private static bool _tryDefaultOnce;

    /// <summary>Arms (or clears) a single factory-default attempt on the next read, regardless of the setting.</summary>
    public void SetTryDefaultOnce(bool on) => _tryDefaultOnce = on;

    /// <summary>Whether to try the factory default on this tag: it is known to open it, the one-shot is armed, or
    /// the setting is on and it is not already known to fail on this tag.</summary>
    private static bool ShouldTryDefault(string serial) =>
        _defaultWorks.Contains(serial) || _tryDefaultOnce
        || (AppSettings.AutoTryDefaultPassword && !_defaultFailed.Contains(serial));

    private static void RecordDefault(string serial, bool worked)
    {
        if (worked) { _defaultWorks.Add(serial); _defaultFailed.Remove(serial); }
        else { _defaultFailed.Add(serial); _defaultWorks.Remove(serial); }
    }

    /// <summary>Whether this run already found the factory default does not open this tag.</summary>
    private static bool DefaultKnownFailed(string serial) => _defaultFailed.Contains(serial);

    /// <summary>Whether this run already found the factory default opens this tag (e.g. on an earlier read).</summary>
    private static bool DefaultKnownWorks(string serial) => _defaultWorks.Contains(serial);

    /// <summary>The 6-byte MIFARE Classic key the person entered, tried before the public defaults, or null. A
    /// Classic sector has no wrong-key lockout, so trying one does no harm.</summary>
    private static byte[]? _manualKey;

    /// <summary>Sets (or clears, with null) the MIFARE Classic key the next read tries before the default keys.</summary>
    public void SetManualClassicKey(byte[]? key) => _manualKey = key is { Length: 6 } ? key : null;

    /// <summary>Keys found to open this or other cards, tried right after the built-in defaults on every read.</summary>
    private static IReadOnlyList<byte[]> _rememberedClassicKeys = [];

    /// <summary>A large imported dictionary, tried only when <see cref="_tryAllClassicKeys"/> is set.</summary>
    private static IReadOnlyList<byte[]> _dictionaryClassicKeys = [];

    /// <summary>Whether the next read runs the whole imported dictionary against locked sectors (a few thousand
    /// safe attempts, a few seconds). One read's choice; the page turns it off again afterwards.</summary>
    private static bool _tryAllClassicKeys;

    /// <summary>Which keys the last read found to open sectors: (sector, 12-hex key). The page remembers these for
    /// the card so it opens quickly next time.</summary>
    public static IReadOnlyList<(int Sector, string Key)> ClassicKeysFound { get; private set; } = [];

    /// <summary>Clears the keys from the last read, so a read from another source (the Proxmark, over USB) is not
    /// credited with the phone reader's stale keys when it is shown.</summary>
    public static void ClearClassicKeysFound() => ClassicKeysFound = [];

    /// <summary>Sets the extra MIFARE Classic keys a read tries beyond the built-in defaults: <paramref name="remembered"/>
    /// always (small), and <paramref name="dictionary"/> only when <paramref name="tryAll"/> is set (large).</summary>
    public void SetClassicKeySearch(IReadOnlyList<byte[]> remembered, IReadOnlyList<byte[]> dictionary, bool tryAll)
    {
        _rememberedClassicKeys = remembered;
        _dictionaryClassicKeys = dictionary;
        _tryAllClassicKeys = tryAll;
    }

    /// <summary>A Type 2 dump with the Nintendo capability container and NTAG215-sized content.</summary>
    public static bool IsAmiiboDump(byte[] bytes) => Type2Memory.IsAmiiboDump(bytes);

    /// <summary>Formats page memory as "page: hex  ascii" lines, one per 4-byte page.</summary>
    public static List<string>? FormatDump(byte[] bytes) => Type2Memory.FormatDump(bytes);

#if ANDROID
    private ReaderCallback? _callback;

    /// <summary>When set, the next tag held to the phone is handed to this operation instead of being read.</summary>
    private Func<Tag, (bool Ok, string Note)>? _pendingWrite;

    /// <summary>Arms writing an NDEF message (link, text, vCard) to the next tag; null disarms.</summary>
    public void ArmWrite(NdefMessage? message) =>
        _pendingWrite = message is null ? null : tag => WriteNdef(tag, message);

    /// <summary>Arms writing a raw page dump (Amiibo, NTAG backup) to the next Type 2 tag.
    /// <paramref name="dumpSerial"/> is the serial the dump was taken from, used only for the result note.</summary>
    public void ArmRawWrite(byte[] dump, string dumpSerial) =>
        _pendingWrite = tag => WriteRaw(tag, dump, dumpSerial);

    /// <summary>Arms writing bytes straight into the next Type 2 tag's user pages, with no NDEF wrapper: the
    /// rest of the user area is zeroed, the serial and page 3 are left as they are. Phones will not read such
    /// a tag as text, but this app shows the bytes as readable text, and it works on tags whose page 3 was
    /// burned for Amiibo use, where NDEF never can.</summary>
    public void ArmRawText(byte[] payload) =>
        _pendingWrite = tag => WriteRaw(tag, (before, chip) => PlaceRawText(payload, before, chip), "");

    /// <summary>Arms erasing the next tag: user pages are zeroed, an empty NDEF message is left, and any
    /// password protection this app can open is removed.</summary>
    public void ArmErase() => _pendingWrite = EraseTag;

    public void Disarm() => _pendingWrite = null;
    public bool IsArmedToWrite => _pendingWrite is not null;

    private static NfcAdapter? Adapter =>
        NfcAdapter.GetDefaultAdapter(Platform.AppContext);

    public bool IsSupported => Adapter is not null;
    public bool IsEnabled => Adapter?.IsEnabled == true;

    public void Start()
    {
        var adapter = Adapter;
        var activity = Platform.CurrentActivity;
        if (adapter is null || activity is null || _callback is not null) return;

        _callback = new ReaderCallback(this);
        var extras = new Android.OS.Bundle();
        extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 250);

        // SkipNdefCheck: Android's own NDEF probe makes payment and access cards drop before the app
        // ever sees them. The app reads NDEF itself instead (Type 2 and Type 4 tags).
        // NoPlatformSounds: the person can switch off Android's own "tag found" sound (Settings).
        var flags = NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.NfcF | NfcReaderFlags.NfcV |
                    NfcReaderFlags.NfcBarcode | NfcReaderFlags.SkipNdefCheck;
        if (!AppSettings.NfcSound) flags |= NfcReaderFlags.NoPlatformSounds;
        adapter.EnableReaderMode(activity, _callback, flags, extras);
    }

    public void Stop()
    {
        var adapter = Adapter;
        var activity = Platform.CurrentActivity;
        if (adapter is not null && activity is not null && _callback is not null)
            adapter.DisableReaderMode(activity);
        _callback = null;
    }

    public void OpenSettings()
    {
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionNfcSettings);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Platform.AppContext.StartActivity(intent);
    }

    private void OnTag(Tag tag)
    {
        if (_pendingWrite is { } operation)
        {
            _pendingWrite = null;
            bool ok;
            string note;
            try { (ok, note) = operation(tag); }
            catch (Exception ex)
            {
                ok = false;
                note = "Write failed, hold the tag still and try again. (" + ex.Message + ")";
            }
            MainThread.BeginInvokeOnMainThread(() => TagWritten?.Invoke(ok, note));
            return;
        }

        NfcTagInfo info;
        try
        {
            info = Describe(tag);
        }
        catch (Exception ex)
        {
            info = new NfcTagInfo(ToHex(tag.GetId()), tag.GetTechList()?.ToList() ?? [],
                [("Error", ex.Message)], []);
        }
        MainThread.BeginInvokeOnMainThread(() => TagRead?.Invoke(info));
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private static string TryText(byte[] payload)
    {
        if (payload.Length == 0) return "";
        var text = Encoding.UTF8.GetString(payload);
        return text.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)) ? text : ToHex(payload);
    }

    private sealed class ReaderCallback(NfcReader owner) : Java.Lang.Object, NfcAdapter.IReaderCallback
    {
        public void OnTagDiscovered(Tag? tag)
        {
            if (tag is not null) owner.OnTag(tag);
        }
    }
#else
    public bool IsSupported => false;
    public bool IsEnabled => false;
    public bool IsArmedToWrite => false;
    public void Start() { }
    public void Stop() { }
    public void OpenSettings() { }
    public void ArmRawWrite(byte[] dump, string dumpSerial) { }
    public void ArmRawText(byte[] payload) { }
    public void ArmErase() { }
    public void Disarm() { }
    public static string? ContentFromMemory(byte[] memory) => null;
#endif
}
