using System.Text;
using Android.Nfc;
using TagBuddy.Controls;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The NFC tab: read a tag (details, content, memory dump, private text with its passphrase), or write
/// text, links, contacts, private text, raw bytes, edited pages or blocks, or a dump to one, or erase it.</summary>
[QueryProperty(nameof(IncomingDumpId), AppNavigation.Dump)]
[QueryProperty(nameof(IncomingText), AppNavigation.Text)]
[QueryProperty(nameof(IncomingViewReadId), AppNavigation.ViewRead)]
[QueryProperty(nameof(IncomingEditReadId), AppNavigation.EditRead)]
[QueryProperty(nameof(IncomingApply), AppNavigation.Apply)]
public partial class NfcPage : ContentPage
{
    /// <summary>The write types, in the order of the picker in NfcPage.xaml.</summary>
    private enum WriteMode { Auto, Link, Text, Contact, Private, Raw, Segments, Dump, Erase }

    private const string NdefHint = "Writes NDEF content to writable tags only (NTAG, Ultralight and other NDEF tags). The tag's serial number is fixed and is never changed. Locked cards cannot be written.";
    private static readonly string RawHint = "Writes the text as plain bytes into the tag's user pages, with no NDEF wrapper, and clears the rest. Phones will not show it as a text tag, but this app reads it back" +
        (Features.Amiibo ? ", and it works on Amiibo-formatted tags where text never can." : ".");
    private static readonly string PrivateHint = "Encrypts the text with your passphrase on this phone (AES-256) and writes it as a private record. " +
        "Only Tag Buddy can read it, and only with the passphrase. The passphrase is not stored anywhere: if it is forgotten, the text cannot be recovered. " +
        $"Room for about {144 - 90} characters on an NTAG213, {504 - 90} on an NTAG215 and {888 - 90} on an NTAG216.";

    /// <summary>Readable text found in a tag's raw memory when it carries no NDEF content.</summary>
    private string? _rawText;

    /// <summary>Private text from the tag, once opened with its passphrase. Only kept while the result is shown.</summary>
    private string? _unlocked;

    private string? CurrentContent => _unlocked ?? _last?.Content ?? _rawText;

    /// <summary>Encrypting private text takes a moment on purpose; a newer request (more typing, another mode, the
    /// page going away) makes an older one drop its result.</summary>
    private int _sealGeneration;
    private bool _active;

    /// <summary>Handed over from Saved: a dump to write (by id) or a text to write.</summary>
    public string? IncomingDumpId { get; set; }
    public string? IncomingText { get; set; }

    /// <summary>Handed over from Saved: a saved read (by id) to show as it was read, or to edit for writing back.</summary>
    public string? IncomingViewReadId { get; set; }
    public string? IncomingEditReadId { get; set; }

    /// <summary>Handed over from the diff page: a read to edit, with the other tag's bytes as pending changes.</summary>
    public DiffApply? IncomingApply { get; set; }

    private readonly SavedService _saved;
    private readonly NfcReader _nfc = NfcReader.Instance;
    private NfcTagInfo? _last;
    private bool _writeMode;

    /// <summary>The dump chosen for writing, with where it came from (for the result note and the label).</summary>
    private byte[]? _dumpData;
    private string _dumpSerial = "";
    private string _dumpName = "";

    /// <summary>The segment editor: what it expects and holds, the tag types offered, and what is wrong with the
    /// input (each field's own problem, so fixing one does not hide another).</summary>
    private TagEdit? _edit;
    private List<SegmentLayout> _segmentLayouts = [];
    private bool _renderingSegments;
    private string? _idError, _rangeError, _sizeError;

    /// <summary>What the hidden input field holds between keystrokes: typing adds to it, backspace takes from it, and it
    /// is put back after each, so every key is seen as a change.</summary>
    private const string InputSentinel = "  ";
    private bool _resettingInput;

    /// <summary>Where the keyboard's typing goes: the byte grid, the Sectors tab's key rows for the cursor's sector
    /// (both the edit's own bytes), or the "all sectors" key rows (a scratch edit of their own).</summary>
    private enum TypingTarget { Grid, SectorKeys, AllKeys }
    private TypingTarget _typing = TypingTarget.Grid;

    /// <summary>Key A (row 0) and key B (row 1) to set on every opened sector, typed like the grid's rows.</summary>
    private readonly TagEdit _allKeys = TagEdit.Blank(new SegmentLayout(TagFamily.Iso15693, "keys", 6, 2));

    /// <summary>The segment write last armed, so a successful write to the expected tag can be marked as done.</summary>
    private SegmentWrite? _segmentWrite;

    /// <summary>Segment writes already on tags, newest on top: undoing one writes its old bytes back to that tag, and
    /// moves it to the redo stack; redoing writes the change again. Kept while the app runs.</summary>
    private readonly Stack<SegmentWriteRecord> _tagUndo = new(), _tagRedo = new();

    /// <summary>An undo or redo on the tag that is armed and waits for the tag, or null.</summary>
    private enum TagStep { Undo, Redo }
    private TagStep? _tagStep;

    public NfcPage(SavedService saved)
    {
        InitializeComponent();
        _saved = saved;

        if (!Features.Amiibo)
        {
            WriteTypePicker.Items[(int)WriteMode.Dump] = "Binary dump (backup)";
            DumpHint.Text = "Writes page 3 and every user page of the dump to an NTAG or Ultralight tag. The serial number (pages 0-2) and the permanent lock bits are never written, so the tag stays rewritable.";
            EraseHint.Text = "Clears every user page of the tag (all zeros with an empty NDEF message). The serial number and one-time pages are untouched. Save a dump first if you may want the content back.";
        }

        SegmentGrid.CursorPlaced += OnSegmentCursorPlaced;
        SegmentGrid.SelectionChanged += UpdateSegmentTools;   // keep the selection count in step as it grows
        SegmentGrid.LongPressed += OnGridLongPressed;         // a copy menu on the pressed byte or the selection
        SectorKeyAGrid.CursorPlaced += () => StartTyping(TypingTarget.SectorKeys);
        SectorKeyBGrid.CursorPlaced += () => StartTyping(TypingTarget.SectorKeys);
        AllKeyAGrid.CursorPlaced += () => StartTyping(TypingTarget.AllKeys);
        AllKeyBGrid.CursorPlaced += () => StartTyping(TypingTarget.AllKeys);
        AllKeyAGrid.Window = new SegmentGrid.RowWindow(0, 0, 6, "Key A");
        AllKeyBGrid.Window = new SegmentGrid.RowWindow(1, 0, 6, "Key B");
        AllKeyAGrid.Edit = AllKeyBGrid.Edit = _allKeys;
        UpdateAllKeys();
        WriteTypePicker.SelectedIndex = 0;
        SetMode(write: false);
        SetPasswordMode(asText: false);   // hex by default, the way RFID tools show a password
    }

