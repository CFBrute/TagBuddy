using System.Text;
using Android.Nfc;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The NFC tab: read a tag (details, content, memory dump, private text with its passphrase), or write
/// text, links, contacts, private text, raw bytes or a dump to one, or erase it.</summary>
[QueryProperty(nameof(IncomingDumpId), AppNavigation.Dump)]
[QueryProperty(nameof(IncomingText), AppNavigation.Text)]
public partial class NfcPage : ContentPage
{
    /// <summary>The write types, in the order of the picker in NfcPage.xaml.</summary>
    private enum WriteMode { Auto, Link, Text, Contact, Private, Raw, Dump, Erase }

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

    private readonly SavedService _saved;
    private readonly NfcReader _nfc = NfcReader.Instance;
    private NfcTagInfo? _last;
    private bool _writeMode;

    /// <summary>The dump chosen for writing, with where it came from (for the result note and the label).</summary>
    private byte[]? _dumpData;
    private string _dumpSerial = "";
    private string _dumpName = "";

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

        WriteTypePicker.SelectedIndex = 0;
        SetMode(write: false);
        SetPasswordMode(asText: false);   // hex by default, the way RFID tools show a password
    }

    /// <summary>Something arrived from Saved: switch to write mode and arm it.</summary>
    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
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
        _nfc.SetManualPassword(null, null);   // not kept once the tab is left
        _nfc.SetManualClassicKey(null);
        _tryAllKeys = false;
        _nfc.SetClassicKeySearch([], [], false);
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

    /// <summary>Set for one read when the person taps "Try all": that read runs the whole imported dictionary.</summary>
    private bool _tryAllKeys;

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
        TryAllKeysButton.Text = imported > 0 ? $"Try all {imported:N0} known keys" : "Try all known keys";
        TryAllKeysButton.IsEnabled = imported > 0;
        KeyStoreStatus.Text = imported > 0
            ? $"{imported:N0} keys imported. Hold the card again after tapping to run them against the locked sectors; a key that opens a sector is remembered for this card."
            : "Import a key dictionary (for example mfc_default_keys.dic) to try more than the built-in defaults. Keys that open a card are remembered for next time.";
    }

    private void OnTryAllKeys(object? sender, EventArgs e)
    {
        if (_keyStore.ImportedCount() == 0) return;
        _tryAllKeys = true;
        ApplyClassicKey();
        Notice.Show("Hold the card against the phone again");
        StatusLabel.Text = $"Hold the card again to try all {_keyStore.ImportedCount():N0} known keys on the locked sectors…";
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

        Palette.Select(ReadModeButton, !write);
        Palette.Select(WriteModeButton, write);

        if (write) ArmWrite();
        else _nfc.Disarm();
    }

    private void OnTagRead(NfcTagInfo tag)
    {
        // A password belongs to the tag it was entered for: drop it when a different tag is held to the phone, so
        // it is never tried on the new one (which could count against the new tag's wrong-attempt limit).
        if (_passwordSerial is not null && !string.Equals(tag.Id, _passwordSerial, StringComparison.OrdinalIgnoreCase))
        {
            PasswordEntry.Text = string.Empty;   // clears _passwordSerial and the reader's password via ApplyPassword
        }

        _last = tag;
        Feedback.Tag(ok: true);

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
        if (Features.MifareKeyTools && NfcReader.ClassicKeysFound.Count > 0)
            _keyStore.Remember(tag.Id, NfcReader.ClassicKeysFound.Select(k => k.Key));

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
        if (tag.RawMemory is { Length: > 0 } memory)
        {
            // Type 2 tag: the full page memory, shown with its map, saveable as a backup.
            MemoryTitle.Text = "Readable memory";
            MemoryDescription.Text = "Every page of this tag's memory. Save it as a binary dump to keep a backup that can be written back to the tag later.";
            DumpButtons.IsVisible = true;
            MemoryOverview.IsVisible = true;
            MemoryMapView.Show(MemoryOverview, MemoryLabel, memory, tag.ChipName);
        }
        else if (tag.HasMemoryDump)
        {
            // MIFARE Classic: the sectors the common default keys opened. Not a backup, so no dump buttons.
            MemoryTitle.Text = "Card data";
            MemoryDescription.Text = "The sectors that opened with common default keys. A card secured with custom keys shows only its serial number.";
            DumpButtons.IsVisible = false;
            MemoryOverview.IsVisible = false;
            MemoryLabel.Text = string.Join("\n", tag.MemoryDump!);
        }
        else
        {
            MemoryLabel.Text = string.Empty;
        }
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

    private async void OnCopyMemory(object? sender, EventArgs e)
    {
        if (_last?.MemoryDump is { Count: > 0 } dump)
        {
            await Clipboard.Default.SetTextAsync(string.Join("\n", dump));
            Notice.Show($"{dump.Count} pages copied");
        }
    }

    // ---------------------------------------------------------------- dumps (read side)

    private string DumpChipName => _last?.ChipName ?? "Type2";

    private async void OnSaveDump(object? sender, EventArgs e)
    {
        if (_last is not { HasRawMemory: true } tag) return;
        try
        {
            var suggested = Features.Amiibo && NfcReader.IsAmiiboDump(tag.RawMemory!) ? $"Amiibo {tag.Id}" : $"{DumpChipName} {tag.Id}";
            var name = await DisplayPromptAsync("Save dump", $"{tag.RawMemory!.Length} bytes ({tag.RawMemory.Length / 4} pages). Give it a name:",
                accept: "Save", cancel: "Cancel", initialValue: suggested, maxLength: 60);
            if (name is null) return;

            var dump = await _saved.AddDumpAsync(name, tag.Id, DumpChipName, tag.RawMemory);
            Notice.Show($"Dump saved: {dump.DisplayName}");
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

        TextPanel.IsVisible = WriteType is not (WriteMode.Dump or WriteMode.Erase);
        PrivatePanel.IsVisible = WriteType == WriteMode.Private;
        DumpPanel.IsVisible = WriteType == WriteMode.Dump;
        ErasePanel.IsVisible = WriteType == WriteMode.Erase;
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

        // Keep the result on screen, and silently re-arm so another tag can be written with the same content.
        WriteStatus.Text = ok ? $"{note}  Hold another tag to do it again." : note;
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
            await DisplayAlertAsync("No saved dumps", "Read a tag and tap \"Save dump\", import a .bin file, or paste hex.", "OK");
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
