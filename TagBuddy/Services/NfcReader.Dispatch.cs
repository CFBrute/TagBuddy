#if ANDROID
using Android.Content;
using Android.Nfc;

using static TagBuddy.Services.Type2Memory;

namespace TagBuddy.Services;

/// <summary>Tags Android hands to the app while it is not reading NFC itself: the app is registered for its own
/// private-text record type (<see cref="SealedText.MimeType"/>, see the intent filter on MainActivity), so holding
/// such a tag to the phone opens Tag Buddy, even when it is closed, and the NFC tab shows the passphrase box.</summary>
public sealed partial class NfcReader
{
    /// <summary>A tag from Android's dispatch, waiting for the NFC tab to show it.</summary>
    private NfcTagInfo? _dispatched;

    /// <summary>Raised on the main thread when a dispatched tag is waiting (<see cref="TakeDispatchedTag"/>).</summary>
    public event Action? TagDispatched;

    public bool HasDispatchedTag => _dispatched is not null;

    /// <summary>The waiting dispatched tag, once: the NFC tab takes it to show it.</summary>
    public NfcTagInfo? TakeDispatchedTag()
    {
        var tag = _dispatched;
        _dispatched = null;
        return tag;
    }

    /// <summary>Called by MainActivity with the intent that started or resumed it. Anything but an NDEF tag
    /// discovery is ignored.</summary>
    public void HandleIntent(Intent? intent)
    {
        if (intent?.Action != NfcAdapter.ActionNdefDiscovered) return;
        // Handled once: the same intent comes back if the activity is recreated.
        intent.SetAction(Intent.ActionMain);

        var tag = TagExtra(intent);
        var messages = MessagesExtra(intent);
        Task.Run(() =>
        {
            // Read the tag as the NFC tab would, for its details. If it has already left the phone, Android's own
            // copy of the NDEF message still holds the private text.
            NfcTagInfo? info = null;
            if (tag is not null)
            {
                try { info = Describe(tag); }
                catch { /* the tag moved away; use the message below */ }
            }
            if (info?.SealedRecord is null)
                info = FromMessages(tag, messages, info);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _dispatched = info;
                TagDispatched?.Invoke();
            });
        });
    }

    /// <summary>A tag description from the NDEF messages Android read, keeping what a direct read did learn.</summary>
    private static NfcTagInfo FromMessages(Tag? tag, NdefMessage[] messages, NfcTagInfo? read)
    {
        var records = messages.SelectMany(m => m.GetRecords() ?? []).Select(DescribeRecord).ToList();
        return new NfcTagInfo(
            read?.Id ?? (tag?.GetId() is { } id ? ToHex(id) : ""),
            read?.Technologies ?? tag?.GetTechList()?.ToList() ?? [],
            read?.Details ?? [],
            records,
            read?.MemoryDump,
            read?.RawMemory,
            read?.ChipName);
    }

    private static Tag? TagExtra(Intent intent) =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableExtra(NfcAdapter.ExtraTag, Java.Lang.Class.FromType(typeof(Tag))) as Tag
#pragma warning disable CA1422 // the typed overload above exists only from Android 13
            : intent.GetParcelableExtra(NfcAdapter.ExtraTag) as Tag;
#pragma warning restore CA1422

    private static NdefMessage[] MessagesExtra(Intent intent)
    {
        IEnumerable<object>? items = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? (IEnumerable<object>?)intent.GetParcelableArrayExtra(NfcAdapter.ExtraNdefMessages, Java.Lang.Class.FromType(typeof(NdefMessage)))
#pragma warning disable CA1422
            : intent.GetParcelableArrayExtra(NfcAdapter.ExtraNdefMessages);
#pragma warning restore CA1422
        return items?.OfType<NdefMessage>().ToArray() ?? [];
    }
}
#endif