    /// <summary>Something arrived from Saved: switch to write mode and arm it.</summary>
    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        // Handed over from another tab (a saved read to show or edit, a dump or text to write): apply it once the page
        // has appeared and laid out. Doing it straight here showed nothing the first time a saved tag was opened after
        // launch — it rendered only when opened a second time — because the view was built before its first layout.
        Dispatcher.Dispatch(async () => await ApplyIncomingAsync());
    }

    private async Task ApplyIncomingAsync()
    {
        try
        {
            if (IncomingText is { Length: > 0 } text)
            {
                IncomingText = null;
                SetMode(write: true);
                WriteTypePicker.SelectedIndex = (int)WriteMode.Auto;
                WriteEditor.Text = text;
                ArmWrite();
            }
            else if (IncomingViewReadId is { Length: > 0 } viewId)
            {
                IncomingViewReadId = null;
                if (await LoadSavedReadAsync(viewId) is { } read)
                {
                    SetMode(write: false);
                    ShowSavedRead(read.Tag, read.Item);
                }
            }
            else if (IncomingEditReadId is { Length: > 0 } editId)
            {
                IncomingEditReadId = null;
                if (await LoadSavedReadAsync(editId) is { } read)
                {
                    ShowSavedRead(read.Tag, read.Item);
                    OnEditTag(this, EventArgs.Empty);
                }
            }
            else if (IncomingApply is { } apply)
            {
                IncomingApply = null;
                OnTagRead(apply.Target, fromSaved: true);
                _lastName = apply.TargetName;
                TagTitle.Text = $"{apply.TargetName}\nTag {apply.Target.Id}  ·  {string.Join(", ", apply.Target.Technologies)}";
                OnEditTag(this, EventArgs.Empty);
                if (_edit is { } edit)
                {
                    var (applied, skipped) = edit.Apply(apply.Bytes);
                    SegmentEdited();
                    string noun = edit.Layout.Noun;
                    Notice.Show($"{applied} {noun}{(applied == 1 ? "" : "s")} from {apply.SourceName} ready to write" +
                                (skipped > 0 ? $"; {skipped} cannot be written here and {(skipped == 1 ? "was" : "were")} left out" : ""));
                }
            }
            else if (IncomingDumpId is { Length: > 0 } id)
            {
                IncomingDumpId = null;
                await _saved.LoadAsync();
                var dump = _saved.Dumps.FirstOrDefault(d => d.Id.ToString() == id);
                if (dump is null) return;
                var data = await _saved.ReadDumpAsync(dump);
                SetMode(write: true);
                WriteTypePicker.SelectedIndex = (int)WriteMode.Dump;
                UseDump(data, dump.DisplayName, dump.DumpSerial);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not prepare the write", ex.Message, "OK");
        }
    }

    private async Task<(NfcTagInfo Tag, SavedItem Item)?> LoadSavedReadAsync(string id)
    {
        await _saved.LoadAsync();
        var item = _saved.Dumps.FirstOrDefault(d => d.Id.ToString() == id);
        if (item is null) return null;
        var tag = await _saved.LoadReadAsync(item);
        if (tag is null)
        {
            await DisplayAlertAsync("Not available", "The saved read could not be loaded.", "OK");
            return null;
        }
        return (tag, item);
    }

    /// <summary>Shows a saved read as if the tag had just been held to the phone, saying when it was read.</summary>
    private void ShowSavedRead(NfcTagInfo tag, SavedItem item)
    {
        OnTagRead(tag, fromSaved: true);
        TagTitle.Text = $"{item.DisplayName}  ·  saved {item.CreatedAt:yyyy-MM-dd HH:mm}\nTag {tag.Id}  ·  {string.Join(", ", tag.Technologies)}";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (!_nfc.IsSupported)
        {
            StatusLabel.Text = "This phone has no NFC reader";
            HintLabel.IsVisible = false;
            return;
        }
        if (!_nfc.IsEnabled)
        {
            StatusLabel.Text = "NFC is turned off";
            SettingsButton.IsVisible = true;
            return;
        }

        StatusLabel.Text = "Hold a tag or card against the back of the phone";
        SettingsButton.IsVisible = false;
        _nfc.TagRead += OnTagRead;
        _nfc.TagWritten += OnTagWritten;
        _nfc.TagDispatched += OnTagDispatched;
        _nfc.Start();
        _active = true;
        ApplyPassword();     // the fields may still hold a password or key from before the tab was left
        ApplyClassicKey();
        _nfc.SetPasswordSearch(_passwordStore.All());   // a remembered password opens its tag on its own
        if (_writeMode) ArmWrite();
        OnTagDispatched();
    }

    /// <summary>A private tag Android handed the app (see NfcReader.Dispatch): show it as if it had just been read
    /// here, which brings up the passphrase box.</summary>
    private void OnTagDispatched()
    {
        if (_nfc.TakeDispatchedTag() is not { } tag) return;
        SetMode(write: false);
        OnTagRead(tag);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _active = false;
        _sealGeneration++;
        _nfc.TagRead -= OnTagRead;
        _nfc.TagWritten -= OnTagWritten;
        _nfc.TagDispatched -= OnTagDispatched;
        _nfc.Disarm();
        _compareWith = null;
        CompareHint.IsVisible = false;
        _nfc.SetManualPassword(null, null);   // not kept once the tab is left
        _nfc.SetManualClassicKey(null);
        _tryAllKeys = false;
        _nfc.SetClassicKeySearch([], [], false);
        _nfc.SetPasswordSearch(new Dictionary<string, byte[]>());   // re-armed from the store on the next appear
        _tryDefaultOnce = false;
        _nfc.SetTryDefaultOnce(false);
        _nfc.Stop();
    }

    private void OnOpenSettings(object? sender, EventArgs e) => _nfc.OpenSettings();

    // ---------------------------------------------------------------- tag password

    /// <summary>Shows the password box, once a protected tag has been noticed, with what to do next. It is never
    /// shown before that: nothing to set up in advance.</summary>
    private void RevealPassword(string info)
    {
        PasswordInfo.Text = info;
        if (!PasswordPanel.IsVisible)
        {
            PasswordPanel.IsVisible = true;
            if (PasswordEntry.Text?.Length is null or 0) PasswordEntry.Focus();
        }
    }

    /// <summary>Whether the password box reads what is typed as text (4 characters) instead of hex (8 digits).</summary>
    private bool _passwordAsText;

    private void OnPasswordHexMode(object? sender, EventArgs e) => SetPasswordMode(asText: false);
    private void OnPasswordTextMode(object? sender, EventArgs e) => SetPasswordMode(asText: true);

    private void SetPasswordMode(bool asText)
    {
        _passwordAsText = asText;
        Palette.Select(HexModeButton, !asText);
        Palette.Select(TextModeButton, asText);
        PasswordEntry.MaxLength = asText ? 4 : 11;
        PasswordEntry.Placeholder = asText ? "4 characters, e.g. pass" : "8 hex digits, e.g. FFFFFFFF";
        PasswordDefaultButton.IsVisible = !asText;   // FFFFFFFF is a hex default
        PasswordFormatHint.Text = asText
            ? "A tag's password is 4 bytes, here as 4 typed characters. It is kept only while this tab is open."
            : "A tag's password is 4 bytes, shown as 8 hex digits; a new NTAG/Ultralight's factory password is FFFFFFFF. It is kept only while this tab is open.";
        PasswordEntry.Text = string.Empty;
        ApplyPassword();
    }

    private void OnPasswordChanged(object? sender, TextChangedEventArgs e) => ApplyPassword();

    private bool HasTypedPassword => TagPassword.TryParse(PasswordEntry.Text, _passwordAsText, out _);

    /// <summary>The serial the password in the box was entered for; a password belongs to one tag.</summary>
    private string? _passwordSerial;

    private void ApplyPassword()
    {
        bool ok = TagPassword.TryParse(PasswordEntry.Text, _passwordAsText, out var pwd);
        _passwordSerial = ok ? _last?.Id : null;
        _nfc.SetManualPassword(ok ? pwd : null, _passwordSerial);
    }

    private void OnPasswordDefault(object? sender, EventArgs e)
    {
        PasswordEntry.Text = "FFFFFFFF";
        PasswordEntry.Unfocus();
    }

    /// <summary>Set for one read when the person taps "Try the factory default on the next read" (shown only when
    /// the automatic setting is off).</summary>
    private bool _tryDefaultOnce;

    private void OnTryDefaultOnce(object? sender, EventArgs e)
    {
        _tryDefaultOnce = true;
        _nfc.SetTryDefaultOnce(true);
        Notice.Show("Hold the tag against the phone again");
        StatusLabel.Text = "Hold the tag again to try the factory default password (FFFFFFFF) once…";
    }

    // ---------------------------------------------------------------- MIFARE Classic key

    /// <summary>Known MIFARE Classic keys the app has learned or the person imported (sideload only).</summary>
    private readonly MifareKeyStore _keyStore = new();

    /// <summary>Passwords the app has seen open protected NTAG/Ultralight tags, remembered by serial so the same tag
    /// opens on its own next time, the way <see cref="_keyStore"/> remembers MIFARE Classic keys.</summary>
    private readonly Type2PasswordStore _passwordStore = new();

    /// <summary>Set for one read when the person taps "Try all": that read runs the whole imported dictionary.</summary>
    private bool _tryAllKeys;

    /// <summary>Remembers a working Type 2 password the last read or write found, so the tag opens on its own later,
    /// and re-arms the reader's password search with every remembered password.</summary>
    private void RememberType2Password()
    {
        if (NfcReader.Type2PasswordFound is { } found)
        {
            _passwordStore.Remember(found.Serial, found.Password);
            _nfc.SetPasswordSearch(_passwordStore.All());
        }
    }

    private void OnClassicKeyChanged(object? sender, TextChangedEventArgs e) => ApplyClassicKey();

    /// <summary>Hands the reader the entered key, the keys remembered from earlier reads (always tried), and the
    /// imported dictionary (tried only when "Try all" is armed).</summary>
    private void ApplyClassicKey()
    {
        _nfc.SetManualClassicKey(MifareClassicKeys.TryParseKey(ClassicKeyEntry.Text, out var key) ? key : null);
        if (Features.MifareKeyTools)
            _nfc.SetClassicKeySearch(_keyStore.AllRemembered(), _keyStore.ImportedKeys(), _tryAllKeys);
    }

    /// <summary>The dictionary tools, shown under the custom-key box when a Classic card has locked sectors.</summary>
    private void ShowKeyDictionaryTools()
    {
        KeyDictionaryPanel.IsVisible = Features.MifareKeyTools;
        if (!Features.MifareKeyTools) return;
        int imported = _keyStore.ImportedCount();
        TryAllKeysButton.Text = imported > 0 ? $"Try all {imported:N0} default keys" : "Try all default keys";
        TryAllKeysButton.IsEnabled = imported > 0;
        KeyStoreStatus.Text = imported > 0
            ? $"{imported:N0} default keys imported. Hold the card again after tapping to run them against the locked sectors; a key that opens a sector is remembered for this card."
            : "Import a dictionary of default keys (for example mfc_default_keys.dic) to try more than the built-in set. Keys that open a card are remembered for next time.";
    }

    private void OnTryAllKeys(object? sender, EventArgs e)
    {
        if (_keyStore.ImportedCount() == 0) return;
        _tryAllKeys = true;
        ApplyClassicKey();
        Notice.Show("Hold the card against the phone again");
        StatusLabel.Text = $"Hold the card again to try all {_keyStore.ImportedCount():N0} default keys on the locked sectors…";
    }

    private async void OnImportKeys(object? sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Pick a key file (.dic or .txt)" });
            if (file is null) return;
            using var reader = new StreamReader(await file.OpenReadAsync());
            int added = _keyStore.Import(await reader.ReadToEndAsync());
            ApplyClassicKey();
            ShowKeyDictionaryTools();
            await DisplayAlertAsync("Keys imported", added > 0
                ? $"Added {added:N0} new keys. The store now has {_keyStore.ImportedCount():N0}."
                : "No new keys were found in that file (it may already be imported, or hold no 12-hex-digit keys).", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not import", ex.Message, "OK");
        }
    }

    // ---------------------------------------------------------------- mode switch

    private void OnReadMode(object? sender, EventArgs e) => SetMode(write: false);
    private void OnWriteMode(object? sender, EventArgs e) => SetMode(write: true);

    private void SetMode(bool write)
    {
        _writeMode = write;
        ReadSection.IsVisible = !write;
        WriteSection.IsVisible = write;

        Palette.SelectTab(ReadModeButton, ReadUnderline, !write);
        Palette.SelectTab(WriteModeButton, WriteUnderline, write);
        UpdateFloatingToolsVisibility();   // the floating editor tools belong to write mode only

        if (write) ArmWrite();
        else _nfc.Disarm();
    }

    private void OnTagRead(NfcTagInfo tag) => OnTagRead(tag, fromSaved: false);

    /// <param name="fromSaved">A saved read shown again: no vibration, and nothing about keys remembered.</param>
    private void OnTagRead(NfcTagInfo tag, bool fromSaved)
    {
        // A password belongs to the tag it was entered for: drop it when a different tag is held to the phone, so
        // it is never tried on the new one (which could count against the new tag's wrong-attempt limit).
        if (_passwordSerial is not null && !string.Equals(tag.Id, _passwordSerial, StringComparison.OrdinalIgnoreCase))
        {
            PasswordEntry.Text = string.Empty;   // clears _passwordSerial and the reader's password via ApplyPassword
        }

        // Waiting to compare: this read is the other side, and both go to the diff page.
        if (!fromSaved && _compareWith is { } left)
        {
            _compareWith = null;
            CompareHint.IsVisible = false;
            Feedback.Tag(ok: true);
            _ = ShowDiffAsync(left.Tag, left.Name, tag, "read just now");
            return;
        }

        _last = tag;
        if (!fromSaved) Feedback.Tag(ok: true);

        WaitingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

        // Protected pages the read could not reach: offer the password, then the person holds the tag again.
        // Running the default once is a one-shot per tap; turn it off again now that this read is done.
        if (_tryDefaultOnce)
        {
            _tryDefaultOnce = false;
            _nfc.SetTryDefaultOnce(false);
        }

        if (tag.NeedsPassword)
        {
            RevealPassword(HasTypedPassword
                ? "That password did not open this tag. Check it and hold the tag again, or try another."
                : "This tag is password-protected. Enter its password, then hold the tag again to read the locked pages.");
            // When the app does not try the default by itself (setting off), offer a one-tap attempt here.
            TryDefaultButton.IsVisible = !AppSettings.AutoTryDefaultPassword;
        }
        else
            PasswordPanel.IsVisible = false;   // read fully; nothing to unlock

        // A MIFARE Classic card: remember the keys that opened its sectors, so this card opens quickly next time.
        if (!fromSaved && Features.MifareKeyTools && NfcReader.ClassicKeysFound.Count > 0)
            _keyStore.Remember(tag.Id, NfcReader.ClassicKeysFound.Select(k => k.Key));

        // An NTAG/Ultralight opened with a password (typed, remembered, or the default): remember it for this tag.
        if (!fromSaved) RememberType2Password();

        // Running the whole dictionary is a one-shot per tap; turn it off again now that this read is done.
        if (_tryAllKeys)
        {
            _tryAllKeys = false;
            ApplyClassicKey();
        }

        // A MIFARE Classic card with sectors the default keys did not open: offer a custom key and the dictionary.
        ClassicKeyPanel.IsVisible = tag.NeedsClassicKey;
        if (tag.NeedsClassicKey) ShowKeyDictionaryTools();

        TagTitle.Text = $"Tag {tag.Id}  ·  {string.Join(", ", tag.Technologies)}";

        // No NDEF content: bytes written raw by this app (or anything else wordy) still count as content.
        _rawText = null;
        if (!tag.HasContent && tag.SealedRecord is null && tag.HasRawMemory)
        {
            var raw = DumpText.Describe(tag.RawMemory!);
            if (raw.Length > 0) _rawText = raw;
        }

        _unlocked = null;
        UnlockEntry.Text = string.Empty;
        UnlockStatus.Text = "This tag holds private text. Enter its passphrase to read it.";
        UnlockStatus.TextColor = Colors.Gray;
        UnlockPanel.IsVisible = tag.SealedRecord is not null;
        ShowContent();

        Details.Children.Clear();
        AddDetail("Serial number", tag.Id);
        foreach (var (label, value) in tag.Details)
            AddDetail(label, value);

        if (tag.Records.Count > 0)
        {
            AddDetail("Records", tag.Records.Count.ToString());
            foreach (var record in tag.Records)
                AddDetail(record.Kind, record.Text.Length == 0 ? "(empty)" : record.Text);
        }

        if (tag.HasContent)
        {
            AddDetail("Content", ScanDetails.DescribeContent(tag.Content!));
            AddDetail("Length", $"{tag.Content!.Length} characters, {Encoding.UTF8.GetByteCount(tag.Content)} bytes");
            foreach (var (label, value) in ScanDetails.DescribeStructuredContent(tag.Content))
                AddDetail(label, value);
        }
        else if (_rawText is not null)
        {
            AddDetail("Content", "Raw bytes, no NDEF wrapper (only this app reads them as text)");
            AddDetail("Length", $"{_rawText.Length} characters");
        }
        if (tag.SealedRecord?.Sealed is { } sealedBytes)
            AddDetail("Private text", $"{sealedBytes.Length - SealedText.Overhead} bytes, encrypted with a passphrase (AES-256-GCM)");

        MemoryPanel.IsVisible = tag.HasMemoryDump;
        EditTagButton.IsVisible = tag.CanEdit;
        SaveTagButton.IsVisible = tag.HasMemoryDump && !fromSaved;   // already in Saved when shown from there
        CompareButton.IsVisible = tag.Segments is not null;
        _lastName = fromSaved ? TagTitle.Text.Split('\n')[0].Split("  ·  ")[0] is { Length: > 0 } n ? "saved: " + n : "saved" : "read just now";
        if (tag.RawMemory is { Length: > 0 } memory)
        {
            // Type 2 tag: the full page memory, shown with its map, saveable as a backup.
            MemoryTitle.Text = "Readable memory";
            MemoryDescription.Text = "Every page of this tag's memory. Save the tag to keep it as read: a backup that can be viewed again, edited, or written back later.";
            DumpButtons.IsVisible = true;
            MemoryOverview.IsVisible = true;
            MemoryMapView.Show(MemoryOverview, MemoryLabel, memory, tag.ChipName);
        }
        else if (tag.HasMemoryDump)
        {
            // MIFARE Classic: the sectors the common default keys opened; ISO 15693: the blocks that answered.
            // Not a backup, so no dump buttons.
            bool label = tag.Segments?.Layout.Family == TagFamily.Iso15693;
            MemoryTitle.Text = label ? "Label memory" : "Card data";
            MemoryDescription.Text = label
                ? "Every block of this label that answered a read."
                : "The sectors that opened with common default keys. A card secured with custom keys shows only its serial number.";
            DumpButtons.IsVisible = false;
            if (label)
            {
                MemoryOverview.IsVisible = false;
                MemoryLabel.Text = string.Join("\n", tag.MemoryDump!);
            }
            else
            {
                // MIFARE Classic: the trailers and rules in colour, with a legend in the overview label.
                MemoryOverview.IsVisible = true;
                ClassicDumpView.Show(MemoryOverview, MemoryLabel, tag.MemoryDump!);
            }
        }
        else
        {
            MemoryLabel.Text = string.Empty;
        }

        // Offer to bring a saved copy of this tag up to date when it has changed since it was saved.
        if (!fromSaved && tag.HasMemoryDump && AppSettings.AskUpdateSavedTag)
            _ = OfferToUpdateSavedTagAsync(tag);
    }

    /// <summary>After reading a tag that is already saved (same serial) but has changed since, offers to update the
    /// saved copy so it stays current. A saved copy that already matches is never asked about (see OutdatedReadAsync);
    /// the Settings toggle turns the offer off.</summary>
    private async Task OfferToUpdateSavedTagAsync(NfcTagInfo tag)
    {
        try
        {
            if (await _saved.OutdatedReadAsync(tag) is not { } item) return;
            if (_last?.Id != tag.Id) return;   // another tag was read while the files were checked
            if (!await DisplayAlertAsync("Update the saved tag?",
                    $"You have this tag saved as \"{item.DisplayName}\", and it has changed since. Update the saved copy " +
                    "to what you just read?", "Update", "Keep"))
                return;
            await _saved.UpdateReadAsync(item, tag);
            Notice.Show($"Updated \"{item.DisplayName}\"");
        }
        catch { /* a read or save hiccup: just skip the offer */ }
    }

    /// <summary>The tag's content, or what it is when there is none to show: the private text once unlocked.</summary>
    private void ShowContent()
    {
        if (_last is not { } tag) return;
        var card = tag.Details.FirstOrDefault(d => d.Label == "Card").Value;
        var data = tag.Details.FirstOrDefault(d => d.Label == "Data").Value;
        var content = CurrentContent;
        bool hasContent = !string.IsNullOrWhiteSpace(content);

        ContentLabel.Text = hasContent ? content
            : tag.SealedRecord is not null ? "Private text"
            : card ?? data ?? "No readable content on this tag";
        ContentLabel.TextColor = hasContent ? Palette.Accent : Colors.Gray;

        ContentActions.IsVisible = hasContent;
        Preview.Value = content;
        ViewPageButton.IsVisible = Links.IsWebLink(content, out _);
        HandoffButtons.Show(hasContent ? content : null, OpenLinkButton, HandoffButton);
    }

    /// <summary>Opens the tag's private text with the passphrase typed, off the main thread (the key derivation
    /// is deliberately slow). A wrong passphrase simply does not open it.</summary>
    private async void OnUnlock(object? sender, EventArgs e)
    {
        if (_last is not { SealedRecord.Sealed: { } sealedBytes } tag) return;
        var passphrase = UnlockEntry.Text ?? string.Empty;
        if (passphrase.Length == 0)
        {
            UnlockStatus.Text = "Enter the passphrase first.";
            return;
        }

        UnlockStatus.Text = "Unlocking...";
        UnlockStatus.TextColor = Colors.Gray;
        bool ok;
        string text;
        try
        {
            (ok, text) = await Task.Run(() =>
            {
                bool opened = SealedText.TryOpen(sealedBytes, passphrase, out var plain);
                return (opened, plain);
            });
        }
        catch (Exception ex)
        {
            UnlockStatus.Text = "This phone could not decrypt the text: " + ex.Message;
            UnlockStatus.TextColor = Palette.Error;
            return;
        }
        if (!ReferenceEquals(tag, _last)) return;   // another tag was read meanwhile

        if (!ok)
        {
            Feedback.Tag(ok: false);
            UnlockStatus.Text = "That passphrase does not open this tag (or the tag was changed).";
            UnlockStatus.TextColor = Palette.Error;
            return;
        }

        Feedback.Tag(ok: true);
        _unlocked = text;
        UnlockEntry.Text = string.Empty;
        UnlockEntry.Unfocus();
        UnlockPanel.IsVisible = false;
        ShowContent();
    }

    /// <summary>Long press on the read's memory: copy it as the shown listing, or as plain hex bytes.</summary>
    private async void OnReadMemoryLongPressed(object? sender, EventArgs e)
    {
        if (_last is not { } tag || !tag.HasMemoryDump && tag.RawMemory is not { Length: > 0 }) return;
        var choice = await DisplayActionSheet("Copy memory", "Cancel", null, "Copy as text", "Copy as hex");
        if (choice == "Copy as text")
        {
            var lines = tag.MemoryDump ?? NfcReader.FormatDump(tag.RawMemory ?? []) ?? [];
            await Clipboard.Default.SetTextAsync(string.Join("\n", lines));
            Notice.Show("Memory copied as text");
        }
        else if (choice == "Copy as hex")
        {
            await Clipboard.Default.SetTextAsync(string.Concat(SavedService.TagBytes(tag).Select(b => b.ToString("X2"))));
            Notice.Show("Memory copied as hex");
        }
    }

    /// <summary>Long press on the details: copy the serial and the detail lines as text.</summary>
    private async void OnReadDetailsLongPressed(object? sender, EventArgs e)
    {
        if (_last is not { } tag) return;
        var lines = tag.Details.Select(d => $"{d.Label}: {d.Value}").Prepend($"Tag {tag.Id}");
        await Clipboard.Default.SetTextAsync(string.Join("\n", lines));
        Notice.Show("Details copied");
    }

    // ---------------------------------------------------------------- compare

    /// <summary>Where the shown read came from, for the diff page's labels.</summary>
    private string _lastName = "read just now";

    /// <summary>A read waiting for the next tag held to the phone, to compare the two; null when not comparing.</summary>
    private (NfcTagInfo Tag, string Name)? _compareWith;

    /// <summary>Compares the shown read with a saved read of the same kind of tag, or with the next tag held to the
    /// phone. Only the pages or blocks that differ are shown.</summary>
    private async void OnCompare(object? sender, EventArgs e)
    {
        if (_last is not { Segments: { } segments } tag) return;
        try
        {
            await _saved.LoadAsync();
            var candidates = _saved.Dumps.Where(d => d.HasRead).OrderByDescending(d => d.SortTime).ToList();
            const string next = "The next tag held to the phone";
            var labels = candidates.Select(c => $"{c.DisplayName}  ·  {c.Subtitle}").Prepend(next).ToArray();
            var choice = await DisplayActionSheetAsync($"Compare this {segments.Layout.ChipName} with", "Cancel", null, labels);
            if (choice is null || choice == "Cancel") return;

            if (choice == next)
            {
                _compareWith = (tag, _lastName);
                CompareHint.IsVisible = true;
                CompareHint.Text = $"Hold the other {segments.Layout.ChipName} to the back of the phone to compare. Reading another kind of tag cancels.";
                return;
            }

            int index = Array.IndexOf(labels, choice) - 1;
            if (index < 0) return;
            var other = await _saved.LoadReadAsync(candidates[index]);
            if (other is null)
            {
                await DisplayAlertAsync("Not available", "That saved read could not be loaded.", "OK");
                return;
            }
            await ShowDiffAsync(tag, _lastName, other, "saved: " + candidates[index].DisplayName);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not compare", ex.Message, "OK");
        }
    }

    private async Task ShowDiffAsync(NfcTagInfo left, string leftName, NfcTagInfo right, string rightName)
    {
        var diff = TagDiff.Compare(left, right, out var whyNot);
        if (diff is null)
        {
            await DisplayAlertAsync("Cannot compare", whyNot, "OK");
            return;
        }
        await DiffPage.ShowAsync(diff, leftName, rightName);
    }

    // ---------------------------------------------------------------- dumps (read side)

    private string DumpChipName => _last?.ChipName ?? "Type2";

    /// <summary>Keeps the tag in Saved as it was read: its memory as a dump, and the whole read (details, records,
    /// segments) so it can be shown again as read, or edited and written back to this tag.</summary>
    private async void OnSaveDump(object? sender, EventArgs e)
    {
        if (_last is not { HasMemoryDump: true } tag) return;
        try
        {
            var chip = tag.ChipName ?? tag.Segments?.Layout.ChipName ?? DumpChipName;
            var suggested = Features.Amiibo && tag.HasRawMemory && NfcReader.IsAmiiboDump(tag.RawMemory!) ? $"Amiibo {tag.Id}" : $"{chip} {tag.Id}";
            if (await SavePromptPage.ShowAsync(this, _saved, "Save tag", suggested) is not { } save) return;
            var item = await _saved.AddReadAsync(save.Name, tag, save.Group);
            Notice.Show(save.Group.Length > 0 ? $"Tag saved in {save.Group}: {item.DisplayName}" : $"Tag saved: {item.DisplayName}");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not save", ex.Message, "OK");
        }
    }

    private async void OnShareDump(object? sender, EventArgs e)
    {
        if (_last is { HasRawMemory: true } tag)
            await CodeActions.ShareDumpAsync(this, $"{DumpChipName}_{tag.SerialForFileName}.bin".Replace(' ', '-'),
                tag.RawMemory!, "Share tag dump");
    }

    private void AddDetail(string label, string value) => DetailRows.Add(Details, label, value, labelWidth: 120);

    /// <summary>Keeps the tag's link or text as a text note in Saved. From there it can become a QR code, a
    /// barcode, or be written to another tag.</summary>
    private async void OnSave(object? sender, EventArgs e)
    {
        if (CurrentContent is not { } content) return;
        if (_unlocked is not null)
            await CodeActions.SaveTextAsync(this, _saved, content,
                "Saved notes are not encrypted: this keeps the text readable on this phone. Give it a name (optional):");
        else
            await CodeActions.SaveTextAsync(this, _saved, content);
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (CurrentContent is { } content) await CodeActions.CopyTextAsync(this, content);
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (CurrentContent is { } content) await CodeActions.OpenLinkAsync(this, content);
    }

    private async void OnViewPage(object? sender, EventArgs e)
    {
        if (CurrentContent is { } content) await WebPage.ShowAsync(content);
    }

    // ---------------------------------------------------------------- write

    private WriteMode WriteType => (WriteMode)Math.Max(0, WriteTypePicker.SelectedIndex);

    private void OnWriteTextChanged(object? sender, TextChangedEventArgs e) => ArmWrite();

    private async void OnWriteTypeChanged(object? sender, EventArgs e)
    {
        if (WriteType == WriteMode.Erase)
        {
            // Erasing is destructive and stays armed for every tag held to the phone, so ask once first.
            _nfc.Disarm();
            var ok = await DisplayAlertAsync("Erase tags?",
                "Every tag held to the phone while this mode is selected will have its content wiped. Save a dump first if you may want it back.",
                "Erase mode", "Cancel");
            if (!ok)
            {
                WriteTypePicker.SelectedIndex = (int)WriteMode.Auto;
                return;
            }
        }

        TextPanel.IsVisible = WriteType is not (WriteMode.Dump or WriteMode.Erase or WriteMode.Segments);
        PrivatePanel.IsVisible = WriteType == WriteMode.Private;
        SegmentPanel.IsVisible = WriteType == WriteMode.Segments;
        if (WriteType == WriteMode.Segments && _edit is null)
        {
            // Set up by hand: open the expectations, since the tag type has to be chosen first.
            _edit = TagEdit.Blank(SegmentLayout.Known[0]);
            ShowEdit(expand: true);
        }
        DumpPanel.IsVisible = WriteType == WriteMode.Dump;
        ErasePanel.IsVisible = WriteType == WriteMode.Erase;
        UpdateFloatingToolsVisibility();   // only the segment editor shows the floating tools
        WriteHint.IsVisible = TextPanel.IsVisible;
        WriteHint.Text = WriteType switch
        {
            WriteMode.Raw => RawHint,
            WriteMode.Private => PrivateHint,
            _ => NdefHint,
        };
        ArmWrite();
    }

    private void OnClearEditor(object? sender, EventArgs e)
    {
        WriteEditor.Text = string.Empty;
        ArmWrite();
    }

    /// <summary>Arms the reader for the chosen write type and, unless told otherwise, shows what it is waiting for.</summary>
    private void ArmWrite(bool updateStatus = true)
    {
        _sealGeneration++;   // an encryption still running for older input must not arm the writer
        switch (WriteType)
        {
            case WriteMode.Erase:
                _nfc.ArmErase();
                ShowStatus("Ready. Hold the tag to erase against the back of the phone", ok: true);
                return;

            case WriteMode.Segments:
                ArmSegments();
                return;

            case WriteMode.Dump:
                if (_dumpData is null)
                {
                    _nfc.Disarm();
                    ShowStatus("Choose a dump, then hold the tag to load it to the back of the phone", ok: null);
                    return;
                }
                _nfc.ArmRawWrite(_dumpData, _dumpSerial);
                ShowStatus($"Ready. Hold the tag to load \"{_dumpName}\" ({_dumpData.Length} bytes)", ok: true);
                return;
        }

        var text = WriteEditor.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            _nfc.Disarm();
            ShowStatus("Enter content, then hold a writable tag to the back of the phone", ok: null);
            return;
        }

        if (WriteType == WriteMode.Private)
        {
            ArmPrivate(text, updateStatus);
            return;
        }

        if (WriteType == WriteMode.Raw)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            _nfc.ArmRawText(payload);
            ShowStatus($"Ready. Hold a Type 2 tag against the back of the phone to write {payload.Length} bytes raw", ok: true);
            return;
        }

        try
        {
            var message = BuildMessage(text);
            _nfc.ArmWrite(message);
            ShowStatus("Ready. Hold a writable tag against the back of the phone", ok: true);
        }
        catch (Exception ex)
        {
            _nfc.Disarm();
            ShowStatus(ex.Message, ok: false);
        }

        void ShowStatus(string text, bool? ok)
        {
            if (updateStatus) SetWriteStatus(text, ok);
        }

        void ArmSegments()
        {
            _segmentWrite = null;
            UpdateTagHistory();
            if (_tagStep is { } step && (step == TagStep.Undo ? _tagUndo : _tagRedo).TryPeek(out var record))
            {
                // Undoing or redoing a write on the tag takes the next touch, and only on that tag.
                _nfc.ArmSegmentWrite(step == TagStep.Undo ? record.UndoWrite : record.RedoWrite);
                ShowStatus(step == TagStep.Undo
                    ? $"Hold tag {record.TagId} to put {record.Segments} back as they were before the write"
                    : $"Hold tag {record.TagId} to write the change to {record.Segments} again", ok: true);
                return;
            }
            if (_edit is not { } edit) { _nfc.Disarm(); return; }
            if ((_idError ?? _sizeError ?? _rangeError) is { } problem)
            {
                _nfc.Disarm();
                ShowStatus(problem, ok: false);
                return;
            }

            var write = edit.ToWrite();
            if (write.IsEmpty)
            {
                _nfc.Disarm();
                ShowStatus(edit.FromRead
                    ? $"Change a {edit.Layout.Noun} above, then hold the tag to write it"
                    : $"Fill in the {edit.Layout.Noun}s to write, then hold the tag", ok: null);
                return;
            }

            _segmentWrite = write;
            _nfc.ArmSegmentWrite(write);
            var target = write.ExpectedId is { } id ? $"tag {id}" : $"any {write.Layout.ChipName}";
            ShowStatus($"Ready. Hold {target} to the back of the phone to write {write.Summary}", ok: true);
        }
    }

    private void SetWriteStatus(string text, bool? ok)
    {
        WriteStatus.Text = text;
        WriteStatus.TextColor = ok switch
        {
            true => Palette.Accent,
            false => Palette.Error,
            null => Colors.Gray,
        };
    }

    /// <summary>Encrypts the text with the passphrase once typing pauses, in the background, then arms the writer
    /// with the private record. Nothing is armed until the two passphrases match and are long enough.</summary>
    private async void ArmPrivate(string text, bool updateStatus)
    {
        _nfc.Disarm();
        var passphrase = PassphraseEntry.Text ?? string.Empty;
        if (passphrase.Length < SealedText.MinPassphraseLength)
        {
            if (updateStatus) SetWriteStatus($"Choose a passphrase of at least {SealedText.MinPassphraseLength} characters", null);
            return;
        }
        if (passphrase != (RepeatEntry.Text ?? string.Empty))
        {
            if (updateStatus) SetWriteStatus("Type the same passphrase twice", null);
            return;
        }

        int generation = _sealGeneration;
        if (updateStatus) SetWriteStatus("Encrypting...", null);
        await Task.Delay(400);
        if (generation != _sealGeneration) return;

        byte[] sealedBytes;
        try
        {
            sealedBytes = await Task.Run(() => SealedText.Seal(text, passphrase));
        }
        catch (Exception ex)
        {
            if (generation == _sealGeneration) SetWriteStatus("Could not encrypt the text: " + ex.Message, false);
            return;
        }
        if (generation != _sealGeneration || !_active || !_writeMode) return;

        _nfc.ArmWrite(NfcReader.BuildSealed(sealedBytes));
        if (updateStatus)
            SetWriteStatus($"Ready. Hold a writable tag against the back of the phone to write the encrypted text ({sealedBytes.Length} bytes). " +
                           "Keep the passphrase: without it the text cannot be read back.", true);
    }

    private NdefMessage BuildMessage(string text)
    {
        return WriteType switch
        {
            WriteMode.Link => NfcReader.BuildUri(Links.TryGetOpenable(text, out var u) ? u.ToString() : text),
            WriteMode.Text => NfcReader.BuildText(text),
            WriteMode.Contact => text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)
                    ? NfcReader.BuildVCard(text)
                    : throw new InvalidOperationException("A contact must be a vCard starting with BEGIN:VCARD."),
            _ => Links.TryGetOpenable(text, out var uri)
                    ? NfcReader.BuildUri(uri.ToString())
                    : NfcReader.BuildText(text),
        };
    }

    private void OnTagWritten(bool ok, string note)
    {
        Feedback.Tag(ok);

        // A write that opened a protected NTAG/Ultralight with a password: remember it for this tag.
        RememberType2Password();

        if (WriteType == WriteMode.Segments) note = SegmentWritten(ok, note);

        // Keep the result on screen, and silently re-arm so another tag can be written with the same content.
        WriteStatus.Text = ok && WriteType != WriteMode.Segments ? $"{note}  Hold another tag to do it again." : note;
        WriteStatus.TextColor = ok ? Palette.Accent : Palette.Error;
        UseRawButton.IsVisible = !ok && note.Contains(NfcReader.RawBytesTip)
            && WriteType is WriteMode.Auto or WriteMode.Text or WriteMode.Link;

        // The write was refused because the tag is password-protected: offer the password, then hold it again.
        if (!ok && note.Contains("password-protected"))
            RevealPassword(HasTypedPassword
                ? "That password did not open this tag for writing. Check it and hold the tag again."
                : "This tag is password-protected. Enter its password, then hold the tag again to write it.");

        if (_writeMode) ArmWrite(updateStatus: false);
    }

    /// <summary>The tag cannot hold NDEF (its page 3 says otherwise for good): switch to raw bytes, which arms the
    /// writer with the same text, so the next touch writes it.</summary>
    private void OnUseRaw(object? sender, EventArgs e)
    {
        UseRawButton.IsVisible = false;
        WriteTypePicker.SelectedIndex = (int)WriteMode.Raw;
    }

    private async void OnLoadFavorite(object? sender, EventArgs e)
    {
        await _saved.LoadAsync();
        var candidates = _saved.Items.Where(i => !i.IsDump).OrderByDescending(i => i.SortTime).ToList();
        if (candidates.Count == 0)
        {
            await DisplayAlertAsync("Nothing saved", "You have no saved codes or texts to load.", "OK");
            return;
        }

        var labels = candidates.Select(i => $"{i.DisplayName}  ·  {i.FormatLabel}").ToArray();
        var choice = await DisplayActionSheetAsync("Load from Saved", "Cancel", null, labels);
        if (choice is null || choice == "Cancel") return;

        int index = Array.IndexOf(labels, choice);
        if (index >= 0)
        {
            WriteEditor.Text = candidates[index].Value;
            ArmWrite();
            await _saved.MarkUsedAsync(candidates[index]);
        }
    }

    // ---------------------------------------------------------------- segment editor

    /// <summary>After a segment write: keep what it changed for undoing on the tag, move an undo or redo along its
    /// stacks, and mark the bytes as on the tag when it is the one the edit expects (so the same touch is not
    /// repeated; with any tag expected, the edit stays armed for the next one). Returns the note to show.</summary>
    private string SegmentWritten(bool ok, string note)
    {
        var record = _nfc.TakeLastSegmentWrite();
        var step = _tagStep;
        // Nothing changed on the tag: a refused touch (another tag, say) leaves an undo or redo armed, while one that
        // found the tag already as it should be has done its job.
        if (record is null && !(ok && step is not null))
        {
            UpdateTagHistory();
            return note;
        }

        _tagStep = null;
        switch (step)
        {
            case TagStep.Undo: _tagRedo.Push(_tagUndo.Pop()); note = "Undone on the tag: " + note; break;
            case TagStep.Redo: _tagUndo.Push(_tagRedo.Pop()); note = "Redone on the tag: " + note; break;
            default:
                _tagUndo.Push(record!);
                _tagRedo.Clear();
                break;
        }

        if (record is null) { UpdateTagHistory(); return note; }
        bool expected = _edit?.ExpectedId is { } id && TagEdit.SameId(id, record.TagId);
        if (NfcReader.ClassicKeysSet is { Count: > 0 } keysSet)
        {
            // The sectors opened with their new keys: remember them for the tag, and the edit no longer has them pending.
            _keyStore.Remember(record.TagId, keysSet.Select(k => k.Key));
            if (expected) foreach (var (sector, _) in keysSet) _edit!.SectorChanged(sector);
            UpdateSectorKeyPanel();
        }
        // A magic card given a new UID answers to it from now on: its known keys go with it, and the edit (and the
        // undo on the tag) expect the new serial, or the next touch of that same card would be refused.
        if (record.IdAfter is var newId && !TagEdit.SameId(newId, record.TagId))
        {
            _keyStore.CarryOver(record.TagId, newId);
            if (expected)
            {
                _edit!.ExpectedId = newId;
                _renderingSegments = true;
                ExpectedIdEntry.Text = newId;
                _renderingSegments = false;
                UpdateExpectSummary();
            }
            note += $" The card's serial is now {newId} (it shows once the card is held again); its remembered keys go with it.";
        }
        if (expected && (ok || step is not null))
            _edit!.Commit(record.After.ToDictionary(p => p.Key, p => p.Value.Select(b => (byte?)b).ToArray()));
        SegmentGrid.Invalidate();
        UpdateSegmentTools();
        UpdateTagHistory();
        return note;
    }

    private void UpdateTagHistory()
    {
        bool any = _tagUndo.Count > 0 || _tagRedo.Count > 0;
        TagHistoryPanel.IsVisible = any;
        if (!any) return;

        TagHistoryLabel.Text = _tagUndo.TryPeek(out var last)
            ? $"Last write: tag {last.TagId}, {last.Segments}." + (_tagUndo.Count > 1 ? $" {_tagUndo.Count - 1} earlier write{(_tagUndo.Count == 2 ? "" : "s")} can be undone after it." : "")
            : $"Undone: tag {_tagRedo.Peek().TagId}, {_tagRedo.Peek().Segments}.";
        TagUndoButton.IsEnabled = _tagUndo.Count > 0 && _tagStep is null;
        TagRedoButton.IsEnabled = _tagRedo.Count > 0 && _tagStep is null;
        TagStepCancelButton.IsVisible = _tagStep is not null;
    }

    private void OnTagUndo(object? sender, EventArgs e) => StartTagStep(TagStep.Undo);
    private void OnTagRedo(object? sender, EventArgs e) => StartTagStep(TagStep.Redo);

    private void StartTagStep(TagStep step)
    {
        if ((step == TagStep.Undo ? _tagUndo : _tagRedo).Count == 0) return;
        _tagStep = step;
        ArmWrite();
    }

    private void OnTagStepCancel(object? sender, EventArgs e)
    {
        _tagStep = null;
        ArmWrite();
    }

    /// <summary>From a read: edit this tag's pages or blocks, expecting the same tag (serial and chip) at write time.</summary>
    private void OnEditTag(object? sender, EventArgs e)
    {
        if (_last is not { Segments: { } read } tag) return;
        _edit = TagEdit.FromTag(read, tag.Id);
        SetMode(write: true);
        ShowEdit(expand: false);
        if (WriteType != WriteMode.Segments) WriteTypePicker.SelectedIndex = (int)WriteMode.Segments;   // arms the write
        else ArmWrite();
    }

    /// <summary>Fills the editor's fields from <see cref="_edit"/> without treating that as input.</summary>
    private void ShowEdit(bool? expand = null)
    {
        if (_edit is not { } edit) return;
        _renderingSegments = true;
        try
        {
            _segmentLayouts = SegmentLayout.Known.ToList();
            int index = _segmentLayouts.FindIndex(l => l.Family == edit.Layout.Family && l.ChipName == edit.Layout.ChipName);
            if (index < 0) { _segmentLayouts.Insert(0, edit.Layout); index = 0; }
            SegmentTypePicker.ItemsSource = _segmentLayouts.Select(l => l.ChipName).ToList();
            SegmentTypePicker.SelectedIndex = index;

            ExpectedIdEntry.Text = edit.ExpectedId ?? string.Empty;
            RangeFromEntry.Text = edit.From.ToString();
            RangeToEntry.Text = edit.To.ToString();
            RangeFromLabel.Text = $"From {edit.Layout.Noun}";
            RangeToLabel.Text = $"To {edit.Layout.Noun}";
            SegmentSizePanel.IsVisible = edit.Layout.Family == TagFamily.Iso15693 && !edit.FromRead;
            SegmentSizeEntry.Text = edit.Layout.SegmentSize.ToString();
            ExpectHint.Text = edit.Layout.Family switch
            {
                TagFamily.Type2 => "Pages are 4 bytes. The serial (pages 0-2), page 3 and the lock and password pages are never " +
                                   "written, so an edit cannot lock the tag for good or change its password.",
                TagFamily.Classic => "Blocks are 16 bytes. Block 0 (the serial) and each sector's trailer (its keys and access bits) " +
                                     "are never written, so an edit cannot lock you out of a sector. Sectors are opened with the same " +
                                     "keys a read tries: the key you entered, the default keys and keys remembered for cards.",
                _ => "Labels use blocks of 4 or 8 bytes, sometimes more. The label reports its size when written, and nothing is " +
                     "written if it differs from the bytes per block here. Lock commands are never sent.",
            };
            _idError = _rangeError = _sizeError = null;
            SelectSegmentTab(sectors: false);   // always open on the byte grid
            RenderSegments();
        }
        finally { _renderingSegments = false; }

        if (expand is { } open) SetExpectExpanded(open);
        UpdateExpectSummary();
    }

    /// <summary>Shows the edit in the grid (after it was replaced, or its range changed).</summary>
    private void RenderSegments()
    {
        if (_edit is not { } edit) return;
        SegmentGrid.Edit = edit;
        UpdateSegmentTools();
    }

    /// <summary>The column buttons, undo and redo, the hint, and the hidden input next to the cursor's row.</summary>
    private void UpdateSegmentTools()
    {
        if (_edit is not { } edit) return;
        // Whole-tag buttons kept at the top.
        SegmentRevertButton.IsEnabled = edit.HasChanges;

        // The floating toolbar holds the per-byte tools so they stay reachable while the grid scrolls.
        string row = $"{(edit.Layout.Noun == "page" ? "Page" : "Block")} to 00";
        FloatUndoButton.IsEnabled = edit.CanUndo;
        FloatRedoButton.IsEnabled = edit.CanRedo;
        FloatZeroRowButton.Text = row;
        Palette.Select(FloatSelectButton, SegmentGrid.Selecting);
        FloatSelectButton.Text = SegmentGrid.Selecting ? "Done" : "Select";
        UpdateFloatingToolsVisibility();

        // Sector keys & rules live on their own tab for a MIFARE Classic card; other tags have no sectors, so the
        // tab bar is hidden and only the grid shows.
        bool classic = edit.Layout.Family == TagFamily.Classic;
        SegmentTabBar.IsVisible = classic;
        if (!classic) { EditTab.IsVisible = true; SectorsTab.IsVisible = false; }

        // Block 0 (UID) editing: shown only for a read MIFARE Classic card; the switch reflects whether it is unlocked.
        BlockZeroPanel.IsVisible = edit.CanEditBlockZero;
        if (BlockZeroSwitch.IsToggled != edit.AllowBlockZero)
        {
            bool wasRendering = _renderingSegments;
            _renderingSegments = true;
            BlockZeroSwitch.IsToggled = edit.AllowBlockZero;
            _renderingSegments = wasRendering;
        }

        UpdateSectorKeyPanel();
        // The hidden input follows the cursor, across as well as down: Android scrolls to the view with the keyboard,
        // so an input left at the far left pulled the grid back to its start on every keystroke and tap.
        double left = Math.Max(0, Math.Min(SegmentGrid.CursorX, SegmentGrid.WidthRequest - SegmentInput.WidthRequest));
        SegmentInput.Margin = new Thickness(left, SegmentGrid.CursorY, 0, 0);

        string noun = edit.Layout.Noun;
        SegmentHint.Text =
            $"Tap a byte to put the cursor there, then type: hex digits in the hex column, any text in the text column. " +
            $"Typing writes over the bytes and runs on into the next {noun}; backspace puts a byte back as it was. Grey rows are never written. " +
            (edit.FromRead
                ? "The bytes are as the tag held them when it was read; only the ones you change are written."
                : "·· is a byte not set: the tag keeps its own byte there, and only the bytes you type are written.") +
            $" \"{(noun == "page" ? "Page" : "Block")} to 00\" clears the cursor's {noun}, \"All to 00\" every {noun} shown. " +
            "Undo and Redo go back or forward one keystroke at a time; \"Reset byte\" puts the byte under the cursor back as the tag had it and steps left, " +
            "so it walks back over a change wherever the cursor is put. \"Revert\" drops every change.";
    }

    private void OnEditTabSelected(object? sender, EventArgs e) => SelectSegmentTab(sectors: false);
    private void OnSectorsTabSelected(object? sender, EventArgs e) => SelectSegmentTab(sectors: true);

    /// <summary>Switches between the Edit tab (the byte grid) and the Sector keys &amp; rules tab.</summary>
    private void SelectSegmentTab(bool sectors)
    {
        EditTab.IsVisible = !sectors;
        SectorsTab.IsVisible = sectors;
        Palette.SelectTab(EditTabButton, EditUnderline, !sectors);
        Palette.SelectTab(SectorsTabButton, SectorsUnderline, sectors);
        UpdateFloatingToolsVisibility();
    }

    /// <summary>Whether the floating toolbar is tucked away to its little handle.</summary>
    private bool _floatingToolsCollapsed;

    private void OnFloatingCollapse(object? sender, EventArgs e) { _floatingToolsCollapsed = true; UpdateFloatingToolsVisibility(); }
    private void OnFloatingShow(object? sender, EventArgs e) { _floatingToolsCollapsed = false; UpdateFloatingToolsVisibility(); }

    /// <summary>Shows the floating editor tools only while editing bytes (the Edit tab of a segment write), as either the
    /// toolbar or, when tucked away, its handle.</summary>
    private void UpdateFloatingToolsVisibility()
    {
        bool show = _writeMode && _edit is not null && SegmentPanel.IsVisible && EditTab.IsVisible;
        FloatingTools.IsVisible = show;
        FloatingBar.IsVisible = show && !_floatingToolsCollapsed;
        FloatingShow.IsVisible = show && _floatingToolsCollapsed;
    }

    /// <summary>After any change to the bytes or the cursor: redraw, update the tools, re-arm the write.</summary>
    private void SegmentEdited()
    {
        SegmentGrid.Invalidate();
        UpdateSegmentTools();
        ArmWrite();
    }

    /// <summary>A byte was tapped: the keyboard's typing goes there.</summary>
    private void OnSegmentCursorPlaced() => StartTyping(TypingTarget.Grid);

    /// <summary>A byte of the grid or of a key row was tapped: the keyboard's typing goes to that edit, through the
    /// hidden input next to it.</summary>
    private void StartTyping(TypingTarget target)
    {
        _typing = target;
        if (target == TypingTarget.AllKeys) UpdateAllKeys();
        else UpdateSegmentTools();   // moves the hidden input to the cursor first, so focusing it scrolls there
        ResetInput();
        TypingInput.Focus();
    }

    private Entry TypingInput => _typing switch
    {
        TypingTarget.SectorKeys => SectorKeyInput,
        TypingTarget.AllKeys => AllKeysInput,
        _ => SegmentInput,
    };

    private TagEdit? TypingEdit => _typing == TypingTarget.AllKeys ? _allKeys : _edit;

    private void ResetInput()
    {
        _resettingInput = true;
        try
        {
            TypingInput.Text = InputSentinel;
            TypingInput.CursorPosition = InputSentinel.Length;
        }
        finally { _resettingInput = false; }
    }

    /// <summary>Keystrokes from a hidden input: characters typed (or pasted) after the sentinel, or backspaces that
    /// shortened it. In the hex column only hex digits count; in the text column every character does. Typing in a
    /// sector's key rows stays in that sector's trailer, so it never runs on into the next sector's blocks.</summary>
    private void OnSegmentInput(object? sender, TextChangedEventArgs e)
    {
        if (_resettingInput || sender != TypingInput || TypingEdit is not { } edit) return;
        int? within = _typing == TypingTarget.SectorKeys && edit.Length > 0 ? edit.At(edit.Cursor).Segment : null;
        if (within is { } segment && !MifareClassicKeys.IsTrailer(segment)) { ResetInput(); return; }   // the cursor left the keys
        bool Inside(int position) => position >= 0 && position < edit.Length && (within is null || edit.At(position).Segment == within);

        var text = e.NewTextValue ?? string.Empty;
        if (text.Length < InputSentinel.Length)
        {
            for (int i = text.Length; i < InputSentinel.Length; i++)
            {
                if (within is not null && (edit.InText || edit.Nibble == 0) && !Inside(edit.Cursor - 1)) break;
                edit.Backspace();
            }
        }
        else if (text.Length > InputSentinel.Length)
        {
            var typed = text.StartsWith(InputSentinel, StringComparison.Ordinal) ? text[InputSentinel.Length..] : text[..^InputSentinel.Length];
            bool ended = within is null
                ? edit.InText
                    ? edit.TypeText(typed) < Encoding.UTF8.GetByteCount(typed)
                    : typed.Where(Uri.IsHexDigit).Any(c => !edit.TypeHex(c))
                : edit.InText
                    ? typed.Any(c => !Inside(edit.Cursor) || edit.TypeText(c.ToString()) == 0)
                    : typed.Where(Uri.IsHexDigit).Any(c => !Inside(edit.Cursor) || !edit.TypeHex(c));
            if (ended)
                Notice.Show(_typing switch
                {
                    TypingTarget.Grid => $"That is the end of {edit.Layout.Noun}s {edit.From}-{edit.To}",
                    _ => "That is the end of key B",
                });
        }
        ResetInput();
        if (_typing == TypingTarget.AllKeys) UpdateAllKeys();
        else SegmentEdited();
    }

    /// <summary>Enter: to the start of the next row (Android closes the keyboard on Enter, so it is opened again). In
    /// the key rows Enter just closes the keyboard.</summary>
    private void OnSegmentInputCompleted(object? sender, EventArgs e)
    {
        if (_typing != TypingTarget.Grid || _edit is not { } edit) return;
        int size = edit.Layout.SegmentSize;
        edit.Place(Math.Min((edit.Cursor / size + 1) * size, Math.Max(0, edit.Length - 1)), edit.InText);
        SegmentEdited();
        SegmentInput.Focus();
    }

    private void OnSegmentUndo(object? sender, EventArgs e) { if (_edit?.Undo() == true) SegmentEdited(); }
    private void OnSegmentRedo(object? sender, EventArgs e) { if (_edit?.Redo() == true) SegmentEdited(); }

    /// <summary>The cursor's page or block to 00 (one undo step; written like any other change).</summary>
    private void OnSegmentZeroRow(object? sender, EventArgs e)
    {
        if (_edit is not { Length: > 0 } edit) return;
        edit.Fill(0x00, edit.At(edit.Cursor).Segment);
        SegmentEdited();
    }

    /// <summary>Every writable page or block in the range to 00: clearing the tag, short of what is never written.</summary>
    private void OnSegmentZeroAll(object? sender, EventArgs e)
    {
        _edit?.Fill(0x00);
        SegmentEdited();
    }

    /// <summary>The byte under the cursor back as the tag had it, then one byte left; pressed again, it walks back.</summary>
    private void OnSegmentResetByte(object? sender, EventArgs e)
    {
        _edit?.ResetAtCursor();
        SegmentEdited();
    }

    /// <summary>Turns byte selection on or off: on, tapping bytes extends a highlighted run; off clears it.</summary>
    private void OnSegmentSelect(object? sender, EventArgs e)
    {
        if (_edit is not { Length: > 0 }) return;
        SegmentGrid.Selecting = !SegmentGrid.Selecting;
        UpdateSegmentTools();
    }

    /// <summary>A long press on the grid: offer to copy the selected run of bytes — or, with nothing selected, the block
    /// that was pressed — as hex or as text.</summary>
    private async void OnGridLongPressed((int Segment, int Index) cell)
    {
        if (_edit is not { Length: > 0 } edit) return;
        byte[] bytes;
        string what;
        if (SegmentGrid.Selection is { } sel)
        {
            bytes = Enumerable.Range(sel.Start, sel.End - sel.Start + 1)
                .Select(p => { var (s, i) = edit.At(p); return edit.ValueAt(s, i) ?? (byte)0; }).ToArray();
            what = $"{bytes.Length} byte{(bytes.Length == 1 ? "" : "s")}";
        }
        else
        {
            bytes = edit.SegmentBytes(cell.Segment);
            what = $"{edit.Layout.Noun} {cell.Segment}";
        }

        var choice = await DisplayActionSheet($"Copy {what}", "Cancel", null, "Copy as hex", "Copy as text");
        if (choice == "Copy as hex")
        {
            await Clipboard.Default.SetTextAsync(string.Concat(bytes.Select(b => b.ToString("X2"))));
            Notice.Show($"{what} copied as hex");
        }
        else if (choice == "Copy as text")
        {
            await Clipboard.Default.SetTextAsync(new string(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray()));
            Notice.Show($"{what} copied as text");
        }
    }

    /// <summary>The rule pickers' choices, in picker order: data rules and trailer rules from <see cref="MifareClassicKeys"/>.</summary>
    private static readonly string[] DataRuleItems = MifareClassicKeys.DataRules.Select(r => r.Text).ToArray();
    private static readonly string[] TrailerRuleItems = MifareClassicKeys.TrailerRules.Select(r => r.Permanent ? "⚠ " + r.Text : r.Text).ToArray();

    /// <summary>Shows the sector box for the cursor's sector when its key is known: the new key, the rules of its blocks
    /// and of its keys (as read, or as chosen), and every sector with a change waiting.</summary>
    private static int DataRuleIndex(int bits) => MifareClassicKeys.DataRules.ToList().FindIndex(r => r.Bits == bits);

    private void UpdateSectorKeyPanel()
    {
        if (_edit is not { Layout.Family: TagFamily.Classic, FromRead: true, Length: > 0 } edit)
        {
            SectorChoosePanel.IsVisible = AllSectorsPanel.IsVisible = SectorKeyPanel.IsVisible = SectorsEmptyLabel.IsVisible = false;
            return;
        }

        var changeable = edit.ChangeableSectors.ToList();
        int sector = MifareClassicKeys.SectorOf(edit.At(edit.Cursor).Segment);
        bool can = edit.CanChangeKey(sector);

        _renderingSegments = true;
        try
        {
            // The sector chooser and the "all sectors" action show whenever any sector can be changed.
            SectorChoosePanel.IsVisible = AllSectorsPanel.IsVisible = changeable.Count > 0;
            SectorsEmptyLabel.IsVisible = changeable.Count == 0;
            if (SectorPicker.ItemsSource is not List<string> items || items.Count != changeable.Count)
                SectorPicker.ItemsSource = changeable.Select(s => $"Sector {s}").ToList();
            SectorPicker.SelectedIndex = changeable.IndexOf(sector);
            if (AllBlocksPicker.ItemsSource is null) AllBlocksPicker.ItemsSource = DataRuleItems;
            if (AllSectorsPicker.ItemsSource is null) { AllSectorsPicker.ItemsSource = DataRuleItems; AllSectorsPicker.Title = "Set every sector's data blocks to…"; }

            SectorKeyPanel.IsVisible = can || edit.Sectors.Count > 0;
            if (SectorKeyPanel.IsVisible)
            {
                SectorKeyTitle.Text = can ? $"Sector {sector}: keys and rules" : $"Sector {sector} did not open, so it cannot be changed";
                RevertSectorButton.IsVisible = can && edit.SectorHasChanges(sector);
                SectorRulesPanel.IsVisible = can;
                var change = edit.SectorChangeFor(sector);
                var rules = change?.Rules ?? edit.CurrentRules(sector);

                // Keys can be edited only when the sector opened and the rules still let them change. The two rows
                // are the trailer row's key bytes, so they always show what the grid (or "all sectors") set.
                bool keysOpen = can && edit.KeysChangeable(sector);
                SectorKeysPanel.IsVisible = can;
                int trailer = TagEdit.TrailerOf(sector);
                ShowKeyRow(SectorKeyAGrid, new SegmentGrid.RowWindow(trailer, 0, 6, "Key A"), edit, keysOpen);
                ShowKeyRow(SectorKeyBGrid, new SegmentGrid.RowWindow(trailer, 10, 6, "Key B"), edit, keysOpen);
                SectorKeyHint.IsVisible = keysOpen;
                KeysLockedLabel.IsVisible = can && !keysOpen;
                KeyASpareLabel.IsVisible = keysOpen && rules is not null && MifareClassicKeys.KeyAUnused(rules);

                if (can)
                {
                    bool wide = sector >= 32;
                    Picker[] pickers = [DataRule0Picker, DataRule1Picker, DataRule2Picker];
                    Label[] labels = [DataRule0Label, DataRule1Label, DataRule2Label];
                    for (int g = 0; g < 3; g++)
                    {
                        labels[g].Text = wide ? $"Blocks {g * 5}-{g * 5 + 4} of the sector" : $"Block {g} of the sector";
                        if (pickers[g].ItemsSource is null) pickers[g].ItemsSource = DataRuleItems;
                        pickers[g].SelectedIndex = rules is null ? -1 : DataRuleIndex(rules.Data[g]);
                    }
                    // "All data blocks" shows the common rule when the three match, otherwise nothing.
                    AllBlocksPicker.SelectedIndex = rules is not null && rules.Data0 == rules.Data1 && rules.Data1 == rules.Data2
                        ? DataRuleIndex(rules.Data0) : -1;
                    if (TrailerRulePicker.ItemsSource is null) TrailerRulePicker.ItemsSource = TrailerRuleItems;
                    TrailerRulePicker.SelectedIndex = rules is null ? -1 : MifareClassicKeys.TrailerRules.ToList().FindIndex(r => r.Bits == rules.Trailer);
                    TrailerRuleFull.Text = TrailerRulePicker.SelectedIndex >= 0 ? TrailerRuleItems[TrailerRulePicker.SelectedIndex] : "";
                    KeyBNote.IsVisible = rules is not null && MifareClassicKeys.KeyBReadable(rules.Trailer);
                    RulesUnknownLabel.IsVisible = rules is null;
                    PermanentWarning.IsVisible = change?.Rules?.IsPermanent == true;
                }
            }
        }
        finally { _renderingSegments = false; }

        UpdateSectorChangeList(edit);
        UpdateAllKeys();
    }

    /// <summary>Picked another sector in the Sectors tab: move the cursor to its first editable block, so the panel
    /// (which follows the cursor) shows that sector.</summary>
    private void OnSectorPicked(object? sender, EventArgs e)
    {
        if (_renderingSegments || _edit is not { } edit) return;
        var changeable = edit.ChangeableSectors.ToList();
        if (SectorPicker.SelectedIndex < 0 || SectorPicker.SelectedIndex >= changeable.Count) return;
        int sector = changeable[SectorPicker.SelectedIndex];
        int block = edit.Segments.Where(b => MifareClassicKeys.SectorOf(b) == sector).DefaultIfEmpty(-1).First();
        if (block < 0) return;
        if (_typing == TypingTarget.SectorKeys) SectorKeyInput.Unfocus();   // the key rows now show another sector
        edit.Place(edit.PositionOf(block, 0), edit.InText);
        SegmentEdited();
    }

    /// <summary>One choice that sets all three data blocks of the cursor's sector to the same rule.</summary>
    private void OnAllBlocksRuleChanged(object? sender, EventArgs e)
    {
        if (_renderingSegments || _edit is not { Length: > 0 } edit || AllBlocksPicker.SelectedIndex < 0) return;
        int sector = MifareClassicKeys.SectorOf(edit.At(edit.Cursor).Segment);
        edit.SetAllDataRules(sector, MifareClassicKeys.DataRules[AllBlocksPicker.SelectedIndex].Bits);
        UpdateSectorKeyPanel();
        ArmWrite();
    }

    /// <summary>One choice that sets the same data-block rule on every sector that opened. Confirmed first; the picker
    /// is an action, not a kept choice, so it resets afterwards.</summary>
    private async void OnAllSectorsRuleChanged(object? sender, EventArgs e)
    {
        if (_renderingSegments || _edit is not { Length: > 0 } edit || AllSectorsPicker.SelectedIndex < 0) return;
        var rule = MifareClassicKeys.DataRules[AllSectorsPicker.SelectedIndex];
        int count = edit.ChangeableSectors.Count;
        bool sure = await DisplayAlertAsync("Set every sector?",
            $"Set all data blocks of every opened sector ({count}) to \"{rule.Text}\"? Each sector's keys and trailer rule " +
            "stay as they are, and nothing is written until you hold the tag.", "Set all", "Cancel");
        _renderingSegments = true;
        AllSectorsPicker.SelectedIndex = -1;
        _renderingSegments = false;
        if (!sure) return;
        edit.SetAllSectorsDataRules(rule.Bits);
        UpdateSectorKeyPanel();
        ArmWrite();
    }

    private void UpdateSectorChangeList(TagEdit edit)
    {
        SectorKeyList.IsVisible = edit.Sectors.Count > 0;
        SectorKeyList.Text = edit.Sectors.Count == 0 ? "" :
            "Waiting to be written: " + string.Join("; ", edit.Sectors.OrderBy(k => k.Key).Select(k =>
                $"sector {k.Key}: " + string.Join(", ", new[]
                {
                    k.Value.SameKey ? "key " + MifareClassicKeys.KeyHex(k.Value.KeyA!) : null,
                    !k.Value.SameKey && k.Value.KeyA is { } a ? "key A " + MifareClassicKeys.KeyHex(a) : null,
                    !k.Value.SameKey && k.Value.KeyB is { } b ? "key B " + MifareClassicKeys.KeyHex(b) : null,
                    k.Value.Rules is { } r ? (r.IsPermanent ? "rules (PERMANENT)" : "rules") : null,
                }.Where(x => x is not null))));
    }

    /// <summary>Points a Sectors-tab key row at a key of the cursor sector's trailer and redraws it; a sector whose
    /// rules lock the keys shows them dimmed and takes no taps.</summary>
    private static void ShowKeyRow(SegmentGrid grid, SegmentGrid.RowWindow window, TagEdit edit, bool open)
    {
        if (grid.Edit != edit) grid.Edit = edit;
        grid.Window = window;
        grid.IsEnabled = open;
        grid.Opacity = open ? 1 : 0.5;
        grid.Invalidate();
    }

    /// <summary>A key typed in full in an "all sectors" row (all six bytes), or null when it was left blank or half done.</summary>
    private byte[]? AllKey(int row) =>
        Enumerable.Range(0, 6).All(i => _allKeys.IsEdited(row, i))
            ? Enumerable.Range(0, 6).Select(i => _allKeys.ValueAt(row, i)!.Value).ToArray() : null;

    private bool AllKeyPartial(int row) =>
        AllKey(row) is null && Enumerable.Range(0, 6).Any(i => _allKeys.IsEdited(row, i));

    /// <summary>Redraws the "all sectors" key rows and says what "Set on every sector" would do.</summary>
    private void UpdateAllKeys()
    {
        AllKeyAGrid.Invalidate();
        AllKeyBGrid.Invalidate();
        int sectors = _edit is { } edit ? edit.ChangeableSectors.Count(edit.KeysChangeable) : 0;
        bool any = AllKey(0) is not null || AllKey(1) is not null;
        AllKeysApplyButton.IsEnabled = any && sectors > 0 && !AllKeyPartial(0) && !AllKeyPartial(1);
        AllKeysClearButton.IsEnabled = _allKeys.HasChanges;
        AllKeysHint.Text = AllKeyPartial(0) || AllKeyPartial(1)
            ? "A key is six bytes: finish it, or clear it to leave that key as it is."
            : $"Tap a byte and type key A and/or key B (hex, or tap the text side for characters); a key left as ·· stays as each sector has it. " +
              $"Sets them on every sector that opened and whose rules let its keys change ({sectors}); each sector's key rows and trailer show them, " +
              "and Undo in the Edit tab takes them back in one step.";
    }

    /// <summary>Puts the typed key A and/or key B on every sector whose keys can change, as if typed in each trailer.</summary>
    private async void OnAllKeysApply(object? sender, EventArgs e)
    {
        if (_edit is not { Length: > 0 } edit) return;
        byte[]? keyA = AllKey(0), keyB = AllKey(1);
        if (keyA is null && keyB is null) return;
        int sectors = edit.ChangeableSectors.Count(edit.KeysChangeable);
        var keys = string.Join(" and ", new[]
        {
            keyA is null ? null : "key A " + MifareClassicKeys.KeyHex(keyA),
            keyB is null ? null : "key B " + MifareClassicKeys.KeyHex(keyB),
        }.Where(k => k is not null));
        bool sure = await DisplayAlertAsync("Set keys on every sector?",
            $"Set {keys} on every opened sector whose keys can change ({sectors})? The phone remembers new keys for this tag " +
            "— keep a copy, or the sectors cannot be opened on another phone. Nothing is written until you hold the tag.",
            "Set keys", "Cancel");
        if (!sure) return;
        edit.SetAllSectorsKeys(keyA, keyB);
        SegmentEdited();
        UpdateAllKeys();
    }

    private void OnAllKeysClear(object? sender, EventArgs e)
    {
        _allKeys.RevertAll();
        _allKeys.Place(0, _allKeys.InText);
        UpdateAllKeys();
    }

    /// <summary>Unlocks or locks editing of block 0 (the UID). Only a magic/clone card can take the write; a warning
    /// is shown before it is unlocked.</summary>
    private async void OnBlockZeroToggled(object? sender, ToggledEventArgs e)
    {
        if (_renderingSegments || _edit is not { } edit) return;
        if (e.Value && !edit.AllowBlockZero)
        {
            bool sure = await DisplayAlertAsync("Edit block 0 (the UID)?",
                "Block 0 holds the card's serial number (UID) and is never written on a normal card — the write simply " +
                "fails. Only a magic/clone card lets it be rewritten. A wrong check byte (BCC) can make the card unreadable " +
                "to some readers. Unlock it only if you know this is a magic card.", "Unlock block 0", "Cancel");
            if (!sure)
            {
                _renderingSegments = true;
                BlockZeroSwitch.IsToggled = false;
                _renderingSegments = false;
                return;
            }
        }
        edit.SetBlockZeroEditable(e.Value);
        SegmentEdited();
    }

    /// <summary>A rule picked for the cursor's sector. The rules are set as a whole from the four pickers; when they
    /// equal what the sector already has, no rule change is pending. A permanent trailer rule is confirmed first.</summary>
    private async void OnSectorRuleChanged(object? sender, EventArgs e)
    {
        if (_renderingSegments || _edit is not { Length: > 0 } edit) return;
        int sector = MifareClassicKeys.SectorOf(edit.At(edit.Cursor).Segment);
        if (!edit.CanChangeKey(sector)) return;
        Picker[] pickers = [DataRule0Picker, DataRule1Picker, DataRule2Picker];
        if (pickers.Any(p => p.SelectedIndex < 0) || TrailerRulePicker.SelectedIndex < 0) return;

        var trailerRule = MifareClassicKeys.TrailerRules[TrailerRulePicker.SelectedIndex];
        var chosen = new SectorRules(
            MifareClassicKeys.DataRules[DataRule0Picker.SelectedIndex].Bits,
            MifareClassicKeys.DataRules[DataRule1Picker.SelectedIndex].Bits,
            MifareClassicKeys.DataRules[DataRule2Picker.SelectedIndex].Bits,
            trailerRule.Bits);
        var current = edit.CurrentRules(sector);

        if (chosen.IsPermanent && current?.IsPermanent != true && sender == TrailerRulePicker)
        {
            bool sure = await DisplayAlertAsync("This can never be undone",
                $"\"{trailerRule.Text}\" freezes sector {sector}: once written, its keys and rules can never be changed again, " +
                "by anyone, for the life of the tag. The data blocks keep the rules chosen above, for good.",
                "I understand, freeze it", "Cancel");
            if (!sure)
            {
                UpdateSectorKeyPanel();   // back to what is pending or read
                return;
            }
        }

        edit.SetSectorRules(sector, current is not null && chosen == current ? null : chosen);
        PermanentWarning.IsVisible = edit.SectorChangeFor(sector)?.Rules?.IsPermanent == true;
        TrailerRuleFull.Text = TrailerRuleItems[TrailerRulePicker.SelectedIndex];   // keep the full text in step
        KeyBNote.IsVisible = MifareClassicKeys.KeyBReadable(trailerRule.Bits);
        UpdateSectorChangeList(edit);
        ArmWrite();
    }

    private void OnSegmentRevert(object? sender, EventArgs e)
    {
        _edit?.RevertAll();
        SegmentEdited();
    }

    /// <summary>Puts just the cursor's sector back as the tag had it: its keys, rules and block bytes.</summary>
    private void OnRevertSector(object? sender, EventArgs e)
    {
        if (_edit is not { Length: > 0 } edit) return;
        edit.RevertSector(MifareClassicKeys.SectorOf(edit.At(edit.Cursor).Segment));
        SegmentEdited();
    }

    private void UpdateExpectSummary()
    {
        if (_edit is not { } edit) return;
        var tag = edit.ExpectedId is { } id ? $"tag {id}" : "any tag";
        var size = edit.Layout.Family == TagFamily.Iso15693 ? $", {edit.Layout.SegmentSize} bytes per block" : "";
        ExpectSummary.Text = $"Expects {edit.Layout.ChipName}, {tag}{size}. Edits {edit.Layout.Noun}s {edit.From}-{edit.To}.";
    }

    private void OnToggleExpect(object? sender, EventArgs e) => SetExpectExpanded(!ExpectDetails.IsVisible);

    private void SetExpectExpanded(bool open)
    {
        ExpectDetails.IsVisible = open;
        ExpectToggle.Text = open ? "Done" : "Change";
    }

    /// <summary>Empties the editor back to a blank one of the same tag type: the loaded tag and every edit, key and rule
    /// change are dropped, so switching write type and back does not leave old data behind. Confirmed when there are
    /// unsaved changes; a pristine loaded read can be cleared freely (it can be read again).</summary>
    private async void OnStartOver(object? sender, EventArgs e)
    {
        if (_edit is not { } edit) return;
        if (edit.HasChanges && !await DisplayAlertAsync("Start over?",
            "Clear the editor and start with an empty one of the same tag type? Your edits, key and rule changes are not saved.",
            "Clear", "Cancel"))
            return;
        _edit = TagEdit.Blank(edit.Layout);
        ShowEdit(expand: false);
        ArmWrite();
    }

    /// <summary>Another tag type: a new, empty edit for it (the bytes of a read do not carry over to another layout).</summary>
    private void OnSegmentTypeChanged(object? sender, EventArgs e)
    {
        if (_renderingSegments || _edit is not { } edit || SegmentTypePicker.SelectedIndex < 0) return;
        var layout = _segmentLayouts[SegmentTypePicker.SelectedIndex];
        if (layout.Family == edit.Layout.Family && layout.ChipName == edit.Layout.ChipName) return;
        _edit = TagEdit.Blank(layout);
        _edit.ExpectedId = edit.ExpectedId;
        ShowEdit();
        ArmWrite();
    }

    private void OnExpectedIdChanged(object? sender, TextChangedEventArgs e)
    {
        if (_renderingSegments || _edit is null) return;
        var text = ExpectedIdEntry.Text?.Trim() ?? string.Empty;
        var id = TagEdit.ParseId(text);
        _idError = text.Length > 0 && id is null ? "The expected serial number is hex bytes, like 04:A1:B2:C3:D4:E5:F6." : null;
        _edit.ExpectedId = text.Length == 0 ? null : id;
        UpdateExpectSummary();
        ArmWrite();
    }

    private void OnRangeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_renderingSegments || _edit is not { } edit) return;
        _rangeError = int.TryParse(RangeFromEntry.Text, out int from) && int.TryParse(RangeToEntry.Text, out int to)
            ? edit.SetRange(from, to)
            : $"Enter the first and last {edit.Layout.Noun} to edit.";
        if (_rangeError is null) RenderSegments();
        UpdateExpectSummary();
        ArmWrite();
    }

    /// <summary>ISO 15693 set up by hand: the block size, which only the label itself can confirm.</summary>
    private void OnSegmentSizeChanged(object? sender, TextChangedEventArgs e)
    {
        if (_renderingSegments || _edit is not { } edit) return;
        if (!int.TryParse(SegmentSizeEntry.Text, out int size) || size < 1 || size > SegmentLayout.MaxIsoBlockSize)
        {
            _sizeError = $"Bytes per block is a number from 1 to {SegmentLayout.MaxIsoBlockSize}; most labels use 4 or 8.";
            ArmWrite();
            return;
        }
        _sizeError = null;
        if (size == edit.Layout.SegmentSize) { ArmWrite(); return; }
        int from = edit.From, to = edit.To;
        _edit = TagEdit.Blank(edit.Layout with { SegmentSize = size });
        _edit.ExpectedId = edit.ExpectedId;
        _edit.SetRange(from, to);
        RenderSegments();
        UpdateExpectSummary();
        ArmWrite();
    }

    // ---------------------------------------------------------------- dumps (write side)

    private void UseDump(byte[] data, string name, string serial)
    {
        _dumpData = data;
        _dumpName = name;
        _dumpSerial = serial;

        var kind = Features.Amiibo && NfcReader.IsAmiiboDump(data) ? "Amiibo dump" : "Tag dump";
        var from = serial.Length > 0 ? $"from tag {serial}" : "imported";
        DumpLabel.Text = $"{name}\n{kind}, {data.Length} bytes ({data.Length / 4} pages), {from}";
        ArmWrite();
    }

    private async Task<SavedItem?> PickSavedDumpAsync(string title)
    {
        await _saved.LoadAsync();
        var dumps = _saved.Dumps.OrderByDescending(d => d.SortTime).ToList();
        if (dumps.Count == 0)
        {
            await DisplayAlertAsync("No saved dumps", "Read a tag and tap \"Save tag as read\", import a .bin file, or paste hex.", "OK");
            return null;
        }

        var labels = dumps.Select(d => $"{d.DisplayName}  ·  {d.DumpSize} B").ToArray();
        var choice = await DisplayActionSheetAsync(title, "Cancel", null, labels);
        if (choice is null || choice == "Cancel") return null;
        int index = Array.IndexOf(labels, choice);
        return index >= 0 ? dumps[index] : null;
    }

    private async void OnChooseDump(object? sender, EventArgs e)
    {
        var dump = await PickSavedDumpAsync("Choose a dump to write");
        if (dump is null) return;
        try
        {
            UseDump(await _saved.ReadDumpAsync(dump), dump.DisplayName, dump.DumpSerial);
            await _saved.MarkUsedAsync(dump);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not load dump", ex.Message, "OK");
        }
    }

    private async void OnDeleteDump(object? sender, EventArgs e)
    {
        try
        {
            var dump = await PickSavedDumpAsync("Delete which dump?");
            if (dump is null) return;
            if (!await DisplayAlertAsync("Delete dump", $"Delete \"{dump.DisplayName}\" ({dump.Subtitle})?", "Delete", "Keep")) return;

            await _saved.RemoveAsync(dump);
            if (_dumpName == dump.DisplayName && _dumpSerial == dump.DumpSerial)
            {
                _dumpData = null;
                DumpLabel.Text = "No dump chosen";
                ArmWrite();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not delete", ex.Message, "OK");
        }
    }

    private async void OnImportDump(object? sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose a tag dump (.bin)",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = ["application/octet-stream", "*/*"],
                }),
            });
            if (result is null) return;

            await using var stream = await result.OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var data = buffer.ToArray();

            if (Type2Dump.Problem(data) is { } problem)
            {
                await DisplayAlertAsync("Not a tag dump", problem, "OK");
                return;
            }

            await SaveAndUseDumpAsync(Path.GetFileNameWithoutExtension(result.FileName), data);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not import", ex.Message, "OK");
        }
    }

    private async void OnPasteHex(object? sender, EventArgs e)
    {
        try
        {
            var text = await Clipboard.Default.GetTextAsync();
            var data = SavedService.ParseHex(text);
            var problem = data is null
                ? "Copy a dump as hex first: either this app's page listing, or plain hex bytes with or without spaces or colons."
                : Type2Dump.Problem(data);
            if (data is null || problem is not null)
            {
                await DisplayAlertAsync("Nothing usable on the clipboard", problem, "OK");
                return;
            }

            var name = await DisplayPromptAsync("Pasted dump", $"{data.Length} bytes ({data.Length / 4} pages). Name it:",
                accept: "Use", cancel: "Cancel", initialValue: $"Pasted {DateTime.Now:yyyy-MM-dd HH:mm}", maxLength: 60);
            if (name is null) return;

            await SaveAndUseDumpAsync(name, data);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not use the clipboard", ex.Message, "OK");
        }
    }

    /// <summary>Keeps an imported or pasted dump in Saved and chooses it for writing.</summary>
    private async Task SaveAndUseDumpAsync(string name, byte[] data)
    {
        var serial = Type2Dump.Serial(data);
        var saved = await _saved.AddDumpAsync(name, serial, Type2Dump.ChipLabel(data), data);
        UseDump(data, saved.DisplayName, serial);
    }
}
