using System.Text;
using Android.Nfc;
using BarcodeBuddy.Models;
using BarcodeBuddy.Services;

namespace BarcodeBuddy.Pages;

public partial class NfcPage : ContentPage
{
    private readonly FavoritesService _favorites;
    private readonly NfcReader _nfc = NfcReader.Instance;
    private NfcTagInfo? _last;
    private bool _writeMode;

    public NfcPage(FavoritesService favorites)
    {
        InitializeComponent();
        _favorites = favorites;
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
        _nfc.ArmWrite(null);
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
        else _nfc.ArmWrite(null);
    }

    private void OnTagRead(NfcTagInfo tag)
    {
        _last = tag;
        try { Vibration.Default.Vibrate(60); } catch { /* not supported on every device */ }

        WaitingPanel.IsVisible = false;
        ResultPanel.IsVisible = true;

        TagTitle.Text = $"Tag {tag.Id}  ·  {string.Join(", ", tag.Technologies)}";
        var card = tag.Details.FirstOrDefault(d => d.Label == "Card").Value;
        ContentLabel.Text = tag.Content ?? card ?? "No readable content on this tag";
        ContentLabel.TextColor = tag.HasContent ? (Color)Application.Current!.Resources["Primary"] : Colors.Gray;

        ContentActions.IsVisible = tag.HasContent;
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

    private async void OnUseInGenerator(object? sender, EventArgs e)
    {
        if (_last?.Content is not { } content) return;
        await Shell.Current.GoToAsync("//generate/qr", new Dictionary<string, object> { ["value"] = content });
    }

    // ---------------------------------------------------------------- write

    private void OnWriteTextChanged(object? sender, TextChangedEventArgs e) => ArmWrite();
    private void OnWriteTypeChanged(object? sender, EventArgs e) => ArmWrite();

    /// <summary>Builds the NDEF message from the current content and arms the reader to write the next tag.</summary>
    private void ArmWrite()
    {
        var text = WriteEditor.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            _nfc.ArmWrite(null);
            WriteStatus.Text = "Enter content, then hold a writable tag to the back of the phone";
            WriteStatus.TextColor = Colors.Gray;
            return;
        }

        try
        {
            var message = BuildMessage(text);
            _nfc.ArmWrite(message);
            WriteStatus.Text = "Ready. Hold a writable tag against the back of the phone";
            WriteStatus.TextColor = (Color)Application.Current!.Resources["Primary"];
        }
        catch (Exception ex)
        {
            _nfc.ArmWrite(null);
            WriteStatus.Text = ex.Message;
            WriteStatus.TextColor = Color.FromArgb("#DC2626");
        }
    }

    private NdefMessage BuildMessage(string text)
    {
        // 0 Auto, 1 Link, 2 Text, 3 Contact
        return WriteTypePicker.SelectedIndex switch
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
        WriteStatus.Text = ok ? $"{note}  Hold another tag to write again." : note;
        WriteStatus.TextColor = ok ? (Color)Application.Current!.Resources["Primary"] : Color.FromArgb("#DC2626");

        if (!_writeMode) return;
        var text = WriteEditor.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return;
        try { _nfc.ArmWrite(BuildMessage(text)); } catch { /* keep the error message shown above */ }
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
}
