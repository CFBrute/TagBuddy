using System.Text;
using Android.Nfc;
using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class NfcPage : ContentPage
{
    private const int ModeDump = 4;
    private const int ModeErase = 5;

    private readonly FavoritesService _favorites;
    private readonly NfcDumpStore _dumps;
    private readonly NfcReader _nfc = NfcReader.Instance;
    private NfcTagInfo? _last;
    private bool _writeMode;

    /// <summary>The dump chosen for writing, with where it came from (for the result note and the label).</summary>
    private byte[]? _dumpData;
    private string _dumpSerial = "";
    private string _dumpName = "";

    public NfcPage(FavoritesService favorites, NfcDumpStore dumps)
    {
        InitializeComponent();
        _favorites = favorites;
        _dumps = dumps;
        WriteTypePicker.SelectedIndex = 0;
        SetMode(write: false);
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
        _nfc.Start();
        if (_writeMode) ArmWrite();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _nfc.TagRead -= OnTagRead;
        _nfc.TagWritten -= OnTagWritten;
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

        var accent = (Color)Application.Current!.Resources["Primary"];
        var grey = Color.FromArgb("#6B7280");
        ReadModeButton.BackgroundColor = write ? grey : accent;
        ReadModeButton.TextColor = write ? Colors.White : (Color)Application.Current!.Resources["PrimaryDarkText"];
        WriteModeButton.BackgroundColor = write ? accent : grey;
        WriteModeButton.TextColor = write ? (Color)Application.Current!.Resources["PrimaryDarkText"] : Colors.White;

        if (write) ArmWrite();
        else _nfc.Disarm();
    }

    private void OnTagRead(NfcTagInfo tag)
    {
        _last = tag;
        try { Vibration.Default.Vibrate(60); } catch { /* not supported on every device */ }

        WaitingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

        TagTitle.Text = $"Tag {tag.Id}  ·  {string.Join(", ", tag.Technologies)}";
        var card = tag.Details.FirstOrDefault(d => d.Label == "Card").Value;
        var data = tag.Details.FirstOrDefault(d => d.Label == "Data").Value;
        ContentLabel.Text = tag.Content ?? card ?? data ?? "No readable content on this tag";
        ContentLabel.TextColor = tag.HasContent ? (Color)Application.Current!.Resources["Primary"] : Colors.Gray;

        ContentActions.IsVisible = tag.HasContent;
        Preview.Value = tag.HasContent ? tag.Content : null;
        ViewPageButton.IsVisible = Links.IsWebLink(tag.Content, out _);
        if (tag.HasContent)
        {
            OpenLinkButton.IsVisible = Links.TryGetOpenable(tag.Content, out var uri);
            if (OpenLinkButton.IsVisible) IconButtons.SetLabel(OpenLinkButton, Links.OpenLabel(uri));
        }

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

        MemoryPanel.IsVisible = tag.HasMemoryDump;
        MemoryLabel.Text = tag.HasMemoryDump ? string.Join("\n", tag.MemoryDump!) : string.Empty;
    }

    private async void OnCopyMemory(object? sender, EventArgs e)
    {
        if (_last?.MemoryDump is { Count: > 0 } dump)
        {
            await Clipboard.Default.SetTextAsync(string.Join("\n", dump));
            await DisplayAlertAsync("Copied", $"{dump.Count} pages copied to the clipboard.", "OK");
        }
    }

    // ---------------------------------------------------------------- dumps (read side)

    private string DumpChipName => _last?.ChipName ?? "Type2";

    private async void OnSaveDump(object? sender, EventArgs e)
    {
        if (_last is not { HasRawMemory: true } tag) return;
        try
        {
            var suggested = NfcReader.IsAmiiboDump(tag.RawMemory!) ? $"Amiibo {tag.Id}" : $"{DumpChipName} {tag.Id}";
            var name = await DisplayPromptAsync("Save dump", $"{tag.RawMemory!.Length} bytes ({tag.RawMemory.Length / 4} pages). Give it a name:",
                accept: "Save", cancel: "Cancel", initialValue: suggested, maxLength: 60);
            if (name is null) return;

            var dump = await _dumps.SaveAsync(name, tag.Id, DumpChipName, tag.RawMemory);
            await DisplayAlertAsync("Dump saved", $"\"{dump.DisplayName}\" is kept in the app. Choose it under Write a tag, Binary dump, to write it back.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not save", ex.Message, "OK");
        }
    }

    private async void OnShareDump(object? sender, EventArgs e)
    {
        if (_last is not { HasRawMemory: true } tag) return;
        try
        {
            var file = $"{DumpChipName}_{tag.SerialForFileName}.bin".Replace(' ', '-');
            var path = await _dumps.ExportTempAsync(file, tag.RawMemory!);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Share tag dump",
                File = new ShareFile(path, "application/octet-stream"),
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not share", ex.Message, "OK");
        }
    }

    private void AddDetail(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = [new ColumnDefinition(120), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
        row.Add(new Label { Text = label, FontSize = 13, TextColor = Colors.Gray });
        var v = new Label { Text = value, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
        Grid.SetColumn(v, 1);
        row.Add(v);
        Details.Children.Add(row);
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (_last?.Content is not { } content) return;

        if (_favorites.Contains(content, BarcodeTypes.QrCode.Key))
        {
            await DisplayAlertAsync("Already saved", "This content is already in your favorites.", "OK");
            return;
        }

        var name = await DisplayPromptAsync("Add to favorites", "Give it a name (optional):",
            accept: "Save", cancel: "Cancel", placeholder: content, maxLength: 60);
        if (name is null) return;

        await _favorites.AddAsync(new BarcodeItem { Name = name.Trim(), Value = content, Format = BarcodeTypes.QrCode.Key });
        await DisplayAlertAsync("Saved to favorites", "Saved as a QR code with the tag's content.", "OK");
    }

    private async void OnCopy(object? sender, EventArgs e)
    {
        if (_last?.Content is { } content) await CodeActions.CopyTextAsync(this, content);
    }

    private async void OnOpenLink(object? sender, EventArgs e)
    {
        if (_last?.Content is { } content) await CodeActions.OpenLinkAsync(this, content);
    }

    private async void OnViewPage(object? sender, EventArgs e)
    {
        if (_last?.Content is { } content) await WebPage.ShowAsync(content);
    }

    private async void OnUseInGenerator(object? sender, EventArgs e)
    {
        if (_last?.Content is not { } content) return;
        await Shell.Current.GoToAsync("//generate/qr", new Dictionary<string, object> { ["value"] = content });
    }

    // ---------------------------------------------------------------- write

    private int WriteType => Math.Max(0, WriteTypePicker.SelectedIndex);

    private void OnWriteTextChanged(object? sender, TextChangedEventArgs e) => ArmWrite();

    private async void OnWriteTypeChanged(object? sender, EventArgs e)
    {
        if (WriteType == ModeErase)
        {
            // Erasing is destructive and stays armed for every tag held to the phone, so ask once first.
            _nfc.Disarm();
            var ok = await DisplayAlertAsync("Erase tags?",
                "Every tag held to the phone while this mode is selected will have its content wiped. Save a dump first if you may want it back.",
                "Erase mode", "Cancel");
            if (!ok)
            {
                WriteTypePicker.SelectedIndex = 0;
                return;
            }
        }

        TextPanel.IsVisible = WriteType is not (ModeDump or ModeErase);
        DumpPanel.IsVisible = WriteType == ModeDump;
        ErasePanel.IsVisible = WriteType == ModeErase;
        WriteHint.IsVisible = TextPanel.IsVisible;
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
        switch (WriteType)
        {
            case ModeErase:
                _nfc.ArmErase();
                ShowStatus("Ready. Hold the tag to erase against the back of the phone", ok: true);
                return;

            case ModeDump:
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
            if (!updateStatus) return;
            WriteStatus.Text = text;
            WriteStatus.TextColor = ok switch
            {
                true => (Color)Application.Current!.Resources["Primary"],
                false => Color.FromArgb("#DC2626"),
                null => Colors.Gray,
            };
        }
    }

    private NdefMessage BuildMessage(string text)
    {
        // 0 Auto, 1 Link, 2 Text, 3 Contact
        return WriteType switch
        {
            1 => NfcReader.BuildUri(Links.TryGetOpenable(text, out var u) ? u.ToString() : text),
            2 => NfcReader.BuildText(text),
            3 => text.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)
                    ? NfcReader.BuildVCard(text)
                    : throw new InvalidOperationException("A contact must be a vCard starting with BEGIN:VCARD."),
            _ => Links.TryGetOpenable(text, out var uri)
                    ? NfcReader.BuildUri(uri.ToString())
                    : NfcReader.BuildText(text),
        };
    }

    private void OnTagWritten(bool ok, string note)
    {
        try { Vibration.Default.Vibrate(ok ? 60 : 200); } catch { /* not supported on every device */ }

        // Keep the result on screen, and silently re-arm so another tag can be written with the same content.
        WriteStatus.Text = ok ? $"{note}  Hold another tag to do it again." : note;
        WriteStatus.TextColor = ok ? (Color)Application.Current!.Resources["Primary"] : Color.FromArgb("#DC2626");

        if (_writeMode) ArmWrite(updateStatus: false);
    }

    private async void OnLoadFavorite(object? sender, EventArgs e)
    {
        await _favorites.LoadAsync();
        if (_favorites.Items.Count == 0)
        {
            await DisplayAlertAsync("No favorites", "You have no saved codes to load.", "OK");
            return;
        }

        var labels = _favorites.Items.Select(i => i.DisplayName).ToArray();
        var choice = await DisplayActionSheetAsync("Load from favorites", "Cancel", null, labels);
        if (choice is null || choice == "Cancel") return;

        var item = _favorites.Items.FirstOrDefault(i => i.DisplayName == choice);
        if (item is not null)
        {
            WriteEditor.Text = item.Value;
            ArmWrite();
        }
    }

    // ---------------------------------------------------------------- dumps (write side)

    private void UseDump(byte[] data, string name, string serial)
    {
        _dumpData = data;
        _dumpName = name;
        _dumpSerial = serial;

        var kind = NfcReader.IsAmiiboDump(data) ? "Amiibo dump" : "Tag dump";
        var from = serial.Length > 0 ? $"from tag {serial}" : "imported";
        DumpLabel.Text = $"{name}\n{kind}, {data.Length} bytes ({data.Length / 4} pages), {from}";
        ArmWrite();
    }

    private async Task<NfcDump?> PickSavedDumpAsync(string title)
    {
        await _dumps.LoadAsync();
        if (_dumps.Items.Count == 0)
        {
            await DisplayAlertAsync("No saved dumps", "Read a tag and tap \"Save dump\", import a .bin file, or paste hex.", "OK");
            return null;
        }

        var labels = _dumps.Items.Select(d => $"{d.DisplayName}  ·  {d.SizeBytes} B").ToArray();
        var choice = await DisplayActionSheetAsync(title, "Cancel", null, labels);
        if (choice is null || choice == "Cancel") return null;
        int index = Array.IndexOf(labels, choice);
        return index >= 0 ? _dumps.Items[index] : null;
    }

    private async void OnChooseDump(object? sender, EventArgs e)
    {
        var dump = await PickSavedDumpAsync("Choose a dump to write");
        if (dump is null) return;
        try
        {
            UseDump(await _dumps.ReadAsync(dump), dump.DisplayName, dump.Serial);
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
            if (!await DisplayAlertAsync("Delete dump", $"Delete \"{dump.DisplayName}\" ({dump.Summary})?", "Delete", "Keep")) return;

            await _dumps.RemoveAsync(dump);
            if (_dumpName == dump.DisplayName && _dumpSerial == dump.Serial)
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

            if (!IsPlausibleDump(data, out var why))
            {
                await DisplayAlertAsync("Not a tag dump", why, "OK");
                return;
            }

            var serial = SerialFromDump(data);
            var name = Path.GetFileNameWithoutExtension(result.FileName);
            var saved = await _dumps.SaveAsync(name, serial, NfcReader.IsAmiiboDump(data) ? "Amiibo" : "Type2", data);
            UseDump(data, saved.DisplayName, serial);
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
            var data = NfcDumpStore.ParseHex(text);
            string why = "";
            if (data is null || !IsPlausibleDump(data, out why))
            {
                await DisplayAlertAsync("Nothing usable on the clipboard",
                    data is null
                        ? "Copy a dump as hex first: either this app's page listing, or plain hex bytes with or without spaces or colons."
                        : why, "OK");
                return;
            }

            var serial = SerialFromDump(data);
            var name = await DisplayPromptAsync("Pasted dump", $"{data.Length} bytes ({data.Length / 4} pages). Name it:",
                accept: "Use", cancel: "Cancel", initialValue: $"Pasted {DateTime.Now:yyyy-MM-dd HH:mm}", maxLength: 60);
            if (name is null) return;

            var saved = await _dumps.SaveAsync(name, serial, NfcReader.IsAmiiboDump(data) ? "Amiibo" : "Type2", data);
            UseDump(data, saved.DisplayName, serial);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not use the clipboard", ex.Message, "OK");
        }
    }

    private static bool IsPlausibleDump(byte[] data, out string why)
    {
        why = "";
        if (data.Length < 16) why = $"Only {data.Length} bytes; a tag dump is at least 16 bytes (4 pages).";
        else if (data.Length % 4 != 0) why = $"{data.Length} bytes is not a whole number of 4-byte pages.";
        else if (data.Length > 4096) why = $"{data.Length} bytes is far more than any Type 2 tag holds.";
        return why.Length == 0;
    }

    /// <summary>The 7-byte serial stored in pages 0-2 of a Type 2 dump (byte 3 and byte 8 are check bytes).</summary>
    private static string SerialFromDump(byte[] data)
    {
        if (data.Length < 9 || data[0] == 0 && data[1] == 0) return "";
        byte[] uid = [data[0], data[1], data[2], data[4], data[5], data[6], data[7]];
        return string.Join(":", uid.Select(b => b.ToString("X2")));
    }
}
