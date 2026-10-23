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
        _nfc.Stop();
    }

    private void OnOpenSettings(object? sender, EventArgs e) => _nfc.OpenSettings();

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
        _last = tag;
        Feedback.Success();

        WaitingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

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
        MemoryLabel.Text = tag.HasMemoryDump ? string.Join("\n", tag.MemoryDump!) : string.Empty;
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
        Uri? uri = null;
        OpenLinkButton.IsVisible = hasContent && Links.TryGetOpenable(content, out uri);
        if (OpenLinkButton.IsVisible && uri is not null) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
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
            Feedback.Failure();
            UnlockStatus.Text = "That passphrase does not open this tag (or the tag was changed).";
            UnlockStatus.TextColor = Palette.Error;
            return;
        }

        Feedback.Success();
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
        if (ok) Feedback.Success();
        else Feedback.Failure();

        // Keep the result on screen, and silently re-arm so another tag can be written with the same content.
        WriteStatus.Text = ok ? $"{note}  Hold another tag to do it again." : note;
        WriteStatus.TextColor = ok ? Palette.Accent : Palette.Error;

        if (_writeMode) ArmWrite(updateStatus: false);
    }

    private async void OnLoadFavorite(object? sender, EventArgs e)
    {
        await _saved.LoadAsync();
        var candidates = _saved.Items.Where(i => !i.IsDump).ToList();
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
        var dumps = _saved.Dumps.ToList();
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
